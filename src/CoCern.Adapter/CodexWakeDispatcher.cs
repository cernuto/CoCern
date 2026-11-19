using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CoCern.Adapter.Node;

namespace CoCern.Adapter;

/// <param name="activity">Follows Codex's rollouts to hold wakes back while a thread is mid-turn; null never holds them.</param>
/// <param name="queue">Codex's own queue: while a CoCern wake for the thread is still pending there, another is never queued; null never holds them.</param>
public sealed class CodexWakeDispatcher(string executable, CodexActivity? activity = null, TimeProvider? clock = null, CodexQueue? queue = null) : IWakeDispatcher
{
    private readonly int? desktopOwner = CodexLocator.DesktopOwner(executable);

    public bool IsBusy(string threadId) =>
        queue?.HasPendingWake(threadId) == true || activity is not null && activity.IsBusy(threadId, (clock ?? TimeProvider.System).GetUtcNow());

    private const int MaximumOutput = 32768;
    public static bool HasQueuedReceipt(int exitCode, string output, string threadId)
    {
        if (exitCode != 0 || !Guid.TryParseExact(threadId, "D", out _)) return false;
        var receipt = new Regex(@"^Queued message ([0-9a-fA-F-]{36}) for thread " + Regex.Escape(threadId) + @"\.$", RegexOptions.CultureInvariant);
        return output.Split('\n').Select(line => receipt.Match(line.TrimEnd('\r')))
            .Any(match => match.Success && Guid.TryParseExact(match.Groups[1].Value, "D", out var message) && message != Guid.Empty);
    }

    public async Task<bool> QueueAsync(string threadId, string prompt, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !Guid.TryParseExact(threadId, "D", out _)) return false;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true
            }
        };
        foreach (var argument in new[] { "queue", "--thread", threadId, "--message", prompt }) process.StartInfo.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var outputPipe = OperatingSystem.IsWindows() ? new QueueOutputPipe() : null;
            using var errorPipe = OperatingSystem.IsWindows() ? new QueueOutputPipe() : null;
            process.StartInfo.StandardOutputHandle = outputPipe?.Writer;
            process.StartInfo.StandardErrorHandle = errorPipe?.Writer;
            process.StartInfo.RedirectStandardOutput = outputPipe is null;
            process.StartInfo.RedirectStandardError = errorPipe is null;
            if (!process.Start()) return false;
            outputPipe?.Writer.Dispose();
            errorPipe?.Writer.Dispose();
            CodexDesktopWake.Prepare(executable, desktopOwner);
            using var exited = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var stdout = outputPipe?.CaptureAsync(exited.Token, timeout.Token, completeLines: true) ?? ReadBounded(process.StandardOutput, timeout.Token);
            var stderr = errorPipe?.CaptureAsync(exited.Token, timeout.Token, completeLines: false) ?? ReadBounded(process.StandardError, timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (outputPipe is not null)
                {
                    exited.Cancel();
                    await Task.WhenAll(stdout, stderr);
                }
            }
            var output = await stdout;
            var errors = await stderr;
            return CompleteQueue(process.ExitCode, output, errors, threadId, id => CodexDesktopWake.Request(executable, desktopOwner, id));
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    internal static bool CompleteQueue(int exitCode, string? output, string? errors, string threadId, Action<Guid> requestDesktopWake)
    {
        if (output is null || errors is null || !HasQueuedReceipt(exitCode, output, threadId)) return false;
        requestDesktopWake(Guid.ParseExact(threadId, "D"));
        return true;
    }

    // A descendant may hold the CLI's Windows output handles open after the CLI has exited.
    // Drain during execution, then capture exactly the bytes still queued when the pending read is canceled.
    internal sealed class QueueOutputPipe : IDisposable
    {
        private readonly FileStream input;
        internal SafeFileHandle Writer { get; }

        internal QueueOutputPipe()
        {
            SafeFileHandle.CreateAnonymousPipe(out var reader, out var writer, asyncRead: true);
            Writer = writer;
            try { input = new FileStream(reader, FileAccess.Read, bufferSize: 1, isAsync: true); }
            catch { reader.Dispose(); writer.Dispose(); throw; }
        }

        internal async Task<string?> CaptureAsync(CancellationToken exited, CancellationToken timeout, bool completeLines)
        {
            var encoding = Console.OutputEncoding;
            var byteLimit = Math.Max(Encoding.UTF32.GetMaxByteCount(MaximumOutput), encoding.GetMaxByteCount(MaximumOutput)) + 4;
            using var data = new MemoryStream();
            var buffer = new byte[4096];
            var overflow = false;
            var reachedEnd = true;
            void Record(int count)
            {
                if (data.Length + count > byteLimit) overflow = true;
                if (!overflow) data.Write(buffer, 0, count);
            }
            try
            {
                int count;
                while ((count = await input.ReadAsync(buffer, exited)) > 0) Record(count);
            }
            catch (OperationCanceledException) when (exited.IsCancellationRequested && !timeout.IsCancellationRequested)
            {
                // Each read that completed before cancellation has been recorded. Read the remainder
                // without waiting for more output from another process that inherited the writer.
                if (TryGetAvailable(out var remaining))
                {
                    reachedEnd = false;
                    if (overflow || data.Length + remaining > byteLimit) return null;
                    while (remaining > 0)
                    {
                        var count = (int)Math.Min((uint)buffer.Length, remaining);
                        await input.ReadExactlyAsync(buffer.AsMemory(0, count), timeout);
                        Record(count);
                        remaining -= (uint)count;
                    }
                }
            }
            timeout.ThrowIfCancellationRequested();
            if (overflow) return null;
            data.Position = 0;
            using var text = new StreamReader(data, encoding);
            var captured = text.ReadToEnd();
            if (captured.Length > MaximumOutput) return null;
            // A genuine end-of-stream also terminates its final line. With a live inherited writer,
            // a partial line at the snapshot boundary is not a complete queue receipt.
            if (completeLines && !reachedEnd && !captured.EndsWith('\n') && TryGetAvailable(out _))
                return captured[..(captured.LastIndexOf('\n') + 1)];
            return captured;
        }

        private bool TryGetAvailable(out uint bytes)
        {
            if (PeekNamedPipe(input.SafeFileHandle, 0, 0, 0, out bytes, 0)) return true;
            var error = Marshal.GetLastPInvokeError();
            if (error is 109 or 233) return false; // ERROR_BROKEN_PIPE or ERROR_PIPE_NOT_CONNECTED
            throw new System.ComponentModel.Win32Exception(error);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(SafeFileHandle handle, nint buffer, uint bufferSize, nint read, out uint available, nint left);

        public void Dispose()
        {
            input.Dispose();
            Writer.Dispose();
        }
    }

    private static async Task<string?> ReadBounded(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var builder = new StringBuilder();
        var overflow = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (builder.Length + count > MaximumOutput) overflow = true;
            if (!overflow) builder.Append(buffer, 0, count);
        }
        return overflow ? null : builder.ToString();
    }
}

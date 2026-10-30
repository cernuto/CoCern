using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PkRelay.Adapter;

/// <param name="activity">Follows Codex's rollouts to hold wakes back while a thread is mid-turn; null never holds them.</param>
public sealed class CodexWakeDispatcher(string executable, CodexActivity? activity = null, TimeProvider? clock = null) : IWakeDispatcher
{
    public bool IsBusy(string threadId) => activity is not null && activity.IsBusy(threadId, (clock ?? TimeProvider.System).GetUtcNow());

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
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in new[] { "queue", "--thread", threadId, "--message", prompt }) process.StartInfo.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (!process.Start()) return false;
            var stdout = ReadBounded(process.StandardOutput, timeout.Token);
            var stderr = ReadBounded(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var errors = await stderr;
            return output is not null && errors is not null && HasQueuedReceipt(process.ExitCode, output, threadId);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
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

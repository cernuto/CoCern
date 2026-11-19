using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using CoCern.Adapter.Node;

namespace CoCern.Adapter;

internal static class CodexDesktopWake
{
    // Windows PowerShell supplies the inbox WinRT projection for the package-targeted launcher.
    private const string LaunchScript = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $launcher = [Windows.System.Launcher,Windows.System,ContentType=WindowsRuntime]
        $options = [Windows.System.LauncherOptions,Windows.System,ContentType=WindowsRuntime]::new()
        $options.TargetApplicationPackageFamilyName = 'OpenAI.Codex_2p2nqsd0c76g0'
        $convert = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
        $asTask = $convert.MakeGenericMethod([bool])
        while ($null -ne ($line = [Console]::ReadLine())) {
            $thread = [guid]::ParseExact($line, 'D')
            $operation = $launcher::LaunchUriAsync([uri]('codex://threads/wake/' + $thread.ToString('D')), $options)
            [void]$asTask.Invoke($null, @($operation)).GetAwaiter().GetResult()
            [Console]::WriteLine('done')
        }
        """;

    private sealed record Command(string Executable, int Owner, Guid? Thread);
    private static readonly Lazy<ChannelWriter<Command>> Commands = new(StartWorker);

    internal static void Prepare(string executable, int? owner)
    {
        if (OperatingSystem.IsWindows() && owner is int id)
            Commands.Value.TryWrite(new(executable, id, null));
    }

    internal static void Request(string executable, int? owner, Guid thread)
    {
        if (OperatingSystem.IsWindows() && owner is int id && thread != Guid.Empty)
            Commands.Value.TryWrite(new(executable, id, thread));
    }

    private static ChannelWriter<Command> StartWorker()
    {
        // Activation is a hint for an already durable message. A full channel must never delay the queue receipt.
        var commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(32) { SingleReader = true });
        _ = Task.Run(() => RunAsync(commands.Reader));
        return commands.Writer;
    }

    private static async Task RunAsync(ChannelReader<Command> commands)
    {
        Process? helper = null;
        while (await commands.WaitToReadAsync())
        {
            try
            {
                using var idle = new CancellationTokenSource();
                do
                {
                    while (commands.TryRead(out var command))
                    {
                        using var desktop = Process.GetProcessById(command.Owner);
                        if (!string.Equals(desktop.MainModule?.FileName, CodexLocator.DesktopImage(command.Executable), StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (helper?.HasExited == true)
                        {
                            helper.Dispose();
                            helper = null;
                        }
                        if (helper is null)
                        {
                            helper = Process.Start(CreateLauncher());
                            helper?.BeginErrorReadLine();
                        }
                        if (helper is null || command.Thread is not Guid thread) continue;
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await helper.StandardInput.WriteLineAsync(thread.ToString("D").AsMemory(), timeout.Token);
                        if (await helper.StandardOutput.ReadLineAsync(timeout.Token) != "done")
                            throw new IOException("Desktop launcher exited without acknowledging the wake.");
                    }
                    idle.CancelAfter(TimeSpan.FromMinutes(2));
                } while (await commands.WaitToReadAsync(idle.Token));
            }
            catch (Exception error) when (error is ArgumentException or Win32Exception or InvalidOperationException
                or NotSupportedException or UnauthorizedAccessException or IOException or OperationCanceledException)
            {
                // The durable queue remains available to desktop polling if the helper or owner goes away.
            }
            finally
            {
                if (helper is not null)
                {
                    try { if (!helper.HasExited) helper.Kill(); }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
                    helper.Dispose();
                    helper = null;
                }
            }
        }
    }

    internal static ProcessStartInfo CreateLauncher()
    {
        var system = Environment.SystemDirectory;
        var start = new ProcessStartInfo(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = system, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", LaunchScript })
            start.ArgumentList.Add(argument);
        return start;
    }
}

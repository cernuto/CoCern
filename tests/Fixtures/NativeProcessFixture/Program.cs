using System.Diagnostics;
using System.Text.Json;

namespace CoCern.Tests.Fixtures.NativeProcess;

internal static class FixtureProgram
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--sentinel"])
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            return 0;
        }
        if (args is not ["--native-hook", "claude", "--connection", var folder] || !Path.IsPathFullyQualified(folder)) return 2;
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath;
        if (dotnet is null || !Path.IsPathFullyQualified(dotnet)) return 2;
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(FixtureProgram).Assembly.Location);
        start.ArgumentList.Add("--sentinel");
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PROCESSOR_ARCHITECTURE", "DOTNET_ROOT", "DOTNET_HOST_PATH", "HOME", "USERPROFILE", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        using var sentinel = Process.Start(start) ?? throw new IOException("The owned fixture sentinel did not start.");
        try
        {
            var temporary = Path.Combine(folder, "owned-pids.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { helper = Environment.ProcessId, sentinel = sentinel.Id }));
            File.Move(temporary, Path.Combine(folder, "owned-pids.json"));
        }
        catch
        {
            if (!sentinel.HasExited) sentinel.Kill();
            await sentinel.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
        await Console.In.ReadToEndAsync();
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    }
}

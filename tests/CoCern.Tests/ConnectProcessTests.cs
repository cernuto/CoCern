using System.Diagnostics;
using System.Text.Json;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

/// <summary>Exercises the real CLI boundary without ever permitting a write before its paths have been proved isolated.</summary>
public sealed class ConnectProcessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-connect-process-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(root, "profile");
    private string AppData => Path.Combine(root, "appdata");
    private string Token => Path.Combine(root, "token");
    private string Codex => Path.Combine(Profile, ".codex", "config.toml");
    private string Desktop => Path.Combine(AppData, "Claude", "claude_desktop_config.json");

    public ConnectProcessTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Codex)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Desktop)!);
        File.WriteAllText(Token, "isolated-test-token");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Cli_connect_honors_isolated_profile_and_appdata_before_writing()
    {
        var realFiles = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")
        }.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        try
        {
            // An error must identify each exact temporary path. A generic "connected" response cannot prove isolation:
            // a real user's existing CoCern entry is also reported connected, even when its connection differs.
            foreach (var (client, path) in new[] { ("codex", Codex), ("claude-desktop", Desktop) })
            {
                File.WriteAllBytes(path, [0xff]);
                var probe = await Run(["--check", "--only", client]);
                Assert.Equal(5, probe.Code);
                using var json = JsonDocument.Parse(probe.Output);
                var entry = Assert.Single(json.RootElement.GetProperty("clients").EnumerateArray());
                Assert.Equal("error", entry.GetProperty("state").GetString());
                Assert.Contains($"'{path}'", entry.GetProperty("detail").GetString());
                Assert.True(File.ReadAllBytes(path).SequenceEqual(new byte[] { 0xff }), "The read-only probe changed its fixture.");
                File.Delete(path);
            }

            // This invocation is deliberately unreachable against an adapter that ignores the isolation environment.
            var result = await Run();
            Assert.True(result.Code == 0, $"Isolated connect exited {result.Code}: {result.Error}");
            Assert.Contains("[mcp_servers.cocern]", File.ReadAllText(Codex));
            foreach (var path in new[] { Path.Combine(Profile, ".claude.json"), Desktop })
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal(Token, json.RootElement.GetProperty("mcpServers").GetProperty("cocern").GetProperty("env").GetProperty("COCERN_TOKEN_FILE").GetString());
            }
        }
        finally
        {
            foreach (var (path, before) in realFiles)
            {
                var after = File.Exists(path) ? File.ReadAllBytes(path) : null;
                Assert.True(before is null ? after is null : after is not null && before.SequenceEqual(after), $"The CLI changed a real settings file: {path}");
            }
        }
    }

    [Theory]
    [InlineData("USERPROFILE", "relative-folder")]
    [InlineData("HOME", "relative-folder")]
    [InlineData("APPDATA", "relative-folder")]
    [InlineData("USERPROFILE", "   ")]
    [InlineData("HOME", "   ")]
    [InlineData("APPDATA", "   ")]
    public async Task Cli_connect_rejects_invalid_folder_overrides_without_writing(string variable, string value)
    {
        var result = await Run(["--check"], variable, value);
        Assert.Equal(1, result.Code);
        Assert.Contains(variable, result.Error);
        Assert.False(File.Exists(Codex));
        Assert.False(File.Exists(Desktop));
    }

    private async Task<(int Code, string Output, string Error)> Run(string[]? arguments = null, string? invalidVariable = null, string? invalidValue = null)
    {
        var tests = typeof(ConnectProcessTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(tests, ".runtimeconfig.json"), "--depsfile", Path.ChangeExtension(tests, ".deps.json"), typeof(ConnectCommand).Assembly.Location, "--connect", "--json", "--token-file", Token }.Concat(arguments ?? []))
            start.ArgumentList.Add(argument);
        start.Environment["USERPROFILE"] = Profile;
        start.Environment["HOME"] = Profile;
        start.Environment["APPDATA"] = AppData;
        if (invalidVariable is not null)
        {
            // Force either platform's primary variable out of the way when exercising its fallback.
            if (invalidVariable == "HOME") start.Environment.Remove("USERPROFILE");
            if (invalidVariable == "USERPROFILE") start.Environment.Remove("HOME");
            start.Environment[invalidVariable] = invalidValue;
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await output, await error);
    }
}

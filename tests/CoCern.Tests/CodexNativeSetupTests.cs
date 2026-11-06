using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

public sealed class CodexNativeSetupTests
{
    [Fact]
    public async Task Windows_native_hooks_parse_as_literal_PowerShell_invocations()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string dotnet = @"C:\CoCern tools\O'Brien\dotnet.exe";
        const string adapter = @"C:\CoCern $data\$(Get-Item unused)`helper\CoCern.Adapter.dll";
        const string connection = @"C:\connections\O'Brien $data\$(Get-Item unused)`connection";
        var command = CodexNativeSetup.HookCommandLine(dotnet, adapter) + " " + CodexNativeSetup.QuoteHookArgument(connection);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("""
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(), [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { exit 1 }
            $command = $ast.EndBlock.Statements[0].PipelineElements[0]
            if ($command.InvocationOperator -ne 'Ampersand') { exit 2 }
            $values = @($command.CommandElements | ForEach-Object {
                if ($_ -isnot [System.Management.Automation.Language.StringConstantExpressionAst] -or $_.StringConstantType -ne 'SingleQuoted') { exit 3 }
                $_.Value
            })
            ConvertTo-Json -InputObject $values -Compress
            """);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.StandardInput.WriteAsync(command);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exited = true;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            exited = false;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
        }
        Assert.True(exited, "PowerShell parsing did not finish.");
        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal(new[] { dotnet, adapter, connection }, JsonSerializer.Deserialize<string[]>(await output));
    }

    [Fact]
    public void Explicit_native_setup_restores_owned_token_privacy_without_rotating_its_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-native-permissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "synthetic-token");
            var bytes = System.Text.Encoding.UTF8.GetBytes("synthetic-existing-token\r\n");
            File.WriteAllBytes(path, bytes);
            if (!OperatingSystem.IsWindows() && !NodeState.IsPrivateFile(path))
            {
                Assert.Throws<IOException>(() => NodeState.ProtectOwnedFile(path));
                Assert.Equal(bytes, File.ReadAllBytes(path));
                NodeState.WritePrivately(path, bytes);
            }
            NodeState.ProtectOwnedFile(path);
            Assert.True(NodeState.IsPrivateFile(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            NodeState.ProtectOwnedFile(path);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Native_catalog_preferences_survive_development_launch_without_changing_verified_credentials()
    {
        var start = new System.Diagnostics.ProcessStartInfo();
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("COCERN_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["COCERN_ROLE"] = "admin";
        start.Environment["COCERN_PROFILE"] = "diagnostics";
        start.Environment["COCERN_TOKEN"] = "untrusted-override";
        start.Environment["COCERN_URL"] = "http://127.0.0.1:6666";
        NativeRegistration.RestrictPrivateEnvironment(start);
        var connection = new AdapterOptions("http://127.0.0.1:4321", "verified-token", signalR: true) { NodeFolder = "verified-private-folder" };
        var options = NativeRegistration.ClientOptions(connection, key => start.Environment.TryGetValue(key, out var value) ? value : null);
        Assert.True(options.Admin);
        Assert.True(options.Diagnostics);
        Assert.True(options.SignalR);
        Assert.Equal(connection.BaseUri, options.BaseUri);
        Assert.Equal("verified-token", options.Token);
        Assert.Equal("verified-private-folder", options.NodeFolder);
        var worker = NativeRegistration.ClientOptions(connection, _ => null);
        Assert.False(worker.Admin);
        Assert.False(worker.Diagnostics);
        var root = Path.Combine(Path.GetTempPath(), "cocern-native-apphost-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = Path.Combine(root, "config.toml"); var token = Path.Combine(root, "token");
            var native = new[] { "--native-client", "codex", "--connection", Path.Combine(root, "connection"), "--profile", Path.Combine(root, "profile") };
            NodeState.WritePrivately(token, "synthetic-apphost-token"u8.ToArray());
            File.WriteAllText(configuration, $"[mcp_servers.cocern]\ncommand = {JsonSerializer.Serialize(Path.Combine(root, "CoCern.Adapter.exe"))}\nargs = {JsonSerializer.Serialize(native)}\nenv = {{ COCERN_TOKEN_FILE = {JsonSerializer.Serialize(token)}, COCERN_ROLE = \"admin\", COCERN_PROFILE = \"diagnostics\" }}\n");
            var deployment = Path.Combine(root, "deployment"); var launcher = Path.Combine(deployment, "versions", new string('a', 64), "CoCern.Adapter.dll");
            ClientSettings.PlanDevelopmentCodex(configuration, "dotnet", launcher, deployment, token, replaceLauncher: true).Apply(TimeProvider.System);
            var updated = File.ReadAllText(configuration);
            var arguments = updated.Split('\n').Single(line => line.StartsWith("args = ", StringComparison.Ordinal))[7..].Trim();
            Assert.Equal(new[] { launcher, "--dev-launch", deployment }.Concat(native), ClientSettings.ParseArgumentArray(arguments));
            Assert.Contains("COCERN_ROLE = \"admin\"", updated);
            Assert.Contains("COCERN_PROFILE = \"diagnostics\"", updated);
            Assert.Equal("synthetic-apphost-token", File.ReadAllText(token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Native_hook_repair_preserves_unrelated_handlers_and_is_idempotent()
    {
        const string oldCommand = "dotnet old.dll --native-hook codex --connection \"private\" --profile \"profile\"";
        const string command = "& 'dotnet' 'current.dll' --native-hook codex --connection 'private' --profile 'profile'";
        var existing = JsonNode.Parse(JsonSerializer.Serialize(new { description = "My hooks", hooks = new
        {
            SessionStart = new[] { new { hooks = new[] { new { type = "command", command = oldCommand }, new { type = "command", command = "my-startup-helper --profile \"profile\"" }, new { type = "command", command = "dotnet other.dll --native-hook codex --connection \"private\" --profile \"other-profile\"" } } } },
            Stop = new[] { new { hooks = new[] { new { type = "command", command = "my-stop-helper" } } } }
        } }))!.AsObject();
        var desired = JsonSerializer.SerializeToElement(new
        {
            SessionStart = new[] { new { hooks = new[] { new { type = "command", command, timeout = 20 } } } },
            PreToolUse = new[] { new { matcher = "^mcp__cocern__.*$", hooks = new[] { new { type = "command", command, timeout = 20 } } } }
        });
        var first = CodexNativeSetup.MergeHooks(existing, desired, "profile").ToJsonString();
        Assert.Contains("my-startup-helper", first);
        Assert.Contains("my-stop-helper", first);
        Assert.Contains("My hooks", first);
        Assert.Contains("other.dll", first);
        Assert.DoesNotContain("old.dll", first);
        var second = CodexNativeSetup.MergeHooks(JsonNode.Parse(first)!.AsObject(), desired, "profile").ToJsonString();
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_different_or_unreachable_session_cannot_make_native_readiness_ready()
    {
        var handle = new SessionHandle("own", "synthetic-private-token", "repo", "test", DateTimeOffset.UtcNow);
        AdapterReply Directory(string session, string repository, string provider, bool reachable) => new(true, "Ok", JsonSerializer.SerializeToElement(new
        { items = new[] { new { relaySessionId = session, repositoryId = repository, provider, reachable } } }));
        Assert.True(NativeStartupEvidence.HasReachableRoute(Directory("own", "repo", "codex", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("other", "repo", "codex", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("own", "another-repo", "codex", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("own", "repo", "claude", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("own", "repo", "codex", false), handle));
    }

    [Fact]
    public void Read_only_native_inspection_does_not_create_connection_hook_or_skill_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-native-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = CodexNativeSetup.Inspect(root, Path.Combine(root, ".codex"), new Dictionary<string, string>(), Path.Combine(root, "cache"));
            Assert.Equal("setup_required", result.State);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

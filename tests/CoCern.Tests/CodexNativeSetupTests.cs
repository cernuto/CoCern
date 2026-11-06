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
    public async Task Explicit_native_refresh_updates_literal_package_paths_without_approving_trust_or_changing_disabled_choices()
    {
        using var fixture = new NativeRefreshFixture();
        var configBefore = File.ReadAllBytes(fixture.Config); var hooksBefore = File.ReadAllBytes(fixture.Hooks); var candidateBefore = File.ReadAllBytes(fixture.Candidate);
        var enrollmentBefore = Directory.EnumerateFiles(fixture.Node).Where(path => path != fixture.Candidate).ToDictionary(path => path, File.ReadAllBytes);
        using var output = new StringWriter();
        var environment = fixture.Environment(output);
        Assert.Equal(0, await RepairCommand.RunAsync(["--refresh-native", "--check", "--json", "--data-dir", fixture.Node], environment, CancellationToken.None));
        Assert.Equal(configBefore, File.ReadAllBytes(fixture.Config)); Assert.Equal(hooksBefore, File.ReadAllBytes(fixture.Hooks)); Assert.Equal(candidateBefore, File.ReadAllBytes(fixture.Candidate));
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories), path => path.Contains(".cocern-backup-", StringComparison.Ordinal));
        Assert.Contains("Modified hook definitions require Codex review", output.ToString());
        // A reader that denies writing must block the entire update before any file or backup changes.
        using (var reader = new FileStream(fixture.Hooks, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<ClientSettingsException>(() => CodexNativeSetup.RefreshEnrolledNative(fixture.Node, fixture.Profile, "dotnet", fixture.Adapter, TimeProvider.System, check: false));
        Assert.Equal(configBefore, File.ReadAllBytes(fixture.Config)); Assert.Equal(hooksBefore, File.ReadAllBytes(fixture.Hooks)); Assert.Equal(candidateBefore, File.ReadAllBytes(fixture.Candidate));
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories), path => path.Contains(".cocern-backup-", StringComparison.Ordinal));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await RepairCommand.RunAsync(["--refresh-native", "--json", "--data-dir", fixture.Node], environment, CancellationToken.None));
        Assert.DoesNotContain(fixture.OldImage.Replace("\\", "\\\\", StringComparison.Ordinal), File.ReadAllText(fixture.Config));
        Assert.Equal(fixture.OldImageBytes, File.ReadAllBytes(fixture.OldImage));
        Assert.NotNull(NativeRegistration.Read(fixture.Candidate, "codex", fixture.Node, fixture.Profile));
        var hooks = JsonNode.Parse(File.ReadAllBytes(fixture.Hooks))!.AsObject();
        Assert.True((bool)hooks["disableAllHooks"]!);
        Assert.True((bool)hooks["hooks"]!["SessionStart"]![0]!["disabled"]!);
        Assert.True((bool)hooks["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["disabled"]!);
        Assert.Equal("unrelated-helper", (string?)hooks["hooks"]!["SessionStart"]![0]!["hooks"]![1]!["command"]);
        Assert.Contains("COCERN_ROLE = 'admin'", File.ReadAllText(fixture.Config)); Assert.Contains("COCERN_PROFILE = 'diagnostics'", File.ReadAllText(fixture.Config));
        Assert.Equal("custom workflow guidance", File.ReadAllText(CodexNativeSetup.SkillPath(fixture.Root)));
        foreach (var (path, bytes) in enrollmentBefore) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Node), path => Path.GetFileName(path).Contains("startup", StringComparison.OrdinalIgnoreCase));
        var readiness = CodexNativeSetup.Inspect(fixture.Root, fixture.Profile, new Dictionary<string, string> { ["COCERN_NODE_DIR"] = fixture.Node });
        Assert.False(readiness.StartupObserved); Assert.False(readiness.SessionVerified); Assert.False(readiness.WakeReady);
        Assert.False(CodexNativeSetup.RefreshEnrolledNative(fixture.Node, fixture.Profile, "dotnet", fixture.Adapter, TimeProvider.System, check: true));
        Assert.Contains("does not approve trust", output.ToString());
    }

    [Fact]
    public void Native_refresh_refuses_a_standard_entry_or_a_candidate_that_does_not_match_its_old_image_without_mutation()
    {
        using var fixture = new NativeRefreshFixture();
        var validCandidate = File.ReadAllBytes(fixture.Candidate);
        var original = JsonNode.Parse(validCandidate)!.AsObject();
        original["helperVersion"] = Guid.NewGuid().ToString("D"); original.Remove("definition");
        original["definition"] = NativeCheckout.Hash(NativeJoinHook.Canonical(JsonSerializer.SerializeToElement(original)));
        NodeState.WritePrivately(fixture.Candidate, JsonSerializer.SerializeToUtf8Bytes(original));
        var candidateBefore = File.ReadAllBytes(fixture.Candidate); var configBefore = File.ReadAllBytes(fixture.Config); var hooksBefore = File.ReadAllBytes(fixture.Hooks);
        Assert.Throws<ClientSettingsException>(() => CodexNativeSetup.RefreshEnrolledNative(fixture.Node, fixture.Profile, "dotnet", fixture.Adapter, TimeProvider.System, check: false));
        Assert.Equal(candidateBefore, File.ReadAllBytes(fixture.Candidate)); Assert.Equal(configBefore, File.ReadAllBytes(fixture.Config)); Assert.Equal(hooksBefore, File.ReadAllBytes(fixture.Hooks));
        NodeState.WritePrivately(fixture.Candidate, validCandidate); candidateBefore = validCandidate;
        File.Delete(fixture.Config);
        ClientSettings.PlanCodex(fixture.Config, "dotnet", fixture.Adapter, new Dictionary<string, string> { ["COCERN_NODE_DIR"] = fixture.Node }).Apply(TimeProvider.System);
        configBefore = File.ReadAllBytes(fixture.Config);
        Assert.Throws<ClientSettingsException>(() => CodexNativeSetup.RefreshEnrolledNative(fixture.Node, fixture.Profile, "dotnet", fixture.Adapter, TimeProvider.System, check: false));
        Assert.Equal(configBefore, File.ReadAllBytes(fixture.Config)); Assert.Equal(candidateBefore, File.ReadAllBytes(fixture.Candidate)); Assert.Equal(hooksBefore, File.ReadAllBytes(fixture.Hooks));
    }

    private sealed class NativeRefreshFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "cocern-native-refresh-" + Guid.NewGuid().ToString("N"));
        internal readonly string Node, Profile, Config, Hooks, Candidate, OldImage;
        internal readonly string Adapter = typeof(AdapterOptions).Assembly.Location;
        internal readonly byte[] OldImageBytes;
        internal NativeRefreshFixture()
        {
            Node = Path.Combine(Root, "node"); Profile = Path.Combine(Root, ".codex"); Config = Path.Combine(Profile, "config.toml"); Hooks = Path.Combine(Profile, "hooks.json");
            using (var state = NodeState.Open(Node)) { state.SetHub("http://127.0.0.1:5271/", null); state.SaveCredential(new string('a', 64)); }
            OldImage = Path.Combine(Root, "old-package", "CoCern.Adapter.dll"); Directory.CreateDirectory(Path.GetDirectoryName(OldImage)!); File.Copy(Adapter, OldImage); OldImageBytes = File.ReadAllBytes(OldImage);
            ClientSettings.PlanCodex(Config, "dotnet", OldImage, new Dictionary<string, string> { ["COCERN_NODE_DIR"] = Node, ["COCERN_ROLE"] = "admin", ["COCERN_PROFILE"] = "diagnostics" }).Apply(TimeProvider.System);
            Candidate = ClientSettings.StageNativeCandidate(Node, "codex", CodexNativeSetup.HookCommandLine("dotnet", OldImage), clientProfile: Profile)!;
            ClientSettings.PlanNativeCodex(Config, "dotnet", OldImage, Node, Profile, null).Apply(TimeProvider.System);
            var candidate = JsonNode.Parse(File.ReadAllBytes(Candidate))!.AsObject();
            var hooks = new JsonObject { ["disableAllHooks"] = true, ["hooks"] = candidate["hooks"]!.DeepClone() };
            hooks["hooks"]!["SessionStart"]![0]!["disabled"] = true;
            hooks["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["disabled"] = true;
            hooks["hooks"]!["SessionStart"]![0]!["hooks"]!.AsArray().Add(new JsonObject { ["type"] = "command", ["command"] = "unrelated-helper" });
            File.WriteAllText(Hooks, hooks.ToJsonString());
            var skill = CodexNativeSetup.SkillPath(Root); Directory.CreateDirectory(Path.GetDirectoryName(skill)!); File.WriteAllText(skill, "custom workflow guidance");
            foreach (var backup in Directory.EnumerateFiles(Root, "*.cocern-backup-*", SearchOption.AllDirectories)) File.Delete(backup);
        }
        internal SetupEnvironment Environment(TextWriter output) => new()
        {
            Profile = Root, DotnetPath = "dotnet", AdapterDll = Adapter, Out = output, Error = TextWriter.Null,
            RunKey = new NativeRefreshRunKey(), Transport = new NodeTransport(), StartNode = (_, _, _) => throw new InvalidOperationException("Native helper maintenance must not start a node.")
        };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class NativeRefreshRunKey : IRunKey
    {
        public string? Get(string name) => throw new InvalidOperationException("Native helper maintenance must not read startup settings.");
        public void Set(string name, string value) => throw new InvalidOperationException("Native helper maintenance must not change startup settings.");
    }

    [Fact]
    public void Standard_preparation_refreshes_only_the_configured_current_native_helper_metadata_and_preserves_disabled_hooks()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-native-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = Path.Combine(root, ".codex"); var config = Path.Combine(profile, "config.toml");
            var token = Path.Combine(root, "token"); var cache = Path.Combine(root, "cache");
            NodeState.WritePrivately(token, "synthetic-native-update-token"u8.ToArray());
            var environment = new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = token, ["COCERN_URL"] = "http://127.0.0.1:5271/", ["COCERN_ROLE"] = "admin", ["COCERN_PROFILE"] = "diagnostics" };
            var adapter = typeof(AdapterOptions).Assembly.Location;
            ClientSettings.PlanCodex(config, "dotnet", adapter, environment).Apply(TimeProvider.System);
            var candidatePath = ClientSettings.StageLocalNativeCandidate("codex", "dotnet", adapter, environment, cache, clientProfile: profile)!;
            var folder = Path.GetDirectoryName(candidatePath)!;
            var candidate = JsonNode.Parse(File.ReadAllBytes(candidatePath))!.AsObject();
            var oldVersion = Guid.NewGuid().ToString("D"); candidate["helperVersion"] = oldVersion; candidate.Remove("definition");
            candidate["definition"] = NativeCheckout.Hash(NativeJoinHook.Canonical(JsonSerializer.SerializeToElement(candidate)));
            NodeState.WritePrivately(candidatePath, JsonSerializer.SerializeToUtf8Bytes(candidate));
            Assert.NotNull(NativeRegistration.Read(candidatePath, "codex", folder, profile, oldVersion));
            var originalCandidate = File.ReadAllBytes(candidatePath);
            var hooks = Path.Combine(profile, "hooks.json");
            const string disabled = "{\"disableAllHooks\":true,\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"retained --native-hook codex\",\"disabled\":true}]}]}}";
            File.WriteAllText(hooks, disabled);
            var retained = Path.Combine(root, "retained", "CoCern.Adapter.dll"); Directory.CreateDirectory(Path.GetDirectoryName(retained)!); File.WriteAllBytes(retained, []);
            ClientSettings.PlanNativeCodex(config, "dotnet", retained, folder, profile, null).Apply(TimeProvider.System);
            var retainedConfig = File.ReadAllBytes(config);
            Assert.True(ClientSettings.PlanOwnedStandardCodexLaunch(config, "dotnet", adapter).IsUnchanged);
            CodexNativeSetup.PrepareStandard(root, profile, "dotnet", adapter, environment, TimeProvider.System, cache);
            Assert.Equal(retainedConfig, File.ReadAllBytes(config));
            Assert.Equal(originalCandidate, File.ReadAllBytes(candidatePath));

            ClientSettings.PlanNativeCodex(config, "dotnet", adapter, folder, profile, null).Apply(TimeProvider.System);
            var currentConfig = File.ReadAllBytes(config);
            Assert.True(CodexNativeSetup.NeedsCurrentNativeCandidateRefresh(profile, environment, cache));
            var readiness = CodexNativeSetup.PrepareStandard(root, profile, "dotnet", adapter, environment, TimeProvider.System, cache);
            Assert.Equal(currentConfig, File.ReadAllBytes(config));
            Assert.Equal(disabled, File.ReadAllText(hooks));
            Assert.Equal("synthetic-native-update-token", File.ReadAllText(token));
            Assert.NotNull(NativeRegistration.Read(candidatePath, "codex", folder, profile));
            var updated = JsonNode.Parse(File.ReadAllBytes(candidatePath))!.AsObject();
            candidate.Remove("helperVersion"); candidate.Remove("definition"); updated.Remove("helperVersion"); updated.Remove("definition");
            Assert.True(JsonNode.DeepEquals(candidate, updated));
            Assert.NotEqual("standard", readiness.State);
            Assert.False(readiness.StartupObserved);
            Assert.False(readiness.SessionVerified);
            Assert.False(readiness.WakeReady);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
            Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
            File.WriteAllBytes(launcher, []);
            var sdk = Path.Combine(root, "sdk folder");
            Directory.CreateDirectory(sdk);
            File.WriteAllBytes(Path.Combine(sdk, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), []);
            var searchPath = OperatingSystem.IsWindows() ? '"' + sdk + '"' : sdk;
            Assert.True(ClientSettings.IsNativeCodex(configuration, native[3], native[5], searchPath));
            Assert.False(ClientSettings.IsNativeCodex(configuration, native[3], native[5], ""));
            Assert.False(ClientSettings.IsNativeCodex(configuration, native[3], native[5], "."));
            Assert.False(ClientSettings.IsNativeCodex(configuration, Path.Combine(root, "another-connection"), native[5], searchPath));
            ClientSettings.PlanDevelopmentCodex(configuration, Path.Combine(root, "missing", "dotnet.exe"), launcher, deployment, token, replaceLauncher: true).Apply(TimeProvider.System);
            Assert.False(ClientSettings.IsNativeCodex(configuration, native[3], native[5], searchPath));
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

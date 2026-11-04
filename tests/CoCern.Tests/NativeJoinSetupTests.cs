using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Security.Cryptography;
using CoCern.Adapter;
using CoCern.Adapter.Development;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

public sealed class NativeJoinSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Workspace(), "artifacts", "n4s", Guid.NewGuid().ToString("N")[..12]);
    private string Cache => Path.Combine(root, "cache");
    private string Token => Path.Combine(root, "token");
    private string Profile => Path.Combine(root, "profile");
    private string Adapter => Path.Combine(root, "launch path with spaces", "CoCern.Adapter.dll");
    private const string Hub = "http://127.0.0.1:4321/";

    public NativeJoinSetupTests()
    {
        Directory.CreateDirectory(root);
        NodeState.WritePrivately(Token, Encoding.UTF8.GetBytes("synthetic-setup-secret"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Staging_is_private_inactive_and_keeps_raw_entries_and_unknown_disabled_hooks(string provider)
    {
        var connection = Connection();
        var active = Path.Combine(root, "active-hooks.json");
        const string untouched = "{\"hooks\":{\"Stop\":[{\"matcher\":\"mine\",\"unknown\":17,\"hooks\":[{\"type\":\"command\",\"command\":\"other --stop-hook\",\"disabled\":true}]}]},\"other\":true}";
        File.WriteAllText(active, untouched);
        var command = SetupEnvironment.AdapterCommandLine("dotnet", Adapter);
        var path = ClientSettings.StageNativeCandidate(connection.Folder, provider, command);
        Assert.True(File.Exists(path));
        Assert.True(NodeState.IsPrivateFile(path));
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var candidate = document.RootElement;
        Assert.Equal(provider, candidate.GetProperty("provider").GetString());
        Assert.Equal("inactive", candidate.GetProperty("state").GetString());
        Assert.Equal(connection.Folder, candidate.GetProperty("connection").GetString());
        var hooks = candidate.GetProperty("hooks");
        Assert.Equal("^mcp__cocern__.*$", hooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
        var pre = hooks.GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        Assert.Contains("--native-hook " + provider, pre.GetProperty("command").GetString());
        Assert.Contains("--connection", pre.GetProperty("command").GetString());
        Assert.False(pre.TryGetProperty("permissionDecision", out _));
        Assert.DoesNotContain("synthetic-setup-secret", File.ReadAllText(path));
        Assert.DoesNotContain("sessionToken", File.ReadAllText(path));
        Assert.Equal(untouched, File.ReadAllText(active));
        if (provider == "claude") Assert.True(hooks.GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("asyncRewake").GetBoolean());
    }

    [Fact]
    public void Repeated_staging_is_idempotent_and_launcher_changes_refresh_only_the_owned_candidate()
    {
        var folder = Connection().Folder;
        var first = ClientSettings.StageNativeCandidate(folder, "claude", "\"old launcher.exe\"");
        Assert.True(File.Exists(first));
        var bytes = File.ReadAllBytes(first);
        var modified = File.GetLastWriteTimeUtc(first);
        Assert.Equal(first, ClientSettings.StageNativeCandidate(folder, "claude", "\"old launcher.exe\""));
        Assert.Equal(bytes, File.ReadAllBytes(first)); Assert.Equal(modified, File.GetLastWriteTimeUtc(first));
        ClientSettings.StageNativeCandidate(folder, "claude", "\"new launcher.exe\"");
        Assert.Contains("new launcher.exe", File.ReadAllText(first));
        Assert.Single(Directory.EnumerateFiles(folder, "*.candidate.json"));
    }

    [Fact]
    public void Corrupt_inactive_candidate_is_left_untouched_and_cannot_break_raw_setup()
    {
        var folder = Connection().Folder;
        var path = Path.Combine(folder, "native-client-claude.candidate.json");
        NodeState.WritePrivately(path, Encoding.UTF8.GetBytes("{corrupt"));
        Assert.Null(ClientSettings.StageNativeCandidate(folder, "claude", "adapter"));
        Assert.Equal("{corrupt", File.ReadAllText(path));
    }

    [Fact]
    public void Unverified_promotion_keeps_the_single_raw_registration_and_all_settings_bytes()
    {
        var folder = Connection().Folder;
        var path = Path.Combine(root, "raw-config.json");
        const string raw = "{\"mcpServers\":{\"cocern\":{\"command\":\"raw-adapter\"},\"other\":{\"command\":\"mine\"}},\"unknown\":true}";
        File.WriteAllText(path, raw);
        ClientSettings.StageNativeCandidate(folder, "claude", "adapter");
        var plan = ClientSettings.PlanNativePromotion(path, folder, "claude");
        Assert.True(plan.IsUnchanged);
        Assert.Equal(ClientChange.Unchanged, plan.Apply(TimeProvider.System));
        Assert.Equal(raw, File.ReadAllText(path));
        Assert.Single(JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject(), item => item.Key == "cocern");
        Assert.Empty(Directory.EnumerateFiles(root, "*.bak*"));
    }

    [Fact]
    public void Helper_reads_only_the_explicit_private_connection_and_never_discovers_another()
    {
        var connection = Connection();
        var options = NativeJoinHook.ReadConnection(connection.Folder);
        Assert.NotNull(options);
        Assert.Equal("synthetic-setup-secret", options.Token);
        Assert.Equal(Hub, options.BaseUri.AbsoluteUri);
        Assert.Null(NativeJoinHook.ReadConnection(Path.Combine(root, "missing")));
        Assert.Null(NativeJoinHook.ReadConnection("relative"));
    }

    [Fact]
    public void Local_connect_stages_both_candidates_without_loading_native_hooks_or_replacing_raw_launch()
    {
        Assert.Equal(0, ConnectCommand.Run(["--token-file", Token, "--hub", Hub], Profile, null, "dotnet", Adapter, TextWriter.Null, TextWriter.Null, nativeCacheRoot: Cache));
        var folder = Connection().Folder;
        Assert.Equal(2, Directory.EnumerateFiles(folder, "*.candidate.json").Count());
        var codex = File.ReadAllText(Path.Combine(Profile, ".codex", "config.toml"));
        var claude = File.ReadAllText(Path.Combine(Profile, ".claude.json"));
        Assert.DoesNotContain("--native", codex); Assert.DoesNotContain("--native", claude);
        Assert.DoesNotContain("--native", File.ReadAllText(Path.Combine(Profile, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(Profile, ".codex", "hooks.json")));
    }

    [Fact]
    public void Development_launcher_swap_refreshes_candidates_without_activating_the_provider()
    {
        var config = Path.Combine(Profile, ".claude.json");
        ClientSettings.PlanClaudeCode(config, "dotnet", Adapter, new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = Token, ["COCERN_URL"] = Hub }).Apply(TimeProvider.System);
        var deployment = Path.Combine(root, "deployment");
        var launcher = Path.Combine(deployment, "launcher path", "CoCern.Adapter.dll");
        DevelopmentSetup.ConfigureClaudeCode(config, "dotnet", launcher, deployment, Token, replaceLauncher: true, nativeCacheRoot: Cache);
        var candidate = Path.Combine(Connection().Folder, "native-client-claude.candidate.json");
        Assert.True(File.Exists(candidate));
        Assert.Contains("launcher path", File.ReadAllText(candidate));
        Assert.DoesNotContain("--native", File.ReadAllText(config));
        Assert.DoesNotContain("--native", File.ReadAllText(Path.Combine(Profile, ".claude", "settings.json")));
    }

    [Fact]
    public async Task Hook_process_refuses_its_missing_private_connection_before_environment_fallback()
    {
        var adapter = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "CoCern.Adapter", "debug", "CoCern.Adapter.dll"));
        Assert.True(File.Exists(adapter));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (dotnet is null || !Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
            dotnet = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") : "/usr/bin/dotnet";
        Assert.True(File.Exists(dotnet));
        var local = Directory.CreateDirectory(Path.Combine(root, "child-local")).FullName;
        var sentinel = Path.Combine(local, "sentinel");
        File.WriteAllText(sentinel, "unchanged");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(adapter); start.ArgumentList.Add("--native-hook"); start.ArgumentList.Add("claude"); start.ArgumentList.Add("--connection"); start.ArgumentList.Add(Path.Combine(root, "missing-connection"));
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PROCESSOR_ARCHITECTURE" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["PATH"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_CLI_HOME"] = Directory.CreateDirectory(Path.Combine(root, "child-dotnet")).FullName;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["USERPROFILE"] = Directory.CreateDirectory(Path.Combine(root, "child-profile")).FullName;
        start.Environment["HOME"] = start.Environment["USERPROFILE"];
        start.Environment["LOCALAPPDATA"] = local;
        start.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(root, "child-roaming")).FullName;
        start.Environment["TEMP"] = Directory.CreateDirectory(Path.Combine(root, "child-temp")).FullName;
        start.Environment["TMP"] = start.Environment["TEMP"];
        start.Environment["COCERN_URL"] = "deliberately-invalid-origin";
        start.Environment["COCERN_TOKEN"] = "synthetic-never-used";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, process.ExitCode);
        Assert.Equal("Native hooks need their configured private connection.", (await error).Trim());
        Assert.Empty(await output);
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.False(Directory.Exists(Path.Combine(local, "CoCern", "local-wake")));
    }

    [Fact]
    public async Task Registration_pair_authenticates_only_configured_scope_and_persists_no_receipt()
    {
        var (registration, candidate) = Registration();
        var original = File.ReadAllBytes(candidate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        Assert.True(await registration.AnswerAsync(timeout.Token, TimeSpan.FromSeconds(2)));
        var verified = await accepting;
        Assert.NotNull(verified);
        Assert.Equal("codex", verified.Provider);
        Assert.Equal(Connection().Folder, verified.Folder);
        Assert.Equal(Profile, verified.Profile);
        Assert.DoesNotContain("synthetic-setup-secret", verified.ToString());
        Assert.Equal(original, File.ReadAllBytes(candidate));
        Assert.Empty(Directory.EnumerateFiles(verified.Folder, "*.receipt*"));
        Assert.Null(await registration.AcceptAsync(timeout.Token, TimeSpan.FromMilliseconds(100)));
        Assert.False(await registration.AnswerAsync(timeout.Token, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Registration_candidate_binds_exact_profile_provider_connection_and_definition()
    {
        var (registration, candidate) = Registration();
        Assert.Equal("codex", registration.Provider);
        Assert.Equal(Profile, registration.Profile);
        Assert.Equal(64, registration.Definition.Length);
        Assert.Null(NativeRegistration.Read(candidate, "claude", Connection().Folder, Profile));
        var otherProfile = Directory.CreateDirectory(Path.Combine(root, "different-profile")).FullName;
        Assert.Null(NativeRegistration.Read(candidate, "codex", Connection().Folder, otherProfile));
        Assert.Null(NativeRegistration.Read(candidate, "codex", root, Profile));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("credentialScope")]
    [InlineData("command")]
    public void Registration_rejects_a_modified_owned_candidate_without_adopting_it(string field)
    {
        var (_, candidate) = Registration();
        var data = JsonNode.Parse(File.ReadAllBytes(candidate))!.AsObject();
        if (field == "command") data["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"] = "unrelated command";
        else data[field] = field == "provider" ? "claude" : new string('0', 64);
        NodeState.WritePrivately(candidate, JsonSerializer.SerializeToUtf8Bytes(data));
        Assert.Null(NativeRegistration.Read(candidate, "codex", Connection().Folder, Profile));
    }

    [Fact]
    public async Task Registration_without_a_trusted_helper_is_bounded_and_cannot_select_a_native_context()
    {
        var (registration, _) = Registration();
        var elapsed = Stopwatch.StartNew();
        Assert.Null(await registration.AcceptAsync(default, TimeSpan.FromMilliseconds(150)));
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("wrong-provider")]
    [InlineData("wrong-mac")]
    [InlineData("stale-nonce")]
    [InlineData("expired")]
    [InlineData("unknown-field")]
    public async Task Registration_refuses_invalid_bounded_wire_before_a_native_context_exists(string form)
    {
        var (registration, _) = Registration();
        Assert.NotEmpty(registration.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        await using var client = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        var challenge = await ReadFrame(client, timeout.Token);
        Assert.Equal(new[] { "deadline", "definition", "mac", "nonce", "profile", "provider", "scope", "version" }.Order(), challenge.EnumerateObject().Select(item => item.Name).Order());
        Assert.DoesNotContain("synthetic-setup-secret", challenge.GetRawText());
        Assert.DoesNotContain("session", challenge.GetRawText(), StringComparison.OrdinalIgnoreCase);
        if (form == "oversize")
        {
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, NativeRegistration.MaximumFrameBytes + 1);
            await client.WriteAsync(prefix, timeout.Token); await client.FlushAsync(timeout.Token);
        }
        else
        {
            var response = JsonNode.Parse(challenge.GetRawText())!.AsObject();
            if (form == "wrong-provider") response["provider"] = "claude";
            if (form == "wrong-mac") response["mac"] = new string('0', 64);
            if (form == "stale-nonce") response["nonce"] = new string('0', 64);
            if (form == "expired") response["deadline"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            if (form == "unknown-field") response["tool_input"] = new JsonObject { ["untrusted"] = true };
            await WriteFrame(client, JsonSerializer.SerializeToElement(response), timeout.Token);
        }
        Assert.Null(await accepting);
    }

    [Fact]
    public async Task Registration_cancellation_keeps_the_original_token_and_no_listener()
    {
        var (registration, _) = Registration();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var serverError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration.AcceptAsync(canceled.Token));
        var clientError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration.AnswerAsync(canceled.Token));
        Assert.Equal(canceled.Token, serverError.CancellationToken); Assert.Equal(canceled.Token, clientError.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        Assert.True(await registration.AnswerAsync(timeout.Token, TimeSpan.FromSeconds(2)));
        Assert.NotNull(await accepting);
    }

    [Fact]
    public async Task Registration_instances_use_fresh_nonces_and_refuse_cross_instance_replay()
    {
        var (registration, _) = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        var second = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        await using var one = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var two = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await one.ConnectAsync(timeout.Token); await two.ConnectAsync(timeout.Token);
        var challengeOne = await ReadFrame(one, timeout.Token); var challengeTwo = await ReadFrame(two, timeout.Token);
        Assert.NotEqual(challengeOne.GetProperty("nonce").GetString(), challengeTwo.GetProperty("nonce").GetString());
        await WriteFrame(one, challengeTwo, timeout.Token); await WriteFrame(two, challengeOne, timeout.Token);
        Assert.Null(await first); Assert.Null(await second);
    }

    [Fact]
    public async Task Registration_concurrent_instances_are_bounded_and_capacity_is_released()
    {
        var (registration, _) = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = Enumerable.Range(0, NativeRegistration.MaximumInstances).Select(_ => registration.AcceptAsync(timeout.Token, TimeSpan.FromMilliseconds(300))).ToArray();
        var refused = registration.AcceptAsync(timeout.Token, TimeSpan.FromMilliseconds(150));
        Assert.True(refused.IsCompleted);
        Assert.Null(await refused);
        Assert.All(await Task.WhenAll(waiting), value => Assert.Null(value));
        var fresh = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        Assert.True(await registration.AnswerAsync(timeout.Token, TimeSpan.FromSeconds(2)));
        Assert.NotNull(await fresh);
    }

    [Fact]
    public async Task Wiring_backend_selects_native_discovery_only_after_a_real_fake_helper_exchange_and_keeps_call_MAC_required()
    {
        await using var relay = new WiringRelayCounter();
        var (registration, args) = WiringCandidate(relay.Origin);
        await using var child = WiringStart(args);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var helper = registration.AnswerAsync(timeout.Token);
        try
        {
            var initialized = await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-wiring-fixture", version = "1" } }, timeout.Token);
            Assert.True(await helper);
            Assert.Contains("Your native client supplies this chat's current connection context", initialized.GetProperty("result").GetProperty("instructions").GetString());
            await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var listed = await WiringRpc(child, 2, "tools/list", new { }, timeout.Token);
            var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(new[] { "ack", "chat_directory", "check_inbox", "send_message" }, tools.Select(tool => tool.GetProperty("name").GetString()).Order());
            foreach (var tool in tools)
                foreach (var own in new[] { "sessionId", "sessionToken", "repositoryId", "requestId", "_cocernNative" })
                    Assert.False(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty(own, out _));
            var send = tools.Single(tool => tool.GetProperty("name").GetString() == "send_message");
            Assert.True(send.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("recipientSessionId", out _));
            foreach (var arguments in new object[] { new { }, new { _cocernNative = new { mac = "forged", invocation = "forged" } } })
            {
                var result = await WiringRpc(child, 3, "tools/call", new { name = "check_inbox", arguments }, timeout.Token);
                Assert.True(result.TryGetProperty("error", out _) || result.GetProperty("result").TryGetProperty("isError", out var failed) && failed.GetBoolean());
            }
            Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        }
        finally
        {
            timeout.Cancel();
            try { await helper; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Wiring_native_MCP_accepts_a_valid_signed_read_without_any_hub_mutation()
    {
        await using var relay = new WiringReadRelay();
        await using var binding = new NativeJoinHookTests.HookFixture(origin: relay.Origin);
        Directory.CreateDirectory(Profile);
        var candidate = ClientSettings.StageNativeCandidate(binding.Folder, "claude", "fixture-helper", clientProfile: Profile)!;
        var registration = NativeRegistration.Read(candidate, "claude", binding.Folder, Profile)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Only the synthetic HttpMessageHandler allocates the private fixture handle. The loopback endpoint is read-only.
        await binding.Join.EnsureAsync(binding.Capture, timeout.Token);
        var stamped = await NativeJoinHook.StampAsync(binding.Join, binding.Capture, "chat_directory", "actual-mcp-read", JsonSerializer.SerializeToElement(new { }), timeout.Token);
        Assert.Equal(0, relay.Reads);
        await using var child = WiringStart(["--native-client", "claude", "--connection", binding.Folder, "--profile", Profile]);
        var helper = registration.AnswerAsync(timeout.Token);
        try
        {
            await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-signed-read-fixture", version = "1" } }, timeout.Token);
            Assert.True(await helper);
            await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var response = await WiringRpc(child, 2, "tools/call", new { name = "chat_directory", arguments = stamped }, timeout.Token);
            var result = response.GetProperty("result");
            using var reply = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
            var code = ChatLocators.Text(reply.RootElement, "code") ?? "missing-code";
            var safeCode = code.Length <= 128 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? code : "unrecognized-code";
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), $"{safeCode}; fixture reads={relay.Reads},preflights={relay.Preflights},probes={relay.Probes},mutations={relay.Mutations},invalidWire={relay.InvalidWire},hints={relay.HintAttempts}");
            Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
            Assert.Equal("fixture-peer", reply.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("sessionId").GetString());
            Assert.True(relay.Reads > 0); Assert.True(relay.Preflights > 0); Assert.True(relay.Probes > 0);
            Assert.True(relay.HintAttempts > 0); // This exact fixture channel intentionally remains unavailable.
            Assert.Equal(0, relay.Mutations); Assert.Equal(0, relay.InvalidWire);
            Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
        }
        finally { timeout.Cancel(); try { await helper; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("no-helper")]
    [InlineData("wrong-profile")]
    [InlineData("changed-credential")]
    public async Task Wiring_private_backend_refuses_unverified_scope_without_raw_fallback_or_relay(string refusal)
    {
        await using var relay = new WiringRelayCounter();
        var (_, args) = WiringCandidate(relay.Origin);
        if (refusal == "wrong-profile") args[^1] = Directory.CreateDirectory(Path.Combine(root, "another-profile")).FullName;
        if (refusal == "changed-credential") NodeState.WritePrivately(Token, Encoding.UTF8.GetBytes("different-synthetic-token"));
        await using var child = WiringStart(args);
        child.Process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(refusal == "no-helper" ? 25 : 10));
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, child.Process.ExitCode);
        Assert.Equal("Native client registration could not be verified.", (await child.Error).Trim());
        Assert.Empty(await child.Process.StandardOutput.ReadToEndAsync(timeout.Token));
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(child.Sentinel)!, "CoCern", "local-wake")));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("misplaced")]
    public async Task Wiring_private_backend_rejects_ambiguous_arguments_before_environment_or_relay(string malformed)
    {
        await using var relay = new WiringRelayCounter();
        var (_, args) = WiringCandidate(relay.Origin);
        args = malformed switch
        {
            "duplicate" => [.. args, "--profile", Profile],
            "missing" => args[..^1],
            "misplaced" => ["--extra", "value", .. args],
            _ => [.. args, "--extra", "value"]
        };
        await using var child = WiringStart(args);
        child.Process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, child.Process.ExitCode);
        Assert.Equal("Native client registration could not be verified.", (await child.Error).Trim());
        Assert.Empty(await child.Process.StandardOutput.ReadToEndAsync(timeout.Token));
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
    }

    [Fact]
    public async Task Wiring_original_raw_backend_keeps_its_open_tool_and_manual_session_guidance()
    {
        await using var relay = new WiringRelayCounter();
        await using var child = WiringStart([], relay.Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initialized = await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-raw-fixture", version = "1" } }, timeout.Token);
        Assert.Contains("Keep one session per chat and reuse its handle across reconnects", initialized.GetProperty("result").GetProperty("instructions").GetString());
        await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var listed = await WiringRpc(child, 2, "tools/list", new { }, timeout.Token);
        var open = listed.GetProperty("result").GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "session_open");
        Assert.True(open.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("requestId", out _));
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
    }

    [Fact]
    public async Task Wiring_development_launcher_forwards_context_and_every_backend_generation_requires_fresh_proof()
    {
        await using var relay = new WiringRelayCounter();
        var (registration, args) = WiringCandidate(relay.Origin);
        var deployment = WiringDeployment();
        await using var child = WiringStart(["--dev-launch", deployment, .. args]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var helper = registration.AnswerAsync(timeout.Token);
        try
        {
            var initialized = await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-launcher-fixture", version = "1" } }, timeout.Token);
            Assert.Contains("Your native client supplies this chat's current connection context", initialized.GetProperty("result").GetProperty("instructions").GetString());
            Assert.True(await helper);
            await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            AdapterDeployment.Publish(WiringOutput(), deployment); // Same immutable binary, new connection generation.
            var refreshed = WiringRpc(child, 2, "tools/list", new { }, timeout.Token);
            helper = registration.AnswerAsync(timeout.Token);
            var listed = await refreshed;
            Assert.True(await helper);
            var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(new[] { "ack", "chat_directory", "check_inbox", "send_message" }, tools.Select(tool => tool.GetProperty("name").GetString()).Order());
            Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        }
        finally { timeout.Cancel(); try { await helper; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task Wiring_development_launcher_without_helper_refuses_before_advertising_any_catalog()
    {
        await using var relay = new WiringRelayCounter();
        var (_, args) = WiringCandidate(relay.Origin);
        await using var child = WiringStart(["--dev-launch", WiringDeployment(), .. args]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"owned-refusal-fixture\",\"version\":\"1\"}}}");
        Assert.Null(await child.Process.StandardOutput.ReadLineAsync(timeout.Token));
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, child.Process.ExitCode);
        Assert.Contains("Native client registration could not be verified.", await child.Error);
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
    }

    [Fact]
    public async Task Wiring_original_development_launcher_still_proxies_the_raw_catalog()
    {
        await using var relay = new WiringRelayCounter();
        await using var child = WiringStart(["--dev-launch", WiringDeployment()], relay.Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-raw-launcher-fixture", version = "1" } }, timeout.Token);
        await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var listed = await WiringRpc(child, 2, "tools/list", new { }, timeout.Token);
        Assert.Contains(listed.GetProperty("result").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "session_open");
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
    }



    [Fact]
    public async Task FixRound1_missing_private_selector_refuses_valid_raw_settings_before_startup()
    {
        await using var relay = new WiringRelayCounter();
        var (_, args) = WiringCandidate(relay.Origin);
        await using var child = WiringStart(args[2..], relay.Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"owned-incomplete-private\",\"version\":\"1\"}}}");
        var response = await child.Process.StandardOutput.ReadLineAsync(timeout.Token);
        child.Process.StandardInput.Close();
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
        Assert.Null(response);
        Assert.Equal(1, child.Process.ExitCode);
        Assert.Equal("Native client registration could not be verified.", (await child.Error).Trim());
    }

    [Fact]
    public async Task FixRound1_private_launcher_refuses_initial_actual_flag_ignoring_backend_before_raw_startup()
    {
        await using var relay = new WiringRelayCounter();
        var (_, args) = WiringCandidate(relay.Origin);
        var deployment = Path.Combine(root, "old deployment with spaces");
        AdapterDeployment.Publish(FixRound1OldOutput(), deployment);
        await using var child = WiringStart(["--dev-launch", deployment, .. args], relay.Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"owned-old-private\",\"version\":\"1\"}}}");
        var response = await child.Process.StandardOutput.ReadLineAsync(timeout.Token);
        child.Process.StandardInput.Close();
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
        Assert.Null(response);
        Assert.Equal(1, child.Process.ExitCode);
        Assert.Contains("Native client registration could not be verified.", await child.Error);
    }

    [Fact]
    public async Task FixRound1_private_launcher_never_admits_actual_flag_ignoring_replacement_or_forwards_raw_calls()
    {
        await using var relay = new WiringRelayCounter();
        var (registration, args) = WiringCandidate(relay.Origin);
        var deployment = WiringDeployment();
        await using var child = WiringStart(["--dev-launch", deployment, .. args], relay.Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var helper = registration.AnswerAsync(timeout.Token);
        try
        {
            await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-replacement-fixture", version = "1" } }, timeout.Token);
            Assert.True(await helper);
            await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            AdapterDeployment.Publish(FixRound1OldOutput(), deployment);
            var listed = await WiringRpc(child, 2, "tools/list", new { }, timeout.Token);
            var call = await WiringRpc(child, 3, "tools/call", new { name = "chat_directory", arguments = new { repositoryId = "repo" } }, timeout.Token);
            var rejected = call.TryGetProperty("error", out _) || call.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();
            Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
            Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
            Assert.DoesNotContain(listed.GetProperty("result").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "session_open");
            Assert.True(rejected);
        }
        finally { timeout.Cancel(); try { await helper; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixRound1_actual_PreToolUse_child_without_dispatch_has_zero_relay_traffic(bool established)
    {
        await using var relay = new WiringReadRelay();
        await using var fixture = new NativeJoinHookTests.HookFixture(origin: relay.Origin);
        if (established) await fixture.Join.EnsureAsync(fixture.Capture, default);
        fixture.Requests = 0;
        await using var child = WiringStart(["--native-hook", "claude", "--connection", fixture.Folder]);
        var text = JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", session_id = fixture.Capture.NativeId, cwd = fixture.Capture.Cwd,
            tool_name = "mcp__cocern__send_message", tool_use_id = "cancelled-before-MCP", tool_input = new { recipientSessionId = "peer", text = "never dispatch" } });
        await child.Process.StandardInput.WriteAsync(text); child.Process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = await child.Process.StandardOutput.ReadToEndAsync(timeout.Token);
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, relay.Connections); Assert.Equal(0, relay.Reads); Assert.Equal(0, relay.Preflights);
        Assert.Equal(0, relay.Probes); Assert.Equal(0, relay.Mutations); Assert.Equal(0, relay.InvalidWire); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
        Assert.Equal(established ? 0 : 2, child.Process.ExitCode);
        if (established) Assert.Contains("updatedInput", output); else Assert.Empty(output);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
    }

    [Fact]
    public async Task FixRound1_cancelled_development_hook_preserves_independent_sentinel_descendant()
    {
        var source = FixturePackage("CoCern.NativeProcessFixture");
        var (environment, _) = WiringStartInfo([]);
        environment.Environment["DOTNET_HOST_PATH"] = environment.FileName;
        var deployment = Path.Combine(root, "synthetic-helper-deployment");
        AdapterDeployment.Publish(source, deployment);
        using var cancel = new CancellationTokenSource();
        using var output = new StringWriter(); using var error = new StringWriter();
        var run = NativeJoinHook.RunDevelopmentAsync(deployment, environment.FileName, "claude", root, new StringReader("{}"), output, error, cancel.Token,
            configureProcess: start => { start.Environment.Clear(); foreach (var pair in environment.Environment) start.Environment[pair.Key] = pair.Value; });
        Process? sentinel = null; Process? helper = null;
        try
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var marker = Path.Combine(root, "owned-pids.json");
            while (!File.Exists(marker)) { await Task.Delay(25, ready.Token); if (run.IsCompleted) await run; }
            using var pids = JsonDocument.Parse(File.ReadAllText(marker));
            sentinel = Process.GetProcessById(pids.RootElement.GetProperty("sentinel").GetInt32());
            helper = Process.GetProcessById(pids.RootElement.GetProperty("helper").GetInt32());
            Assert.False(sentinel.HasExited); Assert.False(helper.HasExited);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(helper.HasExited);
            Assert.False(sentinel.HasExited, "Cancelling an owned hook must leave its separately managed descendant alive.");
        }
        finally
        {
            cancel.Cancel();
            foreach (var owned in new[] { helper, sentinel })
            {
                if (owned is null) continue;
                try { if (!owned.HasExited) { owned.Kill(); await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
                finally { owned.Dispose(); }
            }
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task FixRound2_packaged_actual_legacy_adapter_ignores_private_arguments_and_serves_raw_catalog()
    {
        await using var relay = new WiringRelayCounter();
        var (_, args) = WiringCandidate(relay.Origin);
        var package = FixRound1OldOutput();
        await using var child = WiringStart(args, relay.Origin, Path.Combine(package, "CoCern.Adapter.dll"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initialized = await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-actual-legacy-precondition", version = "1" } }, timeout.Token);
        Assert.Contains("Keep one session per chat", initialized.GetProperty("result").GetProperty("instructions").GetString());
        await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var listed = await WiringRpc(child, 2, "tools/list", new { }, timeout.Token);
        Assert.Contains(listed.GetProperty("result").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "session_open");
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
    }

    [Theory]
    [InlineData("joined")]
    [InlineData("orphaned-joined")]
    [InlineData("mixed")]
    [InlineData("joined-hook")]
    [InlineData("joined-development-root")]
    [InlineData("upper-joined")]
    [InlineData("upper-pairs")]
    [InlineData("bare-joined")]
    public async Task FixRound2_private_option_names_refuse_all_unsupported_forms_before_raw_configuration(string form)
    {
        await using var relay = new WiringRelayCounter();
        var (_, valid) = WiringCandidate(relay.Origin);
        var folder = valid[3];
        var args = form switch
        {
            "joined" => new[] { "--native-client=codex", "--connection=" + folder, "--profile=" + Profile },
            "orphaned-joined" => ["--connection=" + folder, "--profile=" + Profile],
            "mixed" => ["--native-client=codex", "--connection", folder, "--profile=" + Profile],
            "joined-hook" => ["--native-hook=claude", "--connection=" + folder, "--profile=" + Profile],
            "joined-development-root" => ["--development-root=" + root, "--connection=" + folder, "--profile=" + Profile],
            "upper-joined" => ["--NATIVE-CLIENT=codex", "--CONNECTION=" + folder, "--PROFILE=" + Profile],
            "bare-joined" => ["native-client=codex", "connection=" + folder, "profile=" + Profile],
            _ => ["--NATIVE-CLIENT", "codex", "--CONNECTION", folder, "--PROFILE", Profile]
        };
        await using var child = WiringStart(args, relay.Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"owned-private-name-refusal\",\"version\":\"1\"}}}");
        var response = await child.Process.StandardOutput.ReadLineAsync(timeout.Token);
        child.Process.StandardInput.Close(); await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
        Assert.Null(response); Assert.Equal(1, child.Process.ExitCode);
        Assert.Equal("Native client registration could not be verified.", (await child.Error).Trim());
    }

    private string FixRound1OldOutput() => FixturePackage("CoCern.NativeLegacyFixture");

    private string FixturePackage(string assembly)
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", assembly, new DirectoryInfo(AppContext.BaseDirectory).Name));
        Assert.True(File.Exists(Path.Combine(source, assembly + ".dll")), "Build the test fixture dependency in this invocation's isolated outputs.");
        if (assembly == "CoCern.NativeLegacyFixture") VerifyLegacySource(source);
        var package = Path.Combine(root, "fixture-packages", assembly);
        Assert.False(Directory.Exists(package));
        Directory.CreateDirectory(package);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(package, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var original = Path.Combine(source, assembly + suffix);
            var copied = Path.Combine(package, "CoCern.Adapter" + suffix);
            File.Copy(original, copied);
            Assert.True(SHA256.HashData(File.ReadAllBytes(original)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(copied))));
        }
        return package;
    }

    private static void VerifyLegacySource(string source)
    {
        var archivePath = Path.Combine(source, "legacy-adapter-source.zip");
        var manifestPath = Path.Combine(source, "source-manifest.json");
        Assert.Equal("9AA0030EFF65B527F20D8AD2296ABB6D0F1432751E360047875944BB3F4978ED", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))));
        Assert.Equal("28B267DDF8AD3C7C8C7FBAF03CD936D30DB02F7FA5DC00411D9F3A85318EE857", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        Assert.Equal("349db7e0948954a53596f73a7d8e7326df7de6bb", manifest.RootElement.GetProperty("Commit").GetString());
        Assert.Equal("ae8d82e1a960f9dfead83330863bf948e6698107", manifest.RootElement.GetProperty("AdapterTree").GetString());
        var expected = manifest.RootElement.GetProperty("Files").EnumerateArray().ToDictionary(row => row.GetProperty("Path").GetString()!, row => row.Clone());
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        Assert.Equal(expected.Count, files.Length);
        foreach (var entry in files)
        {
            Assert.StartsWith("src/CoCern.Adapter/", entry.FullName); Assert.DoesNotContain("..", entry.FullName);
            var row = expected[entry.FullName]; Assert.Equal(row.GetProperty("Length").GetInt64(), entry.Length);
            using var stream = entry.Open();
            Assert.Equal(row.GetProperty("SHA256").GetString(), Convert.ToHexString(SHA256.HashData(stream)));
        }
    }

    private static string WiringOutput() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "CoCern.Adapter", new DirectoryInfo(AppContext.BaseDirectory).Name));
    private string WiringDeployment()
    {
        var deployment = Path.Combine(root, "deployment with spaces");
        AdapterDeployment.Publish(WiringOutput(), deployment);
        return deployment;
    }

    private (NativeRegistration Registration, string[] Args) WiringCandidate(string origin)
    {
        Directory.CreateDirectory(Profile);
        var connection = LocalWakeConnection.Create(new AdapterOptions(origin, "synthetic-setup-secret"), Token, Cache)!;
        var candidate = ClientSettings.StageNativeCandidate(connection.Folder, "codex", "fixture-helper", clientProfile: Profile)!;
        return (NativeRegistration.Read(candidate, "codex", connection.Folder, Profile)!, ["--native-client", "codex", "--connection", connection.Folder, "--profile", Profile]);
    }

    private WiringChild WiringStart(string[] args, string? rawOrigin = null, string? adapter = null)
    {
        var (start, sentinel) = WiringStartInfo(args, rawOrigin, adapter);
        return new(Process.Start(start)!, sentinel);
    }

    private (ProcessStartInfo Start, string Sentinel) WiringStartInfo(string[] args, string? rawOrigin = null, string? adapter = null)
    {
        adapter ??= Path.Combine(WiringOutput(), "CoCern.Adapter.dll");
        Assert.True(File.Exists(adapter));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (dotnet is null || !Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
            dotnet = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") : "/usr/bin/dotnet";
        Assert.True(File.Exists(dotnet));
        var home = Directory.CreateDirectory(Path.Combine(root, "wiring-child")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(home, "local")).FullName;
        var sentinel = Path.Combine(local, "sentinel"); File.WriteAllText(sentinel, "unchanged");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(adapter); foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PROCESSOR_ARCHITECTURE" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        var gitFolder = OperatingSystem.IsWindows() ? "C:/Program Files/Git/cmd" : "/usr/bin";
        Assert.True(File.Exists(Path.Combine(gitFolder, OperatingSystem.IsWindows() ? "git.exe" : "git")));
        start.Environment["PATH"] = Path.GetDirectoryName(dotnet)! + Path.PathSeparator + gitFolder;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_CLI_HOME"] = Directory.CreateDirectory(Path.Combine(home, "dotnet")).FullName;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["USERPROFILE"] = home; start.Environment["HOME"] = home; start.Environment["LOCALAPPDATA"] = local;
        start.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(home, "roaming")).FullName;
        start.Environment["TEMP"] = Directory.CreateDirectory(Path.Combine(home, "temp")).FullName; start.Environment["TMP"] = start.Environment["TEMP"];
        start.Environment["COCERN_URL"] = rawOrigin ?? "deliberately-invalid-origin";
        start.Environment["COCERN_TOKEN"] = "synthetic-never-used";
        return (start, sentinel);
    }

    private static async Task<JsonElement> WiringRpc(WiringChild child, int id, string method, object args, CancellationToken ct)
    {
        await child.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = args }));
        while (await child.Process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("id", out var returned) && returned.TryGetInt32(out var actual) && actual == id) return json.RootElement.Clone();
        }
        throw new InvalidOperationException("Own adapter ended before the requested MCP response; fixed error classes: " + WiringClassifyError(await child.Error));
    }

    private static string WiringClassifyError(string line)
    {
        // Fixed vocabulary only. Unknown text, paths, payloads, environment values and credentials never reach a report.
        foreach (var (fragment, classification) in new[]
        {
            ("COCERN_URL", "invalid-origin"), ("CoCern adapter stopped unexpectedly", "adapter-fixed-unexpected"),
            ("not recognized as an internal or external command", "cmd-command-tokenization"), ("operable program or batch file", "cmd-command-tokenization-continuation"),
            ("specified command or file was not found", "dotnet-command-file-missing"), ("does not exist", "path-does-not-exist"),
            ("hostpolicy", "hostpolicy-load"), ("hostfxr", "hostfxr-load"), ("runtimeconfig", "runtimeconfig"),
            ("dependencies manifest", "dependency-manifest"), ("JSON parsing", "host-json-parse"), ("too long", "path-too-long"),
            ("CoreCLR", "coreclr-load"), (".NET to run this application", "framework-missing"), ("error", "host-error-other")
        }) if (line.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return classification;
        return "other-line";
    }

    private sealed class WiringChild(Process process, string sentinel) : IAsyncDisposable
    {
        internal Process Process { get; } = process;
        internal string Sentinel { get; } = sentinel;
        internal Task<string> Error { get; } = process.StandardError.ReadToEndAsync();
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
            await Error; Process.Dispose();
        }
    }

    private sealed class WiringReadRelay : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task watching;
        private int reads, preflights, probes, mutations, invalidWire, hintAttempts, connections;
        internal int Connections => Volatile.Read(ref connections);
        internal int Reads => Volatile.Read(ref reads);
        internal int Preflights => Volatile.Read(ref preflights);
        internal int Probes => Volatile.Read(ref probes);
        internal int Mutations => Volatile.Read(ref mutations);
        internal int InvalidWire => Volatile.Read(ref invalidWire);
        internal int HintAttempts => Volatile.Read(ref hintAttempts);
        internal string Origin { get; }
        internal WiringReadRelay()
        {
            listener.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            watching = Watch();
        }
        private async Task Watch()
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    Interlocked.Increment(ref connections);
                    using var stream = client.GetStream();
                    using var headers = new MemoryStream();
                    var one = new byte[1];
                    while (headers.Length < 16384)
                    {
                        await stream.ReadExactlyAsync(one, stopping.Token); headers.WriteByte(one[0]);
                        if (headers.Length >= 4 && headers.GetBuffer().AsSpan((int)headers.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                    }
                    Assert.True(headers.Length < 16384);
                    var lines = Encoding.ASCII.GetString(headers.GetBuffer(), 0, (int)headers.Length).Split("\r\n");
                    var route = lines[0].Split(' ')[1];
                    if (route.Split('?')[0] == "/hubs/coordination/negotiate")
                    {
                        Interlocked.Increment(ref hintAttempts);
                        await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), stopping.Token);
                        continue;
                    }
                    var size = int.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    Assert.InRange(size, 1, NativeJoinHook.MaximumInputBytes);
                    var body = new byte[size]; await stream.ReadExactlyAsync(body, stopping.Token);
                    using var document = JsonDocument.Parse(body);
                    object data;
                    var allowed = true;
                    if (route.EndsWith("/session_open", StringComparison.Ordinal) && document.RootElement.TryGetProperty("nativePreflight", out _) && !document.RootElement.TryGetProperty("nativeJoin", out _))
                    { Interlocked.Increment(ref preflights); data = new { repositoryId = "repo" }; }
                    else if (route.EndsWith("/get_status", StringComparison.Ordinal))
                    { Interlocked.Increment(ref probes); data = new { sessionId = "s-native-hook", control = new { paused = false, requests = Array.Empty<object>() } }; }
                    else if (route.EndsWith("/chat_directory", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref reads);
                        if (document.RootElement.TryGetProperty("_cocernNative", out _) || ChatLocators.Text(document.RootElement, "repositoryId") != "repo")
                        { Interlocked.Increment(ref invalidWire); allowed = false; }
                        data = new { items = new[] { new { sessionId = "fixture-peer", label = "fixture peer" } } };
                    }
                    else { Interlocked.Increment(ref mutations); allowed = false; data = new { refused = true }; }
                    var response = JsonSerializer.SerializeToUtf8Bytes(new { ok = allowed, code = allowed ? "Ok" : "fixture_mutation_refused", dataJson = JsonSerializer.Serialize(data), version = 1 });
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + response.Length + "\r\n\r\n"), stopping.Token);
                    await stream.WriteAsync(response, stopping.Token);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stopping.Cancel(); listener.Stop(); await watching; stopping.Dispose(); }
    }

    private sealed class WiringRelayCounter : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task watching;
        private int count;
        private int hintAttempts;
        internal int Count => Volatile.Read(ref count);
        internal int HintAttempts => Volatile.Read(ref hintAttempts);
        internal int BusinessRequests => Count - HintAttempts;
        internal string Origin { get; }
        internal WiringRelayCounter()
        {
            listener.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            watching = Watch();
        }
        private async Task Watch()
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    Interlocked.Increment(ref count); // Count every relay connection, retaining no request data or headers.
                    using var stream = client.GetStream();
                    var firstLine = new byte[4096]; var length = 0;
                    while (length < firstLine.Length)
                    {
                        var read = await stream.ReadAsync(firstLine.AsMemory(length, 1), stopping.Token);
                        if (read == 0) break;
                        if (firstLine[length++] == '\n') break;
                    }
                    var parts = Encoding.ASCII.GetString(firstLine, 0, length).Split(' ');
                    if (parts.Length >= 2 && parts[1].Split('?')[0] == "/hubs/coordination/negotiate") Interlocked.Increment(ref hintAttempts);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stopping.Cancel(); listener.Stop(); await watching; stopping.Dispose(); }
    }

    private (NativeRegistration Registration, string Candidate) Registration()
    {
        Directory.CreateDirectory(Profile);
        var connection = Connection();
        var candidate = ClientSettings.StageNativeCandidate(connection.Folder, "codex", "\"fixture launcher.exe\"", clientProfile: Profile);
        Assert.NotNull(candidate);
        var registration = NativeRegistration.Read(candidate, "codex", connection.Folder, Profile);
        Assert.NotNull(registration);
        return (registration, candidate);
    }

    private static async Task<JsonElement> ReadFrame(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct);
        var count = BinaryPrimitives.ReadInt32BigEndian(prefix);
        Assert.InRange(count, 1, NativeRegistration.MaximumFrameBytes);
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct);
        using var json = JsonDocument.Parse(bytes); return json.RootElement.Clone();
    }

    private static async Task WriteFrame(Stream stream, JsonElement value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(value.GetRawText());
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }

    private LocalWakeConnection Connection() => LocalWakeConnection.Create(new AdapterOptions(Hub, "synthetic-setup-secret"), Token, Cache)!;

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CoCern.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Native setup fixtures require their isolated workspace.");
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Workspace(), "artifacts", "n4s")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its artifact folder.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

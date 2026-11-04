using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Net;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public sealed class NativeJoinHookTests
{
    private const string Parent = "c9231b18-d5e5-48bd-9a36-123f7af99a68";
    private const string Child = "428b95a1-5717-4397-a8ee-f3a0c0e65ae0";

    [Theory]
    [InlineData("codex", "startup")]
    [InlineData("codex", "resume")]
    [InlineData("codex", "compact")]
    [InlineData("claude", "startup")]
    [InlineData("claude", "resume")]
    [InlineData("claude", "compact")]
    public void Native_event_cases_keep_the_actual_session_id_across_same_id_events(string provider, string source)
    {
        var capture = Parse(provider, JsonSerializer.SerializeToElement(new
        {
            hook_event_name = "SessionStart", session_id = Parent, cwd = Path.GetFullPath("."), source,
            session_title = "Same display title"
        }));
        Assert.NotNull(capture);
        Assert.Equal(provider, capture.Provider);
        Assert.Equal(Parent, capture.NativeId);
        Assert.Equal(Path.GetFullPath("."), capture.Cwd);
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("clear")]
    [InlineData("resume")]
    public void Changed_id_with_same_title_is_a_distinct_participant_without_a_continuation_link(string source)
    {
        var first = Parse("claude", JsonSerializer.SerializeToElement(new { hook_event_name = "SessionStart", session_id = Parent, cwd = Path.GetFullPath("."), session_title = "same", source }));
        var second = Parse("claude", JsonSerializer.SerializeToElement(new { hook_event_name = "SessionStart", session_id = Child, cwd = Path.GetFullPath("."), session_title = "same", source }));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.NativeId, second.NativeId);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void Delegate_uses_the_parent_session_capture_and_never_the_agent_id(string provider)
    {
        var capture = Parse(provider, JsonSerializer.SerializeToElement(new
        {
            hook_event_name = "PreToolUse", session_id = Parent, cwd = Path.GetFullPath("."),
            agent_id = Child, tool_name = "mcp__cocern__send_message", tool_use_id = "invoke-1", tool_input = new { recipientSessionId = "peer", text = "hello" }
        }));
        Assert.NotNull(capture);
        Assert.Equal(Parent, capture.NativeId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"session_id\":\"invalid\",\"cwd\":\"C:/checkout\",\"hook_event_name\":\"SessionStart\"}")]
    [InlineData("{\"session_id\":\"c9231b18-d5e5-48bd-9a36-123f7af99a68\",\"cwd\":\"relative\",\"hook_event_name\":\"SessionStart\"}")]
    [InlineData("{\"agent_id\":\"428b95a1-5717-4397-a8ee-f3a0c0e65ae0\",\"cwd\":\"C:/checkout\",\"hook_event_name\":\"PreToolUse\"}")]
    [InlineData("{\"session_id\":\"c9231b18-d5e5-48bd-9a36-123f7af99a68\",\"session_id\":\"428b95a1-5717-4397-a8ee-f3a0c0e65ae0\",\"cwd\":\"C:/checkout\",\"hook_event_name\":\"SessionStart\"}")]
    public void Missing_malformed_duplicate_or_child_only_capture_is_refused(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(Parse("codex", document.RootElement));
    }

    private static NativeHookContext? Parse(string provider, JsonElement input) => NativeJoinHook.ParseContext(provider, input);

    [Fact]
    public void Whole_business_arguments_survive_while_only_root_self_envelope_is_removed()
    {
        using var original = JsonDocument.Parse("""{"sessionId":"wrong-own","sessionToken":"wrong-token","repositoryId":"wrong-repo","requestId":"wrong-request","_cocernNative":{"forged":true},"recipientSessionId":"peer","expectedVersion":17,"expectedTaskVersion":9,"ackMessageIds":["message"],"payload":{"sessionId":"business-target","requestId":"business-request","unknown":[true,null,1.25]}}""");
        var copy = NativeJoinHook.CloneArguments(original.RootElement);
        Assert.Equal("peer", copy.GetProperty("recipientSessionId").GetString());
        Assert.Equal(17, copy.GetProperty("expectedVersion").GetInt32());
        Assert.Equal(9, copy.GetProperty("expectedTaskVersion").GetInt32());
        Assert.Equal(original.RootElement.GetProperty("payload").GetRawText(), copy.GetProperty("payload").GetRawText());
        foreach (var field in new[] { "sessionId", "sessionToken", "repositoryId", "requestId", "_cocernNative" }) Assert.False(copy.TryGetProperty(field, out _));
        Assert.True(original.RootElement.TryGetProperty("sessionToken", out _));
    }

    [Theory]
    [InlineData("claude", false)]
    [InlineData("codex", true)]
    public void Provider_wrappers_preserve_business_json_and_only_Codex_candidate_has_allow(string provider, bool decision)
    {
        var input = JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", expectedVersion = 7, nested = new { text = "hello" } });
        var wrapped = NativeJoinHook.WrapOutput(provider, input).GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", wrapped.GetProperty("hookEventName").GetString());
        Assert.Equal(input.GetRawText(), wrapped.GetProperty("updatedInput").GetRawText());
        Assert.Equal(decision, wrapped.TryGetProperty("permissionDecision", out var permission));
        if (decision) Assert.Equal("allow", permission.GetString());
    }

    [Fact]
    public async Task Signed_call_uses_own_private_handle_and_preserves_targets_and_versions()
    {
        await using var fixture = new HookFixture();
        var input = JsonSerializer.SerializeToElement(new { recipientSessionId = "peer-target", text = "hello", expectedVersion = 11, payload = new { sessionId = "business-target" } });
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "call-1", input, default);
        Assert.True(stamped.TryGetProperty("_cocernNative", out var stamp));
        Assert.True(stamp.TryGetProperty("mac", out _));
        var text = stamped.GetRawText();
        Assert.DoesNotContain("synthetic-handle-token", text);
        Assert.DoesNotContain("bindingSecret", text);
        fixture.Requests = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, stamped, default);
        Assert.True(result.Ok, result.Code);
        var sent = fixture.Business.Single();
        Assert.Equal("s-native-hook", sent.GetProperty("sessionId").GetString());
        Assert.Equal("synthetic-handle-token", sent.GetProperty("sessionToken").GetString());
        Assert.Equal("peer-target", sent.GetProperty("recipientSessionId").GetString());
        Assert.Equal(11, sent.GetProperty("expectedVersion").GetInt32());
        Assert.Equal("business-target", sent.GetProperty("payload").GetProperty("sessionId").GetString());
        Assert.False(sent.TryGetProperty("_cocernNative", out _));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("other-tool")]
    [InlineData("other-provider")]
    public async Task Missing_or_conflicting_stamp_fails_before_every_relay_request(string change)
    {
        await using var fixture = new HookFixture();
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "call-2", JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "original" }), default);
        var args = JsonNode.Parse(stamped.GetRawText())!.AsObject();
        if (change == "missing") args.Remove("_cocernNative");
        if (change == "tampered") args["text"] = "changed";
        fixture.Requests = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, change == "other-provider" ? "codex" : "claude", change == "other-tool" ? RelayTools.Ack : RelayTools.SendMessage, JsonSerializer.SerializeToElement(args), default);
        Assert.False(result.Ok);
        Assert.Empty(fixture.Business);
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task Session_start_joins_without_emitting_a_model_handle()
    {
        await using var fixture = new HookFixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        var input = JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = Parent, cwd = fixture.Capture.Cwd });
        Assert.Equal(0, await NativeJoinHook.RunAsync("claude", new StringReader(input), output, error, fixture.Join, default));
        Assert.True(fixture.Requests > 0);
        Assert.Empty(output.ToString()); Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Actual_helper_replaces_the_whole_CoCern_object_and_leaves_other_tools_alone()
    {
        await using var fixture = new HookFixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        var input = JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", session_id = Parent, cwd = fixture.Capture.Cwd,
            tool_name = "mcp__cocern__send_message", tool_use_id = "helper-1", tool_input = new { recipientSessionId = "peer", text = "hello", expectedVersion = 5 } });
        Assert.Equal(0, await NativeJoinHook.RunAsync("claude", new StringReader(input), output, error, fixture.Join, default));
        Assert.False(string.IsNullOrEmpty(output.ToString()));
        using var wrapped = JsonDocument.Parse(output.ToString());
        var replacement = wrapped.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Equal("peer", replacement.GetProperty("recipientSessionId").GetString());
        Assert.Equal(5, replacement.GetProperty("expectedVersion").GetInt32());
        Assert.True(replacement.TryGetProperty("_cocernNative", out _));
        fixture.Requests = 0; output.GetStringBuilder().Clear();
        input = input.Replace("mcp__cocern__send_message", "mcp__other__send_message", StringComparison.Ordinal);
        Assert.Equal(0, await NativeJoinHook.RunAsync("claude", new StringReader(input), output, error, fixture.Join, default));
        Assert.Equal(0, fixture.Requests); Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task Malformed_helper_input_never_calls_the_hub(string input)
    {
        await using var fixture = new HookFixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        await NativeJoinHook.RunAsync("claude", new StringReader(input), output, error, fixture.Join, default);
        Assert.Equal(0, fixture.Requests); Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Owned_Stop_path_delivers_from_the_scoped_native_waiter()
    {
        await using var fixture = new HookFixture();
        var input = JsonSerializer.Serialize(new { hook_event_name = "Stop", session_id = Parent, cwd = fixture.Capture.Cwd });
        using var error = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        fixture.WakeReady = true;
        var result = await StopHook.RunNativeAsync(new StringReader(input), error, fixture.Join, timeout.Token, fixture.Handler, TimeSpan.FromSeconds(4));
        Assert.Equal(2, result);
        Assert.NotEmpty(error.ToString());
        Assert.NotEmpty(fixture.OwnHandles);
        Assert.All(fixture.OwnHandles, id => Assert.Equal("s-native-hook", id));
        Assert.DoesNotContain("synthetic-handle-token", error.ToString());
    }

    [Fact]
    public async Task Native_catalog_is_only_the_authorized_compact_surface_and_raw_schemas_stay_identical()
    {
        await using var fixture = new HookFixture();
        var raw = CoordinationTools.Create(fixture.Forwarder, admin: true).ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var native = CoordinationTools.CreateNative(fixture.Forwarder, fixture.Join, "claude", admin: true).ToArray();
        Assert.Equal(new[] { RelayTools.Ack, RelayTools.ChatDirectory, RelayTools.CheckInbox, RelayTools.SendMessage }.Order(), native.Select(tool => tool.ProtocolTool.Name).Order());
        foreach (var tool in native)
            foreach (var field in new[] { "sessionId", "sessionToken", "repositoryId", "requestId", "_cocernNative" })
                Assert.False(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty(field, out _));
        var send = native.Single(tool => tool.ProtocolTool.Name == RelayTools.SendMessage).ProtocolTool.InputSchema;
        Assert.True(send.GetProperty("properties").TryGetProperty("recipientSessionId", out _));
        Assert.Equal(raw, CoordinationTools.Create(fixture.Forwarder, admin: true).ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText()));
    }

    [Fact]
    public async Task Native_reply_projects_only_the_own_envelope_and_preserves_peer_business_references()
    {
        await using var fixture = new HookFixture();
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "reply-1", JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "hello" }), default);
        fixture.OwnReply = true;
        var reply = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, stamped, default);
        Assert.True(reply.Ok, reply.Code);
        Assert.NotNull(reply.Data);
        foreach (var field in new[] { "sessionId", "sessionToken", "_cocernNative", "nativeJoin" }) Assert.False(reply.Data.Value.TryGetProperty(field, out _));
        Assert.Equal("peer", reply.Data.Value.GetProperty("recipientSessionId").GetString());
        Assert.Equal("peer-nested", reply.Data.Value.GetProperty("peer").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Expired_stamp_makes_zero_requests_and_does_not_create_a_binding()
    {
        await using var fixture = new HookFixture();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "expiry-1", JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "hello" }), default, clock);
        fixture.Requests = 0;
        var reply = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, stamped, default, new FixedClock(clock.GetUtcNow().AddMinutes(3)));
        Assert.False(reply.Ok); Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
        var forged = JsonNode.Parse(stamped.GetRawText())!.AsObject();
        forged["_cocernNative"]!["nativeId"] = Child;
        reply = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, JsonSerializer.SerializeToElement(forged), default, clock);
        Assert.False(reply.Ok); Assert.Equal(0, fixture.Requests);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Join.PrivateFolder, NodeState.SessionsFolder, "native"), "*.json"));
    }

    [Fact]
    public async Task Duplicate_transport_reuses_request_identity_and_changed_business_arguments_are_refused()
    {
        await using var fixture = new HookFixture();
        var input = JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "hello", expectedVersion = 7 });
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "stable-1", input, default);
        Assert.True((await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, stamped, default)).Ok);
        var reordered = JsonNode.Parse(stamped.GetRawText())!.AsObject();
        var reorderedInput = JsonSerializer.SerializeToElement(new JsonObject { ["expectedVersion"] = 7.0, ["text"] = "hello", ["recipientSessionId"] = "peer", ["_cocernNative"] = reordered["_cocernNative"]!.DeepClone() });
        Assert.True((await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, reorderedInput, default)).Ok);
        Assert.Equal(fixture.Business[0].GetProperty("requestId").GetString(), fixture.Business[1].GetProperty("requestId").GetString());
        Assert.Equal(fixture.Business[0].GetRawText(), fixture.Business[1].GetRawText());
        var changed = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "stable-1", JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "changed", expectedVersion = 7 }), default);
        Assert.False((await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, changed, default)).Ok);
        Assert.Equal(2, fixture.Business.Count);
    }

    [Fact]
    public async Task A_full_durable_mutation_ledger_does_not_disable_authenticated_reads()
    {
        await using var fixture = new HookFixture();
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "read-with-full-mutation-history", JsonSerializer.SerializeToElement(new { }), default);
        await using (var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true))
        {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(binding.StatePath)!, "calls-" + binding.BindingKey)).FullName;
            for (var index = 0; index < 4096; index++) NodeState.WritePrivately(Path.Combine(directory, index.ToString("D8") + ".json"), System.Text.Encoding.ASCII.GetBytes(new string('a', 64)));
        }
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, default);
        Assert.True(result.Ok, result.Code);
        Assert.Single(fixture.Business);
    }

    [Fact]
    public async Task More_than_4096_expiring_read_admissions_keep_working_without_permanent_history()
    {
        await using var fixture = new HookFixture();
        var empty = JsonSerializer.SerializeToElement(new { });
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using (var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true))
        {
            for (var index = 0; index < 4100; index++)
            {
                now += 180;
                Assert.Null(NativeJoinHook.BindReadInvocation(binding, RelayTools.ChatDirectory, "read-" + index, empty, now + 120, now));
            }
            Assert.Equal("native_invocation_conflict", NativeJoinHook.BindReadInvocation(binding, RelayTools.CheckInbox, "read-4099", empty, now + 120, now));
            Assert.Equal("native_stamp_expired", NativeJoinHook.BindReadInvocation(binding, RelayTools.ChatDirectory, "expired", empty, now, now));
            var cache = Path.ChangeExtension(binding.StatePath, ".reads");
            Assert.True(NodeState.IsPrivateFile(cache));
            using var records = JsonDocument.Parse(File.ReadAllBytes(cache));
            Assert.Single(records.RootElement.EnumerateArray());
            Assert.DoesNotContain("synthetic-handle-token", File.ReadAllText(cache));
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(binding.StatePath)!, "calls-" + binding.BindingKey)));
        }
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(now + 180));
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "read-after-4100", empty, default, clock);
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, default, clock);
        Assert.True(result.Ok, result.Code);
        Assert.Single(fixture.Business);
        var changed = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "read-after-4100", JsonSerializer.SerializeToElement(new { limit = 2 }), default, clock);
        fixture.Requests = 0;
        Assert.False((await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, changed, default, clock)).Ok);
        Assert.Equal(0, fixture.Requests);
        var later = new FixedClock(clock.GetUtcNow().AddMinutes(3));
        var refreshed = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "read-after-4100", JsonSerializer.SerializeToElement(new { limit = 2 }), default, later);
        Assert.True((await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, refreshed, default, later)).Ok);
        Assert.Equal(2, fixture.Business.Count);
    }

    [Fact]
    public void Native_integer_canonicalization_preserves_business_versions_without_rounding_or_exponent_spelling()
    {
        using var input = JsonDocument.Parse("""{"expectedVersion":1000,"expectedTaskVersion":9007199254740993,"ratio":0.0100}""");
        using var alternate = JsonDocument.Parse("""{"ratio":1e-2,"expectedTaskVersion":9007199254740993,"expectedVersion":1e3}""");
        var canonical = NativeJoinHook.Canonical(input.RootElement);
        Assert.Equal(canonical, NativeJoinHook.Canonical(alternate.RootElement));
        using var parsed = JsonDocument.Parse(canonical);
        Assert.True(parsed.RootElement.GetProperty("expectedVersion").TryGetInt64(out var version));
        Assert.Equal(1000, version);
        Assert.Equal(9007199254740993L, parsed.RootElement.GetProperty("expectedTaskVersion").GetInt64());
        Assert.Equal(0.01m, parsed.RootElement.GetProperty("ratio").GetDecimal());
    }

    [Fact]
    public async Task Actual_hub_replays_equivalent_signed_arguments_without_a_second_mutation()
    {
        await using var fixture = new HookFixture(RelayHostFixture.Token);
        await using var host = new RelayHostFixture(databasePath: Path.Combine(fixture.Root, "relay.db"), remoteAddress: IPAddress.Loopback, settings: new Dictionary<string, string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = fixture.Capture.Cwd,
            ["Relay:DocCache:Repositories:0:Fetch"] = "false"
        });
        using var http = host.CreateClient();
        await using var join = new NativeJoin(fixture.Folder, fixture.Options, new RelayForwarder(http, fixture.Options));
        var peer = await join.EnsureAsync(fixture.Capture with { NativeId = Child }, default);
        var input = JsonSerializer.SerializeToElement(new { recipientSessionId = peer.SessionId, text = "one native message", wake = false });
        var stamped = await NativeJoinHook.StampAsync(join, fixture.Capture, RelayTools.SendMessage, "actual-hub-retry", input, default);
        var first = await NativeJoinHook.InvokeAsync(join, "claude", RelayTools.SendMessage, stamped, default);
        Assert.True(first.Ok, first.Code);
        var original = JsonNode.Parse(stamped.GetRawText())!.AsObject();
        var changedOrder = JsonSerializer.SerializeToElement(new JsonObject { ["wake"] = false, ["text"] = "one native message", ["recipientSessionId"] = peer.SessionId, ["_cocernNative"] = original["_cocernNative"]!.DeepClone() });
        var replay = await NativeJoinHook.InvokeAsync(join, "claude", RelayTools.SendMessage, changedOrder, default);
        Assert.True(replay.Ok, replay.Code);
        Assert.Equal(first.Data?.GetRawText(), replay.Data?.GetRawText());
        Assert.Equal(first.Version, replay.Version);
    }

    private sealed class HookFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Workspace(), "artifacts", "n4", Guid.NewGuid().ToString("N")[..12]);
        private readonly HttpClient http;
        internal NativeJoin Join { get; }
        internal RelayForwarder Forwarder { get; }
        internal string Root => root;
        internal string Folder { get; }
        internal AdapterOptions Options { get; }
        internal HookHandler Handler { get; }
        internal NativeHookContext Capture { get; }
        internal int Requests;
        internal bool WakeReady;
        internal bool OwnReply;
        internal List<string> OwnHandles { get; } = [];
        internal List<JsonElement> Business { get; } = [];

        internal HookFixture(string connectionToken = "synthetic-connection")
        {
            var checkout = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
            var git = new ProcessStartInfo("git") { WorkingDirectory = checkout, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            git.ArgumentList.Add("init"); git.ArgumentList.Add("-q");
            foreach (var key in git.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) git.Environment.Remove(key);
            using (var process = Process.Start(git)!) { var error = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.True(process.ExitCode == 0, error); }
            var options = Options = new AdapterOptions("http://127.0.0.1:4321", connectionToken);
            var token = Path.Combine(root, "fixture-token");
            NodeState.WritePrivately(token, System.Text.Encoding.UTF8.GetBytes(options.Token));
            var folder = Folder = LocalWakeConnection.Create(options, token, Path.Combine(root, "cache"))!.Folder;
            Handler = new HookHandler(async (request, ct) =>
            {
                Requests++;
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var args = doc.RootElement;
                if (ChatLocators.Text(args, "sessionId") is { } own) { OwnHandles.Add(own); Assert.Equal("synthetic-handle-token", ChatLocators.Text(args, "sessionToken")); }
                object reply;
                if (args.TryGetProperty("nativePreflight", out _)) reply = new { repositoryId = "repo" };
                else if (args.TryGetProperty("nativeJoin", out _)) reply = new { sessionId = "s-native-hook", sessionToken = "synthetic-handle-token" };
                else if (WakeReady && args.TryGetProperty("headersOnly", out _)) reply = new { items = new[] { new { messageId = "m-native", repositoryId = "repo" } }, total = 1, nextCursor = (int?)null, truncated = false };
                else if (request.RequestUri!.AbsolutePath.EndsWith("/get_status", StringComparison.Ordinal)) reply = new { sessionId = "s-native-hook", control = new { paused = false, requests = Array.Empty<object>() } };
                else { Business.Add(args.Clone()); reply = OwnReply ? new { sessionId = "s-native-hook", sessionToken = "synthetic-handle-token", recipientSessionId = "peer", peer = new { sessionId = "peer-nested" }, _cocernNative = new { hidden = true }, nativeJoin = new { hidden = true } } : (object)new { sent = true }; }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = JsonSerializer.Serialize(reply), version = 1 }), System.Text.Encoding.UTF8, "application/json") };
            });
            http = new HttpClient(Handler, disposeHandler: false);
            Forwarder = new RelayForwarder(http, options);
            Join = new NativeJoin(folder, options, Forwarder);
            Capture = new("claude", Parent, checkout);
        }

        public async ValueTask DisposeAsync()
        {
            await Join.DisposeAsync();
            http.Dispose();
            Handler.Dispose();
            var parent = Path.GetFullPath(Path.Combine(Workspace(), "artifacts", "n4")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its artifact folder.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static string Workspace()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "CoCern.slnx"))) return directory.FullName;
            throw new InvalidOperationException("Native hook fixtures require their isolated workspace.");
        }
    }

    private sealed class HookHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

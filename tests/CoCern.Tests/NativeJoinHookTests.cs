using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Net;
using System.Text;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

public sealed class NativeJoinHookTests(ITestOutputHelper output)
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
        await fixture.Join.EnsureAsync(fixture.Capture, default);
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
        await fixture.Join.EnsureAsync(fixture.Capture, default);
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
        await fixture.Join.EnsureAsync(fixture.Capture, default);
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

    private static string StopInput(HookFixture fixture) => JsonSerializer.Serialize(new { hook_event_name = "Stop", session_id = Parent, cwd = fixture.Capture.Cwd });

    /// <summary>A real process that stands in for a running waiter: pid files are checked against the live process table.</summary>
    private static Process StartStandIn() =>
        Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;

    /// <summary>This process runs under client 9001; the waiter runs under <paramref name="waiterClient"/>.</summary>
    private static Func<int, ProcessFacts?> Clients(int waiterId, int waiterClient)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return id => id switch
        {
            9001 or 9002 => new ProcessFacts(id, 1, "claude", t0),
            _ when id == Environment.ProcessId => new ProcessFacts(id, 9001, "dotnet", t0.AddHours(1)),
            _ when id == waiterId => new ProcessFacts(id, waiterClient, "CoCern.Adapter", t0.AddHours(1)),
            _ => null
        };
    }

    [Fact]
    public async Task A_native_hook_from_a_new_client_arms_once_a_waiter_left_by_another_client_ends()
    {
        await using var fixture = new HookFixture();
        fixture.WakeReady = true;
        using var oldWaiter = StartStandIn();
        try
        {
            ClaudeWaiter.WritePid(fixture.Folder, Parent, oldWaiter.Id, oldWaiter.StartTime.ToUniversalTime());
            using var error = new StringWriter();
            var hook = StopHook.RunNativeAsync(new StringReader(StopInput(fixture)), error, fixture.Join, default, fixture.Handler, TimeSpan.FromSeconds(10), read: Clients(oldWaiter.Id, 9002), grace: TimeSpan.FromSeconds(30));
            await Task.Delay(300);
            Assert.False(hook.IsCompleted);
            oldWaiter.Kill(entireProcessTree: true);
            await oldWaiter.WaitForExitAsync();
            Assert.Equal(2, await hook.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Contains("m-native", error.ToString());
        }
        finally { if (!oldWaiter.HasExited) oldWaiter.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task A_native_hook_leaves_a_live_waiter_of_its_own_client_alone_and_ends_at_once()
    {
        await using var fixture = new HookFixture();
        fixture.WakeReady = true;
        using var ownWaiter = StartStandIn();
        try
        {
            ClaudeWaiter.WritePid(fixture.Folder, Parent, ownWaiter.Id, ownWaiter.StartTime.ToUniversalTime());
            using var error = new StringWriter();
            Assert.Equal(0, await StopHook.RunNativeAsync(new StringReader(StopInput(fixture)), error, fixture.Join, default, fixture.Handler, TimeSpan.FromSeconds(10), read: Clients(ownWaiter.Id, 9001), grace: TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("", error.ToString());
            Assert.True(File.Exists(Path.Combine(fixture.Folder, "waiters", Parent + ".pid")));
        }
        finally { if (!ownWaiter.HasExited) ownWaiter.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task A_native_waiter_follows_its_chat_to_a_replacement_session_and_never_to_a_legacy_handle()
    {
        await using var fixture = new HookFixture();
        fixture.WakeReady = true;
        // Mail waits only in the replacement's inbox, so a waiter left on the first handle never wakes.
        fixture.InboxOf = session => session == "s-native-new" ? ["m-new"] : [];
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var location = new ChatLocation("claude", Parent);
        NodeState.WriteSessionHandle(fixture.Folder, new("s-neighbor", "neighbor", "repo", "Same", DateTimeOffset.UtcNow, location, location));
        var deliveries = new List<string>();
        var waiting = ClaudeWaiter.RunNativeAsync(fixture.Join, fixture.Capture, deliveries.Add, TimeSpan.FromSeconds(20), default, fixture.Handler, hubHandler: () => new HookHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))), interval: TimeSpan.FromMilliseconds(50));
        var pid = Path.Combine(fixture.Folder, "waiters", Parent + ".pid");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(pid) && !waiting.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(File.Exists(pid));
        // A later Stop hook of this client finds this waiter alive and leaves it running, so only the waiter itself can notice the replacement.
        await using (var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default))
        {
            Assert.True(binding.AdvanceExpired(binding.FrozenOpenArguments));
            var replacement = new SessionHandle("s-native-new", "synthetic-handle-token", "repo", "fixture", DateTimeOffset.UtcNow, location, location) { NativeBindingKey = binding.OwnershipKey };
            NodeState.WriteSessionHandle(fixture.Folder, replacement);
            Assert.True(binding.Complete(replacement));
        }
        Assert.Equal("queued", await waiting);
        Assert.Single(deliveries);
        Assert.Contains("s-native-new", fixture.OwnHandles);
        Assert.DoesNotContain("s-neighbor", fixture.OwnHandles);
    }

    [Fact]
    public async Task Two_native_wakes_do_not_repeat_a_message()
    {
        await using var fixture = new HookFixture();
        fixture.WakeReady = true;
        fixture.InboxOf = _ => ["m1"];
        using var error = new StringWriter();
        Assert.Equal(2, await StopHook.RunNativeAsync(new StringReader(StopInput(fixture)), error, fixture.Join, default, fixture.Handler, TimeSpan.FromSeconds(4)));
        Assert.Contains("- m1: UNTRUSTED", error.ToString());
        Assert.DoesNotContain("Respect existing user authority", error.ToString());
        fixture.InboxOf = _ => ["m1", "m2"];
        var deliveries = new List<string>();
        // A minute later, so the rate limit does not hold the second wake back.
        Assert.Equal("queued", await ClaudeWaiter.RunNativeAsync(fixture.Join, fixture.Capture, deliveries.Add, TimeSpan.FromSeconds(4), default, fixture.Handler, printInbox: true, clock: new AdvancingClock(DateTimeOffset.UtcNow.AddMinutes(1))));
        var second = Assert.Single(deliveries);
        Assert.Contains("- m2: UNTRUSTED", second);
        Assert.DoesNotContain("- m1:", second);
        Assert.Contains("1 earlier unacknowledged message is not repeated", second);
        // The ledger and the seen report used the native session, and never a credential in the text.
        Assert.All(fixture.OwnHandles, id => Assert.Equal("s-native-hook", id));
        Assert.DoesNotContain("synthetic-handle-token", second);
    }

    [Theory]
    [InlineData("SessionStart")]
    [InlineData("PreToolUse")]
    public async Task Wiring_configured_helper_answers_the_registration_before_its_existing_event_behavior(string eventName)
    {
        await using var fixture = new HookFixture();
        if (eventName == "PreToolUse") await fixture.Join.EnsureAsync(fixture.Capture, default);
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Folder, "owned-profile")).FullName;
        var candidate = ClientSettings.StageNativeCandidate(fixture.Folder, "claude", "fixture-helper", clientProfile: profile)!;
        var registration = NativeRegistration.Read(candidate, "claude", fixture.Folder, profile)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = registration.AcceptAsync(timeout.Token, TimeSpan.FromMilliseconds(750));
        var input = JsonSerializer.Serialize(new { hook_event_name = eventName, session_id = Parent, cwd = fixture.Capture.Cwd,
            tool_name = "mcp__cocern__send_message", tool_use_id = "registered-1", tool_input = new { recipientSessionId = "peer", text = "hello", expectedVersion = 5 } });
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await NativeJoinHook.RunRegisteredAsync("claude", new StringReader(input), output, error, fixture.Join, registration, timeout.Token));
        Assert.NotNull(await accepting);
        Assert.Empty(error.ToString());
        if (eventName == "SessionStart") Assert.Empty(output.ToString());
        else
        {
            using var wrapped = JsonDocument.Parse(output.ToString());
            var updated = wrapped.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
            Assert.Equal("peer", updated.GetProperty("recipientSessionId").GetString());
            Assert.Equal(5, updated.GetProperty("expectedVersion").GetInt32());
            Assert.True(updated.TryGetProperty("_cocernNative", out _));
            Assert.False(wrapped.RootElement.GetProperty("hookSpecificOutput").TryGetProperty("permissionDecision", out _));
        }
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("changed-candidate")]
    public async Task Wiring_helper_refuses_a_conflicting_or_changed_registration_before_relay(string conflict)
    {
        await using var fixture = new HookFixture();
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Folder, "owned-profile")).FullName;
        var candidate = ClientSettings.StageNativeCandidate(fixture.Folder, "claude", "fixture-helper", clientProfile: profile)!;
        var registration = NativeRegistration.Read(candidate, "claude", fixture.Folder, profile)!;
        if (conflict == "changed-candidate")
        {
            var changed = JsonNode.Parse(File.ReadAllText(candidate))!.AsObject(); changed["credentialScope"] = new string('0', 64);
            NodeState.WritePrivately(candidate, JsonSerializer.SerializeToUtf8Bytes(changed));
        }
        var input = JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", session_id = Parent, cwd = fixture.Capture.Cwd,
            tool_name = "mcp__cocern__send_message", tool_use_id = "refused-1", tool_input = new { recipientSessionId = "peer", text = "hello" } });
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await NativeJoinHook.RunRegisteredAsync(conflict == "provider" ? "codex" : "claude", new StringReader(input), output, error, fixture.Join, registration, default));
        Assert.Equal(0, fixture.Requests); Assert.Empty(output.ToString());
        Assert.Contains("could not be verified", error.ToString(), StringComparison.Ordinal);
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
        await fixture.Join.EnsureAsync(fixture.Capture, default);
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
        await fixture.Join.EnsureAsync(fixture.Capture, default);
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
        await fixture.Join.EnsureAsync(fixture.Capture, default);
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
        await fixture.Join.EnsureAsync(fixture.Capture, default);
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
                try { Assert.Null(await NativeJoinHook.BindReadInvocationAsync(binding, RelayTools.ChatDirectory, "read-" + index, empty, now + 120, now)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Fixed metadata only; preserve the original exception and every stress assertion.
                    // Neither paths, invocation payloads, credentials nor cache contents are reported.
                    try
                    {
                        var destination = Path.ChangeExtension(binding.StatePath, ".reads");
                        var exists = File.Exists(destination);
                        var attributes = exists ? (int)File.GetAttributes(destination) : -1;
                        var privateFile = exists && NodeState.IsPrivateFile(destination);
                        var temporaryCount = Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + ".*.tmp").Take(8).Count();
                        output.WriteLine($"read-admission-io iteration={index} hresult={exception.HResult:X8} destinationExists={exists} attributesCode={attributes} private={privateFile} temporaryCount={temporaryCount}");
                    }
                    catch (Exception diagnostic) when (diagnostic is IOException or UnauthorizedAccessException)
                    { output.WriteLine($"read-admission-io iteration={index} hresult={exception.HResult:X8} diagnosticHresult={diagnostic.HResult:X8}"); }
                    throw;
                }
            }
            Assert.Equal("native_invocation_conflict", await NativeJoinHook.BindReadInvocationAsync(binding, RelayTools.CheckInbox, "read-4099", empty, now + 120, now));
            Assert.Equal("native_stamp_expired", await NativeJoinHook.BindReadInvocationAsync(binding, RelayTools.ChatDirectory, "expired", empty, now, now));
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
        await join.EnsureAsync(fixture.Capture, default);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixRound1_forged_scope_never_resolves_checkout_before_authentication(bool unc)
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var valid = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "forged-path", JsonSerializer.SerializeToElement(new { }), default);
        var forged = JsonNode.Parse(valid.GetRawText())!.AsObject();
        if (unc) forged["_cocernNative"]!["cwd"] = @"\\untrusted.invalid\share\checkout";
        forged["_cocernNative"]!["mac"] = new string('0', 64);
        var probes = 0;
        fixture.Requests = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(forged), default,
            resolveCheckout: (_, _) => { probes++; throw new IOException("Instrumented checkout access refused."); });
        Assert.False(result.Ok); Assert.Equal("native_stamp_invalid", result.Code);
        Assert.Equal(0, probes); Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixRound1_read_and_mutation_share_invocation_identity(bool mutationFirst)
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var read = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "cross-domain", JsonSerializer.SerializeToElement(new { }), default);
        var mutation = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "cross-domain", JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "once" }), default);
        var first = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", mutationFirst ? RelayTools.SendMessage : RelayTools.ChatDirectory, mutationFirst ? mutation : read, default);
        Assert.True(first.Ok, first.Code);
        fixture.Requests = 0;
        var second = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", mutationFirst ? RelayTools.ChatDirectory : RelayTools.SendMessage, mutationFirst ? read : mutation, default);
        Assert.False(second.Ok);
        Assert.Equal("native_invocation_conflict", second.Code);
        Assert.Equal(0, fixture.Requests);
        Assert.Single(fixture.Business);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixRound1_PreToolUse_without_dispatch_never_contacts_relay(bool established)
    {
        await using var fixture = new HookFixture();
        if (established) await fixture.Join.EnsureAsync(fixture.Capture, default);
        fixture.Requests = 0;
        var text = JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", session_id = Parent, cwd = fixture.Capture.Cwd,
            tool_name = "mcp__cocern__send_message", tool_use_id = "cancelled-ask", tool_input = new { recipientSessionId = "peer", text = "cancelled" } });
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await NativeJoinHook.RunAsync("claude", new StringReader(text), output, error, fixture.Join, default);
        Assert.Equal(0, fixture.Requests);
        Assert.Empty(fixture.Business);
        Assert.Equal(established ? 0 : 2, exit);
        if (established) Assert.Contains("updatedInput", output.ToString());
        else { Assert.Empty(output.ToString()); Assert.Contains("could not be verified", error.ToString()); }
    }

    [Fact]
    public async Task FixRound1_exact_uncertain_legacy_mutation_retry_preserves_original_wire_and_fence()
    {
        await using var fixture = new HookFixture();
        var handle = await fixture.Join.EnsureAsync(fixture.Capture, default);
        using var original = JsonDocument.Parse("""{"text":"legacy once","recipientSessionId":"peer","expectedVersion":7}""");
        const string invocation = "uncertain-before-canonicalization";
        string fencePath; string legacy;
        await using (var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true))
        {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(binding.StatePath)!, "calls-" + binding.BindingKey)).FullName;
            fencePath = Path.Combine(directory, NativeCheckout.Hash(binding.BindingKey + ":" + invocation) + ".json");
            legacy = NativeCheckout.Hash(JsonSerializer.Serialize(new[] { binding.Generation, handle.SessionId, RelayTools.SendMessage, original.RootElement.GetRawText() }));
            NodeState.WritePrivately(fencePath, Encoding.ASCII.GetBytes(legacy));
        }
        var exact = await fixture.Join.CallAsync(fixture.Capture, invocation, RelayTools.SendMessage, original.RootElement, default);
        Assert.True(exact.Ok, exact.Code);
        Assert.Single(fixture.Business);
        Assert.StartsWith("{\"text\":\"legacy once\",\"recipientSessionId\":\"peer\",\"expectedVersion\":7,", fixture.Business[0].GetRawText());
        Assert.Equal(legacy, File.ReadAllText(fencePath));
        fixture.Requests = 0;
        var changed = await fixture.Join.CallAsync(fixture.Capture, invocation, RelayTools.SendMessage,
            JsonSerializer.SerializeToElement(new { text = "changed", recipientSessionId = "peer", expectedVersion = 7 }), default);
        Assert.False(changed.Ok); Assert.Equal("native_invocation_conflict", changed.Code);
        Assert.Single(fixture.Business);
        Assert.Equal(legacy, File.ReadAllText(fencePath));
    }

    [Fact]
    public async Task FixRound1_actual_hub_replays_a_signed_exact_legacy_retry_without_second_mutation()
    {
        await using var fixture = new HookFixture(RelayHostFixture.Token);
        await using var host = new RelayHostFixture(databasePath: Path.Combine(fixture.Root, "legacy-relay.db"), remoteAddress: IPAddress.Loopback, settings: new Dictionary<string, string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = fixture.Capture.Cwd,
            ["Relay:DocCache:Repositories:0:Fetch"] = "false"
        });
        using var http = host.CreateClient();
        var forwarder = new RelayForwarder(http, fixture.Options);
        await using var join = new NativeJoin(fixture.Folder, fixture.Options, forwarder);
        var sender = await join.EnsureAsync(fixture.Capture, default);
        var peer = await join.EnsureAsync(fixture.Capture with { NativeId = Child }, default);
        var original = JsonSerializer.SerializeToElement(new { text = "one legacy mutation", recipientSessionId = peer.SessionId, wake = false });
        const string invocation = "legacy-actual-hub";
        string path; string legacy; string request;
        await using (var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true))
        {
            request = NativeCheckout.Hash(binding.BindingKey + ":" + invocation);
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(binding.StatePath)!, "calls-" + binding.BindingKey)).FullName;
            path = Path.Combine(directory, request + ".json");
            legacy = NativeCheckout.Hash(JsonSerializer.Serialize(new[] { binding.Generation, sender.SessionId, RelayTools.SendMessage, original.GetRawText() }));
            NodeState.WritePrivately(path, Encoding.ASCII.GetBytes(legacy));
        }
        var firstPayload = JsonNode.Parse(original.GetRawText())!.AsObject();
        firstPayload["repositoryId"] = sender.RepositoryId; firstPayload["sessionId"] = sender.SessionId;
        firstPayload["sessionToken"] = sender.SessionToken; firstPayload["requestId"] = request;
        var first = await forwarder.ForwardAsync(RelayTools.SendMessage, RelayForwarder.WithOrigin(JsonSerializer.SerializeToElement(firstPayload), background: false), default);
        Assert.True(first.Ok, first.Code);
        var signed = await NativeJoinHook.StampAsync(join, fixture.Capture, RelayTools.SendMessage, invocation, original, default);
        var replay = await NativeJoinHook.InvokeAsync(join, "claude", RelayTools.SendMessage, signed, default);
        Assert.True(replay.Ok, replay.Code);
        Assert.Equal(first.Data?.GetRawText(), replay.Data?.GetRawText()); Assert.Equal(first.Version, replay.Version);
        Assert.Equal(legacy, File.ReadAllText(path));
        var changed = await NativeJoinHook.StampAsync(join, fixture.Capture, RelayTools.SendMessage, invocation,
            JsonSerializer.SerializeToElement(new { text = "changed", recipientSessionId = peer.SessionId, wake = false }), default);
        var refused = await NativeJoinHook.InvokeAsync(join, "claude", RelayTools.SendMessage, changed, default);
        Assert.False(refused.Ok); Assert.Equal("native_invocation_conflict", refused.Code);
        Assert.Equal(legacy, File.ReadAllText(path));
    }

    [Fact]
    public async Task FixRound1_signed_read_cache_atomic_write_failure_refuses_before_relay()
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "cache-write-refusal", JsonSerializer.SerializeToElement(new { }), default);
        string cache; string mutations;
        await using (var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true))
        {
            cache = Path.ChangeExtension(binding.StatePath, ".reads");
            mutations = Path.Combine(Path.GetDirectoryName(binding.StatePath)!, "calls-" + binding.BindingKey);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "sentinel"), "unchanged");
        }
        fixture.Requests = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, default);
        Assert.False(result.Ok); Assert.Equal("native_stamp_invalid", result.Code);
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
        Assert.True(Directory.Exists(cache)); Assert.Equal("unchanged", File.ReadAllText(Path.Combine(cache, "sentinel")));
        Assert.False(Directory.Exists(mutations));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(cache)!, Path.GetFileName(cache) + ".*.tmp"));
    }

    [Fact]
    public async Task FixRound1_stamp_expiring_behind_the_binding_lease_is_refused_before_relay()
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var clock = new AdvancingClock(DateTimeOffset.UtcNow);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "lease-expiry", JsonSerializer.SerializeToElement(new { }), default, clock);
        var held = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true);
        fixture.Requests = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<AdapterReply> pending;
        try
        {
            pending = NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, timeout.Token, clock);
            clock.Now = clock.Now.AddMinutes(3);
        }
        finally { await held.DisposeAsync(); }
        var result = await pending;
        Assert.False(result.Ok); Assert.Equal("native_stamp_expired", result.Code);
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task ReadRecovery_transient_native_denial_retries_identical_bytes_once(int nativeError)
    {
        await using var fixture = new HookFixture();
        await RecoveryCache(fixture);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "transient-read", JsonSerializer.SerializeToElement(new { }), default);
        fixture.Requests = 0;
        var attempts = 0; var waits = 0; byte[]? frozen = null;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, default,
            writeReadCache: (path, bytes, _) =>
            {
                attempts++; frozen ??= bytes.ToArray(); Assert.True(bytes.AsSpan().SequenceEqual(frozen));
                if (attempts == 1) throw NativeReadError(nativeError);
                NodeState.WritePrivately(path, bytes); return Task.CompletedTask;
            }, retryDelay: (delay, _) => { Assert.Equal(TimeSpan.FromMilliseconds(25), delay); waits++; return Task.CompletedTask; });
        Assert.True(result.Ok, result.Code); Assert.Equal(2, attempts); Assert.Equal(1, waits);
        Assert.Single(fixture.Business);
    }

    [Fact]
    public async Task ReadRecovery_persistent_native_denial_refuses_with_exact_attempt_bound_and_zero_relay()
    {
        await using var fixture = new HookFixture();
        var cache = await RecoveryCache(fixture);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "persistent-read", JsonSerializer.SerializeToElement(new { }), default);
        fixture.Requests = 0;
        var attempts = 0; var waits = 0; byte[]? frozen = null;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, default,
            writeReadCache: (_, bytes, _) => { attempts++; frozen ??= bytes.ToArray(); Assert.True(bytes.AsSpan().SequenceEqual(frozen)); throw NativeReadError(32); },
            retryDelay: (delay, _) => { Assert.Equal(TimeSpan.FromMilliseconds(25), delay); waits++; return Task.CompletedTask; });
        Assert.False(result.Ok); Assert.Equal("native_stamp_invalid", result.Code);
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business); Assert.Equal("[]", File.ReadAllText(cache));
        Assert.Equal(4, attempts); Assert.Equal(3, waits);
    }

    [Fact]
    public async Task ReadRecovery_cancellation_during_recovery_propagates_without_relay()
    {
        await using var fixture = new HookFixture();
        var cache = await RecoveryCache(fixture);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "cancelled-read", JsonSerializer.SerializeToElement(new { }), default);
        fixture.Requests = 0;
        using var cancel = new CancellationTokenSource();
        var attempts = 0; var waits = 0;
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, cancel.Token,
            writeReadCache: (_, _, _) => { attempts++; throw NativeReadError(32); },
            retryDelay: (_, ct) => { waits++; cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        Assert.Equal(cancel.Token, cancelled.CancellationToken);
        Assert.Equal(1, attempts); Assert.Equal(1, waits);
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business); Assert.Equal("[]", File.ReadAllText(cache));
    }

    [Fact]
    public async Task ReadRecovery_expiry_during_recovery_refuses_before_relay()
    {
        await using var fixture = new HookFixture();
        await RecoveryCache(fixture);
        var clock = new AdvancingClock(DateTimeOffset.UtcNow);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "expired-read", JsonSerializer.SerializeToElement(new { }), default, clock);
        fixture.Requests = 0;
        var attempts = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, default, clock,
            writeReadCache: (path, bytes, _) => { attempts++; if (attempts == 1) throw NativeReadError(32); NodeState.WritePrivately(path, bytes); return Task.CompletedTask; },
            retryDelay: (_, _) => { clock.Now = clock.Now.AddMinutes(3); return Task.CompletedTask; });
        Assert.False(result.Ok); Assert.Equal("native_stamp_expired", result.Code);
        Assert.Equal(2, attempts); Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("changed")]
    [InlineData("corrupt")]
    [InlineData("other-error")]
    public async Task ReadRecovery_unsafe_or_unrelated_failure_is_never_retried(string scenario)
    {
        await using var fixture = new HookFixture();
        var cache = await RecoveryCache(fixture, create: scenario is not ("missing" or "directory"));
        if (scenario == "directory") Directory.CreateDirectory(cache);
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "unsafe-read", JsonSerializer.SerializeToElement(new { }), default);
        fixture.Requests = 0;
        var attempts = 0; var waits = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, stamped, default,
            writeReadCache: (_, _, _) =>
            {
                attempts++;
                if (scenario is "changed" or "corrupt") NodeState.WritePrivately(cache, Encoding.UTF8.GetBytes(scenario == "changed" ? "[]\n" : "[broken"));
                throw NativeReadError(scenario == "other-error" ? 112 : 32);
            }, retryDelay: (_, _) => { waits++; return Task.CompletedTask; });
        Assert.False(result.Ok); Assert.Equal("native_stamp_invalid", result.Code);
        Assert.Equal(scenario == "directory" ? 0 : 1, attempts); Assert.Equal(0, waits); Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
        if (scenario == "directory") Assert.True(Directory.Exists(cache));
        if (scenario == "missing") Assert.False(File.Exists(cache));
        if (scenario is "changed" or "corrupt") Assert.Equal(scenario == "changed" ? "[]\n" : "[broken", File.ReadAllText(cache));
    }

    private static IOException NativeReadError(int nativeError) => new("Synthetic native read-cache denial.", unchecked((int)(0x80070000u | (uint)nativeError)));

    private static async Task<string> RecoveryCache(HookFixture fixture, bool create = true)
    {
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        await using var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true);
        var cache = Path.ChangeExtension(binding.StatePath, ".reads");
        if (create) NodeState.WritePrivately(cache, Encoding.UTF8.GetBytes("[]"));
        return cache;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixRound2_unauthenticated_short_name_spelling_never_normalizes_or_resolves_checkout(bool unc)
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var signed = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "short-name-forgery", JsonSerializer.SerializeToElement(new { }), default);
        var forged = JsonNode.Parse(signed.GetRawText())!.AsObject();
        forged["_cocernNative"]!["cwd"] = unc ? @"\\untrusted.invalid\share\PROJEC~1" : @"C:\untrusted-fixture\PROJEC~1";
        forged["_cocernNative"]!["mac"] = new string('0', 64);
        var normalizations = 0; var resolutions = 0; fixture.Requests = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(forged), default,
            normalizeWorkspace: _ => { normalizations++; throw new IOException("Instrumented normalization refused."); },
            resolveCheckout: (_, _) => { resolutions++; throw new IOException("Instrumented checkout resolution refused."); });
        Assert.False(result.Ok); Assert.Equal("native_stamp_invalid", result.Code);
        Assert.Equal(0, normalizations); Assert.Equal(0, resolutions); Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixRound2_signing_waits_for_complete_candidate_set_and_refuses_ambiguity(bool ambiguous)
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var repository = ambiguous ? "other-repo" : "repo";
        var held = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, repository, fixture.Capture.Cwd, default);
        if (ambiguous)
        {
            var handle = new SessionHandle("s-other-owned", "synthetic-other-token", repository, "fixture", DateTimeOffset.UtcNow,
                new("claude", Parent), new("claude", Parent)) { NativeBindingKey = held.OwnershipKey };
            NodeState.WriteSessionHandle(fixture.Folder, handle); Assert.True(held.Complete(handle));
        }
        fixture.Requests = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stamping = NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "busy-candidate", JsonSerializer.SerializeToElement(new { }), timeout.Token,
            resolveCheckout: (_, _) => Task.FromResult(fixture.Capture.Cwd));
        var completedWhileBusy = stamping.IsCompleted;
        await held.DisposeAsync();
        JsonElement result = default;
        var failure = await Record.ExceptionAsync(async () => { result = await stamping.WaitAsync(timeout.Token); });
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
        Assert.False(completedWhileBusy, "A busy candidate must not disappear from the lookup.");
        if (ambiguous) Assert.IsAssignableFrom<IOException>(failure);
        else { Assert.Null(failure); Assert.True(result.TryGetProperty("_cocernNative", out _)); }
    }

    [Fact]
    public async Task FixRound2_signing_wait_on_established_lease_is_cancellable_without_relay()
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        await using var held = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true);
        fixture.Requests = 0;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stamping = NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.ChatDirectory, "cancelled-lookup", JsonSerializer.SerializeToElement(new { }), cancel.Token,
            resolveCheckout: (_, _) => Task.FromResult(fixture.Capture.Cwd));
        cancel.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stamping);
        Assert.Equal(cancel.Token, cancelled.CancellationToken);
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business);
    }

    [Fact]
    public async Task FixRound2_nonregular_read_history_refuses_signed_mutation_before_any_relay_or_permanent_fence()
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        var signed = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "directory-mutation", JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "never dispatch" }), default);
        string cache; string mutations;
        await using (var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, "claude", Parent, Environment.MachineName, "repo", fixture.Capture.Cwd, default, existingOnly: true))
        {
            cache = Path.ChangeExtension(binding.StatePath, ".reads"); mutations = Path.Combine(Path.GetDirectoryName(binding.StatePath)!, "calls-" + binding.BindingKey);
            Directory.CreateDirectory(cache); File.WriteAllText(Path.Combine(cache, "sentinel"), "unchanged");
        }
        fixture.Requests = 0;
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, signed, default);
        Assert.False(result.Ok); Assert.Equal("native_stamp_invalid", result.Code);
        Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Business); Assert.False(Directory.Exists(mutations));
        Assert.True(Directory.Exists(cache)); Assert.Equal("unchanged", File.ReadAllText(Path.Combine(cache, "sentinel")));
    }

    private sealed class AdvancingClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class HookFixture : IAsyncDisposable
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
        /// <summary>A session's inbox while <see cref="WakeReady"/>; unset, every session holds one message.</summary>
        internal Func<string?, string[]>? InboxOf;
        internal bool OwnReply;
        internal List<string> OwnHandles { get; } = [];
        internal List<JsonElement> Business { get; } = [];

        internal HookFixture(string connectionToken = "synthetic-connection", string origin = "http://127.0.0.1:4321")
        {
            var checkout = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
            var git = new ProcessStartInfo("git") { WorkingDirectory = checkout, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            git.ArgumentList.Add("init"); git.ArgumentList.Add("-q");
            foreach (var key in git.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) git.Environment.Remove(key);
            using (var process = Process.Start(git)!) { var error = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.True(process.ExitCode == 0, error); }
            var options = Options = new AdapterOptions(origin, connectionToken);
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
                else if (WakeReady && args.TryGetProperty("headersOnly", out _)) { var inbox = InboxOf?.Invoke(ChatLocators.Text(args, "sessionId")) ?? new[] { "m-native" }; reply = new { items = inbox.Select(id => new { messageId = id, repositoryId = "repo" }), total = inbox.Length, nextCursor = (int?)null, truncated = false }; }
                else if (WakeReady && request.RequestUri!.AbsolutePath.EndsWith("/check_inbox", StringComparison.Ordinal)) { var inbox = InboxOf?.Invoke(ChatLocators.Text(args, "sessionId")) ?? new[] { "m-native" }; reply = new { items = inbox.Select(id => new { messageId = id, repositoryId = "repo", text = "UNTRUSTED" }), total = inbox.Length, nextCursor = (int?)null, truncated = false }; }
                else if (request.RequestUri!.AbsolutePath.EndsWith("/get_status", StringComparison.Ordinal)) reply = new { sessionId = ChatLocators.Text(args, "sessionId") ?? "s-native-hook", control = new { paused = false, requests = Array.Empty<object>() } };
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

    internal sealed class HookHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

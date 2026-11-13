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
    [InlineData("codex")]
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
    [InlineData("{\"agent_id\":\"428b95a1-5717-4397-a8ee-f3a0c0e65ae0\",\"cwd\":\"C:/checkout\",\"hook_event_name\":\"PreToolUse\"}")]
    [InlineData("{\"session_id\":\"c9231b18-d5e5-48bd-9a36-123f7af99a68\",\"session_id\":\"428b95a1-5717-4397-a8ee-f3a0c0e65ae0\",\"cwd\":\"C:/checkout\",\"hook_event_name\":\"SessionStart\"}")]
    public void Missing_malformed_duplicate_or_child_only_capture_is_refused(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(Parse("codex", document.RootElement));
    }

    private static NativeHookContext? Parse(string provider, JsonElement input) => NativeJoinHook.ParseContext(provider, input);

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

    // Security boundary: the PreToolUse hook stamps a tool_call's target and inner arguments, never the wrapper, and only for an on-demand target.
    [Fact]
    public async Task The_tool_call_hook_stamps_only_an_on_demand_target_with_its_own_arguments()
    {
        await using var fixture = new HookFixture();
        await fixture.Join.EnsureAsync(fixture.Capture, default);
        string Hook(string invocation, object toolInput) => JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", session_id = Parent, cwd = fixture.Capture.Cwd,
            tool_name = "mcp__cocern__tool_call", tool_use_id = invocation, tool_input = toolInput });

        using (var refusedOutput = new StringWriter())
        using (var refusedError = new StringWriter())
        {
            Assert.Equal(2, await NativeJoinHook.RunAsync("claude", new StringReader(Hook("listed-1", new { name = RelayTools.SendMessage, arguments = new { recipientSessionId = "peer", text = "hello" } })), refusedOutput, refusedError, fixture.Join, default));
            Assert.Empty(refusedOutput.ToString());
        }

        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await NativeJoinHook.RunAsync("claude", new StringReader(Hook("on-demand-1", new { name = RelayTools.DocSearch, arguments = new { query = "a" } })), output, error, fixture.Join, default));
        using var emitted = JsonDocument.Parse(output.ToString());
        var updated = emitted.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.True(OnDemandTools.TryTarget(updated, out var target, out var arguments));
        Assert.Equal(RelayTools.DocSearch, target);

        fixture.Requests = 0;
        Assert.False((await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.DocSection, arguments, default)).Ok);
        Assert.Equal(0, fixture.Requests);
        Assert.Empty(fixture.Business);
        var result = await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.DocSearch, arguments, default);
        Assert.True(result.Ok, result.Code);
        Assert.Equal("a", fixture.Business.Single().GetProperty("query").GetString());
    }

    [Fact]
    public async Task Session_start_stays_pending_until_first_CoCern_call_and_midlife_join_needs_no_resume()
    {
        await using var fixture = new HookFixture();
        using var output = new StringWriter(); using var error = new StringWriter();
        var configured = NativeJoinHook.ReadConnection(fixture.Folder, name => name == "COCERN_REPOSITORY" ? "repo" : null)!;
        Assert.Equal("repo", configured.Repository);
        Assert.Equal(fixture.Options.BaseUri, configured.BaseUri);
        Assert.Equal(fixture.Options.Token, configured.Token);
        Assert.Equal("repo", NativeRegistration.ClientOptions(configured, _ => null).Repository);
        var privateStart = new ProcessStartInfo();
        privateStart.Environment["COCERN_REPOSITORY"] = "repo";
        privateStart.Environment["COCERN_TOKEN"] = "untrusted-ambient";
        NativeRegistration.RestrictPrivateEnvironment(privateStart);
        Assert.Equal("repo", privateStart.Environment["COCERN_REPOSITORY"]);
        Assert.Equal("invalid-private-fallback", privateStart.Environment["COCERN_TOKEN"]);
        var input = JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = Parent, cwd = fixture.Capture.Cwd });
        Assert.Equal(0, await NativeJoinHook.RunAsync("claude", new StringReader(input), output, error, fixture.Join, default));
        Assert.Equal(0, fixture.Requests);
        Assert.Equal(0, fixture.NativeJoins);
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(fixture.Folder));
        Assert.Equal(fixture.Capture.Cwd, fixture.Join.LatestPendingWorkspace("claude"));
        Assert.Empty(output.ToString()); Assert.Empty(error.ToString());
        for (var i = 0; i < 3; i++)
        {
            var ephemeral = JsonSerializer.Serialize(new { hook_event_name = "SessionStart", session_id = Guid.NewGuid().ToString("D"), cwd = fixture.Capture.Cwd });
            Assert.Equal(0, await NativeJoinHook.RunAsync("claude", new StringReader(ephemeral), output, error, fixture.Join, default));
        }
        Assert.Single(Directory.EnumerateFiles(fixture.Folder, "native-startup-intent-claude-*.json"));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Folder, "native-pending-*"));
        await fixture.Join.ResumeAsync(default);
        Assert.Equal(0, fixture.Requests);
        var stop = JsonSerializer.Serialize(new { hook_event_name = "Stop", session_id = Parent, cwd = fixture.Capture.Cwd });
        Assert.Equal(0, await StopHook.RunNativeAsync(new StringReader(stop), error, fixture.Join, default, fixture.Handler));
        Assert.Equal(0, fixture.Requests);

        var business = JsonSerializer.SerializeToElement(new { recipientSessionId = "peer", text = "hello" });
        var stamped = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.SendMessage, "lazy-first", business, default);
        Assert.Equal(1, fixture.NativeJoins);
        Assert.Empty(fixture.Business);
        Assert.True((await NativeJoinHook.InvokeAsync(fixture.Join, "claude", RelayTools.SendMessage, stamped, default)).Ok);
        Assert.Single(fixture.Business);
        Assert.Single(NativeJoinBinding.DispatchSnapshot(fixture.Folder));

        // Hooks installed into an existing chat need no fabricated SessionStart or manual resume.
        await using var midlife = new HookFixture();
        var first = await NativeJoinHook.StampAsync(midlife.Join, midlife.Capture, RelayTools.SendMessage, "midlife-first", business, default);
        Assert.Equal(1, midlife.NativeJoins);
        Assert.True((await NativeJoinHook.InvokeAsync(midlife.Join, "claude", RelayTools.SendMessage, first, default)).Ok);
        Assert.Single(midlife.Business);
    }

    [Fact]
    public async Task Codex_repository_denial_reports_mapping_guidance_without_verifying_or_allocating_a_session()
    {
        await using var fixture = new HookFixture { DenyPreflight = true };
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Folder, "owned-codex-profile")).FullName;
        var candidate = ClientSettings.StageNativeCandidate(fixture.Folder, "codex", "fixture-helper", clientProfile: profile)!;
        var registration = NativeRegistration.Read(candidate, "codex", fixture.Folder, profile)!;
        var initial = new CoCern.Contracts.Dashboard.DashboardCodexReadiness
        { State = "awaiting_call", Summary = "Ready for first call", NextAction = "Use CoCern", HooksInstalled = true, SkillInstalled = true };
        Task<AdapterReply> Preflight(JsonElement args, CancellationToken ct) => fixture.Forwarder.ForwardAsync(RelayTools.SessionOpen, args, ct);
        // Refresh diagnoses the hub before a hook has fired, without creating a session or receipt.
        var beforeStartup = await NativeStartupEvidence.VerifyAsync(registration, initial, fixture.Capture.Cwd, "repo", Preflight);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, beforeStartup.Detail);
        Assert.Contains("Repository mapping", beforeStartup.Summary, StringComparison.Ordinal);
        Assert.False(beforeStartup.StartupObserved);
        Assert.False(beforeStartup.SessionVerified);
        Assert.False(beforeStartup.WakeReady);
        Assert.Equal(0, fixture.NativeJoins);
        Assert.Equal(1, fixture.Requests);
        Assert.False(File.Exists(Path.Combine(fixture.Folder, "native-codex-startup.json")));
        foreach (var code in new[] { "native_private_connection_required", "native_handle_conflict" })
        {
            NativeStartupEvidence.RecordRefusal(registration, code);
            var refused = NativeStartupEvidence.Verify(registration, initial);
            Assert.Equal(code, refused.Detail);
            Assert.False(refused.SessionVerified);
            Assert.False(refused.WakeReady);
            Assert.DoesNotContain("resume", refused.NextAction, StringComparison.OrdinalIgnoreCase);
        }
        var input = JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", session_id = Parent, cwd = fixture.Capture.Cwd,
            tool_name = "mcp__cocern__send_message", tool_use_id = "mapped-first", tool_input = new { recipientSessionId = "peer", text = "hello" } });
        using var output = new StringWriter(); using var error = new StringWriter();

        Assert.Equal(2, await NativeJoinHook.RunAsync("codex", new StringReader(input), output, error, fixture.Join, default, registration));
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(0, fixture.NativeJoins);
        Assert.Contains(RelayErrorCodes.RepositoryDenied, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("synthetic", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Capture.Cwd, error.ToString(), StringComparison.Ordinal);
        NodeState.WritePrivately(Path.Combine(fixture.Folder, "native-codex-startup.json"), Encoding.UTF8.GetBytes("{malformed old success receipt"));
        var readiness = NativeStartupEvidence.Verify(registration, initial);
        Assert.Equal("error", readiness.State);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, readiness.Detail);
        Assert.True(readiness.StartupObserved);
        Assert.True(readiness.HooksInstalled);
        Assert.False(readiness.SessionVerified);
        Assert.False(readiness.WakeReady);
        Assert.Null(readiness.SessionId);
        Assert.Null(readiness.RepositoryId);
        var rejectedPath = Path.Combine(fixture.Folder, "native-codex-startup-error.json");
        Assert.True(NodeState.IsPrivateFile(rejectedPath));
        using var rejected = JsonDocument.Parse(File.ReadAllBytes(rejectedPath));
        Assert.Equal(new[] { "Definition", "Code", "CapturedAt" }, rejected.RootElement.EnumerateObject().Select(property => property.Name));

        fixture.DenyPreflight = false;
        var mapped = await NativeStartupEvidence.VerifyAsync(registration, initial, fixture.Capture.Cwd, "REPO", Preflight);
        Assert.Equal("awaiting_call", mapped.State);
        Assert.False(mapped.StartupObserved);
        Assert.False(mapped.SessionVerified);
        Assert.False(mapped.WakeReady);
        Assert.Equal(0, fixture.NativeJoins);
        var diagnosticTime = DateTimeOffset.UtcNow.AddSeconds(-1);
        using (var diagnostics = new ConnectionTraceStore(Path.Combine(fixture.Folder, "connection-diagnostics"), persistAutomatically: false))
        {
            diagnostics.Record(new CoCern.Contracts.Dashboard.ConnectionDiagnosticEvent
            {
                CorrelationId = "cc-" + Guid.NewGuid().ToString("N"), Tool = "unknown", Source = "adapter", Stage = "native_registration",
                StartedAt = diagnosticTime, DurationMs = 15000, Outcome = "error", ErrorCode = "native_registration_unverified"
            });
            diagnostics.Flush();
            var unloaded = await NativeStartupEvidence.VerifyAsync(registration, initial, fixture.Capture.Cwd, "repo", Preflight);
            Assert.Equal("native_registration_unverified", unloaded.Detail);
            Assert.False(unloaded.SessionVerified);
            Assert.False(unloaded.WakeReady);
            Assert.Equal(0, fixture.NativeJoins);
            // A newer private startup/stamp receipt is not evidence that the MCP catalog loaded.
            NodeState.WritePrivately(Path.Combine(fixture.Folder, "native-codex-startup.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                Definition = registration.Definition, Profile = registration.Profile, NativeId = Parent, Workspace = fixture.Capture.Cwd,
                RepositoryId = "repo", SessionId = "s-native-hook", Generation = "unloaded-fixture", CapturedAt = DateTimeOffset.UtcNow
            }));
            Assert.Equal("native_registration_unverified", (await NativeStartupEvidence.VerifyAsync(registration, initial, fixture.Capture.Cwd, "repo", Preflight)).Detail);
            File.Delete(Path.Combine(fixture.Folder, "native-codex-startup.json"));
            diagnostics.Record(new CoCern.Contracts.Dashboard.ConnectionDiagnosticEvent
            {
                CorrelationId = "cc-" + Guid.NewGuid().ToString("N"), Tool = "unknown", Source = "adapter", Stage = "mcp_list_tools",
                StartedAt = diagnosticTime.AddMilliseconds(1), DurationMs = 1, Outcome = "ok"
            });
            diagnostics.Flush();
            Assert.Equal("awaiting_call", (await NativeStartupEvidence.VerifyAsync(registration, initial, fixture.Capture.Cwd, "repo", Preflight)).State);
        }
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        Assert.Equal(0, await NativeJoinHook.RunAsync("codex", new StringReader(input), output, error, fixture.Join, default, registration));
        Assert.Equal(1, fixture.NativeJoins);
        Assert.Empty(error.ToString());
        Assert.Contains("updatedInput", output.ToString(), StringComparison.Ordinal);
        using var succeeded = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Folder, "native-codex-startup.json")));
        Assert.True(succeeded.RootElement.GetProperty("CapturedAt").GetDateTimeOffset() >= rejected.RootElement.GetProperty("CapturedAt").GetDateTimeOffset());
        Assert.NotEqual(RelayErrorCodes.RepositoryDenied, NativeStartupEvidence.Verify(registration, initial).Detail);
    }

    [Fact]
    public async Task Owned_Stop_path_delivers_from_the_scoped_native_waiter()
    {
        await using var fixture = new HookFixture();
        _ = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.GetStatus, "before-stop", JsonSerializer.SerializeToElement(new { }), default);
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
        _ = await NativeJoinHook.StampAsync(fixture.Join, fixture.Capture, RelayTools.GetStatus, "before-wakes", JsonSerializer.SerializeToElement(new { }), default);
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
        // Structured presentation must use this already projected reply, never the underlying private native response.
        var structured = McpReadResults.FromAdapter(RelayTools.GetStatus, reply).StructuredContent!.Value.GetProperty("data");
        foreach (var field in new[] { "sessionId", "sessionToken", "_cocernNative", "nativeJoin" }) Assert.False(structured.TryGetProperty(field, out _));
        Assert.Equal("peer", structured.GetProperty("recipientSessionId").GetString());
        Assert.Equal("peer-nested", structured.GetProperty("peer").GetProperty("sessionId").GetString());
        const string supportId = "cc-0123456789abcdef0123456789abcdef";
        var refused = McpReadResults.FromAdapter(RelayTools.GetStatus, new(false, "native_stamp_required") { ConnectionTraceId = supportId });
        Assert.Equal(JsonValueKind.Null, refused.StructuredContent!.Value.GetProperty("data").ValueKind);
        Assert.Equal(supportId, refused.StructuredContent.Value.GetProperty("connectionTrace").GetString());
        var schema = McpReadResults.OutputSchema(RelayTools.GetStatus)!.Value;
        foreach (var property in refused.StructuredContent.Value.EnumerateObject()) Assert.True(schema.GetProperty("properties").TryGetProperty(property.Name, out _));
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
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Join.PrivateFolder, NodeState.SessionsFolder, "native-chats"), "*.json"));
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
        internal int NativeJoins;
        internal bool DenyPreflight;
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
                if (DenyPreflight && args.TryGetProperty("nativePreflight", out _))
                    return new(HttpStatusCode.Forbidden) { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, code = RelayErrorCodes.RepositoryDenied, dataJson = "{}", version = 1 }), Encoding.UTF8, "application/json") };
                if (ChatLocators.Text(args, "sessionId") is { } own) { OwnHandles.Add(own); Assert.Equal("synthetic-handle-token", ChatLocators.Text(args, "sessionToken")); }
                object reply;
                if (args.TryGetProperty("nativePreflight", out _)) reply = new { repositoryId = "repo" };
                else if (args.TryGetProperty("nativeJoin", out _)) { NativeJoins++; reply = new { sessionId = "s-native-hook", sessionToken = "synthetic-handle-token" }; }
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

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Every adapter-originated hub call says who made it, so measurement can tell a model's calls from background ones. One test per caller,
/// each asserting what the hub receives; the tag grants nothing, so a call without one, an exact native envelope and what the model sees stay as they were.
/// </summary>
public sealed class CallOriginAdapterTests : IDisposable
{
    private const string Chat = "4f0c2d6e-7a1b-4c3d-9e8f-0a1b2c3d4e5f";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-call-origin-adapter-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ---- One test per caller ----

    [Fact]
    public async Task A_model_tool_call_reports_mcp_on_every_request_including_the_polls_of_one_that_waits()
    {
        var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(), null, null, "origin-scope");
        Assert.True((await calls.CallAsync(RelayTools.SessionOpen, Element(new { repositoryId = "repo", requestId = "open-1", label = "Chat" }), CancellationToken.None)).Ok);
        Assert.True((await calls.CallAsync(RelayTools.TaskGet, Element(new { repositoryId = "repo" }), CancellationToken.None)).Ok);
        Assert.True((await calls.CallAsync(RelayTools.CheckInbox, Element(new { repositoryId = "repo", sessionId = "s-1", sessionToken = "synthetic-token", waitSeconds = 1 }), CancellationToken.None)).Ok);

        Assert.Equal(new[] { RelayTools.SessionOpen, RelayTools.TaskGet, RelayTools.CheckInbox }, hub.Tools.Select(call => call.Tool).Distinct());
        Assert.All(hub.Tools, call => Assert.Equal(CallOrigins.Mcp, Origin(call.Body)));
    }

    [Fact]
    public async Task A_native_tool_call_reports_mcp_its_binding_probe_reports_node_and_the_exact_opening_envelopes_stay_untagged()
    {
        await using var fixture = new NativeJoinHookTests.HookFixture();
        var hub = new FakeHub();
        using var http = new HttpClient(hub, disposeHandler: false);
        await using var join = new NativeJoin(fixture.Folder, fixture.Options, new RelayForwarder(http, fixture.Options));
        // The second call finds a handle, so it verifies it first.
        Assert.True((await join.CallAsync(fixture.Capture, "call-1", RelayTools.TaskGet, Element(new { }), default)).Ok);
        Assert.True((await join.CallAsync(fixture.Capture, "call-2", RelayTools.TaskGet, Element(new { }), default)).Ok);

        var opening = hub.Tools.Where(call => call.Tool == RelayTools.SessionOpen).ToArray();
        // A hub older than the origin strip counts these fields exactly, so nothing may be added to them.
        Assert.Contains(opening, call => call.Body.ContainsKey("nativePreflight"));
        Assert.Contains(opening, call => call.Body.ContainsKey("nativeJoin"));
        Assert.All(opening, call => Assert.False(call.Body.ContainsKey(CallOrigins.Field)));
        Assert.Equal(new[] { "nativePreflight" }, opening.First(call => call.Body.ContainsKey("nativePreflight")).Body.Select(field => field.Key));
        var modelCalls = hub.Tools.Where(call => call.Tool == RelayTools.TaskGet).ToArray();
        Assert.Equal(2, modelCalls.Length);
        Assert.All(modelCalls, call => Assert.Equal(CallOrigins.Mcp, Origin(call.Body)));
        var probes = hub.Tools.Where(call => call.Tool == RelayTools.GetStatus).ToArray();
        Assert.NotEmpty(probes);
        Assert.All(probes, call => Assert.Equal(CallOrigins.Node, Origin(call.Body)));
    }

    [Fact]
    public async Task A_chats_inbox_waiter_reports_claude_waiter_on_its_acknowledgment_its_wait_and_its_printed_inbox()
    {
        EnrollWithClaudeHandle();
        var hub = new FakeHub();
        var printed = new List<string>();
        Assert.Equal("queued", await ClaudeWaiter.RunAsync(root, Chat, printed.Add, TimeSpan.FromSeconds(30), CancellationToken.None, hub, () => new NoHub(), printInbox: true, acknowledge: ["m0"]));

        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.Ack);
        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.GetStatus);
        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.CheckInbox);
        Assert.All(hub.Tools, call => Assert.Equal(CallOrigins.ClaudeWaiter, Origin(call.Body)));
    }

    [Fact]
    public async Task The_stop_hook_reports_claude_stop_on_the_nodes_connection()
    {
        EnrollWithClaudeHandle();
        var hub = new FakeHub();
        using var error = new StringWriter();
        Assert.Equal(2, await StopHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = Chat })), error, root, CancellationToken.None, StopHook.Wait(hub, () => new NoHub())));

        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.GetStatus);
        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.CheckInbox);
        Assert.All(hub.Tools, call => Assert.Equal(CallOrigins.ClaudeStop, Origin(call.Body)));
    }

    [Fact]
    public async Task The_native_stop_hook_reports_claude_stop_and_its_opening_envelopes_stay_untagged()
    {
        await using var fixture = new NativeJoinHookTests.HookFixture();
        var hub = new FakeHub();
        using var http = new HttpClient(hub, disposeHandler: false);
        await using var join = new NativeJoin(fixture.Folder, fixture.Options, new RelayForwarder(http, fixture.Options));
        var input = JsonSerializer.Serialize(new { hook_event_name = "Stop", session_id = fixture.Capture.NativeId, cwd = fixture.Capture.Cwd });
        using var error = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.Equal(2, await StopHook.RunNativeAsync(new StringReader(input), error, join, timeout.Token, hub, TimeSpan.FromSeconds(10)));

        Assert.All(hub.Tools.Where(call => call.Tool == RelayTools.SessionOpen), call => Assert.False(call.Body.ContainsKey(CallOrigins.Field)));
        var waiting = hub.Tools.Where(call => call.Tool != RelayTools.SessionOpen).ToArray();
        Assert.Contains(waiting, call => call.Tool == RelayTools.CheckInbox);
        Assert.All(waiting, call => Assert.Equal(CallOrigins.ClaudeStop, Origin(call.Body)));
    }

    [Fact]
    public async Task The_prompt_hook_reports_node()
    {
        EnrollWithClaudeHandle();
        var hub = new FakeHub();
        await PromptHook.LineAsync(JsonSerializer.Serialize(new { session_id = Chat }), root, TimeSpan.FromSeconds(10), hub);

        var call = Assert.Single(hub.Tools);
        Assert.Equal(RelayTools.GetStatus, call.Tool);
        Assert.Equal(CallOrigins.Node, Origin(call.Body));
    }

    /// <summary>The watcher makes its calls through its reconciler. A runtime that names no origin, as the local chat wake's does not, reports as the watcher.</summary>
    [Fact]
    public async Task The_wake_watcher_and_its_reconciler_report_wake_watcher_by_default()
    {
        Directory.CreateDirectory(root);
        var hub = new FakeHub();
        var runtime = new WakeRuntime(new AdapterOptions(FakeHub.Origin, "synthetic-credential"), Path.Combine(root, "wake.db"), Path.Combine(root, "stop"), FakeHub.Origin + "/") { HubHandler = () => new NoHub() };
        var target = new WakeTarget(new("repo", "board", "unused", "thread", "machine"), new("session", "synthetic-token"), new InboxWaitDispatcher(_ => { }));
        Assert.Equal("queued", await WakeWatcher.RunAsync(runtime, _ => Task.FromResult<IReadOnlyList<WakeTarget>>([target]), exitWhenQueued: true, TimeSpan.FromSeconds(15), CancellationToken.None, hub));

        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.GetStatus);
        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.CheckInbox);
        Assert.All(hub.Tools, call => Assert.Equal(CallOrigins.WakeWatcher, Origin(call.Body)));
    }

    /// <summary>The node's own requests are discovery ones, which no tool telemetry reads, so they carry nothing; the wake loop it starts reports as the watcher.</summary>
    [Fact]
    public async Task The_node_runner_leaves_its_announcements_untagged_and_its_wake_loop_reports_wake_watcher()
    {
        var thread = Guid.NewGuid().ToString();
        using var state = NodeState.Open(root);
        state.SetHub(FakeHub.Origin + "/", null);
        state.SaveCredential(new string('c', 64));
        NodeState.WriteSessionHandle(root, new SessionHandle("s-codex", "synthetic-token", "repo", "Codex chat", DateTimeOffset.UtcNow.AddMinutes(-1), Chat: new ChatLocation(ChatProviders.Codex, thread)));
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new FakeHub { Routes = new JsonObject { ["provider"] = ChatProviders.Codex, ["threadId"] = thread, ["relaySessionId"] = "s-codex", ["repositoryId"] = "repo" } };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = NodeRunner.RunAsync(state, null, new NodeTransport
        {
            CreateHandler = (_, _) => hub, HubHandler = () => new NoHub(), Chats = _ => Task.FromResult(new JsonArray()), Locators = [], Clients = () => [], MachineName = "Node PC",
            CodexExecutable = () => Path.Combine(root, "codex.exe"), CodexDispatcher = _ => new SignallingDispatcher(queued),
            AnnounceInterval = TimeSpan.FromMilliseconds(50), WakeInterval = TimeSpan.FromMilliseconds(50)
        }, TextWriter.Null, stop.Token);
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(20));
        stop.Cancel();
        Assert.Equal(0, await run);

        Assert.NotEmpty(hub.Discovery);
        Assert.All(hub.Discovery, body => Assert.False(body.ContainsKey(CallOrigins.Field)));
        Assert.Contains(hub.Tools, call => call.Tool == RelayTools.CheckInbox);
        Assert.All(hub.Tools, call => Assert.Equal(CallOrigins.WakeWatcher, Origin(call.Body)));
    }

    // ---- The tag on the wire ----

    [Theory]
    [InlineData(CallOrigins.Mcp)]
    [InlineData(CallOrigins.ClaudeStop)]
    [InlineData(CallOrigins.ClaudeWaiter)]
    [InlineData(CallOrigins.WakeWatcher)]
    [InlineData(CallOrigins.Node)]
    public void Every_origin_the_adapter_reports_is_one_the_hub_keeps(string origin) =>
        Assert.Equal(origin, new CoCern.Core.ActivityEvent { CallOrigin = origin }.CallOrigin);

    [Fact]
    public async Task A_forwarder_with_no_origin_sends_the_arguments_byte_for_byte_and_the_call_still_works()
    {
        var hub = new FakeHub();
        var arguments = Element(new { repositoryId = "repo" });
        Assert.True((await hub.Forwarder().ForwardAsync(RelayTools.TaskGet, arguments)).Ok);
        Assert.Equal(arguments.GetRawText(), Assert.Single(hub.Raw));
    }

    [Fact]
    public async Task The_tag_is_added_without_touching_any_other_byte()
    {
        var hub = new FakeHub();
        var forwarder = hub.Forwarder(CallOrigins.Node);
        using var spaced = JsonDocument.Parse("{ \"text\": \"café <b>\\u00e9</b>\", \"limit\": 1.0 }");
        await forwarder.ForwardAsync(RelayTools.TaskGet, spaced.RootElement);
        await forwarder.ForwardAsync(RelayTools.TaskGet, Element(new { }));
        Assert.Equal(new[] { "{ \"text\": \"café <b>\\u00e9</b>\", \"limit\": 1.0 ,\"callOrigin\":\"node\"}", "{\"callOrigin\":\"node\"}" }, hub.Raw);
    }

    [Fact]
    public async Task A_value_in_the_arguments_gives_way_to_the_adapters_own_and_is_not_sent_twice()
    {
        var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(), null, null, "origin-scope");
        Assert.True((await calls.CallAsync(RelayTools.TaskGet, Element(new { repositoryId = "repo", callOrigin = "dashboard" }), CancellationToken.None)).Ok);
        var raw = Assert.Single(hub.Raw);
        Assert.Equal(CallOrigins.Mcp, JsonNode.Parse(raw)![CallOrigins.Field]!.GetValue<string>());
        Assert.Equal(2, raw.Split("callOrigin").Length);
    }

    [Fact]
    public async Task A_repeated_key_goes_out_as_it_was_so_the_hub_refuses_it_as_it_always_has()
    {
        var hub = new FakeHub();
        const string Repeated = "{\"callOrigin\":\"mcp\",\"callOrigin\":\"node\"}";
        using var document = JsonDocument.Parse(Repeated);
        await hub.Forwarder(CallOrigins.Node).ForwardAsync(RelayTools.TaskGet, document.RootElement);
        Assert.Equal(Repeated, Assert.Single(hub.Raw));
    }

    [Theory]
    [InlineData("nativePreflight")]
    [InlineData("nativeJoin")]
    public async Task A_tagged_forwarder_leaves_an_exact_native_envelope_as_it_was(string envelope)
    {
        var hub = new FakeHub();
        using var document = JsonDocument.Parse("{\"" + envelope + "\":{\"checkout\":\"synthetic\"}}");
        await hub.Forwarder(CallOrigins.Mcp).ForwardAsync(RelayTools.SessionOpen, document.RootElement);
        Assert.Equal(document.RootElement.GetRawText(), Assert.Single(hub.Raw));
    }

    [Fact]
    public async Task A_body_the_tag_would_push_past_the_limit_goes_untagged_instead_of_failing()
    {
        var hub = new FakeHub();
        const string Empty = "{\"text\":\"\"}";
        var full = "{\"text\":\"" + new string('a', RelayForwarder.MaxRequestBytes - Empty.Length) + "\"}";
        using var document = JsonDocument.Parse(full);
        Assert.Equal(RelayForwarder.MaxRequestBytes, Encoding.UTF8.GetByteCount(full));
        Assert.True((await hub.Forwarder(CallOrigins.Mcp).ForwardAsync(RelayTools.TaskGet, document.RootElement)).Ok);
        Assert.Equal(full, Assert.Single(hub.Raw));
    }

    // ---- What the model sees ----

    [Fact]
    public async Task The_reply_a_model_sees_is_the_same_with_the_tag_as_without_it()
    {
        var hub = new FakeHub();
        var arguments = RelayForwarder.WithOrigin(Element(new { repositoryId = "repo" }), background: false);
        var untagged = await ConditionalRead.ForwardAsync(hub.Forwarder(), RelayTools.TaskGet, arguments, CancellationToken.None);
        var tagged = await new ChatCalls(hub.Forwarder(), null, null, "origin-scope").CallAsync(RelayTools.TaskGet, Element(new { repositoryId = "repo" }), CancellationToken.None);

        var shown = JsonSerializer.Serialize(tagged, AdapterReply.JsonOptions);
        Assert.Equal(JsonSerializer.Serialize(untagged, AdapterReply.JsonOptions), shown);
        Assert.DoesNotContain(CallOrigins.Field, shown);
    }

    [Fact]
    public void The_tag_is_no_tool_input_and_adds_nothing_to_what_a_model_reads()
    {
        using var client = new HttpClient();
        var tools = CoordinationTools.Create(new RelayForwarder(client, new AdapterOptions(FakeHub.Origin, "synthetic-credential")), admin: true).Select(tool => tool.ProtocolTool).ToArray();

        Assert.Equal(CoordinationTools.NamesForRole(true), tools.Select(tool => tool.Name));
        Assert.All(tools, tool =>
        {
            Assert.DoesNotContain(CallOrigins.Field, tool.InputSchema.GetRawText());
            Assert.DoesNotContain(CallOrigins.Field, tool.Description);
            Assert.True(RelayToolCatalog.TryGet(tool.Name, out var definition));
            // Only the adapter's two existing options differ from the shared forwarding schema.
            if (tool.Name != RelayTools.CheckInbox && !ConditionalRead.Applies(tool.Name)) Assert.Equal(RelayToolCatalog.ForwardingSchema(definition).GetRawText(), tool.InputSchema.GetRawText());
        });
        Assert.All(CoCern.Host.ToolResults.Tools(true), tool => Assert.DoesNotContain(CallOrigins.Field, tool.InputSchema.GetRawText()));
        Assert.DoesNotContain(CallOrigins.Field, AdapterInstructions.Server);
        Assert.DoesNotContain(CallOrigins.Field, AdapterInstructions.Native);
    }

    // ---- Fixtures ----

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static string? Origin(JsonObject body) => body[CallOrigins.Field]?.GetValue<string>();

    private void EnrollWithClaudeHandle()
    {
        using (var seed = NodeState.Open(root))
        {
            seed.SetHub(FakeHub.Origin + "/", null);
            seed.SaveCredential(new string('c', 64));
        }
        var chat = new ChatLocation(ChatProviders.Claude, Chat);
        NodeState.WriteSessionHandle(root, new SessionHandle("s-claude", "synthetic-token", "repo", "Claude chat", DateTimeOffset.UtcNow.AddMinutes(-1), chat, chat));
    }

    /// <summary>The hint channel, which is not a tool call and is never available here.</summary>
    private sealed class NoHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class SignallingDispatcher(TaskCompletionSource queued) : IWakeDispatcher
    {
        public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken cancellationToken)
        {
            queued.TrySetResult();
            return Task.FromResult(true);
        }
    }

    /// <summary>Answers tool calls as a hub with one waiting message would, and records exactly what each request sent. It holds nothing to dispose, so it can be handed out again.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        public const string Origin = "http://127.0.0.1:5271";
        private readonly ConcurrentQueue<(string Tool, string Raw)> _tools = new();
        private readonly ConcurrentQueue<string> _discovery = new();

        /// <summary>The one route an announcement is answered with.</summary>
        public JsonObject? Routes { get; init; }
        public (string Tool, JsonObject Body)[] Tools => _tools.Select(call => (call.Tool, JsonNode.Parse(call.Raw)!.AsObject())).ToArray();
        public string[] Raw => _tools.Select(call => call.Raw).ToArray();
        public JsonObject[] Discovery => _discovery.Select(raw => JsonNode.Parse(raw)!.AsObject()).ToArray();

        public RelayForwarder Forwarder(string? origin = null) => new(new HttpClient(this, disposeHandler: false), new AdapterOptions(Origin, "synthetic-credential"), origin);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/discovery/", StringComparison.Ordinal))
            {
                _discovery.Enqueue(body);
                var routes = new JsonObject { ["routes"] = Routes is null ? new JsonArray() : new JsonArray(Routes.DeepClone()) };
                return new(HttpStatusCode.OK) { Content = new StringContent(routes.ToJsonString(), Encoding.UTF8, "application/json") };
            }
            var tool = request.RequestUri.Segments[^1];
            _tools.Enqueue((tool, body));
            var reply = JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = Data(tool, body), version = 1 });
            return new(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }

        private static string Data(string tool, string body)
        {
            using var document = JsonDocument.Parse(body);
            var args = document.RootElement;
            if (args.TryGetProperty("nativePreflight", out _)) return "{\"repositoryId\":\"repo\"}";
            if (args.TryGetProperty("nativeJoin", out _)) return "{\"sessionId\":\"s-native\",\"sessionToken\":\"synthetic-token\"}";
            return tool switch
            {
                RelayTools.SessionOpen => "{\"sessionId\":\"s-1\",\"sessionToken\":\"synthetic-token\"}",
                RelayTools.GetStatus => new JsonObject { ["sessionId"] = ChatLocators.Text(args, "sessionId"), ["control"] = new JsonObject { ["paused"] = false, ["requests"] = new JsonArray() } }.ToJsonString(),
                RelayTools.CheckInbox => "{\"items\":[{\"messageId\":\"m1\",\"repositoryId\":\"repo\",\"text\":\"synthetic\"}],\"total\":1,\"nextCursor\":null,\"truncated\":false}",
                _ => "{}"
            };
        }
    }
}

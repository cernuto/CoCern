using System.Text.Json;
using CoCern.Contracts;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public sealed class NativeJoinCallsTests
{
    [Fact]
    public async Task First_call_and_startup_retry_share_one_authenticated_open()
    {
        using var scope = new NativeScope();
        var allocations = new Dictionary<string, (string Id, string Token)>();
        var replies = new List<string>();
        var drop = true;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(scope.Options.Token, request.Headers.Authorization?.Parameter);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var args = doc.RootElement;
            if (args.TryGetProperty("nativePreflight", out _)) return Wire(new { repositoryId = "repo" });
            if (args.TryGetProperty("nativeJoin", out _))
            {
                var attempt = args.GetProperty("requestId").GetString()!;
                if (!allocations.TryGetValue(attempt, out var row)) allocations[attempt] = row = ("s-captured", "test-handle");
                if (drop) { drop = false; throw new HttpRequestException("synthetic lost answer"); }
                return Wire(new { sessionId = row.Id, sessionToken = row.Token });
            }
            replies.Add(args.GetProperty("sessionId").GetString()!);
            return Wire(new { sessionId = "s-captured" });
        }));
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        await using var helper = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(http, scope.Options));
        await using var adapter = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(http, scope.Options));
        var call = adapter.CallAsync(context, "tool-1", RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { }), default);
        var startup = helper.EnsureAsync(context, default);
        Assert.True((await call).Ok);
        var joined = await startup;
        Assert.Equal("s-captured", joined.SessionId);
        Assert.Single(allocations);
        Assert.Contains("s-captured", replies);
        Assert.Equal(context.NativeId, Assert.Single(NativeJoinBinding.DispatchSnapshot(scope.Folder)).Chat!.ThreadId);
    }

    private static HttpResponseMessage Wire(object data, bool ok = true, string code = "Ok") => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { ok, code, dataJson = JsonSerializer.Serialize(data), version = 1 }), System.Text.Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task Same_adapter_interleaves_two_participants_without_crossing_credentials()
    {
        using var scope = new NativeScope(admin: true); // Synthetic authorized principal for control_request.
        var a = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout, "Same name");
        var b = a with { Provider = "claude", NativeId = Guid.NewGuid().ToString("D") };
        var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var received = new System.Collections.Concurrent.ConcurrentQueue<(string Tool,string Session,string? Token,string? Request)>();
        using var http = new HttpClient(new Handler(async (req, ct) =>
        {
            var tool = req.RequestUri!.Segments[^1];
            using var doc = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
            var args = doc.RootElement;
            if (args.TryGetProperty("nativePreflight", out _)) return Wire(new { repositoryId = "repo" });
            if (args.TryGetProperty("nativeJoin", out var native))
            {
                var id = attempts.GetOrAdd(native.GetProperty("nativeId").GetString()!, key => "s-" + key);
                return Wire(new { sessionId = id, sessionToken = "token-" + id });
            }
            var session = args.GetProperty("sessionId").GetString()!;
            var token = ChatLocators.Text(args, "sessionToken");
            var request = ChatLocators.Text(args, "requestId");
            if (tool != RelayTools.ControlRequest) Assert.Equal("token-" + session, token);
            received.Enqueue((tool,session,token,request));
            return Wire(new { sessionId = session, value = session });
        }));
        await using var adapter = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(http, scope.Options));
        var payload = JsonSerializer.SerializeToElement(new { messageIds = new[] { "m1" } });
        var results = await Task.WhenAll(adapter.CallAsync(a, "same-tool-id", RelayTools.Ack, payload, default), adapter.CallAsync(b, "same-tool-id", RelayTools.Ack, payload, default));
        Assert.All(results, r => Assert.True(r.Ok, r.Code));
        var control = await adapter.CallAsync(a, "control", RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { command = "close_sessions" }), default);
        Assert.True(control.Ok, control.Code);
        Assert.Equal("s-" + a.NativeId, received.Single(x => x.Tool == RelayTools.ControlRequest).Session);
        var requests = received.Where(x => x.Tool == RelayTools.Ack).ToArray();
        Assert.Equal(2, requests.Select(r => r.Request).Distinct().Count());
        Assert.Equal("s-" + a.NativeId, results[0].Data!.Value.GetProperty("sessionId").GetString());
        Assert.Equal("s-" + b.NativeId, results[1].Data!.Value.GetProperty("sessionId").GetString());
        var replay = await adapter.CallAsync(a, "same-tool-id", RelayTools.Ack, payload, default);
        Assert.True(replay.Ok);
        Assert.Equal(requests.Single(r => r.Session == "s-" + a.NativeId).Request, received.Last(x => x.Tool == RelayTools.Ack).Request);
        var conflict = await adapter.CallAsync(a, "same-tool-id", RelayTools.Ack, JsonSerializer.SerializeToElement(new { messageIds = new[] { "m2" } }), default);
        Assert.Equal("native_invocation_conflict", conflict.Code);
        Assert.Equal(3, received.Count(x => x.Tool == RelayTools.Ack));
    }

    [Fact]
    public async Task Only_confirmed_expiry_can_replace_a_generation_and_uncertain_mutations_stay_on_the_old_session()
    {
        using var scope = new NativeScope();
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var attempts = new Dictionary<string,string>();
        var secrets = new HashSet<string>();
        var retired = false; var deny = false; var writes = 0;
        using var http = new HttpClient(new Handler(async (req, ct) =>
        {
            using var doc = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
            var args = doc.RootElement;
            if (args.TryGetProperty("nativePreflight", out _)) return Wire(new { repositoryId = "repo" });
            if (args.TryGetProperty("nativeJoin", out var native))
            {
                secrets.Add(native.GetProperty("bindingSecret").GetString()!);
                var attempt = args.GetProperty("requestId").GetString()!;
                if (!attempts.TryGetValue(attempt, out var id)) attempts.Add(attempt, id = "s-generation-" + attempts.Count);
                if (retired && id == "s-generation-0") return Wire(new { expired = true }, false, RelayErrorCodes.SessionUnknown);
                return Wire(new { sessionId = id, sessionToken = "t-" + id });
            }
            var session = args.GetProperty("sessionId").GetString();
            if (req.RequestUri!.Segments[^1] == RelayTools.Ack) { writes++; throw new HttpRequestException("lost mutation reply"); }
            return deny ? Wire(new { }, false, RelayErrorCodes.SessionDenied) :
                retired && session == "s-generation-0" ? Wire(new { expired = true }, false, RelayErrorCodes.SessionUnknown) : Wire(new { sessionId = session });
        }));
        var transport = new RelayForwarder(http, scope.Options);
        var payload = JsonSerializer.SerializeToElement(new { messageIds = new[] { "m1" } });
        SessionHandle old;
        await using (var first = new NativeJoin(scope.Folder, scope.Options, transport))
        {
            old = await first.EnsureAsync(context, default);
            Assert.False((await first.CallAsync(context, "uncertain", RelayTools.Ack, payload, default)).Ok);
        }
        await using var restarted = new NativeJoin(scope.Folder, scope.Options, transport);
        deny = true;
        Assert.Equal(RelayErrorCodes.SessionDenied, (await restarted.CallAsync(context, "denied", RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { }), default)).Code);
        Assert.Equal(old, Assert.Single(NativeJoinBinding.DispatchSnapshot(scope.Folder))); Assert.Single(attempts);
        deny = false; retired = true;
        Assert.Equal("native_invocation_conflict", (await restarted.CallAsync(context, "uncertain", RelayTools.Ack, payload, default)).Code);
        Assert.Equal(1, writes); Assert.Single(secrets); Assert.Equal(2, attempts.Count);
        var current = Assert.Single(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        Assert.NotEqual(old.SessionId, current.SessionId); Assert.NotEqual(old.SessionToken, current.SessionToken);
        var routes = new[]{ old, current }.Select(s => new NodeRoute("codex", context.NativeId, s.SessionId, s.RepositoryId));
        var target = Assert.Single(NodeRunner.WakeTargets(scope.Folder, routes, "fixture-codex", _ => new CodexWakeDispatcher("fixture-codex"), "machine"));
        Assert.Equal(current.SessionId, target.Session.SessionId); Assert.Equal(current.SessionToken, target.Session.SessionToken);
        Assert.Equal(context.NativeId, target.Route.ThreadId);
        NodeState.WriteSessionHandle(scope.Folder, current with { Chat = new("claude", context.NativeId) });
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        Assert.Empty(NodeRunner.WakeTargets(scope.Folder, routes, "fixture-codex", _ => new CodexWakeDispatcher("fixture-codex"), "machine"));
    }

    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static (string Folder, AdapterOptions Options) ReviewConnection(NativeScope scope, bool enrolled)
    {
        if (!enrolled) return (scope.Folder, scope.Options);
        using var node = NodeState.Open(Path.Combine(scope.Root, "review-node"));
        node.SetHub(scope.Options.BaseUri.AbsoluteUri, null);
        node.SaveCredential(new string('c', 64));
        return (node.Folder, AdapterOptions.FromEnvironment(key => key == "COCERN_NODE_DIR" ? node.Folder : null));
    }

    private static HttpResponseMessage ReviewReply(JsonElement args) =>
        args.TryGetProperty("nativePreflight", out _) ? Wire(new { repositoryId = "repo" }) :
        args.TryGetProperty("nativeJoin", out _) ? Wire(new { sessionId = "s-native", sessionToken = "own-token" }) :
        args.TryGetProperty("headersOnly", out _) ? Wire(new { items = new[] { new { messageId = "m1", repositoryId = "repo" } }, total = 1, nextCursor = (int?)null, truncated = false }) :
        Wire(new { sessionId = "s-native", control = new { paused = false, requests = Array.Empty<object>() } });

    [Fact]
    public async Task Round1_compact_chat_private_probe_supports_startup_and_subsequent_calls()
    {
        using var scope = new NativeScope();
        var options = new AdapterOptions(scope.Options.BaseUri.AbsoluteUri, scope.Options.Token) { ChatOnly = true };
        var sends = 0;
        using var http = new HttpClient(new Handler(async (r, ct) =>
        {
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (r.RequestUri!.Segments[^1] == RelayTools.Ack) { Assert.Equal("own-token", ChatLocators.Text(doc.RootElement, "sessionToken")); sends++; }
            return ReviewReply(doc.RootElement);
        }));
        var capture = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var forwarder = new RelayForwarder(http, options);
        await using var join = new NativeJoin(scope.Folder, options, forwarder);
        var own = await join.EnsureAsync(capture, default);
        Assert.Equal("s-native", own.SessionId);
        var args = JsonSerializer.SerializeToElement(new { messageIds = new[] { "m1" } });
        foreach (var id in new[] { "first", "later" })
        {
            var reply = await join.CallAsync(capture, id, RelayTools.Ack, args, default);
            Assert.True(reply.Ok, reply.Code);
        }
        Assert.Equal(2, sends);
        Assert.Equal("unknown_tool", (await forwarder.ForwardAsync(RelayTools.GetStatus, args)).Code);
    }

    [Fact]
    public async Task Round1_already_canceled_public_forward_returns_canceled_task_without_http()
    {
        var sends = 0;
        using var http = new HttpClient(new Handler((_, _) => { sends++; return Task.FromResult(Wire(new { })); }));
        var forwarder = new RelayForwarder(http, new AdapterOptions("http://127.0.0.1:4321", "fixture"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var returned = forwarder.ForwardAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { }), canceled.Token);
        Assert.True(returned.IsCanceled);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => returned);
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Round1_scoped_Claude_waiter_delivers_from_own_session_when_legacy_neighbor_exists(bool enrolled)
    {
        using var scope = new NativeScope();
        var (folder, options) = ReviewConnection(scope, enrolled);
        var capture = new NativeHookContext("claude", Guid.NewGuid().ToString("D"), scope.Checkout);
        var keys = new List<string>();
        using var handler = new Handler(async (r, ct) =>
        {
            Assert.Equal(options.Token, r.Headers.Authorization?.Parameter);
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (ChatLocators.Text(doc.RootElement, "sessionId") is { } id)
            { keys.Add(id); Assert.Equal("own-token", ChatLocators.Text(doc.RootElement, "sessionToken")); }
            return ReviewReply(doc.RootElement);
        });
        using var http = new HttpClient(handler, false);
        await using var join = new NativeJoin(folder, options, new RelayForwarder(http, options));
        await join.EnsureAsync(capture, default);
        var location = new ChatLocation("claude", capture.NativeId);
        NodeState.WriteSessionHandle(folder, new("s-neighbor", "neighbor", "repo", "Same", DateTimeOffset.UtcNow, location, location));
        Assert.Equal("s-neighbor", ClaudeWaiter.FindHandle(folder, capture.NativeId)!.SessionId);
        var deliveries = new List<string>();
        Assert.Equal("queued", await ClaudeWaiter.RunNativeAsync(join, capture, deliveries.Add, TimeSpan.FromSeconds(4), default, handler));
        Assert.Single(deliveries); Assert.NotEmpty(keys); Assert.All(keys, id => Assert.Equal("s-native", id));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("removed")]
    [InlineData("ambiguous")]
    public async Task Round1_actual_allocating_open_refuses_mapping_drift_while_waiting_on_binding(string drift)
    {
        using var scope = new NativeScope(RelayHostFixture.Token);
        var configured = Directory.CreateDirectory(Path.Combine(scope.Root, "configured")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(scope.Root, "other")).FullName;
        NativeScope.Git(other, "init", "-q");
        foreach (var checkout in new[] { scope.Checkout, configured })
        {
            if (checkout == configured) NativeScope.Git(checkout, "init", "-q");
            NativeScope.Git(checkout, "remote", "add", "origin", "https://git.example/team/a.git");
        }
        await using var host = new RelayHostFixture(remoteAddress: System.Net.IPAddress.Loopback, settings: new Dictionary<string,string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0", ["Relay:DocCache:Repositories:0:Id"] = "repo",
            ["Relay:DocCache:Repositories:0:ClonePath"] = configured, ["Relay:DocCache:Repositories:0:Fetch"] = "false",
            ["Relay:DocCache:Repositories:1:Id"] = "alternative", ["Relay:DocCache:Repositories:1:ClonePath"] = other,
            ["Relay:DocCache:Repositories:1:Fetch"] = "false", ["Relay:Clients:0:Repositories:1"] = "alternative"
        });
        var preflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = host.CreateClient();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            clone.Headers.Authorization = request.Headers.Authorization;
            var response = await fixture.SendAsync(clone, ct);
            if (body.Contains("nativePreflight", StringComparison.Ordinal) && response.IsSuccessStatusCode) preflight.TrySetResult();
            return response;
        }));
        var capture = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        await using var join = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(http, scope.Options));
        Task<AdapterReply> call;
        await using (var owner = await NativeJoinBinding.AcquireAsync(scope.Folder, "codex", capture.NativeId, Environment.MachineName, "repo", scope.Checkout, default))
        {
            call = join.CallAsync(capture, "after-lock", RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { }), default);
            await preflight.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (drift == "removed") Directory.Move(configured, configured + "-removed");
            else if (drift == "ambiguous") NativeScope.Git(other, "remote", "add", "origin", "https://git.example/team/a.git");
            else NativeScope.Git(configured, "remote", "set-url", "origin", "https://git.example/team/different.git");
        }
        Assert.Equal(RelayErrorCodes.RepositoryDenied, (await call).Code);
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        using var authorized = host.AuthorizedClient();
        var directory = await host.Call(authorized, RelayTools.WorkerList, new { repositoryId = "repo" });
        using var rows = JsonDocument.Parse(directory.GetProperty("dataJson").GetString()!);
        Assert.Empty(rows.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Enrolled_node_delayed_acquisition_announces_only_verified_generation_and_rejects_foreign_records()
    {
        using var scope = new NativeScope();
        using var node = NodeState.Open(Path.Combine(scope.Root, "enrolled"));
        node.SetHub(scope.Options.BaseUri.AbsoluteUri, null);
        node.SaveCredential(new string('a', 64));
        var options = AdapterOptions.FromEnvironment(key => key == "COCERN_NODE_DIR" ? node.Folder : null);
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout, "Display only");
        var preflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0; var wrong = false;
        using var http = new HttpClient(new Handler(async (r, ct) =>
        {
            Assert.Equal(options.Token, r.Headers.Authorization?.Parameter);
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("nativePreflight", out _)) { preflight.TrySetResult(); return Wire(new { repositoryId = "repo" }); }
            if (doc.RootElement.TryGetProperty("nativeJoin", out _))
            {
                opens++;
                return Wire(new { sessionId = "s-node", sessionToken = "node-session" });
            }
            return wrong ? Wire(new { }, false, RelayErrorCodes.SessionDenied) : Wire(new { sessionId = "s-node" });
        }));
        await using var join = new NativeJoin(node.Folder, options, new RelayForwarder(http, options));
        Task<SessionHandle> joining;
        string statePath;
        await using (var lockOwner = await NativeJoinBinding.AcquireAsync(node.Folder, "codex", context.NativeId, Environment.MachineName, "repo", scope.Checkout, default))
        {
            joining = join.EnsureAsync(context, default);
            await preflight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, opens); Assert.False(joining.IsCompleted);
            statePath = lockOwner.StatePath;
        }
        var captured = await joining;
        Assert.Equal("Display only", captured.Label); Assert.Equal(1, opens);
        var announcement = Assert.Single(new NodeChats(node.Folder, [], TimeProvider.System, TimeSpan.FromMinutes(30), TimeSpan.Zero).Read(_ => true));
        Assert.Equal(captured.SessionId, (string?)announcement!["relaySessionId"]);
        Assert.Equal(context.NativeId, (string?)announcement["threadId"]);
        var selected = Assert.Single(NodeRunner.WakeTargets(node.Folder, [new NodeRoute("codex", context.NativeId, captured.SessionId, "repo")], "fixture", x => new CodexWakeDispatcher(x), "node"));
        Assert.Equal(captured.SessionToken, selected.Session.SessionToken);
        // A copy with the wrong filename and a truncated record never become dispatch candidates.
        var alien = Path.Combine(Path.GetDirectoryName(statePath)!, new string('f',64) + ".json");
        NodeState.WritePrivately(alien, File.ReadAllBytes(statePath));
        var state = File.ReadAllBytes(statePath);
        NodeState.WritePrivately(statePath, [123]);
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(node.Folder));
        NodeState.WritePrivately(statePath, state);
        Assert.Equal(captured, Assert.Single(NativeJoinBinding.DispatchSnapshot(node.Folder)));
        // A denied token on a pre-existing own handle cannot advance or adopt another session.
        NodeState.WriteSessionHandle(node.Folder, captured with { SessionToken = "incorrect" });
        wrong = true;
        Assert.Equal(RelayErrorCodes.SessionDenied, (await join.CallAsync(context, "wrong-token", RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { }), default)).Code);
        Assert.True(state.SequenceEqual(File.ReadAllBytes(statePath))); Assert.Equal(1, opens);
        node.SaveCredential(new string('b', 64));
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(node.Folder));
        Assert.False((await join.CallAsync(context, "foreign", RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { }), default)).Ok);
        Assert.Equal(1, opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Round1_route_invalidated_during_final_status_cannot_reserve_or_queue(bool enrolled)
    {
        using var scope = new NativeScope();
        var (folder, options) = ReviewConnection(scope, enrolled);
        if (!enrolled) options = new AdapterOptions(options.BaseUri.AbsoluteUri, options.Token) { ChatOnly = true };
        var capture = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var finalStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (r, ct) =>
        {
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (r.RequestUri!.Segments[^1] == RelayTools.GetStatus && !doc.RootElement.TryGetProperty("attentionDetail", out _))
            { finalStatus.TrySetResult(); await release.Task.WaitAsync(ct); }
            return ReviewReply(doc.RootElement);
        }));
        var forward = new RelayForwarder(http, options);
        await using var join = new NativeJoin(folder, options, forward);
        var handle = await join.EnsureAsync(capture, default);
        var calls = 0;
        var insideQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new HoldingDispatcher(async ct => { calls++; insideQueue.TrySetResult(); await finishQueue.Task.WaitAsync(ct); });
        var watcherForwarder = forward;
        WakeTarget target;
        if (enrolled)
        {
            target = Assert.Single(NodeRunner.WakeTargets(folder, [new NodeRoute("codex", capture.NativeId, handle.SessionId, "repo")], "fixture", _ => dispatcher, "node"));
        }
        else
        {
            var ready = new TaskCompletionSource<(WakeTarget Target, AdapterOptions Options)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var wake = new LocalChatWake(new LocalWakeConnection { Folder = folder, Options = options }, new NodeTransport
            { CodexExecutable = () => "fixture", CodexDispatcher = _ => dispatcher, Locators = [] },
                (runtime, t, _) => { ready.TrySetResult((t, runtime.Options)); return Task.CompletedTask; });
            using var stop = new CancellationTokenSource();
            var running = wake.RunAsync(stop.Token);
            var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            target = result.Target;
            Assert.Equal(options.BaseUri, result.Options.BaseUri);
            Assert.Equal(options.PinnedFingerprint, result.Options.PinnedFingerprint);
            Assert.True(options.Token == result.Options.Token);
            watcherForwarder = new RelayForwarder(http, result.Options);
            Assert.Equal("unknown_tool", (await forward.ForwardAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { }))).Code);
            stop.Cancel(); await running;
        }
        var db = Path.Combine(scope.Root, "review.db");
        using var state = new WakeState(db);
        var wakeup = new WakeReconciler(new(options.BaseUri.AbsoluteUri, "", "", db, Path.Combine(scope.Root, "stop"), []), state, watcherForwarder, target.Dispatcher);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = wakeup.ReconcileAsync(target.Route, target.Session, timeout.Token);
        await finalStatus.Task.WaitAsync(timeout.Token);
        NodeState.WriteSessionHandle(folder, handle with { Chat = new("codex", Guid.NewGuid().ToString("D")) });
        release.TrySetResult();
        Assert.NotEqual("queued", await pending);
        Assert.Equal(0, calls);
        NodeState.WriteSessionHandle(folder, handle);
        var valid = wakeup.ReconcileAsync(target.Route, target.Session, timeout.Token);
        await insideQueue.Task.WaitAsync(timeout.Token);
        try
        {
            using var competing = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using var lease = await NativeJoinBinding.AcquireAsync(folder, "codex", capture.NativeId, Environment.MachineName, "repo", scope.Checkout, competing.Token);
            });
        }
        finally { finishQueue.TrySetResult(); }
        Assert.Equal("queued", await valid);
        Assert.Equal(1, calls);
    }

    private sealed class HoldingDispatcher(Func<CancellationToken, Task> queue) : IWakeDispatcher
    {
        public async Task<bool> QueueAsync(string threadId, string prompt, CancellationToken ct) { await queue(ct); return true; }
    }

    [Fact]
    public async Task Round1_lost_reply_freezes_checked_evidence_and_cannot_change_retry_wire()
    {
        using var scope = new NativeScope();
        const string original = "https://git.example/team/original.git";
        NativeScope.Git(scope.Checkout, "remote", "add", "origin", original);
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var sent = new List<string>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (r, ct) =>
        {
            var wire = await r.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(wire);
            if (doc.RootElement.TryGetProperty("nativeJoin", out var proof))
            {
                var evidence = proof.GetProperty("resolution");
                Assert.True(NativeGitOrigin.ValidCheckoutEvidence(evidence));
                sent.Add(wire);
                if (sent.Count == 1) { arrived.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            }
            return ReviewReply(doc.RootElement);
        }));
        using var sender = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var forward = new RelayForwarder(http, scope.Options);
        await using (var first = new NativeJoin(scope.Folder, scope.Options, forward))
        {
            var opening = first.EnsureAsync(context, sender.Token);
            await arrived.Task.WaitAsync(sender.Token);
            sender.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        }
        NativeScope.Git(scope.Checkout, "remote", "set-url", "origin", "https://git.example/team/changed.git");
        await using var restarted = new NativeJoin(scope.Folder, scope.Options, forward);
        var changed = await restarted.CallAsync(context, "changed", RelayTools.Ack, JsonSerializer.SerializeToElement(new { }), default);
        Assert.False(changed.Ok); Assert.Single(sent);
        NativeScope.Git(scope.Checkout, "remote", "set-url", "origin", original);
        Assert.Equal("s-native", (await restarted.EnsureAsync(context, default)).SessionId);
        Assert.Equal(2, sent.Count);
        Assert.True(sent[0] == sent[1], "The frozen checked open changed across process restart.");
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("duplicate")]
    [InlineData("mixed")]
    [InlineData("forged")]
    public async Task Round1_checked_proof_refuses_malformed_or_mixed_evidence(string form)
    {
        using var scope = new NativeScope();
        await using var binding = await NativeJoinBinding.AcquireAsync(scope.Folder, "claude", Guid.NewGuid().ToString("D"), Environment.MachineName, "repo", scope.Checkout, default);
        var evidence = JsonSerializer.SerializeToElement(new { origin = "https://git.example/team/project" });
        var wire = binding.OpenWireArguments(evidence);
        var guard = new CallGuard(null!, "hub"); var caller = new RelayCaller("worker", false, ["repo"]);
        Assert.True(guard.TryAdmit(caller, RelayTools.SessionOpen, wire, null, out _, out _));
        var mutated = System.Text.Json.Nodes.JsonNode.Parse(wire.GetRawText())!.AsObject();
        if (form == "credential") mutated["nativeJoin"]!["resolution"]!["origin"] = "https://private@host.example/team/project";
        if (form == "forged") mutated[RelayRequestGuard.CheckedNativeResolution] = JsonSerializer.SerializeToNode(evidence);
        if (form == "mixed") mutated["expectedVersion"] = null;
        var raw = mutated.ToJsonString();
        if (form == "duplicate") raw = raw.Replace("\"resolution\":{", "\"resolution\":{\"origin\":null,", StringComparison.Ordinal);
        using var submitted = JsonDocument.Parse(raw);
        Assert.False(guard.TryAdmit(caller, RelayTools.SessionOpen, submitted.RootElement, null, out _, out var refusal));
        Assert.Equal(RelayErrorCodes.InvalidArguments, refusal!.Code);
    }

    private sealed class Meter : ITokenCounter
    {
        public string Encoding => "fixture";
        public int Count(string text) => text.Length;
        public int PrefixLength(string text, int maxTokens) => Math.Min(text.Length, maxTokens);
    }

    [Fact]
    public async Task Round1_preflight_meter_omits_raw_text_for_valid_malformed_and_wrong_tool_calls()
    {
        await using var host = new RelayHostFixture(disableDispatcher: true, configureServices: services => services.AddSingleton<ITokenCounter>(new Meter()));
        var app = host.Services.GetRequiredService<RelayApplication>();
        var caller = new RelayCaller("worker", false, ["repo"]);
        var safe = JsonSerializer.SerializeToElement(new { nativePreflight = new { origin = "https://git.example/team/project" } });
        var unsafeEvidence = JsonSerializer.SerializeToElement(new { nativePreflight = new { origin = "https://user:synthetic-secret@git.example/team/project" } });
        Assert.Equal(RelayErrorCodes.RepositoryDenied, (await app.ExecuteAsync(caller, RelayTools.SessionOpen, safe)).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, (await app.ExecuteAsync(caller, RelayTools.SessionOpen, unsafeEvidence)).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, (await app.ExecuteAsync(caller, RelayTools.WorkerList, unsafeEvidence)).Code);
        await host.Services.GetRequiredService<CoCern.Storage.SqliteStore>().FlushActivityAsync();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = host.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT count(*) FROM activity_log WHERE tool IN ('session_open', 'worker_list') AND request_bytes>0 AND request_tokens IS NULL AND code IN ('InvalidArguments','RepositoryDenied')";
        Assert.Equal(3L, (long)sql.ExecuteScalar()!);
    }

    private sealed class NativeScope : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("co-native-calls-").FullName;
        public string Checkout { get; }
        public AdapterOptions Options { get; }
        public string Folder { get; }
        public NativeScope(string tokenValue = "synthetic-worker", bool admin = false)
        {
            Options = new("http://127.0.0.1:4321", tokenValue, admin: admin);
            Checkout = Directory.CreateDirectory(Path.Combine(Root, "checkout")).FullName;
            Git(Checkout, "init", "-q");
            var token = Path.Combine(Root, "test-token");
            NodeState.WritePrivately(token, System.Text.Encoding.UTF8.GetBytes(Options.Token));
            Folder = LocalWakeConnection.Create(Options, token, Path.Combine(Root, "cache"))!.Folder;
        }
        public static void Git(string directory, params string[] args)
        {
            var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var proc = System.Diagnostics.Process.Start(info)!;
            Assert.True(proc.WaitForExit(10000)); Assert.Equal(0, proc.ExitCode);
        }
        public void Dispose()
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
    [Fact]
    public void Readonly_native_preflight_needs_no_repository_or_mutation_request()
    {
        var guard = new CallGuard(null!, "test-hub");
        var caller = new RelayCaller("worker", false, ["configured"]);
        var request = JsonSerializer.SerializeToElement(new { nativePreflight = new { origin = "https://git.example/team/project" } });
        Assert.True(guard.TryAdmit(caller, RelayTools.SessionOpen, request, null, out var admitted, out var error), error?.Code);
        Assert.NotNull(admitted);
        Assert.False(admitted.Args.ContainsKey("requestId"));
        Assert.False(admitted.Args.ContainsKey("repositoryId"));
    }

    [Fact]
    public async Task Repository_resolution_uses_configured_checkout_or_unique_authorized_origin()
    {
        using var scope = new NativeScope();
        var checkout = scope.Checkout;
        NativeScope.Git(checkout, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-qm", "fixture");
        var worktree = Path.Combine(scope.Root, "linked");
        NativeScope.Git(checkout, "worktree", "add", "--detach", "-q", worktree);
        NativeScope.Git(checkout, "remote", "add", "origin", "https://fake:private@git.example/team/project.git?secret=test#unused");
        var second = Directory.CreateDirectory(Path.Combine(scope.Root, "another", "checkout")).FullName;
        NativeScope.Git(second, "init", "-q");
        NativeScope.Git(second, "remote", "add", "origin", "git@git.example:team/project.git");
        var transport = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        transport.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        DocRepository Repo(string id, string path) => new(id, path, "main", "origin", [], [], null);
        var settings = new DocCacheSettings(true, 0, 2, 32, [Repo("primary", checkout), Repo("other", second)], 1024);
        var registry = new DocRepositoryRegistry(settings, new GitCli(), NullLogger<DocRepositoryRegistry>.Instance, transport);
        var evidence = NativeCheckout.Inspect(worktree);
        Assert.Equal("https://git.example/team/project", evidence.Origin);
        var remote = evidence.Preflight(false).GetRawText();
        Assert.DoesNotContain("private", remote); Assert.DoesNotContain("secret", remote); Assert.DoesNotContain(worktree, remote);
        var caller = new RelayCaller("test", false, ["primary", "other"]);
        var common = NativeCheckout.Hash(evidence.Common!);
        Assert.Equal("primary", await registry.ResolveNativeAsync(caller, NativeCheckout.Hash(evidence.Root), common, evidence.Origin));
        transport.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        Assert.Null(await registry.ResolveNativeAsync(caller, NativeCheckout.Hash(evidence.Root), common, null));
        Assert.Null(await registry.ResolveNativeAsync(caller, null, null, evidence.Origin)); // Authorized clones are ambiguous.
        Assert.Equal("primary", await registry.ResolveNativeAsync(caller with { Repositories = ["primary"] }, null, null, evidence.Origin));
        Assert.Null(await registry.ResolveNativeAsync(caller with { Repositories = ["unrelated"] }, null, null, evidence.Origin));
        NativeScope.Git(second, "remote", "set-url", "origin", "https://git.example/other/project.git");
        Assert.Equal("primary", await registry.ResolveNativeAsync(caller, null, null, evidence.Origin));
        var disabled = new DocRepositoryRegistry(settings with { Enabled = false }, new GitCli(), NullLogger<DocRepositoryRegistry>.Instance, transport);
        Assert.Null(await disabled.ResolveNativeAsync(caller, null, null, evidence.Origin));
        Assert.Equal(2, registry.Repositories.Count);
    }

    [Theory]
    [InlineData("https://git.example/team/../project.git")]
    [InlineData("https://git.example/team%2Fproject.git")]
    [InlineData("file:///C:/work/team/project")]
    [InlineData("ssh://git@git.example:2222/team/project")]
    [InlineData("C:/work/team/project")]
    public void Ambiguous_or_local_origins_are_not_remote_identity(string origin) => Assert.Null(NativeGitOrigin.Canonical(origin));

    [Fact]
    public async Task Configured_non_checkout_subdirectory_never_matches_parent_origin_or_common_directory()
    {
        using var scope = new NativeScope();
        NativeScope.Git(scope.Checkout, "remote", "add", "origin", "https://git.example/parent/project.git");
        var nested = Directory.CreateDirectory(Path.Combine(scope.Checkout, "docs")).FullName;
        var parent = NativeCheckout.Inspect(scope.Checkout);
        var transport = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var settings = new DocCacheSettings(true, 0, 2, 32, [new DocRepository("nested", nested, "main", "origin", [], [], null)], 1024);
        var registry = new DocRepositoryRegistry(settings, new GitCli(), NullLogger<DocRepositoryRegistry>.Instance, transport);
        var caller = new RelayCaller("test", false, ["nested"]);
        transport.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        Assert.Null(await registry.ResolveNativeAsync(caller, NativeCheckout.Hash(parent.Root), NativeCheckout.Hash(parent.Common!), null));
        transport.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        Assert.Null(await registry.ResolveNativeAsync(caller, null, null, parent.Origin));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Bare_transient_http_failure_keeps_pending_intent_for_recovery(int code)
    {
        using var scope = new NativeScope();
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var online = false; var opens = 0; var writes = 0;
        var hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (r, ct) =>
        {
            using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("nativePreflight", out _))
            {
                hit.TrySetResult();
                if (!online) return new((System.Net.HttpStatusCode)code) { Content = new StringContent("temporary gateway failure") };
                return Wire(new { repositoryId = "repo" });
            }
            if (doc.RootElement.TryGetProperty("nativeJoin", out _)) { opens++; return Wire(new { sessionId = "s-recovered", sessionToken = "recovered-token" }); }
            writes++; return Wire(new { });
        }));
        var forwarder = new RelayForwarder(http, scope.Options);
        // The budget also covers the git-based checkout inspection before the first request; on a busy machine a short one ends before any preflight.
        await using (var first = new NativeJoin(scope.Folder, scope.Options, forwarder) { Budget = TimeSpan.FromSeconds(5) })
        {
            var call = first.CallAsync(context, "uncertain", RelayTools.Ack, JsonSerializer.SerializeToElement(new { messageIds = new[] { "m1" } }), default);
            await hit.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False((await call).Ok);
            Assert.Equal(0, opens); Assert.Equal(0, writes);
        }
        Assert.Single(Directory.EnumerateFiles(scope.Folder, "*.json", SearchOption.AllDirectories), p => p.Contains("native-pending-"));
        online = true;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var runtime = new NativeJoin(scope.Folder, scope.Options, forwarder) { ResumeInterval = TimeSpan.FromMilliseconds(25) };
        var resumed = runtime.ResumeAsync(stop.Token);
        while (NativeJoinBinding.DispatchSnapshot(scope.Folder).Count == 0) await Task.Delay(25, stop.Token);
        stop.Cancel(); await resumed;
        Assert.Equal(1, opens); Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Actual_authenticated_host_refuses_before_capture_then_registers_only_own_session()
    {
        using var scope = new NativeScope(RelayHostFixture.Token);
        using var wrong = new NativeScope();
        await using var host = new RelayHostFixture(remoteAddress: System.Net.IPAddress.Loopback, settings: new Dictionary<string,string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = scope.Checkout,
            ["Relay:DocCache:Repositories:0:Fetch"] = "false"
        });
        using var http = host.CreateClient();
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var args = JsonSerializer.SerializeToElement(new { });
        await using var unauthorized = new NativeJoin(wrong.Folder, wrong.Options, new RelayForwarder(http, wrong.Options));
        Assert.False((await unauthorized.CallAsync(context, "first", RelayTools.GetStatus, args, default)).Ok);
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(wrong.Folder));
        await using var join = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(http, scope.Options));
        var neighbor = new SessionHandle("s-unproven", "not-the-chat-token", "repo", "Same title", DateTimeOffset.UtcNow, null, null, Unmapped:true);
        NodeState.WriteSessionHandle(scope.Folder, neighbor);
        var first = await join.EnsureAsync(context, default);
        var result = await join.CallAsync(context, "status", RelayTools.GetStatus, args, default);
        Assert.True(result.Ok, result.Code);
        Assert.Equal(first.SessionId, result.Data!.Value.GetProperty("sessionId").GetString());
        Assert.NotEqual(neighbor.SessionId, first.SessionId);
        Assert.True(NodeState.ReadSessionHandle(NodeState.SessionHandlePath(scope.Folder, neighbor.SessionId))!.Unmapped);
        var denied = await join.CallAsync(context, "wrong-repo", RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "elsewhere" }), default);
        Assert.False(denied.Ok);
        Assert.Equal(first, Assert.Single(NativeJoinBinding.DispatchSnapshot(scope.Folder)));
    }

    [Fact]
    public async Task Persisted_intent_recovers_without_model_call_and_dispatches_exact_chat_without_node()
    {
        using var scope = new NativeScope();
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var online = false;
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("nativePreflight", out _))
            {
                Assert.Single(Directory.EnumerateFiles(scope.Folder, "*.json", SearchOption.AllDirectories), p => p.Contains("native-pending-"));
                attempted.TrySetResult();
                if (!online) throw new HttpRequestException("offline synthetic hub");
                return Wire(new { repositoryId = "repo" });
            }
            return Wire(new { sessionId = "s-offline", sessionToken = "fixture-token" });
        }));
        await using (var startup = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(http, scope.Options)))
        {
            using var deadline = new CancellationTokenSource();
            var registering = startup.EnsureAsync(context, deadline.Token);
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            deadline.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registering);
        }
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        var pending = Assert.Single(Directory.EnumerateFiles(scope.Folder, "*.json", SearchOption.AllDirectories), p => p.Contains("native-pending-"));
        Assert.DoesNotContain(scope.Options.Token, File.ReadAllText(pending));
        online = true;
        await using var runtime = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(http, scope.Options));
        using var stop = new CancellationTokenSource();
        var resumed = runtime.ResumeAsync(stop.Token);
        var actual = new TaskCompletionSource<WakeTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wake = new LocalChatWake(LocalWakeConnection.Read(scope.Folder)!, new NodeTransport { CodexExecutable = () => "fixture-codex", Locators = [] },
            async (_, target, ct) => { actual.TrySetResult(target); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } });
        var watching = wake.RunAsync(stop.Token);
        var captured = await actual.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(context.NativeId, captured.Route.ThreadId);
        Assert.Equal("s-offline", captured.Session.SessionId);
        Assert.Equal("fixture-token", captured.Session.SessionToken);
        Assert.False(File.Exists(pending));
        stop.Cancel(); await Task.WhenAll(resumed, watching).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("""{"nativePreflight":{"origin":"https://git.example/team/project"},"role":"manager"}""")]
    [InlineData("""{"nativePreflight":{"origin":"x","origin":"y"}}""")]
    [InlineData("""{"nativePreflight":{"origin":123}}""")]
    [InlineData("""{"nativePreflight":{"origin":"https://user:bad@git.example/team/project?token=fake"}}""")]
    [InlineData("""{"nativePreflight":{"origin":"https://git.example/team/project"},"expectedVersion":null}""")]
    public void Mixed_or_malformed_preflight_is_never_an_open(string json)
    {
        var guard = new CallGuard(null!, "test-hub");
        using var payload = JsonDocument.Parse(json);
        Assert.False(guard.TryAdmit(new RelayCaller("worker", false, ["configured"]), RelayTools.SessionOpen, payload.RootElement, null, out _, out var error));
        Assert.Equal(RelayErrorCodes.InvalidArguments, error!.Code);
    }
}

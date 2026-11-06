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

    [Theory]
    [InlineData(false)]
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

    [Theory]
    [InlineData("https://git.example/team/../project.git")]
    [InlineData("file:///C:/work/team/project")]
    public void Ambiguous_or_local_origins_are_not_remote_identity(string origin) => Assert.Null(NativeGitOrigin.Canonical(origin));

    [Theory]
    [InlineData(429)]
    public async Task Bare_transient_http_failure_keeps_pending_intent_until_next_stamped_call(int code)
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
        await using var runtime = new NativeJoin(scope.Folder, scope.Options, forwarder);
        await runtime.ResumeAsync(default);
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        Assert.Equal(0, opens); Assert.Equal(0, writes);
        _ = await NativeJoinHook.StampAsync(runtime, context, RelayTools.GetStatus, "recover-first", JsonSerializer.SerializeToElement(new { }), default);
        Assert.Equal(1, opens); Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Classic_native_adoption_keeps_identity_metadata_and_inbox_and_refuses_forged_stale_or_conflicting_proof()
    {
        using var scope = new NativeScope(RelayHostFixture.Token);
        await using var host = new RelayHostFixture(remoteAddress: System.Net.IPAddress.Loopback, settings: new Dictionary<string,string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = scope.Checkout,
            ["Relay:DocCache:Repositories:0:Fetch"] = "false"
        });
        using var http = host.CreateClient();
        var forwarder = new RelayForwarder(http, scope.Options);
        var binding = new { roslyn = "retained-solution", visualStudio = "retained-ide" };
        var opened = await forwarder.ForwardAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new
        {
            repositoryId = "repo", machineId = Environment.MachineName, label = "[FC] Floor Controller", role = "worker", binding,
            requestId = Guid.NewGuid().ToString("N")
        }), default);
        Assert.True(opened.Ok, opened.Code);
        var original = opened.Data!.Value;
        var session = original.GetProperty("sessionId").GetString()!;
        var token = original.GetProperty("sessionToken").GetString()!;
        var capture = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout, "New generic native label");
        var location = new ChatLocation("codex", capture.NativeId);
        var classic = new SessionHandle(session, token, "repo", "[FC] Floor Controller", DateTimeOffset.UtcNow, location, location);
        NodeState.WriteSessionHandle(scope.Folder, classic);
        var sent = await forwarder.ForwardAsync(RelayTools.SendMessage, JsonSerializer.SerializeToElement(new
        { repositoryId = "repo", sessionId = session, sessionToken = token, recipientSessionId = session, text = "keep this inbox", requestId = Guid.NewGuid().ToString("N") }), default);
        Assert.True(sent.Ok, sent.Code);
        AdapterReply beforeInbox = default!;
        for (var delivery = 0; delivery < 100; delivery++)
        {
            beforeInbox = await forwarder.ForwardAsync(RelayTools.CheckInbox,
                JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = session, sessionToken = token, attention = false }), default);
            Assert.True(beforeInbox.Ok, beforeInbox.Code);
            if (beforeInbox.Data!.Value.GetProperty("items").GetArrayLength() > 0) break;
            await Task.Delay(25);
        }
        Assert.Contains(beforeInbox.Data!.Value.GetProperty("items").EnumerateArray(), item => ChatLocators.Text(item, "text") == "keep this inbox");
        var adoptionAttempts = 0;
        using var uncertain = new HttpClient(new Handler(async (request, ct) =>
        {
            var raw = await request.Content!.ReadAsStringAsync(ct);
            using var content = JsonDocument.Parse(raw);
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri)
            { Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json") };
            copy.Headers.Authorization = request.Headers.Authorization;
            var response = await http.SendAsync(copy, ct);
            if (content.RootElement.TryGetProperty("nativeJoin", out _) && ++adoptionAttempts == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Synthetic lost authenticated adoption reply.");
            }
            return response;
        }));
        await using var join = new NativeJoin(scope.Folder, scope.Options, new RelayForwarder(uncertain, scope.Options));
        var result = await join.CallAsync(capture, "adopt-first", RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { attention = false }), default);
        Assert.True(result.Ok, result.Code);
        Assert.True(adoptionAttempts >= 2); // The committed adoption is retried with the same private proof and identity.
        Assert.Equal(session, result.Data!.Value.GetProperty("sessionId").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(binding), result.Data.Value.GetProperty("binding")));
        var adopted = Assert.Single(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        Assert.Equal(classic.SessionId, adopted.SessionId);
        Assert.Equal(classic.SessionToken, adopted.SessionToken);
        Assert.Equal(classic.Label, adopted.Label);
        Assert.Equal(classic.OpenedAt, adopted.OpenedAt);
        var inbox = await join.CallAsync(capture, "adopt-inbox", RelayTools.CheckInbox, JsonSerializer.SerializeToElement(new { attention = false }), default);
        Assert.True(inbox.Ok, inbox.Code);
        Assert.Contains(inbox.Data!.Value.GetProperty("items").EnumerateArray(), item => ChatLocators.Text(item, "text") == "keep this inbox");

        await using var other = await NativeJoinBinding.AcquireAsync(scope.Folder, "codex", Guid.NewGuid().ToString("D"), Environment.MachineName, "repo", scope.Checkout, default);
        var wire = System.Text.Json.Nodes.JsonNode.Parse(other.OpenWireArguments(null).GetRawText())!.AsObject();
        foreach (var proof in new[]
        {
            new { sessionId = session, sessionToken = "forged-token" },
            new { sessionId = Guid.NewGuid().ToString("N"), sessionToken = token },
            new { sessionId = session, sessionToken = token } // Correct credential, different native participant.
        })
        {
            wire["nativeJoin"]!["adopt"] = JsonSerializer.SerializeToNode(proof);
            var refused = await forwarder.ForwardAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(wire), default);
            Assert.False(refused.Ok);
            Assert.Equal(RelayErrorCodes.SessionDenied, refused.Code);
        }
        var workers = await forwarder.ForwardAsync(RelayTools.WorkerList, JsonSerializer.SerializeToElement(new { repositoryId = "repo", includeStale = true }), default);
        Assert.True(workers.Ok, workers.Code);
        var worker = Assert.Single(workers.Data!.Value.GetProperty("items").EnumerateArray());
        Assert.Equal(session, worker.GetProperty("sessionId").GetString());
        Assert.Equal(classic.Label, worker.GetProperty("label").GetString());
        Assert.Equal("worker", worker.GetProperty("role").GetString());
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
    public async Task Persisted_intent_waits_for_stamped_call_then_dispatches_exact_chat_without_node()
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
            var registering = NativeJoinHook.StampAsync(startup, context, RelayTools.GetStatus, "offline-first", JsonSerializer.SerializeToElement(new { }), deadline.Token);
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
        await resumed;
        Assert.Empty(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        Assert.True(File.Exists(pending));
        _ = await NativeJoinHook.StampAsync(runtime, context, RelayTools.GetStatus, "online-first", JsonSerializer.SerializeToElement(new { }), default);
        var actual = new TaskCompletionSource<WakeTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = Directory.CreateDirectory(Path.Combine(scope.Root, "profile")).FullName;
        var candidate = CoCern.Adapter.Setup.ClientSettings.StageNativeCandidate(scope.Folder, "codex", "fixture-helper", clientProfile: profile)!;
        var registration = NativeRegistration.Read(candidate, "codex", scope.Folder, profile)!;
        await using var wake = await LocalChatWake.OpenAsync(scope.Options, tokenFile: null, registration, cacheRoot: Path.Combine(scope.Root, "cache"),
            create: connection => new LocalChatWake(connection, new NodeTransport { CodexExecutable = () => "fixture-codex", Locators = [] },
                async (runtime, target, ct) =>
                {
                    actual.TrySetResult(target);
                    try
                    {
                        await poll.Task.WaitAsync(ct);
                        runtime.Reconciled!(target, "empty");
                        reconciled.TrySetResult();
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException) { }
                }));
        Assert.NotNull(wake);
        Assert.Equal(scope.Folder, wake.Connection.Folder);
        Assert.Null(await LocalChatWake.OpenAsync(new AdapterOptions(scope.Options.BaseUri.AbsoluteUri, scope.Options.Token) { NodeFolder = scope.Folder }, null, registration,
            cacheRoot: Path.Combine(scope.Root, "cache")));
        var watching = wake.RunAsync(stop.Token);
        var captured = await actual.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(context.NativeId, captured.Route.ThreadId);
        Assert.Equal("s-offline", captured.Session.SessionId);
        Assert.Equal("fixture-token", captured.Session.SessionToken);
        Assert.False(File.Exists(pending));
        var handle = Assert.Single(NativeJoinBinding.DispatchSnapshot(scope.Folder));
        await using (var binding = await NativeJoinBinding.AcquireAsync(scope.Folder, "codex", context.NativeId, Environment.MachineName,
            "repo", scope.Checkout, default, existingOnly: true))
            Assert.False(NativeStartupEvidence.HasLiveLocalWake(binding, handle));
        poll.TrySetResult();
        var receipt = Path.Combine(scope.Folder, "native-wake-" + handle.NativeBindingKey + ".json");
        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var binding = await NativeJoinBinding.AcquireAsync(scope.Folder, "codex", context.NativeId, Environment.MachineName,
            "repo", scope.Checkout, default, existingOnly: true))
            Assert.True(NativeStartupEvidence.HasLiveLocalWake(binding, handle));
        Assert.DoesNotContain(scope.Options.Token, File.ReadAllText(receipt));
        Assert.DoesNotContain(captured.Session.SessionToken, File.ReadAllText(receipt));
        stop.Cancel(); await Task.WhenAll(resumed, watching).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(receipt));
    }

    // Security boundary: the proven root travels only inside the private open envelope. A model's arguments cannot name it, no reply or file carries it, and a refused proof sends nothing.
    [Fact]
    public async Task Protected_root_stays_on_the_private_open_wire_and_is_absent_when_the_proof_refuses()
    {
        using var scope = new NativeScope();
        const string proven = @"C:\proven-root";
        var sent = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            sent.Enqueue(body);
            using var document = JsonDocument.Parse(body);
            var args = document.RootElement;
            if (args.TryGetProperty("nativePreflight", out _)) return Wire(new { repositoryId = "repo" });
            if (args.TryGetProperty("nativeJoin", out var join))
            {
                var id = "s-" + join.GetProperty("nativeId").GetString();
                return Wire(new { sessionId = id, sessionToken = "token-" + id });
            }
            return Wire(new { sessionId = args.GetProperty("sessionId").GetString() });
        }));
        var forwarder = new RelayForwarder(http, scope.Options);
        var status = JsonSerializer.SerializeToElement(new { });

        await using var provable = new NativeJoin(scope.Folder, scope.Options, forwarder) { RootProof = (_, _) => proven };
        var context = new NativeHookContext("codex", Guid.NewGuid().ToString("D"), scope.Checkout);
        var reply = await provable.CallAsync(context, "proven", RelayTools.GetStatus, status, default);
        Assert.True(reply.Ok, reply.Code);
        using (var opened = JsonDocument.Parse(Assert.Single(sent, body => body.Contains("protectedRoot"))))
        {
            Assert.Equal(proven, opened.RootElement.GetProperty("nativeJoin").GetProperty("protectedRoot").GetString());
            Assert.False(opened.RootElement.TryGetProperty("protectedRoot", out _));
        }
        Assert.DoesNotContain("protectedRoot", JsonSerializer.Serialize(reply, AdapterReply.JsonOptions));
        foreach (var file in Directory.EnumerateFiles(scope.Folder, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("protectedRoot", File.ReadAllText(file));

        sent.Clear();
        await using var refusing = new NativeJoin(scope.Folder, scope.Options, forwarder) { RootProof = (cwd, root) => ProtectedCheckoutRoot.ProveOrNull(cwd, root, localityProvable: false) };
        reply = await refusing.CallAsync(new("codex", Guid.NewGuid().ToString("D"), scope.Checkout), "refused", RelayTools.GetStatus, status, default);
        Assert.True(reply.Ok, reply.Code); // A refused proof costs the protected scope, not the join.
        Assert.Contains(sent, body => body.Contains("nativeJoin"));
        Assert.DoesNotContain(sent, body => body.Contains("protectedRoot"));

        sent.Clear();
        JsonElement[] forged = [JsonSerializer.SerializeToElement(new { protectedRoot = proven }), JsonSerializer.SerializeToElement(new { nativeJoin = new { protectedRoot = proven } })];
        foreach (var arguments in forged)
        {
            Assert.Equal("native_invocation_conflict", (await provable.CallAsync(context, "forged", RelayTools.GetStatus, arguments, default)).Code);
            Assert.Equal("native_invocation_conflict", (await new ChatCalls(forwarder, null, null, "scope", () => null).CallAsync(RelayTools.SessionOpen, arguments, default)).Code);
        }
        Assert.Empty(sent);
    }
}

using CoCern.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Host;
using Xunit;
using static CoCern.Tests.AdapterTestKit;

namespace CoCern.Tests;

public sealed class NodeTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    public async Task Update_preserves_paused_or_pending_control_and_its_unread_mail(bool paused)
    {
        var database = Path.Combine(root, "control-update-hub.db");
        var download = new TaskCompletionSource<StagedNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new RecordingDispatcher();
        (string SessionId, string Token) receiver = default;
        string credential;
        string controlBefore;
        using var state = NodeState.Open(DataDir);
        // Whole-fixture hang guard includes host setup; each condition keeps its separate 20-second wait.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var host = Hub(databasePath: database))
        {
            var traffic = new HubTraffic();
            var basis = InMemory(host, traffic, TimeSpan.FromMilliseconds(20));
            var transport = new NodeTransport
            {
                JoinCode = basis.JoinCode, ProbeFingerprint = basis.ProbeFingerprint, CreateHandler = basis.CreateHandler, HubHandler = basis.HubHandler,
                Clients = () => [], Locators = [], AnnounceInterval = basis.AnnounceInterval, MachineName = "Node PC",
                CodexExecutable = () => Path.Combine(root, "never-start-codex.exe"), CodexDispatcher = _ => dispatcher, WakeInterval = TimeSpan.FromMilliseconds(20),
                UpdateHost = new(null, [], "win-x64"), Trace = events.Enqueue,
                CreateUpdater = _ => new UpdateFunctionalCoordinator((_, ct) => download.Task.WaitAsync(ct), (_, _) => { prepared.TrySetResult(); throw new IOException("fixture prepare refusal"); })
            };
            var run = NodeRunner.RunAsync(state, InMemoryHub, transport, error, stop.Token);
            try
            {
                await WaitForAsync(() => Count("announced") > 0, run);
                credential = state.ReadCredential()!;
                var sender = await OpenSessionWithTokenAsync(() => host.CreateClient(new() { BaseAddress = InMemoryHub }), credential);
                receiver = await OpenSessionWithTokenAsync(() => host.CreateClient(new() { BaseAddress = InMemoryHub }), credential);
                using var machine = host.CreateClient(new() { BaseAddress = InMemoryHub });
                machine.DefaultRequestHeaders.Authorization = new("Bearer", credential);
                await host.Call(machine, "send_message", new { repositoryId = "repo", sessionId = sender.SessionId, sessionToken = sender.Token, recipientSessionId = receiver.SessionId, requestId = "blocked-mail", text = "mail held by existing control" });
                await WaitForAsync(async () => (await UpdateInboxAsync(host, machine, receiver)).GetProperty("items").GetArrayLength() == 1, run);
                using var admin = AdminClient(host);
                var controlReply = await host.Call(admin, "control_request", new { repositoryId = "repo", sessionId = receiver.SessionId, requestId = Guid.NewGuid().ToString("N"), command = paused ? "pause" : "cancel", actionId = "native-action" });
                Assert.True(controlReply.GetProperty("ok").GetBoolean());
                var status = await UpdateStatusAsync(host, machine, receiver);
                controlBefore = status.GetProperty("control").GetRawText();
                if (paused) Assert.True(status.GetProperty("control").GetProperty("paused").GetBoolean());
                else Assert.NotEmpty(status.GetProperty("control").GetProperty("requests").EnumerateArray());
                NodeState.WriteSessionHandle(DataDir, new(receiver.SessionId, receiver.Token, "repo", "blocked update", DateTimeOffset.UtcNow, Chat: new("codex", Guid.NewGuid().ToString())));
                await WaitForAsync(() => Count("wake-target") > 0, run);
                var package = new NodePackageMetadata(1, "1.0.0", "net10.0", "win-x64", true, [], 1);
                var release = new NodeRelease(1, new string('B', 64), package, 1, new string('B', 64), new string('C', 64));
                download.TrySetResult(new(release, new(new string('b', 64), package, release.AdapterDllSha256, release.ReleaseId), Path.Combine(root, "unused-staged")));
                await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var announced = Count("announced");
                await WaitForAsync(() => Count("announced") >= announced + 2, run);
                Assert.Empty(dispatcher.Threads);
                Assert.Equal(controlBefore, (await UpdateStatusAsync(host, machine, receiver)).GetProperty("control").GetRawText());
                Assert.Equal(1, (await UpdateInboxAsync(host, machine, receiver)).GetProperty("items").GetArrayLength());
                Assert.False(stop.IsCancellationRequested, "The fixture watchdog cannot count as a successful update.");
            }
            finally { stop.Cancel(); Assert.Equal(0, await run); }
        }
        credential = state.ReadCredential()!;
        await using var restarted = Hub(databasePath: database);
        using var reader = restarted.CreateClient(new() { BaseAddress = InMemoryHub });
        reader.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        Assert.Equal(controlBefore, (await UpdateStatusAsync(restarted, reader, receiver)).GetProperty("control").GetRawText());
        Assert.Equal(1, (await UpdateInboxAsync(restarted, reader, receiver)).GetProperty("items").GetArrayLength());
        using var wake = new WakeState(Path.Combine(DataDir, "wake.db"));
        Assert.False(wake.IsUncertain(WakeState.Key(InMemoryHub.AbsoluteUri, "repo", receiver.SessionId)));
    }

    private static async Task<JsonElement> UpdateStatusAsync(RelayHostFixture host, HttpClient client, (string SessionId, string Token) session)
    {
        var envelope = await host.Call(client, "get_status", new { repositoryId = "repo", sessionId = session.SessionId, sessionToken = session.Token });
        using var data = JsonDocument.Parse(envelope.GetProperty("dataJson").GetString()!);
        return data.RootElement.Clone();
    }

    private static async Task<JsonElement> UpdateInboxAsync(RelayHostFixture host, HttpClient client, (string SessionId, string Token) session)
    {
        var envelope = await host.Call(client, "check_inbox", new { repositoryId = "repo", sessionId = session.SessionId, sessionToken = session.Token, attention = false, headersOnly = true, limit = 50 });
        using var data = JsonDocument.Parse(envelope.GetProperty("dataJson").GetString()!);
        return data.RootElement.Clone();
    }

    private sealed class UpdateFunctionalCoordinator(Func<NodeRelease?, CancellationToken, Task<StagedNode?>> stage, Func<StagedNode, CancellationToken, Task> prepare) : INodeUpdateCoordinator
    {
        public Task<StagedNode?> TryStageAsync(NodeRelease? offered, CancellationToken ct) => stage(offered, ct);
        public Task PrepareAsync(StagedNode staged, CancellationToken ct) => prepare(staged, ct);
    }

    [Fact]
    public void Update_context_never_formats_or_serializes_the_machine_credential()
    {
        using var http = new HttpClient();
        var secret = new string('a', 64);
        var context = new NodeUpdateContext("C:/isolated-node", InMemoryHub, http, secret, new(null, [], "win-x64"));
        Assert.DoesNotContain(secret, context.ToString());
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(context));
    }

    private const string AdminToken = "node-admin-test-secret-not-production";
    private const string ProbedFingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string OtherFingerprint = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";
    private static readonly Uri InMemoryHub = new("https://localhost/");
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-node-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter error = new();
    private readonly ConcurrentQueue<string> events = new();

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string DataDir => Path.Combine(root, "node");
    private string NodeJson => Path.Combine(DataDir, "node.json");
    private int Count(string name) => events.Count(x => x == name);

    [Theory]
    [InlineData(true)]
    public async Task Changed_hub_certificate_is_never_retrusted_and_the_node_keeps_running(bool enrolled)
    {
        using var presented = HubCertificate.LoadOrCreate(Path.Combine(root, "f2.pfx"), "hub-pc");
        var port = FreePort();
        var requests = 0;
        await using var stub = await StartTlsStubAsync(presented, port, () => Interlocked.Increment(ref requests));
        var hub = new Uri($"https://127.0.0.1:{port}/");
        using (var seed = NodeState.Open(DataDir))
        {
            seed.SetHub(hub.AbsoluteUri, OtherFingerprint);
            if (enrolled) seed.SaveCredential(new string('c', 64));
        }
        var before = File.ReadAllBytes(NodeJson);

        using var state = NodeState.Open(DataDir);
        // A moment's spoofed name must not take the PC off the hub until the next sign-in: the node keeps trying, but sends nothing.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await NodeRunner.RunAsync(state, hub, Transport(), error, timeout.Token);

        Assert.True(timeout.IsCancellationRequested);
        Assert.Equal(before, File.ReadAllBytes(NodeJson));
        Assert.Contains(CertificateFingerprint.Of(presented), error.ToString());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(error.ToString(), "presented a certificate"));
        Assert.Equal(0, requests);
        Assert.Equal(enrolled ? new string('c', 64) : null, state.ReadCredential());
    }

    [Fact]
    public async Task First_run_enrolls_once_and_announces()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var admin = AdminClient(host);
        using var state = NodeState.Open(DataDir);
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic), error, stop.Token);
            await WaitForAsync(async () => (await DirectoryAsync(admin)).Contains("Node PC"), run);
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        Assert.Equal(1, traffic.Enrolls);
        Assert.Equal([state.MachineId], traffic.EnrolledIds);

        var json = JsonNode.Parse(File.ReadAllText(NodeJson))!;
        Assert.Equal(state.MachineId, json["machineId"]!.GetValue<string>());
        Assert.Equal(InMemoryHub.AbsoluteUri, json["hubUrl"]!.GetValue<string>());
        Assert.Equal(ProbedFingerprint, json["fingerprint"]!.GetValue<string>());
        Assert.True(json["enrolled"]!.GetValue<bool>());
        var credential = state.ReadCredential()!;
        Assert.Matches("^[0-9a-f]{64}$", credential);
        Assert.DoesNotContain(credential, File.ReadAllText(NodeJson));
        Assert.DoesNotContain(credential, error.ToString());
        AssertPrivate(Path.Combine(DataDir, "credential"));

        // A restart with the same typed address is accepted silently and keeps the identity.
        var announces = traffic.Announces;
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, new Uri("https://LOCALHOST"), InMemory(host, traffic), error, stop.Token);
            await WaitForAsync(() => traffic.Announces > announces, run);
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        Assert.Equal(1, traffic.Enrolls);
        Assert.Equal(credential, state.ReadCredential());
        Assert.DoesNotContain("refused", error.ToString());
    }

    [Fact]
    public async Task Revoked_machine_stops_with_a_message()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var admin = AdminClient(host);
        using var state = NodeState.Open(DataDir);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic, TimeSpan.FromMilliseconds(50)), error, timeout.Token);
        await WaitForAsync(() => Count("announced") >= 1, run);
        using var revoke = await admin.PostAsJsonAsync("/api/tools/control_request", new { repositoryId = "repo", requestId = "rv", command = "revoke_machine", machineId = state.MachineId, reason = "test" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(1, await run);
        Assert.Contains("revoked", error.ToString());
        Assert.False(timeout.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true)]
    public async Task Unreadable_credential_never_renews_the_identity_or_deletes_the_secret(bool releaseDuringRetries)
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic), error, stop.Token);
            await WaitForAsync(() => Count("announced") >= 1, run);
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        var credentialPath = Path.Combine(DataDir, "credential");
        var (machineId, credential, before) = (state.MachineId, File.ReadAllBytes(credentialPath), File.ReadAllBytes(NodeJson));
        var announced = Count("announced");

        // Antivirus, backup or an indexer can hold the file open exclusively for a moment.
        var holder = new FileStream(credentialPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var stopSecond = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var second = NodeRunner.RunAsync(state, null, InMemory(host, traffic, credentialAttempts: releaseDuringRetries ? 40 : 4), error, stopSecond.Token);
        try
        {
            if (releaseDuringRetries)
            {
                await Task.Delay(200);
                Assert.False(second.IsCompleted);
                holder.Dispose();
                await WaitForAsync(() => Count("announced") > announced, second);
                stopSecond.Cancel();
                Assert.Equal(0, await second);
            }
            else
            {
                Assert.Equal(1, await second);
                Assert.Contains("cannot be read", error.ToString());
                Assert.Equal(before, File.ReadAllBytes(NodeJson));
            }
        }
        finally { holder.Dispose(); }
        Assert.Equal(1, traffic.Enrolls);
        Assert.Equal(machineId, state.MachineId);
        Assert.Equal(credential, File.ReadAllBytes(credentialPath));
        Assert.DoesNotContain(System.Text.Encoding.ASCII.GetString(credential), error.ToString());
    }

    [Fact]
    public async Task First_https_run_pins_the_presented_certificate_for_every_connection()
    {
        Directory.CreateDirectory(root);
        var (httpPort, httpsPort) = (FreePort(), FreePort());
        await using var app = await StartRelayAsync(httpPort, httpsPort);
        using var admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/") };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var state = NodeState.Open(DataDir);
        var transport = Transport(() => new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/") });
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, new Uri($"https://127.0.0.1:{httpsPort}/"), transport, error, stop.Token);
            await WaitForAsync(async () => Count("watching") >= 1 && (await DirectoryAsync(admin)).Contains("Node PC"), run);
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        using var stored = HubCertificate.LoadOrCreate(Path.Combine(root, "nested", "hub-certificate.pfx"), "unused");
        Assert.Equal(CertificateFingerprint.Of(stored), state.Fingerprint);
        Assert.Equal(CertificateFingerprint.Of(stored), JsonNode.Parse(File.ReadAllText(NodeJson))!["fingerprint"]!.GetValue<string>());
        Assert.True(state.Enrolled);

        // The hint channel accepts only the pinned certificate too.
        var hub = new Uri($"https://127.0.0.1:{httpsPort}/");
        await using (var wrong = RelayHubConnection.Create(new AdapterOptions(hub.AbsoluteUri, state.ReadCredential()!, signalR: true) { PinnedFingerprint = OtherFingerprint }))
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => wrong.StartAsync());
        await using (var pinned = RelayHubConnection.Create(new AdapterOptions(hub.AbsoluteUri, state.ReadCredential()!, signalR: true) { PinnedFingerprint = state.Fingerprint }))
        {
            await pinned.StartAsync();
            await pinned.WatchMachineAsync(CancellationToken.None);
        }
        await app.StopAsync();
    }

    [Fact]
    public void Pinned_options_build_a_pinned_handler_without_redirects_or_cookies()
    {
        var options = new AdapterOptions("https://hub.example:5273/", new string('a', 64)) { PinnedFingerprint = ProbedFingerprint.ToLowerInvariant() };
        Assert.Equal(ProbedFingerprint, options.PinnedFingerprint);
        using var handler = RelayForwarder.CreateHandler(options);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
        using var plain = RelayForwarder.CreateHandler(new AdapterOptions("http://127.0.0.1:5271", "token"));
        Assert.Null(plain.ServerCertificateCustomValidationCallback);
        Assert.Throws<AdapterConfigurationException>(() => new AdapterOptions("https://hub.example/", "token") { PinnedFingerprint = "not-a-fingerprint" });
    }

    [Fact]
    public void Locator_matches_only_the_session_open_result()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (threadA, threadB) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var codex = Path.Combine(root, "codex-sessions");
        // Chat A opened s-one. Chat B opened s-two, then listed the directory (which names s-one) and was told about s-one in text and code output.
        Rollout(codex, threadA, UserText("hello"), CodexTool(threadA, "session_open", OpenReply("s-one")));
        Rollout(codex, threadB, CodexTool(threadB, "session_open", OpenReply("s-two")), CodexTool(threadB, "chat_directory", DirectoryReply("s-one", "s-two")),
            UserText("please message s-one"), CodeOutput(OpenReply("s-one")));
        var codexLocator = new CodexChatLocator(codex);
        Assert.Equal("codex", codexLocator.Provider);
        Assert.Equal(new ChatLocation("codex", threadA), codexLocator.Locate("s-one", openedAt));
        Assert.Equal(new ChatLocation("codex", threadB), codexLocator.Locate("s-two", openedAt));

        var claude = Path.Combine(root, "claude-projects");
        var (sessionA, sessionB) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        Transcript(claude, sessionA, ClaudeUse(sessionA, "u1", "mcp__cocern__session_open"), ClaudeResult(sessionA, "u1", OpenReply("s-one")));
        Transcript(claude, sessionB, ClaudeUse(sessionB, "u2", "mcp__cocern__session_open"), ClaudeResult(sessionB, "u2", OpenReply("s-two")),
            ClaudeUse(sessionB, "u3", "mcp__cocern__chat_directory"), ClaudeResult(sessionB, "u3", DirectoryReply("s-one", "s-two")),
            ClaudeUse(sessionB, "u4", "Bash"), ClaudeResult(sessionB, "u4", OpenReply("s-one")));
        var claudeLocator = new ClaudeChatLocator(claude);
        Assert.Equal("claude", claudeLocator.Provider);
        Assert.Equal(new ChatLocation("claude", sessionA), claudeLocator.Locate("s-one", openedAt));
        Assert.Equal(new ChatLocation("claude", sessionB), claudeLocator.Locate("s-two", openedAt));
    }

    [Fact]
    public async Task Session_handle_file_is_private_and_holds_no_machine_credential()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic), error, stop.Token);
            await WaitForAsync(() => Count("announced") >= 1, run);
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        var credential = state.ReadCredential()!;

        // The node still holds node.lock; the stdio adapter reads the folder without it.
        var chatId = Guid.NewGuid().ToString();
        var variables = new Dictionary<string, string?> { ["COCERN_NODE_DIR"] = DataDir, ["CLAUDE_CODE_SESSION_ID"] = chatId, ["COCERN_ROLE"] = "admin" };
        var options = AdapterOptions.FromEnvironment(key => variables.GetValueOrDefault(key), _ => throw new Xunit.Sdk.XunitException("No token file in node mode."));
        Assert.Equal(InMemoryHub, options.BaseUri);
        Assert.Equal(credential, options.Token);
        Assert.Equal(ProbedFingerprint, options.PinnedFingerprint);
        Assert.Equal(Path.GetFullPath(DataDir), options.NodeFolder);
        Assert.Equal(new ChatLocation("claude", chatId), options.ClientChat);
        Assert.False(options.Admin);

        using var http = new HttpClient(host.Server.CreateHandler());
        var arguments = JsonSerializer.SerializeToElement(new { repositoryId = "repo", machineId = "node-pc", workspaceId = "ws", role = "worker", label = "Handle chat", requestId = "open-1" });
        var reply = await new RelayForwarder(http, options).ForwardAsync("session_open", arguments);
        Assert.True(reply.Ok, reply.Code);
        var (sessionId, sessionToken) = (reply.Data!.Value.GetProperty("sessionId").GetString()!, reply.Data!.Value.GetProperty("sessionToken").GetString()!);
        var handoff = SessionHandoff.For(options, error);
        Assert.NotNull(handoff);
        handoff.Record(arguments, reply);

        var path = Path.Combine(DataDir, "sessions", sessionId + ".json");
        AssertPrivate(path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(credential, text);
        var json = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(["sessionId", "sessionToken", "repositoryId", "label", "openedAt", "client"], json.Select(x => x.Key));
        Assert.Equal(sessionToken, json["sessionToken"]!.GetValue<string>());
        Assert.Equal("repo", json["repositoryId"]!.GetValue<string>());
        Assert.Equal("Handle chat", json["label"]!.GetValue<string>());
        Assert.Equal("claude", json["client"]!["provider"]!.GetValue<string>());
        Assert.Equal(chatId, json["client"]!["chatId"]!.GetValue<string>());
        Assert.True(DateTimeOffset.UtcNow - DateTimeOffset.Parse(json["openedAt"]!.GetValue<string>()) < TimeSpan.FromMinutes(1));
        var handle = NodeState.ReadSessionHandle(path)!;
        Assert.Equal((sessionId, "repo", new ChatLocation("claude", chatId)), (handle.SessionId, handle.RepositoryId, handle.Client));
        Assert.DoesNotContain(sessionToken, handle.ToString());
        Assert.DoesNotContain(sessionToken, error.ToString());
        Assert.DoesNotContain(credential, error.ToString());
    }

    [Fact]
    public void Captured_claude_id_counts_only_when_its_own_transcript_holds_the_result()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var claude = Path.Combine(root, "claude-projects");
        var (stale, holder, verified, fork) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        // The captured ID names an earlier conversation (after /clear or /resume): it exists, even lists the sessions, but never opened them.
        Transcript(claude, stale, ClaudeUse(stale, "d1", "mcp__cocern__chat_directory"), ClaudeResult(stale, "d1", DirectoryReply("s-moved", "s-nowhere")));
        Transcript(claude, holder, ClaudeUse(holder, "o1", "mcp__cocern__session_open"), ClaudeResult(holder, "o1", OpenReply("s-moved")));
        // A verified claim wins even where a forked copy also holds the result.
        Transcript(claude, verified, ClaudeUse(verified, "o2", "mcp__cocern__session_open"), ClaudeResult(verified, "o2", OpenReply("s-verified")));
        Transcript(claude, fork, ClaudeUse(fork, "o2", "mcp__cocern__session_open"), ClaudeResult(fork, "o2", OpenReply("s-verified")));
        foreach (var (id, claim) in new[] { ("s-moved", stale), ("s-nowhere", stale), ("s-verified", verified) })
            NodeState.WriteSessionHandle(DataDir, new SessionHandle(id, "synthetic-token-" + id, "repo", "Chat " + id, openedAt, new ChatLocation("claude", claim)));

        var chats = Announced(Chats([new ClaudeChatLocator(claude)]).Read(_ => true));

        Assert.Equal(("claude", holder, true), Route(chats["s-moved"]));
        Assert.Equal(("unknown", "unmapped", false), Route(chats["s-nowhere"]));
        Assert.Equal(("claude", verified, true), Route(chats["s-verified"]));
        Assert.DoesNotContain(chats.Values, x => x["threadId"]!.GetValue<string>() == stale);
        Assert.Equal(new ChatLocation("claude", holder), Handle("s-moved").Chat);
    }

    [Fact]
    public async Task Node_wakes_its_codex_chats_from_hub_routes_until_the_stop_file_appears()
    {
        var thread = Guid.NewGuid().ToString();
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        var dispatcher = new RecordingDispatcher();
        NodeTransport Waking()
        {
            var transport = InMemory(host, traffic, TimeSpan.FromMilliseconds(50));
            return new NodeTransport
            {
                JoinCode = transport.JoinCode, ProbeFingerprint = transport.ProbeFingerprint, CreateHandler = transport.CreateHandler, HubHandler = transport.HubHandler, MachineName = "Node PC", Clients = () => [], Locators = [],
                AnnounceInterval = transport.AnnounceInterval, FirstRetry = transport.FirstRetry, MaxRetry = transport.MaxRetry, Trace = events.Enqueue,
                CodexExecutable = () => Path.Combine(root, "codex.exe"), CodexDispatcher = _ => dispatcher, WakeInterval = TimeSpan.FromMilliseconds(100)
            };
        }
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, InMemoryHub, Waking(), error, stop.Token);
            // The hub announces only sessions this machine opened, so the chat's session is opened with the node's credential once it has enrolled.
            await WaitForAsync(() => Count("announced") >= 1, run);
            var (session, token) = await OpenSessionWithTokenAsync(() => host.CreateClient(new() { BaseAddress = InMemoryHub }), state.ReadCredential()!);
            NodeState.WriteSessionHandle(DataDir, new SessionHandle(session, token, "repo", "Codex chat", DateTimeOffset.UtcNow.AddMinutes(-1), Chat: new ChatLocation("codex", thread)));
            await WaitForAsync(() => Count("wake-target") >= 1, run);
            Assert.True(File.Exists(Path.Combine(DataDir, "wake.db")));
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        Assert.Equal(("codex", thread, true), Route(JsonNode.Parse(traffic.Accepted!)!["chats"]!.AsArray().Single()!.AsObject()));

        // With the stop file the node keeps announcing but never starts waking.
        File.WriteAllText(Path.Combine(DataDir, "stop"), "");
        events.Clear();
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, InMemoryHub, Waking(), error, stop.Token);
            await WaitForAsync(() => Count("announced") >= 3, run);
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        Assert.Equal(0, Count("waking"));
        Assert.Equal(0, Count("wake-target"));
        Assert.Empty(dispatcher.Threads);
    }

    [Fact]
    public async Task Claude_waiter_can_print_the_inbox_with_the_wake()
    {
        var chatId = Guid.NewGuid().ToString();
        EnrollOnLoopback();
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claude", "synthetic-token-claude", "repo", "Claude chat", DateTimeOffset.UtcNow.AddMinutes(-1), new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        var inbox = new InboxStub(new string('c', 64), "s-claude", "synthetic-token-claude") { Messages = ["m1", "m2"] };
        var printed = new List<string>();
        Assert.Equal("queued", await ClaudeWaiter.RunAsync(DataDir, chatId, printed.Add, TimeSpan.FromSeconds(30), CancellationToken.None, inbox, () => new NoHub(), printInbox: true));
        var output = string.Join("\n", printed);
        // One short header and one line per message replace the fixed prompt, so the chat needs no check_inbox call.
        Assert.Contains("untrusted", output);
        Assert.Contains("ackMessageIds", output);
        Assert.Contains("m1", output);
        Assert.Contains("m2", output);
        Assert.Equal(2, output.Split("UNTRUSTED").Length - 1);
        // The printed messages reached the chat, so the waiter reports them seen (closing their info items) with reads that take no offer.
        Assert.All(inbox.InboxReads, read => Assert.Contains("\"attention\":false", read));
        Assert.Contains(inbox.InboxReads, read => read.Contains("\"seenMessageIds\":[\"m1\",\"m2\"]"));
        Assert.DoesNotContain("Respect existing user authority", output);
        Assert.DoesNotContain("synthetic-token", output);
    }

    private int _wakeMinutes;

    /// <summary>A chat with a session handle on this PC, and a stub inbox for that session.</summary>
    private (string ChatId, InboxStub Inbox) PrintingChat(string repository = "repo")
    {
        var chatId = Guid.NewGuid().ToString();
        EnrollOnLoopback();
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claude", "synthetic-token-claude", repository, "Claude chat", DateTimeOffset.UtcNow.AddMinutes(-1), new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        return (chatId, new InboxStub(new string('c', 64), "s-claude", "synthetic-token-claude"));
    }

    /// <summary>One printed wake, each a minute later than the last so the rate limit never holds it back; every run opens the state database afresh, as a restarted hook does.</summary>
    private async Task<string> PrintedWake(string chatId, InboxStub inbox)
    {
        var printed = new List<string>();
        Assert.Equal("queued", await ClaudeWaiter.RunAsync(DataDir, chatId, printed.Add, TimeSpan.FromSeconds(30), CancellationToken.None, inbox, () => new NoHub(), new ManualClock(DateTimeOffset.UtcNow.AddMinutes(++_wakeMinutes)), printInbox: true));
        return Assert.Single(printed);
    }

    private static string[] MessageLines(string output) => output.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task A_second_wake_prints_only_new_messages_and_says_how_many_it_held_back()
    {
        var (chatId, inbox) = PrintingChat();
        inbox.Messages = ["m1", "m2"];
        Assert.Equal(2, MessageLines(await PrintedWake(chatId, inbox)).Length);
        inbox.Messages = ["m1", "m2", "m3"];
        var second = await PrintedWake(chatId, inbox);
        Assert.StartsWith("- m3:", Assert.Single(MessageLines(second)));
        Assert.Contains("2 earlier unacknowledged messages are not repeated", second);
        Assert.Contains("check_inbox", second);
        // The hub still hears about everything that reached the chat, so a report that failed once is made again.
        Assert.Contains(inbox.InboxReads, read => read.Contains("\"seenMessageIds\":[\"m3\",\"m1\",\"m2\"]"));
        // Acknowledged messages leave the count.
        inbox.Messages = ["m3", "m4"];
        var third = await PrintedWake(chatId, inbox);
        Assert.StartsWith("- m4:", Assert.Single(MessageLines(third)));
        Assert.Contains("1 earlier unacknowledged message is not repeated", third);
    }

    [Fact]
    public async Task A_new_message_behind_a_full_page_of_earlier_ones_is_still_printed()
    {
        var (chatId, inbox) = PrintingChat();
        inbox.PageSize = 2;
        inbox.Messages = ["m1", "m2", "m3", "m4"];
        Assert.Equal(4, MessageLines(await PrintedWake(chatId, inbox)).Length);
        inbox.Messages = ["m1", "m2", "m3", "m4", "m5"];
        var second = await PrintedWake(chatId, inbox);
        Assert.StartsWith("- m5:", Assert.Single(MessageLines(second)));
        Assert.Contains("4 earlier unacknowledged messages", second);
    }

    [Fact]
    public async Task A_chat_that_moves_during_a_read_keeps_one_session_for_that_wake_and_prints_the_new_inbox_whole_on_the_next()
    {
        var (chatId, inbox) = PrintingChat();
        inbox.PageSize = 2;
        inbox.Messages = ["a1", "a2", "a3"];
        inbox.Moved = ("s-new", "synthetic-token-new", ["b1", "b2", "b3"]);
        // Written as the first page is answered, so the second page and the seen report are the calls that could stray to the new session.
        inbox.OnFullRead = () => NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-new", "synthetic-token-new", "repo", "New", DateTimeOffset.UtcNow, new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        var first = await PrintedWake(chatId, inbox);
        Assert.Equal(["a1", "a2", "a3"], MessageLines(first).Select(line => line[2..line.IndexOf(':')]));
        Assert.All(inbox.InboxReads, read => Assert.Contains("\"sessionId\":\"s-claude\"", read));
        Assert.Contains(inbox.InboxReads, read => read.Contains("seenMessageIds"));
        inbox.OnFullRead = null;
        // None of the new session's messages were recorded under the old one, so all of them print once, with none counted as earlier.
        var second = await PrintedWake(chatId, inbox);
        Assert.Equal(["b1", "b2", "b3"], MessageLines(second).Select(line => line[2..line.IndexOf(':')]));
        Assert.DoesNotContain("earlier", second);
    }

    [Fact]
    public async Task A_message_past_the_print_limit_is_not_marked_and_prints_on_a_later_wake()
    {
        var (chatId, inbox) = PrintingChat();
        inbox.Text = _ => new string('x', ClaudeWaiter.MaxPrintedInbox * 2 / 3);
        inbox.Messages = ["m1", "m2"];
        var first = await PrintedWake(chatId, inbox);
        Assert.StartsWith("- m1:", Assert.Single(MessageLines(first)));
        Assert.EndsWith("1 more not shown; check_inbox reads it.", first);
        // Nothing new arrived, so something else has to wake the chat.
        inbox.Attention = "{\"actionable\":1,\"top\":{\"id\":\"a-1\",\"k\":\"info\"},\"wake\":\"a-1\"}";
        var second = await PrintedWake(chatId, inbox);
        Assert.StartsWith("- m2:", Assert.Single(MessageLines(second)));
        Assert.Contains("1 earlier unacknowledged message is not repeated", second);
    }

    [Fact]
    public async Task An_oversized_message_is_not_marked_so_its_pointer_prints_again()
    {
        var (chatId, inbox) = PrintingChat();
        inbox.Oversized = ["big"];
        inbox.Messages = ["small", "big"];
        var first = await PrintedWake(chatId, inbox);
        Assert.Contains("big: (oversized; call check_inbox with partOffset to read it)", first);
        inbox.Attention = "{\"actionable\":1,\"top\":{\"id\":\"a-1\",\"k\":\"info\"},\"wake\":\"a-1\"}";
        var second = await PrintedWake(chatId, inbox);
        Assert.StartsWith("- big: (oversized", Assert.Single(MessageLines(second)));
        Assert.Contains("1 earlier unacknowledged message is not repeated", second);
    }

    [Fact]
    public async Task A_wake_whose_text_never_reached_the_chat_marks_nothing()
    {
        var (chatId, inbox) = PrintingChat();
        inbox.Messages = ["m1"];
        await Assert.ThrowsAsync<IOException>(() => ClaudeWaiter.RunAsync(DataDir, chatId, _ => throw new IOException("lost"), TimeSpan.FromSeconds(30), CancellationToken.None, inbox, () => new NoHub(), new ManualClock(DateTimeOffset.UtcNow.AddMinutes(++_wakeMinutes)), printInbox: true));
        inbox.Messages = ["m1", "m2"];
        Assert.Equal(2, MessageLines(await PrintedWake(chatId, inbox)).Length);
    }

    [Fact]
    public async Task A_failed_inbox_read_falls_back_to_the_fixed_prompt_and_marks_nothing()
    {
        var (chatId, inbox) = PrintingChat();
        inbox.Messages = ["m1"];
        inbox.RefuseFullReads = true;
        Assert.StartsWith("CoCern inbox wake", await PrintedWake(chatId, inbox));
        inbox.RefuseFullReads = false;
        inbox.Messages = ["m1", "m2"];
        Assert.Equal(2, MessageLines(await PrintedWake(chatId, inbox)).Length);
    }

    /// <summary>What a hook leaves behind once it has printed <paramref name="ids"/> for this chat.</summary>
    private void PrintedByHook(string chatId, params string[] ids) =>
        WakeState.RecordPrinted(Path.Combine(DataDir, ClaudeWaiter.WaitersFolder, chatId + ".db"), WakeState.Key("repo", "s-claude"), ids);

    [Theory]
    [InlineData(null)]
    public async Task A_waiter_that_finds_a_hook_printed_everything_does_not_wake_the_chat_with_no_new_messages(string? attention)
    {
        var (chatId, inbox) = PrintingChat();
        inbox.Messages = ["m1"];
        inbox.Attention = attention;
        PrintedByHook(chatId, "m1");
        var delivered = new List<string>();
        var status = await ClaudeWaiter.RunAsync(DataDir, chatId, delivered.Add, TimeSpan.FromSeconds(2), CancellationToken.None, inbox, () => new NoHub(), new ManualClock(DateTimeOffset.UtcNow.AddMinutes(++_wakeMinutes)), printInbox: true, interval: TimeSpan.FromMilliseconds(50));
        Assert.Empty(delivered);
        Assert.Equal("timeout", status);
        // The hub still hears the printed message reached the chat, so its info item closes and stops offering this wake.
        Assert.Contains(inbox.InboxReads, read => read.Contains("\"seenMessageIds\":[\"m1\"]"));
    }

    [Fact]
    public async Task Claude_waiter_follows_its_chat_to_a_newer_session()
    {
        var chatId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        EnrollOnLoopback();
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-old", "synthetic-token-old", "repo", "Old", now.AddHours(-1), new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        var inbox = new InboxStub(new string('c', 64), "s-old", "synthetic-token-old") { Moved = ("s-new", "synthetic-token-new", ["m-new"]) };
        var prompts = new List<string>();
        var waiting = ClaudeWaiter.RunAsync(DataDir, chatId, prompts.Add, TimeSpan.FromSeconds(20), CancellationToken.None, inbox, () => new NoHub(), interval: TimeSpan.FromMilliseconds(50));
        await WaitForAsync(() => File.Exists(Path.Combine(DataDir, "waiters", chatId + ".pid")), waiting);
        // A later Stop hook finds this waiter alive and leaves it running, so only the waiter itself can notice the chat moved.
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-new", "synthetic-token-new", "repo", "New", now, new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        Assert.Equal("queued", await waiting);
        Assert.Single(prompts);
    }

    [Theory]
    [InlineData(true)]
    public async Task A_hook_from_a_new_client_arms_once_a_waiter_left_by_another_client_ends(bool oldClientStillListed)
    {
        var chatId = Guid.NewGuid().ToString();
        using var oldWaiter = StartStandIn();
        try
        {
            SeedWaiter(chatId, oldWaiter);
            var inbox = new InboxStub(new string('c', 64), "s-claude", "synthetic-token-claude") { Messages = ["m1"] };
            var error = new StringWriter();
            var hook = RunHook(chatId, inbox, error, Clients(oldWaiter.Id, oldClientStillListed ? 9002 : null), TimeSpan.FromSeconds(30));
            await Task.Delay(300);
            Assert.False(hook.IsCompleted);
            oldWaiter.Kill(entireProcessTree: true);
            await oldWaiter.WaitForExitAsync();
            Assert.Equal(2, await hook.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Contains("m1", error.ToString());
        }
        finally { if (!oldWaiter.HasExited) oldWaiter.Kill(entireProcessTree: true); }
    }

    /// <summary>A real process that stands in for a running waiter: pid files are checked against the live process table.</summary>
    private static System.Diagnostics.Process StartStandIn() =>
        System.Diagnostics.Process.Start(OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }
            : new System.Diagnostics.ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;

    private void SeedWaiter(string chatId, System.Diagnostics.Process waiter)
    {
        EnrollOnLoopback();
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claude", "synthetic-token-claude", "repo", "Claude chat", DateTimeOffset.UtcNow.AddMinutes(-1), new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        ClaudeWaiter.WritePid(DataDir, chatId, waiter.Id, waiter.StartTime.ToUniversalTime());
    }

    /// <summary>This process runs under client 9001; the waiter runs under <paramref name="waiterClient"/>, or under a client that is gone when null.</summary>
    private static Func<int, ProcessFacts?> Clients(int waiterId, int? waiterClient)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return id => id switch
        {
            9001 or 9002 => new ProcessFacts(id, 1, "claude", t0),
            _ when id == Environment.ProcessId => new ProcessFacts(id, 9001, "dotnet", t0.AddHours(1)),
            _ when id == waiterId => new ProcessFacts(id, waiterClient ?? 9999, "CoCern.Adapter", t0.AddHours(1)),
            _ => null
        };
    }

    private Task<int> RunHook(string chatId, HttpMessageHandler inbox, TextWriter error, Func<int, ProcessFacts?> read, TimeSpan grace) =>
        StopHook.RunAsync(new StringReader($"{{ \"session_id\": \"{chatId}\" }}"), error, DataDir, CancellationToken.None,
            (folder, chat, deliver, limit, ct) => ClaudeWaiter.RunAsync(folder, chat, deliver, limit, ct, inbox, () => new NoHub(), printInbox: true), null, null,
            (folder, chat, ct) => ClaudeWaiter.AwaitOtherClientsWaiterAsync(folder, chat, read, grace, TimeSpan.FromMilliseconds(20), ct));

    [Fact]
    public void An_unreadable_holder_transcript_never_lets_a_fork_win_the_match()
    {
        var codex = Path.Combine(root, "codex-sessions");
        var (holder, fork) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var holderPath = Rollout(codex, holder, CodexTool(holder, "session_open", OpenReply("s-x")));
        Rollout(codex, fork, CodexTool(fork, "session_open", OpenReply("s-x")));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-x", "synthetic-token-x", "repo", "Forked", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var locked = true;
        var nodeChats = Chats([new CodexChatLocator(codex, open: path => locked && string.Equals(path, holderPath, StringComparison.OrdinalIgnoreCase) ? throw new IOException("locked by another process") : ChatLocators.OpenShared(path))]);

        // While the holder cannot be read, the fork is the only holder seen, but the session must stay pending rather than map to it.
        var chats = Announced(nodeChats.Read(_ => true));
        Assert.Equal(("unknown", "unmapped", false), Route(chats["s-x"]));
        Assert.Equal((null, false), (Handle("s-x").Chat, Handle("s-x").Unmapped));

        // Once the holder reads cleanly both transcripts hold the result, which is ambiguous, so the session is never wakeable.
        locked = false;
        chats = Announced(nodeChats.Read(_ => true));
        Assert.False(chats["s-x"]["wakeable"]!.GetValue<bool>());
        Assert.Equal((null, true), (Handle("s-x").Chat, Handle("s-x").Unmapped));
    }

    private void EnrollOnLoopback()
    {
        using var seed = NodeState.Open(DataDir);
        seed.SetHub("http://127.0.0.1:5271/", null);
        seed.SaveCredential(new string('c', 64));
    }

    private sealed class RecordingDispatcher : IWakeDispatcher
    {
        public readonly ConcurrentQueue<string> Threads = new();
        public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken cancellationToken) { Threads.Enqueue(threadId); return Task.FromResult(true); }
    }

    /// <summary>The hub's get_status and check_inbox for one session, checking the bearer and session token on every call.</summary>
    private sealed class InboxStub(string credential, string sessionId, string sessionToken) : HttpMessageHandler
    {
        public volatile string[] Messages = [];
        /// <summary>A second session this stub answers for, with its own inbox.</summary>
        public (string SessionId, string SessionToken, string[] Messages)? Moved;
        /// <summary>The tool and body of every acknowledgment the stub received.</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Acks = new();
        /// <summary>The body of every inbox read the stub received.</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> InboxReads = new();
        /// <summary>The get_status attention block, as JSON; null is a hub with attention off.</summary>
        public string? Attention;
        /// <summary>The most items one inbox page holds.</summary>
        public int PageSize = int.MaxValue;
        /// <summary>A message's text; "UNTRUSTED" for any message it leaves out.</summary>
        public Func<string, string>? Text;
        /// <summary>Messages too large for a page, which come back as a piece.</summary>
        public string[] Oversized = [];
        public string? Sender;
        public string? SentAt;
        /// <summary>Refuses the waiter's full inbox reads while headers-only reads still work.</summary>
        public volatile bool RefuseFullReads;
        /// <summary>Runs as the stub answers each of the waiter's full inbox reads.</summary>
        public volatile Action? OnFullRead;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != credential) return new(HttpStatusCode.Unauthorized);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var args = JsonDocument.Parse(body);
            if (request.RequestUri!.AbsolutePath.EndsWith("/ack")) Acks.Enqueue(body);
            if (request.RequestUri!.AbsolutePath.EndsWith("/check_inbox")) InboxReads.Enqueue(body);
            var asked = args.RootElement.GetProperty("sessionId").GetString();
            var token = args.RootElement.GetProperty("sessionToken").GetString();
            var moved = Moved is { } other && asked == other.SessionId && token == other.SessionToken;
            if (!moved && (asked != sessionId || token != sessionToken)) return new(HttpStatusCode.Forbidden);
            var messages = moved ? Moved!.Value.Messages : Messages;
            var fullRead = request.RequestUri!.AbsolutePath.EndsWith("/check_inbox") && !args.RootElement.TryGetProperty("headersOnly", out _);
            if (fullRead && RefuseFullReads) return new(HttpStatusCode.ServiceUnavailable);
            if (fullRead) OnFullRead?.Invoke();
            var data = request.RequestUri!.AbsolutePath.EndsWith("get_status") ? StatusReply(asked!) : InboxReply(messages, args.RootElement);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = data, version = 0 }), System.Text.Encoding.UTF8, "application/json") };
        }

        private string StatusReply(string asked)
        {
            var status = new JsonObject { ["sessionId"] = asked, ["control"] = new JsonObject { ["paused"] = false, ["requests"] = new JsonArray() } };
            if (Attention is { } attention) status["attention"] = JsonNode.Parse(attention);
            return status.ToJsonString();
        }

        private string InboxReply(string[] messages, JsonElement args)
        {
            var cursor = args.TryGetProperty("cursor", out var from) ? from.GetInt32() : 0;
            var limit = Math.Min(PageSize, args.TryGetProperty("limit", out var asked) ? asked.GetInt32() : int.MaxValue);
            var page = messages.Skip(cursor).Take(limit).ToArray();
            var items = new JsonArray(page.Select(id =>
            {
                var item = new JsonObject { ["messageId"] = id, ["repositoryId"] = "repo" };
                if (Sender is not null) item["senderSessionId"] = Sender;
                if (SentAt is not null) item["sentAt"] = SentAt;
                if (Oversized.Contains(id)) { item["truncated"] = true; item["part"] = "{\"text\":\"aaaa"; item["partOffset"] = 0; }
                else item["text"] = Text?.Invoke(id) ?? "UNTRUSTED";
                return (JsonNode)item;
            }).ToArray());
            var next = cursor + page.Length;
            return new JsonObject { ["items"] = items, ["nextCursor"] = next < messages.Length ? next : null, ["truncated"] = next < messages.Length, ["total"] = messages.Length }.ToJsonString();
        }
    }

    private NodeChats Chats(IChatLocator[] locators, TimeProvider? clock = null) => new(DataDir, locators, clock ?? TimeProvider.System, NodeChats.DefaultLocateWindow, NodeChats.DefaultBudget);

    private SessionHandle Handle(string sessionId) => NodeState.ReadSessionHandle(Path.Combine(DataDir, "sessions", sessionId + ".json"))!;

    private static Dictionary<string, JsonObject> Announced(JsonArray chats) => chats.Select(x => x!.AsObject()).ToDictionary(x => x["relaySessionId"]!.GetValue<string>());

    private static (string, string, bool) Route(JsonObject chat) => (chat["provider"]!.GetValue<string>(), chat["threadId"]!.GetValue<string>(), chat["wakeable"]!.GetValue<bool>());

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Rollout(string codexRoot, string threadId, params string[] lines)
    {
        var folder = Path.Combine(codexRoot, "2026", "10", "30");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"rollout-2026-10-30T10-00-00-{threadId}.jsonl");
        File.WriteAllLines(path, [new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["id"] = threadId } }.ToJsonString(), .. lines]);
        return path;
    }

    private static void Transcript(string claudeRoot, string sessionId, params string[] lines)
    {
        var folder = Path.Combine(claudeRoot, "C--synthetic-project");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, sessionId + ".jsonl"), lines);
    }

    private static string OpenReply(string sessionId) => new JsonObject { ["ok"] = true, ["code"] = "ok", ["data"] = new JsonObject { ["sessionId"] = sessionId, ["sessionToken"] = "synthetic-token-" + sessionId, ["bindingVersion"] = 0 }, ["version"] = 1 }.ToJsonString();

    private static string DirectoryReply(params string[] sessionIds) => new JsonObject
    {
        ["ok"] = true, ["code"] = "ok", ["version"] = 1,
        ["data"] = new JsonObject { ["truncated"] = false, ["items"] = new JsonArray(sessionIds.Select(x => (JsonNode)new JsonObject { ["label"] = "Chat " + x, ["provider"] = "codex", ["machineName"] = "PC", ["relaySessionId"] = x, ["sessionId"] = x, ["repositoryId"] = "repo", ["reachable"] = true }).ToArray()) }
    }.ToJsonString();

    private static string CodexTool(string threadId, string tool, string resultText) => new JsonObject
    {
        ["timestamp"] = "2026-10-30T10:00:01Z", ["type"] = "event_msg",
        ["payload"] = new JsonObject
        {
            ["type"] = "item_completed", ["thread_id"] = threadId, ["turn_id"] = "turn-1",
            ["item"] = new JsonObject
            {
                ["type"] = "McpToolCall", ["id"] = "call-" + tool, ["server"] = "cocern", ["tool"] = tool, ["status"] = "completed", ["arguments"] = new JsonObject { ["repositoryId"] = "repo" },
                ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = resultText }) }
            }
        }
    }.ToJsonString();

    private static string UserText(string text) => new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }) } }.ToJsonString();

    private static string CodeOutput(string text) => new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "custom_tool_call_output", ["call_id"] = "exec-1", ["output"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }) } }.ToJsonString();

    private static string ClaudeUse(string sessionId, string id, string name) => new JsonObject
    {
        ["type"] = "assistant", ["sessionId"] = sessionId,
        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject { ["repositoryId"] = "repo" } }) }
    }.ToJsonString();

    private static string ClaudeResult(string sessionId, string id, string text) => new JsonObject
    {
        ["type"] = "user", ["sessionId"] = sessionId,
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }) },
        ["toolUseResult"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
    }.ToJsonString();

    private NodeTransport Transport(Func<HttpClient>? hub = null) => new()
    {
        Clients = () => [], Locators = [],
        JoinCode = RelayHostFixture.JoinCode,
        MachineName = "Node PC",
        Chats = hub is null ? _ => Task.FromResult(new JsonArray(Chat())) : OwnedChats(hub),
        FirstRetry = TimeSpan.FromMilliseconds(20),
        Trace = events.Enqueue,
        CodexExecutable = () => null
    };

    private NodeTransport InMemory(RelayHostFixture host, HubTraffic traffic, TimeSpan? interval = null, int credentialAttempts = 5) => new()
    {
        Clients = () => [], Locators = [],
        JoinCode = RelayHostFixture.JoinCode,
        CredentialAttempts = credentialAttempts,
        CredentialRetryDelay = TimeSpan.FromMilliseconds(25),
        ProbeFingerprint = (_, _) => Task.FromResult(ProbedFingerprint),
        CreateHandler = (_, _) => traffic.Wrap(host.Server.CreateHandler()),
        HubHandler = () => host.Server.CreateHandler(),
        MachineName = "Node PC",
        Chats = OwnedChats(() => host.CreateClient(new() { BaseAddress = InMemoryHub })),
        AnnounceInterval = interval ?? TimeSpan.FromSeconds(30),
        FirstRetry = TimeSpan.FromMilliseconds(10),
        MaxRetry = TimeSpan.FromMilliseconds(50),
        Trace = events.Enqueue,
        // Never the real %LOCALAPPDATA% lookup, so no test can reach a real codex.exe.
        CodexExecutable = () => null
    };

    /// <summary>
    /// The test chat, under a relay session the node's own machine principal opened: the hub announces only chats whose session the announcing machine owns.
    /// The session is opened with the node's current credential the first time it is needed, and again whenever that credential changes.
    /// </summary>
    private Func<CancellationToken, Task<JsonArray>> OwnedChats(Func<HttpClient> hub)
    {
        string? openedWith = null, session = null;
        return async ct =>
        {
            var credential = File.Exists(Path.Combine(DataDir, "credential")) ? File.ReadAllText(Path.Combine(DataDir, "credential")).Trim() : null;
            if (credential is not null && credential != openedWith)
            {
                session = await OpenSessionAsync(hub, credential, ct);
                openedWith = credential;
            }
            var chat = Chat();
            if (session is not null) chat["relaySessionId"] = session;
            return new JsonArray(chat);
        };
    }

    /// <summary>Opens a relay session in <c>repo</c> as the machine that holds <paramref name="credential"/>; returns its ID and token.</summary>
    private static async Task<string> OpenSessionAsync(Func<HttpClient> hub, string credential, CancellationToken ct = default) => (await OpenSessionWithTokenAsync(hub, credential, ct)).SessionId;

    private static async Task<(string SessionId, string Token)> OpenSessionWithTokenAsync(Func<HttpClient> hub, string credential, CancellationToken ct = default)
    {
        using var client = hub();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await client.PostAsJsonAsync("api/tools/session_open", new { repositoryId = "repo", requestId = Guid.NewGuid().ToString("N") }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        using var data = JsonDocument.Parse(body.GetProperty("dataJson").GetString()!);
        return (data.RootElement.GetProperty("sessionId").GetString()!, data.RootElement.GetProperty("sessionToken").GetString()!);
    }

    private static JsonObject Chat() => new() { ["provider"] = "codex", ["threadId"] = "t1", ["relaySessionId"] = "s-t1", ["repositoryId"] = "repo", ["label"] = "Node chat", ["wakeable"] = true, ["waiterRunning"] = false };

    private static RelayHostFixture Hub(IPAddress? remote = null, string? databasePath = null) => new(databasePath: databasePath, remoteAddress: remote, settings: new Dictionary<string, string?>
    {
        ["Relay:Discovery:Enabled"] = "true",
        ["Relay:Discovery:Repositories:0"] = "repo",
        ["Relay:Clients:1:Token"] = AdminToken,
        ["Relay:Clients:1:PrincipalId"] = "admin-user",
        ["Relay:Clients:1:IsAdministrator"] = "true",
        ["Relay:Clients:1:Repositories:0"] = "repo"
    });

    private static HttpClient AdminClient(RelayHostFixture host)
    {
        var client = host.CreateClient(new() { BaseAddress = InMemoryHub, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    /// <summary>The machine names the chat directory lists for repository <c>repo</c>.</summary>
    private static async Task<string[]> DirectoryAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync("api/tools/chat_directory", new { repositoryId = "repo" });
        if (!response.IsSuccessStatusCode) return [];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var data = JsonDocument.Parse(body.GetProperty("dataJson").GetString()!);
        return DirectoryMachineNames(data.RootElement);
    }

    private static string[] DirectoryMachineNames(JsonElement directory) => directory.GetProperty("items").EnumerateArray()
        .Where(x => x.TryGetProperty("machineName", out var name) && name.ValueKind == JsonValueKind.String)
        .Select(x => x.GetProperty("machineName").GetString()!).ToArray();

    private static async Task WaitForAsync(Func<bool> condition, Task? run = null) => await WaitForAsync(() => Task.FromResult(condition()), run);

    private static async Task WaitForAsync(Func<Task<bool>> condition, Task? run = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition() && DateTime.UtcNow < deadline)
        {
            if (run is { IsCompleted: true }) await run;
            await Task.Delay(25);
        }
        Assert.True(await condition());
    }

    /// <summary>A bare HTTPS listener presenting <paramref name="certificate"/>; it counts any request that gets past TLS.</summary>
    private static async Task<WebApplication> StartTlsStubAsync(X509Certificate2 certificate, int port, Action request)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.Run(context => { request(); return context.Response.WriteAsync("{}"); });
        await app.StartAsync();
        return app;
    }

    private async Task<WebApplication> StartRelayAsync(int httpPort, int httpsPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{httpPort}",
            ["Relay:DatabasePath"] = Path.Combine(root, "relay.db"),
            ["Relay:SiloPort"] = FreePort().ToString(),
            ["Relay:GatewayPort"] = FreePort().ToString(),
            ["Relay:ClusterId"] = Guid.NewGuid().ToString("N"),
            ["Relay:Clients:0:Token"] = AdminToken,
            ["Relay:Clients:0:PrincipalId"] = "admin-user",
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Clients:0:Repositories:0"] = "repo",
            ["Relay:Discovery:Enabled"] = "true",
            ["Relay:Discovery:Repositories:0"] = "repo",
            ["Relay:Network:Enabled"] = "true",
            ["Relay:Network:HttpsPort"] = httpsPort.ToString(),
            ["Relay:Local:HttpsPort"] = FreePort().ToString(),
            ["Relay:Network:CertificatePath"] = Path.Combine("nested", "hub-certificate.pfx"),
            ["Relay:Discovery:JoinCode"] = RelayHostFixture.JoinCode
        });
        builder.AddRelayHost();
        var app = builder.Build();
        app.UseRelayPipeline();
        app.MapRelayEndpoints();
        await app.StartAsync();
        return app;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Counts discovery calls on the way to the in-memory hub and can simulate an outage or a rejected credential.</summary>
    private sealed class HubTraffic
    {
        public int Enrolls, Announces, Failed, FailAnnounces, UnauthorizedAnnounces, ThrottledAnnounces;
        private readonly ConcurrentQueue<string> enrolledIds = new();
        public string[] EnrolledIds => enrolledIds.ToArray();
        private string? accepted;
        /// <summary>The body of the last announcement the hub accepted.</summary>
        public string? Accepted => Volatile.Read(ref accepted);

        public HttpMessageHandler Wrap(HttpMessageHandler inner) => new Counter(this, inner);

        private static bool Take(ref int counter)
        {
            while (true)
            {
                var value = Volatile.Read(ref counter);
                if (value <= 0) return false;
                if (Interlocked.CompareExchange(ref counter, value - 1, value) == value) return true;
            }
        }

        private sealed class Counter(HubTraffic traffic, HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/api/discovery/enroll":
                        Interlocked.Increment(ref traffic.Enrolls);
                        traffic.enrolledIds.Enqueue(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["machineId"]!.GetValue<string>());
                        break;
                    case "/api/discovery/announce":
                        Interlocked.Increment(ref traffic.Announces);
                        if (Take(ref traffic.FailAnnounces)) { Interlocked.Increment(ref traffic.Failed); throw new HttpRequestException("simulated outage"); }
                        if (Take(ref traffic.UnauthorizedAnnounces)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                        if (Take(ref traffic.ThrottledAnnounces)) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                        var response = await base.SendAsync(request, cancellationToken);
                        if (response.IsSuccessStatusCode) Volatile.Write(ref traffic.accepted, body);
                        return response;
                }
                return await base.SendAsync(request, cancellationToken);
            }
        }
    }
}

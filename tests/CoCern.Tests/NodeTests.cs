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

namespace CoCern.Tests;

public sealed class NodeTests : IDisposable
{
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
    [InlineData(false)]
    public async Task Changed_hub_certificate_stops_the_node_without_retrusting(bool enrolled)
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var code = await NodeRunner.RunAsync(state, hub, Transport(), error, timeout.Token);

        Assert.Equal(4, code);
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(before, File.ReadAllBytes(NodeJson));
        Assert.Contains("fingerprint", error.ToString());
        Assert.Contains(HubCertificate.Fingerprint(presented), error.ToString());
        Assert.Equal(0, requests);
        Assert.Equal(enrolled ? new string('c', 64) : null, state.ReadCredential());
    }

    [Fact]
    public void Second_node_on_the_same_data_dir_fails_fast()
    {
        var first = NodeState.Open(DataDir);
        var machineId = first.MachineId;
        var clock = Stopwatch.StartNew();
        var refused = Assert.Throws<AdapterConfigurationException>(() => NodeState.Open(DataDir));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), clock.Elapsed.ToString());
        Assert.Contains("already running", refused.Message);
        first.Dispose();
        using var again = NodeState.Open(DataDir);
        Assert.Equal(machineId, again.MachineId);
        Assert.Matches("^[0-9a-f]{32}$", machineId);
    }

    [Fact]
    public async Task Unreachable_hub_keeps_identity_and_does_not_reenroll()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using var stop = new CancellationTokenSource();
        var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic, TimeSpan.FromMilliseconds(20)), error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 1, run);
        var machineId = state.MachineId;
        var credential = state.ReadCredential();

        Volatile.Write(ref traffic.FailAnnounces, 3);
        await WaitForAsync(() => Volatile.Read(ref traffic.Failed) == 3, run);
        var successes = Count("announced");
        await WaitForAsync(() => Count("announced") > successes, run);
        stop.Cancel();

        Assert.Equal(0, await run);
        Assert.Equal(1, traffic.Enrolls);
        Assert.Equal(machineId, state.MachineId);
        Assert.Equal(credential, state.ReadCredential());
        Assert.Contains("unreachable", error.ToString());
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
    public async Task Route_change_hint_triggers_an_announcement_between_intervals()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using var stop = new CancellationTokenSource();
        var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic, TimeSpan.FromMinutes(10)), error, stop.Token);
        await WaitForAsync(() => Count("watching") >= 1, run);
        await WaitForAsync(() => Count("announced") >= 2, run);
        var announces = traffic.Announces;

        // Something else changes this machine's routes; the hub hints the machine group and the node refreshes long before its interval.
        using var machine = host.CreateClient(new() { BaseAddress = InMemoryHub });
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.ReadCredential());
        using var changed = await machine.PostAsJsonAsync("/api/discovery/announce", new { machineName = "Node PC", clients = Array.Empty<string>(), chats = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await WaitForAsync(() => Count("hint") >= 1 && traffic.Announces > announces, run);
        stop.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task Unauthorized_then_conflict_keeps_the_identity_and_credential_and_asks_for_an_administrator()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using var stop = new CancellationTokenSource();
        var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic, TimeSpan.FromMilliseconds(20)), error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 1, run);
        var machineId = state.MachineId;
        var credentialFile = Path.Combine(DataDir, "credential");
        var bytes = File.ReadAllBytes(credentialFile);

        // Twice, so the message must not repeat: each 401 leads to one enrollment attempt, which the hub answers 409 because it still holds the ID.
        Volatile.Write(ref traffic.UnauthorizedAnnounces, 2);
        await WaitForAsync(() => traffic.Enrolls == 3, run);
        var successes = Count("announced");
        await WaitForAsync(() => Count("announced") > successes, run);
        stop.Cancel();
        Assert.Equal(0, await run);

        // A 409 after a 401 is ambiguous (the hub may be faulty), so nothing is renewed or rewritten.
        Assert.Equal([machineId, machineId, machineId], traffic.EnrolledIds);
        Assert.Equal(machineId, state.MachineId);
        Assert.Equal(bytes, File.ReadAllBytes(credentialFile));
        Assert.Equal(machineId, JsonNode.Parse(File.ReadAllText(NodeJson))!["machineId"]!.GetValue<string>());
        Assert.Single(error.ToString().Split('\n'), line => line.Contains("revoke", StringComparison.OrdinalIgnoreCase) && line.Contains("setup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Throttled_announcement_backs_off_and_keeps_the_identity_without_a_revoke_message()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using var stop = new CancellationTokenSource();
        var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic, TimeSpan.FromMilliseconds(20)), error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 1, run);
        var machineId = state.MachineId;
        var credentialFile = Path.Combine(DataDir, "credential");
        var bytes = File.ReadAllBytes(credentialFile);
        var enrolls = traffic.Enrolls;

        Volatile.Write(ref traffic.ThrottledAnnounces, 3);
        await WaitForAsync(() => Volatile.Read(ref traffic.ThrottledAnnounces) == 0, run);
        var successes = Count("announced");
        await WaitForAsync(() => Count("announced") > successes, run);
        stop.Cancel();
        Assert.Equal(0, await run);

        Assert.Equal(enrolls, traffic.Enrolls);
        Assert.Equal(machineId, state.MachineId);
        Assert.Equal(bytes, File.ReadAllBytes(credentialFile));
        Assert.DoesNotContain("revoke", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enrolling again", error.ToString(), StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task Missing_credential_after_enrollment_enrolls_under_a_new_machine_id()
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
        var first = state.MachineId;
        File.Delete(Path.Combine(DataDir, "credential"));
        var announced = Count("announced");
        using (var stop = new CancellationTokenSource())
        {
            var run = NodeRunner.RunAsync(state, null, InMemory(host, traffic), error, stop.Token);
            await WaitForAsync(() => Count("enrolled") == 2 && Count("announced") > announced, run);
            stop.Cancel();
            Assert.Equal(0, await run);
        }
        Assert.Equal(2, traffic.Enrolls);
        Assert.NotEqual(first, state.MachineId);
        Assert.Equal([first, state.MachineId], traffic.EnrolledIds);
        Assert.NotNull(state.ReadCredential());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
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
    public async Task Hub_address_is_fixed_once_enrolled_and_required_on_first_run()
    {
        using (var empty = NodeState.Open(Path.Combine(root, "empty")))
        {
            Assert.Equal(1, await NodeRunner.RunAsync(empty, null, Transport(), error, CancellationToken.None));
            Assert.Contains("--hub", error.ToString());
        }

        var hub = new Uri("https://127.0.0.1:1/");
        using (var seed = NodeState.Open(DataDir)) { seed.SetHub(hub.AbsoluteUri, ProbedFingerprint); seed.SaveCredential(new string('d', 64)); }
        var before = File.ReadAllBytes(NodeJson);
        using var state = NodeState.Open(DataDir);
        Assert.Equal(1, await NodeRunner.RunAsync(state, new Uri("https://127.0.0.2:1/"), Transport(), error, CancellationToken.None));
        Assert.Contains("already enrolled", error.ToString());
        Assert.Equal(before, File.ReadAllBytes(NodeJson));

        // The same address, typed differently, is accepted silently.
        var output = error.ToString().Length;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, await NodeRunner.RunAsync(state, new Uri("https://127.0.0.1:1"), Transport(), error, stop.Token));
        Assert.DoesNotContain("already enrolled", error.ToString()[output..]);
        Assert.Equal(before, File.ReadAllBytes(NodeJson));
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
        Assert.Equal(HubCertificate.Fingerprint(stored), state.Fingerprint);
        Assert.Equal(HubCertificate.Fingerprint(stored), JsonNode.Parse(File.ReadAllText(NodeJson))!["fingerprint"]!.GetValue<string>());
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
    public async Task Loopback_http_hub_is_never_probed_or_pinned()
    {
        await using var host = Hub(IPAddress.Loopback);
        var traffic = new HubTraffic();
        var pins = new ConcurrentQueue<string?>();
        using var state = NodeState.Open(DataDir);
        var transport = new NodeTransport
        {
            JoinCode = RelayHostFixture.JoinCode,
            ProbeFingerprint = (_, _) => throw new InvalidOperationException("A loopback HTTP hub must not be probed."),
            CreateHandler = (fingerprint, _) => { pins.Enqueue(fingerprint); return traffic.Wrap(host.Server.CreateHandler()); },
            HubHandler = () => host.Server.CreateHandler(),
            MachineName = "Node PC",
            FirstRetry = TimeSpan.FromMilliseconds(10),
            Trace = events.Enqueue,
            CodexExecutable = () => null
        };
        using var stop = new CancellationTokenSource();
        var run = NodeRunner.RunAsync(state, new Uri("http://127.0.0.1:5271"), transport, error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 1, run);
        stop.Cancel();
        Assert.Equal(0, await run);
        // The announce client and the wake watcher's client: neither is pinned.
        Assert.NotEmpty(pins);
        Assert.All(pins, Assert.Null);
        Assert.Null(state.Fingerprint);
        Assert.True(state.Enrolled);
        Assert.Equal("http://127.0.0.1:5271/", state.HubUrl);
    }

    [Fact]
    public void Clients_are_detected_by_their_user_folders()
    {
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(Path.Combine(profile, ".codex"));
        Assert.Equal(["codex"], NodeRunner.DetectClients(profile));
        Directory.CreateDirectory(Path.Combine(profile, ".claude"));
        Assert.Equal(["codex", "claude"], NodeRunner.DetectClients(profile));
        Assert.Empty(NodeRunner.DetectClients(Path.Combine(root, "nobody")));
    }

    [Fact]
    public void Retry_backoff_doubles_and_caps_at_60_seconds()
    {
        var delay = TimeSpan.FromSeconds(1);
        var seen = new List<double>();
        for (var i = 0; i < 9; i++) { seen.Add(delay.TotalSeconds); delay = NodeRunner.NextRetry(delay, NodeTransport.DefaultMaxRetry); }
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60], seen);
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
    public async Task Ambiguous_or_missing_match_is_announced_unwakeable()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var codex = Path.Combine(root, "codex-sessions");
        var (located, twinA, twinB, old) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        Rollout(codex, located, CodexTool(located, "session_open", OpenReply("s-located")));
        Rollout(codex, twinA, CodexTool(twinA, "session_open", OpenReply("s-twin")));
        Rollout(codex, twinB, CodexTool(twinB, "session_open", OpenReply("s-twin")));
        // A rollout last written well before the session opened cannot hold its result.
        File.SetLastWriteTimeUtc(Rollout(codex, old, CodexTool(old, "session_open", OpenReply("s-old"))), openedAt.UtcDateTime.AddMinutes(-10));
        var locator = new CodexChatLocator(codex);
        Assert.Equal(new ChatLocation("codex", located), locator.Locate("s-located", openedAt));
        Assert.Null(locator.Locate("s-twin", openedAt));
        Assert.Null(locator.Locate("s-missing", openedAt));
        Assert.Null(locator.Locate("s-old", openedAt));
        Assert.Null(new ClaudeChatLocator(Path.Combine(root, "no-claude")).Locate("s-located", openedAt));

        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        var claudeChat = Guid.NewGuid().ToString();
        var claude = Path.Combine(root, "claude-projects");
        Transcript(claude, claudeChat, ClaudeUse(claudeChat, "u1", "mcp__cocern__session_open"), ClaudeResult(claudeChat, "u1", OpenReply("s-claude")));
        foreach (var (id, client) in new[] { ("s-located", (ChatLocation?)null), ("s-twin", null), ("s-missing", null), ("s-claude", new ChatLocation("claude", claudeChat)) })
            NodeState.WriteSessionHandle(DataDir, new SessionHandle(id, "synthetic-token-" + id, "repo", "Chat " + id, openedAt, client));
        using var stop = new CancellationTokenSource();
        var transport = InMemory(host, traffic, TimeSpan.FromMilliseconds(50));
        var run = NodeRunner.RunAsync(state, InMemoryHub, new NodeTransport
        {
            JoinCode = transport.JoinCode, ProbeFingerprint = transport.ProbeFingerprint, CreateHandler = transport.CreateHandler, HubHandler = transport.HubHandler, MachineName = "Node PC",
            Locators = [locator, new ClaudeChatLocator(claude)], AnnounceInterval = transport.AnnounceInterval, FirstRetry = transport.FirstRetry, MaxRetry = transport.MaxRetry, Trace = events.Enqueue,
            CodexExecutable = () => Path.Combine(root, "codex.exe"), CodexDispatcher = _ => new RecordingDispatcher()
        }, error, stop.Token);
        await WaitForAsync(() => traffic.Accepted is not null && Count("announced") >= 2, run);
        stop.Cancel();
        Assert.Equal(0, await run);

        var accepted = traffic.Accepted!;
        Assert.DoesNotContain("synthetic-token", accepted);
        Assert.DoesNotContain("sessionToken", accepted);
        var chats = JsonNode.Parse(accepted)!["chats"]!.AsArray().Select(x => x!.AsObject()).ToDictionary(x => x["relaySessionId"]!.GetValue<string>());
        Assert.Equal(4, chats.Count);
        Assert.Equal(("codex", located, true), Route(chats["s-located"]));
        Assert.Equal(("claude", claudeChat, true), Route(chats["s-claude"]));
        Assert.False(chats["s-twin"]["wakeable"]!.GetValue<bool>());
        Assert.False(chats["s-missing"]["wakeable"]!.GetValue<bool>());
        Assert.All(chats.Values, x => Assert.Equal("repo", x["repositoryId"]!.GetValue<string>()));
        Assert.Equal("Chat s-twin", chats["s-twin"]["label"]!.GetValue<string>());
        Assert.Equal(CoCern.Domain.DiscoveryLimits.MaxChatsPerMachine, NodeChats.MaxChats);
        // These sessions were never opened by the node's machine, so the hub refuses them; each refusal is reported once, never with a token.
        Assert.Single(error.ToString().Split('\n'), line => line.Contains("relay session 's-twin'"));
        Assert.DoesNotContain("synthetic-token", error.ToString());
        Assert.True(Handle("s-twin").Unmapped);
        Assert.Equal((null, false), (Handle("s-missing").Chat, Handle("s-missing").Unmapped));
    }

    [Fact]
    public async Task Denied_repository_chats_are_left_out_and_reported_once()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-allowed", "synthetic-token-a", "repo", "Allowed chat", openedAt, new ChatLocation("claude", Guid.NewGuid().ToString())));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-denied", "synthetic-token-d", "elsewhere", "Denied chat", openedAt, new ChatLocation("claude", Guid.NewGuid().ToString())));
        using var stop = new CancellationTokenSource();
        var transport = InMemory(host, traffic, TimeSpan.FromMilliseconds(30));
        var run = NodeRunner.RunAsync(state, InMemoryHub, new NodeTransport
        {
            JoinCode = transport.JoinCode, ProbeFingerprint = transport.ProbeFingerprint, CreateHandler = transport.CreateHandler, HubHandler = transport.HubHandler, MachineName = "Node PC",
            Locators = [], AnnounceInterval = transport.AnnounceInterval, FirstRetry = transport.FirstRetry, MaxRetry = transport.MaxRetry, Trace = events.Enqueue, CodexExecutable = () => null
        }, error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 4, run);
        stop.Cancel();
        Assert.Equal(0, await run);

        var chats = JsonNode.Parse(traffic.Accepted!)!["chats"]!.AsArray();
        Assert.Equal("s-allowed", Assert.Single(chats)!["relaySessionId"]!.GetValue<string>());
        Assert.Single(error.ToString().Split('\n'), line => line.Contains("elsewhere"));
        Assert.DoesNotContain("synthetic-token", error.ToString());
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
    public void Explicit_environment_keeps_existing_behaviour()
    {
        using (var seed = NodeState.Open(DataDir))
        {
            seed.SetHub(InMemoryHub.AbsoluteUri, ProbedFingerprint);
            seed.SaveCredential(new string('c', 64));
        }
        using var held = NodeState.Open(DataDir);
        var cases = new[]
        {
            new Dictionary<string, string?> { ["COCERN_TOKEN"] = "env-token" },
            new Dictionary<string, string?> { ["COCERN_TOKEN_FILE"] = "token-file" },
            new Dictionary<string, string?> { ["COCERN_URL"] = "https://hub.example/", ["COCERN_TOKEN"] = "env-token" }
        };
        foreach (var variables in cases)
        {
            variables["COCERN_NODE_DIR"] = DataDir;
            variables["CLAUDE_CODE_SESSION_ID"] = Guid.NewGuid().ToString();
            var options = AdapterOptions.FromEnvironment(key => variables.GetValueOrDefault(key), _ => "file-token");
            Assert.Equal(variables.ContainsKey("COCERN_TOKEN_FILE") ? "file-token" : "env-token", options.Token);
            Assert.Equal(new Uri(variables.GetValueOrDefault("COCERN_URL") ?? AdapterOptions.DefaultUrl), options.BaseUri);
            Assert.Null(options.PinnedFingerprint);
            Assert.Null(options.NodeFolder);
            Assert.Null(options.ClientChat);
            Assert.Null(SessionHandoff.For(options, error));
        }
        // COCERN_URL alone still means "no token", never node credentials.
        var urlOnly = new Dictionary<string, string?> { ["COCERN_URL"] = "https://hub.example/", ["COCERN_NODE_DIR"] = DataDir };
        Assert.Contains("COCERN_TOKEN", Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(key => urlOnly.GetValueOrDefault(key))).Message);
        // Without enrolled node state the error is the same as before.
        var empty = new Dictionary<string, string?> { ["COCERN_NODE_DIR"] = Path.Combine(root, "not-a-node") };
        Assert.Contains("COCERN_TOKEN", Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(key => empty.GetValueOrDefault(key))).Message);
        Assert.False(Directory.Exists(Path.Combine(root, "not-a-node")));
        Assert.False(Directory.Exists(Path.Combine(DataDir, "sessions")));
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
    public void Handle_older_than_the_locate_window_is_not_scanned_and_is_unwakeable()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var codex = Path.Combine(root, "codex-sessions");
        var thread = Guid.NewGuid().ToString();
        Rollout(codex, thread, CodexTool(thread, "session_open", OpenReply("s-late")));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-late", "synthetic-token-late", "repo", "Late chat", openedAt));
        var opens = new ConcurrentDictionary<string, int>();
        var clock = new ManualClock(openedAt + NodeChats.DefaultLocateWindow + TimeSpan.FromMinutes(1));
        var chats = Chats([new CodexChatLocator(codex, open: Counting(opens))], clock);

        Assert.Equal(("unknown", "unmapped", false), Route(Announced(chats.Read(_ => true))["s-late"]));
        Assert.Empty(opens);
        Assert.True(Handle("s-late").Unmapped);
        // Permanently unmapped: a later cycle, or a restart, never scans for it.
        Assert.False(Announced(Chats([new CodexChatLocator(codex, open: Counting(opens))]).Read(_ => true))["s-late"]["wakeable"]!.GetValue<bool>());
        Assert.Empty(opens);
    }

    [Fact]
    public void Located_result_survives_a_restart_without_rescanning()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var codex = Path.Combine(root, "codex-sessions");
        var thread = Guid.NewGuid().ToString();
        Rollout(codex, thread, CodexTool(thread, "session_open", OpenReply("s-kept")));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-kept", "synthetic-token-kept", "repo", "Kept chat", openedAt));
        var first = new ConcurrentDictionary<string, int>();
        Assert.Equal(("codex", thread, true), Route(Announced(Chats([new CodexChatLocator(codex, open: Counting(first))]).Read(_ => true))["s-kept"]));
        Assert.Single(first);

        var path = Path.Combine(DataDir, "sessions", "s-kept.json");
        AssertPrivate(path);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("synthetic-token-kept", json["sessionToken"]!.GetValue<string>());
        Assert.Equal(thread, json["chat"]!["threadId"]!.GetValue<string>());

        // A restarted node gets new locators with no memory; the handle alone answers.
        var second = new ConcurrentDictionary<string, int>();
        Assert.Equal(("codex", thread, true), Route(Announced(Chats([new CodexChatLocator(codex, open: Counting(second))]).Read(_ => true))["s-kept"]));
        Assert.Empty(second);
    }

    [Fact]
    public void Each_transcript_is_read_once_per_cycle_for_all_pending_handles()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var codex = Path.Combine(root, "codex-sessions");
        var (one, two, other) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        Rollout(codex, one, CodexTool(one, "session_open", OpenReply("s-a")), CodexTool(one, "session_open", OpenReply("s-b")));
        Rollout(codex, two, CodexTool(two, "session_open", OpenReply("s-c")));
        Rollout(codex, other, UserText("unrelated"));
        foreach (var id in new[] { "s-a", "s-b", "s-c", "s-x" })
            NodeState.WriteSessionHandle(DataDir, new SessionHandle(id, "synthetic-token-" + id, "repo", "Chat " + id, id == "s-b" ? openedAt.AddSeconds(1) : openedAt));
        var opens = new ConcurrentDictionary<string, int>();
        var nodeChats = Chats([new CodexChatLocator(codex, open: Counting(opens))]);

        var chats = Announced(nodeChats.Read(_ => true));
        Assert.Equal(3, opens.Count);
        Assert.All(opens.Values, x => Assert.Equal(1, x));
        // Both sessions in thread one were located in the one read; only the newer is that thread's current session.
        Assert.False(chats.ContainsKey("s-a"));
        Assert.Equal(("codex", one, true), Route(chats["s-b"]));
        Assert.Equal(("codex", two, true), Route(chats["s-c"]));
        Assert.False(chats["s-x"]["wakeable"]!.GetValue<bool>());

        // s-x is still pending, but nothing was appended, so the next cycle reads nothing again.
        Assert.False(Announced(nodeChats.Read(_ => true))["s-x"]["wakeable"]!.GetValue<bool>());
        Assert.All(opens.Values, x => Assert.Equal(1, x));
        Assert.False(Handle("s-x").Unmapped);
    }

    [Fact]
    public void Oversize_line_is_skipped()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var codex = Path.Combine(root, "codex-sessions");
        var thread = Guid.NewGuid().ToString();
        var big = JsonNode.Parse(CodexTool(thread, "session_open", OpenReply("s-big")))!;
        big["payload"]!["item"]!["arguments"]!["padding"] = new string('x', 5000);
        Rollout(codex, thread, big.ToJsonString(), CodexTool(thread, "session_open", OpenReply("s-after")));
        var locator = new CodexChatLocator(codex, maxLineBytes: 2048);
        Assert.Null(locator.Locate("s-big", openedAt));
        Assert.Equal(new ChatLocation("codex", thread), locator.Locate("s-after", openedAt));
        Assert.Equal(new ChatLocation("codex", thread), new CodexChatLocator(codex).Locate("s-big", openedAt));

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("short\n" + new string('y', 100) + "\nlast\npartial"));
        Assert.Equal([("short", 6L), (null, 107L), ("last", 112L)], ChatLocators.ReadLines(stream, 0, 50).ToArray());
    }

    [Fact]
    public async Task Budget_stops_scanning_without_blocking_the_announce()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var codex = Path.Combine(root, "codex-sessions");
        var thread = Guid.NewGuid().ToString();
        Rollout(codex, thread, CodexTool(thread, "session_open", OpenReply("s-slow")));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-slow", "synthetic-token-slow", "repo", "Slow chat", openedAt));
        var opens = new ConcurrentDictionary<string, int>();
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using var stop = new CancellationTokenSource();
        var transport = InMemory(host, traffic, TimeSpan.FromMilliseconds(30));
        var run = NodeRunner.RunAsync(state, InMemoryHub, new NodeTransport
        {
            JoinCode = transport.JoinCode, ProbeFingerprint = transport.ProbeFingerprint, CreateHandler = transport.CreateHandler, HubHandler = transport.HubHandler, MachineName = "Node PC",
            Locators = [new CodexChatLocator(codex, open: Counting(opens))], LocateBudget = TimeSpan.Zero,
            AnnounceInterval = transport.AnnounceInterval, FirstRetry = transport.FirstRetry, MaxRetry = transport.MaxRetry, Trace = events.Enqueue, CodexExecutable = () => null
        }, error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 2, run);
        stop.Cancel();
        Assert.Equal(0, await run);

        var chat = JsonNode.Parse(traffic.Accepted!)!["chats"]!.AsArray().Single()!.AsObject();
        Assert.Equal(("unknown", "unmapped", false), Route(chat));
        Assert.Empty(opens);
        Assert.Equal((null, false), (Handle("s-slow").Chat, Handle("s-slow").Unmapped));
        // Still pending, so the next cycle with time to spare finds it.
        Assert.Equal(("codex", thread, true), Route(Announced(Chats([new CodexChatLocator(codex)]).Read(_ => true))["s-slow"]));
    }

    [Fact]
    public void Missing_codex_executable_marks_routes_unwakeable()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (codexThread, claudeThread) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-codex", "synthetic-token-codex", "repo", "Codex chat", openedAt, Chat: new ChatLocation("codex", codexThread)));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claude", "synthetic-token-claude", "repo", "Claude chat", openedAt, Chat: new ChatLocation("claude", claudeThread)));

        var without = Announced(new NodeChats(DataDir, [], TimeProvider.System, NodeChats.DefaultLocateWindow, NodeChats.DefaultBudget, codexFound: () => false).Read(_ => true));
        Assert.Equal(("codex", codexThread, false), Route(without["s-codex"]));
        Assert.Equal(("claude", claudeThread, true), Route(without["s-claude"]));
        Assert.Equal(("codex", codexThread, true), Route(Announced(Chats([]).Read(_ => true))["s-codex"]));

        // The newest installed codex.exe under %LOCALAPPDATA%\OpenAI\Codex\bin\<version>\ (a temporary stand-in here).
        var local = Path.Combine(root, "local-app-data");
        Assert.Null(CodexLocator.Find(local));
        string Install(string version, DateTime written)
        {
            var path = Path.Combine(local, "OpenAI", "Codex", "bin", version, "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not a program");
            File.SetLastWriteTimeUtc(path, written);
            return path;
        }
        Install("0.9.0", DateTime.UtcNow.AddDays(-2));
        var newest = Install("1.0.0", DateTime.UtcNow.AddMinutes(-5));
        Directory.CreateDirectory(Path.Combine(local, "OpenAI", "Codex", "bin", "empty"));
        Assert.Equal(newest, CodexLocator.Find(local));

        // Only a codex route whose handle on this PC names the same thread becomes a target, and only when codex.exe was found.
        var dispatcher = new RecordingDispatcher();
        NodeRoute[] routes =
        [
            new("codex", codexThread, "s-codex", "repo"), new("claude", claudeThread, "s-claude", "repo"), new("codex", Guid.NewGuid().ToString(), "s-elsewhere", "repo"),
            new("codex", Guid.NewGuid().ToString(), "s-codex", "repo"), new("codex", codexThread, @"..\sessions\s-codex", "repo"), new("codex", codexThread, "s-codex", "other-repo")
        ];
        Assert.Empty(NodeRunner.WakeTargets(DataDir, routes, null, _ => dispatcher, "Node PC"));
        var target = Assert.Single(NodeRunner.WakeTargets(DataDir, routes, newest, path => { Assert.Equal(newest, path); return dispatcher; }, "Node PC"));
        Assert.Equal((codexThread, "s-codex", "synthetic-token-codex", "repo"), (target.Route.ThreadId, target.Session.SessionId, target.Session.SessionToken, target.Route.RepositoryId));
        Assert.Same(dispatcher, target.Dispatcher);
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
                JoinCode = transport.JoinCode, ProbeFingerprint = transport.ProbeFingerprint, CreateHandler = transport.CreateHandler, HubHandler = transport.HubHandler, MachineName = "Node PC", Locators = [],
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
    public async Task Claude_waiter_presence_follows_its_process()
    {
        var chatId = Guid.NewGuid().ToString();
        EnrollOnLoopback();
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claude", "synthetic-token-claude", "repo", "Claude chat", DateTimeOffset.UtcNow.AddMinutes(-1), new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        var inbox = new InboxStub(new string('c', 64), "s-claude", "synthetic-token-claude");
        var prompts = new List<string>();
        var pid = Path.Combine(DataDir, "waiters", chatId + ".pid");
        bool Waiting() => Announced(Chats([]).Read(_ => true))["s-claude"]["waiterRunning"]!.GetValue<bool>();
        Assert.False(Waiting());

        // The node folder stays locked by a running node; the waiter never needs node.lock.
        using (NodeState.Open(DataDir))
        {
            var waiting = ClaudeWaiter.RunAsync(DataDir, chatId.ToUpperInvariant(), prompts.Add, TimeSpan.FromSeconds(3), CancellationToken.None, inbox, () => new NoHub());
            await WaitForAsync(() => File.Exists(pid), waiting);
            Assert.True(Waiting());
            AssertPrivate(pid);
            // A second waiter for the same chat is refused and leaves the live waiter's file alone.
            await Assert.ThrowsAsync<AdapterConfigurationException>(() => ClaudeWaiter.RunAsync(DataDir, chatId, prompts.Add, TimeSpan.FromSeconds(1), CancellationToken.None, inbox, () => new NoHub()));
            Assert.True(File.Exists(pid));
            Assert.True(Waiting());
            Assert.Equal("timeout", await waiting);
        }
        Assert.False(File.Exists(pid));
        Assert.False(Waiting());

        inbox.Messages = ["m1"];
        Assert.Equal("queued", await ClaudeWaiter.RunAsync(DataDir, chatId, prompts.Add, TimeSpan.FromSeconds(30), CancellationToken.None, inbox, () => new NoHub()));
        Assert.Contains("inbox", Assert.Single(prompts));
        Assert.DoesNotContain("synthetic-token", prompts[0]);
        Assert.False(File.Exists(pid));

        var missing = await Assert.ThrowsAsync<AdapterConfigurationException>(() => ClaudeWaiter.RunAsync(DataDir, Guid.NewGuid().ToString(), prompts.Add, TimeSpan.FromSeconds(1), CancellationToken.None, inbox, () => new NoHub()));
        Assert.Contains("session_open", missing.Message);
        Assert.False(Directory.EnumerateFiles(Path.Combine(DataDir, "waiters"), "*.pid").Any());
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

    [Fact]
    public async Task Claude_waiter_acknowledges_handled_messages_before_waiting()
    {
        var chatId = Guid.NewGuid().ToString();
        EnrollOnLoopback();
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claude", "synthetic-token-claude", "repo", "Claude chat", DateTimeOffset.UtcNow.AddMinutes(-1), new ChatLocation("claude", chatId), new ChatLocation("claude", chatId)));
        var inbox = new InboxStub(new string('c', 64), "s-claude", "synthetic-token-claude");
        // Acknowledging and re-arming are one command, so handling a wake costs the chat one call fewer.
        Assert.Equal("timeout", await ClaudeWaiter.RunAsync(DataDir, chatId, _ => { }, TimeSpan.FromSeconds(1), CancellationToken.None, inbox, () => new NoHub(), acknowledge: ["m1", "m2"]));
        using var ack = JsonDocument.Parse(Assert.Single(inbox.Acks));
        Assert.Equal(["m1", "m2"], ack.RootElement.GetProperty("messageIds").EnumerateArray().Select(x => x.GetString()));
        Assert.False(string.IsNullOrEmpty(ack.RootElement.GetProperty("requestId").GetString()));
        Assert.Equal("repo", ack.RootElement.GetProperty("repositoryId").GetString());
    }

    [Fact]
    public void Claude_waiter_with_several_handles_uses_the_newest()
    {
        var chatId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-old", "synthetic-token-old", "repo", "Old", now.AddHours(-2), new ChatLocation("claude", chatId)));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-new", "synthetic-token-new", "repo", "New", now.AddMinutes(-1), Chat: new ChatLocation("claude", chatId)));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-codex", "synthetic-token-codex", "repo", "Codex", now, Chat: new ChatLocation("codex", chatId)));
        Assert.Equal("s-new", ClaudeWaiter.FindHandle(DataDir, chatId)?.SessionId);
        Assert.Null(ClaudeWaiter.FindHandle(DataDir, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Waiter_pid_file_from_a_reused_process_id_is_ignored_and_removed()
    {
        var chatId = Guid.NewGuid().ToString();
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claude", "synthetic-token-claude", "repo", "Claude chat", DateTimeOffset.UtcNow.AddMinutes(-1), Chat: new ChatLocation("claude", chatId)));
        var pid = Path.Combine(DataDir, "waiters", chatId + ".pid");
        bool Waiting() => Announced(Chats([]).Read(_ => true))["s-claude"]["waiterRunning"]!.GetValue<bool>();
        using var current = Process.GetCurrentProcess();
        var started = current.StartTime.ToUniversalTime();

        // The same process ID, but a process that started at another time: the waiter is gone and the ID was reused.
        ClaudeWaiter.WritePid(DataDir, chatId, current.Id, started.AddHours(-1));
        Assert.False(Waiting());
        Assert.False(File.Exists(pid));

        // A process that has exited.
        using var exited = Process.Start(OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false } : new ProcessStartInfo("/bin/sh", "-c true"))!;
        var exitedStart = exited.StartTime.ToUniversalTime();
        exited.WaitForExit();
        ClaudeWaiter.WritePid(DataDir, chatId, exited.Id, exitedStart);
        Assert.False(Waiting());
        Assert.False(File.Exists(pid));

        // An orphaned file for a chat this PC no longer announces is cleaned up too; the live waiter's file stays.
        var orphan = Guid.NewGuid().ToString();
        ClaudeWaiter.WritePid(DataDir, orphan, exited.Id, exitedStart);
        File.WriteAllText(Path.Combine(DataDir, "waiters", "not-a-pid-file.pid"), "garbage");
        ClaudeWaiter.WritePid(DataDir, chatId, current.Id, started);
        Assert.True(Waiting());
        Assert.True(File.Exists(pid));
        Assert.False(File.Exists(Path.Combine(DataDir, "waiters", orphan + ".pid")));
    }

    [Fact]
    public void Unmapped_handles_older_than_a_day_are_pruned()
    {
        var now = DateTimeOffset.UtcNow;
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-stale", "synthetic-token-stale", "repo", "Stale", now.AddHours(-25), Unmapped: true));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-never-found", "synthetic-token-never", "repo", "Never found", now.AddHours(-25)));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-recent", "synthetic-token-recent", "repo", "Recent", now.AddHours(-1), Unmapped: true));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-located", "synthetic-token-located", "repo", "Located", now.AddDays(-3), Chat: new ChatLocation("codex", Guid.NewGuid().ToString())));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-denied", "synthetic-token-denied", "elsewhere", "Denied", now.AddHours(-25), Unmapped: true));

        var chats = Announced(Chats([]).Read(repository => repository == "repo"));

        Assert.Equal(["s-located", "s-recent"], chats.Keys.Order());
        string HandlePath(string id) => Path.Combine(DataDir, "sessions", id + ".json");
        Assert.False(File.Exists(HandlePath("s-stale")));
        Assert.False(File.Exists(HandlePath("s-never-found")));
        Assert.False(File.Exists(HandlePath("s-denied")));
        Assert.True(File.Exists(HandlePath("s-recent")));
        Assert.True(File.Exists(HandlePath("s-located")));
    }

    [Fact]
    public void Announced_chats_fit_the_hub_payload_limit_dropping_the_oldest_first()
    {
        var now = DateTimeOffset.UtcNow;
        // Labels of 256 non-ASCII characters serialize as escapes, so 64 of them are far beyond the hub's 32 KB request limit.
        for (var i = 0; i < NodeChats.MaxChats; i++)
            NodeState.WriteSessionHandle(DataDir, new SessionHandle($"s-{i:D2}", "synthetic-token", "repo", new string('€', 256), now.AddMinutes(-i), Chat: new ChatLocation("codex", Guid.NewGuid().ToString())));

        var chats = Chats([]).Read(_ => true);

        var body = new JsonObject { ["machineName"] = new string('€', 128), ["clients"] = new JsonArray("codex", "claude"), ["chats"] = chats.DeepClone() };
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body.ToJsonString()) <= CoCern.Contracts.RelayLimits.MaxPayloadBytes);
        Assert.InRange(chats.Count, 1, NodeChats.MaxChats - 1);
        // The newest are kept, in order.
        Assert.Equal(Enumerable.Range(0, chats.Count).Select(i => $"s-{i:D2}"), chats.Select(x => x!["relaySessionId"]!.GetValue<string>()));
    }

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

    [Fact]
    public void Pruning_keeps_old_unmapped_handles_that_a_claude_waiter_can_still_use()
    {
        var now = DateTimeOffset.UtcNow;
        var (claimed, waited) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        // Never located, older than a day: one carries the chat ID Claude Code captured, the other's chat has a live waiter (this test process).
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-claimed", "synthetic-token-claimed", "repo", "Claimed", now.AddHours(-25), Client: new ChatLocation("claude", claimed), Unmapped: true));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-waited", "synthetic-token-waited", "repo", "Waited", now.AddHours(-26), Client: new ChatLocation("claude", waited)));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-stale", "synthetic-token-stale", "repo", "Stale", now.AddHours(-25), Unmapped: true));
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        ClaudeWaiter.WritePid(DataDir, waited, self.Id, self.StartTime.ToUniversalTime());

        var chats = Announced(Chats([]).Read(_ => true));

        Assert.Equal(["s-claimed", "s-waited"], chats.Keys.Order());
        Assert.False(File.Exists(Path.Combine(DataDir, "sessions", "s-stale.json")));
        // --await-inbox --chat finds its handle the same way.
        Assert.Equal("s-claimed", ClaudeWaiter.FindHandle(DataDir, claimed)?.SessionId);
        Assert.Equal("s-waited", ClaudeWaiter.FindHandle(DataDir, waited)?.SessionId);
    }

    [Fact]
    public void A_newer_session_in_the_same_thread_replaces_the_older_handle()
    {
        var now = DateTimeOffset.UtcNow;
        var codex = Path.Combine(root, "codex-sessions");
        var (thread, other) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        // The thread opened s-old earlier, then s-new; the rollout holds both results. Another thread's handle is untouched.
        Rollout(codex, thread, CodexTool(thread, "session_open", OpenReply("s-old")), CodexTool(thread, "session_open", OpenReply("s-new")));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-old", "synthetic-token-old", "repo", "Old", now.AddMinutes(-20), Chat: new ChatLocation("codex", thread.ToUpperInvariant())));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-new", "synthetic-token-new", "repo", "New", now.AddMinutes(-1)));
        NodeState.WriteSessionHandle(DataDir, new SessionHandle("s-other", "synthetic-token-other", "repo", "Other", now.AddMinutes(-30), Chat: new ChatLocation("codex", other)));

        var chats = Announced(Chats([new CodexChatLocator(codex)]).Read(_ => true));

        Assert.Equal(["s-new", "s-other"], chats.Keys.Order());
        Assert.Equal(("codex", thread, true), Route(chats["s-new"]));
        // The older handle is deleted, and its token with it.
        Assert.False(File.Exists(Path.Combine(DataDir, "sessions", "s-old.json")));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(DataDir, "sessions")), path => File.ReadAllText(path).Contains("synthetic-token-old"));
        Assert.True(File.Exists(Path.Combine(DataDir, "sessions", "s-other.json")));
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

    private sealed class NoHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>The hub's get_status and check_inbox for one session, checking the bearer and session token on every call.</summary>
    private sealed class InboxStub(string credential, string sessionId, string sessionToken) : HttpMessageHandler
    {
        public volatile string[] Messages = [];
        /// <summary>The tool and body of every acknowledgment the stub received.</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Acks = new();
        /// <summary>The body of every inbox read the stub received.</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> InboxReads = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != credential) return new(HttpStatusCode.Unauthorized);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var args = JsonDocument.Parse(body);
            if (request.RequestUri!.AbsolutePath.EndsWith("/ack")) Acks.Enqueue(body);
            if (request.RequestUri!.AbsolutePath.EndsWith("/check_inbox")) InboxReads.Enqueue(body);
            if (args.RootElement.GetProperty("sessionId").GetString() != sessionId || args.RootElement.GetProperty("sessionToken").GetString() != sessionToken) return new(HttpStatusCode.Forbidden);
            var messages = Messages;
            var data = request.RequestUri!.AbsolutePath.EndsWith("get_status")
                ? JsonSerializer.Serialize(new { sessionId, control = new { paused = false, requests = Array.Empty<object>() } })
                : JsonSerializer.Serialize(new { items = messages.Select(id => new { messageId = id, repositoryId = "repo", text = "UNTRUSTED" }), nextCursor = (int?)null, truncated = false, total = messages.Length });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = data, version = 0 }), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private NodeChats Chats(IChatLocator[] locators, TimeProvider? clock = null) => new(DataDir, locators, clock ?? TimeProvider.System, NodeChats.DefaultLocateWindow, NodeChats.DefaultBudget);

    private SessionHandle Handle(string sessionId) => NodeState.ReadSessionHandle(Path.Combine(DataDir, "sessions", sessionId + ".json"))!;

    private static Dictionary<string, JsonObject> Announced(JsonArray chats) => chats.Select(x => x!.AsObject()).ToDictionary(x => x["relaySessionId"]!.GetValue<string>());

    private static (string, string, bool) Route(JsonObject chat) => (chat["provider"]!.GetValue<string>(), chat["threadId"]!.GetValue<string>(), chat["wakeable"]!.GetValue<bool>());

    private static Func<string, Stream> Counting(ConcurrentDictionary<string, int> opens) => path => { opens.AddOrUpdate(path, 1, (_, count) => count + 1); return ChatLocators.OpenShared(path); };

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
        JoinCode = RelayHostFixture.JoinCode,
        MachineName = "Node PC",
        Chats = hub is null ? _ => Task.FromResult(new JsonArray(Chat())) : OwnedChats(hub),
        FirstRetry = TimeSpan.FromMilliseconds(20),
        Trace = events.Enqueue,
        CodexExecutable = () => null
    };

    private NodeTransport InMemory(RelayHostFixture host, HubTraffic traffic, TimeSpan? interval = null, int credentialAttempts = 5) => new()
    {
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

    private static RelayHostFixture Hub(IPAddress? remote = null) => new(remoteAddress: remote, settings: new Dictionary<string, string?>
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
        return data.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("machineName").GetString()!).ToArray();
    }

    private static void AssertPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.False(rule.IsInherited);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

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

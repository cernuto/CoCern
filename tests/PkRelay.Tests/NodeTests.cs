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
using PkRelay.Adapter;
using PkRelay.Adapter.Node;
using PkRelay.Host;
using Xunit;

namespace PkRelay.Tests;

public sealed class NodeTests : IDisposable
{
    private const string AdminToken = "node-admin-test-secret-not-production";
    private const string ProbedFingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string OtherFingerprint = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";
    private static readonly Uri InMemoryHub = new("https://localhost/");
    private readonly string root = Path.Combine(Path.GetTempPath(), "pkrelay-node-" + Guid.NewGuid().ToString("N"));
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
    public async Task Unauthorized_announcement_reenrolls_under_a_fresh_identity_when_the_old_one_is_taken()
    {
        await using var host = Hub();
        var traffic = new HubTraffic();
        using var state = NodeState.Open(DataDir);
        using var stop = new CancellationTokenSource();
        var run = NodeRunner.RunAsync(state, InMemoryHub, InMemory(host, traffic, TimeSpan.FromMilliseconds(20)), error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 1, run);
        var (machineId, credential) = (state.MachineId, state.ReadCredential());

        Volatile.Write(ref traffic.UnauthorizedAnnounces, 1);
        await WaitForAsync(() => traffic.Enrolls == 3, run);
        var successes = Count("announced");
        await WaitForAsync(() => Count("announced") > successes, run);
        stop.Cancel();
        Assert.Equal(0, await run);

        // The same identity is tried first; the hub still holds it under the old credential, so the node moves to a new one.
        Assert.Equal([machineId, machineId, state.MachineId], traffic.EnrolledIds);
        Assert.NotEqual(machineId, state.MachineId);
        Assert.NotEqual(credential, state.ReadCredential());
        AssertPrivate(Path.Combine(DataDir, "credential"));
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
        var transport = Transport();
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
            ProbeFingerprint = (_, _) => throw new InvalidOperationException("A loopback HTTP hub must not be probed."),
            CreateHandler = (fingerprint, _) => { pins.Enqueue(fingerprint); return traffic.Wrap(host.Server.CreateHandler()); },
            HubHandler = () => host.Server.CreateHandler(),
            MachineName = "Node PC",
            FirstRetry = TimeSpan.FromMilliseconds(10),
            Trace = events.Enqueue
        };
        using var stop = new CancellationTokenSource();
        var run = NodeRunner.RunAsync(state, new Uri("http://127.0.0.1:5271"), transport, error, stop.Token);
        await WaitForAsync(() => Count("announced") >= 1, run);
        stop.Cancel();
        Assert.Equal(0, await run);
        Assert.Equal([null], pins);
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

    private NodeTransport Transport() => new()
    {
        MachineName = "Node PC",
        Chats = _ => Task.FromResult(new JsonArray(Chat())),
        FirstRetry = TimeSpan.FromMilliseconds(20),
        Trace = events.Enqueue
    };

    private NodeTransport InMemory(RelayHostFixture host, HubTraffic traffic, TimeSpan? interval = null, int credentialAttempts = 5) => new()
    {
        CredentialAttempts = credentialAttempts,
        CredentialRetryDelay = TimeSpan.FromMilliseconds(25),
        ProbeFingerprint = (_, _) => Task.FromResult(ProbedFingerprint),
        CreateHandler = (_, _) => traffic.Wrap(host.Server.CreateHandler()),
        HubHandler = () => host.Server.CreateHandler(),
        MachineName = "Node PC",
        Chats = _ => Task.FromResult(new JsonArray(Chat())),
        AnnounceInterval = interval ?? TimeSpan.FromSeconds(30),
        FirstRetry = TimeSpan.FromMilliseconds(10),
        MaxRetry = TimeSpan.FromMilliseconds(50),
        Trace = events.Enqueue
    };

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
            ["Relay:Network:CertificatePath"] = Path.Combine("nested", "hub-certificate.pfx")
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
        public int Enrolls, Announces, Failed, FailAnnounces, UnauthorizedAnnounces;
        private readonly ConcurrentQueue<string> enrolledIds = new();
        public string[] EnrolledIds => enrolledIds.ToArray();

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
                        break;
                }
                return await base.SendAsync(request, cancellationToken);
            }
        }
    }
}

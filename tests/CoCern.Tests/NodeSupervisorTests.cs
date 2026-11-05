using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using CoCern.Adapter.Development;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

public sealed class NodeSupervisorTests : IDisposable
{
    [Theory]
    [InlineData("valid", true, true)]
    public async Task Task5_fix1_ordinary_start_uses_the_supervised_lifecycle_and_preserves_original_credential_recovery(string scenario, bool marked, bool ordinary)
    {
        PrepareRoot();
        var source = Package("ordinary-credential-" + scenario, ordinaryStartup: marked);
        var fingerprint = new string('C', 64);
        var oldCredential = new string('a', 64);
        var newCredential = new string('b', 64);
        string originalMachine;
        using (var state = NodeState.Open(NodeFolder))
        {
            state.SetHub("https://localhost/", fingerprint); state.SaveCredential(oldCredential); state.SaveJoinCode("authorized-fixture-code");
            originalMachine = state.MachineId;
        }
        var identityBefore = File.ReadAllBytes(Path.Combine(NodeFolder, "node.json"));
        var credentialBefore = File.ReadAllBytes(Path.Combine(NodeFolder, "credential"));
        var requests = new List<string>();
        var starts = 0;
        var announces = 0;
        var stoppedAtExpectedEvent = false;
        // Completion is driven by the lifecycle event below; this is only a finite hang guard.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = new StringWriter();
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            FirstRetry = TimeSpan.FromMilliseconds(10), MaxRetry = TimeSpan.FromMilliseconds(20),
            ProbeFingerprint = (_, _) => throw new InvalidOperationException("An enrolled pinned hub must not be probed again."),
            HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            Trace = value => { if (value == "announced") { stoppedAtExpectedEvent = true; stop.Cancel(); } },
            CreateHandler = (pin, _) => new UpdateWorkerHandler(request =>
            {
                Assert.Equal(fingerprint, pin);
                Assert.Equal("https", request.RequestUri!.Scheme);
                Assert.Throws<CoCern.Adapter.AdapterConfigurationException>(() => NodeState.Open(NodeFolder));
                var path = request.RequestUri!.AbsolutePath;
                requests.Add(path);
                if (path == "/api/discovery/announce")
                {
                    announces++;
                    var credential = request.Headers.Authorization?.Parameter;
                    if (scenario == "valid" || scenario == "expired" && credential == newCredential)
                        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"routes\":[]}") };
                    Assert.Equal(oldCredential, credential);
                    if (scenario == "conflict" && announces == 2) { stoppedAtExpectedEvent = true; stop.Cancel(); }
                    return new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
                }
                Assert.Equal("/api/discovery/enroll", path);
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("authorized-fixture-code", body.RootElement.GetProperty("joinCode").GetString());
                var machine = body.RootElement.GetProperty("machineId").GetString();
                if (scenario == "conflict")
                    return new(System.Net.HttpStatusCode.Conflict) { Content = new StringContent("{\"code\":\"AliasConflict\"}") };
                if (machine == originalMachine)
                    return new(System.Net.HttpStatusCode.Conflict) { Content = new StringContent("{\"code\":\"MachineExpired\"}") };
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { machineId = machine, credential = newCredential })) };
            })
        };
        var exit = await SetupEnvironment.RunNodeAsync(NodeFolder, null, transport, Path.Combine(source, "CoCern.Adapter.dll"), FixHost.DotnetPath, error, stop.Token,
            (folder, dll, _, ct) =>
            {
                Assert.Empty(requests);
                using (var released = NodeState.OpenExisting(folder)) Assert.Equal(originalMachine, released.MachineId);
                INodeChild? Start(NodeLaunchSpec launch)
                {
                    starts++;
                    Assert.Equal(NodeFolder, launch.Arguments[Array.IndexOf(launch.Arguments, "--data-dir") + 1]);
                    var executing = Path.GetDirectoryName(launch.Arguments[0])!;
                    var startup = launch.StartupContext!;
                    var argument = launch.Arguments[Array.IndexOf(launch.Arguments, "--activation-nonce") + 1];
                    Assert.StartsWith(marked && ordinary ? "v2;" : "v1;", argument);
                    return new UpdateWorkerChild(workerCt => NodeRunner.RunWorkerAsync(folder, startup.ActivationNonce, executing, transport, error, workerCt, startup), ct);
                }
                return ordinary ? NodeSupervisor.RunOrdinaryAsync(folder, dll, FixHost, Start, TimeProvider.System, ct)
                    : NodeSupervisor.RunAsync(folder, dll, FixHost, Start, TimeProvider.System, ct);
            });
        if (!marked || !ordinary)
        {
            Assert.Equal(1, exit);
            Assert.Equal(1, starts);
            Assert.Equal(new[] { "/api/discovery/announce" }, requests);
            Assert.Equal(identityBefore, File.ReadAllBytes(Path.Combine(NodeFolder, "node.json")));
            Assert.Equal(credentialBefore, File.ReadAllBytes(Path.Combine(NodeFolder, "credential")));
            return;
        }
        Assert.True(stoppedAtExpectedEvent, "The fixture watchdog fired before the expected lifecycle event. " + error);
        Assert.Equal(0, exit);
        Assert.Equal(1, starts);
        using var reopened = NodeState.OpenExisting(NodeFolder);
        Assert.Equal("https://localhost/", reopened.HubUrl);
        Assert.Equal(fingerprint, reopened.Fingerprint);
        Assert.Equal("authorized-fixture-code", reopened.ReadJoinCode());
        if (scenario == "expired")
        {
            Assert.Equal(new[] { "/api/discovery/announce", "/api/discovery/enroll", "/api/discovery/enroll", "/api/discovery/announce" }, requests);
            Assert.NotEqual(originalMachine, reopened.MachineId);
            Assert.Equal(newCredential, reopened.ReadCredential());
        }
        else
        {
            Assert.Equal(scenario == "valid" ? new[] { "/api/discovery/announce" } : new[] { "/api/discovery/announce", "/api/discovery/enroll", "/api/discovery/announce" }, requests);
            Assert.Equal(originalMachine, reopened.MachineId);
            Assert.Equal(identityBefore, File.ReadAllBytes(Path.Combine(NodeFolder, "node.json")));
            Assert.Equal(credentialBefore, File.ReadAllBytes(Path.Combine(NodeFolder, "credential")));
        }
    }

    [Theory]
    [InlineData(false)]
    public async Task Task5_hardening_ordinary_http_worker_never_sends_stored_join_code_after_credential_refusal(bool acceptedFirst)
    {
        PrepareRoot();
        var source = Package("ordinary-http-refusal", ordinaryStartup: true);
        var oldCredential = new string('a', 64);
        string originalMachine;
        using (var state = NodeState.Open(NodeFolder))
        {
            state.SetHub("http://localhost/", null);
            state.SaveCredential(oldCredential);
            state.SaveJoinCode("retained-fixture-join-code");
            originalMachine = state.MachineId;
        }
        var retainedFiles = new[] { "node.json", "credential", "join-code" }
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(NodeFolder, name)));
        var requests = new List<string>();
        var sentJoinCodes = new List<string?>();
        var starts = 0;
        var announces = 0;
        var accepted = 0;
        var error = new StringWriter();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            FirstRetry = TimeSpan.FromMilliseconds(10), MaxRetry = TimeSpan.FromMilliseconds(20), AnnounceInterval = TimeSpan.FromMilliseconds(10),
            ProbeFingerprint = (_, _) => throw new InvalidOperationException("Loopback HTTP must not probe a certificate."),
            HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            Trace = value => { if (value == "announced") accepted++; },
            CreateHandler = (pin, _) => new UpdateWorkerHandler(request =>
            {
                Assert.Null(pin);
                Assert.Equal("http://localhost", request.RequestUri!.GetLeftPart(UriPartial.Authority));
                Assert.Throws<CoCern.Adapter.AdapterConfigurationException>(() => NodeState.Open(NodeFolder));
                var path = request.RequestUri.AbsolutePath;
                requests.Add(path);
                if (path == "/api/discovery/announce")
                {
                    Assert.Equal(oldCredential, request.Headers.Authorization?.Parameter);
                    announces++;
                    return acceptedFirst && announces == 1
                        ? new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"routes\":[]}") }
                        : new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
                }
                Assert.Equal("/api/discovery/enroll", path);
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                sentJoinCodes.Add(body.RootElement.TryGetProperty("joinCode", out var code) ? code.GetString() : null);
                // Close the pre-fix recovery attempt without accepting or changing an identity.
                return new(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("{\"code\":\"JoinCodeRequired\"}") };
            })
        };
        var exit = await SetupEnvironment.RunNodeAsync(NodeFolder, null, transport, Path.Combine(source, "CoCern.Adapter.dll"), FixHost.DotnetPath, error, stop.Token,
            (folder, dll, _, ct) =>
            {
                Assert.Empty(requests);
                using (var released = NodeState.OpenExisting(folder)) Assert.Equal(originalMachine, released.MachineId);
                return NodeSupervisor.RunOrdinaryAsync(folder, dll, FixHost, launch =>
                {
                    starts++;
                    Assert.Equal(NodeFolder, launch.Arguments[Array.IndexOf(launch.Arguments, "--data-dir") + 1]);
                    var argument = launch.Arguments[Array.IndexOf(launch.Arguments, "--activation-nonce") + 1];
                    Assert.StartsWith("v2;", argument);
                    Assert.True(NodeWorkerStartupContext.TryParse(argument, out var startup));
                    Assert.True(startup!.OrdinaryStartup);
                    var executing = Path.GetDirectoryName(launch.Arguments[0])!;
                    return new UpdateWorkerChild(workerCt => NodeRunner.RunWorkerAsync(folder, startup.ActivationNonce, executing, transport, error, workerCt, startup), ct);
                }, TimeProvider.System, ct);
            });
        Assert.Equal(1, exit);
        Assert.Equal(1, starts);
        Assert.False(stop.IsCancellationRequested);
        Assert.Equal(acceptedFirst ? 1 : 0, accepted);
        Assert.Empty(sentJoinCodes);
        Assert.Equal(acceptedFirst ? new[] { "/api/discovery/announce", "/api/discovery/announce" } : new[] { "/api/discovery/announce" }, requests);
        Assert.Contains("node-update:authentication-refused", error.ToString());
        using var reopened = NodeState.OpenExisting(NodeFolder);
        Assert.Equal(originalMachine, reopened.MachineId);
        Assert.Equal(oldCredential, reopened.ReadCredential());
        Assert.Equal("retained-fixture-join-code", reopened.ReadJoinCode());
        Assert.Equal("http://localhost/", reopened.HubUrl);
        Assert.Null(reopened.Fingerprint);
        foreach (var file in retainedFiles) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(NodeFolder, file.Key)));
    }

    private static NodeHostCapabilities FixHost => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");

    [Theory]
    [InlineData(true)]
    public async Task Fix1_private_worker_refusal_never_creates_or_adopts_identity(bool missing)
    {
        PrepareRoot(); var current = Image("fix-identity"); NodeUpdateState.Write(Updates, Committed(current));
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        var path = Path.Combine(NodeFolder, "node.json");
        if (missing) File.Delete(path);
        else { var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; json["enrolled"] = false; File.WriteAllText(path, json.ToJsonString()); }
        var before = missing ? null : File.ReadAllBytes(path); var requests = 0;
        var transport = new NodeTransport { Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            CreateHandler = (_, _) => new UpdateWorkerHandler(_ => { requests++; return new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }; }) };
        try { Assert.Equal(1, await NodeRunner.RunWorkerAsync(NodeFolder, null, Path.Combine(Updates, "versions", current.ContentId), transport, new StringWriter(), default)); }
        catch (CoCern.Adapter.AdapterConfigurationException) { }
        Assert.Equal(0, requests);
        if (missing) Assert.False(File.Exists(path)); else Assert.Equal(before, File.ReadAllBytes(path));
        using var released = new FileStream(Path.Combine(NodeFolder, "node.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Update_private_worker_without_nonce_refuses_first_401_without_reenrollment()
    {
        PrepareRoot();
        var current = Image("no-nonce-auth");
        NodeUpdateState.Write(Updates, Committed(current));
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        var before = File.ReadAllBytes(Path.Combine(NodeFolder, "node.json"));
        var paths = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            CreateHandler = (_, _) => new UpdateWorkerHandler(request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                if (paths.Count == 2) stop.Cancel();
                return new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            }),
            FirstRetry = TimeSpan.FromMilliseconds(10)
        };
        Assert.Equal(1, await NodeRunner.RunWorkerAsync(NodeFolder, null, Path.Combine(Updates, "versions", current.ContentId), transport, new StringWriter(), stop.Token));
        Assert.Equal(["/api/discovery/announce"], paths);
        using var reopened = NodeState.Open(NodeFolder);
        Assert.Equal(new string('a', 64), reopened.ReadCredential());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(NodeFolder, "node.json")));
    }

    [Fact]
    public async Task Update_mail_after_old_exit_and_before_candidate_authentication_survives_commit()
    {
        PrepareRoot();
        var current = Image("mail-gap-A");
        var candidate = Image("mail-gap-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var database = Path.Combine(fixture, "mail-gap-hub.db");
        (string SessionId, string Token) receiver = default;
        string credential;
        var settings = new Dictionary<string, string?> { ["Relay:Discovery:Enabled"] = "true", ["Relay:Discovery:Repositories:0"] = "repo" };
        await using (var hub = new RelayHostFixture(database, remoteAddress: System.Net.IPAddress.Loopback, settings: settings))
        {
            using var machine = hub.CreateClient();
            using (var state = NodeState.Open(NodeFolder))
            {
                state.SetHub("http://localhost/", null);
                using var enrolled = await machine.PostAsJsonAsync("api/discovery/enroll", new { machineId = state.MachineId, machineName = "fixture worker", joinCode = RelayHostFixture.JoinCode });
                enrolled.EnsureSuccessStatusCode();
                var json = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
                credential = json.GetProperty("credential").GetString()!;
                state.SaveCredential(credential);
            }
            machine.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            async Task<(string SessionId, string Token)> Open(string request)
            {
                var reply = await hub.Call(machine, "session_open", new { repositoryId = "repo", requestId = request });
                using var data = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!);
                return (data.RootElement.GetProperty("sessionId").GetString()!, data.RootElement.GetProperty("sessionToken").GetString()!);
            }
            var sender = await Open("gap-sender");
            receiver = await Open("gap-receiver");
            var announceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var authenticate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new NodeTransport
            {
                Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
                CreateHandler = (_, _) => new UpdateAnnounceBarrier(hub.Server.CreateHandler(), announceEntered, authenticate), HubHandler = () => hub.Server.CreateHandler(),
                Trace = value => { if (value == "announced") announced.TrySetResult(); }
            };
            var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var launches = 0;
            var supervisor = NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
            {
                if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
                var starting = NodeUpdateState.Read(Updates).Record!;
                Assert.NotNull(launch.StartupContext); Assert.NotEqual(Guid.Empty, launch.StartupContext.LaunchId);
                return new UpdateWorkerChild(ct => NodeRunner.RunWorkerAsync(NodeFolder, starting.ActivationNonce, Path.Combine(Updates, "versions", candidate.ContentId), transport, new StringWriter(), ct, launch.StartupContext), stop.Token);
            }, TimeProvider.System, stop.Token);
            try
            {
                await announceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, launches); // Old child exit was observed before candidate startup.
                Assert.Null(NodeUpdateState.ReadReady(Updates));
                Assert.Equal(NodeUpdatePhase.Starting, NodeUpdateState.Read(Updates).Record!.Phase);
                await hub.Call(machine, "send_message", new { repositoryId = "repo", sessionId = sender.SessionId, sessionToken = sender.Token, recipientSessionId = receiver.SessionId, requestId = "gap-mail", text = "mail after old exit before candidate ready" });
                await GapInboxCount(hub, machine, receiver, 1);
                Assert.Null(NodeUpdateState.ReadReady(Updates));
                authenticate.TrySetResult();
                // Releasing the request is not authenticated readiness. Keep its processing inside
                // the existing outer guard, then measure the supervisor's commit from actual readiness.
                await announced.Task.WaitAsync(stop.Token);
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                while (ReadPrimaryPhase(Updates) != NodeUpdatePhase.Committed && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                var committed = NodeUpdateState.Read(Updates).Record!;
                Assert.Equal(NodeUpdatePhase.Committed, committed.Phase);
                Assert.Equal(candidate.ContentId, committed.Current.ContentId);
                Assert.Equal(current.ContentId, committed.Previous!.ContentId);
                Assert.Equal(Environment.ProcessId, NodeUpdateState.ReadReady(Updates)!.ProcessId);
                await GapInboxCount(hub, machine, receiver, 1);
            }
            finally { authenticate.TrySetResult(); stop.Cancel(); Assert.Equal(0, await supervisor); }
        }
        await using var reopened = new RelayHostFixture(database, remoteAddress: System.Net.IPAddress.Loopback, settings: settings);
        using var reader = reopened.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        await GapInboxCount(reopened, reader, receiver, 1);
    }

    private static async Task GapInboxCount(RelayHostFixture hub, HttpClient machine, (string SessionId, string Token) receiver, int expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        var count = 0;
        do
        {
            var reply = await hub.Call(machine, "check_inbox", new { repositoryId = "repo", sessionId = receiver.SessionId, sessionToken = receiver.Token, attention = false, headersOnly = true });
            using var data = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!);
            count = data.RootElement.GetProperty("items").GetArrayLength();
            if (count == expected) break;
            await Task.Delay(20);
        } while (DateTimeOffset.UtcNow < deadline);
        Assert.Equal(expected, count);
    }

    private sealed class UpdateAnnounceBarrier(HttpMessageHandler inner, TaskCompletionSource entered, TaskCompletionSource authenticate) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/discovery/announce", StringComparison.Ordinal)) { entered.TrySetResult(); await authenticate.Task.WaitAsync(ct); }
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class UpdateWorkerChild(Func<CancellationToken, Task<int>> run, CancellationToken ct) : INodeChild
    {
        public bool HasExited => ended?.IsCompleted == true;
        private readonly CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        private Task<int>? ended;
        public int ProcessId => Environment.ProcessId;
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => ended ??= run(stopping.Token);
        public async ValueTask DisposeAsync() { stopping.Cancel(); if (ended is not null) { try { await ended; } catch (OperationCanceledException) { } } stopping.Dispose(); }
    }

    private sealed class UpdateWorkerHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Theory]
    [InlineData(NodeUpdatePhase.Starting)]
    public void Update_worker_validates_nonce_and_its_exact_immutable_image_before_authentication(NodeUpdatePhase phase)
    {
        PrepareRoot();
        var current = Image("worker-A");
        var candidate = Image("worker-B") with { ReleaseId = new string('B', 64) };
        var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current));
        NodeUpdateState.Write(Updates, new(1, current, null, candidate, nonce, phase, null, null));
        var expected = phase == NodeUpdatePhase.Starting ? candidate : current;
        var path = Path.Combine(Updates, "versions", expected.ContentId);
        var validated = NodeUpdateState.ValidateWorkerStartup(NodeFolder, nonce, path);
        Assert.NotNull(validated);
        AssertImage(expected, validated!);
        Assert.Null(NodeUpdateState.ValidateWorkerStartup(NodeFolder, Guid.NewGuid(), path));
        Assert.Null(NodeUpdateState.ValidateWorkerStartup(NodeFolder, nonce, fixture));
        File.AppendAllText(Path.Combine(path, "CoCern.Adapter.dll"), "changed");
        Assert.Null(NodeUpdateState.ValidateWorkerStartup(NodeFolder, nonce, path));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("nonce")]
    public async Task Update_supervisor_commits_only_exact_owned_authenticated_readiness(string identity)
    {
        PrepareRoot();
        var current = Image("handoff-A");
        var candidate = Image("handoff-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var launches = 0;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
        {
            launches++;
            if (launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            var journal = NodeUpdateState.Read(Updates).Record!;
            if (launches == 2)
            {
                Assert.Equal(NodeUpdatePhase.Starting, journal.Phase);
                NodeUpdateState.WriteReady(Updates, new(identity == "nonce" ? Guid.NewGuid() : journal.ActivationNonce!.Value, identity == "content" ? current.ContentId : candidate.ContentId, identity == "pid" ? 5679 : 5678, launch.StartupContext!.LaunchId));
            }
            else
            {
                Assert.Equal(NodeUpdatePhase.Rollback, journal.Phase);
                AssertImage(candidate, journal.Pending!);
                NodeUpdateState.WriteReady(Updates, new(journal.ActivationNonce!.Value, current.ContentId, 5678, launch.StartupContext!.LaunchId));
            }
            return new UpdateReadyChild(Updates);
        }, TimeProvider.System, default);
        Assert.Equal(0, exit);
        Assert.Equal(identity == "valid" ? 2 : 3, launches);
        var committed = NodeUpdateState.Read(Updates).Record!;
        Assert.Equal(NodeUpdatePhase.Committed, committed.Phase);
        Assert.Null(committed.Pending);
        Assert.Null(committed.ActivationNonce);
        AssertImage(identity == "valid" ? candidate : current, committed.Current);
        if (identity == "valid") AssertImage(current, committed.Previous!);
        else Assert.Null(committed.Previous);
        Assert.Equal(identity == "valid" ? null : candidate.ReleaseId, committed.LastFailedReleaseId);
    }

    private sealed class UpdateExitedChild(int code) : INodeChild
    {
        public bool HasExited => true;
        public int ProcessId => 5678;
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(code);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UpdateReadyChild(string updates, IDisposable? blockedReplace = null, bool signaled = false) : INodeChild
    {
        private readonly CancellationTokenSource stopped = new();
        private Task<int>? ended;
        public int ProcessId => 5678;
        public bool HasExited => signaled || ended?.IsCompleted == true;
        public Task<int> WaitForExitAsync(CancellationToken ct) => ended ??= WaitAsync(ct);
        private async Task<int> WaitAsync(CancellationToken ct)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stopped.Token);
            try
            {
                while (ReadPrimaryPhase(updates) != NodeUpdatePhase.Committed)
                    await Task.Delay(10, lifetime.Token);
                return 0;
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { return 1; }
        }
        public async ValueTask DisposeAsync()
        {
            blockedReplace?.Dispose();
            stopped.Cancel();
            if (ended is not null) await ended;
            stopped.Dispose();
        }
    }

    private static NodeUpdatePhase? ReadPrimaryPhase(string updates)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(updates, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<NodeUpdateRecord>(stream)?.Phase;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Journal_replace_failure_recovers_the_actual_durable_commit_boundary(bool failBackupAfterCommit)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var candidate = Image("B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var path = Path.Combine(Updates, failBackupAfterCommit ? "committed.json" : "state.json");
        var next = failBackupAfterCommit
            ? Committed(candidate) with { Previous = current }
            : new NodeUpdateRecord(1, current, null, candidate, Guid.Parse("4add542d-1e4a-47eb-a812-0fb0071907ab"), NodeUpdatePhase.Starting, null, null);
        using (var blockedReplace = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<InvalidDataException>(() => NodeUpdateState.Write(Updates, next));
        var recovered = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, recovered.Status);
        Assert.Equal(NodeUpdatePhase.Committed, recovered.Record!.Phase);
        Assert.Equal(failBackupAfterCommit ? candidate.ContentId : current.ContentId, recovered.Record.Current.ContentId);
        Assert.Null(recovered.Record.Pending);
        var backup = JsonSerializer.Deserialize<NodeUpdateRecord>(File.ReadAllText(Path.Combine(Updates, "committed.json")))!;
        Assert.Equal(current.ContentId, backup.Current.ContentId);
    }

    private static void AssertImage(NodeImage expected, NodeImage actual)
    {
        Assert.Equal(expected.ContentId, actual.ContentId);
        Assert.Equal(expected.AdapterDllSha256, actual.AdapterDllSha256);
        Assert.Equal(expected.ReleaseId, actual.ReleaseId);
        Assert.Equal((expected.Package.Schema, expected.Package.ProductVersion, expected.Package.TargetFramework, expected.Package.Rid, expected.Package.SelfContained, expected.Package.SupervisorProtocol), (actual.Package.Schema, actual.Package.ProductVersion, actual.Package.TargetFramework, actual.Package.Rid, actual.Package.SelfContained, actual.Package.SupervisorProtocol));
        Assert.Equal(expected.Package.Frameworks.Select(f => (f.Name, f.Version)).OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal), actual.Package.Frameworks.Select(f => (f.Name, f.Version)).OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal));
    }

    private readonly string fixture = Path.Combine(Path.GetTempPath(), "cocern-node-supervisor-" + Guid.NewGuid().ToString("N"));
    private string NodeFolder => Path.Combine(fixture, "original node");
    private string Updates => Path.Combine(NodeFolder, "updates");

    [Fact]
    public void Committed_record_round_trips_and_is_private_without_touching_original_state()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var record = Committed(current);
        var original = File.ReadAllBytes(Path.Combine(NodeFolder, "original-state.bin"));
        NodeUpdateState.Write(Updates, record);
        var result = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, result.Status);
        Assert.Equal(JsonSerializer.Serialize(record), JsonSerializer.Serialize(result.Record));
        AssertPrivate(Path.Combine(Updates, "state.json"));
        AssertPrivate(Path.Combine(Updates, "committed.json"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(NodeFolder, "original-state.bin")));
    }

    [Theory]
    [InlineData(false)]
    public async Task Interrupted_start_launches_retained_current_and_keeps_rollback_evidence_until_authenticated_readiness(bool corruptBackup)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var pending = Image("B") with { ReleaseId = new string('B', 64) };
        var nonce = Guid.Parse("4add542d-1e4a-47eb-a812-0fb0071907ab");
        WriteRaw("state.json", JsonSerializer.Serialize(new NodeUpdateRecord(1, current, null, pending, nonce, NodeUpdatePhase.Starting, null, null)));
        WriteRaw("committed.json", corruptBackup ? "corrupt-backup" : JsonSerializer.Serialize(Committed(current)));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        NodeLaunchSpec? launched = null;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/absent-explicit-bootstrap/CoCern.Adapter.dll", host, launch => { launched = launch; return new ExitedChild(); }, TimeProvider.System, default);
        Assert.NotNull(launched);
        Assert.Equal(1, exit);
        Assert.Equal(Path.Combine(Updates, "versions", current.ContentId, "CoCern.Adapter.dll"), launched.Arguments[0]);
        Assert.True(NodeWorkerStartupContext.TryParse(launched.Arguments[^1], out var startup));
        Assert.Equal(nonce, startup!.ActivationNonce);
        Assert.NotEqual(Guid.Empty, startup.LaunchId);
        Assert.Equal(launched.StartupContext!.LaunchId, startup.LaunchId);
        var recovered = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, recovered.Status);
        Assert.Equal(NodeUpdatePhase.Rollback, recovered.Record!.Phase);
        Assert.Equal(current.ContentId, recovered.Record.Current.ContentId);
        Assert.Equal(pending.ContentId, recovered.Record.Pending!.ContentId);
        Assert.Equal(nonce, recovered.Record.ActivationNonce);
        Assert.Equal(pending.ReleaseId, recovered.Record.LastFailedReleaseId);
        Assert.NotNull(recovered.Record.LastFailedAtUtc);
    }

    private sealed class ExitedChild : INodeChild
    {
        public bool HasExited => true;
        public int ProcessId => 12346;
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static NodeUpdateRecord Committed(NodeImage image) => new(1, image, null, null, null, NodeUpdatePhase.Committed, null, null);

    private void PrepareRoot()
    {
        Directory.CreateDirectory(NodeFolder);
        File.WriteAllBytes(Path.Combine(NodeFolder, "original-state.bin"), [3, 1, 4, 1, 5, 9]);
        PrivateDirectory(Updates);
    }

    private NodeImage Image(string payload)
    {
        var source = Package(payload);
        var metadata = NodePackageMetadataReader.Read(source);
        Assert.NotNull(metadata);
        var contentId = AdapterDeployment.ContentHash(source);
        var path = Path.Combine(Updates, "versions", contentId);
        PrivateDirectory(Path.GetDirectoryName(path)!);
        PrivateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
        return new(contentId, metadata, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "CoCern.Adapter.dll")))), null);
    }

    private string Package(string payload, int protocol = 1, bool ordinaryStartup = false)
    {
        var path = Path.Combine(fixture, "installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var capability = ordinaryStartup ? "[assembly: AssemblyMetadata(\"CoCernNodeOrdinaryStartupProtocol\", \"2\")] " : "";
        var code = "using System.Reflection; [assembly: AssemblyInformationalVersion(\"7.8.9+installed\")] [assembly: AssemblyMetadata(\"CoCernNodeSupervisorProtocol\", \"" + protocol + "\")] " + capability + "public static class InertPackage { }";
        var compilation = CSharpCompilation.Create("CoCern.Adapter", [CSharpSyntaxTree.ParseText(code)], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(Path.Combine(path, "CoCern.Adapter.dll"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        File.WriteAllText(Path.Combine(path, "CoCern.Adapter.deps.json"), "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0\"}}");
        File.WriteAllText(Path.Combine(path, "CoCern.Adapter.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        File.WriteAllText(Path.Combine(path, "payload.txt"), payload);
        return path;
    }

    private void WriteRaw(string name, string json) => File.WriteAllText(Path.Combine(Updates, name), json);

    private static void PrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    private static void AssertPrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new FileInfo(path).GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow) Assert.Equal(user, rule.IdentityReference);
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(fixture);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("cocern-node-supervisor-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}

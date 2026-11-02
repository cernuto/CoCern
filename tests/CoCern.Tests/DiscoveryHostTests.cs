using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace CoCern.Tests;

public sealed class DiscoveryHostTests
{
    private const string AdminToken = "discovery-admin-test-secret-not-production";

    private static RelayHostFixture Host(bool enabled = true, IPAddress? remote = null, string? repositories = "repo", Action<IServiceCollection>? services = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Discovery:Enabled"] = enabled ? "true" : "false",
            ["Relay:Clients:1:Token"] = AdminToken,
            ["Relay:Clients:1:PrincipalId"] = "admin-user",
            ["Relay:Clients:1:IsAdministrator"] = "true",
            ["Relay:Clients:1:Repositories:0"] = "repo"
        };
        if (repositories is not null) settings["Relay:Discovery:Repositories:0"] = repositories;
        return new RelayHostFixture(remoteAddress: remote, settings: settings, configureServices: services);
    }

    private static HttpClient Client(RelayHostFixture host, string? token = null, string scheme = "https")
    {
        var client = host.CreateClient(new() { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<string> EnrollAsync(RelayHostFixture host, string machineId, string name = "PC")
    {
        using var client = Client(host);
        var (status, body) = await PostAsync(client, "/api/discovery/enroll", new { machineId, machineName = name, joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(machineId, body.GetProperty("machineId").GetString());
        return body.GetProperty("credential").GetString()!;
    }

    private static object Chat(string repository = "repo", string thread = "t1", bool wakeable = true, string? session = null) =>
        new { provider = "codex", threadId = thread, relaySessionId = session ?? "s-" + thread, repositoryId = repository, label = "Chat " + thread, wakeable, waiterRunning = false };

    /// <summary>Opens a relay session as the client's principal and returns its ID; announce accepts only chats whose session the announcing machine owns.</summary>
    private static async Task<string> OpenSessionAsync(HttpClient client, string repository = "repo")
    {
        var (status, body) = await PostAsync(client, "/api/tools/session_open", new { repositoryId = repository, requestId = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.OK, status);
        using var data = JsonDocument.Parse(body.GetProperty("dataJson").GetString()!);
        return data.RootElement.GetProperty("sessionId").GetString()!;
    }

    private static object Announcement(params object[] chats) => new { machineName = "PC", clients = new[] { "codex" }, chats };

    [Fact]
    public async Task Enrolled_machine_can_call_coordination_tools_but_not_control_request()
    {
        await using var host = Host();
        var credential = await EnrollAsync(host, "pc-a");
        Assert.Equal(64, credential.Length);
        using var machine = Client(host, credential);
        Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync("/api/tools")).StatusCode);
        var opened = await PostAsync(machine, "/api/tools/session_open", new { repositoryId = "repo", requestId = "open-1" });
        Assert.Equal(HttpStatusCode.OK, opened.Status);
        var denied = await PostAsync(machine, "/api/tools/control_request", new { repositoryId = "repo", requestId = "c-1", command = "revoke_machine", machineId = "pc-a" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("AdministratorRequired", denied.Body.GetProperty("code").GetString());
        using var tools = JsonDocument.Parse(await machine.GetStringAsync("/api/tools"));
        Assert.DoesNotContain(tools.RootElement.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "control_request");
    }

    [Fact]
    public async Task Machine_principal_is_limited_to_discovery_repositories()
    {
        await using var host = Host();
        using var machine = Client(host, await EnrollAsync(host, "pc-a"));
        var denied = await PostAsync(machine, "/api/tools/get_status", new { repositoryId = "repo-not-listed" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("RepositoryDenied", denied.Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(machine, "/api/tools/session_open", new { repositoryId = "repo-not-listed", requestId = "x" })).Status);
    }

    [Fact]
    public async Task Discovery_repositories_default_to_cocern_only_when_the_setting_is_absent()
    {
        await using var host = Host(repositories: null);
        using var machine = Client(host, await EnrollAsync(host, "pc-a"));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(machine, "/api/tools/session_open", new { repositoryId = "cocern", requestId = "x" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(machine, "/api/tools/session_open", new { repositoryId = "repo", requestId = "y" })).Status);
    }

    [Fact]
    public async Task Enrollment_is_rate_limited_per_address()
    {
        await using var host = Host();
        using var client = Client(host);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/discovery/enroll", new { machineId = "pc-" + i, machineName = "PC", joinCode = RelayHostFixture.JoinCode })).Status);
        var limited = await PostAsync(client, "/api/discovery/enroll", new { machineId = "pc-10", machineName = "PC", joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("RateLimited", limited.Body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Enroll_and_announce_return_404_when_discovery_is_disabled()
    {
        await using var host = Host(enabled: false);
        using var anonymous = Client(host);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(anonymous, "/api/discovery/enroll", new { machineId = "pc-a", machineName = "PC" })).Status);
        using var admin = Client(host, AdminToken);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(admin, "/api/discovery/announce", Announcement())).Status);
        // No machine validation happens: a credential-shaped token is simply unknown.
        using var guess = Client(host, new string('a', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await guess.GetAsync("/api/tools")).StatusCode);
    }

    [Fact]
    public async Task Credentials_never_appear_in_logs()
    {
        var sink = new CapturingSink();
        await using var host = Host(services: services => services.AddSerilog(configuration => configuration.MinimumLevel.Verbose().WriteTo.Sink(sink)));
        var credential = await EnrollAsync(host, "pc-a");
        using var machine = Client(host, credential);
        await PostAsync(machine, "/api/discovery/announce", Announcement(Chat()));
        await machine.GetAsync("/api/tools");
        using var wrong = Client(host, new string('b', 64));
        await wrong.GetAsync("/api/tools");
        Assert.NotEmpty(sink.Lines);
        Assert.DoesNotContain(sink.Lines, line => line.Contains(credential, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Announce_is_for_machine_principals_only()
    {
        await using var host = Host();
        using var anonymous = Client(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, "/api/discovery/announce", Announcement())).Status);
        using var configured = Client(host, RelayHostFixture.Token);
        var refused = await PostAsync(configured, "/api/discovery/announce", Announcement());
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        using var admin = Client(host, AdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(admin, "/api/discovery/announce", Announcement())).Status);
    }

    [Fact]
    public async Task Announce_returns_routes_and_rejects_repositories_outside_the_discovery_list()
    {
        await using var host = Host();
        using var machine = Client(host, await EnrollAsync(host, "pc-a"));
        var (s1, s2) = (await OpenSessionAsync(machine), await OpenSessionAsync(machine));
        var accepted = await PostAsync(machine, "/api/discovery/announce", Announcement(Chat(thread: "t1", session: s1), Chat(thread: "t2", wakeable: false, session: s2)));
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        var route = Assert.Single(accepted.Body.GetProperty("routes").EnumerateArray());
        Assert.Equal("t1", route.GetProperty("threadId").GetString());
        var denied = await PostAsync(machine, "/api/discovery/announce", Announcement(Chat(thread: "t1", session: s1), Chat(repository: "repo-not-listed", thread: "t3")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("RepositoryDenied", denied.Body.GetProperty("code").GetString());
        // The refused announcement changed nothing: the earlier chats are still the machine's announced set.
        using var admin = Client(host, AdminToken);
        var directory = await PostAsync(admin, "/api/tools/chat_directory", new { repositoryId = "repo" });
        using var items = JsonDocument.Parse(directory.Body.GetProperty("dataJson").GetString()!);
        Assert.Equal(2, items.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Announce_rejects_malformed_bodies_without_touching_state()
    {
        await using var host = Host();
        using var machine = Client(host, await EnrollAsync(host, "pc-a"));
        foreach (var body in new[] { "[]", "\"x\"", "{\"machineName\":\"PC\",\"chats\":{}}", "{\"machineName\":\"PC\",\"chats\":[1]}", "{\"machineName\":\"PC\",\"chats\":[{\"provider\":\"codex\"}]}", "{\"machineName\":\"PC\",\"chats\":[{\"repositoryId\":5}]}", "{" })
        {
            using var response = await machine.PostAsync("/api/discovery/announce", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Announce_takes_identity_from_the_credential_never_from_the_body()
    {
        await using var host = Host();
        using var a = Client(host, await EnrollAsync(host, "pc-a", "PC A"));
        using var b = Client(host, await EnrollAsync(host, "pc-b", "PC B"));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(b, "/api/discovery/announce", new { machineName = "PC B", chats = new[] { Chat(thread: "tb", session: await OpenSessionAsync(b)) } })).Status);
        // A claims to be B and tries to replace B's chats.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(a, "/api/discovery/announce", new { machineId = "pc-b", machineName = "PC A", chats = new[] { Chat(thread: "ta", session: await OpenSessionAsync(a)) } })).Status);
        using var admin = Client(host, AdminToken);
        var directory = await PostAsync(admin, "/api/tools/chat_directory", new { repositoryId = "repo" });
        using var items = JsonDocument.Parse(directory.Body.GetProperty("dataJson").GetString()!);
        var rows = items.RootElement.GetProperty("items").EnumerateArray().Select(x => (x.GetProperty("machineName").GetString(), x.GetProperty("label").GetString())).OrderBy(x => x.Item2).ToArray();
        Assert.Equal([("PC A", "Chat ta"), ("PC B", "Chat tb")], rows);
    }

    [Fact]
    public async Task Enroll_reports_why_it_refuses()
    {
        await using var host = Host();
        await EnrollAsync(host, "pc-a");
        using var anonymous = Client(host);
        var duplicate = await PostAsync(anonymous, "/api/discovery/enroll", new { machineId = "pc-a", machineName = "PC", joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Equal("AliasConflict", duplicate.Body.GetProperty("code").GetString());
        Assert.DoesNotContain("credential", duplicate.Body.GetRawText());

        using var admin = Client(host, AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(admin, "/api/tools/control_request", new { repositoryId = "repo", requestId = "rv", command = "revoke_machine", machineId = "pc-a", reason = "test" })).Status);
        var revoked = await PostAsync(anonymous, "/api/discovery/enroll", new { machineId = "pc-a", machineName = "PC", joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.Forbidden, revoked.Status);
        Assert.Equal("MachineRevoked", revoked.Body.GetProperty("code").GetString());

        var directory = host.Services.GetRequiredService<IMachineDirectory>();
        for (var i = 0; i < DiscoveryLimits.MaxMachines; i++) Assert.NotNull(await directory.EnrollAsync("fill-" + i, "Fill", "secret-" + i, CancellationToken.None));
        var full = await PostAsync(anonymous, "/api/discovery/enroll", new { machineId = "one-too-many", machineName = "PC", joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.Status);
        Assert.Equal("CapacityExceeded", full.Body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Revoked_machine_credential_stops_working_immediately()
    {
        await using var host = Host();
        var credential = await EnrollAsync(host, "pc-a");
        using var machine = Client(host, credential);
        Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync("/api/tools")).StatusCode);
        using var admin = Client(host, AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(admin, "/api/tools/control_request", new { repositoryId = "repo", requestId = "rv", command = "revoke_machine", machineId = "pc-a", reason = "test" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await machine.GetAsync("/api/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(machine, "/api/discovery/announce", Announcement())).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"machineId\":\"pc-a\"}")]
    [InlineData("{\"machineName\":\"PC\"}")]
    [InlineData("{\"machineId\":5,\"machineName\":\"PC\"}")]
    [InlineData("{\"machineId\":\"\",\"machineName\":\"PC\"}")]
    [InlineData("{\"machineId\":\"pc:a\",\"machineName\":\"PC\"}")]
    [InlineData("{\"machineId\":\"pc a\",\"machineName\":\"PC\"}")]
    [InlineData("{\"machineId\":\"pc-a\",\"machineName\":\"bad\\nname\"}")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task Enroll_rejects_invalid_bodies(string body)
    {
        await using var host = Host();
        using var client = Client(host);
        using var response = await client.PostAsync("/api/discovery/enroll", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("credential", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Enroll_refuses_an_identity_that_would_collide_with_a_configured_principal()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Discovery:Enabled"] = "true",
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "machine:pc-a", ["Relay:Clients:1:Repositories:0"] = "repo"
        };
        await using var host = new RelayHostFixture(settings: settings);
        using var client = Client(host);
        var refused = await PostAsync(client, "/api/discovery/enroll", new { machineId = "pc-a", machineName = "PC", joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("AliasConflict", refused.Body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Enroll_and_machine_credentials_require_https_or_loopback()
    {
        await using var host = Host(remote: IPAddress.Parse("203.0.113.9"));
        using var plain = Client(host, scheme: "http");
        var refused = await PostAsync(plain, "/api/discovery/enroll", new { machineId = "pc-a", machineName = "PC", joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("HttpsRequired", refused.Body.GetProperty("code").GetString());
        Assert.DoesNotContain("credential", refused.Body.GetRawText());
        var credential = await EnrollAsync(host, "pc-a");
        using var plainMachine = Client(host, credential, "http");
        Assert.Equal(HttpStatusCode.Unauthorized, (await plainMachine.GetAsync("/api/tools")).StatusCode);
    }

    [Fact]
    public async Task Enroll_over_plain_loopback_is_allowed()
    {
        await using var host = Host(remote: IPAddress.Loopback);
        using var plain = Client(host, scheme: "http");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(plain, "/api/discovery/enroll", new { machineId = "pc-a", machineName = "PC", joinCode = RelayHostFixture.JoinCode })).Status);
    }

    [Fact]
    public async Task Watch_machine_is_for_machine_principals_and_receives_hints_only_when_routes_change()
    {
        await using var host = Host();
        var credential = await EnrollAsync(host, "pc-a");
        var other = await EnrollAsync(host, "pc-b");
        using var machine = Client(host, credential);
        using var otherMachine = Client(host, other);
        await using var hub = host.Hub(credential);
        await using var otherHub = host.Hub(other);
        await using var configuredHub = host.Hub();
        var hints = new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
        var otherHints = new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
        hub.On<JsonElement>("discovery.changed", hint => hints.Enqueue(hint));
        otherHub.On<JsonElement>("discovery.changed", hint => otherHints.Enqueue(hint));
        await hub.StartAsync(); await otherHub.StartAsync(); await configuredHub.StartAsync();
        await hub.InvokeAsync("WatchMachine");
        await otherHub.InvokeAsync("WatchMachine");
        await Assert.ThrowsAsync<HubException>(() => configuredHub.InvokeAsync("WatchMachine"));

        // Empty routes are the starting point, so a first announcement without wakeable chats is not a change.
        var session = await OpenSessionAsync(machine);
        await PostAsync(machine, "/api/discovery/announce", Announcement(Chat(wakeable: false, session: session)));
        await Task.Delay(300);
        Assert.Empty(hints);
        await PostAsync(machine, "/api/discovery/announce", Announcement(Chat(wakeable: true, session: session)));
        await WaitForAsync(() => hints.Count == 1);
        Assert.DoesNotContain(credential, hints.Single().GetRawText());
        await PostAsync(machine, "/api/discovery/announce", Announcement(Chat(wakeable: true, session: session)));
        await Task.Delay(300);
        Assert.Single(hints);
        await PostAsync(machine, "/api/discovery/announce", Announcement());
        await WaitForAsync(() => hints.Count == 2);
        // Another machine's group never hears about this machine's routes.
        Assert.Empty(otherHints);
    }

    [Fact]
    public async Task Announce_refuses_only_the_chats_whose_session_the_machine_does_not_own()
    {
        await using var host = Host();
        using var a = Client(host, await EnrollAsync(host, "pc-a", "PC A"));
        using var b = Client(host, await EnrollAsync(host, "pc-b", "PC B"));
        using var configured = Client(host, RelayHostFixture.Token);
        var (own, others, users) = (await OpenSessionAsync(a), await OpenSessionAsync(b), await OpenSessionAsync(configured));
        var reply = await PostAsync(a, "/api/discovery/announce", Announcement(Chat(thread: "own", session: own), Chat(thread: "b", session: others), Chat(thread: "user", session: users), Chat(thread: "none", session: "no-such-session")));
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        // Each refusal names the session and carries the denial code; the owned chat is still announced.
        var refused = reply.Body.GetProperty("refused").EnumerateArray().ToDictionary(x => x.GetProperty("relaySessionId").GetString()!, x => x.GetProperty("code").GetString());
        Assert.Equal(new Dictionary<string, string?> { [others] = "SessionDenied", [users] = "SessionDenied", ["no-such-session"] = "SessionDenied" }, refused);
        Assert.Equal(own, Assert.Single(reply.Body.GetProperty("routes").EnumerateArray()).GetProperty("relaySessionId").GetString());
        using var admin = Client(host, AdminToken);
        var directory = await PostAsync(admin, "/api/tools/chat_directory", new { repositoryId = "repo" });
        using var items = JsonDocument.Parse(directory.Body.GetProperty("dataJson").GetString()!);
        Assert.Equal([own], items.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("relaySessionId").GetString()));
    }

    [Theory]
    [InlineData("\"bad\\nname\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("5")]
    [InlineData("null")]
    public async Task Announce_applies_the_enroll_rules_to_the_machine_name(string name)
    {
        await using var host = Host();
        using var machine = Client(host, await EnrollAsync(host, "pc-a", "PC A"));
        var session = await OpenSessionAsync(machine);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(machine, "/api/discovery/announce", Announcement(Chat(session: session)))).Status);
        using var response = await machine.PostAsync("/api/discovery/announce", new StringContent("{\"machineName\":" + name + ",\"chats\":[]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var tooLong = await PostAsync(machine, "/api/discovery/announce", new { machineName = new string('x', 129), chats = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.Status);
        using var admin = Client(host, AdminToken);
        var directory = await PostAsync(admin, "/api/tools/chat_directory", new { repositoryId = "repo" });
        using var items = JsonDocument.Parse(directory.Body.GetProperty("dataJson").GetString()!);
        Assert.Equal(["PC"], items.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("machineName").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grain_failure_during_credential_validation_answers_503_never_401(bool throws)
    {
        var grain = new ScriptedDiscoveryGrain { Validate = throws ? null : Replies.Error("StorageFailure") };
        await using var host = Host(services: services => services.AddSingleton<IMachineDirectory>(new MachineDirectory(ScriptedFactory.For(grain))));
        using var machine = Client(host, new string('c', 64));
        using var response = await machine.GetAsync("/api/tools");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(machine, "/api/discovery/announce", Announcement())).Status);
        // A hub fault is never cached: once the grain answers again, a genuinely unknown credential is 401.
        grain.Validate = Replies.Error("NotFound");
        Assert.Equal(HttpStatusCode.Unauthorized, (await machine.GetAsync("/api/tools")).StatusCode);
    }

    [Fact]
    public async Task Repeated_unknown_credential_reaches_the_grain_once_within_a_minute()
    {
        var grain = new ScriptedDiscoveryGrain();
        await using var host = Host(services: services => services.AddSingleton<IMachineDirectory>(new MachineDirectory(ScriptedFactory.For(grain))));
        using var junk = Client(host, new string('d', 64));
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await junk.GetAsync("/api/tools")).StatusCode);
        // Even anonymous endpoints authenticate the header; the cached failure covers them too.
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await junk.GetAsync("/health")).StatusCode);
        Assert.Equal(1, grain.Validations);
    }

    [Fact]
    public async Task Failed_credential_validations_are_limited_per_address()
    {
        var grain = new ScriptedDiscoveryGrain();
        await using var host = Host(services: services => services.AddSingleton<IMachineDirectory>(new MachineDirectory(ScriptedFactory.For(grain))));
        for (var i = 0; i < 30; i++)
        {
            using var junk = Client(host, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i))));
            Assert.Equal(i < 20 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, (await junk.GetAsync("/api/tools")).StatusCode);
        }
        Assert.Equal(20, grain.Validations);
        // Configured clients never touch the machine path, so the limit does not lock them out.
        using var configured = Client(host, RelayHostFixture.Token);
        Assert.Equal(HttpStatusCode.OK, (await configured.GetAsync("/api/tools")).StatusCode);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static string Junk(int i) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)));

    private static Action<IServiceCollection> WithClock(ManualClock clock, ScriptedDiscoveryGrain grain, IControlGrain? control = null) => services =>
    {
        services.AddSingleton<IMachineDirectory>(new MachineDirectory(ScriptedFactory.For(grain, control), clock));
        services.AddSingleton(new MachineCredentialLimiter(clock));
    };

    [Fact]
    public async Task Address_over_its_failure_budget_gets_429_with_retry_after_and_the_grain_is_not_asked()
    {
        var clock = new ManualClock();
        var grain = new ScriptedDiscoveryGrain();
        await using var host = Host(services: WithClock(clock, grain));
        for (var i = 0; i < 20; i++) { using var junk = Client(host, Junk(i)); Assert.Equal(HttpStatusCode.Unauthorized, (await junk.GetAsync("/api/tools")).StatusCode); }
        using var over = Client(host, Junk(100));
        using var response = await over.GetAsync("/api/tools");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromMinutes(1));
        Assert.Equal(20, grain.Validations);
    }

    [Fact]
    public async Task Valid_uncached_credential_from_an_exhausted_address_gets_429_not_401_and_works_after_the_window()
    {
        var clock = new ManualClock();
        var grain = new ScriptedDiscoveryGrain();
        await using var host = Host(services: WithClock(clock, grain));
        for (var i = 0; i < 20; i++) { using var junk = Client(host, Junk(i)); await junk.GetAsync("/api/tools"); }
        grain.Validate = Replies.Ok(new { machineId = "pc-v", principalId = "machine:pc-v" });
        using var valid = Client(host, new string('e', 64));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await valid.GetAsync("/api/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(valid, "/api/discovery/announce", Announcement())).Status);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await valid.GetAsync("/api/tools")).StatusCode);
    }

    [Fact]
    public async Task Repeating_one_cached_junk_token_does_not_exhaust_the_address_budget()
    {
        var clock = new ManualClock();
        var grain = new ScriptedDiscoveryGrain();
        await using var host = Host(services: WithClock(clock, grain));
        using var junk = Client(host, Junk(1));
        for (var i = 0; i < 50; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await junk.GetAsync("/api/tools")).StatusCode);
        Assert.Equal(1, grain.Validations);
        using var other = Client(host, Junk(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/tools")).StatusCode);
        Assert.Equal(2, grain.Validations);
    }

    private static Microsoft.AspNetCore.Http.DefaultHttpContext From(string address) => new() { Connection = { RemoteIpAddress = IPAddress.Parse(address) } };

    [Fact]
    public void Failure_budgets_are_per_address_even_inside_one_ipv6_prefix_and_ipv4_mapped_addresses_normalize()
    {
        var limiter = new MachineCredentialLimiter(new ManualClock());
        var first = From("2001:db8:1:2::1");
        for (var i = 0; i < MachineCredentialLimiter.FailuresPerMinute; i++) limiter.Failed(first);
        Assert.False(limiter.MayValidate(first));
        Assert.True(limiter.MayValidate(From("2001:db8:1:2::2")));
        for (var i = 0; i < MachineCredentialLimiter.FailuresPerMinute; i++) limiter.Failed(From("::ffff:192.0.2.9"));
        Assert.False(limiter.MayValidate(From("192.0.2.9")));
        Assert.True(limiter.MayValidate(From("192.0.2.10")));
    }

    [Fact]
    public async Task Grain_exception_during_the_ownership_read_answers_503_not_500()
    {
        var grain = new ScriptedDiscoveryGrain { Validate = Replies.Ok(new { machineId = "pc-v", principalId = "machine:pc-v" }) };
        await using var host = Host(services: WithClock(new ManualClock(), grain, new ThrowingControlGrain()));
        using var machine = Client(host, new string('f', 64));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(machine, "/api/discovery/announce", Announcement(Chat()))).Status);
    }

    private sealed class ThrowingControlGrain : IControlGrain
    {
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson) => throw new InvalidOperationException("silo unavailable");
    }

    /// <summary>A discovery grain whose validation answer the test sets; null throws, like an unreachable silo. It counts validation calls.</summary>
    internal sealed class ScriptedDiscoveryGrain : IDiscoveryGrain
    {
        private int validations;
        public volatile RelayReply? Validate = Replies.Error("NotFound");
        public int Validations => Volatile.Read(ref validations);
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
        {
            if (operation != DiscoveryOperations.Validate) return Task.FromResult(Replies.Ok(new { }));
            Interlocked.Increment(ref validations);
            return Validate is { } reply ? Task.FromResult(reply) : throw new InvalidOperationException("silo unavailable");
        }
    }

    internal class ScriptedFactory : System.Reflection.DispatchProxy
    {
        private IDiscoveryGrain? grain;
        private IControlGrain? control;
        public static Orleans.IGrainFactory For(IDiscoveryGrain grain, IControlGrain? control = null)
        {
            var factory = Create<Orleans.IGrainFactory, ScriptedFactory>();
            ((ScriptedFactory)(object)factory).grain = grain;
            ((ScriptedFactory)(object)factory).control = control;
            return factory;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name != "GetGrain" ? throw new NotSupportedException(targetMethod.Name)
            : targetMethod.ReturnType == typeof(IDiscoveryGrain) ? grain
            : targetMethod.ReturnType == typeof(IControlGrain) && control is not null ? control
            : throw new NotSupportedException(targetMethod.Name);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition());
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> lines = new();
        public IReadOnlyCollection<string> Lines => lines;
        public void Emit(LogEvent logEvent) => lines.Enqueue(logEvent.RenderMessage() + " " + string.Join(" ", logEvent.Properties.Select(x => x.Key + "=" + x.Value)) + " " + logEvent.Exception);
    }
}

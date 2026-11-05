using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using CoCern.Core;
using CoCern.Contracts;
using System.Text.Json.Nodes;
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
    public async Task Machine_authentication_keeps_repository_role_and_announced_identity_bound_to_its_credential()
    {
        await using var host = Host();
        var credential = await EnrollAsync(host, "pc-a", "PC A");
        Assert.Equal(64, credential.Length);
        using var a = Client(host, credential);
        using var b = Client(host, await EnrollAsync(host, "pc-b", "PC B"));
        using var anonymous = Client(host);
        using var configured = Client(host, RelayHostFixture.Token);
        using var admin = Client(host, AdminToken);

        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/tools")).StatusCode);
        var opened = await PostAsync(a, "/api/tools/session_open", new { repositoryId = "repo", requestId = "open-1" });
        Assert.Equal(HttpStatusCode.OK, opened.Status);
        using var openedData = JsonDocument.Parse(opened.Body.GetProperty("dataJson").GetString()!);
        var aSession = openedData.RootElement.GetProperty("sessionId").GetString()!;

        var denied = await PostAsync(a, "/api/tools/control_request", new { repositoryId = "repo", requestId = "c-1", command = "revoke_machine", machineId = "pc-a" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("AdministratorRequired", denied.Body.GetProperty("code").GetString());
        using var tools = JsonDocument.Parse(await a.GetStringAsync("/api/tools"));
        Assert.DoesNotContain(tools.RootElement.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "control_request");

        denied = await PostAsync(a, "/api/tools/get_status", new { repositoryId = "repo-not-listed" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("RepositoryDenied", denied.Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(a, "/api/tools/session_open", new { repositoryId = "repo-not-listed", requestId = "x" })).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, "/api/discovery/announce", Announcement())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(configured, "/api/discovery/announce", Announcement())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(admin, "/api/discovery/announce", Announcement())).Status);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(b, "/api/discovery/announce", new { machineName = "PC B", chats = new[] { Chat(thread: "tb", session: await OpenSessionAsync(b)) } })).Status);
        // A's credential, not its supplied machineId, determines which machine it can announce.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(a, "/api/discovery/announce", new { machineId = "pc-b", machineName = "PC A", chats = new[] { Chat(thread: "ta", session: aSession) } })).Status);
        var directory = await PostAsync(admin, "/api/tools/chat_directory", new { repositoryId = "repo" });
        using var items = JsonDocument.Parse(directory.Body.GetProperty("dataJson").GetString()!);
        var rows = items.RootElement.GetProperty("items").EnumerateArray().Select(x => (x.GetProperty("machineName").GetString(), x.GetProperty("label").GetString())).OrderBy(x => x.Item2).ToArray();
        Assert.Equal([("PC A", "Chat ta"), ("PC B", "Chat tb")], rows);
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
        var chats = items.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { own, others, users }.Order(), chats.Select(x => x.GetProperty("relaySessionId").GetString()).Order());
        Assert.True(Assert.Single(chats, x => x.GetProperty("relaySessionId").GetString() == own).GetProperty("reachable").GetBoolean());
        foreach (var local in chats.Where(x => x.GetProperty("relaySessionId").GetString() != own))
        {
            Assert.False(local.GetProperty("reachable").GetBoolean());
            Assert.False(local.TryGetProperty("provider", out _));
        }
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

    private static Microsoft.AspNetCore.Http.DefaultHttpContext From(string address) => new() { Connection = { RemoteIpAddress = IPAddress.Parse(address) } };

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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PkRelay.Core;
using PkRelay.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using PkRelay.Storage;
using Xunit;

namespace PkRelay.Tests;

public class HostTests
{
    [Fact]
    public async Task DueDispatcherAdvancesPast64ThrowingOwnersToHealthyWork()
    {
        var source = new FairnessDueSource();
        var delivered = new List<string>();
        var failures = 0;
        var pump = new PkRelay.Host.DueWorkPump(source, (work, _) =>
        {
            if (work.WorkId != "healthy") throw new IOException("Owner unavailable.");
            delivered.Add(work.WorkId);
            return Task.CompletedTask;
        }, () => failures++);
        Assert.Equal(64, await pump.DispatchNextBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Empty(delivered);
        Assert.Equal(1, await pump.DispatchNextBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(new[] { "healthy" }, delivered);
        Assert.Equal(64, failures);
        Assert.Equal(new[] { 0, 64 }, source.Offsets);
        await pump.DispatchNextBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, source.Offsets[^1]);
    }

    private sealed class FairnessDueSource : IDueWorkSource
    {
        public List<int> Offsets { get; } = [];
        public Task<IReadOnlyList<DueWorkReference>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default, int offset = 0)
        {
            Assert.Equal(64, limit);
            Offsets.Add(offset);
            IReadOnlyList<DueWorkReference> rows = Enumerable.Range(0, 65).Skip(offset).Take(limit).Select(index => new DueWorkReference("session", "pkrelay.session", "owner" + index, index == 64 ? "healthy" : index.ToString(), "effect", now)).ToArray();
            return Task.FromResult(rows);
        }
    }

    [Fact]
    public async Task RequiresAuthenticationButPublishesSafeMetadata()
    {
        await using var host = new RelayHostFixture();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "repo" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/hubs/coordination/negotiate?negotiateVersion=1", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var spec = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/tools/", spec);
        Assert.DoesNotContain(RelayHostFixture.Token, spec);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar")).StatusCode);
    }

    [Fact]
    public async Task RoutesRealCallsAndRejectsRepositoryAndIdentityForgery()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "forbidden", principalId = "admin", isAdministrator = true })).StatusCode);
        var opened = await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "one", principalId = "forged" });
        Assert.True(opened.GetProperty("ok").GetBoolean());
        using var sessionDocument = JsonDocument.Parse(opened.GetProperty("dataJson").GetString()!);
        var session = sessionDocument.RootElement;
        var forged = await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = "wrong" });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/tools/execute_ide", new { repositoryId = "repo" })).StatusCode);
        Assert.DoesNotContain(RelayHostFixture.Token, await client.GetStringAsync("/api/tools"));
    }

    [Fact]
    public async Task RejectsMalformedAndOversizedJson()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/tools/get_status", new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/tools/get_status", new StringContent("{\"padding\":\"" + new string('x', 33000) + "\"}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task McpSdkNegotiatesListsAndCallsAuthenticatedCoordination()
    {
        await using var host = new RelayHostFixture();
        using var http = host.AuthorizedClient();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await mcp.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, tool => tool.Name == "session_open");
        Assert.DoesNotContain(tools, tool => tool.Name == "control_request" || tool.Name.Contains("debugger"));
        var result = await mcp.CallToolAsync("session_open", new Dictionary<string, object?> { ["repositoryId"] = "repo", ["requestId"] = "mcp-open", ["principalId"] = "forged", ["isAdministrator"] = true }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError);
        using var reply = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        var denied = await mcp.CallToolAsync("get_status", new Dictionary<string, object?> { ["repositoryId"] = "forbidden" }, cancellationToken: timeout.Token);
        Assert.True(denied.IsError);
    }

    [Fact]
    public async Task SignalRValidatesSessionHandleAndOnlySendsHintsToWatchedSession()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var first = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "a" }));
        var second = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "b" }));
        await using var a = host.Hub();
        await using var b = host.Hub();
        var aEvents = new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
        var bEvent = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.On<JsonElement>("coordination.changed", hint => aEvents.Enqueue(hint));
        b.On<JsonElement>("coordination.changed", hint => bEvent.TrySetResult(hint));
        await a.StartAsync();
        await b.StartAsync();
        await a.InvokeAsync("WatchSession", first.GetProperty("sessionId").GetString(), first.GetProperty("sessionToken").GetString(), "repo");
        await Assert.ThrowsAsync<HubException>(() => a.InvokeAsync("WatchSession", second.GetProperty("sessionId").GetString(), first.GetProperty("sessionToken").GetString(), "repo"));
        await b.InvokeAsync("WatchSession", second.GetProperty("sessionId").GetString(), second.GetProperty("sessionToken").GetString(), "repo");
        await host.Services.GetRequiredService<ICoordinationNotifier>().NotifyAsync(second.GetProperty("sessionId").GetString()!, "hint-only");
        var hint = await bEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hint-only", hint.GetProperty("eventId").GetString());
        Assert.DoesNotContain("sessionToken", hint.GetRawText());
        await Task.Delay(200);
        Assert.Empty(aEvents);
    }

    [Fact]
    public async Task SqliteRestartScansQueuedEffectsAndPreservesPrivateUnreadInbox()
    {
        var path = Path.Combine(Path.GetTempPath(), "pkrelay-restart-" + Guid.NewGuid().ToString("N"), "relay.db");
        JsonElement first;
        JsonElement second;
        await using (var initial = new RelayHostFixture(path, disableDispatcher: true))
        {
            using var client = initial.AuthorizedClient();
            first = Data(await initial.Call(client, "session_open", new { repositoryId = "repo", requestId = "sender" }));
            second = Data(await initial.Call(client, "session_open", new { repositoryId = "repo", requestId = "receiver" }));
            await initial.Call(client, "send_message", new { repositoryId = "repo", requestId = "send", sessionId = first.GetProperty("sessionId").GetString(), sessionToken = first.GetProperty("sessionToken").GetString(), recipientSessionId = second.GetProperty("sessionId").GetString(), text = "restart delivery" });
            Assert.NotEmpty(await initial.Services.GetRequiredService<IDueWorkSource>().ReadDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 64));
            Assert.Empty(Data(await initial.Call(client, "check_inbox", SessionArgs(second))).GetProperty("items").EnumerateArray());
        }
        await using (var restarted = new RelayHostFixture(path))
        {
            using var client = restarted.AuthorizedClient();
            JsonElement inbox = default;
            for (var retry = 0; retry < 40; retry++)
            {
                inbox = Data(await restarted.Call(client, "check_inbox", SessionArgs(second)));
                if (inbox.GetProperty("items").GetArrayLength() > 0) break;
                await Task.Delay(100);
            }
            Assert.Single(inbox.GetProperty("items").EnumerateArray());
            Assert.Contains("restart delivery", inbox.GetRawText());
            Assert.Single(Data(await restarted.Call(client, "check_inbox", SessionArgs(second))).GetProperty("items").EnumerateArray());
            Assert.Empty(Data(await restarted.Call(client, "check_inbox", SessionArgs(first))).GetProperty("items").EnumerateArray());
            var forged = await client.PostAsJsonAsync("/api/tools/check_inbox", new { repositoryId = "repo", sessionId = second.GetProperty("sessionId").GetString(), sessionToken = first.GetProperty("sessionToken").GetString() });
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        }
    }

    [Fact]
    public async Task DoesNotTrustForwardedLoopbackOrEnableCrossOriginAccess()
    {
        await using var host = new RelayHostFixture();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayHostFixture.Token);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "repo" })).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.False((await client.GetAsync("/health")).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AdapterReadsQuoteHeavyTaskAfterTwoLegalPatchesWithinWireBound()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "quote-session" }));
        var sessionId = session.GetProperty("sessionId").GetString();
        var sessionToken = session.GetProperty("sessionToken").GetString();
        await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "quote-one", sessionId, sessionToken, taskId = "quotes", expectedVersion = 0, patch = new { first = new string('"', 2400) } });
        await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "quote-two", sessionId, sessionToken, taskId = "quotes", expectedVersion = 1, patch = new { second = new string('"', 2400) } });
        var arguments = JsonSerializer.SerializeToElement(new { repositoryId = "repo", taskId = "quotes" });
        using var response = await client.PostAsJsonAsync("/api/tools/task_get", arguments);
        response.EnsureSuccessStatusCode();
        Assert.InRange((await response.Content.ReadAsByteArrayAsync()).Length, 1, 32768);
        var options = PkRelay.Adapter.AdapterOptions.FromEnvironment(key => key switch { "PKRELAY_URL" => "https://localhost", "PKRELAY_TOKEN" => RelayHostFixture.Token, _ => null });
        var forwarded = await new PkRelay.Adapter.RelayForwarder(client, options).ForwardAsync("task_get", arguments);
        Assert.True(forwarded.Ok, forwarded.Code);
        Assert.Equal(2, forwarded.Version);
    }

    [Fact]
    public async Task AdapterPreservesRealHttpStaleVersionFailure()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "stale-session" }));
        var sessionId = session.GetProperty("sessionId").GetString();
        var sessionToken = session.GetProperty("sessionToken").GetString();
        await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "create", sessionId, sessionToken, taskId = "stale", expectedVersion = 0 });
        var forwarder = new PkRelay.Adapter.RelayForwarder(client, new PkRelay.Adapter.AdapterOptions("https://localhost", RelayHostFixture.Token));
        var result = await forwarder.ForwardAsync("task_update", JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "stale", sessionId, sessionToken, taskId = "stale", expectedVersion = 0 }));
        Assert.False(result.Ok);
        Assert.Equal("VersionConflict", result.Code);
        Assert.Equal(1, result.Version);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("other", null, false)]
    [InlineData("relay", null, true)]
    [InlineData(null, "relay", true)]
    public async Task JwtRequiresEnrolledSubjectAndResourceScope(string? scope, string? scp, bool allowed)
    {
        await using var host = new RelayHostFixture(configureJwt: true);
        using var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayHostFixture.Jwt(scope, scp));
        var response = await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "repo", isAdministrator = true });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "other-repository" })).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayHostFixture.Jwt(scope, scp, "not-enrolled"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "repo" })).StatusCode);
        }
    }

    [Theory]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost", true)]
    public async Task JwtRejectsActualRemotePlaintextDespiteForwardedHeaders(string origin, bool allowed)
    {
        await using var host = new RelayHostFixture(configureJwt: true, remoteAddress: IPAddress.Parse("203.0.113.10"));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayHostFixture.Jwt("relay"));
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "repo" })).StatusCode);
    }

    [Fact]
    public async Task OAuthPublishesConfiguredResourceMetadataWithoutCredentialsOrIssuerFetch()
    {
        await using var host = new RelayHostFixture(configureJwt: true);
        using var client = host.CreateClient();
        var metadata = await client.GetStringAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.Contains("https://relay.example/mcp", metadata);
        Assert.Contains(RelayHostFixture.JwtAuthority, metadata);
        Assert.DoesNotContain(RelayHostFixture.Token, metadata);
        var denied = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("resource_metadata", denied.Headers.WwwAuthenticate.ToString());
    }

    private static JsonElement Data(JsonElement reply) { using var document = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!); return document.RootElement.Clone(); }
    private static object SessionArgs(JsonElement session) => new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() };
}

internal sealed class RelayHostFixture : WebApplicationFactory<PkRelay.Host.RelayHostMarker>
{
    public const string Token = "host-test-secret-not-a-production-token";
    public string DatabasePath { get; }
    private readonly bool disableDispatcher;
    private readonly bool configureJwt;
    private readonly IPAddress? remoteAddress;
    public const string JwtAuthority = "https://issuer.example";
    private static readonly SymmetricSecurityKey JwtKey = new(Encoding.UTF8.GetBytes("test-only-signing-key-with-at-least-64-bytes-not-production-key-1234567890"));
    private readonly IReadOnlyDictionary<string, string?>? settings;
    private readonly Action<IServiceCollection>? configureServices;
    public RelayHostFixture(string? databasePath = null, bool disableDispatcher = false, bool configureJwt = false, IPAddress? remoteAddress = null, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null)
    {
        this.settings = settings;
        this.configureServices = configureServices;
        DatabasePath = databasePath ?? Path.Combine(Path.GetTempPath(), "pkrelay-host-" + Guid.NewGuid().ToString("N"), "relay.db");
        this.disableDispatcher = disableDispatcher;
        this.configureJwt = configureJwt;
        this.remoteAddress = remoteAddress;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Relay:DatabasePath"] = DatabasePath,
            ["Relay:SiloPort"] = FreePort().ToString(),
            ["Relay:GatewayPort"] = FreePort().ToString(),
            ["Relay:ClusterId"] = Guid.NewGuid().ToString("N"),
            ["Relay:Clients:0:Token"] = Token,
            ["Relay:Clients:0:PrincipalId"] = "test-user",
            ["Relay:Clients:0:Repositories:0"] = "repo"
        }));
        if (settings is not null) builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        if (configureServices is not null) builder.ConfigureServices(configureServices);
        if (configureJwt)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:OAuth:Authority"] = JwtAuthority,
                ["Relay:OAuth:Audience"] = "pkrelay-tests",
                ["Relay:OAuth:Resource"] = "https://relay.example/mcp",
                ["Relay:OAuth:RequiredScopes:0"] = "relay",
                ["Relay:OAuth:Principals:0:Subject"] = "enrolled-subject",
                ["Relay:OAuth:Principals:0:PrincipalId"] = "jwt-user",
                ["Relay:OAuth:Principals:0:Repositories:0"] = "repo"
            }));
            builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>("ExternalJwt", options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = JwtAuthority };
                options.Configuration.SigningKeys.Add(JwtKey);
                options.TokenValidationParameters.ValidIssuer = JwtAuthority;
                options.TokenValidationParameters.ValidAudience = "pkrelay-tests";
                options.TokenValidationParameters.IssuerSigningKey = JwtKey;
            }));
        }
        if (remoteAddress is not null) builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteConnectionStartupFilter(remoteAddress)));
        if (disableDispatcher) builder.ConfigureServices(services =>
        {
            var dispatcher = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "DueWorkDispatcher");
            services.Remove(dispatcher);
        });
    }

    public HttpClient AuthorizedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(Path.GetFullPath(DatabasePath), Services.GetRequiredService<SqliteStore>().DatabasePath);
        var configuration = Services.GetRequiredService<IConfiguration>();
        var endpoints = Services.GetRequiredService<IOptions<EndpointOptions>>().Value;
        Assert.Equal(configuration.GetValue<int>("Relay:SiloPort"), endpoints.SiloPort);
        Assert.Equal(configuration.GetValue<int>("Relay:GatewayPort"), endpoints.GatewayPort);
        Assert.Equal(configuration["Relay:ClusterId"], Services.GetRequiredService<IOptions<ClusterOptions>>().Value.ClusterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public async Task<JsonElement> Call(HttpClient client, string tool, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, arguments);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    public HubConnection Hub(string? token = null) => new HubConnectionBuilder().WithUrl("https://localhost/hubs/coordination", options =>
    {
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(token ?? Token);
        options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
    }).Build();

    public static string Jwt(string? scope = null, string? scp = null, string subject = "enrolled-subject")
    {
        var claims = new List<Claim> { new("sub", subject), new("isAdministrator", "true") };
        if (scope is not null) claims.Add(new("scope", scope));
        if (scp is not null) claims.Add(new("scp", scp));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = JwtAuthority, Audience = "pkrelay-tests", Subject = new ClaimsIdentity(claims), Expires = DateTime.UtcNow.AddMinutes(5), SigningCredentials = new SigningCredentials(JwtKey, SecurityAlgorithms.HmacSha256) });
    }

    private sealed class RemoteConnectionStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continueRequest) => { context.Connection.RemoteIpAddress = address; return continueRequest(context); });
            next(app);
        };
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

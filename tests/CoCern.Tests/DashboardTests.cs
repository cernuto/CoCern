using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using CoCern.Host.Dashboard;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardTests
{

    [Fact]
    public async Task Traffic_reports_actual_correlated_calls_and_pushes_without_payload_or_observer_feedback()
    {
        await using var host = OperatorHost();
        using var browser = BrowserClient(host);
        (await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
        await using var viewer = await StartViewerAsync(host, browser);
        using var agent = host.AuthorizedClient();
        var openedReply = await host.Call(agent, "session_open", new { repositoryId = "repo", requestId = "traffic-open" });
        using var opened = JsonDocument.Parse(openedReply.GetProperty("dataJson").GetString()!);
        var sessionId = opened.RootElement.GetProperty("sessionId").GetString()!;
        var denied = await agent.PostAsJsonAsync("/api/tools/get_status", new { repositoryId = "forbidden", sessionId = "PRIVATE-FAKE-SESSION", sessionToken = "PRIVATE-TOKEN", text = "PRIVATE-MESSAGE" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await host.Services.GetRequiredService<ICoordinationNotifier>().NotifyAsync(sessionId, "PRIVATE-EVENT-ID");
        CoCern.Contracts.Dashboard.DashboardSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 70; attempt++)
        {
            snapshot = await browser.GetFromJsonAsync<CoCern.Contracts.Dashboard.DashboardSnapshot>("/dashboard/api/snapshot");
            if (snapshot!.Traffic.Any(item => item.Kind == "push")) break;
            await Task.Delay(50);
        }
        var calls = snapshot!.Traffic.Where(item => item.Name == "session_open").ToArray();
        Assert.Equal(new[] { "request", "response" }, calls.Select(item => item.Kind));
        Assert.NotNull(calls[0].CorrelationId);
        Assert.Equal(calls[0].CorrelationId, calls[1].CorrelationId);
        Assert.NotNull(calls[1].DurationMilliseconds);
        Assert.Contains(snapshot.Traffic, item => item.Kind == "push" && item.Name == "coordination.changed" && item.SessionId == sessionId);
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(snapshot.Traffic));
        Assert.DoesNotContain(snapshot.Traffic, item => item.Name.Contains("dashboard", StringComparison.OrdinalIgnoreCase));
        Assert.True(snapshot.Traffic.Length <= 120);
    }

    [Fact]
    public async Task Dashboard_serves_its_wasm_shell_and_deep_links_without_capturing_other_routes()
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host);
        var shell = await client.GetAsync("/dashboard/");
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Contains("<base href=\"/dashboard/\"", await shell.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/unrelated/deep-link")).StatusCode);
    }

    [Theory]
    [InlineData("Relay:Dashboard:ViewerPrincipalIds:0", "revoked")]
    [InlineData("Relay:Clients:0:IsAdministrator", "false")]
    [InlineData("Relay:Clients:0:PrincipalId", "removed")]
    [InlineData("Relay:Clients:0:Token", "PRIVATE-ROTATED-TEST-CREDENTIAL")]
    public async Task Revoking_operator_policy_closes_the_existing_observer_stream_and_rejects_cookie(string key, string value)
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host);
        var login = await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        login.EnsureSuccessStatusCode();
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        await using var connection = new HubConnectionBuilder().WithUrl("https://localhost/dashboard/hub", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
            options.Headers["Cookie"] = cookie;
            options.Headers["Origin"] = "https://localhost";
        }).Build();
        var frame = new TaskCompletionSource<CoCern.Contracts.Dashboard.DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<CoCern.Contracts.Dashboard.DashboardSnapshot>("snapshot", snapshot => frame.TrySetResult(snapshot));
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await connection.StartAsync();
        var observed = await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(observed.InstanceId);
        Assert.True((await connection.InvokeAsync<CoCern.Contracts.Dashboard.DashboardSnapshot>("GetSnapshot")).Revision >= observed.Revision);
        host.Services.GetRequiredService<IConfiguration>()[key] = value;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/auth")).StatusCode);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dashboard_requires_its_cookie_even_when_relay_bearer_is_valid()
    {
        await using var host = OperatorHost();
        using var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/auth")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/dashboard/hub/negotiate?negotiateVersion=1", new { })).StatusCode);
    }

    [Fact]
    public async Task Only_explicitly_allowlisted_admin_can_exchange_a_token_for_viewer_cookie()
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host);
        var response = await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RelayHostFixture.Token, cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard/api/auth")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard/api/snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/dashboard/api/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/auth")).StatusCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Admin_role_and_explicit_instance_grant_are_both_required(bool administrator, bool grant)
    {
        await using var host = OperatorHost(administrator: administrator, grant: grant);
        using var client = BrowserClient(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://attacker.example")]
    [InlineData("null")]
    public async Task Login_rejects_missing_or_cross_origin_requests(string? origin)
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host, origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).StatusCode);
    }

    [Fact]
    public async Task Remote_plaintext_login_is_rejected_despite_forwarded_headers()
    {
        await using var host = OperatorHost(remoteAddress: IPAddress.Parse("192.0.2.7"));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).StatusCode);
    }

    private static RelayHostFixture OperatorHost(bool administrator = true, bool grant = true, IPAddress? remoteAddress = null) => new(remoteAddress: remoteAddress, settings: new Dictionary<string, string?>
    {
        ["Relay:Clients:0:IsAdministrator"] = administrator.ToString(),
        ["Relay:Dashboard:ViewerPrincipalIds:0"] = grant ? "test-user" : "different-user"
    });

    private static HttpClient BrowserClient(RelayHostFixture host, string? origin = "https://localhost")
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (origin is not null) client.DefaultRequestHeaders.Add("Origin", origin);
        return client;
    }

    private static async Task<HubConnection> StartViewerAsync(RelayHostFixture host, HttpClient browser)
    {
        var login = await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        login.EnsureSuccessStatusCode();
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var viewer = new HubConnectionBuilder().WithUrl("https://localhost/dashboard/hub", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
            options.Headers["Cookie"] = cookie;
            options.Headers["Origin"] = "https://localhost";
        }).Build();
        try { await viewer.StartAsync(); return viewer; }
        catch { await viewer.DisposeAsync(); throw; }
    }

    private sealed class QuietChatSource : IRelayObservationSource
    {
        public Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default) => Task.FromResult<RelayObservation?>(new()
        {
            WaitingMail = [new WaitingMail { SessionId = "quiet", RepositoryId = "repo", Sent = [now.AddHours(-1)], LastCallAt = now.AddHours(-2) }]
        });
        public Task<CoCern.Contracts.Dashboard.DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default) => Task.FromResult<CoCern.Contracts.Dashboard.DatabaseMetrics?>(new());
    }

    private sealed class TruncatedWaitingSource : IRelayObservationSource
    {
        public Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default)
        {
            var text = new string('\u754c', 256);
            return Task.FromResult<RelayObservation?>(new()
            {
                SessionsTruncated = true,
                Sessions = Enumerable.Range(0, 200).Select(index => new CoCern.Contracts.Dashboard.DashboardSession { SessionId = index.ToString(), Label = text, Provider = text, MachineId = text, MachineName = text, RepositoryId = text }).ToArray()
            });
        }
        public Task<CoCern.Contracts.Dashboard.DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default) => Task.FromResult<CoCern.Contracts.Dashboard.DatabaseMetrics?>(new());
        public Task<DashboardStandingObservation?> ReadStandingAsync(CancellationToken cancellationToken = default) => Task.FromResult<DashboardStandingObservation?>(new()
        {
            Boards = [new CoCern.Contracts.Dashboard.DashboardStanding { RepositoryId = "repo", BoardId = "default", Proposals = [new CoCern.Contracts.Dashboard.DashboardStandingProposal { Id = "p-1", Kind = "line", Summary = "A line", ProposedAt = DateTimeOffset.UtcNow }] }]
        });
    }

    private sealed class LargeObservationSource : IRelayObservationSource
    {
        public Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default)
        {
            var text = new string('\u754c', 256);
            return Task.FromResult<RelayObservation?>(new()
            {
                Sessions = Enumerable.Range(0, 200).Select(index => new CoCern.Contracts.Dashboard.DashboardSession { SessionId = index.ToString(), Label = text, Provider = text, MachineId = text, MachineName = text, RepositoryId = text }).ToArray(),
                Machines = Enumerable.Range(0, 256).Select(index => new CoCern.Contracts.Dashboard.DashboardMachine { MachineId = index.ToString(), Name = text }).ToArray()
            });
        }
        public Task<CoCern.Contracts.Dashboard.DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default) => Task.FromResult<CoCern.Contracts.Dashboard.DatabaseMetrics?>(new());
    }
}

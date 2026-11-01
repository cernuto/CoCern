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
using PkRelay.Core;
using PkRelay.Domain;
using PkRelay.Storage;
using PkRelay.Host.Dashboard;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace PkRelay.Tests;

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
        PkRelay.Contracts.Dashboard.DashboardSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 70; attempt++)
        {
            snapshot = await browser.GetFromJsonAsync<PkRelay.Contracts.Dashboard.DashboardSnapshot>("/dashboard/api/snapshot");
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
    public async Task Serialized_observation_frame_is_bounded_and_reports_trimming()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true", ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user" }, configureServices: services => services.AddSingleton<IRelayObservationSource>(new LargeObservationSource()));
        using var client = BrowserClient(host);
        (await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
        await using var viewer = await StartViewerAsync(host, client);
        byte[] bytes = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            bytes = await client.GetByteArrayAsync("/dashboard/api/snapshot");
            if (JsonSerializer.Deserialize<PkRelay.Contracts.Dashboard.DashboardSnapshot>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Revision > 0) break;
            await Task.Delay(50);
        }
        Assert.True(bytes.Length <= 256 * 1024);
        var snapshot = JsonSerializer.Deserialize<PkRelay.Contracts.Dashboard.DashboardSnapshot>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(snapshot.SessionsTruncated);
        Assert.NotEmpty(snapshot.Warnings);
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
        var frame = new TaskCompletionSource<PkRelay.Contracts.Dashboard.DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<PkRelay.Contracts.Dashboard.DashboardSnapshot>("snapshot", snapshot => frame.TrySetResult(snapshot));
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await connection.StartAsync();
        var observed = await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(observed.InstanceId);
        Assert.True((await connection.InvokeAsync<PkRelay.Contracts.Dashboard.DashboardSnapshot>("GetSnapshot")).Revision >= observed.Revision);
        host.Services.GetRequiredService<IConfiguration>()[key] = value;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/auth")).StatusCode);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Same_origin_cookie_polling_get_does_not_require_an_origin_header()
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host);
        var login = await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        // An unknown connection is404; rejecting a valid same-origin transport before SignalR would be403.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dashboard/hub?id=missing-connection")).StatusCode);
    }

    [Fact]
    public void Logs_accept_only_fixed_safe_templates_and_bound_the_ring()
    {
        var logs = new DashboardLogBuffer();
        var parser = new MessageTemplateParser();
        logs.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, new Exception("PRIVATE-EXCEPTION"), parser.Parse("PRIVATE-ARBITRARY-MESSAGE"), []));
        logs.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms"), [new("RequestPath", new ScalarValue("/dashboard/api/login")), new("StatusCode", new ScalarValue(200))]));
        Assert.Empty(logs.Snapshot());
        for (var i = 0; i < 205; i++)
            logs.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, new Exception("PRIVATE-EXCEPTION"), parser.Parse("HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms"), [new("RequestPath", new ScalarValue("/api/tools/PRIVATE-PATH?token=PRIVATE-TOKEN")), new("StatusCode", new ScalarValue(200)), new("RequestBody", new ScalarValue("PRIVATE-BODY"))]));
        var entries = logs.Snapshot();
        Assert.Equal(200, entries.Length);
        Assert.Equal(6, entries[0].Sequence);
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(entries));
    }

    [Fact]
    public async Task Observation_limits_session_count_and_exposes_truncation()
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "pkrelay-dashboard-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var writer = new SqliteGrainStorage(store);
        for (var i = 0; i < 205; i++)
        {
            var state = new GrainState<StateEnvelope<CoordinationDocument>>(new() { Value = new() { Json = new JsonObject { ["sessionId"] = "session-" + i, ["repositoryId"] = "repo", ["label"] = "Session " + i }.ToJsonString() } });
            await writer.WriteStateAsync("session", GrainId.Create("pkrelay.session", "repo/" + i), state);
        }
        var source = Assert.IsAssignableFrom<IRelayObservationSource>(store);
        var observation = Assert.IsType<RelayObservation>(await source.ReadObservationAsync(DateTimeOffset.UtcNow, "test-incarnation"));
        Assert.InRange(observation.Sessions.Length, 1, 200);
        Assert.Equal(205, observation.SessionCount);
        Assert.True(observation.SessionsTruncated);
        Assert.NotEmpty(observation.Warnings);
    }

    [Fact]
    public async Task Observation_joins_real_session_and_discovery_metadata_without_consuming_private_mail()
    {
        var path = Path.Combine(Path.GetTempPath(), "pkrelay-dashboard-" + Guid.NewGuid().ToString("N"), "relay.db");
        using var store = new SqliteStore(path);
        var now = DateTimeOffset.UtcNow;
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new()
        {
            Value = new() { Json = new JsonObject
            {
                ["sessionId"] = "session-a", ["repositoryId"] = "repo", ["machineId"] = "pc-a", ["label"] = "Real session", ["sessionToken"] = "PRIVATE-SESSION-SECRET",
                ["observedAt"] = now.ToString("O"), ["freshUntil"] = now.AddMinutes(1).ToString("O"),
                ["inbox"] = new JsonObject { ["mail"] = new JsonObject { ["text"] = "PRIVATE-MESSAGE-TEXT" } }
            }.ToJsonString() }
        });
        var writer = new SqliteGrainStorage(store);
        await writer.WriteStateAsync("session", GrainId.Create("pkrelay.session", "repo/session-a"), state);
        var discovery = new GrainState<StateEnvelope<CoordinationDocument>>(new()
        {
            Value = new() { Json = new JsonObject { ["machines"] = new JsonObject { ["pc-a"] = new JsonObject
            {
                ["machineName"] = "PC A", ["lastSeen"] = now.ToString("O"), ["credentialHash"] = "PRIVATE-CREDENTIAL-HASH",
                ["chats"] = new JsonArray(new JsonObject { ["relaySessionId"] = "session-a", ["repositoryId"] = "repo", ["provider"] = "codex", ["threadId"] = "PRIVATE-THREAD", ["label"] = "Real session", ["wakeable"] = true })
            } } }.ToJsonString() }
        });
        await writer.WriteStateAsync("discovery", GrainId.Create("pkrelay.discovery", "machines"), discovery);
        var source = Assert.IsAssignableFrom<IRelayObservationSource>(store);
        var observation = Assert.IsType<RelayObservation>(await source.ReadObservationAsync(now, "test-incarnation"));
        for (var attempt = 0; observation.SessionsTruncated && attempt < 20; attempt++)
        {
            await Task.Delay(50);
            observation = Assert.IsType<RelayObservation>(await source.ReadObservationAsync(now, "test-incarnation"));
        }
        Assert.False(observation.SessionsTruncated, JsonSerializer.Serialize(observation));
        var session = Assert.Single(observation.Sessions);
        Assert.Equal("session-a", session.SessionId);
        Assert.Equal("PC A", session.MachineName);
        Assert.True(session.Reachable);
        Assert.True(session.IsMapped);
        Assert.Equal(1, session.UnreadMessages);
        Assert.Equal(0, session.PendingEffects);
        Assert.Equal("pc-a", Assert.Single(observation.Machines).MachineId);
        var json = JsonSerializer.Serialize(observation);
        Assert.DoesNotContain("PRIVATE-", json);
        var read = new GrainState<StateEnvelope<CoordinationDocument>>();
        await writer.ReadStateAsync("session", GrainId.Create("pkrelay.session", "repo/session-a"), read);
        Assert.Equal(state.ETag, read.ETag);
        Assert.Equal(Assert.IsType<StateEnvelope<CoordinationDocument>>(state.State).Value.Json, Assert.IsType<StateEnvelope<CoordinationDocument>>(read.State).Value.Json);
    }

    [Fact]
    public async Task Sql_metrics_report_physical_tables_and_label_a_capped_count_as_lower_bound()
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "pkrelay-dashboard-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var source = Assert.IsAssignableFrom<IRelayObservationSource>(store);
        var metrics = Assert.IsType<PkRelay.Contracts.Dashboard.DatabaseMetrics>(await source.ReadDatabaseMetricsAsync(rowCountLimit: 2));
        Assert.Equal("SQLite", metrics.Provider);
        Assert.Equal(new[] { "activity_daily", "activity_log", "due_work", "grain_state", "hub_metadata", "search_index" }, metrics.Tables.Select(table => table.Name));
        var metadata = Assert.Single(metrics.Tables, table => table.Name == "hub_metadata");
        Assert.Equal(2, metadata.RowCount);
        Assert.Equal("lower-bound", metadata.CountKind);
        Assert.Equal("exact", Assert.Single(metrics.Tables, table => table.Name == "grain_state").CountKind);
        Assert.NotNull(metrics.ObservedAt);
        Assert.True(metrics.SizeBytes > 0);
        Assert.All(metrics.Tables, table => Assert.Null(table.SizeBytes));
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

    [Fact]
    public async Task Standing_panel_shows_lines_grants_provenance_proposals_and_history()
    {
        await using var host = OperatorHost();
        using var agent = host.AuthorizedClient();
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var put = await host.Call(agent, "standing_put", new { repositoryId = "repo", requestId = "panel-put", expectedRevision = 0, lines = new[] { new { id = "css", text = "Never deploy CSS without a restoration receipt." } }, grants = new[] { new { id = "rig", scope = "resource", targets = new[] { "rig-1" }, expiresAt = expires } } });
        Assert.True(put.GetProperty("ok").GetBoolean());
        using var openedDocument = JsonDocument.Parse((await host.Call(agent, "session_open", new { repositoryId = "repo", requestId = "panel-session" })).GetProperty("dataJson").GetString()!);
        var opened = openedDocument.RootElement;
        Assert.True((await host.Call(agent, "standing_propose", new { repositoryId = "repo", requestId = "panel-propose", sessionId = opened.GetProperty("sessionId").GetString(), sessionToken = opened.GetProperty("sessionToken").GetString(), line = new { id = "css", text = "Deploy CSS only with a receipt." } })).GetProperty("ok").GetBoolean());

        using var browser = BrowserClient(host);
        (await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
        await using var viewer = await StartViewerAsync(host, browser);
        PkRelay.Contracts.Dashboard.DashboardSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            snapshot = await browser.GetFromJsonAsync<PkRelay.Contracts.Dashboard.DashboardSnapshot>("/dashboard/api/snapshot");
            if (snapshot!.Standing.Length > 0) break;
            await Task.Delay(50);
        }
        var board = Assert.Single(snapshot!.Standing);
        Assert.Equal("repo/default", board.Board);
        Assert.Equal(1, board.Revision);
        var line = Assert.Single(board.Lines);
        Assert.Equal(("css", "test-user", "test-user"), (line.Id, line.Author, line.ApprovedBy));
        var grant = Assert.Single(board.Grants);
        Assert.Equal(("rig", "resource", "test-user"), (grant.Id, grant.Scope, grant.Grantor));
        Assert.Equal(["rig-1"], grant.Targets);
        var proposal = Assert.Single(board.Proposals);
        Assert.True(proposal.Replaces);
        Assert.Equal("Deploy CSS only with a receipt.", proposal.Summary);
        Assert.Equal(["+line:css", "+grant:rig"], Assert.Single(board.History).Changes);
        Assert.DoesNotContain(opened.GetProperty("sessionToken").GetString()!, JsonSerializer.Serialize(snapshot));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/dashboard/standing")).StatusCode);
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

    private sealed class LargeObservationSource : IRelayObservationSource
    {
        public Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default)
        {
            var text = new string('\u754c', 256);
            return Task.FromResult<RelayObservation?>(new()
            {
                Sessions = Enumerable.Range(0, 200).Select(index => new PkRelay.Contracts.Dashboard.DashboardSession { SessionId = index.ToString(), Label = text, Provider = text, MachineId = text, MachineName = text, RepositoryId = text }).ToArray(),
                Machines = Enumerable.Range(0, 256).Select(index => new PkRelay.Contracts.Dashboard.DashboardMachine { MachineId = index.ToString(), Name = text }).ToArray()
            });
        }
        public Task<PkRelay.Contracts.Dashboard.DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default) => Task.FromResult<PkRelay.Contracts.Dashboard.DatabaseMetrics?>(new());
    }
}

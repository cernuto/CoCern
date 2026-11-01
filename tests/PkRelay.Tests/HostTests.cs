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
using PkRelay.Contracts;
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

    [Fact]
    public async Task Activity_counts_each_real_call_wake_and_inbox_read_once_and_keeps_usage_out_of_state()
    {
        // With attention on, a message no longer wakes its chat (it waits as an info item), so message wakes are counted with it off.
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Attention:Enabled"] = "false" });
        using var client = host.AuthorizedClient();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var manager = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "activity-manager", role = "Manager" }));
        var worker = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "activity-worker", role = "worker" }));
        string Id(JsonElement session) => session.GetProperty("sessionId").GetString()!;
        string Token(JsonElement session) => session.GetProperty("sessionToken").GetString()!;
        Assert.True((await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "activity-create", sessionId = Id(worker), sessionToken = Token(worker), taskId = "T1", expectedVersion = 0, patch = new { status = "started" }, usage = new { inputTokens = 1200, outputTokens = 80 } })).GetProperty("ok").GetBoolean());
        // The same status again under a new request is a duplicate status write.
        Assert.True((await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "activity-repeat", sessionId = Id(worker), sessionToken = Token(worker), taskId = "T1", expectedVersion = 1, patch = new { status = "started" } })).GetProperty("ok").GetBoolean());
        await host.Call(client, "send_message", new { repositoryId = "repo", requestId = "activity-wake", sessionId = Id(manager), sessionToken = Token(manager), recipientSessionId = Id(worker), text = "please look" });
        await host.Call(client, "send_message", new { repositoryId = "repo", requestId = "activity-quiet", sessionId = Id(manager), sessionToken = Token(manager), recipientSessionId = Id(worker), text = "fyi", wake = false });
        JsonElement inbox = default;
        for (var retry = 0; retry < 40; retry++)
        {
            inbox = Data(await host.Call(client, "check_inbox", SessionArgs(worker)));
            if (inbox.GetProperty("items").GetArrayLength() == 2) break;
            await Task.Delay(100);
        }
        Assert.Equal(2, inbox.GetProperty("items").GetArrayLength());

        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        var counters = await host.Services.GetRequiredService<IActivityCounterSource>().ReadActivityCountersAsync("repo", started, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(1, counters.Wakes);
        // The polling loop above is one burst of reads by one session.
        Assert.Equal(1, counters.InboxReads);
        Assert.Equal(1, counters.DuplicateStatusWrites);
        Assert.Equal(1200, counters.InputTokens);
        Assert.Equal(80, counters.OutputTokens);
        // The manager's session_open and two messages are one burst.
        Assert.Equal(1, counters.ManagerTurns);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role || '|' || code FROM activity_log WHERE tool = 'send_message' LIMIT 1;";
        Assert.Equal("manager|Ok", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM grain_state WHERE payload LIKE '%inputTokens%';";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Usage_on_a_rejected_milestone_is_not_counted()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var worker = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "usage-worker" }));
        var sessionId = worker.GetProperty("sessionId").GetString();
        var sessionToken = worker.GetProperty("sessionToken").GetString();
        var usage = new { inputTokens = 500, outputTokens = 40 };
        Assert.True((await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "usage-create", sessionId, sessionToken, taskId = "U1", expectedVersion = 0 })).GetProperty("ok").GetBoolean());
        // A stale milestone fails, and the agent resends it with the same usage under the current version.
        using (var stale = await client.PostAsJsonAsync("/api/tools/task_update", new { repositoryId = "repo", requestId = "usage-stale", sessionId, sessionToken, taskId = "U1", expectedVersion = 0, patch = new { step = 2 }, usage }))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True((await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "usage-retry", sessionId, sessionToken, taskId = "U1", expectedVersion = 1, patch = new { step = 2 }, usage })).GetProperty("ok").GetBoolean());
        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        var counters = await store.ReadActivityCountersAsync("repo", started, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(500, counters.InputTokens);
        Assert.Equal(40, counters.OutputTokens);
    }

    [Fact]
    public async Task An_offer_whose_claim_fails_counts_as_one_offer_that_ended_blocked()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "offer-first" }));
        var second = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "offer-second" }));
        object Update(JsonElement session, string taskId, string state, bool includeNextWork) => new
        {
            repositoryId = "repo", requestId = Guid.NewGuid().ToString("N"), sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString(),
            taskId, expectedVersion = 0, includeNextWork, workflow = OfferWorkflow(state)
        };
        async Task<HttpStatusCode> Claim(JsonElement session)
        {
            using var response = await client.PostAsJsonAsync("/api/tools/task_claim", new { repositoryId = "repo", requestId = Guid.NewGuid().ToString("N"), sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString(), taskId = "next", expectedVersion = 1, resourceIds = Array.Empty<string>() });
            return response.StatusCode;
        }
        Assert.True((await host.Call(client, "task_update", Update(first, "next", "ready", false))).GetProperty("ok").GetBoolean());
        // Both sessions finish a task and are offered "next".
        foreach (var (session, done) in new[] { (first, "done-first"), (second, "done-second") })
            Assert.Equal("Offered", Data(await host.Call(client, "task_update", Update(session, done, "completed", true))).GetProperty("nextWork").GetProperty("code").GetString());
        // The second session claims it; the first session's claim then fails, so its offer ended blocked.
        Assert.Equal(HttpStatusCode.OK, await Claim(second));
        Assert.Equal(HttpStatusCode.Conflict, await Claim(first));
        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        var counters = await store.ReadActivityCountersAsync("repo", started, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, counters.OffersMade);
        Assert.Equal(1, counters.OffersEndedBlocked);
        Assert.Equal(2, counters.Closures);
    }

    private static object OfferWorkflow(string state) => new
    {
        state,
        packet = new { goal = "Finish a useful leg", target = "repo at revision abc", authorityRef = "human-grant:17", restoration = "Remove owned fixture", stopCondition = "Stop on target mismatch" },
        criteria = new[] { state == "completed" ? (object)new { id = "leg", description = "Leg passes", result = "passed", evidenceRef = "test-report:17" } : new { id = "leg", description = "Leg passes", result = "not_run" } },
        blockers = Array.Empty<object>()
    };

    [Fact]
    public async Task A_failing_activity_sink_never_fails_or_changes_a_call()
    {
        await using var host = new RelayHostFixture(configureServices: services => services.AddSingleton<IActivitySink, ThrowingSink>());
        using var client = host.AuthorizedClient();
        var opened = await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "throwing-sink" });
        Assert.True(opened.GetProperty("ok").GetBoolean());
        var status = await host.Call(client, "get_status", SessionArgs(Data(opened)));
        Assert.True(status.GetProperty("ok").GetBoolean());
        Assert.True(host.Services.GetRequiredService<IActivitySink>() is ThrowingSink { Calls: > 0 });
    }

    private const string AnalyticsAdminToken = "analytics-admin-test-secret-not-production";

    private static async Task<RelayHostFixture> AnalyticsHost(bool enabled, bool adHoc = false, string? timeout = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AnalyticsAdminToken, ["Relay:Clients:1:PrincipalId"] = "analytics-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo",
            ["Relay:Analytics:Enabled"] = enabled.ToString(), ["Relay:Analytics:AllowAdHocSql"] = adHoc.ToString(), ["Relay:Analytics:SnapshotInterval"] = "1.00:00:00",
            ["Relay:Analytics:ExtensionDirectory"] = await AnalyticsTestExtension.DirectoryAsync()
        };
        if (timeout is not null) settings["Relay:Analytics:QueryTimeout"] = timeout;
        var host = new RelayHostFixture(settings: settings);
        if (enabled)
        {
            using var warm = host.AuthorizedClient();
            var analytics = host.Services.GetRequiredService<PkRelay.Analytics.AnalyticsService>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (analytics.SnapshotAt is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
            Assert.NotNull(analytics.SnapshotAt);
        }
        return host;
    }

    private static async Task<(HttpStatusCode Status, string Code, JsonElement Data)> Analytics(HttpClient client, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/analytics_query", arguments);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("code").GetString()!, JsonDocument.Parse(body.GetProperty("dataJson").GetString()!).RootElement.Clone());
    }

    private static async Task<(HttpStatusCode Status, string Code)> AnalyticsStatus(HttpClient client, object arguments)
    {
        var (status, code, _) = await Analytics(client, arguments);
        return (status, code);
    }

    private static HttpClient AnalyticsAdmin(RelayHostFixture host)
    {
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AnalyticsAdminToken);
        return client;
    }

    [Fact]
    public async Task Analytics_query_refuses_non_administrators_and_is_unavailable_when_analytics_is_off()
    {
        await using (var off = await AnalyticsHost(enabled: false))
        {
            using var worker = off.AuthorizedClient();
            using var admin = AnalyticsAdmin(off);
            Assert.Null(off.Services.GetService<IAnalyticsQueries>());
            // The worker learns nothing about the hub's analytics settings.
            Assert.Equal((HttpStatusCode.Forbidden, "AdministratorRequired"), await AnalyticsStatus(worker, new { repositoryId = "repo", name = "noisiest_tools" }));
            Assert.Equal((HttpStatusCode.BadRequest, "AnalyticsUnavailable"), await AnalyticsStatus(admin, new { repositoryId = "repo", name = "noisiest_tools" }));
        }

        await using var on = await AnalyticsHost(enabled: true);
        using var onWorker = on.AuthorizedClient();
        // A session that calls itself a manager is still not an administrator.
        var manager = Data(await on.Call(onWorker, "session_open", new { repositoryId = "repo", requestId = "analytics-manager", role = "manager" }));
        var asManager = await Analytics(onWorker, new { repositoryId = "repo", name = "noisiest_tools", sessionId = manager.GetProperty("sessionId").GetString(), sessionToken = manager.GetProperty("sessionToken").GetString() });
        Assert.Equal((HttpStatusCode.Forbidden, "AdministratorRequired"), (asManager.Status, asManager.Code));
        Assert.Equal((HttpStatusCode.Forbidden, "AdministratorRequired"), await AnalyticsStatus(onWorker, new { repositoryId = "repo", name = "sql", sql = "SELECT 1" }));
        // Over MCP the worker is not even shown the tool.
        Assert.DoesNotContain("analytics_query", PkRelay.Host.ToolResults.Tools(false).Select(tool => tool.Name));
    }

    [Fact]
    public async Task Analytics_query_runs_named_queries_and_refuses_ad_hoc_sql_unless_enabled()
    {
        await using var host = await AnalyticsHost(enabled: true);
        using var admin = AnalyticsAdmin(host);
        var named = await Analytics(admin, new { repositoryId = "repo", name = "noisiest_tools", @params = new { days = 3 } });
        Assert.Equal((HttpStatusCode.OK, "Ok"), (named.Status, named.Code));
        Assert.Equal(["key", "cur", "prev", "cur_n", "prev_n"], named.Data.GetProperty("cols").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(JsonValueKind.Array, named.Data.GetProperty("rows").ValueKind);
        Assert.True(DateTimeOffset.TryParse(named.Data.GetProperty("at").GetString(), out _));

        var refused = await Analytics(admin, new { repositoryId = "repo", name = "sql", sql = "SELECT 1" });
        Assert.Equal((HttpStatusCode.BadRequest, "AnalyticsRefused"), (refused.Status, refused.Code));
        Assert.Contains("turned off", refused.Data.GetProperty("message").GetString());

        foreach (var (arguments, field) in new (object, string)[]
        {
            (new { repositoryId = "repo", name = "drop_tables" }, "name"),
            (new { repositoryId = "repo" }, "name"),
            (new { repositoryId = "repo", name = "noisiest_tools", sql = "SELECT 1" }, "sql"),
            (new { repositoryId = "repo", name = "sql" }, "sql"),
            (new { repositoryId = "repo", name = "noisiest_tools", @params = new { days = 99 } }, "params"),
            (new { repositoryId = "repo", name = "noisiest_tools", @params = new { since = "yesterday" } }, "params"),
            (new { repositoryId = "repo", name = "noisiest_tools", @params = 7 }, "params")
        })
        {
            var invalid = await Analytics(admin, arguments);
            Assert.Equal((HttpStatusCode.BadRequest, "InvalidArguments"), (invalid.Status, invalid.Code));
            Assert.Equal(field, invalid.Data.GetProperty("field").GetString());
        }
    }

    [Fact]
    public async Task Enabled_ad_hoc_sql_stays_read_only_with_the_timeout_row_cap_and_reply_limit()
    {
        await using var host = await AnalyticsHost(enabled: true, adHoc: true, timeout: "00:00:01");
        using var admin = AnalyticsAdmin(host);
        object Sql(string sql) => new { repositoryId = "repo", name = "sql", sql };

        var one = await Analytics(admin, Sql("SELECT 42 AS answer, 'x' AS text, NULL AS nothing"));
        Assert.Equal((HttpStatusCode.OK, "Ok"), (one.Status, one.Code));
        Assert.Equal("[[42,\"x\",null]]", one.Data.GetProperty("rows").GetRawText());
        Assert.False(one.Data.TryGetProperty("truncated", out _));

        var capped = await Analytics(admin, Sql("SELECT range AS n FROM range(100)"));
        Assert.Equal(50, capped.Data.GetProperty("rows").GetArrayLength());
        Assert.True(capped.Data.GetProperty("truncated").GetBoolean());

        foreach (var write in new[] { "CREATE TABLE copied AS SELECT 1", "DETACH relay", "SET enable_external_access = true", $"ATTACH '{host.DatabasePath}' AS live", "SELECT 1; SELECT 2" })
            Assert.Equal((HttpStatusCode.BadRequest, "AnalyticsRefused"), await AnalyticsStatus(admin, Sql(write)));
        Assert.Equal((HttpStatusCode.BadRequest, "AnalyticsFailed"), await AnalyticsStatus(admin, Sql("SELECT getenv('HOME')")));
        var trillion = await Analytics(admin, Sql("SELECT range AS n FROM range(1000000000000)"));
        Assert.Equal(50, trillion.Data.GetProperty("rows").GetArrayLength());
        Assert.True(trillion.Data.GetProperty("truncated").GetBoolean());
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge"), await AnalyticsStatus(admin, Sql("SELECT repeat('x', 10000000) AS filler")));
        var fileRead = await Analytics(admin, Sql($"SELECT * FROM read_text('{host.DatabasePath}')"));
        Assert.Equal((HttpStatusCode.BadRequest, "AnalyticsFailed"), (fileRead.Status, fileRead.Code));
        Assert.Contains("disabled", fileRead.Data.GetProperty("message").GetString());
        Assert.Equal((HttpStatusCode.BadRequest, "AnalyticsTimeout"), await AnalyticsStatus(admin, Sql("SELECT count(*) FROM range(1000000000000) t(a) WHERE a % 7 = 3")));

        var large = await Analytics(admin, Sql("SELECT repeat('x', 1000) AS filler FROM range(20)"));
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge"), (large.Status, large.Code));
        Assert.Contains("narrower", large.Data.GetProperty("message").GetString());
        // The interrupted and refused queries left the snapshot usable.
        Assert.Equal((HttpStatusCode.OK, "Ok"), await AnalyticsStatus(admin, Sql("SELECT count(*) FROM tool_calls")));
    }

    private sealed class ThrowingSink : IActivitySink
    {
        public int Calls;
        public void Record(ActivityEvent activity) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Telemetry unavailable."); }
    }

    [Fact]
    public async Task Search_is_scoped_to_the_repository_and_the_callers_own_inbox_and_needs_a_session()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Clients:0:Repositories:1"] = "other" });
        using var client = host.AuthorizedClient();
        var alice = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "search-alice" }));
        var bob = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "search-bob" }));
        var carol = Data(await host.Call(client, "session_open", new { repositoryId = "other", requestId = "search-carol" }));
        string Id(JsonElement session) => session.GetProperty("sessionId").GetString()!;
        string Token(JsonElement session) => session.GetProperty("sessionToken").GetString()!;
        Assert.True((await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "search-t1", sessionId = Id(alice), sessionToken = Token(alice), taskId = "T-1", expectedVersion = 0, notes = "refresh timeout after idle" })).GetProperty("ok").GetBoolean());
        Assert.True((await host.Call(client, "task_update", new { repositoryId = "other", requestId = "search-x1", sessionId = Id(carol), sessionToken = Token(carol), taskId = "X-1", expectedVersion = 0, notes = "refresh timeout elsewhere" })).GetProperty("ok").GetBoolean());
        await host.Call(client, "send_message", new { repositoryId = "repo", requestId = "search-private", sessionId = Id(alice), sessionToken = Token(alice), recipientSessionId = Id(bob), text = "refresh plan for bob only" });
        for (var retry = 0; retry < 40 && Data(await host.Call(client, "check_inbox", SessionArgs(bob))).GetProperty("items").GetArrayLength() == 0; retry++) await Task.Delay(100);

        async Task<string[]> Search(string repository, JsonElement session, object? extra = null)
        {
            var arguments = new Dictionary<string, object?> { ["repositoryId"] = repository, ["sessionId"] = Id(session), ["sessionToken"] = Token(session), ["query"] = "refresh", ["limit"] = 10 };
            var reply = await host.Call(client, "search", arguments);
            Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
            return Data(reply).GetProperty("hits").EnumerateArray().Select(hit => hit.GetProperty("k").GetString() + ":" + hit.GetProperty("id").GetString()).Order().ToArray();
        }
        var bobs = await Search("repo", bob);
        Assert.Equal(2, bobs.Length);
        Assert.Contains("task:T-1", bobs);
        Assert.Contains(bobs, hit => hit.StartsWith("message:", StringComparison.Ordinal));
        // Another session's inbox and another repository's tasks are never found.
        Assert.Equal(new[] { "task:T-1" }, await Search("repo", alice));
        Assert.Equal(new[] { "task:X-1" }, await Search("other", carol));

        async Task<(HttpStatusCode Status, string Code)> Refused(object arguments)
        {
            using var response = await client.PostAsJsonAsync("/api/tools/search", arguments);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!);
        }
        Assert.NotEqual(HttpStatusCode.OK, (await Refused(new { repositoryId = "repo", query = "refresh" })).Status);
        Assert.Equal("SessionDenied", (await Refused(new { repositoryId = "repo", sessionId = Id(bob), sessionToken = "wrong", query = "refresh" })).Code);
        // A session from another repository cannot search this one.
        Assert.Equal("RepositoryDenied", (await Refused(new { repositoryId = "repo", sessionId = Id(carol), sessionToken = Token(carol), query = "refresh" })).Code);
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidArguments"), await Refused(new { repositoryId = "repo", sessionId = Id(bob), sessionToken = Token(bob), query = "refresh", kinds = new[] { "standing" } }));
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidArguments"), await Refused(new { repositoryId = "repo", sessionId = Id(bob), sessionToken = Token(bob), query = new string('q', RelayLimits.MaxSearchQueryLength + 1) }));
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidArguments"), await Refused(new { repositoryId = "repo", sessionId = Id(bob), sessionToken = Token(bob), query = "  " }));
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidArguments"), await Refused(new { repositoryId = "repo", sessionId = Id(bob), sessionToken = Token(bob), query = "refresh", limit = "3" }));
    }

    [Fact]
    public async Task Search_turned_off_answers_SearchUnavailable()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Search:Enabled"] = "false" });
        using var client = host.AuthorizedClient();
        var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "search-off" }));
        using var response = await client.PostAsJsonAsync("/api/tools/search", new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString(), query = "anything" });
        Assert.Equal(RelayErrorCodes.SearchUnavailable, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.False(host.Services.GetRequiredService<SqliteStore>().SearchEnabled);
    }

    [Fact]
    public async Task Attention_respond_over_rest_uses_the_configured_snooze_and_leaves_milestone_replies_unchanged()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Attention:SnoozeBoundaries"] = "4", ["Relay:Attention:ItemExpiryDays"] = "3" });
        using var client = host.AuthorizedClient();
        var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "attention-open" }));
        var sessionId = session.GetProperty("sessionId").GetString()!;
        var sessionToken = session.GetProperty("sessionToken").GetString()!;
        Assert.Equal(new AttentionOptions { SnoozeBoundaries = 4, ItemExpiryDays = 3 }, host.Services.GetRequiredService<AttentionOptions>());
        // No item is waiting, so a milestone reply carries no attention.
        var created = Data(await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "attention-task", sessionId, sessionToken, taskId = "T-1", expectedVersion = 0 }));
        Assert.False(created.TryGetProperty("attention", out _));
        var hub = new RelayCaller("test-user", false, ["repo"], true);
        var raised = await host.Services.GetRequiredService<Orleans.IGrainFactory>().GetGrain<ISessionGrain>(sessionId)
            .ExecuteAsync(hub, "__attention_raise", JsonSerializer.Serialize(new { repositoryId = "repo", kind = "review_ready", taskId = "T-1" }));
        var itemId = JsonDocument.Parse(raised.DataJson).RootElement.GetProperty("itemId").GetString();
        var later = Data(await host.Call(client, "attention_respond", new { repositoryId = "repo", requestId = "attention-later", sessionId, sessionToken, itemId, action = "later" }));
        Assert.Equal(4, later.GetProperty("snoozeBoundaries").GetInt32());
        var again = await host.Services.GetRequiredService<Orleans.IGrainFactory>().GetGrain<ISessionGrain>(sessionId)
            .ExecuteAsync(hub, "__attention_raise", JsonSerializer.Serialize(new { repositoryId = "repo", kind = "failed", taskId = "T-1", blocking = true }));
        var blocking = JsonDocument.Parse(again.DataJson).RootElement.GetProperty("itemId").GetString();
        foreach (var attempt in new[] { "b1", "b2" }) await host.Call(client, "attention_respond", new { repositoryId = "repo", requestId = attempt, sessionId, sessionToken, itemId = blocking, action = "later" });
        using var capped = await client.PostAsJsonAsync("/api/tools/attention_respond", new { repositoryId = "repo", requestId = "b3", sessionId, sessionToken, itemId = blocking, action = "later" });
        Assert.Equal(HttpStatusCode.Conflict, capped.StatusCode);
        using var unknown = await client.PostAsJsonAsync("/api/tools/attention_respond", new { repositoryId = "repo", requestId = "attention-missing", sessionId, sessionToken, itemId = "a-404", action = "accept" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var noSession = await client.PostAsJsonAsync("/api/tools/attention_respond", new { repositoryId = "repo", requestId = "attention-anon", itemId, action = "accept" });
        Assert.Equal(HttpStatusCode.Forbidden, noSession.StatusCode);
    }

    [Fact]
    public async Task Attention_settings_outside_their_limits_fail_clearly()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Attention:SnoozeBoundaries"] = "0" });
        var failure = Assert.ThrowsAny<Exception>(() => host.Services.GetRequiredService<AttentionOptions>());
        Assert.Contains("Relay:Attention:SnoozeBoundaries", failure.ToString());
    }

    [Fact]
    public async Task Anomaly_monitor_is_off_unless_analytics_is_on_and_its_settings_fail_clearly()
    {
        await using (var host = new RelayHostFixture())
        {
            using var client = host.AuthorizedClient();
            Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "anomaly-manager", role = "manager" }));
            Assert.Equal(TimeSpan.FromHours(1), host.Services.GetRequiredService<AnomalyOptions>().Interval);
            await Task.Delay(300);
            // With analytics off (the default) a manager session starts no monitor, so no check is ever scheduled.
            var due = await host.Services.GetRequiredService<SqliteStore>().ReadDueAsync(DateTimeOffset.UtcNow.AddDays(30), 64);
            Assert.DoesNotContain(due, work => work.GrainType == "pkrelay.anomaly");
        }
        foreach (var (setting, value, named) in new[]
        {
            ("Relay:Attention:AnomalyInterval", "00:00:30", "Relay:Attention:AnomalyInterval"),
            ("Relay:Attention:AnomalyThresholds:not_a_query:Ratio", "3", "Relay:Attention:AnomalyThresholds:not_a_query"),
            ("Relay:Attention:AnomalyThresholds:tokens_per_task:Ratio", "1", "Relay:Attention:AnomalyThresholds:tokens_per_task:Ratio"),
            ("Relay:Attention:AnomalyThresholds:tokens_per_task:MinSamples", "0", "Relay:Attention:AnomalyThresholds:tokens_per_task:MinSamples"),
            ("Relay:Attention:AnomalyThresholds:quality_guardrails:DropRatio", "1", "Relay:Attention:AnomalyThresholds:quality_guardrails:DropRatio")
        })
        {
            await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { [setting] = value });
            var failure = Assert.ThrowsAny<Exception>(() => host.Services.GetRequiredService<AnomalyOptions>());
            Assert.Contains(named, failure.ToString());
        }
        await using (var off = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Attention:AnomalyInterval"] = "0", ["Relay:Attention:AnomalyThresholds:noisiest_tools:Ratio"] = "0" }))
        {
            var options = off.Services.GetRequiredService<AnomalyOptions>();
            Assert.False(options.Enabled);
            Assert.DoesNotContain(options.Checks(), check => check.Query == AnalyticsQueryNames.NoisiestTools);
        }
    }

    /// <summary>A ready workflow, or a blocked one with one blocker, as the board accepts it.</summary>
    internal static object Workflow(string state, string blockerSummary = "waiting on schema review") => new
    {
        state,
        packet = new { goal = "g", target = "t", authorityRef = "human-grant:1", restoration = "r", stopCondition = "s" },
        criteria = new[] { new { id = "c", description = "d", result = "not_run" } },
        blockers = state == "blocked" ? new[] { new { id = "b1", kind = "prerequisite", summary = blockerSummary, recheckCondition = "r" } } : []
    };

    [Fact]
    public async Task Attention_offers_ride_boundary_replies_including_model_visible_check_inbox_and_attention_false_skips_them()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var manager = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "delivery-manager", role = "Manager" }));
        var worker = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "delivery-worker", role = "worker" }));
        object As(JsonElement session, object extra)
        {
            var arguments = JsonSerializer.SerializeToNode(extra)!.AsObject();
            arguments["repositoryId"] = "repo"; arguments["sessionId"] = session.GetProperty("sessionId").GetString(); arguments["sessionToken"] = session.GetProperty("sessionToken").GetString();
            return arguments;
        }
        Assert.True(Data(await host.Call(client, "task_update", As(worker, new { requestId = "d-1", taskId = "T-1", expectedVersion = 0, workflow = Workflow("ready") }))).TryGetProperty("version", out _));
        Data(await host.Call(client, "task_claim", As(worker, new { requestId = "d-2", taskId = "T-1", expectedVersion = 1 })));
        // The worker's blocked milestone raises a blocked item to the manager through the board's and the control's outboxes.
        var blocked = Data(await host.Call(client, "task_update", As(worker, new { requestId = "d-3", taskId = "T-1", expectedVersion = 2, workflow = Workflow("blocked") })));
        Assert.False(blocked.TryGetProperty("attention", out _));
        async Task<JsonElement> Status(bool detail = false) => Data(await host.Call(client, "get_status", As(manager, detail ? new { attentionDetail = true } : new { attentionDetail = false })));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await Status()).GetProperty("attention").GetProperty("actionable").GetInt32() == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        // get_status shows only the count; the adapter's detail adds the top item and the wake candidate (blocked is top tier).
        var status = (await Status()).GetProperty("attention");
        Assert.Equal(["actionable"], status.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, status.GetProperty("actionable").GetInt32());
        var detail = (await Status(detail: true)).GetProperty("attention");
        Assert.Equal(("blocked", "T-1", 1), (detail.GetProperty("top").GetProperty("k").GetString(), detail.GetProperty("top").GetProperty("t").GetString(), detail.GetProperty("urgent").GetInt32()));
        Assert.Equal(detail.GetProperty("top").GetProperty("id").GetString(), detail.GetProperty("wake").GetString());

        // A background inbox read (the adapter's watchers and waits send attention: false) takes no offer.
        Assert.False(Data(await host.Call(client, "check_inbox", As(manager, new { attention = false }))).TryGetProperty("attention", out _));
        // attention: false skips the offer and leaves the item unshown.
        var quiet = Data(await host.Call(client, "task_update", As(manager, new { requestId = "d-4", taskId = "M-1", expectedVersion = 0, attention = false })));
        Assert.False(quiet.TryGetProperty("attention", out _));
        // The next boundary reply carries exactly one compact offer.
        var milestone = Data(await host.Call(client, "task_update", As(manager, new { requestId = "d-5", taskId = "M-1", expectedVersion = 1 })));
        var offer = milestone.GetProperty("attention");
        Assert.Equal(["id", "k", "more", "t", "x"], offer.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(("blocked", "T-1", "T-1 blocked: waiting on schema review", 0), (offer.GetProperty("k").GetString(), offer.GetProperty("t").GetString(), offer.GetProperty("x").GetString(), offer.GetProperty("more").GetInt32()));
        // The offer is not repeated on the worker's replies, and a non-boolean opt-out is refused.
        Assert.False(Data(await host.Call(client, "task_update", As(worker, new { requestId = "d-6", taskId = "W-1", expectedVersion = 0 }))).TryGetProperty("attention", out _));
        using var bad = await client.PostAsJsonAsync("/api/tools/task_update", As(manager, new { requestId = "d-7", taskId = "M-1", expectedVersion = 2, attention = "no" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // A model-visible check_inbox is a boundary too, so a manager that mostly reads its inbox still gets offers.
        Assert.True((await host.Services.GetRequiredService<Orleans.IGrainFactory>().GetGrain<ISessionGrain>(manager.GetProperty("sessionId").GetString()!)
            .ExecuteAsync(new RelayCaller("test-user", false, ["repo"], true), "__attention_raise", JsonSerializer.Serialize(new { repositoryId = "repo", kind = "review_ready", taskId = "T-9" }))).Ok);
        var inboxOffer = Data(await host.Call(client, "check_inbox", As(manager, new { }))).GetProperty("attention");
        Assert.Equal(("review_ready", "T-9"), (inboxOffer.GetProperty("k").GetString(), inboxOffer.GetProperty("t").GetString()));
        Data(await host.Call(client, "attention_respond", As(manager, new { requestId = "d-review", itemId = inboxOffer.GetProperty("id").GetString(), action = "dismiss" })));

        // Postponed, the item leaves the count; get_status never counts snoozed items.
        Data(await host.Call(client, "attention_respond", As(manager, new { requestId = "d-8", itemId = offer.GetProperty("id").GetString(), action = "later" })));
        Assert.Equal(0, (await Status()).GetProperty("attention").GetProperty("actionable").GetInt32());
        Assert.False((await Status(detail: true)).GetProperty("attention").TryGetProperty("wake", out _));

        // Clearing the blocker closes the manager's item.
        var ready = JsonSerializer.SerializeToNode(Workflow("ready"))!.AsObject(); ready["recheckEvidenceRef"] = "evidence:1";
        Data(await host.Call(client, "task_update", As(worker, new { requestId = "d-9", taskId = "T-1", expectedVersion = 3, workflow = ready })));
        // The adapter's detailed status reads are background reads: they carry no role, so they never count as manager turns.
        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM activity_log WHERE tool = 'get_status' AND role IS NULL;";
            Assert.True((long)command.ExecuteScalar()! >= 2);
            command.CommandText = "SELECT COUNT(*) FROM activity_log WHERE tool = 'get_status' AND role = 'manager';";
            Assert.True((long)command.ExecuteScalar()! >= 1);
        }

        // Once the close is delivered the item is gone, so answering it is NotFound; until then "later" just keeps it snoozed.
        var gone = DateTime.UtcNow.AddSeconds(20);
        for (var attempt = 0; ; attempt++)
        {
            using var answer = await client.PostAsJsonAsync("/api/tools/attention_respond", As(manager, new { requestId = "d-close-" + attempt, itemId = offer.GetProperty("id").GetString(), action = "later" }));
            if (answer.StatusCode == HttpStatusCode.NotFound) break;
            Assert.True(DateTime.UtcNow < gone, "The blocked item was not closed when its blocker cleared.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Messages_shown_by_adapter_readers_close_their_info_items_without_an_offer()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var sender = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "seen-sender" }));
        var reader = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "seen-reader" }));
        async Task<int> Actionable() => Data(await host.Call(client, "get_status", SessionArgs(reader))).GetProperty("attention").GetProperty("actionable").GetInt32();
        async Task<string> Send(string requestId)
        {
            var sent = Data(await host.Call(client, "send_message", new { repositoryId = "repo", requestId, sessionId = sender.GetProperty("sessionId").GetString(), sessionToken = sender.GetProperty("sessionToken").GetString(), recipientSessionId = reader.GetProperty("sessionId").GetString(), text = "please look" }));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (await Actionable() == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            return sent.GetProperty("messageId").GetString()!;
        }
        object Read(object extra)
        {
            var arguments = JsonSerializer.SerializeToNode(extra)!.AsObject();
            arguments["repositoryId"] = "repo"; arguments["sessionId"] = reader.GetProperty("sessionId").GetString(); arguments["sessionToken"] = reader.GetProperty("sessionToken").GetString();
            return arguments;
        }
        // A message is an info item until the model has seen it.
        await Send("seen-1");
        Assert.Equal(1, await Actionable());
        // A background read that showed nothing to the model leaves it open.
        Assert.False(Data(await host.Call(client, "check_inbox", Read(new { attention = false }))).TryGetProperty("attention", out _));
        Assert.Equal(1, await Actionable());
        // A waiting read whose page reached the model (seen) closes it, with no offer.
        Assert.False(Data(await host.Call(client, "check_inbox", Read(new { attention = false, seen = true }))).TryGetProperty("attention", out _));
        Assert.Equal(0, await Actionable());
        // The waiter names the messages it printed.
        var second = await Send("seen-2");
        Assert.Equal(1, await Actionable());
        await host.Call(client, "check_inbox", Read(new { attention = false, limit = 1, seenMessageIds = new[] { second } }));
        Assert.Equal(0, await Actionable());
        using var bad = await client.PostAsJsonAsync("/api/tools/check_inbox", Read(new { seenMessageIds = new[] { 1 } }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_claim_raises_look_alikes_only_on_an_all_words_match_after_the_reply()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "alike-open" }));
        object As(object extra)
        {
            var arguments = JsonSerializer.SerializeToNode(extra)!.AsObject();
            arguments["repositoryId"] = "repo"; arguments["sessionId"] = session.GetProperty("sessionId").GetString(); arguments["sessionToken"] = session.GetProperty("sessionToken").GetString();
            if (arguments["workflow"] is null) arguments.Remove("workflow");
            return arguments;
        }
        async Task Task(string taskId, string title, object? workflow = null) =>
            Assert.True((await host.Call(client, "task_update", As(new { requestId = "alike-" + taskId, taskId, expectedVersion = 0, patch = new { title }, workflow, attention = false }))).GetProperty("ok").GetBoolean());
        // Enough unrelated tasks that the shared words are rare, as on a real board.
        for (var i = 0; i < 12; i++) await Task("F-" + i, "unrelated chore number " + i);
        await Task("T-A", "rotate printer firmware keys");
        await Task("T-B", "rotate printer firmware keys again");
        await Task("T-C", "printer paper");
        var completed = JsonSerializer.SerializeToNode(Workflow("ready"))!.AsObject();
        completed["state"] = "completed"; completed["criteria"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "passed", ["evidenceRef"] = "e:1" });
        await Task("T-D", "rotate printer firmware keys for the lab", completed);

        var claim = Data(await host.Call(client, "task_claim", As(new { requestId = "alike-claim", taskId = "T-A", expectedVersion = 1 })));
        // The lookup runs after the reply, so the claim reply carries none of it.
        Assert.False(claim.TryGetProperty("attention", out _));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Data(await host.Call(client, "get_status", As(new { }))).GetProperty("attention").GetProperty("actionable").GetInt32() < 2 && DateTime.UtcNow < deadline) await System.Threading.Tasks.Task.Delay(50);
        var first = Data(await host.Call(client, "check_inbox", As(new { }))).GetProperty("attention");
        var second = Data(await host.Call(client, "check_inbox", As(new { }))).GetProperty("attention");
        // An open look-alike is a possible duplicate, a completed one related work; a task sharing one word (T-C) is neither.
        Assert.Equal(("duplicate_warning", "duplicate_warning: may duplicate T-A refs T-B"), (first.GetProperty("k").GetString(), first.GetProperty("x").GetString()));
        Assert.Equal(("related_work", "related_work: related to T-A refs T-D"), (second.GetProperty("k").GetString(), second.GetProperty("x").GetString()));
    }

    [Fact]
    public async Task Attention_turned_off_attaches_no_offers_or_counts()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Attention:Enabled"] = "false", ["Relay:Attention:MaxHoldMinutes"] = "5" });
        using var client = host.AuthorizedClient();
        Assert.Equal(new AttentionOptions { Enabled = false, MaxHoldMinutes = 5 }, host.Services.GetRequiredService<AttentionOptions>());
        var manager = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "off-manager", role = "manager" }));
        var args = new { repositoryId = "repo", sessionId = manager.GetProperty("sessionId").GetString(), sessionToken = manager.GetProperty("sessionToken").GetString() };
        var raised = await host.Services.GetRequiredService<Orleans.IGrainFactory>().GetGrain<ISessionGrain>(args.sessionId!)
            .ExecuteAsync(new RelayCaller("test-user", false, ["repo"], true), "__attention_raise", JsonSerializer.Serialize(new { repositoryId = "repo", kind = "blocked", taskId = "T-1" }));
        Assert.True(raised.Ok);
        var reply = Data(await host.Call(client, "task_update", new { args.repositoryId, args.sessionId, args.sessionToken, requestId = "off-1", taskId = "T-2", expectedVersion = 0 }));
        Assert.False(reply.TryGetProperty("attention", out _));
        Assert.False(Data(await host.Call(client, "get_status", args)).TryGetProperty("attention", out _));
    }

    private static JsonElement Data(JsonElement reply) { using var document = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!); return document.RootElement.Clone(); }
    private static object SessionArgs(JsonElement session) => new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() };

    [Theory]
    [InlineData("sessionId")]
    [InlineData("sessionToken")]
    public async Task Over_long_session_handles_are_refused_over_rest(string field)
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var arguments = new Dictionary<string, object?> { ["repositoryId"] = "repo", ["sessionId"] = "s", ["sessionToken"] = "t", [field] = new string('x', 257) };
        using var response = await client.PostAsJsonAsync("/api/tools/check_inbox", arguments);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidArguments", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
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

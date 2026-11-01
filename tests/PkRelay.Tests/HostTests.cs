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
        await using var host = new RelayHostFixture();
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
        // Nothing raises items or attaches offers yet, so a milestone reply carries no attention.
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

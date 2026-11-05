using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using CoCern.Storage;
using Xunit;

namespace CoCern.Tests;

public class HostTests
{

    private sealed class FairnessDueSource : IDueWorkSource
    {
        public List<int> Offsets { get; } = [];
        public Task<IReadOnlyList<DueWorkReference>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default, int offset = 0)
        {
            Assert.Equal(64, limit);
            Offsets.Add(offset);
            IReadOnlyList<DueWorkReference> rows = Enumerable.Range(0, 65).Skip(offset).Take(limit).Select(index => new DueWorkReference("session", "cocern.session", "owner" + index, index == 64 ? "healthy" : index.ToString(), "effect", now)).ToArray();
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
        var path = Path.Combine(Path.GetTempPath(), "cocern-restart-" + Guid.NewGuid().ToString("N"), "relay.db");
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

    private static object OfferWorkflow(string state) => new
    {
        state,
        packet = new { goal = "Finish a useful leg", target = "repo at revision abc", authorityRef = "human-grant:17", restoration = "Remove owned fixture", stopCondition = "Stop on target mismatch" },
        criteria = new[] { state == "completed" ? (object)new { id = "leg", description = "Leg passes", result = "passed", evidenceRef = "test-report:17" } : new { id = "leg", description = "Leg passes", result = "not_run" } },
        blockers = Array.Empty<object>()
    };

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
            var analytics = host.Services.GetRequiredService<CoCern.Analytics.AnalyticsService>();
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
        Assert.DoesNotContain("analytics_query", CoCern.Host.ToolResults.Tools(false).Select(tool => tool.Name));
    }

    [Fact]
    public async Task Enabled_ad_hoc_sql_stays_read_only_with_the_row_cap_and_reply_limit()
    {
        // Leave time for payload checks even when other test hosts contend for the CPU.
        await using var host = await AnalyticsHost(enabled: true, adHoc: true, timeout: "00:00:10");
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
        var large = await Analytics(admin, Sql("SELECT repeat('x', 1000) AS filler FROM range(20)"));
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge"), (large.Status, large.Code));
        Assert.Contains("narrower", large.Data.GetProperty("message").GetString());
        // The refused and oversized queries left the snapshot usable.
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

    /// <summary>A ready workflow, or a blocked one with one blocker, as the board accepts it.</summary>
    internal static object Workflow(string state, string blockerSummary = "waiting on schema review") => new
    {
        state,
        packet = new { goal = "g", target = "t", authorityRef = "human-grant:1", restoration = "r", stopCondition = "s" },
        criteria = new[] { new { id = "c", description = "d", result = "not_run" } },
        blockers = state == "blocked" ? new[] { new { id = "b1", kind = "prerequisite", summary = blockerSummary, recheckCondition = "r" } } : []
    };

    [Fact]
    public async Task A_seen_page_cut_by_compaction_closes_only_the_messages_it_kept()
    {
        // Token budgets are enforced by default, so a page of token-dense messages ("7 3 9 1 4" is one token per byte) is cut.
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var sender = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "cut-sender" }));
        var reader = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "cut-reader" }));
        object Read(object extra)
        {
            var arguments = JsonSerializer.SerializeToNode(extra)!.AsObject();
            arguments["repositoryId"] = "repo"; arguments["sessionId"] = reader.GetProperty("sessionId").GetString(); arguments["sessionToken"] = reader.GetProperty("sessionToken").GetString();
            return arguments;
        }
        async Task<int> Actionable() => Data(await host.Call(client, "get_status", Read(new { attentionDetail = true }))).GetProperty("attention").GetProperty("actionable").GetInt32();
        for (var i = 0; i < 30; i++)
            Data(await host.Call(client, "send_message", new { repositoryId = "repo", requestId = $"cut-{i}", sessionId = sender.GetProperty("sessionId").GetString(), sessionToken = sender.GetProperty("sessionToken").GetString(),
                recipientSessionId = reader.GetProperty("sessionId").GetString(), text = $"Message {i}: " + string.Concat(Enumerable.Repeat("7 3 9 1 4 ", 35)) }));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await Actionable() < 30 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(30, await Actionable());

        // The page the model saw was compacted: only the messages it kept close; the ones cut off stay open.
        var page = Data(await host.Call(client, "check_inbox", Read(new { attention = false, seen = true, limit = 50 })));
        Assert.True(page.GetProperty("compacted").GetBoolean());
        var kept = page.GetProperty("items").GetArrayLength();
        Assert.InRange(kept, 1, 29);
        Assert.Equal(30 - kept, await Actionable());
        // Reading on from the cursor closes the rest.
        var cursor = kept;
        while (cursor < 30)
        {
            var next = Data(await host.Call(client, "check_inbox", Read(new { attention = false, seen = true, limit = 50, cursor })));
            cursor += next.GetProperty("items").GetArrayLength();
        }
        Assert.Equal(0, await Actionable());
    }

    private static JsonElement Data(JsonElement reply) { using var document = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!); return document.RootElement.Clone(); }
    private static object SessionArgs(JsonElement session) => new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() };

}

internal sealed class RelayHostFixture : WebApplicationFactory<CoCern.Host.RelayHostMarker>
{
    public const string Token = "host-test-secret-not-a-production-token";
    public const string JoinCode = "host-test-join-code-not-production";
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
        DatabasePath = databasePath ?? Path.Combine(Path.GetTempPath(), "cocern-host-" + Guid.NewGuid().ToString("N"), "relay.db");
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
            ["Relay:Clients:0:Repositories:0"] = "repo",
            ["Relay:Discovery:JoinCode"] = JoinCode
        }));
        if (settings is not null) builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        if (configureServices is not null) builder.ConfigureServices(configureServices);
        if (configureJwt)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:OAuth:Authority"] = JwtAuthority,
                ["Relay:OAuth:Audience"] = "cocern-tests",
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
                options.TokenValidationParameters.ValidAudience = "cocern-tests";
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
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = JwtAuthority, Audience = "cocern-tests", Subject = new ClaimsIdentity(claims), Expires = DateTime.UtcNow.AddMinutes(5), SigningCredentials = new SigningCredentials(JwtKey, SecurityAlgorithms.HmacSha256) });
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

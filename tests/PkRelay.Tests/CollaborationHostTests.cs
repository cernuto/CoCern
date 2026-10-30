using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PkRelay.Tests;

public sealed class CollaborationHostTests
{
    [Fact]
    public async Task HttpAndMcpSharePinnedDeltaPagesAndRejectChangedSnapshots()
    {
        await using var host = new RelayHostFixture();
        using var http = host.AuthorizedClient();
        var session = Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "delta-session" }));
        await Update(host, http, session, "a", 0, Workflow());
        await Update(host, http, session, "b", 0, Workflow());
        var first = Data(await host.Call(http, "task_get", new { repositoryId = "repo", sinceRevision = 0, limit = 1 }));
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.True(first.GetProperty("truncated").GetBoolean());
        var revision = first.GetProperty("snapshotRevision").GetInt64();
        var cursor = first.GetProperty("nextCursor").GetInt32();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var continued = await mcp.CallToolAsync("task_get", new Dictionary<string, object?> { ["repositoryId"] = "repo", ["sinceRevision"] = 0, ["snapshotRevision"] = revision, ["cursor"] = cursor, ["limit"] = 1 }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, continued.IsError);
        var next = Data(McpReply(continued));
        Assert.Equal(revision, next.GetProperty("snapshotRevision").GetInt64());
        Assert.Single(next.GetProperty("items").EnumerateArray());
        Assert.NotEqual(first.GetProperty("items")[0].GetProperty("taskId").GetString(), next.GetProperty("items")[0].GetProperty("taskId").GetString());
        await Update(host, http, session, "a", 1, Workflow());
        using var stale = await http.PostAsJsonAsync("/api/tools/task_get", new { repositoryId = "repo", sinceRevision = 0, snapshotRevision = revision, cursor, limit = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleReply = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DeltaRestartRequired", staleReply.GetProperty("code").GetString());
        var options = PkRelay.Adapter.AdapterOptions.FromEnvironment(key => key switch { "PKRELAY_URL" => "https://localhost", "PKRELAY_TOKEN" => RelayHostFixture.Token, _ => null });
        var forwarded = await new PkRelay.Adapter.RelayForwarder(http, options).ForwardAsync("task_get", JsonSerializer.SerializeToElement(new { repositoryId = "repo", sinceRevision = 0, snapshotRevision = revision, cursor, limit = 1 }));
        Assert.False(forwarded.Ok);
        Assert.Equal("DeltaRestartRequired", forwarded.Code);
        Assert.Equal(staleReply.GetProperty("version").GetInt64(), forwarded.Version);
    }

    [Fact]
    public async Task SqliteRestartPreservesDeltaCursorCheckpointAndLaterChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "pkrelay-collaboration-" + Guid.NewGuid().ToString("N"), "relay.db");
        JsonElement session;
        JsonElement first;
        await using (var initial = new RelayHostFixture(path))
        {
            using var http = initial.AuthorizedClient();
            session = Data(await initial.Call(http, "session_open", new { repositoryId = "repo", requestId = "restart-session" }));
            await Update(initial, http, session, "a", 0, Workflow());
            await Update(initial, http, session, "b", 0, Workflow());
            first = Data(await initial.Call(http, "task_get", new { repositoryId = "repo", sinceRevision = 0, limit = 1 }));
        }
        await using (var restarted = new RelayHostFixture(path))
        {
            using var http = restarted.AuthorizedClient();
            var revision = first.GetProperty("snapshotRevision").GetInt64();
            var page = Data(await restarted.Call(http, "task_get", new { repositoryId = "repo", sinceRevision = 0, snapshotRevision = revision, cursor = first.GetProperty("nextCursor").GetInt32(), limit = 1 }));
            Assert.Equal(revision, page.GetProperty("snapshotRevision").GetInt64());
            Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.False(page.GetProperty("truncated").GetBoolean());
            var updated = await Update(restarted, http, session, "a", 1, Workflow());
            Assert.Equal(2, updated.GetProperty("version").GetInt64());
            var changes = Data(await restarted.Call(http, "task_get", new { repositoryId = "repo", sinceRevision = revision }));
            Assert.True(changes.GetProperty("snapshotRevision").GetInt64() > revision);
            Assert.Equal("a", Assert.Single(changes.GetProperty("items").EnumerateArray()).GetProperty("taskId").GetString());
        }
    }

    [Fact]
    public async Task PartialCompletionIsRejectedAndBlockedWorkDoesNotBecomeAnOfferAfterTime()
    {
        await using var host = new RelayHostFixture();
        using var http = host.AuthorizedClient();
        var session = Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "workflow-session" }));
        var partial = Workflow("completed");
        ((JsonArray)partial["criteria"]!).Add(new JsonObject { ["id"] = "unrun", ["description"] = "Still required", ["result"] = "not_run" });
        using var rejected = await http.PostAsJsonAsync("/api/tools/task_update", Arguments(session, "partial", 0, partial));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var incomplete = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(incomplete.GetProperty("ok").GetBoolean());
        Assert.Equal("WorkflowIncomplete", incomplete.GetProperty("code").GetString());
        var blocked = Workflow("blocked");
        blocked["blockers"] = new JsonArray(new JsonObject { ["id"] = "fixture", ["kind"] = "missing_fixture", ["summary"] = "Need fixture", ["recheckCondition"] = "Fixture exists" });
        await Update(host, http, session, "blocked", 0, blocked);
        await Task.Delay(1100);
        var report = await Update(host, http, session, "report", 0, Workflow("completed"), includeNextWork: true);
        Assert.Equal("NoEligibleWork", Data(report).GetProperty("nextWork").GetProperty("code").GetString());
        var stored = Data(await host.Call(http, "task_get", new { repositoryId = "repo", taskId = "blocked", section = "workflow" }));
        Assert.Contains("blocked", stored.GetRawText());
        Assert.Contains("Fixture exists", stored.GetRawText());
    }

    [Fact]
    public async Task ReportAndOfferShareAuthenticatedCallButConcurrentOffersStillRequireClaims()
    {
        await using var host = new RelayHostFixture();
        using var http = host.AuthorizedClient();
        var first = Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "offer-first" }));
        var second = Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "offer-second" }));
        await Update(host, http, first, "next", 0, Workflow());
        var reports = await Task.WhenAll(Update(host, http, first, "done-first", 0, Workflow("completed"), true), Update(host, http, second, "done-second", 0, Workflow("completed"), true));
        foreach (var reply in reports)
        {
            Assert.True(reply.GetProperty("ok").GetBoolean());
            var next = Data(reply).GetProperty("nextWork");
            Assert.Equal("Offered", next.GetProperty("code").GetString());
            Assert.Equal("next", next.GetProperty("offer").GetProperty("taskId").GetString());
            Assert.True(next.GetProperty("offer").GetProperty("claimRequired").GetBoolean());
            Assert.DoesNotContain(first.GetProperty("sessionToken").GetString()!, next.GetRawText());
            Assert.DoesNotContain(second.GetProperty("sessionToken").GetString()!, next.GetRawText());
        }
        var claims = await Task.WhenAll(Claim(http, first, "claim-first"), Claim(http, second, "claim-second"));
        Assert.Single(claims, reply => reply.GetProperty("ok").GetBoolean());
        Assert.Single(claims, reply => !reply.GetProperty("ok").GetBoolean());
        var queue = await host.Call(http, "task_get", new { repositoryId = "repo", sinceRevision = 0 });
        Assert.DoesNotContain(first.GetProperty("sessionToken").GetString()!, queue.GetRawText());
        Assert.DoesNotContain(second.GetProperty("sessionToken").GetString()!, queue.GetRawText());
        var forged = Arguments(second, "forged", 0, Workflow("completed"), true);
        forged["sessionToken"] = first.GetProperty("sessionToken").GetString();
        using var denied = await http.PostAsJsonAsync("/api/tools/task_update", forged);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var absent = await http.PostAsJsonAsync("/api/tools/task_get", new { repositoryId = "repo", taskId = "forged" });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    [Fact]
    public async Task QuoteHeavyWorkflowAndOfferFitActualSerializedHttpEnvelope()
    {
        await using var host = new RelayHostFixture();
        using var http = host.AuthorizedClient();
        var session = Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "bound-session" }));
        var workflow = Workflow();
        workflow["packet"]!["goal"] = new string('"', 800);
        for (var i = 1; i < 5; i++) ((JsonArray)workflow["criteria"]!).Add(new JsonObject { ["id"] = "criterion-" + i, ["description"] = new string('"', 200), ["result"] = "not_run" });
        await Update(host, http, session, "quote-next", 0, workflow);
        using var response = await http.PostAsJsonAsync("/api/tools/task_update", Arguments(session, "quote-report", 0, Workflow("completed"), true));
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.InRange(bytes.Length, 1, 32768);
        var reply = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("Offered", Data(reply).GetProperty("nextWork").GetProperty("code").GetString());
        Assert.Equal("quote-next", Data(reply).GetProperty("nextWork").GetProperty("offer").GetProperty("taskId").GetString());
        Assert.True(Data(reply).GetProperty("nextWork").GetProperty("offer").GetProperty("detailFetchRequired").GetBoolean());
    }

    private static JsonObject Workflow(string state = "ready") => new()
    {
        ["state"] = state,
        ["packet"] = new JsonObject { ["goal"] = "Finish a useful leg", ["target"] = "repo at source revision abc", ["authorityRef"] = "human-grant:17", ["restoration"] = "Remove owned fixture", ["stopCondition"] = "Stop on target mismatch" },
        ["criteria"] = new JsonArray(state == "completed" ? new JsonObject { ["id"] = "leg", ["description"] = "Required leg passes", ["result"] = "passed", ["evidenceRef"] = "test-report:17" } : new JsonObject { ["id"] = "leg", ["description"] = "Required leg passes", ["result"] = "not_run" }),
        ["blockers"] = new JsonArray()
    };

    private static JsonObject Arguments(JsonElement session, string taskId, long version, JsonObject workflow, bool includeNextWork = false) => new()
    {
        ["repositoryId"] = "repo", ["sessionId"] = session.GetProperty("sessionId").GetString(), ["sessionToken"] = session.GetProperty("sessionToken").GetString(), ["requestId"] = Guid.NewGuid().ToString("N"), ["taskId"] = taskId, ["expectedVersion"] = version, ["workflow"] = workflow, ["includeNextWork"] = includeNextWork
    };

    private static Task<JsonElement> Update(RelayHostFixture host, HttpClient http, JsonElement session, string taskId, long version, JsonObject workflow, bool includeNextWork = false) => host.Call(http, "task_update", Arguments(session, taskId, version, workflow, includeNextWork));
    private static async Task<JsonElement> Claim(HttpClient http, JsonElement session, string requestId)
    {
        using var response = await http.PostAsJsonAsync("/api/tools/task_claim", new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString(), requestId, taskId = "next", expectedVersion = 1, resourceIds = Array.Empty<string>() });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
    private static JsonElement McpReply(CallToolResult reply) => JsonSerializer.Deserialize<JsonElement>(Assert.IsType<TextContentBlock>(Assert.Single(reply.Content)).Text);
    private static JsonElement Data(JsonElement reply) => JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("dataJson").GetString()!);
}

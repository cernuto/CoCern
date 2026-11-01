using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CoCern.Tests;

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
        var options = CoCern.Adapter.AdapterOptions.FromEnvironment(key => key switch { "COCERN_URL" => "https://localhost", "COCERN_TOKEN" => RelayHostFixture.Token, _ => null });
        var forwarded = await new CoCern.Adapter.RelayForwarder(http, options).ForwardAsync("task_get", JsonSerializer.SerializeToElement(new { repositoryId = "repo", sinceRevision = 0, snapshotRevision = revision, cursor, limit = 1 }));
        Assert.False(forwarded.Ok);
        Assert.Equal("DeltaRestartRequired", forwarded.Code);
        Assert.Equal(staleReply.GetProperty("version").GetInt64(), forwarded.Version);
    }

    [Fact]
    public async Task SqliteRestartPreservesDeltaCursorCheckpointAndLaterChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "cocern-collaboration-" + Guid.NewGuid().ToString("N"), "relay.db");
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

    private const string AdminToken = "standing-admin-test-secret-not-production";

    private static RelayHostFixture StandingHost() => new(settings: new Dictionary<string, string?>
    {
        ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "admin-user", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo"
    });

    private static HttpClient AdminClient(RelayHostFixture host)
    {
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private static async Task<JsonElement> Post(HttpClient http, string tool, object arguments)
    {
        using var response = await http.PostAsJsonAsync("/api/tools/" + tool, arguments);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static object Line(string id, string text) => new { id, text };

    [Fact]
    public async Task Standing_writes_need_an_administrator_and_proposals_never_take_effect_unapproved()
    {
        await using var host = StandingHost();
        using var worker = host.AuthorizedClient();
        using var admin = AdminClient(host);
        var session = Data(await host.Call(worker, "session_open", new { repositoryId = "repo", requestId = "standing-worker", role = "coder" }));
        var handle = new { sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() };

        using (var denied = await worker.PostAsJsonAsync("/api/tools/standing_put", new { repositoryId = "repo", requestId = "worker-put", expectedRevision = 0, lines = new[] { Line("css", "Deploy CSS freely.") } }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("AdministratorRequired", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        // The admin-only tool is not even listed for a worker.
        Assert.DoesNotContain("standing_put", (await worker.GetFromJsonAsync<JsonElement>("/api/tools")).GetRawText());

        var proposed = Data(await host.Call(worker, "standing_propose", new { repositoryId = "repo", requestId = "propose", handle.sessionId, handle.sessionToken, line = new { text = "Never deploy CSS without a restoration receipt." } }));
        Assert.True(proposed.GetProperty("pending").GetBoolean());
        var proposalId = proposed.GetProperty("proposalId").GetString();
        var unapproved = Data(await host.Call(worker, "session_open", new { repositoryId = "repo", requestId = "standing-unapproved" }));
        Assert.False(unapproved.TryGetProperty("standing", out _));
        var pending = Data(await host.Call(worker, "task_get", new { repositoryId = "repo", section = "standing" }));
        Assert.Equal(0, pending.GetProperty("revision").GetInt64());
        Assert.Empty(pending.GetProperty("lines").EnumerateArray());
        Assert.Equal(proposalId, Assert.Single(pending.GetProperty("proposals").EnumerateArray()).GetProperty("id").GetString());

        var approved = Data(await host.Call(admin, "standing_put", new { repositoryId = "repo", requestId = "approve", expectedRevision = 0, approve = new[] { proposalId } }));
        Assert.Equal(1, approved.GetProperty("revision").GetInt64());
        var opened = Data(await host.Call(worker, "session_open", new { repositoryId = "repo", requestId = "standing-approved" }));
        var line = Assert.Single(opened.GetProperty("standing").GetProperty("lines").EnumerateArray());
        Assert.Equal("Never deploy CSS without a restoration receipt.", line.GetProperty("text").GetString());
        var record = Data(await host.Call(worker, "task_get", new { repositoryId = "repo", section = "standing" })).GetProperty("lines")[0];
        Assert.Equal("test-user", record.GetProperty("author").GetString());
        Assert.Equal("admin-user", record.GetProperty("approvedBy").GetString());
    }

    [Fact]
    public async Task Session_open_returns_standing_and_replies_attach_it_only_when_the_revision_is_newer()
    {
        await using var host = StandingHost();
        using var worker = host.AuthorizedClient();
        using var admin = AdminClient(host);
        Assert.True((await host.Call(admin, "standing_put", new { repositoryId = "repo", requestId = "put-1", expectedRevision = 0, lines = new[] { Line("css", "Never deploy CSS without a restoration receipt.") } })).GetProperty("ok").GetBoolean());
        var session = Data(await host.Call(worker, "session_open", new { repositoryId = "repo", requestId = "standing-session" }));
        var standing = session.GetProperty("standing");
        Assert.Equal(1, standing.GetProperty("revision").GetInt64());
        Assert.Equal("css", Assert.Single(standing.GetProperty("lines").EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty(standing.GetProperty("grants").EnumerateArray());

        // The chat already holds revision 1, so its milestone replies leave standing out.
        Assert.False(Data(await Update(host, worker, session, "a", 0, Workflow())).TryGetProperty("standing", out _));
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        Assert.True((await host.Call(admin, "standing_put", new { repositoryId = "repo", requestId = "put-2", expectedRevision = 1, lines = new[] { Line("smoke", "Run the smoke suite before merging.") }, grants = new[] { new { id = "rig", scope = "resource", targets = new[] { "rig-1" }, expiresAt = expires } } })).GetProperty("ok").GetBoolean());
        // check_inbox never carries standing: background wake readers would record it as received without the model seeing it.
        Assert.False(Data(await host.Call(worker, "check_inbox", new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() })).TryGetProperty("standing", out _));
        var changed = Data(await Update(host, worker, session, "a", 1, Workflow())).GetProperty("standing");
        Assert.Equal(2, changed.GetProperty("revision").GetInt64());
        Assert.Equal(2, changed.GetProperty("lines").GetArrayLength());
        Assert.Equal("rig", Assert.Single(changed.GetProperty("grants").EnumerateArray()).GetProperty("id").GetString());
        Assert.False(Data(await Update(host, worker, session, "a", 2, Workflow())).TryGetProperty("standing", out _));
        Assert.False(Data(await Update(host, worker, session, "b", 0, Workflow())).TryGetProperty("standing", out _));
        // A settings-only change is a new revision too, and the ready task naming the grant passes readiness.
        var authorized = Workflow(); authorized["packet"]!["authorityRef"] = "grant:rig"; authorized["packet"]!["resources"] = new JsonArray("rig-1");
        Assert.True((await host.Call(admin, "standing_put", new { repositoryId = "repo", requestId = "put-3", expectedRevision = 2, settings = new { requireResources = true } })).GetProperty("ok").GetBoolean());
        var third = Data(await Update(host, worker, session, "c", 0, authorized));
        Assert.Equal(3, third.GetProperty("standing").GetProperty("revision").GetInt64());
        Assert.True(third.GetProperty("standing").GetProperty("settings").GetProperty("requireResources").GetBoolean());
        var stale = await Post(admin, "standing_put", new { repositoryId = "repo", requestId = "put-stale", expectedRevision = 2, remove = new[] { "css" } });
        Assert.Equal("VersionConflict", stale.GetProperty("code").GetString());
        Assert.Equal(3, stale.GetProperty("version").GetInt64());
    }

    private static JsonObject Workflow(string state = "ready") => new()
    {
        ["state"] = state,
        ["packet"] = new JsonObject { ["goal"] = "Finish a useful leg", ["target"] = "repo at source revision abc", ["authorityRef"] = "human-grant:17", ["restoration"] = "Remove owned fixture", ["stopCondition"] = "Stop on target mismatch" },
        ["criteria"] = new JsonArray(state == "completed" ? new JsonObject { ["id"] = "leg", ["description"] = "Required leg passes", ["result"] = "passed", ["evidenceRef"] = "test-report:17" } : new JsonObject { ["id"] = "leg", ["description"] = "Required leg passes", ["result"] = "not_run" }),
        ["blockers"] = new JsonArray()
    };

    [Fact]
    public async Task A_tip_rule_firing_for_five_sessions_in_a_week_is_proposed_as_a_team_rule_that_changes_nothing_until_approved()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Feedback:ProposeRules"] = "true", ["Relay:Clients:0:IsAdministrator"] = "true" });
        using var http = host.AuthorizedClient();
        async Task<JsonElement> Open(string name) => Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "team-" + name, role = "worker" }));
        async Task ReadBoardThreeTimes(JsonElement session)
        {
            for (var i = 0; i < 3; i++)
                Assert.True((await host.Call(http, "task_get", new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() })).GetProperty("ok").GetBoolean());
        }
        async Task<JsonElement[]> Proposals()
        {
            var view = Data(await host.Call(http, "task_get", new { repositoryId = "repo", section = "standing" }));
            return view.GetProperty("proposals").EnumerateArray().ToArray();
        }

        // A worker fills its own per-session proposal cap first: the hub's proposal is outside it.
        var proposer = await Open("proposer");
        for (var i = 0; i < 5; i++)
            Assert.True((await host.Call(http, "standing_propose", new { repositoryId = "repo", requestId = "own-" + i, sessionId = proposer.GetProperty("sessionId").GetString(), sessionToken = proposer.GetProperty("sessionToken").GetString(), line = new { text = "Own idea " + i }, proposedBy = SessionRolesHub, hub = true })).GetProperty("ok").GetBoolean());
        // Nothing a client sends makes its proposal the hub's.
        Assert.All(await Proposals(), proposal => { Assert.Equal("test-user", proposal.GetProperty("proposedBy").GetString()); Assert.False(proposal.TryGetProperty("hub", out _)); });
        // The hub's own operation is internal: no tool reaches it.
        using (var internalCall = await http.PostAsJsonAsync("/api/tools/__standing_propose_hub", new { repositoryId = "repo", line = new { id = "tip-x", text = "x" } }))
            Assert.False((await internalCall.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        // Four sessions are not enough.
        for (var i = 0; i < 4; i++) await ReadBoardThreeTimes(await Open("reader-" + i));
        await Task.Delay(500);
        Assert.Equal(5, (await Proposals()).Length);
        // The fifth different session in the week makes the hub propose the rule's standing line, once.
        await ReadBoardThreeTimes(await Open("reader-4"));
        JsonElement[] hub = [];
        for (var retry = 0; retry < 50 && hub.Length == 0; retry++)
        {
            await Task.Delay(100);
            hub = (await Proposals()).Where(proposal => proposal.TryGetProperty("hub", out _)).ToArray();
        }
        var proposal = Assert.Single(hub);
        Assert.Equal("cocern-hub", proposal.GetProperty("proposedBy").GetString());
        Assert.Equal("tip-board-reread", proposal.GetProperty("line").GetProperty("id").GetString());
        Assert.Equal("Read the board as changes since your last revision.", proposal.GetProperty("line").GetProperty("text").GetString());
        Assert.False(proposal.TryGetProperty("sessionId", out _));
        await ReadBoardThreeTimes(await Open("reader-5"));
        await Task.Delay(300);
        Assert.Single(await Proposals(), item => item.TryGetProperty("hub", out _));

        // Nothing changes until an administrator approves it: a new chat gets no standing.
        var before = Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "team-before" }));
        Assert.False(before.TryGetProperty("standing", out _));
        var approved = Data(await host.Call(http, "standing_put", new { repositoryId = "repo", requestId = "team-approve", expectedRevision = 0, approve = new[] { proposal.GetProperty("id").GetString() } }));
        Assert.Equal(1, approved.GetProperty("revision").GetInt64());
        var after = Data(await host.Call(http, "session_open", new { repositoryId = "repo", requestId = "team-after" }));
        var line = Assert.Single(after.GetProperty("standing").GetProperty("lines").EnumerateArray());
        Assert.Equal(("tip-board-reread", "Read the board as changes since your last revision."), (line.GetProperty("id").GetString(), line.GetProperty("text").GetString()));
    }

    private const string SessionRolesHub = "cocern-hub";

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

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
    public async Task External_error_requires_an_owned_session_and_retries_create_only_one_triage_task()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var reporter = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "error-reporter" }));
        var recipient = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "triage-worker" }));
        var sessionId = reporter.GetProperty("sessionId").GetString();
        var sessionToken = reporter.GetProperty("sessionToken").GetString();
        var target = recipient.GetProperty("sessionId").GetString();
        var incident = new { repositoryId = "repo", sessionId, sessionToken, source = "monitor", eventId = "evt-42",
            summary = "Timeout during refresh", targetSessionId = target };
        const string path = CoCern.Host.RelayEndpoints.ErrorEventPath;

        using var denied = await client.PostAsJsonAsync(path, incident with { sessionToken = "different-session-token" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var created = await client.PostAsJsonAsync(path, incident);
        created.EnsureSuccessStatusCode();
        var taskId = Data(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetString();
        using var replay = await client.PostAsJsonAsync(path, incident);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(taskId, Data(await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetString());
        using var conflict = await client.PostAsJsonAsync(path, incident with { summary = "Different event contents" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var task = Data(await host.Call(client, "task_get", new { repositoryId = "repo", taskId, sessionId, sessionToken }));
        Assert.Equal(1, task.GetProperty("version").GetInt32());
        Assert.Equal(incident.summary, Assert.Single(task.GetProperty("messages").EnumerateArray()).GetProperty("text").GetString());
        Assert.False(task.GetProperty("workflow").TryGetProperty("targetSessionId", out _));
        var inbox = Data(await host.Call(client, "check_inbox", new { repositoryId = "repo", sessionId = target,
            sessionToken = recipient.GetProperty("sessionToken").GetString(), waitSeconds = 5, attention = false }));
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), item => item.GetRawText().Contains(taskId!, StringComparison.Ordinal));
    }

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

    private static JsonObject Workflow(string state = "ready") => new()
    {
        ["state"] = state,
        ["packet"] = new JsonObject { ["goal"] = "Finish a useful leg", ["target"] = "repo at source revision abc", ["authorityRef"] = "human-grant:17", ["restoration"] = "Remove owned fixture", ["stopCondition"] = "Stop on target mismatch" },
        ["criteria"] = new JsonArray(state == "completed" ? new JsonObject { ["id"] = "leg", ["description"] = "Required leg passes", ["result"] = "passed", ["evidenceRef"] = "test-report:17" } : new JsonObject { ["id"] = "leg", ["description"] = "Required leg passes", ["result"] = "not_run" }),
        ["blockers"] = new JsonArray()
    };

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

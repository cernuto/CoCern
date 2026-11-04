using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public sealed class ManagerSelectionHostTests
{
    [Fact]
    public async Task Composed_status_redacts_other_repository_selection_and_selected_worker_gains_no_authority()
    {
        const string adminToken = "manager-selection-fixture-admin";
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = adminToken,
            ["Relay:Clients:1:PrincipalId"] = "test-user",
            ["Relay:Clients:1:IsAdministrator"] = "true",
            ["Relay:Clients:1:Repositories:0"] = "secret-repo"
        });
        using var worker = host.AuthorizedClient(); using var admin = host.AuthorizedClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var target = Data(await host.Call(admin, RelayTools.SessionOpen, new { repositoryId = "secret-repo", requestId = "manager-target", label = "CoCern Manager", role = "manager" }));
        var session = Data(await host.Call(worker, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "worker", label = "CoCern Manager", role = "manager" }));
        var id = target.GetProperty("sessionId").GetString()!;
        var decision = new { repositoryId = "secret-repo", requestId = "select", command = "appoint_manager", targetSessionId = id, expectedManagerGeneration = 0, humanWords = "fixture-private-human-words" };
        var accepted = await host.Call(admin, RelayTools.ControlRequest, decision); Assert.True(accepted.GetProperty("ok").GetBoolean());
        foreach (var composed in new[] { false, true })
        {
            var args = new Dictionary<string, object?> { ["repositoryId"] = "repo" };
            if (composed) { args["sessionId"] = session.GetProperty("sessionId").GetString(); args["sessionToken"] = session.GetProperty("sessionToken").GetString(); }
            var result = Data(await host.Call(worker, RelayTools.GetStatus, args)); var text = result.GetRawText();
            foreach (var marker in new[] { id, "secret-repo", "fixture-private-human-words", target.GetProperty("sessionToken").GetString()! }) Assert.DoesNotContain(marker, text);
            var manager = (composed ? result.GetProperty("control") : result).GetProperty("manager");
            Assert.Equal(0, manager.GetProperty("generation").GetInt64()); Assert.Equal(JsonValueKind.Null, manager.GetProperty("current").ValueKind); Assert.Empty(manager.GetProperty("history").EnumerateArray());
        }
        Assert.True((await host.Call(admin, RelayTools.ControlRequest, new { repositoryId = "repo", requestId = "select-worker", command = "appoint_manager", targetSessionId = session.GetProperty("sessionId").GetString(), expectedManagerGeneration = 1, humanWords = "fixture selection of worker" })).GetProperty("ok").GetBoolean());
        using var replay = await worker.PostAsJsonAsync("/api/tools/control_request", new { repositoryId = "repo", requestId = "select-worker", command = "appoint_manager", targetSessionId = session.GetProperty("sessionId").GetString(), expectedManagerGeneration = 1, humanWords = "fixture selection of worker" });
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        var replayReply = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AdministratorRequired", replayReply.GetProperty("code").GetString()); Assert.DoesNotContain("fixture selection of worker", replayReply.GetRawText());
        using var denied = await worker.PostAsJsonAsync("/api/tools/control_request", new { repositoryId = "repo", requestId = "self-select", command = "appoint_manager", targetSessionId = session.GetProperty("sessionId").GetString(), expectedManagerGeneration = 2, humanWords = "select myself", role = "manager", isAdministrator = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("AdministratorRequired", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var composedHistory = Data(await host.Call(admin, RelayTools.GetStatus, new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString(), managerHistoryOnly = true })).GetProperty("control");
        Assert.True(composedHistory.GetProperty("requestsOmitted").GetBoolean()); Assert.Empty(composedHistory.GetProperty("requests").EnumerateArray());
        Assert.False(composedHistory.GetProperty("manager").GetProperty("historyReadRequired").GetBoolean()); Assert.NotEmpty(composedHistory.GetProperty("manager").GetProperty("history").EnumerateArray());
        var visible = Data(await host.Call(admin, RelayTools.GetStatus, new { repositoryId = "repo" })).GetProperty("manager");
        Assert.Equal(session.GetProperty("sessionId").GetString(), visible.GetProperty("current").GetProperty("sessionId").GetString());
        Assert.DoesNotContain("sessionToken", visible.GetRawText()); Assert.DoesNotContain(target.GetProperty("sessionToken").GetString()!, visible.GetRawText());
    }

    private static JsonElement Data(JsonElement reply) { Assert.True(reply.GetProperty("ok").GetBoolean()); using var json = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!); return json.RootElement.Clone(); }
}

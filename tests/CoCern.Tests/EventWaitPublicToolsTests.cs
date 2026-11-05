using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

// Public contract tests. Baseline meter and assertion RED precede production changes.
public sealed class EventWaitPublicToolsTests
{
    private const string Tool = "event_wait";

    [Fact]
    public void The_optional_tool_requires_own_session_and_has_only_public_wait_arguments()
    {
        Assert.True(RelayToolCatalog.TryGet(Tool, out var definition));
        Assert.True(definition.RequiresSession);
        Assert.True(definition.IsMutation);
        Assert.False(definition.AdministratorOnly);
        var fields = RelayToolCatalog.InputSchema(definition).GetProperty("properties");
        foreach (var name in new[] { "action", "waitId", "kind", "taskId", "resourceId", "generation", "peerSessionId", "replyTo", "expiresAt" })
            Assert.True(fields.TryGetProperty(name, out _), name);
        foreach (var name in new[] { "ownerSessionId", "sourceKey", "baselineRevision", "watchId" })
            Assert.False(fields.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void Default_worker_and_administrator_catalogs_do_not_pay_for_optional_waits()
    {
        Assert.DoesNotContain(RelayToolCatalog.ForRole(false), tool => tool.Name == Tool);
        Assert.DoesNotContain(RelayToolCatalog.ForRole(true), tool => tool.Name == Tool);
        Assert.Equal(18861, Cost(false));
        Assert.Equal(22603, Cost(true));
        static int Cost(bool admin) => RelayToolCatalog.ForRole(admin).Sum(tool =>
            tool.Description.Length + RelayToolCatalog.ForwardingSchema(tool).GetRawText().Length);
    }

    [Fact]
    public void Discovery_and_forwarding_require_explicit_opt_in()
    {
        Assert.Contains(RelayToolCatalog.ForRole(false, eventWaits: true), tool => tool.Name == Tool);
        Assert.DoesNotContain(Tool, CoCern.Adapter.CoordinationTools.NamesForRole(false));
        Assert.Contains(Tool, CoCern.Adapter.CoordinationTools.NamesForRole(false, eventWaits: true));
        Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(Tool, false));
        Assert.True(CoCern.Adapter.CoordinationTools.IsAllowed(Tool, false, eventWaits: true));
        Assert.DoesNotContain(CoCern.Host.ToolResults.Tools(false), tool => tool.Name == Tool);
        Assert.Contains(CoCern.Host.ToolResults.Tools(false, eventWaits: true), tool => tool.Name == Tool);
        var options = CoCern.Adapter.AdapterOptions.FromEnvironment(name => name switch
        {
            "COCERN_URL" => "http://127.0.0.1:5271", "COCERN_TOKEN" => "fixture-secret", "COCERN_EVENT_WAITS" => "true", _ => null
        });
        Assert.True(options.EventWaits);
    }

    [Fact]
    public async Task Stable_register_replay_and_fresh_read_survive_cancellation_without_exposing_a_token()
    {
        await using var host = Host(); using var client = host.AuthorizedClient();
        var handle = Data(await Call(host, client, RelayTools.SessionOpen, new() { ["repositoryId"] = "repo", ["requestId"] = "open" }));
        JsonObject Own(string request) => new() { ["repositoryId"] = "repo", ["sessionId"] = handle["sessionId"]!.DeepClone(),
            ["sessionToken"] = handle["sessionToken"]!.DeepClone(), ["requestId"] = request };
        var task = Own("task"); task["taskId"] = "task"; task["expectedVersion"] = 0; task["notes"] = "fixture";
        AssertOk(await Call(host, client, RelayTools.TaskUpdate, task));
        var register = Own("register"); register["action"] = "register"; register["waitId"] = "wait";
        register["kind"] = "task_completed"; register["taskId"] = "task"; register["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        var first = await Call(host, client, Tool, register); AssertOk(first);
        var replay = await Call(host, client, Tool, register); AssertOk(replay);
        Assert.Equal("wait", Data(first)["waitId"]!.GetValue<string>());
        Assert.Equal(Data(first).ToJsonString(), Data(replay).ToJsonString());
        var read = Own("same-read-id"); read["action"] = "read"; read["waitId"] = "wait";
        var before = await Call(host, client, Tool, read); AssertOk(before); Assert.Null(Data(before)["terminal"]);
        var cancel = Own("cancel"); cancel["action"] = "cancel"; cancel["waitId"] = "wait";
        AssertOk(await Call(host, client, Tool, cancel));
        var after = await Call(host, client, Tool, read); AssertOk(after);
        Assert.Equal("canceled", Data(after)["terminal"]!["outcome"]!.GetValue<string>());
        Assert.False(Data(after).ToJsonString().Contains(handle["sessionToken"]!.GetValue<string>(), StringComparison.Ordinal));
        var denied = (JsonObject)read.DeepClone(); denied["sessionToken"] = "wrong-fixture-token";
        Assert.Equal(RelayErrorCodes.SessionDenied, (await Call(host, client, Tool, denied, HttpStatusCode.Forbidden))["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ownerSessionId")]
    [InlineData("sourceKey")]
    [InlineData("baselineRevision")]
    public async Task Public_requests_cannot_supply_the_sources_private_provenance(string field)
    {
        await using var host = Host(); using var client = host.AuthorizedClient();
        var handle = Data(await Call(host, client, RelayTools.SessionOpen, new() { ["repositoryId"] = "repo", ["requestId"] = "open" }));
        var args = new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = handle["sessionId"]!.DeepClone(),
            ["sessionToken"] = handle["sessionToken"]!.DeepClone(), ["requestId"] = "register", ["action"] = "register", ["waitId"] = "wait",
            ["kind"] = "task_completed", ["taskId"] = "task", ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O") };
        args[field] = field == "baselineRevision" ? JsonValue.Create(999L) : JsonValue.Create("forged");
        Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(host, client, Tool, args, HttpStatusCode.BadRequest))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Public_peer_wait_completes_through_the_real_dispatcher_and_fresh_read()
    {
        await using var host = new RelayHostFixture(); using var client = host.AuthorizedClient();
        var owner = Data(await Call(host, client, RelayTools.SessionOpen, new() { ["repositoryId"] = "repo", ["requestId"] = "owner" }));
        var peer = Data(await Call(host, client, RelayTools.SessionOpen, new() { ["repositoryId"] = "repo", ["requestId"] = "peer" }));
        JsonObject Own(JsonObject handle, string request) => new() { ["repositoryId"] = "repo", ["sessionId"] = handle["sessionId"]!.DeepClone(),
            ["sessionToken"] = handle["sessionToken"]!.DeepClone(), ["requestId"] = request };
        var send = Own(owner, "original"); send["recipientSessionId"] = peer["sessionId"]!.DeepClone(); send["text"] = "original";
        var original = Data(await Call(host, client, RelayTools.SendMessage, send))["messageId"]!.GetValue<string>();
        var register = Own(owner, "register"); register["action"] = "register"; register["waitId"] = "peer-wait";
        register["kind"] = "peer_reply"; register["peerSessionId"] = peer["sessionId"]!.DeepClone(); register["replyTo"] = original;
        register["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        AssertOk(await Call(host, client, Tool, register));
        var reply = Own(peer, "reply"); reply["recipientSessionId"] = owner["sessionId"]!.DeepClone(); reply["replyTo"] = original; reply["text"] = "actual reply";
        AssertOk(await Call(host, client, RelayTools.SendMessage, reply));
        var read = Own(owner, "unchanging-read-id"); read["action"] = "read"; read["waitId"] = "peer-wait";
        JsonObject data; var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            data = Data(await Call(host, client, Tool, read));
            if (data["terminal"] is not null) break;
            await Task.Delay(25);
        } while (DateTimeOffset.UtcNow < deadline);
        var terminal = Assert.IsType<JsonObject>(data["terminal"]);
        Assert.Equal("satisfied", terminal["outcome"]!.GetValue<string>());
    }

    private static RelayHostFixture Host() => new(disableDispatcher: true);
    private static async Task<JsonObject> Call(RelayHostFixture host, HttpClient client, string tool, JsonObject args, HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, args);
        Assert.Equal(expectedStatus, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
    private static void AssertOk(JsonObject reply) => Assert.True(reply["ok"]!.GetValue<bool>(), reply["code"]?.GetValue<string>());
    private static JsonObject Data(JsonObject reply)
    {
        AssertOk(reply);
        var wireData = Assert.IsAssignableFrom<JsonValue>(reply["dataJson"]);
        return Assert.IsType<JsonObject>(JsonNode.Parse(wireData.GetValue<string>()));
    }
}

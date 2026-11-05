using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

// Public contract tests authored before catalog/composer implementation. UNRUN
// while the integration gate owns runtime; the baseline meter runs first.
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
        Assert.Equal(RelayErrorCodes.SessionDenied, (await Call(host, client, Tool, denied))["code"]!.GetValue<string>());
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
        Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(host, client, Tool, args))["code"]!.GetValue<string>());
    }

    private static RelayHostFixture Host() => new(disableDispatcher: true, settings: new Dictionary<string, string?> { ["Relay:EventWaits:Enabled"] = "true" });
    private static async Task<JsonObject> Call(RelayHostFixture host, HttpClient client, string tool, JsonObject args)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, args);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

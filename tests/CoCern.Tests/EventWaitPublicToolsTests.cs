using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

// KEEP: optional exposure boundary and one authenticated public/replay smoke.
public sealed class EventWaitPublicToolsTests
{
    private const string Tool = "event_wait";

    [Fact]
    public void Discovery_and_forwarding_require_explicit_opt_in()
    {
        Assert.True(RelayToolCatalog.TryGet(Tool, out var definition));
        Assert.True(definition.RequiresSession);
        foreach (var admin in new[] { false, true })
        {
            Assert.DoesNotContain(RelayToolCatalog.ForRole(admin), tool => tool.Name == Tool);
            Assert.Contains(RelayToolCatalog.ForRole(admin, eventWaits: true), tool => tool.Name == Tool);
        }
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
    public async Task Public_peer_wait_completes_through_the_real_dispatcher_and_fresh_read()
    {
        await using var host = new RelayHostFixture(); using var client = host.AuthorizedClient();
        var owner = Data(await Call(client, RelayTools.SessionOpen, new() { ["repositoryId"] = "repo", ["requestId"] = "owner" }));
        var peer = Data(await Call(client, RelayTools.SessionOpen, new() { ["repositoryId"] = "repo", ["requestId"] = "peer" }));
        JsonObject Own(JsonObject handle, string request) => new() { ["repositoryId"] = "repo", ["sessionId"] = handle["sessionId"]!.DeepClone(),
            ["sessionToken"] = handle["sessionToken"]!.DeepClone(), ["requestId"] = request };
        var send = Own(owner, "original"); send["recipientSessionId"] = peer["sessionId"]!.DeepClone(); send["text"] = "original";
        var original = Data(await Call(client, RelayTools.SendMessage, send))["messageId"]!.GetValue<string>();
        var register = Own(owner, "register"); register["action"] = "register"; register["waitId"] = "peer-wait";
        register["kind"] = "peer_reply"; register["peerSessionId"] = peer["sessionId"]!.DeepClone(); register["replyTo"] = original;
        register["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        // A client's fields cannot establish source identity or a baseline.
        foreach (var field in new[] { "ownerSessionId", "sourceKey", "baselineRevision" })
        {
            var forged = (JsonObject)register.DeepClone(); forged["requestId"] = "forged-" + field;
            forged[field] = field == "baselineRevision" ? JsonValue.Create(999L) : JsonValue.Create("forged");
            Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(client, Tool, forged, HttpStatusCode.BadRequest))["code"]!.GetValue<string>());
        }
        var first = Data(await Call(client, Tool, register));
        Assert.Equal(first.ToJsonString(), Data(await Call(client, Tool, register)).ToJsonString());
        var read = Own(owner, "unchanging-read-id"); read["action"] = "read"; read["waitId"] = "peer-wait";
        Assert.Null(Data(await Call(client, Tool, read))["terminal"]);
        var reply = Own(peer, "reply"); reply["recipientSessionId"] = owner["sessionId"]!.DeepClone(); reply["replyTo"] = original; reply["text"] = "actual reply";
        Data(await Call(client, RelayTools.SendMessage, reply));
        JsonObject data; var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            data = Data(await Call(client, Tool, read));
            if (data["terminal"] is not null) break;
            await Task.Delay(25);
        } while (DateTimeOffset.UtcNow < deadline);
        Assert.Equal("satisfied", Assert.IsType<JsonObject>(data["terminal"])["outcome"]!.GetValue<string>());
        Assert.DoesNotContain(owner["sessionToken"]!.GetValue<string>(), data.ToJsonString());

        // One host also checks cancellation and uncached reads under a reused
        // public requestId, then rejects a forged private session credential.
        register["waitId"] = "cancel-wait"; register["requestId"] = "cancel-register";
        Data(await Call(client, Tool, register)); read["waitId"] = "cancel-wait";
        Assert.Null(Data(await Call(client, Tool, read))["terminal"]);
        var cancel = Own(owner, "cancel"); cancel["action"] = "cancel"; cancel["waitId"] = "cancel-wait";
        Data(await Call(client, Tool, cancel));
        Assert.Equal("canceled", Data(await Call(client, Tool, read))["terminal"]!["outcome"]!.GetValue<string>());
        read["sessionToken"] = "wrong-fixture-token";
        Assert.Equal(RelayErrorCodes.SessionDenied, (await Call(client, Tool, read, HttpStatusCode.Forbidden))["code"]!.GetValue<string>());
    }

    private static async Task<JsonObject> Call(HttpClient client, string tool, JsonObject args, HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, args);
        Assert.Equal(expectedStatus, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
    private static JsonObject Data(JsonObject reply)
    {
        Assert.True(reply["ok"]!.GetValue<bool>(), reply["code"]?.GetValue<string>());
        return Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsAssignableFrom<JsonValue>(reply["dataJson"]).GetValue<string>()));
    }
}

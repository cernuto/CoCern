using System.Text.Json.Nodes;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Core;
using Orleans;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>Regression tests for the bug hunt over the claim, work-tracking and enrollment changes.</summary>
public sealed class BugHuntTests
{
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");

    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string kind, string operation, JsonObject args, int seconds = 0)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, kind, "key", Hub, operation, args, Now.AddSeconds(seconds));
    }

    private static JsonObject State(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!.AsObject();
    private static JsonObject Payload(DurableEffect effect) => JsonNode.Parse(effect.PayloadJson)!.AsObject();

    private static StateEnvelope<CoordinationDocument> OpenSession(JsonObject? extra = null)
    {
        var session = new StateEnvelope<CoordinationDocument>();
        var open = new JsonObject { ["sessionId"] = "s1", ["sessionToken"] = "secret", ["principalId"] = "alice" };
        foreach (var property in extra ?? []) open[property.Key] = property.Value?.DeepClone();
        Assert.True(Apply(session, "session", Operations.Open, open).Ok);
        return session;
    }

    [Fact]
    public async Task Session_open_arguments_cannot_forge_freshness_or_keep_a_session_from_being_pruned()
    {
        var app = new RelayApplication(System.Reflection.DispatchProxy.Create<IGrainFactory, CoordinationTests.TestGrainFactory>());
        async Task<JsonObject> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            var reply = await app.ExecuteAsync(Hub with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
            Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
            return JsonNode.Parse(reply.DataJson)!.AsObject();
        }
        var forever = "9999-12-31T00:00:00Z";
        var session = await Invoke(RelayTools.SessionOpen, new()
        {
            ["activeAt"] = forever, ["freshUntil"] = forever, ["observedAt"] = forever, ["availability"] = "available", ["bindingVersion"] = 41, ["label"] = "mine"
        });
        var status = await Invoke(RelayTools.GetStatus, new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone() });
        Assert.DoesNotContain("9999", status.ToJsonString());
        Assert.DoesNotContain("available", status.ToJsonString());
        Assert.DoesNotContain("41", status["bindingVersion"]?.ToJsonString() ?? "");
    }

    [Fact]
    public void A_reordered_older_close_leaves_a_newer_open_in_the_ledger()
    {
        var session = OpenSession();
        JsonObject Work(bool open, int at) => new ActionWorkReport("repo", "s1", "default", "t", "k", 1, open, Now.AddSeconds(at)).ToJson();
        bool Owns(RelayReply reply) => JsonNode.Parse(reply.DataJson)!["owns"]!.GetValue<bool>();
        Assert.True(Owns(Apply(session, "session", Operations.SessionWork, Work(true, 9), 10)));
        // The close from 7 arrives after the reopen at 9: the action came back to this session, so it stays open.
        Assert.True(Owns(Apply(session, "session", Operations.SessionWork, Work(false, 7), 10)));
        Assert.False(Owns(Apply(session, "session", Operations.SessionWork, Work(false, 10), 10)));
    }

    private static RelayReply? Guard(string tool, string json)
    {
        Assert.True(RelayToolCatalog.TryGet(tool, out var definition));
        return RelayRequestGuard.Validate(Hub, definition!, JsonDocument.Parse(System.Text.Encoding.UTF8.GetBytes(json)).RootElement, out _);
    }

}

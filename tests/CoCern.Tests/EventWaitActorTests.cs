using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public sealed class EventWaitActorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Primary_actor_delivers_a_real_close_once_even_after_restart()
    {
        var owner = Owner(); var source = Open("peer");
        var watch = Bind(owner);
        Assert.True(ApplyEffect(source, watch, Epoch).Ok);
        Assert.True(Close(source, "peer", force: true).Ok);
        var notice = Assert.Single(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.True(ApplyEffect(owner, notice, Epoch.AddMinutes(2)).Ok);
        Assert.Equal("target_gone", Terminal(owner)["outcome"]!.GetValue<string>());
        Assert.Single(owner.Effects, x => x.Operation == "__deliver");
        var restarted = new StateEnvelope<CoordinationDocument> { Value = new() { Json = owner.Value.Json } };
        Assert.True(ApplyEffect(restarted, notice, Epoch.AddMinutes(3)).Ok);
        Assert.Empty(restarted.Effects);
    }

    [Fact]
    public void Public_callers_cannot_install_private_watches_or_forge_lifecycle_receipts()
    {
        var owner = Owner(); var source = Open("peer"); var watch = Bind(owner);
        var before = source.Value.Json;
        var denied = CoordinationStateMachine.Apply(source, "session", "peer", Caller(), watch.Operation,
            JsonNode.Parse(watch.PayloadJson)!.AsObject(), Epoch);
        Assert.False(denied.Ok);
        Assert.Equal("InternalOnly", denied.Code);
        Assert.Equal(before, source.Value.Json);
        Assert.Empty(source.Effects);
        var lifecycle = JsonSerializer.SerializeToNode(new EventWaitLifecycle(
            new EventWaitOwnerWatch("repo", "owner", "wait", "watch", "peer", Epoch.AddMinutes(10)), "stranded"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        lifecycle["repositoryId"] = "repo";
        var ownerBefore = owner.Value.Json;
        Assert.Equal("InternalOnly", CoordinationStateMachine.Apply(owner, "session", "owner", Caller(),
            EventWaitSources.LifecycleOperation, lifecycle, Epoch).Code);
        Assert.Equal(ownerBefore, owner.Value.Json);
    }

    private static StateEnvelope<CoordinationDocument> Open(string id)
    {
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        Assert.True(CoordinationStateMachine.Apply(state, "session", id, Caller(true), "__open", new JsonObject
        { ["repositoryId"] = "repo", ["sessionId"] = id, ["sessionToken"] = "fixture-token", ["principalId"] = "principal" }, Epoch).Ok);
        state.Effects.Clear(); return state;
    }

    private static StateEnvelope<CoordinationDocument> Owner()
    {
        var owner = Open("owner");
        Assert.True(EventWaitAggregate.Apply(owner, Caller(), "owner", "register", Args("owner"), Epoch,
            new("wait", "task_completed", "board/task", "", Epoch.AddMinutes(10), BaselineRevision: 12, QualifyingData: "completed")).Ok);
        owner.Effects.Clear(); return owner;
    }

    private static DurableEffect Bind(StateEnvelope<CoordinationDocument> owner)
    {
        var watch = new EventWaitOwnerWatch("repo", "owner", "wait", "watch", "peer", Epoch.AddMinutes(10));
        Assert.True(EventWaitSources.Apply(owner, Caller(), "owner", "bind", Args("owner"), Epoch, watch).Ok);
        return Assert.Single(owner.Effects, x => x.Operation == EventWaitSources.WatchOperation);
    }

    private static RelayReply ApplyEffect(StateEnvelope<CoordinationDocument> state, DurableEffect effect, DateTimeOffset at) =>
        CoordinationStateMachine.Apply(state, effect.TargetKind, effect.TargetKey, Caller(true), effect.Operation,
            JsonNode.Parse(effect.PayloadJson)!.AsObject(), at);

    private static RelayReply Close(StateEnvelope<CoordinationDocument> state, string id, bool force, bool idle = false) =>
        CoordinationStateMachine.Apply(state, "session", id, Caller(true), "__session_expire", new JsonObject
        { ["repositoryId"] = "repo", ["sessionId"] = id, ["closeSeq"] = force ? 2 : 1, ["force"] = force,
            ["idle"] = idle, ["requestedBy"] = "fixture-operator" }, Epoch.AddMinutes(2));
    private static JsonObject Terminal(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!["eventWaits"]!["wait"]!["terminal"]!.AsObject();
    private static RelayCaller Caller(bool internalCall = false) => new("principal", false, ["repo"], internalCall);
    private static JsonObject Args(string id) => new() { ["repositoryId"] = "repo", ["sessionId"] = id, ["sessionToken"] = "fixture-token" };
}

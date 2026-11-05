using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using CoCern.Domain.Messages;
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
    public void Close_overtaking_watch_install_is_observed_by_the_primary_actor()
    {
        var owner = Owner(); var source = Open("peer"); var watch = Bind(owner);
        Assert.True(Close(source, "peer", force: true).Ok);
        source.Effects.Clear();
        Assert.True(ApplyEffect(source, watch, Epoch.AddMinutes(3)).Ok);
        var notice = Assert.Single(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.True(ApplyEffect(owner, notice, Epoch.AddMinutes(3)).Ok);
        Assert.Equal("target_gone", Terminal(owner)["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void Refused_idle_close_does_not_emit_a_gone_notice()
    {
        var owner = Owner(); var source = Open("peer");
        Assert.True(ApplyEffect(source, Bind(owner), Epoch).Ok);
        var mail = new MessageDelivery(new InboxMessage { MessageId = "unread", RepositoryId = "repo", Text = "pending", Author = "fixture", SentAt = Epoch });
        Assert.True(CoordinationStateMachine.Apply(source, "session", "peer", Caller(true), "__deliver", mail.ToJson(), Epoch).Ok);
        Assert.True(Close(source, "peer", force: false, idle: true).Ok);
        Assert.DoesNotContain(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.Null(JsonNode.Parse(source.Value.Json)!["closedAt"]);
    }

    [Fact]
    public void Forced_close_uses_real_unresolved_work_to_classify_stranding()
    {
        var owner = Owner(); var source = Open("peer");
        Assert.True(ApplyEffect(source, Bind(owner), Epoch).Ok);
        var work = new ActionWorkReport("repo", "peer", "default", "task", "action", 1, true, Epoch).ToJson();
        Assert.True(CoordinationStateMachine.Apply(source, "session", "peer", Caller(true), "__session_work", work, Epoch).Ok);
        Assert.True(Close(source, "peer", force: false).Ok);
        Assert.DoesNotContain(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.True(Close(source, "peer", force: true).Ok);
        var notice = Assert.Single(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.True(ApplyEffect(owner, notice, Epoch.AddMinutes(2)).Ok);
        Assert.Equal("stranded", Terminal(owner)["outcome"]!.GetValue<string>());
        // The session reports an existing board action; it does not release its claim or resource.
        Assert.DoesNotContain(source.Effects, x => x.Operation is "claim_release" or "resource_release");
    }

    [Fact]
    public void Closing_the_waiter_unwatches_without_delivering_to_a_replacement()
    {
        var owner = Owner(); var source = Open("peer");
        Assert.True(ApplyEffect(source, Bind(owner), Epoch).Ok);
        owner.Effects.Clear();
        Assert.True(Close(owner, "owner", force: true).Ok);
        var unwatch = Assert.Single(owner.Effects, x => x.Operation == EventWaitSources.UnwatchOperation);
        Assert.True(ApplyEffect(source, unwatch, Epoch.AddMinutes(2)).Ok);
        source.Effects.Clear();
        Assert.True(Close(source, "peer", force: true).Ok);
        Assert.DoesNotContain(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.Null(JsonNode.Parse(owner.Value.Json)!["eventWaits"]);
        var replacement = Open("replacement");
        Assert.Null(JsonNode.Parse(replacement.Value.Json)!["eventWaits"]);
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

    [Fact]
    public void A_late_private_lifecycle_for_a_closed_waiter_is_consumed_without_reviving_it()
    {
        var owner = Owner(); var source = Open("peer");
        Assert.True(ApplyEffect(source, Bind(owner), Epoch).Ok);
        Assert.True(Close(source, "peer", force: true).Ok);
        var notice = Assert.Single(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.True(Close(owner, "owner", force: true).Ok);
        owner.Effects.Clear();
        Assert.True(ApplyEffect(owner, notice, Epoch.AddMinutes(3)).Ok);
        Assert.Empty(owner.Effects);
        Assert.Null(JsonNode.Parse(owner.Value.Json)!["eventWaits"]);
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

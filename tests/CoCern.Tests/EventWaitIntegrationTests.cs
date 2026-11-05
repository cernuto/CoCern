using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

// Domain source/owner protocol integration only. Shared actor routing, real due
// dispatch and live native wake are separate gates, not simulated PASS claims.
public sealed class EventWaitIntegrationTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_registered_owner_close_becomes_one_durable_negative_terminal_notice()
    {
        var owner = Owner(); var source = Open("peer"); var watch = Watch();
        Assert.True(Call(owner, "owner", "bind", watch: watch).Ok);
        var effect = Assert.Single(owner.Effects, x => x.Operation == EventWaitSources.WatchOperation);
        Assert.DoesNotContain("fixture-token", effect.PayloadJson);
        Assert.True(Call(source, "peer", "watch", internalCall: true, watch: watch).Ok);
        Close(source, "peer");
        // The accepted primary close now emits this in its own transaction;
        // calling the old standalone hook again must not emit a second notice.
        var delivery = Assert.Single(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        var lifecycle = JsonNode.Parse(delivery.PayloadJson)!.Deserialize<EventWaitLifecycle>(Json)!;
        Assert.True(Call(owner, "owner", "lifecycle", internalCall: true, lifecycle: lifecycle).Ok);
        Assert.Equal("target_gone", Terminal(owner)["outcome"]!.GetValue<string>());
        var notice = Assert.Single(owner.Effects, x => x.Operation == "__deliver");
        Assert.Equal("owner", notice.TargetKey);
        var terminal = Terminal(owner).ToJsonString(); owner.Effects.Clear();
        Assert.True(Call(owner, "owner", "lifecycle", internalCall: true, lifecycle: lifecycle).Ok);
        Assert.Empty(owner.Effects);
        Assert.Equal(terminal, Terminal(owner).ToJsonString());
    }

    [Fact]
    public void Close_before_watch_install_is_caught_without_a_lost_wakeup()
    {
        var owner = Owner(); var source = Open("peer"); var watch = Watch();
        Assert.True(Call(owner, "owner", "bind", watch: watch).Ok);
        Close(source, "peer"); source.Effects.Clear();
        Assert.True(Call(source, "peer", "watch", internalCall: true, watch: watch).Ok);
        Assert.Single(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        source.Effects.Clear();
        Assert.True(Call(source, "peer", "watch", internalCall: true, watch: watch).Ok);
        Assert.Empty(source.Effects);
    }

    [Fact]
    public void Wrong_or_external_lifecycle_cannot_settle_another_binding()
    {
        var owner = Owner(); var watch = Watch();
        Assert.True(Call(owner, "owner", "bind", watch: watch).Ok); owner.Effects.Clear();
        Assert.False(Call(owner, "owner", "lifecycle", lifecycle: new(watch, "target_gone")).Ok);
        var before = owner.Value.Json;
        Assert.False(Call(owner, "owner", "lifecycle", internalCall: true,
            lifecycle: new(watch with { WatchId = "another" }, "target_gone")).Ok);
        Assert.Equal(before, owner.Value.Json);
        Assert.Empty(owner.Effects);
    }

    [Fact]
    public void Stranded_notice_ends_wait_without_erasing_the_existing_action_fence()
    {
        var owner = Owner(); var watch = Watch();
        Assert.True(Call(owner, "owner", "bind", watch: watch).Ok);
        var state = JsonNode.Parse(owner.Value.Json)!.AsObject();
        state["actionFenceSentinel"] = new JsonObject { ["ownerClosed"] = true, ["unresolved"] = true };
        owner.Value = new() { Json = state.ToJsonString() }; owner.Effects.Clear();
        Assert.True(Call(owner, "owner", "lifecycle", internalCall: true, lifecycle: new(watch, "stranded")).Ok);
        var after = JsonNode.Parse(owner.Value.Json)!;
        Assert.True(after["actionFenceSentinel"]!["unresolved"]!.GetValue<bool>());
        Assert.Equal("stranded", Terminal(owner)["outcome"]!.GetValue<string>());
        Assert.DoesNotContain(owner.Effects, x => x.Operation.Contains("release", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unwatch_overtaking_install_keeps_a_tombstone_and_cannot_reactivate()
    {
        var source = Open("peer"); var watch = Watch();
        Assert.True(Call(source, "peer", "unwatch", internalCall: true, watch: watch).Ok);
        Assert.True(Call(source, "peer", "watch", internalCall: true, watch: watch).Ok);
        Close(source, "peer"); source.Effects.Clear();
        Assert.True(Call(source, "peer", "close", internalCall: true).Ok);
        Assert.Empty(source.Effects);
    }

    [Fact]
    public void Source_must_really_be_closed_and_cross_repository_watch_is_refused()
    {
        var source = Open("peer");
        Assert.False(Call(source, "peer", "close", internalCall: true).Ok);
        var before = source.Value.Json;
        Assert.False(Call(source, "peer", "watch", internalCall: true, watch: Watch() with { RepositoryId = "other" }).Ok);
        Assert.Equal(before, source.Value.Json);
        Assert.Empty(source.Effects);
    }

    [Fact]
    public void A_late_subscription_retains_the_verified_stranded_close_classification()
    {
        var source = Open("peer"); Close(source, "peer"); source.Effects.Clear();
        Assert.True(Call(source, "peer", "stranded", internalCall: true).Ok);
        Assert.True(Call(source, "peer", "watch", internalCall: true, watch: Watch()).Ok);
        var effect = Assert.Single(source.Effects, x => x.Operation == EventWaitSources.LifecycleOperation);
        Assert.Equal("stranded", JsonNode.Parse(effect.PayloadJson)!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void Cancel_emits_unwatch_in_the_same_owner_transaction_as_its_terminal_notice()
    {
        var owner = Owner(); var watch = Watch();
        Assert.True(Call(owner, "owner", "bind", watch: watch).Ok); owner.Effects.Clear();
        var args = Args("owner"); args["waitId"] = "wait";
        Assert.True(EventWaitAggregate.Apply(owner, Caller(), "owner", "cancel", args, Epoch.AddMinutes(2)).Ok);
        Assert.Single(owner.Effects, x => x.Operation == "__deliver");
        var unwatch = Assert.Single(owner.Effects, x => x.Operation == EventWaitSources.UnwatchOperation);
        Assert.Equal("peer", unwatch.TargetKey);
        Assert.DoesNotContain("fixture-token", unwatch.PayloadJson);
    }

    [Theory]
    [InlineData("{\"bad\":null}")]
    [InlineData("{\"bad\":{}}")]
    [InlineData("[]")]
    public void Corrupt_source_watch_state_is_not_repaired_or_ignored(string json)
    {
        var source = Open("peer");
        var state = JsonNode.Parse(source.Value.Json)!.AsObject();
        state["eventWaitOwnerWatches"] = JsonNode.Parse(json);
        source.Value = new() { Json = state.ToJsonString() };
        var before = source.Value.Json;
        Assert.False(Call(source, "peer", "watch", internalCall: true, watch: Watch()).Ok);
        Assert.Equal(before, source.Value.Json);
        Assert.Empty(source.Effects);
    }

    private static EventWaitOwnerWatch Watch() => new("repo", "owner", "wait", "subscription-1", "peer", Epoch.AddMinutes(10));
    private static JsonObject Terminal(StateEnvelope<CoordinationDocument> envelope) => JsonNode.Parse(envelope.Value.Json)!["eventWaits"]!["wait"]!["terminal"]!.AsObject();
    private static StateEnvelope<CoordinationDocument> Owner()
    {
        var owner = Open("owner");
        Assert.True(EventWaitAggregate.Apply(owner, Caller(), "owner", "register", Args("owner"), Epoch,
            new("wait", "task_completed", "board/task", "", Epoch.AddMinutes(10), BaselineRevision: 12, QualifyingData: "completed")).Ok);
        owner.Effects.Clear(); return owner;
    }
    private static StateEnvelope<CoordinationDocument> Open(string id)
    {
        var result = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        Assert.True(CoordinationStateMachine.Apply(result, "session", id, Caller(true), "__open",
            new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = id, ["sessionToken"] = "fixture-token", ["principalId"] = "principal" }, Epoch).Ok);
        result.Effects.Clear(); return result;
    }
    private static void Close(StateEnvelope<CoordinationDocument> state, string id) => Assert.True(CoordinationStateMachine.Apply(state,
        "session", id, Caller(true), "__session_expire", new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = id,
            ["closeSeq"] = 1, ["force"] = true, ["requestedBy"] = "fixture-operator" }, Epoch.AddMinutes(2)).Ok);
    private static RelayCaller Caller(bool internalCall = false) => new("principal", false, ["repo"], internalCall);
    private static JsonObject Args(string id) => new() { ["repositoryId"] = "repo", ["sessionId"] = id, ["sessionToken"] = "fixture-token" };
    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string id, string operation, bool internalCall = false,
        EventWaitOwnerWatch? watch = null, EventWaitLifecycle? lifecycle = null) => EventWaitSources.Apply(state, Caller(internalCall), id,
            operation, Args(id), Epoch.AddMinutes(2), watch, lifecycle);
}

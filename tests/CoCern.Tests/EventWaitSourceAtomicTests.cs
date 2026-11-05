using System.Text.Json.Nodes;
using System.Text.Json;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

// Source-transaction contract tests. Source install/ACK is the arming boundary;
// an owner request's proposed baseline never substitutes for that capture.
public sealed class EventWaitSourceAtomicTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private const string BoardKey = "repo/default";
    private const string WatchOperation = "__event_wait_source_watch";
    private const string AckOperation = "__event_wait_source_ack";
    private const string SignalOperation = "__event_wait_source_signal";
    private static RelayCaller Caller => new("principal", false, ["repo"], true);

    [Fact]
    public void Install_captures_the_real_revision_after_a_pre_arming_completion_and_reopen()
    {
        var board = Empty();
        Update(board, 0, "ready", Epoch);
        Update(board, 1, "completed", Epoch.AddSeconds(1));
        Update(board, 2, "ready", Epoch.AddSeconds(2));
        var baseline = Revision(board);
        board.Effects.Clear();
        var watch = Watch("task_completed"); watch["baselineRevision"] = 0; // Never trusted as the captured source cursor.
        Assert.True(Apply(board, "board", BoardKey, WatchOperation, watch, Epoch.AddSeconds(3)).Ok);
        var ack = Payload(Assert.Single(board.Effects, effect => effect.Operation == AckOperation));
        Assert.Equal(baseline, ack["baselineRevision"]!.GetValue<long>());
        Assert.False(ack["alreadySatisfied"]!.GetValue<bool>());
        Assert.DoesNotContain(board.Effects, effect => effect.Operation == SignalOperation);
        // The historical completion was before arming. A genuine later completion is the first fire.
        Update(board, 3, "completed", Epoch.AddSeconds(4));
        var signal = Payload(Assert.Single(board.Effects, effect => effect.Operation == SignalOperation));
        Assert.True(long.Parse(signal["revision"]!.GetValue<string>()) > baseline);
    }

    [Fact]
    public void A_completed_install_snapshot_is_reported_explicitly_and_never_fabricated_as_a_fire()
    {
        var board = Empty(); Update(board, 0, "completed", Epoch); board.Effects.Clear();
        Assert.True(Apply(board, "board", BoardKey, WatchOperation, Watch("task_completed"), Epoch.AddSeconds(1)).Ok);
        var ack = Payload(Assert.Single(board.Effects, effect => effect.Operation == AckOperation));
        Assert.True(ack["alreadySatisfied"]!.GetValue<bool>());
        Assert.Equal(Revision(board), ack["baselineRevision"]!.GetValue<long>());
        Assert.DoesNotContain(board.Effects, effect => effect.Operation == SignalOperation);
        // A notes edit while still completed has a newer revision but is not a completion transition.
        Assert.True(Apply(board, "board", BoardKey, "task_update", new JsonObject
        { ["taskId"] = "task", ["expectedVersion"] = 1, ["notes"] = "new note" }, Epoch.AddSeconds(2)).Ok);
        Assert.DoesNotContain(board.Effects, effect => effect.Operation == SignalOperation);
        Update(board, 2, "ready", Epoch.AddSeconds(3));
        Update(board, 3, "completed", Epoch.AddSeconds(4));
        Assert.Single(board.Effects, effect => effect.Operation == SignalOperation);
    }

    [Fact]
    public void Watch_replay_after_restart_keeps_its_original_baseline_and_cannot_refire()
    {
        var board = Empty(); Update(board, 0, "ready", Epoch); board.Effects.Clear();
        var watch = Watch("task_completed");
        Assert.True(Apply(board, "board", BoardKey, WatchOperation, watch, Epoch.AddSeconds(1)).Ok);
        var firstAck = Payload(Assert.Single(board.Effects, effect => effect.Operation == AckOperation));
        Update(board, 1, "completed", Epoch.AddSeconds(2));
        Assert.Single(board.Effects, effect => effect.Operation == SignalOperation);
        var restarted = new StateEnvelope<CoordinationDocument> { Value = new() { Json = board.Value.Json } };
        Assert.True(Apply(restarted, "board", BoardKey, WatchOperation, watch, Epoch.AddSeconds(3)).Ok);
        var replayAck = Payload(Assert.Single(restarted.Effects, effect => effect.Operation == AckOperation));
        Assert.Equal(firstAck["baselineRevision"]!.GetValue<long>(), replayAck["baselineRevision"]!.GetValue<long>());
        Assert.DoesNotContain(restarted.Effects, effect => effect.Operation == SignalOperation);
        Update(restarted, 2, "ready", Epoch.AddSeconds(4));
        Update(restarted, 3, "completed", Epoch.AddSeconds(5));
        Assert.DoesNotContain(restarted.Effects, effect => effect.Operation == SignalOperation);
    }

    [Fact]
    public void Renewed_claim_does_not_fire_at_the_old_expiry_and_only_its_exact_generation_release_fires()
    {
        var resource = Empty();
        Assert.True(Apply(resource, "lock", "resource", "claim_acquire", Claim(leaseSeconds: 30), Epoch).Ok);
        var watch = Watch("claim_released"); watch["revision"] = "1";
        Assert.True(Apply(resource, "lock", "resource", WatchOperation, watch, Epoch.AddSeconds(1)).Ok);
        var ack = Payload(Assert.Single(resource.Effects, effect => effect.Operation == AckOperation));
        Assert.Equal(1, ack["baselineRevision"]!.GetValue<long>());
        Assert.True(Apply(resource, "lock", "resource", "claim_renew", Claim(leaseSeconds: 60), Epoch.AddSeconds(10)).Ok);
        Assert.True(Apply(resource, "lock", "resource", "__event_wait_claim_due", new(), Epoch.AddSeconds(31)).Ok);
        Assert.DoesNotContain(resource.Effects, effect => effect.Operation == SignalOperation);
        Assert.True(Apply(resource, "lock", "resource", "claim_release", Claim(), Epoch.AddSeconds(40)).Ok);
        Assert.Equal("1", Payload(Assert.Single(resource.Effects, effect => effect.Operation == SignalOperation))["revision"]!.GetValue<string>());
        resource.Effects.Clear();
        Assert.True(Apply(resource, "lock", "resource", "claim_acquire", Claim(leaseSeconds: 60), Epoch.AddSeconds(41)).Ok);
        var next = Claim(); next["generation"] = 2;
        Assert.True(Apply(resource, "lock", "resource", "claim_release", next, Epoch.AddSeconds(42)).Ok);
        Assert.DoesNotContain(resource.Effects, effect => effect.Operation == SignalOperation);
    }

    [Fact]
    public void Ordinary_expiry_emits_once_without_a_read_but_an_unresolved_action_keeps_its_fence()
    {
        foreach (var blocked in new[] { false, true })
        {
            var resource = Empty();
            Assert.True(Apply(resource, "lock", "resource", "claim_acquire", Claim(leaseSeconds: 30), Epoch).Ok);
            var watch = Watch("claim_released"); watch["revision"] = "1";
            Assert.True(Apply(resource, "lock", "resource", WatchOperation, watch, Epoch.AddSeconds(1)).Ok);
            if (blocked)
                Assert.True(Apply(resource, "lock", "resource", "__block", new JsonObject
                { ["actionId"] = "action", ["actionKey"] = "board/task/action/1", ["sessionId"] = "holder" }, Epoch.AddSeconds(2)).Ok);
            resource.Effects.Clear();
            Assert.True(Apply(resource, "lock", "resource", "__event_wait_claim_due", new(), Epoch.AddSeconds(31)).Ok);
            Assert.Equal(blocked ? 0 : 1, resource.Effects.Count(effect => effect.Operation == SignalOperation));
            if (blocked) Assert.NotEmpty(JsonNode.Parse(resource.Value.Json)!["blockers"]!.AsObject());
            resource.Effects.Clear();
            Assert.True(Apply(resource, "lock", "resource", "__event_wait_claim_due", new(), Epoch.AddSeconds(32)).Ok);
            Assert.DoesNotContain(resource.Effects, effect => effect.Operation == SignalOperation);
        }
    }

    [Fact]
    public void Unwatch_overtaking_install_cannot_arm_or_emit_afterward()
    {
        var board = Empty(); Update(board, 0, "ready", Epoch); board.Effects.Clear();
        var watch = Watch("task_completed");
        Assert.True(Apply(board, "board", BoardKey, "__event_wait_source_unwatch", watch, Epoch.AddSeconds(1)).Ok);
        Assert.True(Apply(board, "board", BoardKey, WatchOperation, watch, Epoch.AddSeconds(2)).Ok);
        Update(board, 1, "completed", Epoch.AddSeconds(3));
        Assert.DoesNotContain(board.Effects, effect => effect.Operation == SignalOperation);
        Assert.False(Payload(Assert.Single(board.Effects, effect => effect.Operation == AckOperation))["armed"]!.GetValue<bool>());
    }

    [Fact]
    public void Signal_overtaking_ack_is_buffered_then_settled_once_and_survives_restart()
    {
        var board = Empty(); Update(board, 0, "ready", Epoch); board.Effects.Clear();
        var owner = Owner();
        var install = Assert.Single(owner.Effects, effect => effect.Operation == WatchOperation);
        Assert.True(Deliver(board, install, Epoch.AddSeconds(1)).Ok);
        var ack = Assert.Single(board.Effects, effect => effect.Operation == AckOperation);
        Update(board, 1, "completed", Epoch.AddSeconds(2));
        var signal = Assert.Single(board.Effects, effect => effect.Operation == SignalOperation);
        owner.Effects.Clear();
        Assert.True(Deliver(owner, signal, Epoch.AddSeconds(3)).Ok);
        Assert.Null(Entry(owner)["terminal"]);
        Assert.NotNull(Entry(owner)["pendingSourceRevision"]);
        var restarted = new StateEnvelope<CoordinationDocument> { Value = new() { Json = owner.Value.Json } };
        Assert.True(Deliver(restarted, ack, Epoch.AddSeconds(4)).Ok);
        Assert.Equal("satisfied", Entry(restarted)["terminal"]!["outcome"]!.GetValue<string>());
        Assert.Single(restarted.Effects, effect => effect.Operation == "__deliver");
        Assert.Single(restarted.Effects, effect => effect.Operation == "__event_wait_source_unwatch");
        restarted.Effects.Clear();
        Assert.True(Deliver(restarted, signal, Epoch.AddSeconds(5)).Ok);
        Assert.True(Deliver(restarted, ack, Epoch.AddSeconds(5)).Ok);
        Assert.Empty(restarted.Effects);
    }

    [Fact]
    public void An_already_satisfied_ack_does_not_fire_the_owner_and_cancel_overtakes_late_events()
    {
        var board = Empty(); Update(board, 0, "completed", Epoch); board.Effects.Clear();
        var owner = Owner(); var install = Assert.Single(owner.Effects, effect => effect.Operation == WatchOperation);
        Assert.True(Deliver(board, install, Epoch.AddSeconds(1)).Ok);
        var ack = Assert.Single(board.Effects, effect => effect.Operation == AckOperation);
        Assert.True(Deliver(owner, ack, Epoch.AddSeconds(2)).Ok);
        Assert.Null(Entry(owner)["terminal"]);
        Assert.True(Entry(owner)["sourceAck"]!["alreadySatisfied"]!.GetValue<bool>());
        Update(board, 1, "ready", Epoch.AddSeconds(3)); Update(board, 2, "completed", Epoch.AddSeconds(4));
        var signal = Assert.Single(board.Effects, effect => effect.Operation == SignalOperation);
        var cancel = OwnerArgs(); cancel["waitId"] = "wait";
        Assert.True(Apply(owner, "session", "owner", "__event_wait_cancel", cancel, Epoch.AddSeconds(5)).Ok);
        var receipt = Entry(owner)["terminal"]!.ToJsonString(); owner.Effects.Clear();
        Assert.True(Deliver(owner, signal, Epoch.AddSeconds(6)).Ok);
        Assert.Equal(receipt, Entry(owner)["terminal"]!.ToJsonString());
        Assert.Empty(owner.Effects);
    }

    [Fact]
    public void Wrong_subscription_cannot_acknowledge_or_settle_another_wait()
    {
        var owner = Owner(); var before = owner.Value.Json;
        var watch = Watch("task_completed"); watch["watchId"] = "another-watch";
        var forged = new JsonObject { ["watch"] = watch, ["baselineRevision"] = 0, ["armed"] = true, ["alreadySatisfied"] = false };
        Assert.False(Apply(owner, "session", "owner", AckOperation, forged, Epoch.AddSeconds(1)).Ok);
        Assert.Equal(before, owner.Value.Json);
    }

    private static StateEnvelope<CoordinationDocument> Owner()
    {
        var owner = Empty(); var opening = OwnerArgs(); opening["principalId"] = "principal";
        Assert.True(Apply(owner, "session", "owner", "__open", opening, Epoch).Ok);
        var registration = OwnerArgs();
        registration["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest("wait", "task_completed", BoardKey + "/task", "",
            Epoch.AddMinutes(10), BaselineRevision: 0, QualifyingData: "completed"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(Apply(owner, "session", "owner", "__event_wait_register", registration, Epoch).Ok);
        owner.Effects.Clear();
        var binding = Watch("task_completed"); binding["sessionId"] = "owner"; binding["sessionToken"] = "fixture-token";
        Assert.True(Apply(owner, "session", "owner", "__event_wait_source_bind", binding, Epoch).Ok);
        return owner;
    }
    private static JsonObject OwnerArgs() => new() { ["sessionId"] = "owner", ["sessionToken"] = "fixture-token" };
    private static JsonObject Entry(StateEnvelope<CoordinationDocument> owner) => JsonNode.Parse(owner.Value.Json)!["eventWaits"]!["wait"]!.AsObject();
    private static RelayReply Deliver(StateEnvelope<CoordinationDocument> target, DurableEffect effect, DateTimeOffset now) =>
        Apply(target, effect.TargetKind, effect.TargetKey, effect.Operation, Payload(effect), now);

    private static JsonObject Watch(string kind) => new()
    {
        ["repositoryId"] = "repo", ["ownerSessionId"] = "owner", ["waitId"] = "wait", ["watchId"] = "watch",
        ["kind"] = kind, ["sourceKind"] = kind == "task_completed" ? "board" : "lock",
        ["sourceKey"] = kind == "task_completed" ? BoardKey : "resource", ["taskId"] = kind == "task_completed" ? "task" : null,
        ["expiresAt"] = Epoch.AddMinutes(10).ToString("O"), ["qualifyingData"] = kind == "task_completed" ? "completed" : null
    };
    private static JsonObject Claim(int leaseSeconds = 60) => new() { ["sessionId"] = "holder", ["generation"] = 1, ["leaseSeconds"] = leaseSeconds };
    private static StateEnvelope<CoordinationDocument> Empty() => new() { Value = new() { Json = "{}" } };
    private static JsonObject Payload(DurableEffect effect) => JsonNode.Parse(effect.PayloadJson)!.AsObject();
    private static long Revision(StateEnvelope<CoordinationDocument> board) => JsonNode.Parse(board.Value.Json)!["tasks"]!["task"]!["changeRevision"]!.GetValue<long>();
    private static void Update(StateEnvelope<CoordinationDocument> board, long version, string state, DateTimeOffset now)
    {
        var workflow = new JsonObject
        {
            ["state"] = state, ["packet"] = new JsonObject { ["goal"] = "fixture", ["target"] = "isolated board", ["authorityRef"] = "test fixture",
                ["restoration"] = "none", ["stopCondition"] = "refused", ["resources"] = new JsonArray() },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "proof", ["description"] = "fixture", ["result"] = state == "completed" ? "passed" : "not_run",
                ["evidenceRef"] = state == "completed" ? "fixture-evidence" : null }), ["blockers"] = new JsonArray()
        };
        Assert.True(Apply(board, "board", BoardKey, "task_update", new JsonObject
        { ["taskId"] = "task", ["expectedVersion"] = version, ["workflow"] = workflow }, now).Ok);
    }
    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string kind, string key, string operation, JsonObject args, DateTimeOffset now)
    {
        args["repositoryId"] = "repo"; args["boardId"] ??= "default";
        return CoordinationStateMachine.Apply(state, kind, key, Caller, operation, args, now);
    }
}

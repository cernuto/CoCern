using System.Text.Json.Nodes;
using CoCern.Domain;


using Xunit;

namespace CoCern.Tests;

// Contract checkpoint only. No native client, shared hub or actual timer is run.
// Target snapshots represent future authenticated producer input, not caller JSON.
public sealed class EventWaitTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-11-04T00:00:00Z");

    [Fact]
    public void Completed_snapshot_and_duplicate_transition_make_one_terminal_delivery()
    {
        var state = Session();
        var done = Signal("task_completed", "board/task", "12");
        var registered = Context(state, internalCall: true);
        Assert.True(Register(registered, Request(), done).Ok);
        Assert.Equal("satisfied", Receipt(registered, "wait-1")["outcome"]!.GetValue<string>());
        var duplicate = Context(state, internalCall: true);
        Assert.True(Observe(duplicate, done).Ok);
        Assert.Single(Deliveries(registered));
        Assert.Empty(Deliveries(duplicate));
    }

    [Fact]
    public void Peer_reply_requires_exact_repository_sender_and_owned_message_correlation()
    {
        var state = Session();
        var request = Request() with { Kind = "peer_reply", Target = "message-1", Revision = "", BaselineRevision = null, QualifyingData = null, PeerSessionId = "peer", ReplyTo = "message-1" };
        var register = Context(state);
        Assert.True(Register(register, request).Ok);
        foreach (var mismatch in new[]
        {
            Signal("peer_reply", "message-1", "reply-1") with { RepositoryId = "other", PeerSessionId = "peer", ReplyTo = "message-1" },
            Signal("peer_reply", "message-1", "reply-1") with { PeerSessionId = "wrong", ReplyTo = "message-1" },
            Signal("peer_reply", "message-1", "reply-1") with { PeerSessionId = "peer", ReplyTo = "other" }
        })
        {
            var context = Context(state, internalCall: true);
            Observe(context, mismatch);
            Assert.Empty(Deliveries(context));
        }
        var exact = Context(state, internalCall: true);
        Assert.True(Observe(exact, Signal("peer_reply", "message-1", "reply-1") with { PeerSessionId = "peer", ReplyTo = "message-1" }).Ok);
        Assert.Single(Deliveries(exact));
        Assert.Equal("satisfied", Receipt(exact, "wait-1")["outcome"]!.GetValue<string>());
        // Ownership of message-1 must additionally be proven by the producer integration tests.
    }

    [Fact]
    public void Ordinary_claim_expiry_signal_completes_without_an_incidental_resource_read()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request() with { Kind = "claim_released", Target = "resource", Revision = "7", BaselineRevision = null, QualifyingData = null }).Ok);
        var dueEvent = Context(state, internalCall: true, now: Epoch.AddMinutes(2));
        Assert.True(Observe(dueEvent, Signal("claim_released", "resource", "7")).Ok);
        Assert.Single(Deliveries(dueEvent));
        Assert.Equal("7", Receipt(dueEvent, "wait-1")["revision"]!.GetValue<string>());
        // Actual lock due-work emitter is a separate integration gate, not simulated here.
    }

    [Fact]
    public void Delayed_old_generation_does_not_complete_reacquired_generation_wait()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request() with { Kind = "claim_released", Target = "resource", Revision = "8", BaselineRevision = null, QualifyingData = null }).Ok);
        var old = Context(state, internalCall: true);
        Assert.True(Observe(old, Signal("claim_released", "resource", "7")).Ok);
        Assert.Empty(Deliveries(old));
        var current = Context(state, internalCall: true);
        Assert.True(Observe(current, Signal("claim_released", "resource", "8")).Ok);
        Assert.Single(Deliveries(current));
    }

    [Fact]
    public void Stranded_terminal_notice_keeps_action_and_lock_fence_state_unchanged()
    {
        var state = Session();
        state["actionFenceSentinel"] = new JsonObject { ["generation"] = 7, ["unresolved"] = true };
        var originalFence = state["actionFenceSentinel"]!.ToJsonString();
        Assert.True(Register(Context(state), Request()).Ok);
        var context = Context(state, internalCall: true);
        Assert.True(Observe(context, Signal("task_completed", "board/task", "12", "stranded")).Ok);
        Assert.Equal("stranded", Receipt(context, "wait-1")["outcome"]!.GetValue<string>());
        Assert.Equal(originalFence, state["actionFenceSentinel"]!.ToJsonString());
        Assert.Single(Deliveries(context));
        Assert.DoesNotContain(Deliveries(context), x => x.Operation.Contains("release", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cancel_wins_once_and_registration_replay_cannot_replace_terminal_receipt()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request()).Ok);
        var canceled = Context(state);
        Assert.True(Cancel(canceled, "wait-1").Ok);
        var receipt = Receipt(canceled, "wait-1").ToJsonString();
        var late = Context(state, internalCall: true);
        Assert.True(Observe(late, Signal("task_completed", "board/task", "12")).Ok);
        Assert.True(Register(Context(state), Request()).Ok);
        Assert.Equal(receipt, Receipt(late, "wait-1").ToJsonString());
        Assert.Single(Deliveries(canceled));
        Assert.Empty(Deliveries(late));
    }

    [Fact]
    public void Restart_keeps_terminal_receipt_and_durable_wake_identity()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request()).Ok);
        var first = Context(state, internalCall: true);
        Assert.True(Observe(first, Signal("task_completed", "board/task", "12")).Ok);
        var effect = Assert.Single(Deliveries(first));
        Assert.Equal("owner", effect.TargetKey);
        var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
        Assert.NotEqual(false, payload["wake"]?.GetValue<bool>()); // omitted means true in existing MessageDelivery wire contract
        Assert.False(string.IsNullOrWhiteSpace(payload["messageId"]!.GetValue<string>()));
        Assert.Equal(payload["messageId"]!.GetValue<string>(), Receipt(first, "wait-1")["messageId"]!.GetValue<string>());
        var restored = JsonNode.Parse(state.ToJsonString())!.AsObject();
        var replay = Context(restored, internalCall: true);
        Assert.True(Observe(replay, Signal("task_completed", "board/task", "12")).Ok);
        Assert.Equal(Receipt(first, "wait-1").ToJsonString(), Receipt(replay, "wait-1").ToJsonString());
        Assert.Empty(Deliveries(replay));
        // Persistence/delivery crash atomicity remains an actual outbox integration case.
    }

    [Fact]
    public void Verified_owner_gone_event_terminates_without_transferring_the_wait()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request()).Ok);
        var external = Context(state);
        Assert.False(Observe(external, Signal("task_completed", "board/task", "12", "target_gone")).Ok);
        Assert.Empty(Deliveries(external));
        var internalEvent = Context(state, internalCall: true);
        Assert.True(Observe(internalEvent, Signal("task_completed", "board/task", "12", "target_gone")).Ok);
        Assert.Equal("target_gone", Receipt(internalEvent, "wait-1")["outcome"]!.GetValue<string>());
        Assert.Equal("owner", Assert.Single(Deliveries(internalEvent)).TargetKey);
    }

    [Fact]
    public void Invalid_or_unauthorized_registration_does_not_write_and_deadline_is_terminal()
    {
        var state = Session();
        var before = state.ToJsonString();
        var badAuth = Context(state, token: "wrong");
        Assert.Equal("SessionDenied", Register(badAuth, Request()).Code);
        Assert.Equal(before, state.ToJsonString());
        Assert.False(Register(Context(state), Request() with { ExpiresAt = Epoch.AddYears(1) }).Ok);
        Assert.Equal(before, state.ToJsonString());
        Assert.True(Register(Context(state), Request()).Ok);
        var due = Context(state, internalCall: true, now: Epoch.AddMinutes(10));
        Assert.True(Due(due).Ok);
        Assert.Equal("expired", Receipt(due, "wait-1")["outcome"]!.GetValue<string>());
        Assert.Single(Deliveries(due));
    }

    [Fact]
    public void Request_replay_cannot_change_its_target_or_emit_another_notice()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request()).Ok);
        var before = state.ToJsonString();
        var changed = Context(state);
        Assert.Equal("RequestConflict", Register(changed, Request() with { Target = "another/task" }).Code);
        Assert.Equal(before, state.ToJsonString());
        Assert.Empty(Deliveries(changed));
    }

    [Fact]
    public void A_client_cannot_supply_a_trusted_completion_snapshot()
    {
        var state = Session();
        var before = state.ToJsonString();
        var context = Context(state);
        Assert.Equal("InternalOnly", Register(context, Request(), Signal("task_completed", "board/task", "12")).Code);
        Assert.Equal(before, state.ToJsonString());
        Assert.Empty(context.Effects);
    }

    [Fact]
    public void Active_wait_quota_refuses_without_overwriting_an_existing_wait()
    {
        var state = Session();
        for (var index = 0; index < 64; index++)
            Assert.True(Register(Context(state), Request() with { WaitId = "wait-" + index }).Ok);
        var before = state.ToJsonString();
        Assert.Equal("CapacityExceeded", Register(Context(state), Request() with { WaitId = "overflow" }).Code);
        Assert.Equal(before, state.ToJsonString());
    }

    [Fact]
    public void Existing_scheduler_can_read_the_earliest_deadline_and_completed_waits_disappear()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request()).Ok);
        Assert.True(Register(Context(state), Request() with { WaitId = "earlier", ExpiresAt = Epoch.AddMinutes(2) }).Ok);
        var envelope = new StateEnvelope<CoordinationDocument> { Value = new() { Json = state.ToJsonString() } };
        Assert.Equal(Epoch.AddMinutes(2), EventWaitAggregate.NextDue(envelope));
        Assert.True(Cancel(Context(state), "earlier").Ok);
        envelope.Value = new() { Json = state.ToJsonString() };
        Assert.Equal(Epoch.AddMinutes(10), EventWaitAggregate.NextDue(envelope));
    }

    [Fact]
    public void Even_an_internal_source_cannot_put_an_unbounded_revision_into_terminal_mail()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request() with { Kind = "peer_reply", Target = "message", Revision = "", BaselineRevision = null, QualifyingData = null, PeerSessionId = "peer", ReplyTo = "message" }).Ok);
        var before = state.ToJsonString();
        var context = Context(state, internalCall: true);
        Assert.False(Observe(context, Signal("peer_reply", "message", new string('x', 4096)) with { PeerSessionId = "peer", ReplyTo = "message" }).Ok);
        Assert.Equal(before, state.ToJsonString());
        Assert.Empty(context.Effects);
    }

    [Fact]
    public void Corrupt_wait_container_is_refused_without_replacing_private_state()
    {
        var state = Session();
        state["eventWaits"] = "corrupt-fixture";
        var before = state.ToJsonString();
        Assert.False(Due(Context(state, internalCall: true)).Ok);
        Assert.Equal(before, state.ToJsonString());
    }

    [Theory]
    [InlineData("11", "completed", false)]
    [InlineData("12", "completed", false)]
    [InlineData("13", "completed", true)]
    [InlineData("14", "different-data", false)]
    public void Task_event_must_be_strictly_newer_than_registration_baseline_with_exact_qualification(string revision, string data, bool shouldFire)
    {
        var state = Session();
        Assert.True(Register(Context(state), Request() with { BaselineRevision = 12, QualifyingData = "completed" }).Ok);
        var context = Context(state, internalCall: true);
        Assert.True(Observe(context, Signal("task_completed", "board/task", revision) with { QualifyingData = data }).Ok);
        Assert.Equal(shouldFire, Deliveries(context).Length == 1);
    }

    [Fact]
    public void Task_baseline_and_terminal_receipt_survive_restart_without_refiring_for_later_revisions()
    {
        var state = Session();
        Assert.True(Register(Context(state), Request() with { BaselineRevision = 12, QualifyingData = "completed" }).Ok);
        Assert.Equal(12, state["eventWaits"]!["wait-1"]!["request"]!["baselineRevision"]!.GetValue<long>());
        var restored = JsonNode.Parse(state.ToJsonString())!.AsObject();
        var first = Context(restored, internalCall: true);
        Assert.True(Observe(first, Signal("task_completed", "board/task", "13") with { QualifyingData = "completed" }).Ok);
        Assert.Single(Deliveries(first));
        var receipt = Receipt(first, "wait-1").ToJsonString();
        var secondState = JsonNode.Parse(restored.ToJsonString())!.AsObject();
        var replay = Context(secondState, internalCall: true);
        Assert.True(Observe(replay, Signal("task_completed", "board/task", "14") with { QualifyingData = "completed" }).Ok);
        Assert.Empty(Deliveries(replay));
        Assert.Equal(receipt, Receipt(replay, "wait-1").ToJsonString());
    }

    private static JsonObject Session()
    {
        var state = new JsonObject();
        var envelope = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        var reply = CoordinationStateMachine.Apply(envelope, "session", "owner", new RelayCaller("principal", false, ["repo"], true), "__open",
            new JsonObject { ["sessionId"] = "owner", ["sessionToken"] = "fixture-token", ["principalId"] = "principal", ["repositoryId"] = "repo" }, Epoch);
        Assert.True(reply.Ok, reply.Code);
        return JsonNode.Parse(envelope.Value.Json)!.AsObject();
    }

    private static CandidateContext Context(JsonObject state, bool internalCall = false, DateTimeOffset? now = null, string token = "fixture-token") =>
        new(state, new RelayCaller("principal", false, ["repo"], internalCall), "event_wait_candidate",
            new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = "owner", ["sessionToken"] = token }, now ?? Epoch);

    private static EventWaitRequest Request() => new("wait-1", "task_completed", "board/task", "", Epoch.AddMinutes(10), BaselineRevision: 11, QualifyingData: "completed");
    private static EventWaitSignal Signal(string kind, string target, string revision, string outcome = "satisfied") => new("repo", kind, target, revision, outcome, QualifyingData: kind == "task_completed" ? "completed" : null);
    private static JsonObject Receipt(CandidateContext context, string id) => context.State["eventWaits"]![id]!["terminal"]!.AsObject();
    private static DurableEffect[] Deliveries(CandidateContext context) => context.Effects.Where(x => x.Operation == "__deliver").ToArray();

    private sealed record CandidateContext(JsonObject State, RelayCaller Caller, string Operation, JsonObject Args, DateTimeOffset Now)
    {
        public List<DurableEffect> Effects { get; } = [];
    }

    private static RelayReply Register(CandidateContext context, EventWaitRequest request, EventWaitSignal? snapshot = null) => Apply(context, "register", request, snapshot);
    private static RelayReply Observe(CandidateContext context, EventWaitSignal signal) => Apply(context, "observe", signal: signal);
    private static RelayReply Due(CandidateContext context) => Apply(context, "due");
    private static RelayReply Cancel(CandidateContext context, string id)
    {
        context.Args["waitId"] = id;
        return Apply(context, "cancel");
    }
    private static RelayReply Apply(CandidateContext context, string operation, EventWaitRequest? request = null, EventWaitSignal? signal = null)
    {
        var envelope = new StateEnvelope<CoordinationDocument> { Value = new() { Json = context.State.ToJsonString() } };
        var reply = EventWaitAggregate.Apply(envelope, context.Caller, "owner", operation, context.Args, context.Now, request, signal);
        if (reply.Ok)
        {
            var updated = JsonNode.Parse(envelope.Value.Json)!.AsObject();
            context.State.Clear();
            foreach (var pair in updated) context.State[pair.Key] = pair.Value?.DeepClone();
            context.Effects.AddRange(envelope.Effects);
        }
        return reply;
    }
}

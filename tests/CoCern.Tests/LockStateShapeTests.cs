using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A lock stores only the keys <see cref="LockState"/> declares, including those other parts of the Domain write on it: the lease
/// warnings (<c>AttentionSources</c>) and the standing-order watches (<c>StandingOrders</c>). The document is built through
/// <see cref="CoordinationStateMachine.Apply"/>, as the hub builds it.
/// </summary>
public sealed class LockStateShapeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);
    private const string Resource = "printer";

    private static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, string operation, JsonObject args, DateTimeOffset at)
    {
        args["repositoryId"] ??= "repo";
        return CoordinationStateMachine.Apply(envelope, AggregateKinds.Lock, Resource, Hub, operation, args, at, ordersEnabled: true);
    }

    private static RelayReply Send<T>(StateEnvelope<CoordinationDocument> envelope, T message, DateTimeOffset at) where T : class, IEffectMessage<T> =>
        Apply(envelope, T.Operation, message.ToJson(), at);

    private static RelayReply Ask<T>(StateEnvelope<CoordinationDocument> envelope, T request, DateTimeOffset at) where T : class, IInternalRequest<T> =>
        Apply(envelope, T.Operation, request.ToJson(), at);

    [Fact]
    public void A_lock_stores_only_the_keys_its_state_declares()
    {
        var envelope = new StateEnvelope<CoordinationDocument>();
        var deploy = CoordinationStateMachine.ActionKey("repo", AggregateKinds.DefaultBoard, "T-1", "deploy", 1);
        var print = CoordinationStateMachine.ActionKey("repo", AggregateKinds.DefaultBoard, "T-1", "print", 1);
        // A task claim's grant, with an action in flight on it.
        Assert.True(Apply(envelope, RelayTools.ClaimAcquire, new() { ["sessionId"] = "s1", ["requestId"] = "claim", ["taskId"] = "T-1", ["taskClaimGeneration"] = 1, ["leaseSeconds"] = 300 }, T0).Ok);
        Assert.True(Send(envelope, new ActionBlock("repo", "deploy", deploy, "s1"), T0).Ok);
        // Another action of the session was resolved: remembered, so its late block is ignored.
        Assert.True(Send(envelope, new ActionUnblock("repo", "print", print, "s1"), T0.AddSeconds(10)).Ok);
        // The lease nears its end with the action in flight, then ends: claim_expiring, then interrupted.
        Assert.True(Ask(envelope, new LockLeaseCheck("repo"), T0.AddSeconds(300 - RelayLimits.ClaimExpiringLeadSeconds)).Ok);
        Assert.True(Ask(envelope, new LockLeaseCheck("repo"), T0.AddSeconds(301)).Ok);
        // A board's standing order watches the lock's release, and one of its runs tried to release the claim.
        Assert.True(Apply(envelope, Operations.OrderWatch, new() { ["boardKey"] = "repo/ops", ["boardId"] = "ops", ["orderId"] = "o1", ["watch"] = true }, T0.AddSeconds(302)).Ok);
        Assert.True(Apply(envelope, Operations.OrderRelease, new() { ["boardKey"] = "repo/ops", ["boardId"] = "ops", ["orderId"] = "o1", ["run"] = 1, ["holderSessionId"] = "s1", ["claimFor"] = "x" }, T0.AddSeconds(303)).Ok);

        var json = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        Assert.Empty(LockState.Shape.Undeclared(json));
        // The shape is not vacuous: the lock holds every key it declares, but the tombstones only a full replay cache writes.
        Assert.Empty(LockState.Shape.Fields.Keys.Except([ReadModels.RequestTombstonesKey]).Except(json.Select(entry => entry.Key)));

        // Its typed members read what the handlers (and the lease check and orders) wrote.
        var state = new LockState(json);
        Assert.Equal(("s1", 1L, T0.AddSeconds(300)), (state.Holder, state.Generation, state.LeaseUntil));
        Assert.False(state.Live(T0.AddSeconds(301)));
        Assert.NotEmpty(state.ClaimFor);
        Assert.Equal(("s1", "alice"), (state.Blockers.Of(deploy)!.SessionId, state.Blockers.Of(deploy)!.PrincipalId));
        Assert.Equal(1, state.BlockerCount);
        Assert.True(state.IsResolved(print));
        Assert.Equal("T-1", json[LockState.WarnedTaskKey]![LockState.WarnedTaskIdKey]!.GetValue<string>());
        Assert.Equal("ops", json[LockOrderState.OrderBoardsKey]!["repo/ops"]![LockOrderState.BoardIdKey]!.GetValue<string>());
    }

    [Fact]
    public void A_claim_state_read_leaves_the_blocker_map_as_it_found_it_and_a_release_frees_the_lock()
    {
        var envelope = new StateEnvelope<CoordinationDocument>();
        Assert.True(Ask(envelope, new ClaimStateQuery("repo", "s1"), T0).Ok);
        Assert.Null(JsonNode.Parse(envelope.Value.Json)![LockState.BlockersKey]);

        Assert.True(Apply(envelope, RelayTools.ClaimAcquire, new() { ["sessionId"] = "s1", ["requestId"] = "claim", ["taskId"] = "T-1", ["taskClaimGeneration"] = 1 }, T0).Ok);
        var granted = new LockState(JsonNode.Parse(envelope.Value.Json)!.AsObject());
        Assert.True(granted.HeldBy("s1", T0));
        Assert.True(Apply(envelope, RelayTools.ClaimRelease, new() { ["sessionId"] = "s1", ["generation"] = granted.Generation }, T0.AddSeconds(1)).Ok);
        var freed = new LockState(JsonNode.Parse(envelope.Value.Json)!.AsObject());
        Assert.Equal(("", T0.AddSeconds(1), ""), (freed.Holder, freed.LeaseUntil, freed.ClaimFor));
        Assert.False(JsonNode.Parse(envelope.Value.Json)!.AsObject().ContainsKey(LockState.ClaimForKey));
    }
}

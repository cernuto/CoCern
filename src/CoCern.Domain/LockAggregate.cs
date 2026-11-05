using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// One registered resource's advisory claim. A fenced generation identifies each grant. An unresolved external
/// action blocks the resource even after its lease expires, until that action is reported resolved. Its handlers read and write the
/// document through <see cref="LockState"/>.
/// </summary>
internal static class LockAggregate
{

    private const int RetryAfterSeconds = 5;

    public static RelayReply Handle(AggregateContext context) => context.Operation switch
    {
        EventWaitSources.SourceWatchOperation or EventWaitSources.SourceUnwatchOperation => EventWaitSources.HandleSource(context, AggregateKinds.Lock),
        EventWaitSources.ClaimDueOperation => EventWaitSources.ClaimExpired(context),
        Operations.Block => Block(context),
        Operations.Unblock => Unblock(context),
        Operations.ClaimState => ClaimState(context),
        Operations.LeaseCheck => LeaseCheck(context),
        Operations.OrderWatch => StandingOrders.Watch(context),
        Operations.OrderRelease => StandingOrders.Release(context),
        RelayTools.ClaimAcquire => Acquire(context),
        RelayTools.ClaimRenew or RelayTools.ClaimRelease => RenewOrRelease(context),
        _ => Error(UnknownTool)
    };

    /// <summary>
    /// A rolled-back task claim gives an already-held grant back to the claim it served before, and forgets the grant reply it stored
    /// for that claim (<see cref="Operations.ForgetRequestField"/>), so the claim's retry points the grant at itself again. Only a grant
    /// this session still holds is touched: one whose lease ran out meanwhile is never taken again.
    /// </summary>
    private static RelayReply RestoreClaimFor(AggregateContext context)
    {
        var state = new LockState(context.State);
        if (!state.HeldBy(context.SessionId, context.Now)) return Ok(new { restored = false });
        state.ClaimFor = Text(context.Args, Operations.RestoreClaimForField);
        CoordinationStateMachine.ForgetRequest(context.State, context.Caller.PrincipalId, context.SessionId, RelayTools.ClaimAcquire, Text(context.Args, Operations.ForgetRequestField));
        return Ok(new { restored = true });
    }

    /// <summary>A retried claim replays only while this session still holds the same grant.</summary>
    internal static RelayReply? ReplayRefusal(JsonObject state, string operation, JsonObject args, JsonObject grant) =>
        operation is RelayTools.ClaimAcquire or RelayTools.ClaimRenew && new LockState(state) is var current
            && (current.Holder != Text(args, "sessionId") || current.Generation != Number(grant, "generation"))
            ? Error(StaleClaim) : null;

    private static RelayReply Block(AggregateContext context)
    {
        if (!ActionBlock.Decode(context.Args).TryGet(out var block, out var malformed)) return Malformed(malformed);
        var state = new LockState(context.State);
        var (session, actionKey) = (block.SessionId, block.ActionKey);
        var blockers = state.Blockers;
        if (state.IsResolved(actionKey)) return Ok(new { resolved = true });
        if (!state.HeldBy(session, context.Now)) return Error(ClaimRequired);
        if (blockers.Count > 0 && !blockers.Contains(actionKey)) return Error(Busy, new { holder = state.Holder, unresolved = true });
        blockers.Add(actionKey, session, context.Caller.PrincipalId);
        return Ok(new { blocked = true });
    }

    /// <summary>Only the action's own writer may unblock, or an administrator explicitly accepting its cleanup.</summary>
    private static RelayReply Unblock(AggregateContext context)
    {
        if (!ActionUnblock.Decode(context.Args).TryGet(out var unblock, out var malformed)) return Malformed(malformed);
        var state = new LockState(context.State);
        var blockers = state.Blockers;
        var id = unblock.ActionKey;
        var administrativeAcceptance = context.Caller.IsAdministrator && unblock.AdministrativeAcceptance;
        if (blockers.Of(id) is { } blocker && (blocker.SessionId != unblock.SessionId || blocker.PrincipalId != context.Caller.PrincipalId && !administrativeAcceptance)) return Error(ActionDenied);
        // Remembered for the retry window so a late block for this action is ignored; a full set drops its oldest, so unblocking never fails.
        blockers.Remove(id); state.Resolve(id, context.Now);
        // With no action left in flight, a lease nearing its end is no longer worth a claim_expiring item.
        if (blockers.Count == 0) AttentionSources.OnLeaseSettled(context, state.Holder, context.Key);
        EventWaitSources.OnClaimExpiry(context);
        return Ok(new { resolved = true });
    }

    /// <summary>Due work: raises <c>claim_expiring</c> to the holder when the lease nears its end with an action in flight.</summary>
    private static RelayReply LeaseCheck(AggregateContext context)
    {
        if (!LockLeaseCheck.Decode(context.Args).TryGet(out _, out var malformed)) return Malformed(malformed);
        AttentionSources.OnLeaseCheck(context, context.Key);
        return Ok(new { checkedLease = true });
    }

    /// <summary>Read-only: whether <see cref="Acquire"/> would refuse this session now. It changes nothing and acquires nothing.</summary>
    private static RelayReply ClaimState(AggregateContext context)
    {
        if (!ClaimStateQuery.Decode(context.Args).TryGet(out var query, out var malformed)) return Malformed(malformed);
        var state = new LockState(context.State);
        var holder = state.Holder;
        var blockers = state.BlockerCount;
        var live = state.Live(context.Now);
        var owned = holder == query.SessionId && live;
        // An action already in flight on this lock blocks a new one, even this session's own: its block would be refused after the new action was recorded.
        var busy = holder.Length > 0 && holder != query.SessionId && (live || blockers > 0) || blockers > 0;
        // held: this session's grant is live and free, which action_begin needs for each resource its packet declares.
        return Ok(new { busy, held = owned && blockers == 0 });
    }

    private static RelayReply Acquire(AggregateContext context)
    {
        var state = new LockState(context.State);
        var holder = state.Holder;
        var session = context.SessionId;
        var generation = state.Generation;
        var blockers = state.Blockers;
        if (context.Args.ContainsKey(Operations.RestoreClaimForField)) return RestoreClaimFor(context);
        if (holder.Length > 0 && holder != session && (state.Live(context.Now) || blockers.Count > 0))
            return Error(Busy, new { holder, leaseUntil = state.LeaseUntilText, blockers = blockers.Count, retryAfterSeconds = RetryAfterSeconds });
        var owned = state.HeldBy(session, context.Now);
        if (blockers.Count > 0 && !owned) return Error(Busy, new { holder, blockers = blockers.Count });
        if (!owned)
        {
            EventWaitSources.OnClaimExpiry(context);
            generation = state.Grant(Required(context.Args, "sessionId"), context.Now.AddSeconds(Leases.Seconds(context.Args)));
        }
        // The task claim this grant now serves (set by task_claim, even when the holder already had it), so a standing order releases the
        // grant only for that claim and never one the same session took again for its next task.
        var previous = state.ClaimFor;
        state.ClaimFor = Text(context.Args, "taskId") is { Length: > 0 } taskId && context.Args["taskClaimGeneration"] is not null
            ? StandingOrders.ClaimKey(Text(context.Args, "boardId", "default"), taskId, Number(context.Args, "taskClaimGeneration")) : "";
        return Ok(new { newlyAcquired = !owned, generation, leaseUntil = state.LeaseUntilText, previousClaimFor = previous }, generation);
    }

    private static RelayReply RenewOrRelease(AggregateContext context)
    {
        var state = new LockState(context.State);
        var generation = state.Generation;
        var blockers = state.Blockers;
        if (!state.HeldBy(context.SessionId, context.Now) || generation != Number(context.Args, "generation")) return Error(StaleClaim, new { generation });
        var release = context.Operation == RelayTools.ClaimRelease;
        if (!release)
        {
            state.LeaseUntil = context.Now.AddSeconds(Leases.Seconds(context.Args));
            AttentionSources.OnLeaseSettled(context, context.SessionId, context.Key);
        }
        else
        {
            if (blockers.Count > 0) return Error(UnresolvedAction, new { blockers = blockers.Count });
            // A rolled-back task claim releases the grant it took, and its retry must acquire again rather than replay it.
            CoordinationStateMachine.ForgetRequest(context.State, context.Caller.PrincipalId, context.SessionId, RelayTools.ClaimAcquire, Text(context.Args, Operations.ForgetRequestField));
            Release(context, context.SessionId);
        }
        return Ok(new { generation, leaseUntil = state.LeaseUntilText, released = release }, generation);
    }

    /// <summary>Ends <paramref name="holder"/>'s grant: the lock is free, its lease warnings settle, and watching boards hear of it.</summary>
    internal static void Release(AggregateContext context, string holder)
    {
        new LockState(context.State).Free(context.Now);
        EventWaitSources.ClaimReleased(context);
        AttentionSources.OnLeaseSettled(context, holder, context.Key);
        StandingOrders.OnLockReleased(context);
    }
}

internal static class Leases
{
    public static int Seconds(JsonObject args) => (int)Math.Clamp(Number(args, "leaseSeconds", RelayLimits.DefaultLeaseSeconds), 1, RelayLimits.MaxLeaseSeconds);
}

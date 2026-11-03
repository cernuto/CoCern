using System.Collections.Frozen;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain.Messages;

/// <summary>
/// <see cref="Operations.Block"/>: action attempt <see cref="ActionKey"/> of <see cref="SessionId"/> begins on this resource, which stays
/// blocked until the action is reported resolved. Sent in one shape by two senders that both build it here: the hub blocks each lock
/// directly when <c>action_begin</c> runs, and the board queues the same block in its outbox, which is then a no-op. Retried until it
/// lands, except for the refusals in <see cref="SettledBy"/>.
/// </summary>
public sealed record ActionBlock(string RepositoryId, string ActionId, string ActionKey, string SessionId) : IEffectMessage<ActionBlock>
{
    public static string Operation => Operations.Block;
    public static string TargetKind => AggregateKinds.Lock;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    /// <summary>
    /// Refusals that end the board's queued copy instead of retrying it. The hub's direct block already decided the action: one refused as
    /// <c>Busy</c> or <c>ClaimRequired</c> never ran (the hub lifted its blocks and the action waits to be reported canceled), so the
    /// board's copy would be refused the same way on every retry.
    /// </summary>
    public static FrozenSet<string> SettledBy { get; } = new[] { RelayErrorCodes.Busy, RelayErrorCodes.ClaimRequired }.ToFrozenSet(StringComparer.Ordinal);

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["actionId"] = ActionId, ["actionKey"] = ActionKey, ["sessionId"] = SessionId };

    public static Decoding<ActionBlock> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new ActionBlock(read.Text("repositoryId"), read.Text("actionId"), read.Text("actionKey"), read.Text("sessionId")));
    }
}

/// <summary>
/// <see cref="Operations.Unblock"/>: action attempt <see cref="ActionKey"/> is resolved, so it no longer blocks this resource, and a late
/// <see cref="ActionBlock"/> for it is ignored. Only the action's own session and principal may unblock, or an administrator accepting its
/// cleanup (<see cref="AdministrativeAcceptance"/>). Sent by the board when the action resolves, and by the hub directly to lift the blocks
/// of an action that could not begin. Retried until it lands: a lost one leaves the resource blocked.
/// </summary>
public sealed record ActionUnblock(string RepositoryId, string ActionId, string ActionKey, string SessionId, bool AdministrativeAcceptance = false) : IEffectMessage<ActionUnblock>
{
    public static string Operation => Operations.Unblock;
    public static string TargetKind => AggregateKinds.Lock;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId, ["actionId"] = ActionId, ["actionKey"] = ActionKey, ["sessionId"] = SessionId };
        if (AdministrativeAcceptance) json["administrativeAcceptance"] = true;
        return json;
    }

    public static Decoding<ActionUnblock> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new ActionUnblock(read.Text("repositoryId"), read.Text("actionId"), read.Text("actionKey"), read.Text("sessionId"), read.OptionalFlag("administrativeAcceptance")));
    }
}

/// <summary>
/// <see cref="Operations.ClaimState"/>: whether <c>claim_acquire</c> would refuse <see cref="SessionId"/> on this lock now (<c>busy</c>),
/// and whether the session holds a live grant with no action in flight (<c>held</c>), which <c>action_begin</c> needs for each resource its
/// packet declares. Read-only: it acquires nothing. The hub asks it for each declared resource of a new action, and for each resource a
/// next-work offer's candidates list.
/// </summary>
public sealed record ClaimStateQuery(string RepositoryId, string SessionId) : IInternalRequest<ClaimStateQuery>
{
    public static string Operation => Operations.ClaimState;
    public static string TargetKind => AggregateKinds.Lock;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId };

    public static Decoding<ClaimStateQuery> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new ClaimStateQuery(read.Text("repositoryId"), read.Text("sessionId")));
    }
}

/// <summary>
/// <see cref="Operations.LeaseCheck"/>: the lock's own due check of its lease, which its grain runs shortly before the lease ends and again
/// when it ends while the holder has an action in flight: the holder gets a <c>claim_expiring</c> item, then an <c>interrupted</c> one.
/// The grain sends it under the lock's own repository.
/// </summary>
public sealed record LockLeaseCheck(string RepositoryId) : IInternalRequest<LockLeaseCheck>
{
    public static string Operation => Operations.LeaseCheck;
    public static string TargetKind => AggregateKinds.Lock;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId };

    public static Decoding<LockLeaseCheck> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new LockLeaseCheck(read.Text("repositoryId")));
    }
}

using System.Text.Json.Nodes;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>
/// One lock's stored document, read and written through named, typed members only: the session that holds the claim, its grant's fenced
/// generation and lease, the task claim the grant serves, the actions in flight that block the resource (<see cref="LockBlockers"/>), and
/// the actions reported resolved, remembered for the retry window. Every key the document stores is declared in <see cref="Shape"/>,
/// including the ones two other parts of the Domain write on a lock: its lease warnings and its standing-order watches. Like the
/// handlers before it, a member that reaches a map creates it when absent.
/// </summary>
public sealed class LockState(JsonObject json)
{
    public const string HolderKey = "holder";
    public const string GenerationKey = "generation";
    public const string LeaseUntilKey = "leaseUntil";
    public const string ClaimForKey = "claimFor";
    public const string BlockersKey = "blockers";
    public const string ResolvedActionsKey = "resolvedActions";

    // Lease warnings: AttentionSources' lease check writes these on the lock, so a claim_expiring or interrupted item is raised once per
    // lease and its closes name the task it was raised for.
    public const string WarnedLeaseKey = "warnedLease";
    public const string InterruptedLeaseKey = "interruptedLease";
    public const string WarnedTaskKey = "warnedTask";
    public const string WarnedBoardIdKey = "boardId";
    public const string WarnedTaskIdKey = "taskId";
    public const string AttentionRevisionKey = "attentionRevision";

    /// <summary>Every key a lock stores, by whichever part of the Domain writes it.</summary>
    public static StateShape Shape { get; } = CommonState.Shape.With(
        (HolderKey, null), (GenerationKey, null), (LeaseUntilKey, null), (ClaimForKey, null),
        (BlockersKey, StateShape.Map(Blocker.Shape)), (ResolvedActionsKey, StateShape.Map(null)),
        // Lease warnings (AttentionSources).
        (WarnedLeaseKey, null), (InterruptedLeaseKey, null), (WarnedTaskKey, StateShape.Object((WarnedBoardIdKey, null), (WarnedTaskIdKey, null))), (AttentionRevisionKey, null))
        // Standing orders (StandingOrders): the boards watching for the lock's release, and the order runs it applied.
        .With(LockOrderState.Fields).With((EventWaitSources.SubscriptionsKey, StateShape.Map(StateShape.Any)), (EventWaitRetirement.Key, StateShape.Map(null)));

    /// <summary>The session that holds the claim; empty when the lock is free.</summary>
    public string Holder
    {
        get => Text(json, HolderKey);
        set => json[HolderKey] = value;
    }

    /// <summary>The current grant's fenced number: each new grant has a higher one, so a stale holder's renew or release is refused.</summary>
    public long Generation
    {
        get => Number(json, GenerationKey);
        set => json[GenerationKey] = value;
    }

    /// <summary>When the current grant's lease ends; the earliest time when none was ever granted.</summary>
    public DateTimeOffset LeaseUntil
    {
        get => Date(json, LeaseUntilKey);
        set => json[LeaseUntilKey] = value.ToString("O");
    }

    /// <summary>The lease's end as stored, which replies return as it is; empty when none was ever granted.</summary>
    public string LeaseUntilText => Text(json, LeaseUntilKey);

    /// <summary>Whether the current grant's lease is still running.</summary>
    public bool Live(DateTimeOffset now) => LeaseUntil > now;

    /// <summary>Whether <paramref name="sessionId"/> holds the claim and its lease is still running.</summary>
    public bool HeldBy(string sessionId, DateTimeOffset now) => Holder == sessionId && Live(now);

    /// <summary>
    /// The task claim the grant serves (<see cref="StandingOrders.ClaimKey"/>), so an order releases it only for that claim; empty when the
    /// grant was taken directly. Setting it empty removes it.
    /// </summary>
    public string ClaimFor
    {
        get => Text(json, ClaimForKey);
        set
        {
            if (value.Length > 0) json[ClaimForKey] = value;
            else json.Remove(ClaimForKey);
        }
    }

    /// <summary>Grants a new claim to <paramref name="sessionId"/> until <paramref name="leaseUntil"/>, with the next generation, and returns it.</summary>
    public long Grant(string sessionId, DateTimeOffset leaseUntil)
    {
        Holder = sessionId;
        var generation = Generation + 1;
        Generation = generation;
        LeaseUntil = leaseUntil;
        return generation;
    }

    /// <summary>Frees the lock: it has no holder, its lease ended now, and it serves no task claim.</summary>
    public void Free(DateTimeOffset now)
    {
        Holder = "";
        LeaseUntil = now;
        ClaimFor = "";
    }

    /// <summary>The actions in flight that block the resource, by action key.</summary>
    public LockBlockers Blockers => new(ObjectAt(json, BlockersKey));

    /// <summary>How many actions are in flight, read without creating the map, so a read leaves the document as it found it.</summary>
    public int BlockerCount => json[BlockersKey] is JsonObject blockers ? blockers.Count : 0;

    /// <summary>Whether <paramref name="actionKey"/> was reported resolved within the retry window, so a late block for it is ignored.</summary>
    public bool IsResolved(string actionKey) => ObjectAt(json, ResolvedActionsKey).ContainsKey(actionKey);

    /// <summary>Remembers <paramref name="actionKey"/> as resolved, stamped <paramref name="now"/>; a full set drops its oldest first, so this never fails.</summary>
    public void Resolve(string actionKey, DateTimeOffset now)
    {
        var resolved = ObjectAt(json, ResolvedActionsKey);
        if (!resolved.ContainsKey(actionKey)) Retention.MakeRoom(resolved, now);
        resolved[actionKey] = Retention.Stamp(now);
    }
}

/// <summary>The actions in flight on one lock, by action key: while any is, the resource stays blocked, even past its lease.</summary>
public sealed class LockBlockers(JsonObject json)
{
    public int Count => json.Count;

    public bool Contains(string actionKey) => json.ContainsKey(actionKey);

    /// <summary>The action <paramref name="actionKey"/>'s block, or null when it blocks nothing here.</summary>
    public Blocker? Of(string actionKey) => json[actionKey] is JsonObject blocker ? new(blocker) : null;

    /// <summary>Blocks the resource for the action <paramref name="actionKey"/> of <paramref name="sessionId"/>, written by <paramref name="principalId"/>.</summary>
    public void Add(string actionKey, string sessionId, string principalId) =>
        json[actionKey] = new JsonObject { [Blocker.SessionIdKey] = sessionId, [Blocker.PrincipalIdKey] = principalId };

    public void Remove(string actionKey) => json.Remove(actionKey);
}

/// <summary>One action's block on a lock: the session it belongs to and the principal that wrote it, the only ones that may lift it.</summary>
public sealed class Blocker(JsonObject json)
{
    public const string SessionIdKey = "sessionId";
    public const string PrincipalIdKey = "principalId";

    public static StateShape Shape { get; } = StateShape.Object((SessionIdKey, null), (PrincipalIdKey, null));

    public string SessionId => Text(json, SessionIdKey);
    public string PrincipalId => Text(json, PrincipalIdKey);
}

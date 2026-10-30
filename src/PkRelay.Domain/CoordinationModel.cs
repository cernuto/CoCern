namespace PkRelay.Domain;

/// <summary>An authenticated principal. <see cref="IsInternal"/> marks calls the hub itself composes; only those may run "__" operations.</summary>
public sealed record RelayCaller(string PrincipalId, bool IsAdministrator, string[] Repositories, bool IsInternal = false)
{
    public bool MayAccess(string repository) =>
        repository.Length > 0 && (IsAdministrator || Repositories.Contains(repository, StringComparer.Ordinal));
}

/// <summary>Every operation's outcome. <see cref="DataJson"/> is the structured payload; <see cref="Version"/> is the record version it observed or produced.</summary>
public sealed record RelayReply(bool Ok, string Code, string DataJson, long Version = 0);

/// <summary>An operation another aggregate must apply. Persisted with its source until delivered (transactional outbox).</summary>
public sealed record DurableEffect(string Id, string TargetKind, string TargetKey, string Operation, string PayloadJson, DateTimeOffset DueAt);

public sealed record DueWorkItem(string Id, string Kind, DateTimeOffset DueAt);

public interface IHasDueWork
{
    IReadOnlyList<DueWorkItem> GetDueWork();
}

/// <summary>An aggregate's persisted state plus its pending outbox and timers, committed atomically.</summary>
public sealed class StateEnvelope<T> : IHasDueWork where T : new()
{
    public int SchemaVersion { get; set; } = 1;
    public T Value { get; set; } = new();
    public List<DurableEffect> Effects { get; set; } = [];
    public List<DueWorkItem> DueWork { get; set; } = [];
    public IReadOnlyList<DueWorkItem> GetDueWork() => DueWork.Concat(Effects.Select(e => new DueWorkItem(e.Id, "effect", e.DueAt))).ToArray();
}

/// <summary>An aggregate's state document. Its JSON shape is the persisted format.</summary>
public sealed class CoordinationDocument
{
    public string Json { get; set; } = "{}";
}

/// <summary>The four coordination aggregates. Each is a single-writer state machine keyed by a string.</summary>
public static class AggregateKinds
{
    public const string Control = "control";
    public const string Session = "session";
    public const string Board = "board";
    public const string Lock = "lock";

    /// <summary>The machine-scoped discovery aggregate; a hub singleton keyed by <see cref="ControlKey"/>, applied by <see cref="DiscoveryStateMachine"/> rather than the repository-scoped state machine.</summary>
    public const string Discovery = "discovery";

    /// <summary>The control aggregate is a singleton.</summary>
    public const string ControlKey = "hub";
}

/// <summary>Hub-internal operations. They compose public tools across aggregates and are never accepted from clients.</summary>
public static class Operations
{
    public const string Open = "__open";
    public const string Verify = "__verify";
    public const string Register = "__register";
    public const string Deliver = "__deliver";
    public const string Resolve = "__resolve";
    public const string Block = "__block";
    public const string Unblock = "__unblock";
    public const string Dispatch = "__dispatch";
    public const string TaskUnclaim = "__task_unclaim";
    public const string NextWork = "__next_work";
    /// <summary>Read-only: which of the given sessions in the repository the caller's principal opened.</summary>
    public const string SessionOwners = "__session_owners";

    public static bool IsInternal(string operation) => operation.StartsWith("__", StringComparison.Ordinal);
}

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

/// <summary>
/// One searchable piece of an aggregate's state. <see cref="DocId"/> is the ID the agent opens it with (task, context, message,
/// line or grant ID); it is unique per kind within one grain. <see cref="OwnerSessionId"/> is set only for private inbox messages.
/// </summary>
public sealed record SearchDocument(
    string DocId, string Kind, string RepositoryId, string? OwnerSessionId,
    string Title, string Body, string Status, DateTimeOffset UpdatedAt);

/// <summary>Identifies one search document within its grain: storage keys rows by (grain, kind, doc_id).</summary>
public sealed record SearchDocumentKey(string Kind, string DocId);

/// <summary>A document to index with the change marker stored beside its row; see <see cref="SearchExtraction.Markers"/>.</summary>
public sealed record SearchEntry(SearchDocument Document, string Marker);

/// <summary>The rows one commit changes for its grain: insert or replace each of <see cref="Upserts"/> by key, and delete <see cref="Removed"/>.</summary>
public sealed record SearchUpdate(IReadOnlyList<SearchEntry> Upserts, IReadOnlyList<SearchDocumentKey> Removed);

/// <summary>State that describes its own searchable text, as <see cref="IHasDueWork"/> describes its timers. Storage decides how to index it.</summary>
public interface IHasSearchDocuments
{
    IReadOnlyList<SearchDocument> GetSearchDocuments(string stateName);

    /// <summary>The rows to write given what the index holds for this grain now: each indexed key with its stored marker.</summary>
    SearchUpdate GetSearchUpdate(string stateName, IReadOnlyDictionary<SearchDocumentKey, string> indexed);

    /// <summary>
    /// An object that stays the same instance while the searchable content is unchanged (the state document's JSON string), so storage
    /// can skip a commit that only changed the outbox or timers. Null when there is nothing to identify.
    /// </summary>
    object? GetSearchSource();

    /// <summary>Drops anything kept only for indexing this commit. Storage calls it after every write, indexed or not.</summary>
    void ReleaseSearchState();
}

/// <summary>An aggregate's persisted state plus its pending outbox and timers, committed atomically.</summary>
public sealed class StateEnvelope<T> : IHasDueWork, IHasSearchDocuments where T : new()
{
    public int SchemaVersion { get; set; } = 1;
    public T Value { get; set; } = new();
    public List<DurableEffect> Effects { get; set; } = [];
    public List<DueWorkItem> DueWork { get; set; } = [];
    public IReadOnlyList<DueWorkItem> GetDueWork() => DueWork.Concat(Effects.Select(e => new DueWorkItem(e.Id, "effect", e.DueAt))).ToArray();
    public IReadOnlyList<SearchDocument> GetSearchDocuments(string stateName) => Value is CoordinationDocument document ? SearchExtraction.Extract(stateName, document.Json) : [];
    /// <summary>Uses the parsed state the state machine kept for this commit, when there is one, so the document is not parsed again.</summary>
    public SearchUpdate GetSearchUpdate(string stateName, IReadOnlyDictionary<SearchDocumentKey, string> indexed)
    {
        if (Value is not CoordinationDocument document) return new([], indexed.Keys.ToArray());
        var parsed = document.TakeParsed();
        return parsed is not null ? SearchExtraction.Update(stateName, parsed, indexed) : SearchExtraction.Update(stateName, document.Json, indexed);
    }

    public object? GetSearchSource() => Value is CoordinationDocument document ? document.Json : null;

    public void ReleaseSearchState() => (Value as CoordinationDocument)?.TakeParsed();
}

/// <summary>An aggregate's state document. Its JSON shape is the persisted format.</summary>
public sealed class CoordinationDocument
{
    private string _json = "{}";
    private System.Text.Json.Nodes.JsonObject? _parsed;

    public string Json { get => _json; set { _json = value; _parsed = null; } }

    /// <summary>
    /// Keeps the object <see cref="Json"/> was serialized from, for the commit that follows. It is not persisted, it is dropped when
    /// <see cref="Json"/> changes, and it is taken at most once, so a large board's parsed form is not held after its commit.
    /// </summary>
    internal void KeepParsed(System.Text.Json.Nodes.JsonObject state) => _parsed = state;

    internal System.Text.Json.Nodes.JsonObject? TakeParsed()
    {
        var parsed = _parsed;
        _parsed = null;
        return parsed;
    }
}

/// <summary>The four coordination aggregates, plus the anomaly monitor. Each is a single-writer state machine keyed by a string.</summary>
public static class AggregateKinds
{
    public const string Control = "control";
    public const string Session = "session";
    public const string Board = "board";
    public const string Lock = "lock";

    /// <summary>One anomaly monitor per repository, keyed by the repository ID. It only raises and closes manager attention items.</summary>
    public const string Anomaly = "anomaly";

    /// <summary>The machine-scoped discovery aggregate; a hub singleton keyed by <see cref="ControlKey"/>, applied by <see cref="DiscoveryStateMachine"/> rather than the repository-scoped state machine.</summary>
    public const string Discovery = "discovery";

    /// <summary>The control aggregate is a singleton.</summary>
    public const string ControlKey = "hub";
}

/// <summary>Session roles the hub acts on. A role is given at <c>session_open</c>, compared ignoring case, and grants nothing.</summary>
public static class SessionRoles
{
    /// <summary>Sessions that receive manager-bound attention items; a manager session also starts its repository's anomaly monitor.</summary>
    public const string Manager = "manager";
}

/// <summary>Kinds of an aggregate's own timers (due work), besides lease expiry.</summary>
public static class DueWorkKinds
{
    /// <summary>A lock's check, shortly before its lease ends, for an action still in flight (raises <c>claim_expiring</c>).</summary>
    public const string LeaseWarning = "lease_warning";

    /// <summary>The anomaly monitor's next check, rebuilt from its state's <c>nextCheckAt</c>.</summary>
    public const string AnomalyCheck = "anomaly_check";
}

/// <summary>Hub-internal operations. They compose public tools across aggregates and are never accepted from clients.</summary>
public static class Operations
{
    public const string Open = "__open";
    public const string Verify = "__verify";
    public const string Register = "__register";
    public const string Deliver = "__deliver";
    public const string Resolve = "__resolve";
    /// <summary>Read-only: resolves several resource names in one control call.</summary>
    public const string ResolveAll = "__resolve_all";
    public const string Block = "__block";
    public const string Unblock = "__unblock";
    public const string Dispatch = "__dispatch";
    public const string TaskUnclaim = "__task_unclaim";
    public const string NextWork = "__next_work";
    /// <summary>Read-only: whether another session holds a resource now, so a next-work offer can prefer tasks whose resources are free.</summary>
    public const string ClaimState = "__claim_state";
    /// <summary>Read-only: a board's standing lines, active grants and settings in the compact form sent to chats.</summary>
    public const string Standing = "__standing";
    /// <summary>Records the standing revision a session received for a board, so later replies carry standing only when it changed.</summary>
    public const string StandingSeen = "__standing_seen";
    /// <summary>Read-only: which of the given sessions in the repository the caller's principal opened.</summary>
    public const string SessionOwners = "__session_owners";
    /// <summary>
    /// Adds an attention item to a session, or refreshes the open one with the same kind, task and subject. Sent through the outbox.
    /// Sources should always send <c>revision</c>; without one, only an identical copy within a few minutes of a close is ignored.
    /// </summary>
    public const string AttentionRaise = "__attention_raise";
    /// <summary>Closes a session's attention items for a task (optionally only some kinds) when their condition ends. Sent through the outbox.</summary>
    public const string AttentionClose = "__attention_close";
    /// <summary>One boundary: an unanswered shown item counts as later, snoozes count down, the best item is picked and marked shown.</summary>
    public const string AttentionOffer = "__attention_offer";
    /// <summary>Closes the info items of messages an adapter reader showed the model without an offer.</summary>
    public const string AttentionSeen = "__attention_seen";
    /// <summary>
    /// Sent to the control aggregate through the outbox: raises or closes an attention item at every session of one role (such as manager)
    /// in the repository, found in the session directory by the role given at <c>session_open</c>.
    /// </summary>
    public const string AttentionRoute = "__attention_route";
    /// <summary>A lock's due-work check: when its lease nears expiry with an action in flight, the holder gets a <c>claim_expiring</c> item.</summary>
    public const string LeaseCheck = "__lease_check";
    /// <summary>Starts a repository's anomaly monitor: its first check is due now unless one is already scheduled.</summary>
    public const string AnomalyStart = "__anomaly_start";
    /// <summary>The anomaly monitor's due check, with the findings the hub read from the analytics snapshot.</summary>
    public const string AnomalyCheck = "__anomaly_check";

    /// <summary>Read-only: a board's standing orders in the compact form <c>get_status</c> lists.</summary>
    public const string Orders = "__orders";
    /// <summary>Sent by a lock to each board watching it when its claim is released: runs the board's <c>claim_released</c> order.</summary>
    public const string OrderFire = "__order_fire";
    /// <summary>Sent by a board to a lock: starts or stops telling that board when the lock's claim is released.</summary>
    public const string OrderWatch = "__order_watch";
    /// <summary>Sent by a board to a lock: an order releases the claim its task's owner holds, once per order run.</summary>
    public const string OrderRelease = "__order_release";
    /// <summary>Sent by a lock back to the board: an order's release could not run (an action is still unresolved).</summary>
    public const string OrderFailed = "__order_failed";

    /// <summary>Fields of a standing order's attention raise naming its order and run; the session ignores them.</summary>
    public const string OrderIdField = "orderId";
    public const string OrderRunField = "orderRun";

    public static bool IsInternal(string operation) => operation.StartsWith("__", StringComparison.Ordinal);

    /// <summary>Order effects run only while standing orders are on; a target that refuses one is not asked again.</summary>
    public static bool IsOrder(string operation) => operation is OrderFire or OrderWatch or OrderRelease or OrderFailed;

    /// <summary>Attention effects are advisory: a target that refuses one is not asked again.</summary>
    public static bool IsAttention(string operation) => operation is AttentionRaise or AttentionClose or AttentionRoute;
}

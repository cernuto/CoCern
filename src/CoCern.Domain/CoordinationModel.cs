using System.Text.Json.Nodes;
using CoCern.Contracts;
namespace CoCern.Domain;

/// <summary>An immutable read candidate, never authorization; the board must recheck and reserve its exact terms.</summary>
public sealed record StandingPresentation(string BoardId, string ProposalId, string SummaryJson)
{
    public static StandingPresentation? ReadPending(JsonObject standing, string boardId, string proposalId, string principalId, string sessionId, string requestCorrelation)
    {
        var summary = StandingPreferences.PendingPresentation(standing, boardId, proposalId, principalId, sessionId, requestCorrelation);
        return summary is null ? null : new(boardId, proposalId, summary.ToJsonString());
    }
}

/// <summary>An authenticated principal. <see cref="IsInternal"/> marks calls the hub itself composes; only those may run "__" operations.</summary>
public sealed record RelayCaller(string PrincipalId, bool IsAdministrator, string[] Repositories, bool IsInternal = false)
{
    public bool MayAccess(string repository) =>
        repository.Length > 0 && (IsAdministrator || Repositories.Contains(repository, StringComparer.Ordinal));
}

/// <summary>Every operation's outcome. <see cref="DataJson"/> is the structured payload; <see cref="Version"/> is the record version it observed or produced.</summary>
public sealed record RelayReply(bool Ok, string Code, string DataJson, long Version = 0);

/// <summary>An operation another aggregate must apply. Persisted with its source until delivered (transactional outbox).</summary>
/// <summary>
/// One queued effect for another aggregate, removed only after its target accepted it. <paramref name="MalformedReported"/> records, with
/// the effect, that its target answered <c>MalformedEffect</c> and the hub logged and counted that once, so a retried state transfer is
/// not logged again after its grain reloads.
/// </summary>
public sealed record DurableEffect(string Id, string TargetKind, string TargetKey, string Operation, string PayloadJson, DateTimeOffset DueAt, bool MalformedReported = false);

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

/// <summary>
/// A finished task leaving its board for the archive: the task's JSON as it left, when it completed and when it was archived. Storage writes
/// it in the board's own commit (<see cref="IHasTaskArchive"/>), so a task is never lost or kept twice.
/// </summary>
public sealed record ArchivedTask(string RepositoryId, string TaskId, DateTimeOffset CompletedAt, DateTimeOffset ArchivedAt, string Json);

/// <summary>State whose commit also moves tasks to the archive. Storage writes <see cref="PendingArchive"/> in the same transaction.</summary>
public interface IHasTaskArchive
{
    IReadOnlyList<ArchivedTask> PendingArchive();
    /// <summary>Called once the commit that wrote them succeeded.</summary>
    void ArchiveWritten();
}

/// <summary>An aggregate's persisted state plus its pending outbox and timers, committed atomically.</summary>
public sealed class StateEnvelope<T> : IHasDueWork, IHasSearchDocuments, IHasTaskArchive where T : new()
{
    // A private field, so no serializer persists it: it lives only from the state machine to the commit that writes it.
    private List<ArchivedTask> pendingArchive = [];
    public IReadOnlyList<ArchivedTask> PendingArchive() => pendingArchive;
    public void ArchiveWritten() => pendingArchive = [];
    internal void QueueArchive(IEnumerable<ArchivedTask> tasks) => pendingArchive.AddRange(tasks);

    public int SchemaVersion { get; set; } = 1;
    public T Value { get; set; } = new();
    public List<DurableEffect> Effects { get; set; } = [];
    public List<DueWorkItem> DueWork { get; set; } = [];
    public IReadOnlyList<DueWorkItem> GetDueWork() => DueWork.Concat(Effects.Select(e => new DueWorkItem(e.Id, DueWorkKinds.Effect, e.DueAt))).ToArray();
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

    public const string DefaultBoard = "default";

    /// <summary>A board grain's key: the escaped repository and board IDs, so the first <c>/</c> always separates them.</summary>
    public static string BoardKey(string repositoryId, string boardId) => Uri.EscapeDataString(repositoryId) + "/" + Uri.EscapeDataString(boardId);

    /// <summary>The board ID in a board grain key, or null when it is not one.</summary>
    public static string? BoardOf(string key) => key.IndexOf('/') is var slash and >= 0 && key.IndexOf('/', slash + 1) < 0 ? Uri.UnescapeDataString(key[(slash + 1)..]) : null;
}

/// <summary>Session roles the hub acts on. A role is given at <c>session_open</c>, compared ignoring case, and grants nothing.</summary>
public static class SessionRoles
{
    /// <summary>Sessions that receive manager-bound attention items; a manager session also starts its repository's anomaly monitor.</summary>
    public const string Manager = "manager";

    /// <summary>
    /// The hub's own identity for proposed team rules. Only the internal <see cref="Operations.StandingProposeHub"/> proposes under it,
    /// so no client can, whatever principal or role it claims.
    /// </summary>
    public const string HubProposer = "cocern-hub";
}

/// <summary>Kinds of an aggregate's own timers (due work), besides lease expiry.</summary>
public static class DueWorkKinds
{
    /// <summary>A lease's end: the aggregate's own, or one task's (ID <c>lease:&lt;taskId&gt;</c>).</summary>
    public const string Lease = "lease";

    /// <summary>An outbox effect waiting for delivery.</summary>
    public const string Effect = "effect";

    /// <summary>A lock's check, shortly before its lease ends, for an action still in flight (raises <c>claim_expiring</c>).</summary>
    public const string LeaseWarning = "lease_warning";

    /// <summary>The anomaly monitor's next check, rebuilt from its state's <c>nextCheckAt</c>.</summary>
    public const string AnomalyCheck = "anomaly_check";

    /// <summary>A board's next archive sweep: when its oldest finished, idle task passes <c>Relay:Board:ArchiveAfterDays</c>.</summary>
    public const string TaskArchive = "task_archive";

    /// <summary>The ID of a <see cref="TaskArchive"/> sweep pushed back after the board refused one; rescheduling keeps the backoff.</summary>
    public const string TaskArchiveRetry = "task_archive:retry";
}

/// <summary>Hub-internal operations. They compose public tools across aggregates and are never accepted from clients.</summary>
public static class Operations
{
    /// <summary>Initializes a session from its record (<see cref="Messages.SessionOpen"/>).</summary>
    public const string Open = "__open";
    /// <summary>The hub checks the session handle a call presents, and reads the session's record (<see cref="Messages.SessionVerify"/>).</summary>
    public const string Verify = "__verify";
    /// <summary>A session gives the control directory its presence after <c>session_bind</c> (<see cref="Messages.SessionRegistration"/>).</summary>
    public const string Register = "__register";
    /// <summary>A session tells the control directory it is still active, at most hourly (<see cref="Messages.SessionActivity"/>).</summary>
    public const string SessionActive = "__session_active";
    /// <summary>A message for a session's inbox: a direct message, or a notice from a board or a session (<see cref="Messages.MessageDelivery"/>).</summary>
    public const string Deliver = "__deliver";
    public const string Resolve = "__resolve";
    /// <summary>Read-only: resolves several resource names in one control call.</summary>
    public const string ResolveAll = "__resolve_all";
    public const string Block = "__block";
    public const string Unblock = "__unblock";
    public const string Dispatch = "__dispatch";
    /// <summary>The hub rolls back a task claim whose resources could not all be acquired (<see cref="Messages.TaskUnclaim"/>).</summary>
    public const string TaskUnclaim = "__task_unclaim";
    /// <summary>Read-only: the next task a session could claim, as a next-work offer (<see cref="Messages.NextWorkRead"/>).</summary>
    public const string NextWork = "__next_work";
    /// <summary>Read-only: whether another session holds a resource now, so a next-work offer can prefer tasks whose resources are free.</summary>
    public const string ClaimState = "__claim_state";
    /// <summary>Read-only: a board's standing lines, active grants and settings in the compact form sent to chats (<see cref="Messages.StandingRead"/>).</summary>
    public const string Standing = "__standing";
    /// <summary>
    /// Records the standing revision a session received for a board, so later replies carry standing only when it changed
    /// (<see cref="Messages.StandingSeen"/>).
    /// </summary>
    public const string StandingSeen = "__standing_seen";
    /// <summary>
    /// The hub's own standing proposal for a feedback rule that fires across many sessions. Internal only, so no client can propose as the
    /// hub; it is outside the per-session proposal cap (<see cref="Messages.HubStandingProposal"/>).
    /// </summary>
    public const string StandingProposeHub = "__standing_propose_hub";
    /// <summary>
    /// Sent by the control aggregate to a session it dropped from its directory (long stale, directory full). The session then answers
    /// <c>SessionUnknown</c> with <c>expired</c>, and returns deliveries to their senders. The effect carries its own caller, scoped to the
    /// session's repository. A session that answers for an unresolved action refuses it, and so does one with unread mail when the
    /// expiry is an idle close (<c>idle: true</c>); either sends <see cref="SessionRestore"/> instead. Otherwise it closes and sends
    /// <see cref="SessionExpired"/>. Both answers carry the close's <c>closeSeq</c>, which the directory uses to count only the answer to
    /// its latest close of that session, once.
    /// </summary>
    public const string SessionExpire = "__session_expire";
    /// <summary>
    /// Sent by a session that refused <see cref="SessionExpire"/> to the control directory, which had already dropped it: its record comes
    /// back as the session keeps it, with the work it answers for (<c>owns</c>) or the unread mail that exempts it from idle closes (<c>mail</c>).
    /// </summary>
    public const string SessionRestore = "__session_restore";
    /// <summary>Sent by a session that accepted <see cref="SessionExpire"/> to the control directory: that close is settled.</summary>
    public const string SessionExpired = "__session_expired";
    /// <summary>
    /// Sent by a board to the session one of its tasks now targets (<c>workflow.targetSessionId</c>): tell this board when you close. A
    /// session already closed answers at once with <see cref="SessionClosed"/> (<see cref="Messages.SessionTargetWatch"/>).
    /// </summary>
    public const string SessionTargetWatch = "__session_target_watch";
    /// <summary>
    /// Sent by a closed session to each board watching it or holding work it answered for (and by an expired session that hears late of
    /// new work): tasks that still target it lose the target, so any session may claim them, and its unresolved actions there are marked
    /// <c>ownerClosed</c> with their task's claim lapsed, so they can be recovered at once.
    /// </summary>
    public const string SessionClosed = "__session_closed";
    /// <summary>
    /// A board tells a session it starts answering for an unresolved action (<c>open: true</c>: it began or recovered it) or stops
    /// (<see cref="Messages.ActionWorkReport"/>); the session then tells the control directory when it starts or stops owning any
    /// (<see cref="SessionOwns"/>).
    /// </summary>
    public const string SessionWork = "__session_work";
    /// <summary>
    /// A session tells the control directory it now answers for unresolved work, or no longer does (<see cref="Messages.SessionOwnership"/>).
    /// The directory never expires such a session.
    /// </summary>
    public const string SessionOwns = "__session_owns";
    /// <summary>
    /// The hub's periodic sweep, sent to the control directory: closes sessions in every repository not seen for <c>idleHours</c>, as the
    /// full-directory cleanup does (each is sent <see cref="SessionExpire"/>); one that answers for an unresolved action is kept, and so is
    /// one holding unread mail.
    /// </summary>
    public const string SweepSessions = "__sweep_sessions";
    /// <summary>Server-set on <c>task_claim</c> and <c>action_begin</c>: ask the board for the declared resources (<c>DeclaredResourcesNeeded</c>) when the task has any.</summary>
    public const string ResolveDeclaredField = "resolveDeclared";
    /// <summary>Server-set on <c>action_begin</c>: the first declared resource whose lock this session does not hold (<c>code</c>, <c>resourceId</c>).</summary>
    public const string LockRefusalField = "lockRefusal";
    /// <summary>Server-set on a rolled-back claim's lock release: the grant's requestId, whose stored reply is dropped so a retry acquires again.</summary>
    public const string ForgetRequestField = "forgetRequestId";

    /// <summary>Server-set on a rolled-back claim's re-acquire of a grant it already held: the claim that grant served before.</summary>
    public const string RestoreClaimForField = "restoreClaimFor";
    /// <summary>A board's due sweep: moves tasks finished longer than <c>Relay:Board:ArchiveAfterDays</c> to the archive (<see cref="Messages.TaskArchiveSweep"/>).</summary>
    public const string TaskArchive = "__task_archive";
    /// <summary>Read-only: which of the given sessions in the repository the caller's principal opened.</summary>
    public const string SessionOwners = "__session_owners";
    /// <summary>Read-only, sent to the control directory: which of the given session IDs are open in this repository, whoever opened them.</summary>
    public const string SessionsOpen = "__sessions_open";
    /// <summary>
    /// Adds an attention item to a session, or refreshes the open one with the same kind, task and subject. Sent through an aggregate's
    /// outbox (<see cref="Messages.AttentionRaise"/>). Sources should always send <c>revision</c>; without one, only an identical copy within
    /// a few minutes of a close is ignored.
    /// </summary>
    public const string AttentionRaise = "__attention_raise";
    /// <summary>
    /// The hub's own item for the session whose call it serves (offered work, related work, a tip), called directly after the reply
    /// (<see cref="Messages.AttentionSuggestion"/>). Applied as a raise.
    /// </summary>
    public const string AttentionSuggest = "__attention_suggest";
    /// <summary>
    /// Closes a session's attention items for a task (optionally only some kinds) when their condition ends. Sent through an aggregate's
    /// outbox (<see cref="Messages.AttentionClose"/>).
    /// </summary>
    public const string AttentionClose = "__attention_close";
    /// <summary>
    /// The hub tells a session that claimed a task to close its <c>next_work</c> items for it, called directly after the claim
    /// (<see cref="Messages.AttentionClaimed"/>).
    /// </summary>
    public const string AttentionClaimed = "__attention_claimed";
    /// <summary>
    /// One boundary: an unanswered shown item counts as later, snoozes count down, the best item is picked and marked shown
    /// (<see cref="Messages.AttentionOffer"/>).
    /// </summary>
    public const string AttentionOffer = "__attention_offer";
    /// <summary>Reserve one immutable pending Standing proposal for a verified human-active boundary; never grants authority.</summary>
    public const string PresentStanding = "__present_standing";
    /// <summary>Closes the info items of messages an adapter reader showed the model without an offer (<see cref="Messages.AttentionSeen"/>).</summary>
    public const string AttentionSeen = "__attention_seen";
    /// <summary>
    /// Sent to the control aggregate through the outbox: raises or closes an attention item at every session of one role (such as manager)
    /// in the repository, found in the session directory by the role given at <c>session_open</c> (<see cref="Messages.AttentionRoute"/>).
    /// </summary>
    public const string AttentionRoute = "__attention_route";
    /// <summary>A lock's due-work check: when its lease nears expiry with an action in flight, the holder gets a <c>claim_expiring</c> item.</summary>
    public const string LeaseCheck = "__lease_check";
    /// <summary>Starts a repository's anomaly monitor: its first check is due now unless one is already scheduled.</summary>
    public const string AnomalyStart = "__anomaly_start";
    /// <summary>The anomaly monitor's due check begins: the next one is scheduled (<see cref="Messages.AnomalySchedule"/>).</summary>
    public const string AnomalySchedule = "__anomaly_schedule";
    /// <summary>The findings the hub read from the analytics snapshot for one check (<see cref="Messages.AnomalyCheck"/>).</summary>
    public const string AnomalyCheck = "__anomaly_check";

    /// <summary>Read-only: a board's standing orders in the compact form <c>get_status</c> lists (<see cref="Messages.OrdersRead"/>).</summary>
    public const string Orders = "__orders";
    /// <summary>Sent by a lock to each board watching it when its claim is released: runs the board's <c>claim_released</c> order (<see cref="Messages.OrderFire"/>).</summary>
    public const string OrderFire = "__order_fire";
    /// <summary>Sent by a board to a lock: starts or stops telling that board when the lock's claim is released (<see cref="Messages.OrderWatch"/>).</summary>
    public const string OrderWatch = "__order_watch";
    /// <summary>Sent by a board to a lock: an order releases the claim its task's owner holds, once per order run (<see cref="Messages.OrderRelease"/>).</summary>
    public const string OrderRelease = "__order_release";
    /// <summary>
    /// An order's run failed: sent by a lock back to the board when the order's release meets an unresolved action, and applied by the hub
    /// when a session refused an item the order raised (<see cref="Messages.OrderFailed"/>).
    /// </summary>
    public const string OrderFailed = "__order_failed";
    /// <summary>Sent by a lock back to the board: it refused to watch for an order, being watched by too many boards (<see cref="Messages.OrderWatchFailed"/>).</summary>
    public const string OrderWatchFailed = "__order_watch_failed";

    /// <summary>Fields of a standing order's attention raise naming its order and run; the session ignores them.</summary>
    public const string OrderIdField = "orderId";
    public const string OrderRunField = "orderRun";

    public static bool IsInternal(string operation) => operation.StartsWith("__", StringComparison.Ordinal);

    /// <summary>Internal operations that only read: never deduplicated by requestId, never committed for themselves.</summary>
    public static bool IsRead(string operation) => operation is Verify or Resolve or ResolveAll or NextWork or ClaimState or SessionOwners or SessionsOpen or Standing or Orders;

    /// <summary>Order effects run only while standing orders are on; a target that refuses one is not asked again.</summary>
    public static bool IsOrder(string operation) => operation is OrderFire or OrderWatch or OrderRelease or OrderFailed or OrderWatchFailed;

    /// <summary>Attention effects are advisory: a target that refuses one is not asked again.</summary>
    public static bool IsAttention(string operation) => operation is AttentionRaise or AttentionClose or AttentionRoute;
}

/// <summary>
/// Arguments only the hub sets, per tool: values it derives from its own state or settings. A client's copy is dropped on entry
/// (<see cref="StripClientValues"/>), and none of them counts toward a request's replay fingerprint, since a retry may get different ones.
/// </summary>
public static class ServerArguments
{
    /// <summary>The <c>resourceIds</c> a <c>task_claim</c>'s client named, before the hub made them canonical; the fingerprint uses these.</summary>
    public const string RequestedResources = "requestedResourceIds";

    private static readonly Dictionary<string, string[]> ByTool = new(StringComparer.Ordinal)
    {
        [RelayTools.TaskUpdate] = ["archivedDependsOn"],
        [RelayTools.Ack] = ["heldOnly"],
        [RelayTools.TaskClaim] = ["taskClaimGeneration", "declaredResources", Operations.ResolveDeclaredField, Operations.LockRefusalField, Operations.RestoreClaimForField, Operations.ForgetRequestField, RequestedResources],
        [RelayTools.ActionBegin] = ["binding", "bindingVersion", "declaredResources", Operations.ResolveDeclaredField, Operations.LockRefusalField, Operations.RestoreClaimForField, Operations.ForgetRequestField, RequestedResources],
        [RelayTools.ClaimAcquire] = ["taskClaimGeneration", "declaredResources", Operations.ForgetRequestField, Operations.RestoreClaimForField],
        [RelayTools.ClaimRenew] = ["taskClaimGeneration", Operations.ForgetRequestField, Operations.RestoreClaimForField],
        [RelayTools.ClaimRelease] = ["taskClaimGeneration", Operations.ForgetRequestField, Operations.RestoreClaimForField],
        [RelayTools.OrderPut] = ["defaultExpiryDays"],
        [RelayTools.AttentionRespond] = ["snoozeBoundaries"]
    };

    public static IReadOnlyList<string> For(string tool) => ByTool.TryGetValue(tool, out var fields) ? fields : [];

    /// <summary>Drops any value a client sent for a field only the hub sets.</summary>
    public static void StripClientValues(string tool, JsonObject args)
    {
        foreach (var field in For(tool)) args.Remove(field);
    }
}


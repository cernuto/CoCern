using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>
/// One session's stored document, read and written through named, typed members only. It holds the session's record under the names
/// <see cref="SessionRecord"/> gives its fields (never its activity time, which is the directory's), when it opened and when it closed,
/// when it last reported activity, its inbox and the messages it acknowledged, the standing revision it last received per board, the
/// boards waiting on its close, the work it answers for (<see cref="WorkLedger"/>) and its last report of that, and its attention queue
/// (<see cref="AttentionQueue"/>). Every key it stores is declared in <see cref="Shape"/>.
/// </summary>
public sealed class SessionState(JsonObject json)
{
    public const string SessionIdKey = SessionRecord.SessionIdKey;
    public const string PrincipalIdKey = SessionRecord.PrincipalIdKey;
    public const string SessionTokenKey = SessionRecord.SessionTokenKey;
    public const string RoleKey = SessionRecord.RoleKey;
    public const string HubIncarnationKey = SessionPresence.HubIncarnationKey;
    public const string MachineIdKey = SessionPresence.MachineIdKey;
    public const string LabelKey = SessionPresence.LabelKey;
    public const string BindingKey = SessionPresence.BindingKey;
    public const string BindingVersionKey = SessionPresence.BindingVersionKey;
    public const string ObservedAtKey = SessionPresence.ObservedAtKey;
    public const string FreshUntilKey = SessionPresence.FreshUntilKey;
    public const string AvailabilityKey = SessionPresence.AvailabilityKey;

    public const string OpenedAtKey = "openedAt";
    public const string ClosedAtKey = ReadModels.ClosedAtKey;
    public const string ForceClosedByKey = "forceClosedBy";
    public const string ActiveReportedAtKey = "activeReportedAt";
    public const string InboxKey = "inbox";
    public const string AckedKey = "acked";
    public const string StandingSeenKey = "standingSeen";
    public const string TargetBoardsKey = "targetBoards";
    public const string WorkBoardsKey = "workBoards";
    public const string WorkKey = "work";
    public const string WorkReportedKey = "workReported";

    /// <summary>
    /// Every key a session stores. Its tool binding is the client's own, as it reported it; an inbox message holds the keys
    /// <see cref="InboxMessage"/> writes; an acknowledgment is the time it was made, and a standing revision a number.
    /// </summary>
    public static StateShape Shape { get; } = CommonState.Shape.With(
        (SessionIdKey, null), (PrincipalIdKey, null), (SessionTokenKey, null), (RoleKey, null), (HubIncarnationKey, null), (MachineIdKey, null), (LabelKey, null),
        (BindingKey, StateShape.Any), (BindingVersionKey, null), (ObservedAtKey, null), (FreshUntilKey, null), (AvailabilityKey, null),
        (NativeParticipant.Id, null), (NativeParticipant.Generation, null), (NativeParticipant.Request, null),
        (OpenedAtKey, null), (ClosedAtKey, null), (ForceClosedByKey, null), (ActiveReportedAtKey, null),
        (InboxKey, StateShape.Map(StateShape.Object(InboxMessage.Keys.Select(key => (key, (StateShape?)null)).ToArray()))), (AckedKey, StateShape.Map(null)),
        (StandingSeenKey, StateShape.Map(null)), (TargetBoardsKey, StateShape.Map(BoardWatches.EntryShape)), (WorkBoardsKey, StateShape.Map(BoardWatches.EntryShape)),
        (WorkKey, WorkLedger.Shape), (WorkReportedKey, WorkReport.Shape)).With(AttentionQueue.Fields);

    /// <summary>What a closed session forgets: its secret, its mail and attention, its tool binding, and the boards waiting on it (told as it closes).</summary>
    private static readonly string[] ForgottenOnClose =
    [
        SessionTokenKey, InboxKey, AckedKey, AttentionQueue.ItemsKey, AttentionQueue.ClosedKey, AttentionQueue.ArchivedKey, AttentionQueue.RecentKey, StandingSeenKey,
        BindingKey, TargetBoardsKey, WorkBoardsKey
    ];

    /// <summary>Whether a <see cref="SessionOpen"/> ever initialized this session.</summary>
    public bool Opened => json.ContainsKey(PrincipalIdKey);

    /// <summary>Whether the directory closed it (<see cref="ReadModels.IsClosed(JsonObject)"/>).</summary>
    public bool Closed => ReadModels.IsClosed(json);

    public string SessionId => Text(json, SessionIdKey);
    public string NativeId => Text(json, NativeParticipant.Id);
    public string PrincipalId => Text(json, PrincipalIdKey);
    public string SessionToken => Text(json, SessionTokenKey);

    /// <summary>This open session's own copy of its record. It always reads: only a decoded <see cref="SessionOpen"/> opens a session.</summary>
    public SessionRecord Record => SessionRecord.Decode(json).TryGet(out var record, out var malformed)
        ? record : throw new InvalidOperationException("The session's own record lacks " + string.Join(", ", malformed) + ".");

    /// <summary>Opens the session from <paramref name="record"/>: its fields, never its activity time, and when it opened.</summary>
    public void Open(SessionRecord record, DateTimeOffset now)
    {
        foreach (var (field, value) in (record with { ActiveAt = null }).ToJson()) json[field] = value?.DeepClone();
        json[OpenedAtKey] = now.ToString("O");
    }

    /// <summary>The role given at <c>session_open</c>; none when it gave none.</summary>
    public string? Role
    {
        get => json[RoleKey] is null ? null : Text(json, RoleKey);
        set { if (value is null) json.Remove(RoleKey); else json[RoleKey] = value; }
    }

    public string HubIncarnation
    {
        get => Text(json, HubIncarnationKey);
        set => json[HubIncarnationKey] = value;
    }

    /// <summary>The machine the session's chat runs on; empty until the client reports one.</summary>
    public string MachineId
    {
        get => Text(json, MachineIdKey);
        set => json[MachineIdKey] = value;
    }

    public string Label
    {
        get => Text(json, LabelKey);
        set => json[LabelKey] = value;
    }

    /// <summary>The tool binding the client reported, as it gave it; none until it reports one.</summary>
    public JsonNode? Binding
    {
        get => json[BindingKey];
        set => json[BindingKey] = value?.DeepClone();
    }

    /// <summary>The binding's version, which each <c>session_bind</c> raises by one; none until the session opened.</summary>
    public long? BindingVersion
    {
        get => json[BindingVersionKey] is null ? null : Number(json, BindingVersionKey);
        set => json[BindingVersionKey] = value;
    }

    /// <summary>When the client last reported its binding, and until when that report counts as fresh.</summary>
    public DateTimeOffset? ObservedAt
    {
        get => OptionalDate(json, ObservedAtKey);
        set => json[ObservedAtKey] = value?.ToString("O");
    }

    public DateTimeOffset? FreshUntil
    {
        get => OptionalDate(json, FreshUntilKey);
        set => json[FreshUntilKey] = value?.ToString("O");
    }

    /// <summary>What the client reported of its availability; <c>unknown</c> until it reports.</summary>
    public string? Availability
    {
        get => json[AvailabilityKey] is null ? null : Text(json, AvailabilityKey);
        set => json[AvailabilityKey] = value;
    }

    /// <summary>When the directory closed the session; a closed session keeps its record and this.</summary>
    public DateTimeOffset? ClosedAt
    {
        get => OptionalDate(json, ClosedAtKey);
        set => json[ClosedAtKey] = value?.ToString("O");
    }

    /// <summary>The administrator who forced the close, when one did.</summary>
    public string? ForceClosedBy
    {
        get => json[ForceClosedByKey] is null ? null : Text(json, ForceClosedByKey);
        set => json[ForceClosedByKey] = value;
    }

    /// <summary>When the session last told the directory it was active (<see cref="SessionActivity"/>).</summary>
    public DateTimeOffset? ActiveReportedAt
    {
        get => OptionalDate(json, ActiveReportedAtKey);
        set => json[ActiveReportedAtKey] = value?.ToString("O");
    }

    /// <summary>Forgets what a closed session no longer keeps (<see cref="ForgottenOnClose"/>).</summary>
    public void Forget()
    {
        foreach (var key in ForgottenOnClose) json.Remove(key);
    }

    /// <summary>The unread messages by ID, created empty when there are none.</summary>
    public Inbox Inbox => new(ObjectAt(json, InboxKey));

    /// <summary>The number of unread messages; reading it never creates the inbox.</summary>
    public int UnreadCount => (json[InboxKey] as JsonObject)?.Count ?? 0;

    /// <summary>The unread messages as stored, oldest first; reading them never creates the inbox.</summary>
    public IEnumerable<JsonObject> Unread => (json[InboxKey] as JsonObject ?? []).Select(entry => entry.Value).OfType<JsonObject>();

    /// <summary>The messages this session acknowledged, created empty when there are none.</summary>
    public Acknowledgments Acked => new(ObjectAt(json, AckedKey));

    /// <summary>The standing revision this session last received per board, created empty when there is none.</summary>
    public StandingRevisions StandingSeen => new(ObjectAt(json, StandingSeenKey));

    /// <summary>A copy of those revisions for the hub's verification, or null when this session has received no standing.</summary>
    public JsonObject? StandingSeenCopy => (json[StandingSeenKey] as JsonObject)?.DeepClone().AsObject();

    /// <summary>The boards with a task targeting this session, to tell when it closes; created empty when there are none.</summary>
    public BoardWatches TargetBoards => new(ObjectAt(json, TargetBoardsKey));

    /// <summary>The boards holding work this session answers for, to tell when a forced close takes it; created empty when there are none.</summary>
    public BoardWatches WorkBoards => new(ObjectAt(json, WorkBoardsKey));

    /// <summary>
    /// The boards to tell as this session closes: those whose tasks target it and, <paramref name="withWork"/>, those holding work it
    /// answers for. Reading them never creates either list.
    /// </summary>
    public IEnumerable<(string BoardKey, string BoardId)> BoardsToTell(bool withWork) =>
        new[] { json[TargetBoardsKey], withWork ? json[WorkBoardsKey] : null }.OfType<JsonObject>().SelectMany(boards => new BoardWatches(boards).Boards);

    /// <summary>The unresolved actions this session answers for, created empty when there are none.</summary>
    internal WorkLedger Work => new(ObjectAt(json, WorkKey));

    /// <summary>Whether this session answers for an unresolved action reported within the retention window; reading never creates the ledger.</summary>
    internal bool OwnsWork(DateTimeOffset now) => json[WorkKey] is JsonObject ledger && new WorkLedger(ledger).Owns(now);

    /// <summary>Drops the work ledger once it holds nothing.</summary>
    internal void DropEmptyWork()
    {
        if (json[WorkKey] is JsonObject ledger && new WorkLedger(ledger).IsEmpty) json.Remove(WorkKey);
    }

    /// <summary>The session's last report to the directory of whether it owns work (<see cref="SessionOwnership"/>); null before the first.</summary>
    public WorkReport? WorkReported => json[WorkReportedKey] is JsonObject report ? new(report) : null;

    /// <summary>Records the report this session just sent the directory.</summary>
    public void ReportedWork(bool owns, DateTimeOffset at, long seq) =>
        json[WorkReportedKey] = new JsonObject { [WorkReport.OwnsKey] = owns, [WorkReport.AtKey] = at.ToString("O"), [WorkReport.SeqKey] = seq };

    /// <summary>The session's attention queue, kept beside its inbox at the top of its document.</summary>
    public AttentionQueue Attention => new(json);
}

/// <summary>A session's unread messages by ID, each as <see cref="InboxMessage"/> writes it.</summary>
public sealed class Inbox(JsonObject messages)
{
    public int Count => messages.Count;

    public bool Holds(string messageId) => messages.ContainsKey(messageId);

    public void Add(InboxMessage message) => messages[message.MessageId] = message.ToJson();

    public bool Remove(string messageId) => messages.Remove(messageId);

    /// <summary>The IDs of <paramref name="senderSessionId"/>'s unread superseding messages.</summary>
    public IReadOnlyList<string> SupersedingFrom(string senderSessionId) => messages
        .Where(entry => entry.Value is JsonObject message && IsTrue(message, InboxMessage.SupersedeKey) && Text(message, InboxMessage.SenderSessionIdKey) == senderSessionId)
        .Select(entry => entry.Key).ToArray();

    /// <summary>Every message as stored, oldest first, for a <c>check_inbox</c> page.</summary>
    public IEnumerable<JsonNode> Stored => messages.Select(entry => entry.Value!);
}

/// <summary>
/// The messages a session acknowledged, each with when it was, so a later duplicate delivery is ignored. Each is kept for the retry window
/// (<see cref="RelayLimits.RequestReplayRetentionHours"/>), and a full set drops its oldest (<see cref="Retention"/>).
/// </summary>
public sealed class Acknowledgments(JsonObject acked)
{
    public bool Holds(string messageId) => acked.ContainsKey(messageId);

    /// <summary>Records <paramref name="messageId"/> as acknowledged at <paramref name="now"/>, making room first for a new one.</summary>
    public void Add(string messageId, DateTimeOffset now)
    {
        if (!acked.ContainsKey(messageId)) Retention.MakeRoom(acked, now);
        acked[messageId] = Retention.Stamp(now);
    }
}

/// <summary>The standing revision a session last received, per board ID.</summary>
public sealed class StandingRevisions(JsonObject seen)
{
    public int Count => seen.Count;

    public bool Holds(string boardId) => seen.ContainsKey(boardId);

    public long Of(string boardId) => Number(seen, boardId);

    public void Set(string boardId, long revision) => seen[boardId] = revision;
}

/// <summary>Boards waiting on a session's close, by board grain key, each with its board ID; newest last.</summary>
public sealed class BoardWatches(JsonObject boards)
{
    public const string BoardIdKey = "boardId";

    public static StateShape EntryShape { get; } = StateShape.Object((BoardIdKey, null));

    /// <summary>Remembers a board as the newest; past <see cref="RelayLimits.MaxSessionTargetBoards"/> the oldest is forgotten.</summary>
    public void Remember(string boardKey, string boardId)
    {
        boards.Remove(boardKey);
        boards[boardKey] = new JsonObject { [BoardIdKey] = boardId };
        while (boards.Count > RelayLimits.MaxSessionTargetBoards) boards.Remove(boards.First().Key);
    }

    public IEnumerable<(string BoardKey, string BoardId)> Boards =>
        boards.Select(entry => (entry.Key, Text(entry.Value as JsonObject ?? new JsonObject(), BoardIdKey, AggregateKinds.DefaultBoard)));
}

/// <summary>A session's last report to the directory of whether it owns work: what it said, when, and the report's number.</summary>
public sealed class WorkReport(JsonObject json)
{
    public const string OwnsKey = "owns";
    public const string AtKey = "at";
    public const string SeqKey = "seq";

    public static StateShape Shape { get; } = StateShape.Object((OwnsKey, null), (AtKey, null), (SeqKey, null));

    public bool Owns => Flag(json, OwnsKey);
    public DateTimeOffset At => Date(json, AtKey);
    public long Seq => Number(json, SeqKey);
}

/// <summary>
/// A session's attention queue, kept at the top of its document beside the inbox, so items commit atomically with the change that raised
/// them: the open items by ID (<see cref="QueuedItem"/>), the counter that names new ones, the newest source revision each closed key had
/// and each archived task (so a late raise cannot reopen one), the content of items closed in the last minutes (<see cref="RecentClose"/>,
/// so a raise delivered twice cannot reopen one), and when the chat's last boundary reply was.
/// </summary>
public sealed class AttentionQueue(JsonObject session)
{
    public const string ItemsKey = "attention";
    public const string SeqKey = "attentionSeq";
    public const string ClosedKey = "attentionClosed";
    public const string ArchivedKey = "attentionArchived";
    public const string RecentKey = "attentionRecent";
    public const string BoundaryAtKey = "attentionBoundaryAt";

    /// <summary>The keys the queue keeps in its session's document, each with its shape.</summary>
    public static (string Key, StateShape? Shape)[] Fields { get; } =
    [
        (ItemsKey, StateShape.Map(QueuedItem.Shape)), (SeqKey, null), (ClosedKey, StateShape.Map(null)), (ArchivedKey, StateShape.Map(null)),
        (RecentKey, StateShape.Map(RecentClose.Shape)), (BoundaryAtKey, null)
    ];

    /// <summary>The open items by ID, created empty when there are none.</summary>
    public JsonObject Items => ObjectAt(session, ItemsKey);

    /// <summary>The open items by ID, or null when there are none; reading them never creates the queue.</summary>
    public JsonObject? StoredItems => session[ItemsKey] as JsonObject;

    /// <summary>The number of the last item created; the next is <c>a-</c> and one more.</summary>
    public long Seq
    {
        get => Number(session, SeqKey);
        set => session[SeqKey] = value;
    }

    /// <summary>When the chat last had a boundary reply; an idle chat is one without one for a while.</summary>
    public DateTimeOffset? BoundaryAt
    {
        get => OptionalDate(session, BoundaryAtKey);
        set => session[BoundaryAtKey] = value?.ToString("O");
    }

    /// <summary>The newest source revision <paramref name="key"/> had when closed, or 0. Reading it creates the guard list.</summary>
    public long ClosedRevision(string key) => Number(ObjectAt(session, ClosedKey), key);

    /// <summary>Remembers the newest source revision a closed key had, newest last, bounded by <see cref="RelayLimits.MaxAttentionClosedKeys"/>.</summary>
    public void RememberClosed(string key, long revision)
    {
        if (revision <= 0) return;
        Remember(ObjectAt(session, ClosedKey), key, revision, RelayLimits.MaxAttentionClosedKeys);
    }

    /// <summary>The revision <paramref name="taskId"/> was archived at, or 0. Reading it creates the archive list.</summary>
    public long ArchivedRevision(string boardId, string taskId) => Number(ObjectAt(session, ArchivedKey), ArchivedTask(boardId, taskId));

    /// <summary>Remembers an archived task's revision, newest last, bounded by <see cref="RelayLimits.MaxAttentionArchivedTasks"/>.</summary>
    public void RememberArchived(string boardId, string taskId, long revision) =>
        Remember(ObjectAt(session, ArchivedKey), ArchivedTask(boardId, taskId), revision, RelayLimits.MaxAttentionArchivedTasks);

    /// <summary>
    /// The items closed within <see cref="RelayLimits.AttentionTombstoneMinutes"/>, by key; older ones are dropped first. Reading them
    /// creates the list.
    /// </summary>
    public JsonObject Recent(DateTimeOffset now)
    {
        var recent = ObjectAt(session, RecentKey);
        foreach (var stale in recent.Where(entry => entry.Value is not JsonObject tombstone || now >= new RecentClose(tombstone).At.AddMinutes(RelayLimits.AttentionTombstoneMinutes))
            .Select(entry => entry.Key).ToArray())
            recent.Remove(stale);
        return recent;
    }

    /// <summary>A task's key in the archive list: its board and task, under no kind or subject.</summary>
    private static string ArchivedTask(string boardId, string taskId) => AttentionScheduler.Key("", boardId, taskId, "");

    private static void Remember(JsonObject guards, string key, long revision, int limit)
    {
        var newest = Math.Max(revision, Number(guards, key));
        guards.Remove(key);
        guards[key] = newest;
        while (guards.Count > limit) guards.Remove(guards.First().Key);
    }
}

/// <summary>
/// One open attention item as its session keeps it: its content as its source raised it (<see cref="ContentKeys"/>), the source's revision,
/// when it was created, last raised and expires, and whether it was shown or is snoozed. <see cref="AttentionItem"/> reads it for the
/// scheduler.
/// </summary>
public sealed class QueuedItem(JsonObject json)
{
    public const string IdKey = "id";
    public const string KindKey = AttentionContent.KindKey;
    public const string TaskIdKey = AttentionContent.TaskIdKey;
    public const string BoardIdKey = AttentionContent.BoardIdKey;
    public const string SubjectKey = AttentionContent.SubjectKey;
    public const string BlockerKey = AttentionContent.BlockerKey;
    public const string BlockingKey = AttentionContent.BlockingKey;
    public const string PriorityKey = AttentionContent.PriorityKey;
    public const string RefsKey = AttentionContent.RefsKey;
    public const string NoteKey = AttentionContent.NoteKey;
    public const string RevisionKey = AttentionContent.RevisionKey;
    public const string CreatedAtKey = "createdAt";
    public const string UpdatedAtKey = "updatedAt";
    public const string ExpiresAtKey = "expiresAt";
    public const string SnoozesKey = "snoozes";
    public const string SnoozeBoundariesKey = "snoozeBoundaries";
    public const string SnoozeUntilKey = "snoozeUntil";
    public const string FirstSnoozedAtKey = "firstSnoozedAt";
    public const string ShownAtKey = "shownAt";

    /// <summary>The fields that make up an item's content; two raises with the same values are the same raise.</summary>
    public static IReadOnlyList<string> ContentKeys { get; } = [KindKey, TaskIdKey, BoardIdKey, SubjectKey, BlockerKey, BlockingKey, PriorityKey, RefsKey, NoteKey];

    public static StateShape Shape { get; } = StateShape.Object(
        ContentKeys.Concat([IdKey, RevisionKey, CreatedAtKey, UpdatedAtKey, ExpiresAtKey, SnoozesKey, SnoozeBoundariesKey, SnoozeUntilKey, FirstSnoozedAtKey, ShownAtKey])
            .Select(key => (key, (StateShape?)null)).ToArray());

    /// <summary>A new item named <paramref name="id"/>, created at <paramref name="now"/>, never snoozed or shown; its content comes next.</summary>
    public static JsonObject New(string id, DateTimeOffset now) => new()
    {
        [IdKey] = id, [CreatedAtKey] = now.ToString("O"), [SnoozesKey] = 0, [SnoozeBoundariesKey] = 0, [SnoozeUntilKey] = null, [ShownAtKey] = null
    };

    public string Kind => Text(json, KindKey);
    public bool Blocking => IsTrue(json, BlockingKey);

    /// <summary>The source's revision the item was last raised at; 0 when it gave none.</summary>
    public long Revision
    {
        get => Number(json, RevisionKey);
        set => json[RevisionKey] = value;
    }

    public DateTimeOffset UpdatedAt { set => json[UpdatedAtKey] = value.ToString("O"); }
    public DateTimeOffset ExpiresAt { set => json[ExpiresAtKey] = value.ToString("O"); }

    /// <summary>How often the item was snoozed.</summary>
    public long Snoozes
    {
        get => Number(json, SnoozesKey);
        set => json[SnoozesKey] = value;
    }

    /// <summary>The boundaries a snoozed item still sits out.</summary>
    public long SnoozeBoundaries { set => json[SnoozeBoundariesKey] = value; }

    /// <summary>When a snooze ends at the latest; stored as null when none does.</summary>
    public DateTimeOffset? SnoozeUntil { set => json[SnoozeUntilKey] = value?.ToString("O"); }

    /// <summary>When a blocking item was first snoozed, which bounds how long its snoozes may last.</summary>
    public DateTimeOffset? FirstSnoozedAt
    {
        get => json.ContainsKey(FirstSnoozedAtKey) ? Date(json, FirstSnoozedAtKey) : null;
        set => json[FirstSnoozedAtKey] = value?.ToString("O");
    }

    /// <summary>Whether an offer showed the item and the agent has not answered it yet.</summary>
    public bool Shown => json[ShownAtKey] is JsonValue;

    /// <summary>When an offer showed the item; stored as null when none has since the last answer.</summary>
    public DateTimeOffset? ShownAt { set => json[ShownAtKey] = value?.ToString("O"); }

    /// <summary>Gives the item <paramref name="content"/>'s fields (<see cref="ContentKeys"/>); a field the content lacks is removed.</summary>
    public void SetContent(JsonObject content)
    {
        foreach (var key in ContentKeys)
            if (content[key] is { } value) json[key] = value.DeepClone(); else json.Remove(key);
    }

    /// <summary>The item's content, every one of <see cref="ContentKeys"/> in order (null when it lacks one), from which its fingerprint is taken.</summary>
    public static JsonObject Content(JsonObject item)
    {
        var content = new JsonObject();
        foreach (var key in ContentKeys) content[key] = item[key]?.DeepClone();
        return content;
    }
}

/// <summary>A closed item's tombstone: when it closed and its content's fingerprint.</summary>
public sealed class RecentClose(JsonObject json)
{
    public const string AtKey = "at";
    public const string FingerprintKey = "fingerprint";

    public static StateShape Shape { get; } = StateShape.Object((AtKey, null), (FingerprintKey, null));

    public static JsonObject New(DateTimeOffset at, string fingerprint) => new() { [AtKey] = at.ToString("O"), [FingerprintKey] = fingerprint };

    public DateTimeOffset At => Date(json, AtKey);
    public string Fingerprint => Text(json, FingerprintKey);
}

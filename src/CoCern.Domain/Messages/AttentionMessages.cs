using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain.Messages;

/// <summary>
/// What a source says about one attention item. Its kind, board, task and subject make the item's key: a later raise with the same key
/// refreshes the open item. The session bounds what it keeps: an identifier longer than the shared bound is refused, a blocker kind or a
/// reference too long is left out, the note is cut, a priority it does not know counts as normal, and the lifetime stays within its limits.
/// </summary>
public sealed record AttentionContent
{
    public const string KindKey = "kind";
    public const string TaskIdKey = "taskId";
    public const string BoardIdKey = "boardId";
    public const string SubjectKey = "subject";
    public const string BlockerKey = "blocker";
    public const string BlockingKey = "blocking";
    public const string PriorityKey = "priority";
    public const string RefsKey = "refs";
    public const string NoteKey = "note";
    public const string RevisionKey = "revision";
    public const string ExpiryDaysKey = "expiryDays";

    /// <summary>One of <see cref="AttentionKinds.All"/>; the session refuses any other.</summary>
    public required string Kind { get; init; }

    public string? TaskId { get; init; }

    /// <summary>The task's board; none is the default board.</summary>
    public string? BoardId { get; init; }

    /// <summary>What the item is about beside its task: a resource, a proposal, an order, a query, or the task a claim found related work for.</summary>
    public string? Subject { get; init; }

    /// <summary>For a <c>blocked</c> item, the kind of its task's most urgent blocker, which sets its tier.</summary>
    public string? Blocker { get; init; }

    /// <summary>Whether the item's task blocks a task another session claimed: it then ranks one tier higher.</summary>
    public bool Blocking { get; init; }

    /// <summary>
    /// A level of <see cref="AttentionWeights.Priorities"/>; a source with a task's numeric priority gives its level
    /// (<see cref="AttentionWeights.PriorityLevel"/>). None is normal.
    /// </summary>
    public string? Priority { get; init; }

    /// <summary>The IDs the item points to, such as the tasks a blocked one holds up.</summary>
    public IReadOnlyList<string>? Refs { get; init; }

    public string? Note { get; init; }

    /// <summary>The source's revision of the item's key: a raise no newer than a close the session applied, or than its open item, is stale.</summary>
    public long? Revision { get; init; }

    /// <summary>Days the item lives. An outbox raise without one is given the hub's item lifetime when it is delivered.</summary>
    public long? ExpiryDays { get; init; }

    internal void WriteTo(JsonObject json)
    {
        json[KindKey] = Kind;
        if (TaskId is not null) json[TaskIdKey] = TaskId;
        if (BoardId is not null) json[BoardIdKey] = BoardId;
        if (Subject is not null) json[SubjectKey] = Subject;
        if (Blocker is not null) json[BlockerKey] = Blocker;
        if (Blocking) json[BlockingKey] = true;
        if (Priority is not null) json[PriorityKey] = Priority;
        if (Refs is not null) json[RefsKey] = Strings(Refs);
        if (Note is not null) json[NoteKey] = Note;
        if (Revision is { } revision) json[RevisionKey] = revision;
        if (ExpiryDays is { } days) json[ExpiryDaysKey] = days;
    }

    internal static AttentionContent Read(MessageReader read) => new()
    {
        Kind = read.Text(KindKey), TaskId = read.OptionalText(TaskIdKey), BoardId = read.OptionalText(BoardIdKey), Subject = read.OptionalText(SubjectKey),
        Blocker = read.OptionalText(BlockerKey), Blocking = read.OptionalFlag(BlockingKey), Priority = ReadPriority(read), Refs = read.OptionalStrings(RefsKey),
        Note = read.OptionalText(NoteKey), Revision = read.OptionalNumber(RevisionKey, minimum: 0), ExpiryDays = read.OptionalNumber(ExpiryDaysKey)
    };

    /// <summary>A level, or a task's numeric priority as sources once sent it, read as its level.</summary>
    private static string? ReadPriority(MessageReader read)
    {
        if (read.Node(PriorityKey) is not JsonValue value) { read.Check(read.Node(PriorityKey) is null, PriorityKey); return null; }
        if (value.TryGetValue<string>(out var level)) return level;
        if (value.GetValueKind() == JsonValueKind.Number) return long.TryParse(value.ToJsonString(), out var number) ? AttentionWeights.PriorityLevel(number) : AttentionWeights.Normal;
        read.Check(false, PriorityKey);
        return null;
    }

    internal static JsonArray Strings(IEnumerable<string> texts) => new(texts.Select(text => (JsonNode)text).ToArray());
}

/// <summary>
/// Which of a session's attention items a close ends: those of one task (<see cref="TaskId"/>) or of several (<see cref="TaskIds"/>, as
/// archiving sends) on one board, or those of one <see cref="Subject"/>; of every kind, or only of <see cref="Kinds"/>. A close names a task
/// or a subject. With a <see cref="Revision"/>, an item its source raised later stays open, and the close keeps an older raise from opening
/// the keys it names again: per kind when it names kinds, per task when it names tasks but no kinds.
/// </summary>
public sealed record AttentionSelection
{
    public const string TaskIdKey = AttentionContent.TaskIdKey;
    public const string TaskIdsKey = "taskIds";
    public const string BoardIdKey = AttentionContent.BoardIdKey;
    public const string SubjectKey = AttentionContent.SubjectKey;
    public const string KindsKey = "kinds";
    public const string RevisionKey = AttentionContent.RevisionKey;

    public string? TaskId { get; init; }
    public IReadOnlyList<string>? TaskIds { get; init; }

    /// <summary>The tasks' board; none is the default board.</summary>
    public string? BoardId { get; init; }

    public string? Subject { get; init; }

    /// <summary>Only items of these kinds close; kinds the session does not know are ignored.</summary>
    public IReadOnlyList<string>? Kinds { get; init; }

    public long? Revision { get; init; }

    internal void WriteTo(JsonObject json)
    {
        if (TaskId is not null) json[TaskIdKey] = TaskId;
        if (TaskIds is not null) json[TaskIdsKey] = AttentionContent.Strings(TaskIds);
        if (BoardId is not null) json[BoardIdKey] = BoardId;
        if (Subject is not null) json[SubjectKey] = Subject;
        if (Kinds is not null) json[KindsKey] = AttentionContent.Strings(Kinds);
        if (Revision is { } revision) json[RevisionKey] = revision;
    }

    internal static AttentionSelection Read(MessageReader read) => new()
    {
        TaskId = read.OptionalText(TaskIdKey), TaskIds = read.OptionalStrings(TaskIdsKey), BoardId = read.OptionalText(BoardIdKey), Subject = read.OptionalText(SubjectKey),
        Kinds = read.OptionalStrings(KindsKey), Revision = read.OptionalNumber(RevisionKey, minimum: 0)
    };
}

/// <summary>
/// <see cref="Operations.AttentionRaise"/>: an aggregate raises <see cref="Item"/> at a session through its outbox (a board's task items
/// and order steps, a lock's claim items, and every manager item the control directory passes on). A standing order's raise names its order
/// and run (<see cref="OrderId"/>, <see cref="OrderRun"/>), which the session ignores. Advisory: a session that refuses it (closed, never
/// opened, or a field it does not accept) would refuse it on every retry, so it is dropped; a refused order raise counts as a failed run of
/// its order.
/// </summary>
public sealed record AttentionRaise(string RepositoryId, AttentionContent Item, string? OrderId = null, long? OrderRun = null) : IEffectMessage<AttentionRaise>
{
    public static string Operation => Operations.AttentionRaise;
    public static string TargetKind => AggregateKinds.Session;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public const string OrderIdKey = Operations.OrderIdField;
    public const string OrderRunKey = Operations.OrderRunField;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId };
        Item.WriteTo(json);
        if (OrderId is not null) { json[OrderIdKey] = OrderId; json[OrderRunKey] = OrderRun; }
        return json;
    }

    public static Decoding<AttentionRaise> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(Read(read, read.Text("repositoryId")));
    }

    /// <summary>Reads the raise's fields but its repository, which <paramref name="repositoryId"/> gives (a route's item names the route's).</summary>
    internal static AttentionRaise Read(MessageReader read, string repositoryId)
    {
        var raise = new AttentionRaise(repositoryId, AttentionContent.Read(read), read.OptionalText(OrderIdKey), read.OptionalNumber(OrderRunKey, minimum: 0));
        // An order's raise names its run, which a refusal counts as failed.
        read.Check(raise.OrderId is null || raise.OrderRun is not null, OrderRunKey);
        return raise;
    }
}

/// <summary>
/// <see cref="Operations.AttentionClose"/>: an aggregate closes the items <see cref="Selection"/> names at a session through its outbox,
/// when the condition that raised them ends. Advisory, like a raise: a session that refuses it would refuse it on every retry.
/// </summary>
public sealed record AttentionClose(string RepositoryId, AttentionSelection Selection) : IEffectMessage<AttentionClose>
{
    public static string Operation => Operations.AttentionClose;
    public static string TargetKind => AggregateKinds.Session;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId };
        Selection.WriteTo(json);
        return json;
    }

    public static Decoding<AttentionClose> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(Read(read, read.Text("repositoryId")));
    }

    /// <summary>Reads the close's fields but its repository, which <paramref name="repositoryId"/> gives (a route's item names the route's).</summary>
    internal static AttentionClose Read(MessageReader read, string repositoryId) => new(repositoryId, AttentionSelection.Read(read));
}

/// <summary>
/// <see cref="Operations.AttentionRoute"/>: an aggregate asks the control directory to pass one raise or close (<see cref="Raise"/> or
/// <see cref="Close"/>, carried as <c>item</c> under its own <c>operation</c>) to every session of <see cref="Role"/> in the repository. A
/// raise goes to recently active sessions only, never to <see cref="ExceptSessionId"/> (the session whose call caused it); a close reaches
/// every one. Advisory: the items it carries are.
/// </summary>
public sealed record AttentionRoute : IEffectMessage<AttentionRoute>
{
    private AttentionRoute(string repositoryId, string role, string exceptSessionId, AttentionRaise? raise, AttentionClose? close) =>
        (RepositoryId, Role, ExceptSessionId, Raise, Close) = (repositoryId, role, exceptSessionId, raise, close);

    public static string Operation => Operations.AttentionRoute;
    public static string TargetKind => AggregateKinds.Control;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public string RepositoryId { get; }

    /// <summary>The role, given at <c>session_open</c> and compared ignoring case, of the sessions that get the item.</summary>
    public string Role { get; }

    /// <summary>The session that caused a raise, which does not get it; empty when none did, and for a close.</summary>
    public string ExceptSessionId { get; }

    public AttentionRaise? Raise { get; }
    public AttentionClose? Close { get; }

    /// <summary>Passes <paramref name="raise"/> to the sessions of <paramref name="role"/> in its repository, but <paramref name="exceptSessionId"/>.</summary>
    public static AttentionRoute Raising(string role, AttentionRaise raise, string exceptSessionId) => new(raise.RepositoryId, role, exceptSessionId, raise, null);

    /// <summary>Passes <paramref name="close"/> to every session of <paramref name="role"/> in its repository.</summary>
    public static AttentionRoute Closing(string role, AttentionClose close) => new(close.RepositoryId, role, "", null, close);

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["role"] = Role, ["operation"] = Raise is not null ? AttentionRaise.Operation : AttentionClose.Operation, ["exceptSessionId"] = ExceptSessionId,
        ["item"] = Raise?.ToJson() ?? Close!.ToJson()
    };

    public static Decoding<AttentionRoute> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var (repository, role, except, operation) = (read.Text("repositoryId"), read.Text("role"), read.OptionalText("exceptSessionId") ?? "", read.Text("operation"));
        var item = read.Object("item");
        var route = operation switch
        {
            Operations.AttentionRaise => new AttentionRoute(repository, role, except, AttentionRaise.Read(item, repository), null),
            Operations.AttentionClose => new AttentionRoute(repository, role, "", null, AttentionClose.Read(item, repository)),
            _ => null
        };
        read.Check(route is not null, "operation");
        return read.Result(route ?? new AttentionRoute(repository, role, except, null, new AttentionClose(repository, new())));
    }
}

/// <summary>
/// <see cref="Operations.AttentionSuggest"/>: the hub raises its own <see cref="Item"/> at the session whose call it serves, directly, after
/// the reply: the work a <c>task_update</c> reply offered, related work found after a claim, or a feedback tip. Each gives its own lifetime.
/// The session applies it as a raise.
/// </summary>
public sealed record AttentionSuggestion(string RepositoryId, string SessionId, AttentionContent Item) : IInternalRequest<AttentionSuggestion>
{
    public static string Operation => Operations.AttentionSuggest;
    public static string TargetKind => AggregateKinds.Session;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId };
        Item.WriteTo(json);
        return json;
    }

    public static Decoding<AttentionSuggestion> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new AttentionSuggestion(read.Text("repositoryId"), read.Text("sessionId"), AttentionContent.Read(read)));
    }
}

/// <summary>
/// <see cref="Operations.AttentionClaimed"/>: the session claimed <see cref="TaskId"/> on <see cref="BoardId"/>, so the hub closes its
/// <c>next_work</c> items for that task, at the claim's <see cref="Revision"/>, directly after the claim. (The board closes the item at the
/// task's target; this closes the claimer's own, from a next-work offer.)
/// </summary>
public sealed record AttentionClaimed(string RepositoryId, string SessionId, string BoardId, string TaskId, long Revision) : IInternalRequest<AttentionClaimed>
{
    public static string Operation => Operations.AttentionClaimed;
    public static string TargetKind => AggregateKinds.Session;

    /// <summary>The close the session applies.</summary>
    public AttentionSelection Selection => new() { TaskId = TaskId, BoardId = BoardId, Kinds = [AttentionKinds.NextWork], Revision = Revision };

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["boardId"] = BoardId, ["taskId"] = TaskId, ["revision"] = Revision };

    public static Decoding<AttentionClaimed> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new AttentionClaimed(read.Text("repositoryId"), read.Text("sessionId"), read.Text("boardId"), read.Text("taskId"), read.Number("revision", minimum: 0)));
    }
}

/// <summary>
/// <see cref="Operations.AttentionOffer"/>: a boundary reply is on its way to the session (its handle: <see cref="SessionId"/>,
/// <see cref="SessionToken"/>), so it picks the one item that reply offers. Not one about <see cref="FocusTaskId"/> on
/// <see cref="FocusBoardId"/>, the task the call was about, and not the info item of a message on the <c>check_inbox</c> page the reply
/// carries (<see cref="PageMessageIds"/>). An item the agent answers <c>later</c> sits out <see cref="SnoozeBoundaries"/> boundaries.
/// </summary>
public sealed record AttentionOffer(string RepositoryId, string SessionId, string SessionToken) : IInternalRequest<AttentionOffer>
{
    public static string Operation => Operations.AttentionOffer;
    public static string TargetKind => AggregateKinds.Session;

    public string? FocusTaskId { get; init; }
    public string? FocusBoardId { get; init; }
    public long? SnoozeBoundaries { get; init; }
    public IReadOnlyList<string>? PageMessageIds { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["sessionToken"] = SessionToken };
        if (FocusTaskId is not null) json["focusTaskId"] = FocusTaskId;
        if (FocusBoardId is not null) json["focusBoardId"] = FocusBoardId;
        if (SnoozeBoundaries is { } boundaries) json["snoozeBoundaries"] = boundaries;
        if (PageMessageIds is not null) json["pageMessageIds"] = AttentionContent.Strings(PageMessageIds);
        return json;
    }

    public static Decoding<AttentionOffer> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new AttentionOffer(read.Text("repositoryId"), read.Text("sessionId"), read.Text("sessionToken"))
        {
            FocusTaskId = read.OptionalText("focusTaskId"), FocusBoardId = read.OptionalText("focusBoardId"), SnoozeBoundaries = read.OptionalNumber("snoozeBoundaries"),
            PageMessageIds = read.OptionalStrings("pageMessageIds")
        });
    }
}

/// <summary>
/// <see cref="Operations.AttentionSeen"/>: an adapter's reader showed the model <see cref="MessageIds"/> without an offer (a <c>check_inbox</c>
/// page as sent, or the messages it printed), so their info items close. The messages stay in the inbox.
/// </summary>
public sealed record AttentionSeen(string RepositoryId, string SessionId, string SessionToken, IReadOnlyList<string> MessageIds) : IInternalRequest<AttentionSeen>
{
    public static string Operation => Operations.AttentionSeen;
    public static string TargetKind => AggregateKinds.Session;

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["sessionToken"] = SessionToken, ["messageIds"] = AttentionContent.Strings(MessageIds)
    };

    public static Decoding<AttentionSeen> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new AttentionSeen(read.Text("repositoryId"), read.Text("sessionId"), read.Text("sessionToken"), read.Strings("messageIds")));
    }
}

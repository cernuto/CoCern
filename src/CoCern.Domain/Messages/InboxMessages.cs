using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

/// <summary>
/// One message in a session's inbox, in the one shape the session stores and <c>check_inbox</c> returns. It is one of three: a direct
/// message from another session (<c>send_message</c>: <see cref="Author"/>, <see cref="SenderSessionId"/>, <see cref="Text"/>), a notice
/// that a message could not be delivered or read (<see cref="UndeliveredKind"/>), or a board's notice that a task changed
/// (<see cref="TaskChanged"/>). Only <see cref="MessageId"/> and <see cref="RepositoryId"/> are always there; each field is stored only when
/// its message has it.
/// </summary>
public sealed record InboxMessage
{
    public const string MessageIdKey = "messageId";
    public const string RepositoryIdKey = "repositoryId";
    public const string KindKey = "kind";
    public const string TaskIdKey = "taskId";
    public const string BoardIdKey = "boardId";
    public const string TextKey = "text";
    public const string AuthorKey = "author";
    public const string SenderSessionIdKey = "senderSessionId";
    public const string SentAtKey = "sentAt";
    public const string ReplyToKey = "replyTo";
    public const string UndeliveredMessageIdKey = "undeliveredMessageId";
    public const string RecipientSessionIdKey = "recipientSessionId";
    public const string ReasonKey = "reason";
    public const string TaskVersionKey = "taskVersion";
    public const string SupersedeKey = "supersede";
    public const string WakeKey = "wake";
    public const string ManagerGenerationKey = "managerGeneration";

    /// <summary>Every key a stored message may hold.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        MessageIdKey, RepositoryIdKey, KindKey, TaskIdKey, BoardIdKey, TextKey, AuthorKey, SenderSessionIdKey, SentAtKey, ReplyToKey, UndeliveredMessageIdKey,
        RecipientSessionIdKey, ReasonKey, TaskVersionKey, SupersedeKey, WakeKey, ManagerGenerationKey
    ];

    /// <summary>The kind of the notice a sender gets when its message could not be delivered, or was never read.</summary>
    public const string UndeliveredKind = "delivery.failed";

    /// <summary>The kind of a board's notice that a task the session works on or was addressed in changed.</summary>
    public const string TaskChangedKind = "task.changed";

    /// <summary>The message's ID, unique in its recipient's inbox: a second delivery of it is ignored.</summary>
    public required string MessageId { get; init; }
    public required string RepositoryId { get; init; }

    /// <summary>A notice's kind (<see cref="UndeliveredKind"/>, <see cref="TaskChangedKind"/>); a direct message has none.</summary>
    public string? Kind { get; init; }

    public string? TaskId { get; init; }
    public string? BoardId { get; init; }
    public string? Text { get; init; }

    /// <summary>The principal who sent a direct message.</summary>
    public string? Author { get; init; }

    /// <summary>The session that sent it, which is told if it cannot be delivered or is never read.</summary>
    public string? SenderSessionId { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    /// <summary>The ID of the message this one answers. A direct message always says, null when it answers none.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>A notice's message, the session it was for, and why it was not delivered or read.</summary>
    public string? UndeliveredMessageId { get; init; }
    public string? RecipientSessionId { get; init; }
    public string? Reason { get; init; }

    /// <summary>The task version a <see cref="TaskChangedKind"/> notice names.</summary>
    public long? TaskVersion { get; init; }

    /// <summary>Replaces its sender's earlier unread superseding message (a periodic status request), so the inbox keeps one.</summary>
    public bool Supersede { get; init; }

    /// <summary>False for a status or FYI message, which never wakes its recipient; it is read with the next wake or inbox check.</summary>
    public bool Wake { get; init; } = true;

    /// <summary>
    /// Set by the hub alone when the verified sender held the manager's seat, at this generation. It says who sent the message, not that Paul approved
    /// anything: the recipient treats it as a request unless it falls inside a goal he approved.
    /// </summary>
    public long? ManagerGeneration { get; init; }

    /// <summary>A board's notice that <paramref name="taskId"/> changed to <paramref name="taskVersion"/>, for the session it concerns.</summary>
    public static InboxMessage TaskChanged(string repositoryId, string messageId, string boardId, string taskId, long taskVersion) => new()
    {
        MessageId = messageId, RepositoryId = repositoryId, Kind = TaskChangedKind, TaskId = taskId, BoardId = boardId, TaskVersion = taskVersion
    };

    public JsonObject ToJson()
    {
        var json = new JsonObject { [MessageIdKey] = MessageId, [RepositoryIdKey] = RepositoryId };
        if (Kind is not null) json[KindKey] = Kind;
        if (TaskId is not null) json[TaskIdKey] = TaskId;
        if (BoardId is not null) json[BoardIdKey] = BoardId;
        if (Text is not null) json[TextKey] = Text;
        if (Author is not null) json[AuthorKey] = Author;
        if (SenderSessionId is not null) json[SenderSessionIdKey] = SenderSessionId;
        if (SentAt is { } sent) json[SentAtKey] = sent.ToString("O");
        if (ReplyTo is not null || Author is not null) json[ReplyToKey] = ReplyTo;
        if (UndeliveredMessageId is not null) json[UndeliveredMessageIdKey] = UndeliveredMessageId;
        if (RecipientSessionId is not null) json[RecipientSessionIdKey] = RecipientSessionId;
        if (Reason is not null) json[ReasonKey] = Reason;
        if (TaskVersion is { } version) json[TaskVersionKey] = version;
        if (Supersede) json[SupersedeKey] = true;
        if (!Wake) json[WakeKey] = false;
        if (ManagerGeneration is { } generation) json[ManagerGenerationKey] = generation;
        return json;
    }

    /// <summary>Reads a message (a delivery's, or one the inbox holds), naming each missing or invalid field.</summary>
    public static Decoding<InboxMessage> Decode(JsonObject json)
    {
        var read = new MessageReader(json);
        return read.Result(Read(read));
    }

    internal static InboxMessage Read(MessageReader read) => new()
    {
        MessageId = read.Text(MessageIdKey), RepositoryId = read.Text(RepositoryIdKey), Kind = read.OptionalText(KindKey), TaskId = read.OptionalText(TaskIdKey),
        BoardId = read.OptionalText(BoardIdKey), Text = read.OptionalText(TextKey), Author = read.OptionalText(AuthorKey), SenderSessionId = read.OptionalText(SenderSessionIdKey),
        SentAt = read.OptionalDate(SentAtKey), ReplyTo = read.OptionalText(ReplyToKey), UndeliveredMessageId = read.OptionalText(UndeliveredMessageIdKey),
        RecipientSessionId = read.OptionalText(RecipientSessionIdKey), Reason = read.OptionalText(ReasonKey), TaskVersion = read.OptionalNumber(TaskVersionKey),
        Supersede = read.OptionalFlag(SupersedeKey), Wake = read.Node(WakeKey) is null || read.Flag(WakeKey),
        ManagerGeneration = read.OptionalNumber(ManagerGenerationKey)
    };
}

/// <summary>
/// <see cref="Operations.Deliver"/>: a message for a session's inbox, sent by a session (a direct message, or the notice that one of its
/// sender's messages was not delivered or read) or by a board (a task changed). With attention on, the dispatcher adds the lifetime of the
/// info item a message that may wake its chat becomes (<see cref="AttentionExpiryDays"/>); the inbox keeps the message without it. Retried
/// until it lands: a session that cannot take the message (closed, never opened, its inbox full) still accepts it and tells its sender, so
/// nothing refuses one for good.
/// </summary>
public sealed record MessageDelivery(InboxMessage Message, long? AttentionExpiryDays = null, long? PeerReplySequence = null) : IEffectMessage<MessageDelivery>
{
    public static string Operation => Operations.Deliver;
    public static string TargetKind => AggregateKinds.Session;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    /// <summary>The field the dispatcher sets on a delivery's payload; see <see cref="AttentionExpiryDays"/>.</summary>
    public const string AttentionExpiryDaysKey = "attentionExpiryDays";
    /// <summary>Private sender order; delivery metadata is not stored in the inbox.</summary>
    public const string PeerReplySequenceKey = "peerReplySequence";

    public JsonObject ToJson()
    {
        var json = Message.ToJson();
        if (AttentionExpiryDays is { } days) json[AttentionExpiryDaysKey] = days;
        if (PeerReplySequence is { } sequence) json[PeerReplySequenceKey] = sequence;
        return json;
    }

    public static Decoding<MessageDelivery> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var message = InboxMessage.Read(read);
        return read.Result(new MessageDelivery(message, read.OptionalNumber(AttentionExpiryDaysKey, minimum: 0), read.OptionalNumber(PeerReplySequenceKey, minimum: 1)));
    }
}

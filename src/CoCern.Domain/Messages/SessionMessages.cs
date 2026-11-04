using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

/// <summary>Private lifecycle recheck of the directory's exact session after a close acknowledgement is delayed or lost.</summary>
public sealed record NativeExpiryCheck(string RepositoryId, string SessionId, string ParticipantId)
{
    public const string Operation = "__native_expiry_check";
    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, [NativeParticipant.Id] = ParticipantId };
}

/// <summary>
/// <see cref="Operations.Open"/>: initializes a session from its record. Sent twice by design, both built by
/// <see cref="SessionRecord.Opened"/>: the control directory queues it with the record it keeps (with its initial freshness), and
/// <c>session_open</c> calls the session directly with the client's part, so the session can be used at once. The second to land changes
/// nothing. Retried until it lands.
/// </summary>
public sealed record SessionOpen(SessionRecord Record) : IEffectMessage<SessionOpen>
{
    public static string Operation => Operations.Open;
    public static string TargetKind => AggregateKinds.Session;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    public JsonObject ToJson() => Record.ToJson();

    public static Decoding<SessionOpen> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var record = SessionRecord.Read(read);
        return read.Result(new SessionOpen(record));
    }
}

/// <summary>
/// <see cref="Operations.SessionExpire"/>: the control directory dropped this session from its listing (<see cref="CloseSeq"/> numbers
/// that close). <see cref="SeenAt"/> is what the directory had heard of the session when it decided, so one that has reported activity
/// since refuses; <see cref="Idle"/> asks it to refuse for unread mail too; <see cref="Force"/>, from <see cref="RequestedBy"/>, overrides
/// both and unresolved work. Retried until it lands: a lost one leaves a session open that the directory no longer lists.
/// </summary>
public sealed record SessionExpire(string RepositoryId, string SessionId, long CloseSeq, DateTimeOffset? SeenAt = null, bool Idle = false, bool Force = false, string? RequestedBy = null)
    : IEffectMessage<SessionExpire>
{
    public static string Operation => Operations.SessionExpire;
    public static string TargetKind => AggregateKinds.Session;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["closeSeq"] = CloseSeq };
        if (SeenAt is { } seen) json["seenAt"] = seen.ToString("O");
        if (Idle) json["idle"] = true;
        if (Force) { json["force"] = true; json["requestedBy"] = RequestedBy; }
        return json;
    }

    public static Decoding<SessionExpire> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var expire = new SessionExpire(read.Text("repositoryId"), read.Text("sessionId"), read.Number("closeSeq", minimum: 1), read.OptionalDate("seenAt"), read.OptionalFlag("idle"),
            read.OptionalFlag("force"), read.OptionalText("requestedBy"));
        // A forced close names who forced it; the closed session keeps that.
        read.Check(!expire.Force || expire.RequestedBy is { Length: > 0 }, "requestedBy");
        return read.Result(expire);
    }
}

/// <summary>
/// <see cref="Operations.SessionRestore"/>: a session refused the close numbered <see cref="CloseSeq"/>, so the directory, which already
/// dropped it, takes back its record as the session keeps it, with the work it answers for (<see cref="Owns"/>, at the session's report
/// number <see cref="Seq"/>) or the unread mail that exempts it from idle closes (<see cref="Mail"/>). Retried until it lands: a lost one
/// leaves a session open that the directory no longer lists.
/// </summary>
public sealed record SessionRestore(string RepositoryId, string SessionId, long CloseSeq, SessionRecord Record, bool Owns = false, long Seq = 0, bool Mail = false)
    : IEffectMessage<SessionRestore>
{
    public static string Operation => Operations.SessionRestore;
    public static string TargetKind => AggregateKinds.Control;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["closeSeq"] = CloseSeq, ["record"] = Record.ToJson() };
        if (Owns) { json["owns"] = true; json["seq"] = Seq; }
        if (Mail) json["mail"] = true;
        return json;
    }

    public static Decoding<SessionRestore> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var (repository, session, closeSeq) = (read.Text("repositoryId"), read.Text("sessionId"), read.Number("closeSeq", minimum: 1));
        var record = SessionRecord.Read(read.Object("record"));
        var owns = read.OptionalFlag("owns");
        // Ownership is ordered by the session's report number, so a restore that claims work carries it.
        var seq = owns ? read.Number("seq", minimum: 0) : 0;
        read.Check(record.SessionId.Length == 0 || record.SessionId == session, "record.sessionId");
        return read.Result(new SessionRestore(repository, session, closeSeq, record, owns, seq, read.OptionalFlag("mail")));
    }
}

/// <summary>
/// <see cref="Operations.SessionExpired"/>: a session accepted the close numbered <see cref="CloseSeq"/>, so the directory stops waiting for
/// its answer. Retried until it lands.
/// </summary>
public sealed record SessionExpired(string RepositoryId, string SessionId, long CloseSeq) : IEffectMessage<SessionExpired>
{
    public static string Operation => Operations.SessionExpired;
    public static string TargetKind => AggregateKinds.Control;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["closeSeq"] = CloseSeq };

    public static Decoding<SessionExpired> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionExpired(read.Text("repositoryId"), read.Text("sessionId"), read.Number("closeSeq", minimum: 1)));
    }
}

/// <summary>
/// <see cref="Operations.SessionClosed"/>: a closed session tells a board watching it, or holding work it answered for, so tasks targeting
/// it lose the target and its unresolved actions there become recoverable at once. Retried until it lands: a lost one leaves those
/// actions held until their claims' leases run out.
/// </summary>
public sealed record SessionClosed(string RepositoryId, string BoardId, string SessionId) : IEffectMessage<SessionClosed>
{
    public static string Operation => Operations.SessionClosed;
    public static string TargetKind => AggregateKinds.Board;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    /// <summary>The board grain it goes to.</summary>
    public string BoardKey => AggregateKinds.BoardKey(RepositoryId, BoardId);

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["boardId"] = BoardId, ["sessionId"] = SessionId };

    public static Decoding<SessionClosed> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionClosed(read.Text("repositoryId"), read.Text("boardId"), read.Text("sessionId")));
    }
}

/// <summary>
/// <see cref="Operations.Register"/>: after <c>session_bind</c>, a session gives the control directory its presence as it now stands
/// (binding, its version and freshness, availability, machine, label, hub incarnation). Bind is not repeated on its own, so this is retried
/// until it lands; the directory never lets an older binding replace a newer one or move a time back.
/// </summary>
public sealed record SessionRegistration(string RepositoryId, string SessionId, SessionPresence Presence) : IEffectMessage<SessionRegistration>
{
    public static string Operation => Operations.Register;
    public static string TargetKind => AggregateKinds.Control;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId };
        Presence.WriteTo(json);
        return json;
    }

    public static Decoding<SessionRegistration> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var (repository, session) = (read.Text("repositoryId"), read.Text("sessionId"));
        // The version orders registrations, and the times are what the directory judges freshness by.
        read.Number("bindingVersion", minimum: 0); read.Date("observedAt"); read.Date("freshUntil");
        return read.Result(new SessionRegistration(repository, session, SessionPresence.Read(read)));
    }
}

/// <summary>
/// <see cref="Operations.SessionActive"/>: a session tells the control directory it was active at <see cref="ActiveAt"/>, at most once per
/// <see cref="Contracts.RelayLimits.AttentionActivityReportMinutes"/> while it keeps calling. Dropped and counted when refused: its next
/// report replaces it, and the directory keeps the latest time.
/// </summary>
public sealed record SessionActivity(string RepositoryId, string SessionId, DateTimeOffset ActiveAt) : IEffectMessage<SessionActivity>
{
    public static string Operation => Operations.SessionActive;
    public static string TargetKind => AggregateKinds.Control;
    public static DeliveryClass Delivery => DeliveryClass.DropAndCount;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["activeAt"] = ActiveAt.ToString("O") };

    public static Decoding<SessionActivity> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionActivity(read.Text("repositoryId"), read.Text("sessionId"), read.Date("activeAt")));
    }
}

/// <summary>
/// <see cref="Operations.SessionTargetWatch"/>: a board (<see cref="BoardKey"/>, <see cref="BoardId"/>) has a task that now targets this
/// session, and asks to hear when it closes (<see cref="SessionClosed"/>), so the task does not wait for a session that will never claim
/// it. Retried until it lands: the board asks once, when the target is named, and a lost watch would leave the task waiting.
/// </summary>
public sealed record SessionTargetWatch(string RepositoryId, string SessionId, string BoardKey, string BoardId) : IEffectMessage<SessionTargetWatch>
{
    public static string Operation => Operations.SessionTargetWatch;
    public static string TargetKind => AggregateKinds.Session;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["boardKey"] = BoardKey, ["boardId"] = BoardId };

    public static Decoding<SessionTargetWatch> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionTargetWatch(read.Text("repositoryId"), read.Text("sessionId"), read.Text("boardKey"), read.Text("boardId")));
    }
}

/// <summary>
/// <see cref="Operations.Verify"/>: before a call that presents a session handle (<see cref="SessionId"/>, <see cref="SessionToken"/>), the
/// hub asks the session to check it, and gets the session's record back: its binding (current only for this hub's
/// <see cref="HubIncarnation"/>), role, machine and the standing it last received. The session also reports its activity, at most hourly.
/// </summary>
public sealed record SessionVerify(string RepositoryId, string SessionId, string SessionToken, string? HubIncarnation = null) : IInternalRequest<SessionVerify>
{
    public static string Operation => Operations.Verify;
    public static string TargetKind => AggregateKinds.Session;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["sessionToken"] = SessionToken };
        if (HubIncarnation is not null) json["hubIncarnation"] = HubIncarnation;
        return json;
    }

    public static Decoding<SessionVerify> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionVerify(read.Text("repositoryId"), read.Text("sessionId"), read.Text("sessionToken"), read.OptionalText("hubIncarnation")));
    }
}

/// <summary>
/// <see cref="Operations.StandingSeen"/>: the session (its handle: <see cref="SessionId"/>, <see cref="SessionToken"/>) received board
/// <see cref="BoardId"/>'s standing at <see cref="StandingRevision"/>, so later replies carry that board's standing only when it changed.
/// </summary>
public sealed record StandingSeen(string RepositoryId, string SessionId, string SessionToken, string BoardId, long StandingRevision) : IInternalRequest<StandingSeen>
{
    public static string Operation => Operations.StandingSeen;
    public static string TargetKind => AggregateKinds.Session;

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["sessionToken"] = SessionToken, ["boardId"] = BoardId, ["standingRevision"] = StandingRevision
    };

    public static Decoding<StandingSeen> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new StandingSeen(read.Text("repositoryId"), read.Text("sessionId"), read.Text("sessionToken"), read.Text("boardId"), read.Number("standingRevision", minimum: 0)));
    }
}

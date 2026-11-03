using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

/// <summary>
/// <see cref="Operations.SessionWork"/>, sent by a board to the session that answers for one unresolved action: it now does (<see cref="Open"/>:
/// it began or recovered the action, or a long action's daily refresh) or no longer does. <see cref="At"/> is the board's time, by which the
/// session's ledger orders reports that arrive reordered. Retried until it lands: a lost open would let a close strand the action, and a
/// lost close would keep the session from ever closing.
/// </summary>
public sealed record ActionWorkReport(string RepositoryId, string SessionId, string BoardId, string TaskId, string ActionId, long Attempt, bool Open, DateTimeOffset At)
    : IEffectMessage<ActionWorkReport>
{
    public static string Operation => Operations.SessionWork;
    public static string TargetKind => AggregateKinds.Session;
    public static DeliveryClass Delivery => DeliveryClass.Retry;

    /// <summary>The action attempt's key, as the board and the locks it blocks know it.</summary>
    public string ActionKey => CoordinationStateMachine.ActionKey(RepositoryId, BoardId, TaskId, ActionId, Attempt);

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["boardId"] = BoardId, ["taskId"] = TaskId, ["actionId"] = ActionId, ["attempt"] = Attempt,
        ["open"] = Open, ["at"] = At.ToString("O")
    };

    public static Decoding<ActionWorkReport> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new ActionWorkReport(read.Text("repositoryId"), read.Text("sessionId"), read.Text("boardId"), read.Text("taskId"), read.Text("actionId"),
            read.Number("attempt", minimum: 1), read.Flag("open"), read.Date("at")));
    }
}

/// <summary>
/// <see cref="Operations.SessionOwns"/>, sent by a session to the control directory: it now answers for unresolved work (<see cref="Owns"/>)
/// or no longer does, sent when that changes and once a day while it lasts. <see cref="Seq"/> numbers the session's reports, so the
/// directory applies only the newest even when two share a second; <see cref="At"/> is when the session decided, which dates the
/// directory's protection. Dropped and counted when refused: the session's next report states the whole of it again.
/// </summary>
public sealed record SessionOwnership(string RepositoryId, string SessionId, bool Owns, DateTimeOffset At, long Seq) : IEffectMessage<SessionOwnership>
{
    public static string Operation => Operations.SessionOwns;
    public static string TargetKind => AggregateKinds.Control;
    public static DeliveryClass Delivery => DeliveryClass.DropAndCount;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["owns"] = Owns, ["at"] = At.ToString("O"), ["seq"] = Seq };

    public static Decoding<SessionOwnership> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionOwnership(read.Text("repositoryId"), read.Text("sessionId"), read.Flag("owns"), read.Date("at"), read.Number("seq", minimum: 1)));
    }
}

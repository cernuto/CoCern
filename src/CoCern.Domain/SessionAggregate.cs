using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>One chat's private session: its secret, reported tool binding, durable inbox and acknowledgments, and its attention queue.</summary>
internal static class SessionAggregate
{
    private const int MinFreshnessSeconds = 1;
    private const int MaxFreshnessSeconds = 600;
    private const int DefaultFreshnessSeconds = 120;

    public static RelayReply Handle(AggregateContext context)
    {
        if (context.Operation == Operations.Open) return Initialize(context);
        if (context.Operation == Operations.SessionExpire) return Expire(context);
        if (context.Operation == Operations.SessionTargetWatch) return WatchTarget(context);
        var expired = context.State["expiredAt"] is not null;
        // An expired session takes no deliveries (a refusal would be retried forever; the sender is told instead) and refuses attention,
        // which its sources then drop.
        if (context.Operation == Operations.Deliver) return expired ? Undelivered(context, Ok(new { expired = true }), "closed") : Deliver(context);
        // Boards report through their outboxes, without the session secret; an expired or unknown session ignores them.
        if (context.Operation == Operations.SessionWork) return expired || !context.State.ContainsKey("principalId") ? Ok(new { tracked = false }) : TrackWork(context);
        if (expired && context.Operation is Operations.AttentionRaise or Operations.AttentionClose) return Error(SessionUnknown, new { expired = true });
        // Attention items arrive through other aggregates' outboxes, which carry no session secret, just like deliveries.
        if (context.Operation == Operations.AttentionRaise) return SessionAttention.Raise(context);
        if (context.Operation == Operations.AttentionClose) return SessionAttention.Close(context);
        if (expired) return Text(context.State, "principalId") == context.Caller.PrincipalId ? Error(SessionUnknown, new { expired = true, reopen = RelayTools.SessionOpen }) : Error(SessionDenied);
        if (!Authenticates(context)) return Error(SessionDenied);
        return context.Operation switch
        {
            Operations.Verify or RelayTools.GetStatus => Describe(context),
            RelayTools.SessionBind => Bind(context),
            RelayTools.CheckInbox => Page(ObjectAt(context.State, "inbox").Select(x => x.Value!), context.Args),
            RelayTools.Ack => Acknowledge(context),
            RelayTools.SendMessage => SendDirect(context),
            Operations.StandingSeen => MarkStandingSeen(context),
            Operations.AttentionOffer => SessionAttention.Offer(context),
            Operations.AttentionSeen => MarkMessagesSeen(context),
            RelayTools.AttentionRespond => SessionAttention.Respond(context),
            _ => Error(UnknownTool)
        };
    }

    /// <summary>Copies the control directory's record on first delivery; a retry with the same secret is idempotent.</summary>
    private static RelayReply Initialize(AggregateContext context)
    {
        var state = context.State;
        if (state.ContainsKey("principalId"))
            return Text(state, "principalId") == Text(context.Args, "principalId") && Text(state, "sessionToken") == Text(context.Args, "sessionToken") ? Ok(new { initialized = true }) : Error(SessionDenied);
        foreach (var field in OpenFields) if (context.Args[field] is { } value) state[field] = value.DeepClone();
        state["openedAt"] = context.Now.ToString("O");
        if (Text(state, "role").Length > RelayLimits.MaxActivityRoleLength) state.Remove("role");
        // The direct call from session_open carries only the client's fields; the control record's freshness defaults fill the rest.
        state["bindingVersion"] ??= 0;
        state["observedAt"] ??= context.Now.ToString("O");
        state["freshUntil"] ??= context.Now.Add(ControlAggregate.InitialFreshness).ToString("O");
        state["availability"] ??= "unknown";
        return Ok(new { initialized = true });
    }

    /// <summary>What a session copies when it opens; anything else in <c>session_open</c>'s arguments could forge freshness or hub state.</summary>
    internal static readonly string[] OpenFields =
    [
        "sessionId", "sessionToken", "principalId", "repositoryId", "hubIncarnation", "machineId", "label", "binding", "role",
        "bindingVersion", "observedAt", "freshUntil", "availability"
    ];

    /// <summary>What an expired session forgets: its secret, its mail and attention, its tool binding, and the boards waiting on it (told as it closes).</summary>
    private static readonly string[] ExpiredState = ["sessionToken", "inbox", "acked", "attention", "attentionClosed", "attentionArchived", "attentionRecent", "standingSeen", "binding", TargetBoardsKey];

    private const string WorkKey = "work";
    private const string WorkReportedKey = "workReported";

    /// <summary>
    /// A board's report that this session now answers for an unresolved action, or no longer does (see <see cref="WorkLedger"/>). The hub's
    /// directory hears only when that changes, and once a day while it lasts, so a full directory never expires a session that owns work.
    /// </summary>
    private static RelayReply TrackWork(AggregateContext context)
    {
        var (state, args, now) = (context.State, context.Args, context.Now);
        var ledger = ObjectAt(state, WorkKey);
        var tracked = WorkLedger.Apply(ledger, Required(args, "actionKey"), Flag(args, "open"), WorkLedger.ReportedAt(args, now), now);
        var owns = WorkLedger.Owns(ledger, now);
        if (ledger.Count == 0) state.Remove(WorkKey);
        var reported = state[WorkReportedKey] as JsonObject;
        var (wasOwning, reportedAt) = reported is null ? (false, DateTimeOffset.MinValue) : (Flag(reported, "owns"), Date(reported, "at"));
        if (owns != wasOwning || owns && reportedAt <= now.AddHours(-RelayLimits.SessionWorkRefreshHours))
        {
            // The sequence orders reports that share a second, which board times alone cannot.
            var seq = (reported is null ? 0 : Number(reported, "seq")) + 1;
            state[WorkReportedKey] = new JsonObject { ["owns"] = owns, ["at"] = now.ToString("O"), ["seq"] = seq };
            context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.SessionWork, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["sessionId"] = Text(state, "sessionId"), ["owns"] = owns, ["at"] = now.ToString("O"), ["seq"] = seq
            });
        }
        return Ok(new { tracked, owns });
    }

    /// <summary>
    /// The hub's directory closed this session (idle, by an administrator's filter, or to make room). Its secret, inbox and attention are
    /// cleared; afterwards its own calls get <c>SessionUnknown</c> with <c>expired</c>, its chat opens a new session, and each board with a
    /// task targeting it is told (<see cref="Operations.SessionClosed"/>). Idempotent, and accepted for a session never opened here.
    /// <para>The directory decided from what it knew, which can lag what this session knows, so the session checks once more and refuses
    /// (see <see cref="KeepOpen"/>) when it answers for an unresolved action (a board's report that it began one reaches this session
    /// before the directory), or when the close is an idle one (<c>idle: true</c>) and its inbox holds unread mail, which expiry would delete.</para>
    /// </summary>
    private static RelayReply Expire(AggregateContext context)
    {
        var state = context.State;
        if (!state.ContainsKey("principalId") || state["expiredAt"] is not null) return Ok(new { expired = true });
        var owns = state[WorkKey] is JsonObject ledger && WorkLedger.Owns(ledger, context.Now);
        var mail = IsTrue(context.Args, "idle") && state["inbox"] is JsonObject { Count: > 0 };
        if (owns || mail) return KeepOpen(context, owns, mail);
        if (state[TargetBoardsKey] is JsonObject boards)
            foreach (var board in boards) TellBoard(context, board.Key, Text(board.Value as JsonObject ?? new JsonObject(), "boardId", "default"));
        foreach (var key in ExpiredState) state.Remove(key);
        state["expiredAt"] = context.Now.ToString("O");
        return Ok(new { expired = true });
    }

    /// <summary>
    /// Refuses an expiry: the directory already dropped this session, so it sends its record back (<see cref="Operations.SessionRestore"/>),
    /// as the directory keeps it, with the work it answers for (and its report sequence, so the directory orders later reports after it) or
    /// the unread mail that holds it open. Nothing here changes; the chat never sees the close.
    /// </summary>
    private static RelayReply KeepOpen(AggregateContext context, bool owns, bool mail)
    {
        var state = context.State;
        var record = new JsonObject();
        foreach (var field in OpenFields) if (state[field] is { } value) record[field] = value.DeepClone();
        // The directory's activity time is the last one this session reported.
        if (Text(state, "activeReportedAt") is { Length: > 0 } active) record["activeAt"] = active;
        var restore = new JsonObject { ["repositoryId"] = context.Repository, ["sessionId"] = Text(state, "sessionId"), ["record"] = record };
        if (owns) { restore["owns"] = true; restore["seq"] = state[WorkReportedKey] is JsonObject reported ? Number(reported, "seq") : 0; }
        if (mail) restore["mail"] = true;
        context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.SessionRestore, restore);
        return Ok(new { expired = false, keptFor = owns ? "work" : "mail" });
    }

    private const string TargetBoardsKey = "targetBoards";

    /// <summary>
    /// <see cref="Operations.SessionTargetWatch"/>: a board has a task targeting this session and asks to hear when it closes, so the task
    /// does not wait for a session that will never claim it. A closed session tells the board at once. At most
    /// <see cref="RelayLimits.MaxSessionTargetBoards"/> boards are remembered, the oldest forgotten first. A session never opened here is
    /// left alone (nothing names it but the target), and a malformed request is ignored, since a refusal would be retried forever.
    /// </summary>
    private static RelayReply WatchTarget(AggregateContext context)
    {
        var state = context.State;
        var board = Text(context.Args, "boardKey");
        var boardId = Text(context.Args, "boardId", "default");
        if (board.Length == 0 || !state.ContainsKey("principalId")) return Ok(new { watching = false });
        if (state["expiredAt"] is not null) { TellBoard(context, board, boardId); return Ok(new { watching = false, closed = true }); }
        var boards = ObjectAt(state, TargetBoardsKey);
        boards.Remove(board);
        boards[board] = new JsonObject { ["boardId"] = boardId };
        while (boards.Count > RelayLimits.MaxSessionTargetBoards) boards.Remove(boards.First().Key);
        return Ok(new { watching = true });
    }

    private static void TellBoard(AggregateContext context, string boardKey, string boardId) =>
        context.Emit(AggregateKinds.Board, boardKey, Operations.SessionClosed, new JsonObject
        {
            ["repositoryId"] = context.Repository, ["boardId"] = boardId, ["sessionId"] = Text(context.State, "sessionId") is { Length: > 0 } self ? self : context.Key
        });

    /// <summary>The kind of the notice a sender gets when its message could not be delivered.</summary>
    private const string UndeliveredKind = "delivery.failed";

    /// <summary>
    /// A direct message this session cannot take (<paramref name="reason"/>: it is <c>closed</c>, was never opened (<c>unknown</c>), or its
    /// inbox is <c>full</c>) is never dropped silently: its sender gets a <c>delivery.failed</c> notice in its own inbox naming the message
    /// (<c>undeliveredMessageId</c>), this session (<c>recipientSessionId</c>) and the reason, so it can stop waiting for an answer. The
    /// notice keeps the message's <c>wake: false</c>; for a superseding message (a periodic status request) it supersedes this session's
    /// earlier notice, so a sender writing to a closed session on a timer keeps one notice. A board's task message has no sender session
    /// (it stays in the task conversation) and a notice is never returned, so notices cannot loop. Returns <paramref name="reply"/>, with
    /// <c>returned</c> when a notice was sent.
    /// </summary>
    private static RelayReply Undelivered(AggregateContext context, RelayReply reply, string reason)
    {
        var args = context.Args;
        var sender = Text(args, "senderSessionId");
        var id = Text(args, "messageId");
        if (sender.Length == 0 || id.Length == 0 || Text(args, "kind") == UndeliveredKind) return reply;
        var recipient = Text(context.State, "sessionId") is { Length: > 0 } self ? self : context.Key;
        var notice = new JsonObject
        {
            // Derived from the message, so a redelivery of it returns the same notice and the sender's inbox keeps one.
            ["messageId"] = "undelivered-" + Retention.Hash(id), ["repositoryId"] = context.Repository, ["kind"] = UndeliveredKind,
            ["text"] = "Message " + id + " to session " + recipient + " was not delivered: " + reason switch
            {
                "closed" => "that session is closed.", "unknown" => "no such session was opened.", _ => "that session's inbox is full."
            },
            ["undeliveredMessageId"] = id, ["recipientSessionId"] = recipient, ["reason"] = reason, ["sentAt"] = context.Now.ToString("O")
        };
        if (!IsNotFalse(args, "wake")) notice["wake"] = false;
        // Superseding is keyed by the sender, so only then does the notice name the closed session as its own.
        if (IsTrue(args, "supersede")) { notice["supersede"] = true; notice["senderSessionId"] = recipient; }
        context.Emit(AggregateKinds.Session, sender, Operations.Deliver, notice);
        var data = JsonNode.Parse(reply.DataJson)!.AsObject();
        data["returned"] = true;
        return Ok(data);
    }

    private static RelayReply Deliver(AggregateContext context)
    {
        var state = context.State;
        // Never opened: a sender named a session that doesn't exist. Its sender is told; a refusal would be retried forever.
        if (!state.ContainsKey("principalId")) return Undelivered(context, Ok(new { delivered = false, unknown = true }), "unknown");
        var id = Required(context.Args, "messageId");
        var inbox = ObjectAt(state, "inbox");
        if (inbox.ContainsKey(id) || ObjectAt(state, "acked").ContainsKey(id)) return Ok(new { duplicate = true });
        // A superseding message replaces its sender's earlier unread superseding message, so a periodic sender (a status request, a
        // heartbeat) leaves one current entry rather than a pile a busy chat must read through. The replaced one counts as acknowledged,
        // so a late redelivery of it is ignored.
        var superseded = 0;
        if (Flag(context.Args, "supersede") && Text(context.Args, "senderSessionId") is { Length: > 0 } sender)
            foreach (var old in inbox.Where(x => x.Value is JsonObject m && Flag(m, "supersede") && Text(m, "senderSessionId") == sender).Select(x => x.Key).ToArray())
            {
                // The acknowledged set is bounded like explicit acknowledgments.
                Retention.MakeRoom(ObjectAt(state, "acked"), context.Now);
                inbox.Remove(old); ObjectAt(state, "acked")[old] = Retention.Stamp(context.Now); superseded++;
                SessionAttention.CloseMessages(state, [old], context.Now);
            }
        // A full inbox (a chat that stopped reading) drops the message: a refusal would be retried forever and stall the sender's outbox.
        // The sender is told instead.
        if (inbox.Count >= RelayLimits.MaxRecords) return Undelivered(context, Ok(new { delivered = false, full = true }), "full");
        // With attention on, the dispatcher passes the item lifetime: a message that may wake its chat is then an info item.
        var message = context.Args.DeepClone().AsObject();
        var infoDays = message["attentionExpiryDays"] is JsonValue days && days.TryGetValue<int>(out var lifetime) ? lifetime : 0;
        message.Remove("attentionExpiryDays");
        inbox[id] = message;
        if (infoDays > 0 && IsNotFalse(message, "wake")) SessionAttention.AddMessage(state, message, infoDays, context.Now);
        return Ok(new { delivered = true, superseded });
    }

    private static bool Authenticates(AggregateContext context) =>
        Text(context.State, "principalId") == context.Caller.PrincipalId && SecretEquals(Text(context.State, "sessionToken"), Text(context.Args, "sessionToken"));

    private static RelayReply Describe(AggregateContext context)
    {
        var state = context.State;
        // Every verified call keeps the session's directory entry fresh, even with attention off or on attention: false calls.
        if (context.Operation == Operations.Verify) SessionAttention.ReportActive(context);
        var current = Date(state, "freshUntil") > context.Now && Text(state, "hubIncarnation") == Text(context.Args, "hubIncarnation");
        var description = new JsonObject
        {
            ["sessionId"] = Text(state, "sessionId"),
            ["bindingVersion"] = Number(state, "bindingVersion"),
            ["binding"] = state["binding"]?.DeepClone(),
            ["availability"] = current ? Text(state, "availability") : "unknown",
            ["observedAt"] = Text(state, "observedAt")
        };
        // The role reported at session_open labels this session's activity; only the hub's internal verification sees it.
        if (context.Operation == Operations.Verify && Text(state, "role") is { Length: > 0 } role) description["role"] = role;
        // The machine given at session_open resolves this session's resource aliases (next-work lookups); also internal only.
        if (context.Operation == Operations.Verify && Text(state, "machineId") is { Length: > 0 } machine) description["machineId"] = machine;
        // The standing revision this session last received per board, so replies carry standing only when it changed; internal only.
        if (context.Operation == Operations.Verify && state["standingSeen"] is JsonObject seen) description["standingSeen"] = seen.DeepClone();
        // The hub asks for the attention count (and, for the adapter, the top item and wake candidate) with an internal argument.
        if (context.Operation == RelayTools.GetStatus && Text(context.Args, "attentionStatus") is "count" or "detail")
            description["attention"] = SessionAttention.Status(state, context.Args, context.Now, Text(context.Args, "attentionStatus") == "detail");
        return Ok(description);
    }

    private static RelayReply MarkMessagesSeen(AggregateContext context)
    {
        SessionAttention.CloseMessages(context.State, Strings(context.Args["messageIds"]), context.Now);
        return Ok(new { seen = true });
    }

    /// <summary>Records that this session received a board's standing at <c>standingRevision</c>. A revision never moves back.</summary>
    private static RelayReply MarkStandingSeen(AggregateContext context)
    {
        var seen = ObjectAt(context.State, "standingSeen");
        var board = Text(context.Args, "boardId", "default");
        var revision = Number(context.Args, "standingRevision");
        if (!seen.ContainsKey(board) && seen.Count >= RelayLimits.MaxStandingBoardsPerSession) return Ok(new { recorded = false });
        if (revision > Number(seen, board)) seen[board] = revision;
        return Ok(new { recorded = true });
    }

    /// <summary>
    /// <c>session_bind</c>: reports the session's tool binding under <c>expectedVersion</c>, and refreshes its freshness.
    /// <list type="bullet">
    /// <item><c>binding</c> and <c>availability</c> left out, or null, stay as they are: the contract documents only "left out, the current
    /// binding stays", and some clients send null for every optional field, so null never clears either. An empty object replaces the binding.</item>
    /// <item><c>machineId</c> is recorded once: a session opened without one (as older clients did) may report it later, but a chat does not
    /// move between PCs, and the machine resolves the session's resource aliases (an alias names a resource on one machine), so a later
    /// value is ignored rather than let one name mean another lock mid-session.</item>
    /// </list>
    /// </summary>
    private static RelayReply Bind(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        var current = Number(state, "bindingVersion");
        if (!ExpectedVersionMatches(args, current)) return VersionConflict(current);
        // A metadata-only bind (a rename, or a missing machine reported later) leaves out binding and keeps the current one.
        if (args["binding"] is { } binding) state["binding"] = binding.DeepClone();
        state["bindingVersion"] = current + 1;
        if (Text(args, "label") is { Length: > 0 } label) state["label"] = label;
        if (Text(args, "machineId") is { Length: > 0 } machine && Text(state, "machineId").Length == 0) state["machineId"] = machine;
        state["observedAt"] = context.Now.ToString("O");
        state["freshUntil"] = context.Now.AddSeconds(Math.Clamp(Number(args, "freshnessSeconds", DefaultFreshnessSeconds), MinFreshnessSeconds, MaxFreshnessSeconds)).ToString("O");
        if (Text(args, "availability") is { Length: > 0 } availability) state["availability"] = availability;
        else state["availability"] ??= "unknown";
        state["hubIncarnation"] = Text(args, "hubIncarnation");
        var registration = new JsonObject { ["repositoryId"] = Text(state, "repositoryId"), ["sessionId"] = Text(state, "sessionId") };
        foreach (var field in ControlAggregate.RegistrationFields) if (state.ContainsKey(field)) registration[field] = state[field]?.DeepClone();
        context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.Register, registration);
        return Ok(new { bindingVersion = current + 1 }, current + 1);
    }

    /// <summary>
    /// Acknowledgment is explicit: a later duplicate delivery of an acknowledged message is ignored. The acknowledged set keeps each ID for
    /// the retry window (<see cref="RelayLimits.RequestReplayRetentionHours"/>), and a full set drops its oldest, so acknowledging never stops working.
    /// </summary>
    private static RelayReply Acknowledge(AggregateContext context)
    {
        var ids = context.Args["messageIds"] as JsonArray ?? new JsonArray(Text(context.Args, "messageId"));
        var acknowledged = 0;
        var acked = ObjectAt(context.State, "acked");
        foreach (var id in Strings(ids))
        {
            if (!acked.ContainsKey(id)) Retention.MakeRoom(acked, context.Now);
            if (ObjectAt(context.State, "inbox").Remove(id)) acknowledged++;
            acked[id] = Retention.Stamp(context.Now);
        }
        SessionAttention.CloseMessages(context.State, Strings(ids), context.Now);
        return Ok(new { acknowledged });
    }

    private static RelayReply SendDirect(AggregateContext context)
    {
        var args = context.Args;
        var recipient = Required(args, "recipientSessionId");
        var id = Guid.NewGuid().ToString("N");
        var message = new JsonObject
        {
            ["messageId"] = id, ["repositoryId"] = context.Repository, ["text"] = MessageText(args), ["author"] = context.Caller.PrincipalId,
            ["senderSessionId"] = context.SessionId, ["sentAt"] = context.Now.ToString("O"), ["replyTo"] = args["replyTo"]?.DeepClone()
        };
        if (Flag(args, "supersede")) message["supersede"] = true;
        // A status or FYI message is stored like any other but never wakes the recipient; it is read with its next wake or inbox check.
        if (!IsNotFalse(args, "wake")) message["wake"] = false;
        // Measured as the recipient's check_inbox returns it, so a message escaping would make unreadable is never accepted.
        if (CheckReadable(message) is { } unreadable) return unreadable;
        context.Emit(AggregateKinds.Session, recipient, Operations.Deliver, message);
        return Ok(new { messageId = id, delivery = "pending" });
    }

    public static string MessageText(JsonObject args)
    {
        var text = Text(args, "text");
        return text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= RelayLimits.MaxReportBytes ? text : throw new ArgumentException("Message text must contain 1 to 8192 UTF-8 bytes.");
    }
}

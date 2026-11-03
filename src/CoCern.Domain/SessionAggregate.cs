using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// One chat's private session: its secret, reported tool binding, durable inbox and acknowledgments, and its attention queue. Its document
/// is read and written through <see cref="SessionState"/>.
/// </summary>
internal static class SessionAggregate
{
    private const int MinFreshnessSeconds = 1;
    private const int MaxFreshnessSeconds = 600;
    private const int DefaultFreshnessSeconds = 120;

    public static RelayReply Handle(AggregateContext context)
    {
        // Messages from other aggregates' outboxes and the hub's own attention calls carry no session secret. Each handler decodes its
        // message first; an expired session takes no deliveries (the sender is told instead, since a refusal would be retried forever)
        // and refuses attention, which its sources then drop.
        switch (context.Operation)
        {
            case Operations.Open: return Initialize(context);
            case Operations.SessionExpire: return Expire(context);
            case Operations.SessionTargetWatch: return WatchTarget(context);
            case Operations.Deliver: return Deliver(context);
            case Operations.SessionWork: return Work(context);
            case Operations.AttentionRaise: return SessionAttention.Raise(context);
            case Operations.AttentionSuggest: return SessionAttention.Suggest(context);
            case Operations.AttentionClose: return SessionAttention.Close(context);
            case Operations.AttentionClaimed: return SessionAttention.Claimed(context);
        }
        var state = new SessionState(context.State);
        if (state.Closed) return state.PrincipalId == context.Caller.PrincipalId ? Error(SessionUnknown, new { expired = true, reopen = RelayTools.SessionOpen }) : Error(SessionDenied);
        if (!Authenticates(context, state)) return Error(SessionDenied);
        return context.Operation switch
        {
            Operations.Verify => Verify(context, state),
            RelayTools.GetStatus => Describe(context, state, Text(context.Args, "hubIncarnation"), verified: false),
            RelayTools.SessionBind => Bind(context, state),
            RelayTools.CheckInbox => Page(state.Inbox.Stored, context.Args),
            RelayTools.Ack => Acknowledge(context, state),
            RelayTools.SendMessage => SendDirect(context),
            Operations.StandingSeen => MarkStandingSeen(context, state),
            Operations.AttentionOffer => SessionAttention.Offer(context),
            Operations.AttentionSeen => MarkMessagesSeen(context, state),
            RelayTools.AttentionRespond => SessionAttention.Respond(context),
            _ => Error(UnknownTool)
        };
    }

    /// <summary>
    /// A board's work report. Boards report through their outboxes, without the session secret; an unknown session ignores them, and an
    /// expired one tells the board of new work it can no longer answer for.
    /// </summary>
    private static RelayReply Work(AggregateContext context)
    {
        if (!ActionWorkReport.Decode(context.Args).TryGet(out var report, out var malformed)) return Malformed(malformed);
        var state = new SessionState(context.State);
        return state.Closed ? LateWork(context, state, report) : !state.Opened ? Ok(new { tracked = false }) : TrackWork(context, state, report);
    }

    /// <summary>
    /// Copies the session's record on its first <see cref="SessionOpen"/> (the direct call's or the directory's, whichever lands first); a
    /// later one with the same secret is idempotent. The record's activity time is the directory's, never the session's own.
    /// <para>A session already closed stays closed: its close overtook this open (see <see cref="Expire"/>), or this is a late copy, or a
    /// retried <c>session_open</c> replaying the handle of a session that has closed since. The open is accepted as <c>expired</c> and
    /// changes nothing, so the directory's copy is not retried forever and no session opens that the directory no longer lists;
    /// <c>session_open</c> then answers <c>SessionUnknown</c>, and the chat opens a new session.</para>
    /// </summary>
    private static RelayReply Initialize(AggregateContext context)
    {
        if (!SessionOpen.Decode(context.Args).TryGet(out var open, out var malformed)) return Malformed(malformed);
        var state = new SessionState(context.State);
        if (state.Closed) return Ok(new { initialized = false, expired = true });
        var record = open.Record;
        if (state.Opened) return state.PrincipalId == record.PrincipalId && state.SessionToken == record.SessionToken ? Ok(new { initialized = true }) : Error(SessionDenied);
        state.Open(record, context.Now);
        if (state.Role is { Length: > RelayLimits.MaxActivityRoleLength }) state.Role = null;
        // The direct call from session_open carries only the client's fields; the control record's freshness defaults fill the rest.
        state.BindingVersion ??= 0;
        state.ObservedAt ??= context.Now;
        state.FreshUntil ??= context.Now.Add(ControlAggregate.InitialFreshness);
        state.Availability ??= "unknown";
        return Ok(new { initialized = true });
    }

    /// <summary>
    /// A board's report that this session now answers for an unresolved action, or no longer does (see <see cref="WorkLedger"/>). The hub's
    /// directory hears only when that changes, and once a day while it lasts (<see cref="SessionOwnership"/>), so a full directory never
    /// expires a session that owns work.
    /// </summary>
    private static RelayReply TrackWork(AggregateContext context, SessionState state, ActionWorkReport report)
    {
        var now = context.Now;
        var ledger = state.Work;
        var tracked = ledger.Apply(report.ActionKey, report.Open, WorkLedger.ReportedAt(report.At, now), now);
        var owns = ledger.Owns(now);
        // The board holding an action this session answers for, so a forced close can tell it.
        if (tracked && report.Open) state.WorkBoards.Remember(AggregateKinds.BoardKey(context.Repository, report.BoardId), report.BoardId);
        state.DropEmptyWork();
        var reported = state.WorkReported;
        var (wasOwning, reportedAt) = reported is null ? (false, DateTimeOffset.MinValue) : (reported.Owns, reported.At);
        if (owns != wasOwning || owns && reportedAt <= now.AddHours(-RelayLimits.SessionWorkRefreshHours))
        {
            // The sequence orders reports that share a second, which board times alone cannot.
            var seq = (reported?.Seq ?? 0) + 1;
            state.ReportedWork(owns, now, seq);
            context.Send(AggregateKinds.ControlKey, new SessionOwnership(context.Repository, state.SessionId, owns, now, seq));
        }
        return Ok(new { tracked, owns });
    }

    /// <summary>
    /// The hub's directory closed this session (idle, by an administrator's filter, or to make room). Its secret, inbox and attention are
    /// cleared; afterwards its own calls get <c>SessionUnknown</c> with <c>expired</c>, its chat opens a new session, and each board with a
    /// task targeting it is told (<see cref="Operations.SessionClosed"/>). Idempotent. A session never opened here is closed too: its close
    /// overtook its open, which then finds it closed rather than opening a session the directory has already dropped.
    /// <para>The directory decided from what it knew, which can lag what this session knows, so the session checks once more and refuses
    /// (see <see cref="KeepOpen"/>) when it answers for an unresolved action (a board's report that it began one reaches this session
    /// before the directory), or when the close is an idle one (<c>idle: true</c>) and its inbox holds unread mail, which expiry would delete.</para>
    /// </summary>
    private static RelayReply Expire(AggregateContext context)
    {
        if (!SessionExpire.Decode(context.Args).TryGet(out var expire, out var malformed)) return Malformed(malformed);
        var state = new SessionState(context.State);
        if (state.Closed) return Settled(context, expire, Ok(new { expired = true }));
        if (!state.Opened)
        {
            state.ClosedAt = context.Now;
            return Settled(context, expire, Ok(new { expired = true }));
        }
        var force = expire.Force;
        var owns = state.OwnsWork(context.Now);
        var mail = expire.Idle && state.UnreadCount > 0;
        // The directory decided from what it had heard (seenAt). Activity this session has reported since, still on its way there, means it
        // was working when it was closed, so it refuses as it does for unresolved work.
        var working = expire.SeenAt is { } seen && (state.ActiveReportedAt ?? DateTimeOffset.MinValue) > seen;
        if (!force && (owns || mail || working)) return KeepOpen(context, state, expire, owns, mail);
        if (force) state.ForceClosedBy = expire.RequestedBy;
        // Each board waiting on it hears once: those whose tasks target it, and, when it still answers for work (a forced close), those
        // holding that work, which then make it recoverable at once instead of after its claim's lease.
        var told = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (boardKey, boardId) in state.BoardsToTell(withWork: owns))
            if (told.Add(boardKey)) TellBoard(context, state, boardKey, boardId);
        ReturnUnread(context, state);
        state.Forget();
        state.ClosedAt = context.Now;
        return Settled(context, expire, Ok(new { expired = true }));
    }

    /// <summary>Acknowledges the close the directory fenced (<see cref="SessionExpire.CloseSeq"/>), so it stops waiting for this session's answer.</summary>
    private static RelayReply Settled(AggregateContext context, SessionExpire expire, RelayReply reply)
    {
        context.Send(AggregateKinds.ControlKey, new SessionExpired(context.Repository, expire.SessionId, expire.CloseSeq));
        return reply;
    }

    /// <summary>
    /// A board reports work to a session that has already closed: the report raced the close (a close by label lands before the board's
    /// report of an action just begun). The session can no longer answer for it, so it tells that board, which makes the action
    /// recoverable at once (<see cref="Operations.SessionClosed"/>). A report that work ended needs nothing.
    /// </summary>
    private static RelayReply LateWork(AggregateContext context, SessionState state, ActionWorkReport report)
    {
        if (!report.Open) return Ok(new { tracked = false });
        TellBoard(context, state, AggregateKinds.BoardKey(context.Repository, report.BoardId), report.BoardId);
        return Ok(new { tracked = false, toldBoard = true });
    }

    /// <summary>
    /// Refuses an expiry: the directory already dropped this session, so it sends its record back (<see cref="Operations.SessionRestore"/>),
    /// as the directory keeps it, with the work it answers for (and its report sequence, so the directory orders later reports after it) or
    /// the unread mail that holds it open. Nothing here changes; the chat never sees the close.
    /// </summary>
    private static RelayReply KeepOpen(AggregateContext context, SessionState state, SessionExpire expire, bool owns, bool mail)
    {
        // The directory's activity time is the last one this session reported.
        var record = state.Record with { ActiveAt = state.ActiveReportedAt };
        var seq = owns && state.WorkReported is { } reported ? reported.Seq : 0;
        context.Send(AggregateKinds.ControlKey, new SessionRestore(context.Repository, expire.SessionId, expire.CloseSeq, record, owns, seq, mail));
        return Ok(new { expired = false, keptFor = owns ? "work" : mail ? "mail" : "active" });
    }

    /// <summary>
    /// <see cref="Operations.SessionTargetWatch"/>: a board has a task targeting this session and asks to hear when it closes, so the task
    /// does not wait for a session that will never claim it. A closed session tells the board at once, including one closed before it
    /// opened (<see cref="Expire"/>). At most <see cref="RelayLimits.MaxSessionTargetBoards"/> boards are remembered, the oldest forgotten
    /// first. A session never opened or closed here is left alone (nothing names it but the target).
    /// </summary>
    private static RelayReply WatchTarget(AggregateContext context)
    {
        if (!SessionTargetWatch.Decode(context.Args).TryGet(out var watch, out var malformed)) return Malformed(malformed);
        var state = new SessionState(context.State);
        if (state.Closed) { TellBoard(context, state, watch.BoardKey, watch.BoardId); return Ok(new { watching = false, closed = true }); }
        if (!state.Opened) return Ok(new { watching = false });
        state.TargetBoards.Remember(watch.BoardKey, watch.BoardId);
        return Ok(new { watching = true });
    }

    private static void TellBoard(AggregateContext context, SessionState state, string boardKey, string boardId) =>
        context.Send(boardKey, new SessionClosed(context.Repository, boardId, state.SessionId is { Length: > 0 } self ? self : context.Key));

    /// <summary>
    /// A direct message this session cannot take (<paramref name="reason"/>: it is <c>closed</c>, was never opened (<c>unknown</c>), or its
    /// inbox is <c>full</c>) is never dropped silently: its sender gets a <c>delivery.failed</c> notice in its own inbox naming the message
    /// (<c>undeliveredMessageId</c>), this session (<c>recipientSessionId</c>) and the reason, so it can stop waiting for an answer. The
    /// notice keeps the message's <c>wake: false</c>; for a superseding message (a periodic status request) it supersedes this session's
    /// earlier notice, so a sender writing to a closed session on a timer keeps one notice. A board's task message has no sender session
    /// (it stays in the task conversation) and a notice is never returned, so notices cannot loop. Returns <paramref name="reply"/>, with
    /// <c>returned</c> when a notice was sent.
    /// </summary>
    private static RelayReply Undelivered(AggregateContext context, SessionState state, InboxMessage message, RelayReply reply, string reason)
    {
        if (!ReturnToSender(context, state, message, reason)) return reply;
        var data = JsonNode.Parse(reply.DataJson)!.AsObject();
        data["returned"] = true;
        return Ok(data);
    }

    /// <summary>
    /// Unread direct messages a closing session would drop with its inbox go back to their senders as <c>delivery.failed</c>
    /// (<c>unread</c>), so no sender waits for an answer that will never come. At most <see cref="RelayLimits.MaxMessagesReturnedOnClose"/>,
    /// oldest first; the rest are dropped with the inbox.
    /// </summary>
    private static void ReturnUnread(AggregateContext context, SessionState state)
    {
        var returned = 0;
        foreach (var stored in state.Unread)
        {
            if (returned == RelayLimits.MaxMessagesReturnedOnClose) break;
            if (InboxMessage.Decode(stored).TryGet(out var message, out _) && ReturnToSender(context, state, message, "unread")) returned++;
        }
    }

    /// <summary>Sends <paramref name="message"/>'s sender the notice for <paramref name="reason"/>; false when it has no sender session or is itself a notice.</summary>
    private static bool ReturnToSender(AggregateContext context, SessionState state, InboxMessage message, string reason)
    {
        if (message.SenderSessionId is not { Length: > 0 } sender || message.Kind == InboxMessage.UndeliveredKind) return false;
        var id = message.MessageId;
        var recipient = state.SessionId is { Length: > 0 } self ? self : context.Key;
        var notice = new InboxMessage
        {
            // Derived from the message, so a redelivery of it returns the same notice and the sender's inbox keeps one.
            MessageId = "undelivered-" + Retention.Hash(id), RepositoryId = context.Repository, Kind = InboxMessage.UndeliveredKind,
            Text = "Message " + id + " to session " + recipient + " was not " + (reason == "unread" ? "read: that session closed first." : "delivered: " + reason switch
            {
                "closed" => "that session is closed.", "unknown" => "no such session was opened.", _ => "that session's inbox is full."
            }),
            UndeliveredMessageId = id, RecipientSessionId = recipient, Reason = reason, SentAt = context.Now,
            // The notice keeps the message's wake: false. Superseding is keyed by the sender, so only then does the notice name the
            // closed session as its own.
            Wake = message.Wake, Supersede = message.Supersede, SenderSessionId = message.Supersede ? recipient : null
        };
        context.Send(sender, new MessageDelivery(notice));
        return true;
    }

    private static RelayReply Deliver(AggregateContext context)
    {
        if (!MessageDelivery.Decode(context.Args).TryGet(out var delivery, out var malformed)) return Malformed(malformed);
        var (state, message) = (new SessionState(context.State), delivery.Message);
        if (state.Closed) return Undelivered(context, state, message, Ok(new { expired = true }), "closed");
        // Never opened: a sender named a session that doesn't exist. Its sender is told; a refusal would be retried forever.
        if (!state.Opened) return Undelivered(context, state, message, Ok(new { delivered = false, unknown = true }), "unknown");
        var id = message.MessageId.Length <= RelayLimits.MaxIdentifierLength ? message.MessageId : throw new ArgumentException("messageId is required and at most 256 characters.");
        var inbox = state.Inbox;
        if (inbox.Holds(id) || state.Acked.Holds(id)) return Ok(new { duplicate = true });
        // A superseding message replaces its sender's earlier unread superseding message, so a periodic sender (a status request, a
        // heartbeat) leaves one current entry rather than a pile a busy chat must read through. The replaced one counts as acknowledged
        // (the acknowledged set is bounded like explicit acknowledgments), so a late redelivery of it is ignored.
        var superseded = 0;
        if (message.Supersede && message.SenderSessionId is { Length: > 0 } sender)
            foreach (var old in inbox.SupersedingFrom(sender))
            {
                inbox.Remove(old); state.Acked.Add(old, context.Now); superseded++;
                SessionAttention.CloseMessages(state, [old], context.Now);
            }
        // A full inbox (a chat that stopped reading) drops the message: a refusal would be retried forever and stall the sender's outbox.
        // The sender is told instead.
        if (inbox.Count >= RelayLimits.MaxRecords) return Undelivered(context, state, message, Ok(new { delivered = false, full = true }), "full");
        inbox.Add(message);
        // With attention on, the dispatcher passes the item lifetime: a message that may wake its chat is then an info item.
        var infoDays = (int)Math.Min(delivery.AttentionExpiryDays ?? 0, int.MaxValue);
        if (infoDays > 0 && message.Wake) SessionAttention.AddMessage(state, message, infoDays, context.Now);
        return Ok(new { delivered = true, superseded });
    }

    private static bool Authenticates(AggregateContext context, SessionState state) =>
        state.PrincipalId == context.Caller.PrincipalId && SecretEquals(state.SessionToken, Text(context.Args, "sessionToken"));

    private static RelayReply Verify(AggregateContext context, SessionState state)
    {
        if (!SessionVerify.Decode(context.Args).TryGet(out var verify, out var malformed)) return Malformed(malformed);
        // Every verified call keeps the session's directory entry fresh, even with attention off or on attention: false calls.
        SessionAttention.ReportActive(context);
        return Describe(context, state, verify.HubIncarnation ?? "", verified: true);
    }

    /// <summary>
    /// The session as <c>get_status</c> and the hub's verification read it: its binding (current only for the caller's
    /// <paramref name="hubIncarnation"/> while still fresh), and, for the hub's own <paramref name="verified"/> check only, its role, machine
    /// and standing revisions.
    /// </summary>
    private static RelayReply Describe(AggregateContext context, SessionState state, string hubIncarnation, bool verified)
    {
        var current = state.FreshUntil > context.Now && state.HubIncarnation == hubIncarnation;
        var description = new JsonObject
        {
            ["sessionId"] = state.SessionId,
            ["bindingVersion"] = state.BindingVersion ?? 0,
            ["binding"] = state.Binding?.DeepClone(),
            ["availability"] = current ? state.Availability ?? "" : "unknown",
            ["observedAt"] = state.ObservedAt?.ToString("O") ?? ""
        };
        // The role reported at session_open labels this session's activity; only the hub's internal verification sees it.
        if (verified && state.Role is { Length: > 0 } role) description["role"] = role;
        // The machine given at session_open resolves this session's resource aliases (next-work lookups); also internal only.
        if (verified && state.MachineId is { Length: > 0 } machine) description["machineId"] = machine;
        // The standing revision this session last received per board, so replies carry standing only when it changed; internal only.
        if (verified && state.StandingSeenCopy is { } seen) description["standingSeen"] = seen;
        // The hub asks for the attention count (and, for the adapter, the top item and wake candidate) with an internal argument.
        if (!verified && Text(context.Args, "attentionStatus") is "count" or "detail")
            description["attention"] = SessionAttention.Status(state, context.Args, context.Now, Text(context.Args, "attentionStatus") == "detail");
        return Ok(description);
    }

    private static RelayReply MarkMessagesSeen(AggregateContext context, SessionState state)
    {
        if (!AttentionSeen.Decode(context.Args).TryGet(out var seen, out var malformed)) return Malformed(malformed);
        SessionAttention.CloseMessages(state, seen.MessageIds, context.Now);
        return Ok(new { seen = true });
    }

    /// <summary>Records that this session received a board's standing at a revision (<see cref="StandingSeen"/>). A revision never moves back.</summary>
    private static RelayReply MarkStandingSeen(AggregateContext context, SessionState state)
    {
        if (!StandingSeen.Decode(context.Args).TryGet(out var received, out var malformed)) return Malformed(malformed);
        var seen = state.StandingSeen;
        var (board, revision) = (received.BoardId, received.StandingRevision);
        if (!seen.Holds(board) && seen.Count >= RelayLimits.MaxStandingBoardsPerSession) return Ok(new { recorded = false });
        if (revision > seen.Of(board)) seen.Set(board, revision);
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
    private static RelayReply Bind(AggregateContext context, SessionState state)
    {
        var args = context.Args;
        var current = state.BindingVersion ?? 0;
        if (!ExpectedVersionMatches(args, current)) return VersionConflict(current);
        // A metadata-only bind (a rename, or a missing machine reported later) leaves out binding and keeps the current one.
        if (args["binding"] is { } binding) state.Binding = binding;
        state.BindingVersion = current + 1;
        if (Text(args, "label") is { Length: > 0 } label) state.Label = label;
        if (Text(args, "machineId") is { Length: > 0 } machine && state.MachineId.Length == 0) state.MachineId = machine;
        state.ObservedAt = context.Now;
        state.FreshUntil = context.Now.AddSeconds(Math.Clamp(Number(args, "freshnessSeconds", DefaultFreshnessSeconds), MinFreshnessSeconds, MaxFreshnessSeconds));
        if (Text(args, "availability") is { Length: > 0 } availability) state.Availability = availability;
        else state.Availability ??= "unknown";
        state.HubIncarnation = Text(args, "hubIncarnation");
        context.Send(AggregateKinds.ControlKey, new SessionRegistration(context.Repository, state.SessionId, state.Record.Presence));
        return Ok(new { bindingVersion = current + 1 }, current + 1);
    }

    /// <summary>
    /// Acknowledgment is explicit: a later duplicate delivery of an acknowledged message is ignored. The acknowledged set keeps each ID for
    /// the retry window (<see cref="RelayLimits.RequestReplayRetentionHours"/>), and a full set drops its oldest, so acknowledging never stops working.
    /// </summary>
    private static RelayReply Acknowledge(AggregateContext context, SessionState state)
    {
        var ids = context.Args["messageIds"] as JsonArray ?? new JsonArray(Text(context.Args, "messageId"));
        var acknowledged = 0;
        var acked = state.Acked;
        foreach (var id in Strings(ids))
        {
            if (state.Inbox.Remove(id)) acknowledged++;
            acked.Add(id, context.Now);
        }
        SessionAttention.CloseMessages(state, Strings(ids), context.Now);
        return Ok(new { acknowledged });
    }

    private static RelayReply SendDirect(AggregateContext context)
    {
        var args = context.Args;
        var recipient = Required(args, "recipientSessionId");
        var id = Guid.NewGuid().ToString("N");
        var message = new InboxMessage
        {
            MessageId = id, RepositoryId = context.Repository, Text = MessageText(args), Author = context.Caller.PrincipalId, SenderSessionId = context.SessionId,
            SentAt = context.Now, ReplyTo = Text(args, "replyTo") is { Length: > 0 } replyTo ? replyTo : null, Supersede = Flag(args, "supersede"),
            // A status or FYI message is stored like any other but never wakes the recipient; it is read with its next wake or inbox check.
            Wake = IsNotFalse(args, "wake")
        };
        // Measured as the recipient's check_inbox returns it, so a message escaping would make unreadable is never accepted.
        if (CheckReadable(message.ToJson()) is { } unreadable) return unreadable;
        context.Send(recipient, new MessageDelivery(message));
        return Ok(new { messageId = id, delivery = "pending" });
    }

    public static string MessageText(JsonObject args)
    {
        var text = Text(args, "text");
        return text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= RelayLimits.MaxReportBytes ? text : throw new ArgumentException("Message text must contain 1 to 8192 UTF-8 bytes.");
    }
}

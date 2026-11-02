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
        if (context.Operation == Operations.Deliver) return Deliver(context);
        // Attention items arrive through other aggregates' outboxes, which carry no session secret, just like deliveries.
        if (context.Operation == Operations.AttentionRaise) return SessionAttention.Raise(context);
        if (context.Operation == Operations.AttentionClose) return SessionAttention.Close(context);
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
        foreach (var property in context.Args) if (property.Key != "requestId") state[property.Key] = property.Value?.DeepClone();
        return Ok(new { initialized = true });
    }

    private static RelayReply Deliver(AggregateContext context)
    {
        var state = context.State;
        if (!state.ContainsKey("principalId")) return Error(SessionUnknown);
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
        if (inbox.Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
        // With attention on, the dispatcher passes the item lifetime: a message that may wake its chat is then an info item.
        var message = context.Args.DeepClone().AsObject();
        var infoDays = message["attentionExpiryDays"] is JsonValue days && days.TryGetValue<int>(out var lifetime) ? lifetime : 0;
        message.Remove("attentionExpiryDays");
        inbox[id] = message;
        if (infoDays > 0 && !(message["wake"] is JsonValue wake && wake.TryGetValue<bool>(out var wakes) && !wakes)) SessionAttention.AddMessage(state, message, infoDays, context.Now);
        return Ok(new { delivered = true, superseded });
    }

    private static bool Authenticates(AggregateContext context) =>
        Text(context.State, "principalId") == context.Caller.PrincipalId && SecretEquals(Text(context.State, "sessionToken"), Text(context.Args, "sessionToken"));

    private static RelayReply Describe(AggregateContext context)
    {
        var state = context.State;
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

    private static RelayReply Bind(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        var current = Number(state, "bindingVersion");
        if (!ExpectedVersionMatches(args, current)) return VersionConflict(current);
        state["binding"] = args["binding"]?.DeepClone(); state["bindingVersion"] = current + 1;
        state["observedAt"] = context.Now.ToString("O");
        state["freshUntil"] = context.Now.AddSeconds(Math.Clamp(Number(args, "freshnessSeconds", DefaultFreshnessSeconds), MinFreshnessSeconds, MaxFreshnessSeconds)).ToString("O");
        state["availability"] = Text(args, "availability", "unknown");
        state["hubIncarnation"] = Text(args, "hubIncarnation");
        var registration = state.DeepClone().AsObject();
        foreach (var secretOrPrivate in new[] { "sessionToken", "requests", "requestTombstones", "inbox", "acked", "attention", "attentionClosed", "attentionRecent", "attentionSeq", "attentionBoundaryAt", "activeReportedAt" }) registration.Remove(secretOrPrivate);
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
        if (args["wake"] is JsonValue wake && wake.TryGetValue<bool>(out var wakes) && !wakes) message["wake"] = false;
        context.Emit(AggregateKinds.Session, recipient, Operations.Deliver, message);
        return Ok(new { messageId = id, delivery = "pending" });
    }

    public static string MessageText(JsonObject args)
    {
        var text = Text(args, "text");
        return text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= RelayLimits.MaxReportBytes ? text : throw new ArgumentException("Message text must contain 1 to 8192 UTF-8 bytes.");
    }
}

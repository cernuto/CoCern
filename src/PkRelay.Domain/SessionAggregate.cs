using System.Text;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>One chat's private session: its secret, reported tool binding, durable inbox and acknowledgments.</summary>
internal static class SessionAggregate
{
    private const int MinFreshnessSeconds = 1;
    private const int MaxFreshnessSeconds = 600;
    private const int DefaultFreshnessSeconds = 120;

    public static RelayReply Handle(AggregateContext context)
    {
        if (context.Operation == Operations.Open) return Initialize(context);
        if (context.Operation == Operations.Deliver) return Deliver(context);
        if (!Authenticates(context)) return Error(SessionDenied);
        return context.Operation switch
        {
            Operations.Verify or RelayTools.GetStatus => Describe(context),
            RelayTools.SessionBind => Bind(context),
            RelayTools.CheckInbox => Page(ObjectAt(context.State, "inbox").Select(x => x.Value!), context.Args),
            RelayTools.Ack => Acknowledge(context),
            RelayTools.SendMessage => SendDirect(context),
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
                // The acknowledged set is bounded like explicit acknowledgments; once full, older messages are simply kept.
                if (ObjectAt(state, "acked").Count >= RelayLimits.MaxRecords) break;
                inbox.Remove(old); ObjectAt(state, "acked")[old] = true; superseded++;
            }
        if (inbox.Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
        inbox[id] = context.Args.DeepClone();
        return Ok(new { delivered = true, superseded });
    }

    private static bool Authenticates(AggregateContext context) =>
        Text(context.State, "principalId") == context.Caller.PrincipalId && SecretEquals(Text(context.State, "sessionToken"), Text(context.Args, "sessionToken"));

    private static RelayReply Describe(AggregateContext context)
    {
        var state = context.State;
        var current = Date(state, "freshUntil") > context.Now && Text(state, "hubIncarnation") == Text(context.Args, "hubIncarnation");
        return Ok(new
        {
            sessionId = Text(state, "sessionId"),
            bindingVersion = Number(state, "bindingVersion"),
            binding = state["binding"],
            availability = current ? Text(state, "availability") : "unknown",
            observedAt = Text(state, "observedAt")
        });
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
        foreach (var secretOrPrivate in new[] { "sessionToken", "requests", "inbox", "acked" }) registration.Remove(secretOrPrivate);
        context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.Register, registration);
        return Ok(new { bindingVersion = current + 1 }, current + 1);
    }

    /// <summary>Acknowledgment is explicit and permanent: a later duplicate delivery of an acknowledged message is ignored.</summary>
    private static RelayReply Acknowledge(AggregateContext context)
    {
        var ids = context.Args["messageIds"] as JsonArray ?? new JsonArray(Text(context.Args, "messageId"));
        var acknowledged = 0;
        foreach (var id in Strings(ids))
        {
            if (!ObjectAt(context.State, "acked").ContainsKey(id) && ObjectAt(context.State, "acked").Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
            if (ObjectAt(context.State, "inbox").Remove(id)) acknowledged++;
            ObjectAt(context.State, "acked")[id] = true;
        }
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
        context.Emit(AggregateKinds.Session, recipient, Operations.Deliver, message);
        return Ok(new { messageId = id, delivery = "pending" });
    }

    public static string MessageText(JsonObject args)
    {
        var text = Text(args, "text");
        return text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= RelayLimits.MaxReportBytes ? text : throw new ArgumentException("Message text must contain 1 to 8192 UTF-8 bytes.");
    }
}

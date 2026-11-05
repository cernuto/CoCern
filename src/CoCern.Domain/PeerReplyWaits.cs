using System.Text.Json.Nodes;
using CoCern.Domain.Messages;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

// Private provenance, written only by authenticated direct sends and accepted
// outbox deliveries. Labels, principals and caller-supplied baselines prove none
// of these relationships. Missing/evicted provenance fails registration closed.
internal static class PeerReplyWaits
{
    internal const string Key = "eventWaitPeerMessages";
    private const int Capacity = 256;

    internal static void Sent(AggregateContext context, string id, string recipient)
    {
        var ledger = context.State[Key] as JsonObject;
        if (ledger is null) context.State[Key] = ledger = new JsonObject();
        if (ledger.Count >= Capacity)
            ledger.Remove(ledger.OrderBy(x => x.Value!["sentAt"]!.GetValue<string>(), StringComparer.Ordinal).First().Key);
        ledger[id] = new JsonObject { ["recipient"] = recipient, ["sentAt"] = context.Now.ToString("O") };
    }

    internal static RelayReply? Validate(AggregateContext context, EventWaitRequest request)
    {
        if (request.Kind != "peer_reply") return null;
        // A retained wait already contains the immutable proof established at
        // creation; an ordinary send-ledger eviction cannot break its retry.
        if (new EventWaitState(context.State).Entries?[request.WaitId] is JsonObject prior
            && EventWaitState.Request(prior) == request) return null;
        return request.Target == request.ReplyTo
            && context.State[Key]?[request.ReplyTo ?? ""] is JsonObject sent
            && sent["recipient"]?.GetValue<string>() == request.PeerSessionId
            ? null : Error("ReplyNotOwned");
    }

    internal static void Armed(AggregateContext context, EventWaitRequest request)
    {
        if (request.Kind != "peer_reply") return;
        var entry = new EventWaitState(context.State).Entries![request.WaitId]!.AsObject();
        entry["peerArmedAt"] = context.Now.ToString("O");
        entry["alreadySatisfied"] = context.State[Key]?[request.ReplyTo!]?["replyAt"] is not null;
    }

    internal static void Delivered(AggregateContext context, InboxMessage message)
    {
        if (message.RepositoryId != context.Repository || message.Kind is not null || message.TaskId is not null
            || message.ReplyTo is not { Length: > 0 } original || message.SenderSessionId is not { Length: > 0 } sender
            || message.SentAt is not { } sentAt) return;
        if (context.State[Key]?[original] is JsonObject sent && sent["recipient"]?.GetValue<string>() == sender)
            sent["replyAt"] = context.Now.ToString("O");
        if (new EventWaitState(context.State).Entries is not { } waits) return;
        foreach (var entry in waits.Select(x => x.Value).OfType<JsonObject>())
        {
            if (entry["terminal"] is not null) continue;
            var request = EventWaitState.Request(entry);
            if (request.Kind != "peer_reply" || request.PeerSessionId != sender || request.ReplyTo != original
                || entry["peerArmedAt"] is not JsonValue armed
                || !DateTimeOffset.TryParse(armed.GetValue<string>(), out var armedAt) || sentAt <= armedAt) continue;
            EventWaitAggregate.Terminal(context, entry, request,
                request.ExpiresAt <= context.Now ? "expired" : "satisfied", "");
        }
    }
}

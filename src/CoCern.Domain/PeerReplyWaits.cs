using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>Private source binding; never a public tool argument.</summary>
public sealed record PeerReplyArm(string RepositoryId, string OwnerSessionId, string WaitId, string WatchId,
    string PeerSessionId, string OriginalMessageId, DateTimeOffset ExpiresAt);

/// <summary>Authenticated direct-send provenance and durable source order, independent of wall-clock order.</summary>
public static class PeerReplyWaits
{
    public const string ArmOperation = "__event_wait_peer_arm";
    public const string AckOperation = "__event_wait_peer_ack";
    internal const string Key = "eventWaitPeerMessages";
    internal const string SequenceKey = "eventWaitPeerSequence";
    internal const string ArmsKey = "eventWaitPeerArms";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static long Sent(AggregateContext context, string id, string recipient)
    {
        var sequence = context.State[SequenceKey]?.GetValue<long>() ?? 0;
        if (sequence < 0 || sequence == long.MaxValue) throw new InvalidOperationException("Invalid peer sequence.");
        context.State[SequenceKey] = ++sequence;
        var ledger = context.State[Key] as JsonObject;
        if (ledger is null) context.State[Key] = ledger = new JsonObject();
        if (ledger.Count >= 256) ledger.Remove(ledger.First().Key);
        ledger[id] = new JsonObject { ["recipient"] = recipient };
        return sequence;
    }

    internal static RelayReply? Validate(AggregateContext context, EventWaitRequest request)
    {
        if (request.Kind != "peer_reply") return null;
        if (new EventWaitState(context.State).Entries?[request.WaitId] is JsonObject prior
            && EventWaitState.Request(prior) == request) return null;
        return request.Target == request.ReplyTo && context.State[Key]?[request.ReplyTo ?? ""] is JsonObject sent
            && sent["recipient"]?.GetValue<string>() == request.PeerSessionId ? null : Error("ReplyNotOwned");
    }

    public static PeerReplyArm CreateArm(string repository, string owner, EventWaitRequest request)
    {
        var identity = JsonSerializer.Serialize(new[] { repository, owner, request.WaitId, request.PeerSessionId });
        return new(repository, owner, request.WaitId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
            request.PeerSessionId!, request.ReplyTo!, request.ExpiresAt);
    }

    internal static RelayReply Armed(AggregateContext context, EventWaitRequest request)
    {
        if (request.Kind != "peer_reply") return Ok(new { armed = false });
        var entry = new EventWaitState(context.State).Entries![request.WaitId]!.AsObject();
        var arm = CreateArm(context.Repository, context.Key, request);
        entry["peerWatch"] = JsonSerializer.SerializeToNode(arm, Json);
        entry["alreadySatisfied"] = context.State[Key]?[request.ReplyTo!]?["replied"]?.GetValue<bool>() == true;
        if (arm.PeerSessionId == context.Key) return Arm(context, arm); // Never RPC a non-reentrant grain to itself.
        context.Emit(AggregateKinds.Session, arm.PeerSessionId, ArmOperation, JsonSerializer.SerializeToNode(arm, Json)!.AsObject());
        return Ok(new { pending = true });
    }

    internal static RelayReply Handle(AggregateContext context)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        try
        {
            var arm = (context.Operation == ArmOperation ? context.Args : context.Args["arm"])?.Deserialize<PeerReplyArm>(Json);
            if (!Valid(arm) || arm!.RepositoryId != context.Repository) return Error("InvalidArguments");
            return context.Operation == ArmOperation ? Arm(context, arm) : Accept(context, arm,
                context.Args["baseline"]?.GetValue<long>() ?? -1, context.Args["targetGone"]?.GetValue<bool>() ?? false);
        }
        catch (JsonException) { return Error("MalformedEffect"); }
    }

    private static RelayReply Arm(AggregateContext context, PeerReplyArm arm)
    {
        if (arm.PeerSessionId != context.Key) return Error("InvalidArguments");
        if (arm.ExpiresAt <= context.Now) return Ok(new { expired = true });
        if (arm.ExpiresAt - context.Now > TimeSpan.FromHours(1)) return Error("InvalidArguments");
        var source = new SessionState(context.State);
        var gone = !source.Opened || source.Closed;
        var baseline = context.State[SequenceKey]?.GetValue<long>() ?? 0;
        if (baseline < 0) return Error("InvalidArguments");
        if (!gone)
        {
            var map = context.State[ArmsKey] as JsonObject;
            if (map is null) context.State[ArmsKey] = map = new JsonObject();
            foreach (var key in map.Where(x => x.Value!["arm"]!.Deserialize<PeerReplyArm>(Json)!.ExpiresAt <= context.Now).Select(x => x.Key).ToArray()) map.Remove(key);
            if (map[arm.WatchId] is JsonObject prior)
            {
                if (prior["arm"]!.Deserialize<PeerReplyArm>(Json) != arm) return Error("SubscriptionMismatch");
                baseline = prior["baseline"]!.GetValue<long>();
            }
            else
            {
                // Never evict an unexpired snapshot and recapture a later baseline on retry.
                if (map.Count >= 128) return Error("CapacityExceeded");
                map[arm.WatchId] = new JsonObject { ["arm"] = JsonSerializer.SerializeToNode(arm, Json), ["baseline"] = baseline };
            }
        }
        if (arm.OwnerSessionId == context.Key) return Accept(context, arm, baseline, gone);
        context.Emit(AggregateKinds.Session, arm.OwnerSessionId, AckOperation, new JsonObject
        {
            ["repositoryId"] = context.Repository, ["arm"] = JsonSerializer.SerializeToNode(arm, Json), ["baseline"] = baseline, ["targetGone"] = gone
        });
        return Ok(new { armed = !gone });
    }

    private static RelayReply Accept(AggregateContext context, PeerReplyArm arm, long baseline, bool gone)
    {
        if (arm.OwnerSessionId != context.Key || baseline < 0) return Error("InvalidArguments");
        if (new EventWaitState(context.State).Entries?[arm.WaitId] is not JsonObject entry) return Ok(new { unknown = true });
        if (entry["peerWatch"]?.Deserialize<PeerReplyArm>(Json) != arm) return Error("SubscriptionMismatch");
        if (entry["terminal"] is not null) return Ok(new { handled = true });
        var request = EventWaitState.Request(entry);
        if (request.ExpiresAt <= context.Now || gone)
        {
            EventWaitAggregate.Terminal(context, entry, request, request.ExpiresAt <= context.Now ? "expired" : "target_gone", "");
            return Ok(new { handled = true });
        }
        if (entry["peerBaseline"] is JsonValue previous && previous.GetValue<long>() != baseline) return Error("SubscriptionMismatch");
        entry["peerBaseline"] = baseline;
        if (entry["pendingPeerSequence"] is JsonValue pending) Qualify(context, entry, request, pending.GetValue<long>());
        entry.Remove("pendingPeerSequence");
        return Ok(new { handled = true });
    }

    internal static void Delivered(AggregateContext context, MessageDelivery delivery)
    {
        var message = delivery.Message;
        if (delivery.PeerReplySequence is not > 0 || message.RepositoryId != context.Repository || message.Kind is not null || message.TaskId is not null
            || message.ReplyTo is not { Length: > 0 } original || message.SenderSessionId is not { Length: > 0 } sender) return;
        var sequence = delivery.PeerReplySequence.Value;
        if (context.State[Key]?[original] is JsonObject sent && sent["recipient"]?.GetValue<string>() == sender) sent["replied"] = true;
        if (new EventWaitState(context.State).Entries is not { } waits) return;
        foreach (var entry in waits.Select(x => x.Value).OfType<JsonObject>())
        {
            if (entry["terminal"] is not null) continue;
            var request = EventWaitState.Request(entry);
            if (request.Kind != "peer_reply" || request.PeerSessionId != sender || request.ReplyTo != original || entry["peerWatch"] is null) continue;
            if (request.ExpiresAt <= context.Now) EventWaitAggregate.Terminal(context, entry, request, "expired", "");
            else if (entry["peerBaseline"] is not null) Qualify(context, entry, request, sequence);
            else entry["pendingPeerSequence"] = Math.Max(sequence, entry["pendingPeerSequence"]?.GetValue<long>() ?? 0);
        }
    }

    private static void Qualify(AggregateContext context, JsonObject entry, EventWaitRequest request, long sequence)
    {
        if (sequence > entry["peerBaseline"]!.GetValue<long>()) EventWaitAggregate.Terminal(context, entry, request, "satisfied", "");
        else entry["alreadySatisfied"] = true;
    }

    private static bool Valid(PeerReplyArm? arm) => arm is not null
        && new[] { arm.RepositoryId, arm.OwnerSessionId, arm.WaitId, arm.WatchId, arm.PeerSessionId, arm.OriginalMessageId }
            .All(value => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate));
}

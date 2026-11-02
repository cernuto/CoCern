using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;

namespace CoCern.Core;

/// <summary>
/// The outbox's delivery rules, kept apart from the grain's I/O loop: which effects a turned-off feature drops, what a delivery adds to its
/// payload, whether a failure is retried, dropped, or recorded as a standing order's failed run, and which deliveries count as wakes.
/// </summary>
internal static class EffectDelivery
{
    public enum Failure { Retry, Drop, DropAsFailedOrderRun }

    /// <summary>With attention off, items are neither stored nor routed; with standing orders off, order effects are dropped.</summary>
    public static bool Dropped(DurableEffect effect, AttentionOptions? attention, bool ordersOn) =>
        Operations.IsAttention(effect.Operation) && attention is { Enabled: false } || Operations.IsOrder(effect.Operation) && !ordersOn;

    /// <summary>
    /// A raised item takes the hub's lifetime unless its source set one. With attention on, a delivered message is an info item: it waits
    /// for a boundary or the idle-hold wake instead of waking its chat.
    /// </summary>
    public static void Prepare(DurableEffect effect, JsonObject payload, AttentionOptions? attention)
    {
        if (effect.Operation == Operations.AttentionRaise && !payload.ContainsKey("expiryDays") && !payload.ContainsKey("expiresAt") && attention is not null)
            payload["expiryDays"] = attention.ItemExpiryDays;
        if (effect.Operation == Operations.Deliver && effect.TargetKind == AggregateKinds.Session && attention is not { Enabled: false })
            payload["attentionExpiryDays"] = attention?.ItemExpiryDays ?? RelayLimits.DefaultAttentionExpiryDays;
    }

    /// <summary>Failures a later attempt can succeed past: the target's storage or the grain call itself failed.</summary>
    public static bool IsTransient(string code) =>
        code is RelayErrorCodes.StorageFailure or RelayErrorCodes.DeliveryUnavailable or RelayErrorCodes.DeliveryPending or RelayErrorCodes.Unavailable;

    /// <summary>
    /// What a refused delivery becomes. Infrastructure failures retry. Attention and order effects are advisory: a refusal would repeat on
    /// every retry, so they are dropped, and a standing order's item its session refused also counts as a failed run of that order.
    /// </summary>
    public static Failure OnFailure(DurableEffect effect, JsonObject payload, string code, string sourceKind, bool ordersOn)
    {
        if (IsTransient(code) || !(Operations.IsAttention(effect.Operation) || Operations.IsOrder(effect.Operation))) return Failure.Retry;
        return ordersOn && sourceKind == AggregateKinds.Board && effect.Operation == Operations.AttentionRaise && JsonFields.Text(payload, Operations.OrderIdField).Length > 0
            ? Failure.DropAsFailedOrderRun : Failure.Drop;
    }

    /// <summary>Whether a delivered raise stored a new item whose tier may wake a chat.</summary>
    public static bool UrgentRaise(JsonObject payload, RelayReply result)
    {
        var data = JsonNode.Parse(result.DataJson) as JsonObject;
        if (data?["raised"] is not JsonValue raised || !raised.TryGetValue<bool>(out var isRaised) || !isRaised || data["refreshed"] is not JsonValue refreshed || !refreshed.TryGetValue<bool>(out var isRefresh) || isRefresh) return false;
        var blocking = payload["blocking"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        return AttentionWeights.Wakes(AttentionWeights.Tier(JsonFields.Text(payload, "kind"), JsonFields.Text(payload, "blocker") is { Length: > 0 } blocker ? blocker : null, blocking));
    }
}

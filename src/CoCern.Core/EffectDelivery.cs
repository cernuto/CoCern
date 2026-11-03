using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;

namespace CoCern.Core;

/// <summary>
/// The outbox's delivery rules, kept apart from the grain's I/O loop: which effects a turned-off feature drops, what a delivery adds to its
/// payload, whether a failure is retried, dropped (and counted), or recorded as a standing order's failed run, how a malformed effect is
/// logged, and which deliveries count as wakes.
/// </summary>
internal static class EffectDelivery
{
    public enum Failure { Retry, Drop, DropAsFailedOrderRun, DropAndCount }

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
    /// What a refused delivery becomes. Infrastructure failures retry. A typed message's own delivery class decides any other refusal: a
    /// state transfer retries, and a report its sender sends again whole is dropped and counted (<see cref="EffectMessages"/>). Of the
    /// untyped effects, attention and order effects are advisory: a refusal would repeat on every retry, so they are dropped, and a standing
    /// order's item its session refused also counts as a failed run of that order. Any other refusal retries.
    /// </summary>
    public static Failure OnFailure(DurableEffect effect, JsonObject payload, string code, string sourceKind, bool ordersOn)
    {
        // The hub blocks each lock itself when an action begins; the board's own copy refused as busy or unclaimed never runs, and would otherwise retry forever.
        if (effect.Operation == ActionBlock.Operation && ActionBlock.SettledBy.Contains(code)) return Failure.Drop;
        if (IsTransient(code)) return Failure.Retry;
        if (EffectMessages.DeliveryOf(effect.Operation) is { } delivery) return delivery == DeliveryClass.Retry ? Failure.Retry : Failure.DropAndCount;
        if (!(Operations.IsAttention(effect.Operation) || Operations.IsOrder(effect.Operation))) return Failure.Retry;
        return ordersOn && sourceKind == AggregateKinds.Board && effect.Operation == Operations.AttentionRaise && JsonFields.Text(payload, Operations.OrderIdField).Length > 0
            ? Failure.DropAsFailedOrderRun : Failure.Drop;
    }

    /// <summary>
    /// Logs a malformed effect, once (the grain keeps which it logged): the operation, where it came from and went, its ID and the names of
    /// its missing or invalid fields, and what happens to it. Never a payload value: an open or a restore carries the session's secret. A
    /// name that does not look like a field name is shown as <c>?</c>, so not even a handler's mistake can put a value in the log.
    /// </summary>
    public static void LogMalformed(ILogger logger, string sourceKind, string sourceKey, DurableEffect effect, RelayReply reply, Failure failure) =>
        logger.LogError("Malformed {Operation} effect {EffectId} from {SourceKind} {SourceKey} to {TargetKind} {TargetKey}: missing or invalid {Fields}. {Handling}",
            effect.Operation, effect.Id, sourceKind, sourceKey, effect.TargetKind, effect.TargetKey, string.Join(", ", MalformedFields(reply)),
            failure == Failure.Retry ? "It is retried until its sender is fixed, and the effects queued behind it wait." : "It was dropped and counted.");

    /// <summary>The field names a <see cref="RelayErrorCodes.MalformedEffect"/> reply lists, each shown only if it looks like a field name.</summary>
    internal static IReadOnlyList<string> MalformedFields(RelayReply reply)
    {
        try
        {
            return JsonNode.Parse(reply.DataJson)?["fields"] is JsonArray fields
                ? fields.Select(field => field is JsonValue value && value.TryGetValue<string>(out var name) && IsFieldName(name) ? name : "?").ToArray()
                : [];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>Letters and dots, and short: every message field name is one, and no secret, ID or time could pass for one.</summary>
    private static bool IsFieldName(string name) => name.Length is > 0 and <= RelayLimits.MaxLoggedFieldNameLength && name.All(c => char.IsAsciiLetter(c) || c == '.');

    /// <summary>Whether a delivered raise stored a new item whose tier may wake a chat.</summary>
    public static bool UrgentRaise(JsonObject payload, RelayReply result)
    {
        var data = JsonNode.Parse(result.DataJson) as JsonObject;
        if (data?["raised"] is not JsonValue raised || !raised.TryGetValue<bool>(out var isRaised) || !isRaised || data["refreshed"] is not JsonValue refreshed || !refreshed.TryGetValue<bool>(out var isRefresh) || isRefresh) return false;
        var blocking = payload["blocking"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        return AttentionWeights.Wakes(AttentionWeights.Tier(JsonFields.Text(payload, "kind"), JsonFields.Text(payload, "blocker") is { Length: > 0 } blocker ? blocker : null, blocking));
    }
}

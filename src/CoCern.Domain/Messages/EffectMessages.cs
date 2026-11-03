using System.Collections.Frozen;

namespace CoCern.Domain.Messages;

/// <summary>
/// Every typed message, by its operation: what reads messages by name instead of by type. The outbox takes a refused delivery's fate from
/// it, and a test keeps every internal operation either here or on a list of untyped ones that can only shrink.
/// </summary>
public static class EffectMessages
{
    /// <summary>What is known of a typed message without decoding one: its operation, the kind of aggregate it goes to, and its delivery class.</summary>
    public sealed record Kind(string Operation, string TargetKind, DeliveryClass Delivery, Type Type);

    private static Kind Of<T>() where T : class, IEffectMessage<T> => new(T.Operation, T.TargetKind, T.Delivery, typeof(T));

    public static IReadOnlyDictionary<string, Kind> ByOperation { get; } = new[]
    {
        Of<OrderWatch>(), Of<OrderRelease>(), Of<OrderFire>(), Of<OrderFailed>(), Of<OrderWatchFailed>(),
        Of<SessionOpen>(), Of<SessionExpire>(), Of<SessionRestore>(), Of<SessionExpired>(), Of<SessionClosed>(), Of<ActionWorkReport>(), Of<SessionOwnership>(),
        Of<SessionRegistration>(), Of<SessionActivity>(), Of<ActionBlock>(), Of<ActionUnblock>(), Of<MessageDelivery>(), Of<SessionTargetWatch>(), Of<AttentionRaise>(),
        Of<AttentionClose>(), Of<AttentionRoute>()
    }.ToFrozenDictionary(kind => kind.Operation, StringComparer.Ordinal);

    /// <summary>The delivery class of <paramref name="operation"/>'s message, or null for an operation that has no typed message yet.</summary>
    public static DeliveryClass? DeliveryOf(string operation) => ByOperation.TryGetValue(operation, out var kind) ? kind.Delivery : null;
}

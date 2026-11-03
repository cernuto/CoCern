using System.Collections.Frozen;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain.Messages;

/// <summary>
/// A call the hub makes to one aggregate directly, never through an outbox: a read for a composed tool, a step of due work, or a write
/// only the hub may make. Like an <see cref="IEffectMessage{TSelf}"/> it has one shape, and its handler decodes it before acting, so a
/// field the caller left out or mistyped is named (<see cref="RelayErrorCodes.MalformedEffect"/>) instead of read as empty. It has no
/// delivery class: the hub sees the reply at once, and nothing retries it.
/// </summary>
public interface IInternalRequest<TSelf> where TSelf : class, IInternalRequest<TSelf>
{
    /// <summary>The internal operation the request is sent as (one of <see cref="Operations"/>).</summary>
    static abstract string Operation { get; }

    /// <summary>
    /// The kind of aggregate that receives it (one of <see cref="AggregateKinds"/>), or <see cref="InternalRequests.AnyAggregate"/> for one
    /// every coordination grain takes itself.
    /// </summary>
    static abstract string TargetKind { get; }

    /// <summary>Reads the request from its arguments, or names each field that is missing or invalid.</summary>
    static abstract Decoding<TSelf> Decode(JsonObject payload);

    /// <summary>The request's arguments: the one shape <see cref="Decode"/> reads back.</summary>
    JsonObject ToJson();
}

/// <summary>Every typed direct request, by its operation (see <see cref="EffectMessages"/> for the outbox's).</summary>
public static class InternalRequests
{
    /// <summary>What is known of a typed request without decoding one: its operation and the kind of aggregate it goes to.</summary>
    public sealed record Kind(string Operation, string TargetKind, Type Type);

    /// <summary>
    /// The target kind of a request every kind of coordination grain takes itself, before its aggregate sees it (<see cref="OutboxDispatch"/>).
    /// It is never a grain kind: such a request is sent to the grain it is for, by that grain's own kind and key.
    /// </summary>
    public const string AnyAggregate = "*";

    private static Kind Of<T>() where T : class, IInternalRequest<T> => new(T.Operation, T.TargetKind, typeof(T));

    public static IReadOnlyDictionary<string, Kind> ByOperation { get; } = new[]
    {
        Of<AnomalyStart>(), Of<AnomalySchedule>(), Of<AnomalyCheck>(),
        Of<ResourceResolve>(), Of<ResourceResolveAll>(), Of<SessionSweep>(), Of<SessionOwnersQuery>(), Of<SessionsOpenQuery>(),
        Of<ClaimStateQuery>(), Of<LockLeaseCheck>(), Of<OutboxDispatch>()
    }.ToFrozenDictionary(kind => kind.Operation, StringComparer.Ordinal);
}

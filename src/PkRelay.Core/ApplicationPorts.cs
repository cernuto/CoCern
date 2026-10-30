using System.Text.Json;
using PkRelay.Domain;

namespace PkRelay.Core;

/// <summary>The hub's single use-case entry point, shared by REST, MCP and SignalR.</summary>
public interface IRelayApplication
{
    Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default);
}

public sealed record DueWorkReference(string StateName, string GrainType, string GrainKey, string WorkId, string Kind, DateTimeOffset DueAt);

/// <summary>Port implemented by storage: the index of aggregates holding due outbox effects or timers.</summary>
public interface IDueWorkSource
{
    Task<IReadOnlyList<DueWorkReference>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default, int offset = 0);
}

/// <summary>Port implemented by the host: best-effort change hints. The durable inbox stays authoritative.</summary>
public interface ICoordinationNotifier
{
    Task NotifyAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);
}

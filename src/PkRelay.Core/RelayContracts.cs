using System.Text.Json;
using Orleans;

namespace PkRelay.Core;

[GenerateSerializer]
public sealed record RelayCaller(
    [property: Id(0)] string PrincipalId,
    [property: Id(1)] bool IsAdministrator,
    [property: Id(2)] string[] Repositories,
    [property: Id(3)] bool IsInternal = false);

[GenerateSerializer]
public sealed record RelayReply(
    [property: Id(0)] bool Ok,
    [property: Id(1)] string Code,
    [property: Id(2)] string DataJson,
    [property: Id(3)] long Version = 0);

public interface IRelayApplication
{
    Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default);
}

public interface IRelayAggregateGrain : IGrainWithStringKey
{
    Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson);
}

public interface ISessionGrain : IRelayAggregateGrain;
public interface ITaskBoardGrain : IRelayAggregateGrain;
public interface ILockGrain : IRelayAggregateGrain;
public interface IControlGrain : IRelayAggregateGrain;

public sealed record DurableEffect(string Id, string TargetKind, string TargetKey, string Operation, string PayloadJson, DateTimeOffset DueAt);
public sealed record DueWorkItem(string Id, string Kind, DateTimeOffset DueAt);

public interface IHasDueWork
{
    IReadOnlyList<DueWorkItem> GetDueWork();
}

public sealed class StateEnvelope<T> : IHasDueWork where T : new()
{
    public int SchemaVersion { get; set; } = 1;
    public T Value { get; set; } = new();
    public List<DurableEffect> Effects { get; set; } = [];
    public List<DueWorkItem> DueWork { get; set; } = [];
    public IReadOnlyList<DueWorkItem> GetDueWork() => DueWork.Concat(Effects.Select(e => new DueWorkItem(e.Id, "effect", e.DueAt))).ToArray();
}

public sealed record DueWorkReference(string StateName, string GrainType, string GrainKey, string WorkId, string Kind, DateTimeOffset DueAt);

public interface IDueWorkSource
{
    Task<IReadOnlyList<DueWorkReference>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default, int offset = 0);
}

public interface ICoordinationNotifier
{
    Task NotifyAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);
}

using System.Text.Json.Nodes;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

/// <summary>One operation applied to one aggregate's working copy. Effects emitted here commit atomically with the state.</summary>
internal sealed class AggregateContext(JsonObject state, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now)
{
    public JsonObject State { get; } = state;
    public RelayCaller Caller { get; } = caller;
    public string Operation { get; } = operation;
    public JsonObject Args { get; } = args;
    public DateTimeOffset Now { get; } = now;
    public List<DurableEffect> Effects { get; } = [];
    /// <summary>The aggregate's own key, such as a lock's resource ID.</summary>
    public string Key { get; init; } = "";
    /// <summary>Whether this hub runs standing orders (<c>Relay:Orders:Enabled</c>). Off, orders are neither written nor run.</summary>
    public bool OrdersEnabled { get; init; }
    /// <summary>Days after completion before a finished task is archived (<c>Relay:Board:ArchiveAfterDays</c>); 0 archives only when the board fills.</summary>
    public int ArchiveAfterDays { get; init; } = CoCern.Contracts.RelayLimits.DefaultArchiveAfterDays;
    /// <summary>Tasks this operation moved to the archive; storage writes them in the same commit.</summary>
    public List<ArchivedTask> Archived { get; } = [];
    /// <summary>Order triggers this operation caused (trigger, task ID), run once the change is marked; see <see cref="StandingOrders"/>.</summary>
    public List<(string On, string Target)> OrderTriggers { get; } = [];

    public string Repository => Text(Args, "repositoryId");
    public string SessionId => Text(Args, "sessionId");

    public void Emit(string targetKind, string targetKey, string operation, JsonObject payload) =>
        Effects.Add(new(Guid.NewGuid().ToString("N"), targetKind, targetKey, operation, payload.ToJsonString(), Now));

    /// <summary>Emits an effect delivered with its own caller instead of the one whose call caused it.</summary>
    public void Emit(string targetKind, string targetKey, string operation, JsonObject payload, RelayCaller asCaller) =>
        Emit(targetKind, targetKey, operation, CoordinationStateMachine.WithCaller(payload, asCaller));

    /// <summary>Sends a typed message to the aggregate <paramref name="targetKey"/> of its kind, as its operation, in its one shape.</summary>
    public void Send<T>(string targetKey, T message) where T : class, IEffectMessage<T> => Emit(T.TargetKind, targetKey, T.Operation, message.ToJson());

    /// <summary>Sends a typed message delivered with its own caller instead of the one whose call caused it.</summary>
    public void Send<T>(string targetKey, T message, RelayCaller asCaller) where T : class, IEffectMessage<T> =>
        Emit(T.TargetKind, targetKey, T.Operation, message.ToJson(), asCaller);
}

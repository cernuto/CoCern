using System.Text.Json.Nodes;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Domain;

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
    /// <summary>Order triggers this operation caused (trigger, task ID), run once the change is marked; see <see cref="StandingOrders"/>.</summary>
    public List<(string On, string Target)> OrderTriggers { get; } = [];

    public string Repository => Text(Args, "repositoryId");
    public string SessionId => Text(Args, "sessionId");

    public void Emit(string targetKind, string targetKey, string operation, JsonObject payload) =>
        Effects.Add(new(Guid.NewGuid().ToString("N"), targetKind, targetKey, operation, payload.ToJsonString(), Now));
}

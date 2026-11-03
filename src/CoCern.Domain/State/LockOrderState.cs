using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>
/// The keys standing orders keep on a lock, beside the lock's own: the boards watching it for a <c>claim_released</c> order, and the order
/// releases it already applied. Only <see cref="StandingOrders"/> reads and writes them. A lock's state class adds <see cref="Fields"/> to
/// its shape, so the lock's document declares these keys without restating them.
/// </summary>
public sealed class LockOrderState(JsonObject json)
{
    /// <summary>The boards to tell when the lock's claim is released, by board grain key, each with its board ID. Removed when none is left.</summary>
    public const string OrderBoardsKey = "orderBoards";

    /// <summary>The order releases already applied (board key, order and run), the last <see cref="RelayLimits.OrderReleasesRemembered"/> of them.</summary>
    public const string OrderReleasesKey = "orderReleases";

    /// <summary>A watching board's ID, in its entry under <see cref="OrderBoardsKey"/>.</summary>
    public const string BoardIdKey = "boardId";

    /// <summary>The keys and their shapes, for a lock's shape to add (<see cref="StateShape.With"/>).</summary>
    public static (string Key, StateShape? Shape)[] Fields { get; } =
        [(OrderBoardsKey, StateShape.Map(StateShape.Object((BoardIdKey, null)))), (OrderReleasesKey, null)];

    /// <summary>The order keys on their own.</summary>
    public static StateShape Shape { get; } = StateShape.Object(Fields);

    /// <summary>The boards watching the lock, each with its board ID (the default board when it names none).</summary>
    public IEnumerable<(string BoardKey, string BoardId)> WatchingBoards =>
        (json[OrderBoardsKey] as JsonObject ?? []).Select(entry => (entry.Key, Text(entry.Value as JsonObject ?? [], BoardIdKey, AggregateKinds.DefaultBoard))).ToArray();

    public bool IsWatchedBy(string boardKey) => (json[OrderBoardsKey] as JsonObject)?.ContainsKey(boardKey) == true;

    public int WatchCount => (json[OrderBoardsKey] as JsonObject)?.Count ?? 0;

    public void Watch(string boardKey, string boardId) => ObjectAt(json, OrderBoardsKey)[boardKey] = new JsonObject { [BoardIdKey] = boardId };

    /// <summary>Stops telling <paramref name="boardKey"/>; with no board left the key goes.</summary>
    public void Unwatch(string boardKey)
    {
        var boards = ObjectAt(json, OrderBoardsKey);
        boards.Remove(boardKey);
        if (boards.Count == 0) json.Remove(OrderBoardsKey);
    }

    /// <summary>Records <paramref name="mark"/> as applied, unless it already was; returns whether it is new.</summary>
    public bool Remember(string mark)
    {
        var done = ArrayAt(json, OrderReleasesKey);
        if (done.Any(entry => entry is JsonValue value && value.TryGetValue<string>(out var seen) && seen == mark)) return false;
        done.Add(mark);
        while (done.Count > RelayLimits.OrderReleasesRemembered) done.RemoveAt(0);
        return true;
    }
}

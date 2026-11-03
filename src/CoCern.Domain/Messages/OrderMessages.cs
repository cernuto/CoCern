using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

// A board's standing orders and the locks they watch tell each other through these. Every one is advice: a target that refuses one would
// refuse it on every retry, so it is dropped, and with standing orders off none is delivered. Each names its board (BoardId) as the board
// that sent or is meant to run it; an order's runs are fenced by the board's order revision (Run) or the lock's generation, so a copy
// delivered twice is applied once.

/// <summary>
/// <see cref="Operations.OrderWatch"/>, sent by a board to a lock one of its <c>claim_released</c> orders names: start telling the board at
/// <see cref="BoardKey"/> when the lock's claim is released (<see cref="Watch"/>), or stop. A watch names the order it is for
/// (<see cref="OrderId"/>), which the lock reports back when it cannot watch (<see cref="OrderWatchFailed"/>); a board that no longer has
/// an order on the lock may stop watching without naming one.
/// </summary>
public sealed record OrderWatch(string RepositoryId, string BoardId, string BoardKey, string OrderId, bool Watch) : IEffectMessage<OrderWatch>
{
    public static string Operation => Operations.OrderWatch;
    public static string TargetKind => AggregateKinds.Lock;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["boardId"] = BoardId, ["boardKey"] = BoardKey, ["orderId"] = OrderId, ["watch"] = Watch };

    public static Decoding<OrderWatch> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var watch = new OrderWatch(read.Text("repositoryId"), read.Text("boardId"), read.Text("boardKey"), read.OptionalText("orderId") ?? "", read.Flag("watch"));
        read.Check(!watch.Watch || watch.OrderId.Length > 0, "orderId");
        return read.Result(watch);
    }
}

/// <summary>
/// <see cref="Operations.OrderRelease"/>, sent by a board to each resource of a task whose claim an order released: release the grant
/// <see cref="HolderSessionId"/> took for that task claim (<see cref="ClaimFor"/>). The lock applies it once per order run
/// (<see cref="BoardKey"/>, <see cref="OrderId"/>, <see cref="Run"/>), leaves a grant that now serves another claim alone, and reports a
/// grant with an unresolved action back as a failed run (<see cref="OrderFailed"/>).
/// </summary>
public sealed record OrderRelease(string RepositoryId, string BoardId, string BoardKey, string HolderSessionId, string ClaimFor, string OrderId, long Run)
    : IEffectMessage<OrderRelease>
{
    public static string Operation => Operations.OrderRelease;
    public static string TargetKind => AggregateKinds.Lock;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["boardId"] = BoardId, ["boardKey"] = BoardKey, ["holderSessionId"] = HolderSessionId, ["claimFor"] = ClaimFor, ["orderId"] = OrderId,
        ["run"] = Run
    };

    public static Decoding<OrderRelease> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new OrderRelease(read.Text("repositoryId"), read.Text("boardId"), read.Text("boardKey"), read.Text("holderSessionId"), read.Text("claimFor"),
            read.Text("orderId"), read.Number("run", minimum: 1)));
    }
}

/// <summary>
/// <see cref="Operations.OrderFire"/>, sent by a lock to each board watching it when its claim is released: run the board's
/// <c>claim_released</c> order on <see cref="ResourceId"/>, once per lock <see cref="Generation"/>. A board with no such order stops
/// watching the lock.
/// </summary>
public sealed record OrderFire(string RepositoryId, string BoardId, string ResourceId, long Generation) : IEffectMessage<OrderFire>
{
    public static string Operation => Operations.OrderFire;
    public static string TargetKind => AggregateKinds.Board;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["boardId"] = BoardId, ["resourceId"] = ResourceId, ["generation"] = Generation };

    public static Decoding<OrderFire> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new OrderFire(read.Text("repositoryId"), read.Text("boardId"), read.Text("resourceId"), read.Number("generation", minimum: 0)));
    }
}

/// <summary>
/// <see cref="Operations.OrderFailed"/>: run <see cref="Run"/> of order <see cref="OrderId"/> failed for <see cref="Reason"/>, so the board
/// counts one failed run, logs it and raises it to its managers. A failure of an earlier run, or a second failure of the same run, is
/// logged and not counted. Sent in one shape by two senders that both build it here: a lock queues it when the order's release meets an
/// unresolved action, and the hub applies it to the board directly when a session refused an item the order raised.
/// </summary>
public sealed record OrderFailed(string RepositoryId, string BoardId, string OrderId, long Run, string Reason) : IEffectMessage<OrderFailed>
{
    public static string Operation => Operations.OrderFailed;
    public static string TargetKind => AggregateKinds.Board;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["boardId"] = BoardId, ["orderId"] = OrderId, ["run"] = Run, ["reason"] = Reason };

    public static Decoding<OrderFailed> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new OrderFailed(read.Text("repositoryId"), read.Text("boardId"), read.Text("orderId"), read.Number("run", minimum: 1), read.Text("reason")));
    }
}

/// <summary>
/// <see cref="Operations.OrderWatchFailed"/>, sent by a lock back to a board whose watch it refused (it is already watched by
/// <see cref="CoCern.Contracts.RelayLimits.MaxOrderWatchBoards"/> boards): order <see cref="OrderId"/> can never fire, so the board counts
/// a failed run each time. It is split from <see cref="OrderFailed"/>, which names the run that failed; a refused watch belongs to no run.
/// </summary>
public sealed record OrderWatchFailed(string RepositoryId, string BoardId, string OrderId, string Reason) : IEffectMessage<OrderWatchFailed>
{
    public static string Operation => Operations.OrderWatchFailed;
    public static string TargetKind => AggregateKinds.Board;
    public static DeliveryClass Delivery => DeliveryClass.Advisory;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["boardId"] = BoardId, ["orderId"] = OrderId, ["reason"] = Reason };

    public static Decoding<OrderWatchFailed> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new OrderWatchFailed(read.Text("repositoryId"), read.Text("boardId"), read.Text("orderId"), read.Text("reason")));
    }
}

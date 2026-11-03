using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

// The hub's direct calls to a board. A board ID absent from one means the default board, as in every tool; each request writes it only
// when it was given, so the board labels what it emits (attention items, work reports) exactly as the call that caused it named the board.

/// <summary>
/// <see cref="Operations.TaskUnclaim"/>: the hub rolls back the task claim <c>task_claim</c> request <see cref="ClaimRequestId"/> made
/// (claim generation <see cref="ClaimGeneration"/>) when its resources could not all be acquired. The board releases the claim only while
/// <see cref="SessionId"/> still holds that very claim, so a late or repeated rollback changes nothing.
/// </summary>
public sealed record TaskUnclaim(string RepositoryId, string? BoardId, string TaskId, string SessionId, long ClaimGeneration, string ClaimRequestId) : IInternalRequest<TaskUnclaim>
{
    public static string Operation => Operations.TaskUnclaim;
    public static string TargetKind => AggregateKinds.Board;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId };
        if (BoardId is not null) json["boardId"] = BoardId;
        json["taskId"] = TaskId; json["sessionId"] = SessionId; json["claimGeneration"] = ClaimGeneration; json["claimRequestId"] = ClaimRequestId;
        return json;
    }

    public static Decoding<TaskUnclaim> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new TaskUnclaim(read.Text("repositoryId"), read.OptionalText("boardId"), read.Text("taskId"), read.Text("sessionId"),
            read.Number("claimGeneration", minimum: 1), read.Text("claimRequestId")));
    }
}

/// <summary>
/// <see cref="Operations.NextWork"/>, read-only: the highest-priority ready task <see cref="SessionId"/> could claim next, other than
/// <see cref="TaskId"/> (the task its milestone just changed). With a <see cref="Limit"/> the board also lists up to that many candidates
/// with their declared resources; with <see cref="BusyResources"/> (resources another session holds now) a task needing none of them
/// comes first within equal priority. The offer is advice: it acquires nothing.
/// </summary>
public sealed record NextWorkRead(string RepositoryId, string? BoardId, string SessionId, string? TaskId = null, long Limit = 0, IReadOnlyList<string>? BusyResources = null)
    : IInternalRequest<NextWorkRead>
{
    public static string Operation => Operations.NextWork;
    public static string TargetKind => AggregateKinds.Board;

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["repositoryId"] = RepositoryId };
        if (BoardId is not null) json["boardId"] = BoardId;
        json["sessionId"] = SessionId;
        if (TaskId is not null) json["taskId"] = TaskId;
        if (Limit != 0) json["limit"] = Limit;
        if (BusyResources is not null) json["busyResources"] = new JsonArray(BusyResources.Select(resource => (JsonNode)resource).ToArray());
        return json;
    }

    public static Decoding<NextWorkRead> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var request = new NextWorkRead(read.Text("repositoryId"), read.OptionalText("boardId"), read.Text("sessionId"), read.OptionalText("taskId"), read.OptionalNumber("limit") ?? 0);
        IReadOnlyList<string>? busy = null;
        if (read.Node("busyResources") is { } node)
        {
            var items = node as JsonArray;
            read.Check(items is not null && items.All(item => item is JsonValue value && value.TryGetValue<string>(out _)), "busyResources");
            busy = items?.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var resource) ? resource : "").ToArray();
        }
        return read.Result(request with { BusyResources = busy });
    }
}

/// <summary><see cref="Operations.Standing"/>, read-only: a board's standing in the compact form sent to chats, versioned by its revision.</summary>
public sealed record StandingRead(string RepositoryId, string? BoardId) : IInternalRequest<StandingRead>
{
    public static string Operation => Operations.Standing;
    public static string TargetKind => AggregateKinds.Board;

    public JsonObject ToJson() => BoardRequest.Json(RepositoryId, BoardId);

    public static Decoding<StandingRead> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new StandingRead(read.Text("repositoryId"), read.OptionalText("boardId")));
    }
}

/// <summary><see cref="Operations.Orders"/>, read-only: a board's standing orders in the compact form <c>get_status</c> lists.</summary>
public sealed record OrdersRead(string RepositoryId, string? BoardId) : IInternalRequest<OrdersRead>
{
    public static string Operation => Operations.Orders;
    public static string TargetKind => AggregateKinds.Board;

    public JsonObject ToJson() => BoardRequest.Json(RepositoryId, BoardId);

    public static Decoding<OrdersRead> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new OrdersRead(read.Text("repositoryId"), read.OptionalText("boardId")));
    }
}

/// <summary>
/// <see cref="Operations.TaskArchive"/>: a board's due archive sweep, which its own grain applies before delivering its outbox. It moves
/// tasks finished longer than <c>Relay:Board:ArchiveAfterDays</c> to the archive and forgets old archived IDs.
/// </summary>
public sealed record TaskArchiveSweep(string RepositoryId, string? BoardId) : IInternalRequest<TaskArchiveSweep>
{
    public static string Operation => Operations.TaskArchive;
    public static string TargetKind => AggregateKinds.Board;

    public JsonObject ToJson() => BoardRequest.Json(RepositoryId, BoardId);

    public static Decoding<TaskArchiveSweep> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new TaskArchiveSweep(read.Text("repositoryId"), read.OptionalText("boardId")));
    }
}

/// <summary>
/// <see cref="Operations.StandingProposeHub"/>: the hub proposes the standing line (<see cref="LineId"/>, <see cref="Text"/>) of the
/// feedback rule <see cref="Rule"/>, which fired across many sessions. Like any proposal it changes nothing until an administrator approves
/// it; the board checks the line's limits, and skips a line that exists, is pending, or was proposed recently.
/// </summary>
public sealed record HubStandingProposal(string RepositoryId, string? BoardId, string Rule, string LineId, string Text) : IInternalRequest<HubStandingProposal>
{
    public static string Operation => Operations.StandingProposeHub;
    public static string TargetKind => AggregateKinds.Board;

    public JsonObject ToJson()
    {
        var json = BoardRequest.Json(RepositoryId, BoardId);
        json["rule"] = Rule;
        json["line"] = new JsonObject { ["id"] = LineId, ["text"] = Text };
        return json;
    }

    public static Decoding<HubStandingProposal> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var (repository, board, rule) = (read.Text("repositoryId"), read.OptionalText("boardId"), read.Text("rule"));
        var line = read.Object("line");
        return read.Result(new HubStandingProposal(repository, board, rule, line.Text("id"), line.Text("text")));
    }
}

/// <summary>The arguments every direct board request starts from: its repository, and its board when one was named.</summary>
internal static class BoardRequest
{
    public static JsonObject Json(string repositoryId, string? boardId)
    {
        var json = new JsonObject { ["repositoryId"] = repositoryId };
        if (boardId is not null) json["boardId"] = boardId;
        return json;
    }
}

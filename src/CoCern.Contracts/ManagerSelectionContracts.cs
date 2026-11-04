using System.Text.Json.Serialization;

namespace CoCern.Contracts;

/// <summary>The selected session's public identity; selection grants no authority.</summary>
public sealed record ManagerSelectionSummary(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("repositoryId")] string RepositoryId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("available")] bool Available);

/// <summary>One administrator's recorded selection decision, not an action audit.</summary>
public sealed record ManagerSelectionHistory(
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("previousSessionId")] string? PreviousSessionId,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("repositoryId")] string RepositoryId,
    [property: JsonPropertyName("actorPrincipalId")] string ActorPrincipalId,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("humanWords")] string HumanWords);

public sealed record ManagerSelectionStatus(
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("current")] ManagerSelectionSummary? Current,
    [property: JsonPropertyName("history")] ManagerSelectionHistory[] History,
    [property: JsonPropertyName("nextHistoryBefore")] long? NextHistoryBefore,
    [property: JsonPropertyName("historyTruncated")] bool HistoryTruncated,
    [property: JsonPropertyName("historyReadRequired")] bool HistoryReadRequired = false);

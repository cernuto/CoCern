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
    [property: JsonPropertyName("humanWords")] string HumanWords,
    [property: JsonPropertyName("recordedBySessionId")] string? RecordedBySessionId = null);

public sealed record ManagerSelectionStatus(
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("current")] ManagerSelectionSummary? Current,
    [property: JsonPropertyName("history")] ManagerSelectionHistory[] History,
    [property: JsonPropertyName("nextHistoryBefore")] long? NextHistoryBefore,
    [property: JsonPropertyName("historyTruncated")] bool HistoryTruncated,
    [property: JsonPropertyName("historyReadRequired")] bool HistoryReadRequired = false);

/// <summary>
/// One administrative call as the hub saw it. The actor is the authenticated principal; the session and generation appear only when the hub verified them.
/// Attribution, never authority. Holds no credentials and no human quotation; a selection's words stay in <see cref="ManagerSelectionHistory"/>.
/// </summary>
public sealed record AdminActionEntry(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("actorPrincipalId")] string ActorPrincipalId,
    [property: JsonPropertyName("actingSessionId")] string? ActingSessionId,
    [property: JsonPropertyName("managerGeneration")] long? ManagerGeneration,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("repositoryId")] string RepositoryId,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("decisionRef")] string? DecisionRef);

/// <summary>A page of the action log, newest first. <see cref="Dropped"/> counts entries older than the log keeps, so a gap is visible rather than silent.</summary>
public sealed record AdminActionPage(
    [property: JsonPropertyName("items")] AdminActionEntry[] Items,
    [property: JsonPropertyName("nextBefore")] long? NextBefore,
    [property: JsonPropertyName("dropped")] long Dropped);

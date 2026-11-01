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

/// <summary>
/// One search result: the ID to open with <c>task_get</c>, <c>context_get</c> or <c>check_inbox</c>, its kind, status and date, and a
/// short snippet with matches in brackets. <see cref="BoardId"/> is set for a task or note on a board other than <c>default</c>.
/// <see cref="Score"/> is the relevance (the negated bm25 score; higher is better), or 0 when the source gives none.
/// </summary>
public sealed record SearchHit(string DocId, string Kind, string Status, DateTimeOffset UpdatedAt, string Snippet, string? BoardId = null, double Score = 0);

/// <summary>Port implemented by storage: full-text search over committed state, scoped to one repository and one session's inbox.</summary>
public interface IFullTextSearch
{
    /// <summary>
    /// The best hits for <paramref name="query"/>, read as plain words (never as search syntax), in <paramref name="repositoryId"/>:
    /// shared documents plus <paramref name="sessionId"/>'s own inbox. Empty <paramref name="kinds"/> means every kind.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string repositoryId, string sessionId, string query, IReadOnlyList<string> kinds, int limit,
        string? status = null, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="SearchAsync"/>, but only documents that contain every word of <paramref name="query"/>, with their scores. A source
    /// without scores returns its ordinary hits with score 0, which a caller asking for a minimum score then ignores.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAllWordsAsync(string repositoryId, string sessionId, string query, IReadOnlyList<string> kinds, int limit, CancellationToken ct = default) =>
        SearchAsync(repositoryId, sessionId, query, kinds, limit, null, ct);
}

/// <summary>
/// One telemetry row. Kind <c>call</c> is one public tool call; kind <c>wake</c> is one new waking message stored in a session inbox.
/// <see cref="Target"/> names the task, context note or message thread the call wrote; <see cref="ContentHash"/> fingerprints the status
/// text it wrote there, so a repeat of the same status can be counted. Field names follow the dashboard traffic feed where they overlap.
/// </summary>
public sealed record ActivityEvent
{
    public const string Call = "call";
    public const string Wake = "wake";
    /// <summary>One attention offer outcome: <see cref="Code"/> is shown, accepted, later or dismissed, and <see cref="Tool"/> the item's kind.</summary>
    public const string Attention = "attention";

    public DateTimeOffset Timestamp { get; init; }
    public string Kind { get; init; } = Call;
    public string RepositoryId { get; init; } = "";
    public string? SessionId { get; init; }
    public string? Role { get; init; }
    public string? Tool { get; init; }
    public bool Ok { get; init; }
    public string Code { get; init; } = "";
    public int RequestBytes { get; init; }
    public int ReplyBytes { get; init; }
    public double DurationMilliseconds { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    /// <summary>The mutation's requestId, so an exact retry is not counted twice.</summary>
    public string? RequestId { get; init; }
    public string? Target { get; init; }
    public string? ContentHash { get; init; }
    public string? WorkflowState { get; init; }
    /// <summary>The task a next-work offer in this call's reply pointed at, as a <see cref="Target"/>.</summary>
    public string? OfferTarget { get; init; }
}

/// <summary>Port implemented by storage: records telemetry without waiting. It never throws and drops events rather than slow a call.</summary>
public interface IActivitySink
{
    void Record(ActivityEvent activity);
}

/// <summary>Port implemented by storage: the counters derived from recorded activity.</summary>
public interface IActivityCounterSource
{
    Task<ActivityCounters> ReadActivityCountersAsync(string repositoryId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

/// <summary>Hub-side efficiency counters for one repository and time window. Definitions are in docs/monitoring.md.</summary>
public sealed record ActivityCounters
{
    public long Calls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long InboxReads { get; init; }
    public long Wakes { get; init; }
    public long OffersMade { get; init; }
    public long OffersEndedBlocked { get; init; }
    public long DuplicateStatusWrites { get; init; }
    public long ManagerTurns { get; init; }
    public long Closures { get; init; }
    public double? ManagerTurnsPerClosure { get; init; }
    public double? MedianSecondsToAcceptedResult { get; init; }
    /// <summary>Events this process dropped because the writer queue was full.</summary>
    public long DroppedEvents { get; init; }
}

/// <summary>
/// One analytics result: column names, at most the configured row cap of rows, whether more rows were cut off, and when the snapshot
/// it read was taken. Values are plain JSON-friendly types: null, bool, long, double or string.
/// </summary>
public sealed record AnalyticsResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, DateTimeOffset SnapshotAt);

/// <summary>Why an analytics query did not run or did not finish.</summary>
public enum AnalyticsError
{
    /// <summary>No snapshot is loaded yet, or analytics is turned off.</summary>
    Unavailable,
    /// <summary>No named query has this name.</summary>
    UnknownQuery,
    /// <summary>A parameter is unknown or out of range, or the SQL text is empty or too long.</summary>
    InvalidArguments,
    /// <summary>The SQL is not exactly one read-only <c>SELECT</c> statement.</summary>
    Refused,
    /// <summary>The query ran past the per-query timeout and was interrupted.</summary>
    Timeout,
    /// <summary>The database rejected the query, for example a file read, an unknown column, or running out of its memory limit.</summary>
    Failed,
    /// <summary>The rows read so far already pass the reply size limit; the rest are not read.</summary>
    TooLarge
}

public sealed class AnalyticsException(AnalyticsError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public AnalyticsError Error { get; } = error;
}

/// <summary>
/// Port implemented by <c>PkRelay.Analytics</c>: read-only queries against the latest analytics snapshot, never the live database.
/// Failures throw <see cref="AnalyticsException"/>.
/// </summary>
public interface IAnalyticsQueries
{
    /// <summary>The fixed named queries, in a stable order.</summary>
    IReadOnlyList<string> QueryNames { get; }

    /// <summary>
    /// Runs one named query for <paramref name="repositoryId"/>. <paramref name="parameters"/> is a JSON object (or undefined) of the
    /// query's optional parameters, such as <c>days</c>.
    /// </summary>
    Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs ad hoc SQL that must be exactly one <c>SELECT</c>. The caller decides whether ad hoc SQL is allowed
    /// (<c>Relay:Analytics:AllowAdHocSql</c>) and logs it.
    /// </summary>
    Task<AnalyticsResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default);
}

/// <summary>Port implemented by the host: best-effort change hints. The durable inbox stays authoritative.</summary>
public interface ICoordinationNotifier
{
    Task NotifyAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);
}

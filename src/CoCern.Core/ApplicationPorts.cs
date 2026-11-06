using System.Text.Json;
using CoCern.Domain;

namespace CoCern.Core;

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

/// <summary>Port implemented by storage: archived tasks, which left their board once finished. Reads only committed rows.</summary>
public interface ITaskArchive
{
    /// <summary>The task's JSON as it was archived, or null when this board archived no task with that ID.</summary>
    Task<string?> ReadArchivedTaskAsync(string repositoryId, string boardId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>Which of <paramref name="taskIds"/> this board archived. IDs stay resolvable here long after the board forgot them.</summary>
    Task<IReadOnlySet<string>> ArchivedIdsAsync(string repositoryId, string boardId, IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken = default);
}

/// <summary>Port implemented by storage: full-text search over committed state, scoped to one repository and one session's inbox.</summary>
public interface IFullTextSearch
{
    /// <summary>Whether the store keeps a search index; with none the hub answers <c>SearchUnavailable</c>.</summary>
    bool Enabled => true;

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
    /// <summary>One reply over its token budget; <see cref="Code"/> says how it was handled (<c>over</c>, <c>over:&lt;part&gt;</c>, or the compaction step).</summary>
    public const string Budget = "budget";
    /// <summary>
    /// One outbox effect its target refused for good: a report dropped by its delivery class, or a malformed effect (counted once, also when
    /// it is retried). <see cref="Tool"/> is the effect's operation and <see cref="Code"/> the refusal; it should read zero.
    /// </summary>
    public const string EffectRefused = "effect_refused";

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
    /// <summary>Estimated tokens (o200k) in the request's arguments, counted by the hub; null when token counting is off.</summary>
    public int? RequestTokens { get; init; }
    /// <summary>Estimated tokens (o200k) in the reply's data as sent, counted by the hub; null when token counting is off.</summary>
    public int? ReplyTokens { get; init; }
    /// <summary>Request and reply text, counted into the token fields off the call's path, then dropped. Never stored.</summary>
    public string? RequestText { get; init; }
    public string? ReplyText { get; init; }
    /// <summary>The mutation's requestId, so an exact retry is not counted twice.</summary>
    public string? RequestId { get; init; }
    public string? Target { get; init; }
    public string? ContentHash { get; init; }
    public string? WorkflowState { get; init; }
    /// <summary>The task a next-work offer in this call's reply pointed at, as a <see cref="Target"/>.</summary>
    public string? OfferTarget { get; init; }
    /// <summary>Reported adapter origin only: true for internal automation, false for a model-facing call, null when unrecorded. Never authorization or proof of model execution.</summary>
    public bool? BackgroundCall { get; init; }

    private string? _callOrigin;
    /// <summary>Untrusted reported call path, independent of model/background classification and never authorization.</summary>
    public string? CallOrigin
    {
        get => _callOrigin;
        init => _callOrigin = value is "mcp" or "claude-stop" or "claude-waiter" or "wake-watcher" or "node" or "dashboard" ? value : null;
    }
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

/// <summary>Hub-side efficiency counters for one repository and time window. Definitions are in docs/reference.md#activity.</summary>
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
/// Port implemented by <c>CoCern.Analytics</c>: read-only queries against the latest analytics snapshot, never the live database.
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

/// <summary>
/// Port implemented by the host: one shared tokenizer that counts model tokens, loaded once and safe to share. Counts are exact for
/// models that use <see cref="Encoding"/> and estimates for others.
/// </summary>
public interface ITokenCounter
{
    /// <summary>The encoding's name, such as <c>o200k_base</c>.</summary>
    string Encoding { get; }

    /// <summary>The number of tokens in <paramref name="text"/>.</summary>
    int Count(string text);

    /// <summary>The length, in characters, of the longest start of <paramref name="text"/> that is at most <paramref name="maxTokens"/> tokens.</summary>
    int PrefixLength(string text, int maxTokens);
}

/// <summary>One feedback rule's persisted state: whether it is on, and its tip and outcome totals.</summary>
public sealed record FeedbackRuleState(string Rule, bool Enabled = true, long Tips = 0, long Followed = 0, long Ignored = 0, DateTimeOffset? DisabledAt = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Outcomes => Followed + Ignored;
}

/// <summary>Port implemented by storage: each tip rule's on/off state and follow totals, kept across restarts.</summary>
public interface IFeedbackRuleStore
{
    IReadOnlyList<FeedbackRuleState> LoadFeedbackRules();

    /// <summary>Replaces one rule's stored state.</summary>
    Task SaveFeedbackRuleAsync(FeedbackRuleState state);
}

/// <summary>One synced document as read from git: its text, the commit it was read at, and its content hash.</summary>
public sealed record DocContent(string Text, string Commit, string Hash);

/// <summary>
/// Port implemented by the host: reads a synced document with <c>git show &lt;commit&gt;:&lt;path&gt;</c>, never from a working tree.
/// Only configured repositories and paths their patterns include (and do not exclude) are read; anything else, a missing commit or path,
/// or a file over its type's size limit reads as null.
/// </summary>
public interface IDocSource
{
    Task<DocContent?> ReadAsync(string repositoryId, string path, string commit, CancellationToken cancellationToken = default);

    /// <summary>Whether the clone still has <paramref name="commit"/>; a document falls back to the branch head only when it does not.</summary>
    Task<bool> CommitExistsAsync(string repositoryId, string commit, CancellationToken cancellationToken = default);

    /// <summary>Reads the file at the head of <paramref name="branch"/>, for a document whose recorded commit is gone.</summary>
    Task<DocContent?> ReadHeadAsync(string repositoryId, string path, string branch, CancellationToken cancellationToken = default);
}

/// <summary>What one doc sync did: the commit the directory now follows, the files it re-indexed or removed, and the files it skipped.</summary>
public sealed record DocSyncOutcome(string Commit, int Changed, int Skipped, bool Fetched);

public enum DocSyncError
{
    /// <summary>The repository has no doc cache settings on this hub.</summary>
    NotConfigured,
    /// <summary>A manual sync ran for this repository too recently.</summary>
    RateLimited,
    /// <summary>The branch name is malformed, or the clone has no such branch from its remote.</summary>
    UnknownBranch,
    /// <summary>A git command failed or timed out.</summary>
    Failed,
    /// <summary>The requestId was already used for this repository with another branch.</summary>
    RequestConflict
}

public sealed class DocSyncException(DocSyncError error, string message, int retryAfterSeconds = 0) : Exception(message)
{
    public DocSyncError Error { get; } = error;
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Port implemented by the host: runs the doc sync for one repository now, sharing the timer's per-repository lock.</summary>
public interface IDocSync
{
    Task<string?> ResolveNativeAsync(RelayCaller caller, string? checkout, string? common, string? origin, string? repositoryId = null) => Task.FromResult<string?>(null);

    /// <summary>A repeated <paramref name="requestId"/> returns the first run's outcome instead of running again.</summary>
    Task<DocSyncOutcome> SyncNowAsync(string repositoryId, string? branch, string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Advisory, after a session opens: registers the session's git workspace for doc search when it lies inside a configured root.
    /// Returns the repository ID it was registered under, or null when refused.
    /// </summary>
    Task<string?> LearnAsync(string repositoryId, string workspace, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

/// <summary>Port implemented by the host: best-effort change hints. The durable inbox stays authoritative.</summary>
public interface ICoordinationNotifier
{
    Task NotifyAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);
}

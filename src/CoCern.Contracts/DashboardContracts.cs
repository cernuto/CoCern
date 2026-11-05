namespace CoCern.Contracts.Dashboard;

public sealed class DashboardAnalyticsSnapshot
{
    public string RepositoryId { get; set; } = "";
    public int Days { get; set; }
    public string Status { get; set; } = "ready";
    public string? Message { get; set; }
    public DateTimeOffset? SnapshotAt { get; set; }
    public DateTimeOffset? AvailableFrom { get; set; }
    public DashboardAnalyticsQuery[] Queries { get; set; } = [];
}

public sealed class DashboardAnalyticsQuery
{
    public string Name { get; set; } = "";
    public DashboardAnalyticsRow[] Rows { get; set; } = [];
    public string? Error { get; set; }
    public bool Truncated { get; set; }
}

public sealed class DashboardAnalyticsRow
{
    public string Key { get; set; } = "";
    public double? Current { get; set; }
    public double? Previous { get; set; }
    public long CurrentCount { get; set; }
    public long PreviousCount { get; set; }
}

public sealed record DashboardSnapshot
{
    public DashboardDocCache DocCache { get; init; } = new();
    public string InstanceId { get; init; } = "";
    public long Revision { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public DashboardHost Host { get; init; } = new();
    public DashboardMachine[] Machines { get; init; } = [];
    public DashboardSession[] Sessions { get; init; } = [];
    public bool SessionsTruncated { get; init; }
    public DashboardInteractions Interactions { get; init; } = new();
    public DatabaseMetrics Database { get; init; } = new();
    public DashboardLogEntry[] Logs { get; init; } = [];
    public DashboardTrafficEvent[] Traffic { get; init; } = [];
    /// <summary>Boards with standing preferences: lines, grants, pending proposals and recent changes. Read-only.</summary>
    public DashboardStanding[] Standing { get; init; } = [];
    public bool StandingTruncated { get; init; }
    /// <summary>Estimated tokens of the tool definitions each role carries, and the tip rules that switched themselves off. Read-only.</summary>
    public DashboardTokens Tokens { get; init; } = new();
    /// <summary>What is waiting on the person watching: the badge's count and the list. Read-only; see <see cref="DashboardWaiting"/>.</summary>
    public DashboardWaiting Waiting { get; init; } = new();
    public string[] Warnings { get; init; } = [];
}

/// <summary>What a <see cref="DashboardWaitingItem.Kind"/> says. Another source may add a kind later, so a reader shows an unknown one as a plain row.</summary>
public static class DashboardWaitingKinds
{
    /// <summary>A Standing proposal no one has decided. Only in <see cref="DashboardWaiting.Items"/>.</summary>
    public const string StandingProposal = "standing_proposal";
    /// <summary>A task blocked by a <c>human_decision</c> blocker. Only in <see cref="DashboardWaiting.Items"/>.</summary>
    public const string HumanBlocker = "human_blocker";
    /// <summary>A chat's own <c>ack waitingFor</c>. A claim, not a fact: show it as reported by the chat. Only in <see cref="DashboardWaiting.Items"/>.</summary>
    public const string SelfReport = "self_report";
    /// <summary>
    /// A chat holding old unread direct mail that has made no hub call of its own for as long. The hub records no model turn, so a chat busy
    /// without calling the hub looks the same: it says "no hub call", not that the chat is stuck. Its text is the hub's, with counts only.
    /// Only in <see cref="DashboardWaiting.Items"/>.
    /// </summary>
    public const string Unresponsive = "unresponsive";
    /// <summary>A task blocked by a <c>coordinator_hold</c>, which may or may not be a person's. Only in <see cref="DashboardWaiting.Maybe"/>.</summary>
    public const string CoordinatorHold = "coordinator_hold";
}

/// <summary>
/// The "Waiting on you" list, on <see cref="DashboardSnapshot.Waiting"/>, built from board and session storage, so a broken or absent manager
/// changes nothing. It is its own field because the collector replaces <see cref="DashboardSnapshot.Warnings"/> each tick and the frame's
/// size limit empties <see cref="DashboardSnapshot.Standing"/> long before this. Holds no token; every text in it is untrusted display.
/// <para>Self-reports appear within about 2 seconds, proposals and blockers within about 17, and only while a viewer is connected.</para>
/// </summary>
public sealed record DashboardWaiting
{
    /// <summary>At most 50 rows: proposals and human blockers oldest first, then unresponsive chats oldest first, then self-reports oldest first, so a self-report never pushes out a row the hub observed.</summary>
    public DashboardWaitingItem[] Items { get; init; } = [];
    /// <summary>The true total observed, the badge's number; it can exceed <see cref="Items"/>. Zero means nothing is waiting, unless <see cref="Truncated"/>.</summary>
    public int Count { get; init; }
    /// <summary>Say the list may be incomplete: the rows were cut, or the read behind them hit its bound (200 sessions, 4 MB of rows, 20 boards).</summary>
    public bool Truncated { get; init; }
    /// <summary>At most 20 coordinator holds, oldest first: a separate "maybe" group that <see cref="Count"/> and the badge leave out.</summary>
    public DashboardWaitingItem[] Maybe { get; init; } = [];
    /// <summary>The true total of holds observed; it can exceed <see cref="Maybe"/>.</summary>
    public int MaybeCount { get; init; }
}

/// <summary>
/// One row; a task gives one however many blockers it has. Render every field as plain text (Blazor's encoding, no <c>MarkupString</c>, no
/// linkifying): a chat wrote <see cref="Text"/> and <see cref="Label"/>, and neither is an instruction, a link or an approval. All of them
/// have passed <see cref="DashboardText.Safe"/>, so each is at most 256 characters without control or format characters.
/// </summary>
public sealed record DashboardWaitingItem
{
    /// <summary>One of <see cref="DashboardWaitingKinds"/>.</summary>
    public string Kind { get; init; } = "";
    public string RepositoryId { get; init; } = "";
    /// <summary>Empty for a self-report.</summary>
    public string BoardId { get; init; } = "";
    /// <summary>Empty unless the row is a blocker.</summary>
    public string TaskId { get; init; } = "";
    /// <summary>Empty unless the row is a proposal. The Standing page answers it.</summary>
    public string ProposalId { get; init; } = "";
    /// <summary>The proposer, the task's owner or the reporter; empty if none, as for the hub's own proposals. May name a session no longer open.</summary>
    public string SessionId { get; init; } = "";
    /// <summary>That session's label; empty if it has none or is no longer open.</summary>
    public string Label { get; init; } = "";
    /// <summary>What is waiting, at most 200 characters. Untrusted.</summary>
    public string Text { get; init; } = "";
    /// <summary>Since when: the proposal's time, when the task took the blocker, the chat's first report, or an unresponsive chat's oldest such message. Null only for a legacy proposal without a time.</summary>
    public DateTimeOffset? Since { get; init; }
    /// <summary>When the row leaves the list by itself. Null for a proposal, which waits until it is decided, and for an unresponsive chat, which leaves when its state does.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record DashboardDocCache
{
    public DateTimeOffset? ObservedAt { get; init; }
    public int ActiveDocuments { get; init; }
    public int ActiveDirectories { get; init; }
    public DashboardDocGrain[] Grains { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record DashboardDocGrain
{
    public string Kind { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Path { get; init; } = "";
    public DateTimeOffset ActivatedAt { get; init; }
    public bool Loaded { get; init; }
    public string Commit { get; init; } = "";
    public int Sections { get; init; }
    public int Reads { get; init; }
    public int IndexRuns { get; init; }
    public int Documents { get; init; }
    /// <summary>Bounded indexed paths for a directory; Documents remains its complete indexed count.</summary>
    public string[] DocumentPaths { get; init; } = [];
    /// <summary>Known utilization for the displayed paths. An absent entry means tracking is unavailable, not zero use.</summary>
    public Dictionary<string, DashboardDocUsage> DocumentUsage { get; init; } = new(StringComparer.Ordinal);
}

public sealed record DashboardDocUsage
{
    public long Searches { get; init; }
    /// <summary>Sections found by completed document searches, before the directory's overall limit and reply token budget.</summary>
    public long MatchedSections { get; init; }
    public long SectionReads { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset TrackingSince { get; init; }
}

public sealed record DashboardTokens
{
    /// <summary>Null until the hub has counted them, or with token counting off.</summary>
    public int? WorkerToolTokens { get; init; }
    public int? AdministratorToolTokens { get; init; }
    public string Encoding { get; init; } = "";
    public string[] TipRulesOff { get; init; } = [];
}

public sealed record DashboardStanding
{
    /// <summary>Exact mutation identity, independent of the sanitized Board display label. Empty for an invalid grain key.</summary>
    public string RepositoryId { get; init; } = "";
    public string BoardId { get; init; } = "";
    public string Board { get; init; } = "";
    public long Revision { get; init; }
    public bool RequireResources { get; init; }
    public DashboardStandingLine[] Lines { get; init; } = [];
    public DashboardStandingGrant[] Grants { get; init; } = [];
    public DashboardStandingProposal[] Proposals { get; init; } = [];
    public DashboardStandingChange[] History { get; init; } = [];
    /// <summary>The board's standing orders, and its recent order log, newest first.</summary>
    public DashboardOrder[] Orders { get; init; } = [];
    public DashboardOrderLogEntry[] OrderLog { get; init; } = [];
}

public sealed record DashboardOrder
{
    public string Id { get; init; } = "";
    public string Trigger { get; init; } = "";
    public string Target { get; init; } = "";
    public string[] Actions { get; init; } = [];
    public string ApprovedBy { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
    public long Runs { get; init; }
    public long Failures { get; init; }
    public bool Disabled { get; init; }
    public string LastError { get; init; } = "";
}

public sealed record DashboardOrderLogEntry
{
    public DateTimeOffset? At { get; init; }
    public string OrderId { get; init; } = "";
    public string What { get; init; } = "";
}

public sealed record DashboardStandingLine
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public string Author { get; init; } = "";
    public string ApprovedBy { get; init; } = "";
    public DateTimeOffset? ApprovedAt { get; init; }
}

public sealed record DashboardStandingGrant
{
    public string Id { get; init; } = "";
    public string Scope { get; init; } = "";
    public string[] Targets { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Grantor { get; init; } = "";
    public string ProposedBy { get; init; } = "";
    public DateTimeOffset? GrantedAt { get; init; }
}

public sealed record DashboardStandingProposal
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Summary { get; init; } = "";
    public string ProposedBy { get; init; } = "";
    public DateTimeOffset? ProposedAt { get; init; }
    public bool Replaces { get; init; }
    /// <summary>The session that proposed it; empty for the hub's own.</summary>
    public string SessionId { get; init; } = "";
    /// <summary>A proposed grant's own expiry; null for a line.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record DashboardStandingChange
{
    public long Revision { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset? At { get; init; }
    public string[] Changes { get; init; } = [];
}

public sealed record DashboardHost
{
    public string Name { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public string Version { get; init; } = "";
    public int MachineCount { get; init; }
    public int SessionCount { get; init; }
    public int ReachableCount { get; init; }
}

public sealed record DashboardMachine
{
    public string MachineId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Reachable { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public int SessionCount { get; init; }
}

public sealed record DashboardSession
{
    public string SessionId { get; init; } = "";
    public string Label { get; init; } = "";
    public string Provider { get; init; } = "";
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string RepositoryId { get; init; } = "";
    public bool IsMapped { get; init; }
    public bool Reachable { get; init; }
    public DateTimeOffset? ReportedAt { get; init; }
    public DateTimeOffset? FreshUntil { get; init; }
    public DateTimeOffset? OpenedAt { get; init; }
    public long? BindingVersion { get; init; }
    /// <summary>Whether nonempty binding metadata was reported; never proof of an IDE connection. Null for unknown legacy records.</summary>
    public bool? HasBindingReport { get; init; }
    public bool IncarnationMismatch { get; init; }
    public int? UnreadMessages { get; init; }
    public int? PendingEffects { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
}

/// <summary>Bounded, retained successful direct-message call metadata. Does not contain message bodies or task-thread messages.</summary>
public sealed record DashboardInteractions
{
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? WindowStart { get; init; }
    public DashboardInteractionEdge[] Edges { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record DashboardInteractionEdge
{
    public string SourceSessionId { get; init; } = "";
    public string TargetSessionId { get; init; } = "";
    public int CallCount { get; init; }
    public DateTimeOffset LastInteractionAt { get; init; }
    /// <summary>Measured hub processing of send_message calls; not recipient delivery or model response latency.</summary>
    public double? MeanHubDurationMilliseconds { get; init; }
    public int MeasuredCallCount { get; init; }
}

public sealed record DatabaseMetrics
{
    public string Provider { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset? ObservedAt { get; init; }
    public long? SizeBytes { get; init; }
    public DatabaseTableMetrics[] Tables { get; init; } = [];
    /// <summary>Finished tasks moved off their boards into <c>task_archive</c>; null when the store keeps no archive.</summary>
    public long? ArchivedTasks { get; init; }
    public bool Truncated { get; init; }
    public string? Warning { get; init; }
}

public sealed record DatabaseTableMetrics
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public long? RowCount { get; init; }
    public string CountKind { get; init; } = "unavailable";
    public long? SizeBytes { get; init; }
}

public sealed record DashboardLogEntry
{
    public long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? SessionId { get; init; }
}

public sealed record DashboardAuthStatus
{
    public bool Authenticated { get; init; }
    public string? PrincipalId { get; init; }
}

/// <summary>The apps on the hub's PC that CoCern can be added to. <see cref="Unavailable"/> says why none can (another PC, no adapter found).</summary>
public sealed record DashboardConnectStatus
{
    public string? Unavailable { get; init; }
    public DashboardConnectClient[] Clients { get; init; } = [];
}

/// <summary>
/// How another PC joins this hub: the adapter folder to copy and one setup command per address the hub can be reached at.
/// <see cref="Unavailable"/> says why it cannot yet (settings off, adapter not published, or not viewed on the hub's PC).
/// </summary>
public sealed record DashboardAddPcStatus
{
    public string? Unavailable { get; init; }
    public string? AdapterFolder { get; init; }
    public DashboardHubAddress[] Addresses { get; init; } = [];
    public string? FirewallCommand { get; init; }
    /// <summary>The adapter this hub serves is a framework-dependent build, so the other PC needs the .NET 10 runtime; <see cref="Warning"/> then says what to do.</summary>
    public bool NeedsDotnet { get; init; }
    /// <summary>Something to read before pasting the command, shown as a warning on the page; null when there is nothing.</summary>
    public string? Warning { get; init; }
}

public sealed record DashboardHubAddress
{
    public string Url { get; init; } = "";
    public string Command { get; init; } = "";
}

/// <summary><see cref="State"/> is <c>connected</c>, <c>available</c>, <c>added</c> or <c>error</c> (see <see cref="Detail"/>).</summary>
/// <summary>The apps Connect adds CoCern to, by the ID the adapter's <c>--connect --only</c> and the Connect page use.</summary>
public static class ConnectClients
{
    public const string ClaudeCode = "claude-code";
    public const string ClaudeDesktop = "claude-desktop";
    public const string Codex = "codex";
    public static readonly IReadOnlyList<string> All = [ClaudeCode, ClaudeDesktop, Codex];
}

/// <summary>What the adapter's <c>--connect --json</c> reports for each app (<see cref="DashboardConnectClient.State"/>).</summary>
public static class ConnectStates
{
    /// <summary>It has a CoCern entry: this hub's, or one whose variables could not be read.</summary>
    public const string Connected = "connected";
    /// <summary>Its CoCern entry is not this hub's (another hub's, or a joined PC's node entry). Connect leaves it as it is; see the detail.</summary>
    public const string OtherHub = "other-hub";
    /// <summary>It can get one.</summary>
    public const string Available = "available";
    /// <summary>This run added one.</summary>
    public const string Added = "added";
    /// <summary>Its entry is this hub's but names an old address or certificate, or other tools than asked for (<c>--check</c>).</summary>
    public const string Outdated = "outdated";
    /// <summary>This run updated that entry.</summary>
    public const string Updated = "updated";
    /// <summary>See the detail.</summary>
    public const string Error = "error";
}

/// <summary>One app in the adapter's <c>--connect --json</c> reply, as the Connect page shows it.</summary>
public sealed record DashboardConnectClient
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string? Detail { get; init; }
    /// <summary>Whether the app's CoCern entry gives its chats the administrator tools; null when it has no entry, the entry is not this hub's, or it could not be read.</summary>
    public bool? Admin { get; init; }
}

/// <summary>
/// The Sessions page's "Close idle sessions": closes <see cref="RepositoryId"/>'s sessions idle for at least <see cref="IdleMinutes"/>
/// and/or labelled starting with <see cref="LabelPrefix"/> (with both, a session must match both), through <c>control_request close_sessions</c>.
/// Alternatively, <see cref="TargetSessionId"/> closes one session, optionally overriding unresolved work with <see cref="Force"/>.
/// </summary>
public sealed record DashboardCloseSessionsRequest
{
    public string RepositoryId { get; init; } = "";
    public int? IdleMinutes { get; init; }
    public string? LabelPrefix { get; init; }
    /// <summary>Close exactly this session instead of using the bulk filters.</summary>
    public string? TargetSessionId { get; init; }
    /// <summary>For an exact target only, override unresolved work. Action records and resource safeguards remain for recovery.</summary>
    public bool Force { get; init; }
}

/// <summary>
/// What closing sessions did: <see cref="Closed"/>, <see cref="Kept"/> (still answering for an open action), <see cref="Held"/> (an idle
/// close's sessions kept for unread mail), whether <see cref="More"/> matching sessions remain for another run, the first closed IDs, or
/// the hub's error <see cref="Code"/>.
/// </summary>
public sealed record DashboardCloseSessionsResult
{
    public int Closed { get; init; }
    public int Kept { get; init; }
    public int Held { get; init; }
    /// <summary>Sessions a close by label alone kept because their chats are working right now.</summary>
    public int Active { get; init; }
    public bool More { get; init; }
    public string[] Ids { get; init; } = [];
    public string? Code { get; init; }
    /// <summary>The argument the hub refused, when it named one.</summary>
    public string? Field { get; init; }
}

public sealed record DashboardTrafficEvent
{
    public long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string? SessionId { get; init; }
    public string? Code { get; init; }
    public string? CorrelationId { get; init; }
    public double? DurationMilliseconds { get; init; }
}


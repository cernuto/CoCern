namespace CoCern.Contracts.Dashboard;

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
    public DatabaseMetrics Database { get; init; } = new();
    public DashboardLogEntry[] Logs { get; init; } = [];
    public DashboardTrafficEvent[] Traffic { get; init; } = [];
    /// <summary>Boards with standing preferences: lines, grants, pending proposals and recent changes. Read-only.</summary>
    public DashboardStanding[] Standing { get; init; } = [];
    public bool StandingTruncated { get; init; }
    /// <summary>Estimated tokens of the tool definitions each role carries, and the tip rules that switched themselves off. Read-only.</summary>
    public DashboardTokens Tokens { get; init; } = new();
    public string[] Warnings { get; init; } = [];
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
    public bool IncarnationMismatch { get; init; }
    public int? UnreadMessages { get; init; }
    public int? PendingEffects { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
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
}

public sealed record DashboardHubAddress
{
    public string Url { get; init; } = "";
    public string Command { get; init; } = "";
}

/// <summary><see cref="State"/> is <c>connected</c>, <c>available</c>, <c>added</c> or <c>error</c> (see <see cref="Detail"/>).</summary>
public sealed record DashboardConnectClient
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string? Detail { get; init; }
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


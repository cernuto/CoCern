namespace CoCern.Contracts.Dashboard;

/// <summary>Body-free, rolling-hour observations. Null numbers are unknown, never inferred historical savings.</summary>
public sealed record DashboardEfficiencySnapshot
{
    public DateTimeOffset CollectedAt { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public string Scope { get; init; } = "hub-wide";
    public string? RepositoryId { get; init; }
    public string? Unavailable { get; init; }
    public bool Truncated { get; init; }
    public long ActivitySamples { get; init; }
    public DateTimeOffset? ActivityCoverageStart { get; init; }
    public long Calls { get; init; }
    public long BackgroundCalls { get; init; }
    public long RequestBytes { get; init; }
    public long ReplyBytes { get; init; }
    public long? EstimatedRequestTokens { get; init; }
    public long? EstimatedReplyTokens { get; init; }
    public long ReplyTokenSamples { get; init; }
    public DashboardPollingMetrics Watcher { get; init; } = new();
    public DashboardPollingMetrics NodeWatcher { get; init; } = new();
    public DashboardPollingMetrics OtherBackground { get; init; } = new();
    public DashboardProjectionSavings TaskBrief { get; init; } = new();
    public DashboardProjectionSavings FocusedDocument { get; init; } = new();
    public DashboardEfficiencyBaseline? Baseline { get; init; }
    public bool BaselineComparable { get; init; }
    public string? BaselineComparisonUnavailable { get; init; }
    public long DroppedActivityEvents { get; init; }
    public string[] Notes { get; init; } = [];
}

public sealed record DashboardPollingMetrics
{
    public long Calls { get; init; }
    public int Sessions { get; init; }
    // One observed session contributes the rolling window's one hour, matching the frozen baseline denominator.
    public double ObservedSessionHours { get; init; }
    public double? CallsPerSessionHour { get; init; }
    public long RequestBytes { get; init; }
    public long ReplyBytes { get; init; }
    public long? EstimatedReplyTokens { get; init; }
    public long ReplyTokenSamples { get; init; }
    public double? MeanReplyBytes { get; init; }
    public double? MedianReplyBytes { get; init; }
    public long GapSamples { get; init; }
    public double? MedianGapSeconds { get; init; }
    public double? P95GapSeconds { get; init; }
    public DateTimeOffset? FirstCallAt { get; init; }
    public DateTimeOffset? LastCallAt { get; init; }
}

public sealed record DashboardProjectionSavings
{
    public DateTimeOffset? CollectionStartedAt { get; init; }
    public DateTimeOffset? FirstSampleAt { get; init; }
    public DateTimeOffset? LastSampleAt { get; init; }
    public long Samples { get; init; }
    public long? SourceBytes { get; init; }
    public long? ReplyBytes { get; init; }
    public long? SavedBytes { get; init; }
    public long? EstimatedSourceTokens { get; init; }
    public long? EstimatedReplyTokens { get; init; }
    public long? EstimatedSavedTokens { get; init; }
    public long TokenSamples { get; init; }
}

/// <summary>Sanitized aggregate evidence shipped with the host, without source rows or private identifiers.</summary>
public sealed record DashboardEfficiencyBaseline
{
    public DateTimeOffset CapturedAt { get; init; }
    public int DurationSeconds { get; init; }
    public string Scope { get; init; } = "hub-wide";
    public string EvidenceSha256 { get; init; } = "";
    public DashboardPollingMetrics Watcher { get; init; } = new();
    public DashboardPollingMetrics NodeWatcher { get; init; } = new();
    public long BackgroundCalls { get; init; }
    public long BackgroundWireBytes { get; init; }
}

namespace CoCern.Contracts;

/// <summary>The named analytics queries, shared by <c>CoCern.Analytics</c> and the <c>analytics_query</c> tool's schema.</summary>
public static class AnalyticsQueryNames
{
    public const string TokensPerTask = "tokens_per_task";
    public const string RelayTokens = "relay_tokens";
    public const string UsageCoverage = "usage_coverage";
    public const string CallsPerTask = "calls_per_task";
    public const string MessagesPerTask = "messages_per_task";
    public const string ContextByRole = "context_by_role";
    public const string BlockedTime = "blocked_time";
    public const string ClaimConflictsByResource = "claim_conflicts_by_resource";
    public const string NoisiestTools = "noisiest_tools";
    public const string ManagerAssignmentLatency = "manager_assignment_latency";
    public const string QualityGuardrails = "quality_guardrails";
    public const string AttentionOutcomesByKind = "attention_outcomes_by_kind";
    /// <summary>Read from a precomputed file, not the snapshot, so the dashboard's combined snapshot report leaves it out.</summary>
    public const string TestEffort = "test_effort";

    // Manager alerts (Relay:Feedback:Alerts): run by the anomaly monitor only, so they stay out of the analytics_query schema.
    public const string TokensPerTaskByRole = "tokens_per_task_by_role";
    public const string CompactionRateByTool = "compaction_rate_by_tool";
    public const string SessionTokenOutliers = "session_token_outliers";

    /// <summary>The name that runs the tool's <c>sql</c> field instead, when <c>Relay:Analytics:AllowAdHocSql</c> is on.</summary>
    public const string AdHocSql = "sql";

    /// <summary>Every named query, in the order the documentation lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TokensPerTask, CallsPerTask, MessagesPerTask, ContextByRole, BlockedTime, ClaimConflictsByResource, NoisiestTools,
        ManagerAssignmentLatency, QualityGuardrails, AttentionOutcomesByKind, RelayTokens, UsageCoverage, TestEffort
    ];

    /// <summary>The alert queries. The anomaly monitor runs them; <c>analytics_query</c> does not list them.</summary>
    public static readonly IReadOnlyList<string> Alerts = [TokensPerTaskByRole, CompactionRateByTool, SessionTokenOutliers];

    /// <summary>Whether a monitor finding names a query it may run: a named query or an alert query.</summary>
    public static bool IsKnown(string name) => All.Contains(name) || Alerts.Contains(name);
}

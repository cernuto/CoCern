namespace PkRelay.Contracts;

/// <summary>The named analytics queries, shared by <c>PkRelay.Analytics</c> and the <c>analytics_query</c> tool's schema.</summary>
public static class AnalyticsQueryNames
{
    public const string TokensPerTask = "tokens_per_task";
    public const string CallsPerTask = "calls_per_task";
    public const string MessagesPerTask = "messages_per_task";
    public const string ContextByRole = "context_by_role";
    public const string BlockedTime = "blocked_time";
    public const string ClaimConflictsByResource = "claim_conflicts_by_resource";
    public const string NoisiestTools = "noisiest_tools";
    public const string ManagerAssignmentLatency = "manager_assignment_latency";
    public const string QualityGuardrails = "quality_guardrails";
    public const string AttentionOutcomesByKind = "attention_outcomes_by_kind";

    /// <summary>The name that runs the tool's <c>sql</c> field instead, when <c>Relay:Analytics:AllowAdHocSql</c> is on.</summary>
    public const string AdHocSql = "sql";

    /// <summary>Every named query, in the order the documentation lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TokensPerTask, CallsPerTask, MessagesPerTask, ContextByRole, BlockedTime, ClaimConflictsByResource, NoisiestTools,
        ManagerAssignmentLatency, QualityGuardrails, AttentionOutcomesByKind
    ];
}

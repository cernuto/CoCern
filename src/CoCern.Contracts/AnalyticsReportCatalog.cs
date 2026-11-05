namespace CoCern.Contracts;

/// <summary>Shared report labels and units; values and access checks remain in analytics.</summary>
public sealed record AnalyticsReportDefinition(string Name, string Title, string Description, string Unit);

public static class AnalyticsReportCatalog
{
    public static IReadOnlyList<AnalyticsReportDefinition> All { get; } =
    [
        new(AnalyticsQueryNames.TokensPerTask, "Reported tokens per task", "Average self-reported model tokens for tasks with a usage report; unreported tasks are excluded.", "tokens/task"),
        new(AnalyticsQueryNames.CallsPerTask, "Tool calls per task", "Average tool calls naming a task; calls without a task are excluded.", "calls/task"),
        new(AnalyticsQueryNames.MessagesPerTask, "Messages per task", "Average task messages for tasks touched during the period; direct chat messages are excluded.", "messages/task"),
        new(AnalyticsQueryNames.ContextByRole, "Reply bytes by role", "Average relay reply bytes per session by role, including background and unknown calls; this does not measure model context.", "bytes/session"),
        new(AnalyticsQueryNames.BlockedTime, "Blocked time", "Average hours of ended blocked spells; open spells are counted separately. Retained history may omit older transitions.", "hours"),
        new(AnalyticsQueryNames.ClaimConflictsByResource, "Claim conflicts", "Refused claims by recorded resource, or by task when the original resource is unknown.", "conflicts"),
        new(AnalyticsQueryNames.NoisiestTools, "Noisiest tools", "Reply bytes and calls by tool and reported origin. Unknown origin is not proof of model use. Daily totals have day precision.", "bytes"),
        new(AnalyticsQueryNames.ManagerAssignmentLatency, "Ready-to-claim latency", "Average minutes from ready to first claim; an assignment proxy, limited by retained transition history.", "minutes"),
        new(AnalyticsQueryNames.QualityGuardrails, "Quality guardrails", "Closure, reopening and action outcome event counts; retained history may be incomplete.", "events"),
        new(AnalyticsQueryNames.AttentionOutcomesByKind, "Attention offer outcomes", "Shown, accepted, later and dismissed events by kind; each kind has its own denominator.", "events"),
        new(AnalyticsQueryNames.RelayTokens, "Measured relay tokens", "Measured request and reply tokens by reported origin. These exclude other model context and do not measure billing or total model usage.", "tokens"),
        new(AnalyticsQueryNames.UsageCoverage, "Reported usage coverage", "Percentage of touched tasks with a successful model-usage report; sample counts show all touched tasks.", "%"),
        new(AnalyticsQueryNames.TestEffort, "Test effort", "Test run minutes, pointless minutes, tests added and deleted, and costly tests that never failed, from a precomputed file. Each row says measured or estimate.", "minutes")
    ];

    public static AnalyticsReportDefinition Get(string name) =>
        All.FirstOrDefault(report => report.Name == name) ?? throw new ArgumentOutOfRangeException(nameof(name));
}

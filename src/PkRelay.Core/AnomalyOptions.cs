using PkRelay.Contracts;

namespace PkRelay.Core;

/// <summary>When a named query's row is an anomaly: its value is at least <see cref="Ratio"/> times its baseline, both from at least <see cref="MinSamples"/> rows. A ratio of 0 turns the query's check off.</summary>
public sealed record AnomalyThreshold(double Ratio, int MinSamples)
{
    public static readonly AnomalyThreshold Default = new(RelayLimits.DefaultAnomalyRatio, RelayLimits.DefaultAnomalyMinSamples);
}

/// <summary>
/// Host settings for the anomaly monitor (<c>Relay:Attention:AnomalyInterval</c> and <c>Relay:Attention:AnomalyThresholds</c>). The monitor
/// also needs analytics (<c>Relay:Analytics:Enabled</c>, off by default) and attention turned on; otherwise it does nothing.
/// </summary>
public sealed record AnomalyOptions
{
    /// <summary>
    /// The named queries checked by default, each against <see cref="AnomalyThreshold.Default"/>. <c>attention_outcomes_by_kind</c> is
    /// not checked: its counts follow usage, and it is read to tune attention weights instead.
    /// </summary>
    public static readonly IReadOnlyList<string> CheckedQueries =
    [
        AnalyticsQueryNames.TokensPerTask, AnalyticsQueryNames.CallsPerTask, AnalyticsQueryNames.MessagesPerTask, AnalyticsQueryNames.ContextByRole,
        AnalyticsQueryNames.BlockedTime, AnalyticsQueryNames.ClaimConflictsByResource, AnalyticsQueryNames.NoisiestTools,
        AnalyticsQueryNames.ManagerAssignmentLatency, AnalyticsQueryNames.QualityGuardrails
    ];

    /// <summary>Time between checks. Zero turns the monitor off.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(RelayLimits.DefaultAnomalyIntervalMinutes);

    /// <summary>Per-query thresholds that replace <see cref="AnomalyThreshold.Default"/>.</summary>
    public IReadOnlyDictionary<string, AnomalyThreshold> Thresholds { get; init; } = new Dictionary<string, AnomalyThreshold>();

    /// <summary>The monitor's clock. Tests replace it to move between checks.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public bool Enabled => Interval > TimeSpan.Zero;

    /// <summary>The checks to run, in order: each checked query with its threshold, leaving out those turned off.</summary>
    public IEnumerable<(string Query, AnomalyThreshold Threshold)> Checks() =>
        CheckedQueries.Select(query => (query, Thresholds.TryGetValue(query, out var threshold) ? threshold : AnomalyThreshold.Default)).Where(check => check.Item2.Ratio > 0);

    /// <summary>Refuses settings outside their limits with a message that names the setting.</summary>
    public AnomalyOptions Validated()
    {
        if (Interval != TimeSpan.Zero && (Interval < TimeSpan.FromMinutes(RelayLimits.MinAnomalyIntervalMinutes) || Interval > TimeSpan.FromMinutes(RelayLimits.MaxAnomalyIntervalMinutes)))
            throw new InvalidOperationException($"Relay:Attention:AnomalyInterval must be 0 (off) or from {RelayLimits.MinAnomalyIntervalMinutes} minute to {RelayLimits.MaxAnomalyIntervalMinutes / 1_440} days.");
        foreach (var (query, threshold) in Thresholds)
        {
            var setting = "Relay:Attention:AnomalyThresholds:" + query;
            if (!CheckedQueries.Contains(query)) throw new InvalidOperationException($"{setting} names no checked query. Checked queries: {string.Join(", ", CheckedQueries)}.");
            if (threshold.Ratio != 0 && !(threshold.Ratio > 1 && double.IsFinite(threshold.Ratio))) throw new InvalidOperationException($"{setting}:Ratio must be 0 (off) or more than 1.");
            if (threshold.MinSamples is < 1 or > RelayLimits.MaxAnomalyMinSamples) throw new InvalidOperationException($"{setting}:MinSamples must be from 1 to {RelayLimits.MaxAnomalyMinSamples}.");
        }
        return this;
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// One repository's anomaly monitor. The hub runs the named analytics queries and passes what it found; this decides, from the monitor's
/// own state, what to raise or close at the repository's managers. Per named query it keeps the last raise (row key, period, time) and
/// whether its item is open, so:
/// <list type="bullet">
/// <item>a query raises at most one <c>anomaly</c> item (subject = the query name), with metric, value, baseline and sample size in the note;</item>
/// <item>the same query, row key and period is never raised twice, and a query that raised waits <see cref="RelayLimits.AnomalyCooldownHours"/> before raising again;</item>
/// <item>a later check that measures the raised row with enough samples and finds it back in range closes its item.</item>
/// </list>
/// Every raise and close carries a rising revision, so a late copy cannot reopen a closed item.
/// </summary>
public static class AnomalyMonitor
{
    internal static RelayReply Handle(AggregateContext context) => context.Operation switch
    {
        Operations.AnomalyStart => Start(context),
        Operations.AnomalySchedule => Schedule(context),
        Operations.AnomalyCheck => Check(context),
        _ => Error(UnknownTool)
    };

    /// <summary>Schedules the first check now, unless one is already scheduled. Starting again never moves a scheduled check.</summary>
    private static RelayReply Start(AggregateContext context)
    {
        if (!AnomalyStart.Decode(context.Args).TryGet(out _, out var malformed)) return Malformed(malformed);
        var state = new AnomalyState(context.State);
        state.NextCheckAt ??= context.Now;
        return Ok(new { nextCheckAt = state.NextCheckAt!.Value.ToString("O") });
    }

    /// <summary>
    /// A due check begins: the next one is scheduled and nothing else changes, so the due-work pump moves on at once. The hub then runs the
    /// queries off the grain and sends what it found (<see cref="Check"/>).
    /// </summary>
    private static RelayReply Schedule(AggregateContext context)
    {
        if (!AnomalySchedule.Decode(context.Args).TryGet(out var schedule, out var malformed)) return Malformed(malformed);
        if (schedule.IntervalSeconds < RelayLimits.MinAnomalyIntervalMinutes * 60L || schedule.IntervalSeconds > RelayLimits.MaxAnomalyIntervalMinutes * 60L) return Error(InvalidArguments, new { field = "intervalSeconds" });
        var state = new AnomalyState(context.State);
        state.CheckedAt = context.Now;
        state.NextCheckAt = context.Now.AddSeconds(schedule.IntervalSeconds);
        return Ok(new { nextCheckAt = state.NextCheckAt!.Value.ToString("O") });
    }

    /// <summary>
    /// One check's findings. Findings for a check no newer than the last applied one are ignored, and a lost run simply skips one check.
    /// </summary>
    private static RelayReply Check(AggregateContext context)
    {
        if (!AnomalyCheck.Decode(context.Args).TryGet(out var check, out var malformed)) return Malformed(malformed);
        var state = new AnomalyState(context.State);
        if (check.CheckAt <= (state.FindingsAt ?? DateTimeOffset.MinValue)) return Ok(new { applied = false, reason = "stale" });
        state.FindingsAt = check.CheckAt;
        var cooldown = TimeSpan.FromHours(RelayLimits.AnomalyCooldownHours);
        var (raised, closed, suppressed) = (0, 0, 0);
        foreach (var finding in check.Findings)
        {
            if (!AnalyticsQueryNames.IsKnown(finding.Query)) continue;
            var record = state.Check(finding.Query);
            if (finding.Anomaly is { } anomaly)
            {
                var key = Cap(anomaly.Key, RelayLimits.MaxIdentifierLength);
                var period = Cap(finding.Period, 32);
                // Cooldown first: a query that raised recently stays quiet, whichever row is out of range now.
                if (record.RaisedAt > context.Now - cooldown || record.Key == key && record.Period == period) { suppressed++; continue; }
                AttentionSources.ToManagers(context, new AttentionContent
                {
                    Kind = AttentionKinds.Anomaly, Subject = finding.Query, Priority = AttentionWeights.Normal, Note = Note(finding.Query, key, anomaly), Revision = state.NextRevision()
                });
                state.Raised(finding.Query, key, period, context.Now);
                raised++;
            }
            // Only a check that measured the raised row with enough samples, and found it in range, closes the item. Too little data is not "back in range".
            else if (record.Open && finding.InRange)
            {
                AttentionSources.ToManagers(context, new AttentionSelection { Subject = finding.Query, Kinds = [AttentionKinds.Anomaly], Revision = state.NextRevision() });
                record.Open = false;
                closed++;
            }
        }
        return Ok(new { raised, closed, suppressed, nextCheckAt = state.NextCheckAt?.ToString("O") ?? "" });
    }

    /// <summary>The row key of each query whose item is open, for the next check to measure.</summary>
    public static IReadOnlyDictionary<string, string> OpenKeys(string stateJson) => new AnomalyState(JsonNode.Parse(stateJson) as JsonObject ?? []).OpenKeys();

    /// <summary>For example "tokens_per_task all: 5400 vs 2100 baseline (n=12)".</summary>
    private static string Note(string query, string key, AnomalyRow anomaly) =>
        Cap($"{query} {key}: {Format(anomaly.Value)} vs {Format(anomaly.Baseline)} baseline (n={anomaly.Samples})", RelayLimits.MaxAttentionTextLength);

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Cap(string text, int length) => text.Length <= length ? text : text[..length];
}

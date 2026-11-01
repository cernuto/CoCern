using System.Globalization;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
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
        Operations.AnomalyCheck => Check(context),
        _ => Error(UnknownTool)
    };

    /// <summary>Schedules the first check now, unless one is already scheduled. Starting again never moves a scheduled check.</summary>
    private static RelayReply Start(AggregateContext context)
    {
        if (Text(context.State, "nextCheckAt").Length == 0) context.State["nextCheckAt"] = context.Now.ToString("O");
        return Ok(new { nextCheckAt = Text(context.State, "nextCheckAt") });
    }

    /// <summary>
    /// Two calls make one check. When the check is due, the grain sends <c>intervalSeconds</c> only: the next check is scheduled and
    /// nothing else changes, so the due-work pump moves on at once. When the queries are done, it sends <c>checkAt</c> (the due time) and
    /// the findings; findings for a check no newer than the last applied one are ignored, and a lost run simply skips one check.
    /// </summary>
    private static RelayReply Check(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        if (args.ContainsKey("intervalSeconds"))
        {
            var interval = Number(args, "intervalSeconds");
            if (interval < RelayLimits.MinAnomalyIntervalMinutes * 60L || interval > RelayLimits.MaxAnomalyIntervalMinutes * 60L) return Error(InvalidArguments, new { field = "intervalSeconds" });
            state["checkedAt"] = context.Now.ToString("O");
            state["nextCheckAt"] = context.Now.AddSeconds(interval).ToString("O");
        }
        if (!args.ContainsKey("checkAt")) return Ok(new { nextCheckAt = Text(state, "nextCheckAt") });
        var checkAt = Date(args, "checkAt");
        if (checkAt == DateTimeOffset.MinValue) return Error(InvalidArguments, new { field = "checkAt" });
        if (checkAt <= Date(state, "findingsAt")) return Ok(new { applied = false, reason = "stale" });
        state["findingsAt"] = checkAt.ToString("O");
        var checks = ObjectAt(state, "checks");
        var cooldown = TimeSpan.FromHours(RelayLimits.AnomalyCooldownHours);
        var (raised, closed, suppressed) = (0, 0, 0);
        foreach (var finding in (args["findings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var query = Text(finding, "query");
            if (!AnalyticsQueryNames.IsKnown(query)) continue;
            var record = ObjectAt(checks, query);
            if (finding["anomaly"] is JsonObject anomaly)
            {
                var key = Cap(Text(anomaly, "key"), RelayLimits.MaxIdentifierLength);
                var period = Cap(Text(finding, "period"), 32);
                // Cooldown first: a query that raised recently stays quiet, whichever row is out of range now.
                if (Date(record, "raisedAt") > context.Now - cooldown || Text(record, "key") == key && Text(record, "period") == period) { suppressed++; continue; }
                AttentionSources.ToManagers(context, Operations.AttentionRaise, new JsonObject
                {
                    ["kind"] = AttentionKinds.Anomaly, ["subject"] = query, ["priority"] = AttentionWeights.Normal, ["note"] = Note(query, key, anomaly), ["revision"] = NextRevision(state)
                });
                checks[query] = new JsonObject { ["open"] = true, ["key"] = key, ["period"] = period, ["raisedAt"] = context.Now.ToString("O") };
                raised++;
            }
            // Only a check that measured the raised row with enough samples, and found it in range, closes the item. Too little data is not "back in range".
            else if (Flag(record, "open") && finding["inRange"] is JsonValue inRange && inRange.TryGetValue<bool>(out var back) && back)
            {
                AttentionSources.ToManagers(context, Operations.AttentionClose, new JsonObject
                {
                    ["subject"] = query, ["kinds"] = new JsonArray(AttentionKinds.Anomaly), ["revision"] = NextRevision(state)
                });
                record["open"] = false;
                closed++;
            }
        }
        return Ok(new { raised, closed, suppressed, nextCheckAt = Text(state, "nextCheckAt") });
    }

    /// <summary>The row key of each query whose item is open, for the next check to measure.</summary>
    public static IReadOnlyDictionary<string, string> OpenKeys(string stateJson) =>
        (System.Text.Json.Nodes.JsonNode.Parse(stateJson)?["checks"] as JsonObject ?? []).Where(x => x.Value is JsonObject record && Flag(record, "open"))
            .ToDictionary(x => x.Key, x => Text(x.Value!.AsObject(), "key"), StringComparer.Ordinal);

    /// <summary>For example "tokens_per_task all: 5400 vs 2100 baseline (n=12)".</summary>
    private static string Note(string query, string key, JsonObject anomaly) =>
        Cap($"{query} {key}: {Format(anomaly["value"])} vs {Format(anomaly["baseline"])} baseline (n={Number(anomaly, "samples")})", RelayLimits.MaxAttentionTextLength);

    private static string Format(JsonNode? value) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number.ToString("0.##", CultureInfo.InvariantCulture) : "?";

    private static string Cap(string text, int length) => text.Length <= length ? text : text[..length];

    private static long NextRevision(JsonObject state)
    {
        var revision = Number(state, "attentionRevision") + 1;
        state["attentionRevision"] = revision;
        return revision;
    }
}

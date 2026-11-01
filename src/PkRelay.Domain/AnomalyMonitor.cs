using System.Globalization;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>
/// One repository's anomaly monitor. The hub runs the named analytics queries and passes what it found; this decides, from the monitor's
/// own state, what to raise or close at the repository's managers. Per named query it keeps the last raise (row key, period, time) and
/// whether its item is open, so:
/// <list type="bullet">
/// <item>a query raises at most one <c>anomaly</c> item (subject = the query name), with metric, value, baseline and sample size in the note;</item>
/// <item>the same query, row key and period is never raised twice, and a query that raised waits <see cref="RelayLimits.AnomalyCooldownHours"/> before raising again;</item>
/// <item>a later check that finds the query back in range closes its item.</item>
/// </list>
/// Every raise and close carries a rising revision, so a late copy cannot reopen a closed item.
/// </summary>
internal static class AnomalyMonitor
{
    public static RelayReply Handle(AggregateContext context) => context.Operation switch
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

    /// <summary>Applies one check's findings and schedules the next check <c>intervalSeconds</c> from now.</summary>
    private static RelayReply Check(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        var interval = Number(args, "intervalSeconds");
        if (interval < RelayLimits.MinAnomalyIntervalMinutes * 60L || interval > RelayLimits.MaxAnomalyIntervalMinutes * 60L) return Error(InvalidArguments, new { field = "intervalSeconds" });
        var checks = ObjectAt(state, "checks");
        var cooldown = TimeSpan.FromHours(RelayLimits.AnomalyCooldownHours);
        var (raised, closed, suppressed) = (0, 0, 0);
        foreach (var finding in (args["findings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var query = Text(finding, "query");
            if (!AnalyticsQueryNames.All.Contains(query)) continue;
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
            else if (Flag(record, "open"))
            {
                AttentionSources.ToManagers(context, Operations.AttentionClose, new JsonObject
                {
                    ["subject"] = query, ["kinds"] = new JsonArray(AttentionKinds.Anomaly), ["revision"] = NextRevision(state)
                });
                record["open"] = false;
                closed++;
            }
        }
        state["checkedAt"] = context.Now.ToString("O");
        state["nextCheckAt"] = context.Now.AddSeconds(interval).ToString("O");
        return Ok(new { raised, closed, suppressed, nextCheckAt = Text(state, "nextCheckAt") });
    }

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

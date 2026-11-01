using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using PkRelay.Contracts;
using PkRelay.Domain;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Core;

/// <summary>
/// One repository's anomaly monitor (keyed by the repository ID). Its next check is due work in its own state, so checks survive restarts
/// through the <c>due_work</c> table and the hub's due-work pump. Each check runs the named analytics queries against the latest snapshot,
/// compares each row's current value with its baseline (the previous period), and passes what it found to the domain's anomaly monitor
/// rules, which raise or close <c>anomaly</c> items at the repository's managers through the outbox.
/// The queries run detached from the grain, so a slow query never holds up the due-work pump.
/// The first manager session opened in a repository starts the monitor. With analytics, attention or the monitor turned off it does nothing.
/// </summary>
[GrainType("pkrelay.anomaly")]
public sealed class AnomalyMonitorGrain([PersistentState("anomaly", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services)
    : CoordinationGrain(state, grains, services), IAnomalyMonitorGrain
{
    protected override string Kind => AggregateKinds.Anomaly;

    protected override DateTimeOffset Now => (Services.GetService<AnomalyOptions>()?.Time ?? TimeProvider.System).GetUtcNow();

    private protected override async Task<bool> RunDueChecksAsync(StateEnvelope<CoordinationDocument> candidate, DateTimeOffset now, string workId)
    {
        if (!candidate.DueWork.Any(x => x.Kind == DueWorkKinds.AnomalyCheck && x.DueAt <= now && (workId.Length == 0 || x.Id == workId))) return true;
        // Off means nothing runs and nothing is raised. The due check is then dropped with the other due timers, so the monitor sleeps
        // until a manager session starts it again.
        var options = Services.GetService<AnomalyOptions>();
        var queries = Services.GetService<IAnalyticsQueries>();
        if (options is not { Enabled: true } || queries is null || Services.GetService<AttentionOptions>() is { Enabled: false }) return true;
        var repository = Text(JsonNode.Parse(candidate.Value.Json)!.AsObject(), "repositoryId");
        if (repository.Length == 0) return true;
        // The next check is scheduled and committed first, and the queries run detached, so the due-work pump (which also delivers every
        // outbox) never waits on DuckDB. The findings come back through __anomaly_check; a run that is lost skips one check.
        var open = AnomalyMonitor.OpenKeys(candidate.Value.Json);
        var monitor = Monitor(repository);
        var schedule = new JsonObject { ["repositoryId"] = repository, ["intervalSeconds"] = (long)options.Interval.TotalSeconds };
        if (!CoordinationStateMachine.Apply(candidate, Kind, this.GetPrimaryKeyString(), monitor, Operations.AnomalyCheck, schedule, now).Ok) return true;
        CoordinationStateMachine.ScheduleLeases(candidate);
        if (!await CommitAsync(candidate)) return false;
        var self = GrainFactory.GetGrain<IAnomalyMonitorGrain>(this.GetPrimaryKeyString());
        var checks = options.Checks().ToArray();
        _ = Task.Run(() => RunCheckAsync(self, queries, repository, checks, open, now));
        return true;
    }

    private static RelayCaller Monitor(string repository) => new("relay-anomaly-monitor", false, [repository], true);

    /// <summary>Runs one check's queries off the grain and sends the findings back. Any failure skips this check.</summary>
    private static async Task RunCheckAsync(IAnomalyMonitorGrain self, IAnalyticsQueries queries, string repository, IReadOnlyList<(string Query, AnomalyThreshold Threshold)> checks,
        IReadOnlyDictionary<string, string> open, DateTimeOffset checkAt)
    {
        try
        {
            var findings = await FindAsync(queries, repository, checks, open);
            var args = new JsonObject { ["repositoryId"] = repository, ["checkAt"] = checkAt.ToString("O"), ["findings"] = findings };
            await self.ExecuteAsync(Monitor(repository), Operations.AnomalyCheck, args.ToJsonString());
        }
        catch (Exception) { /* A lost run skips one check; the next one is already scheduled. */ }
    }

    /// <summary>
    /// Runs each check's query, within <see cref="RelayLimits.AnomalyCheckBudgetSeconds"/> in all. A query that fails is skipped; with no
    /// snapshot yet, or once the budget is spent, the rest wait for the next check.
    /// </summary>
    private static async Task<JsonArray> FindAsync(IAnalyticsQueries queries, string repository, IEnumerable<(string Query, AnomalyThreshold Threshold)> checks, IReadOnlyDictionary<string, string> open)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(RelayLimits.AnomalyCheckBudgetSeconds));
        var findings = new JsonArray();
        foreach (var (query, threshold) in checks)
        {
            AnalyticsResult result;
            try { result = await queries.RunNamedAsync(query, repository, default, budget.Token); }
            catch (AnalyticsException exception) when (exception.Error == AnalyticsError.Unavailable) { break; }
            catch (Exception) when (!budget.IsCancellationRequested) { continue; }
            catch (Exception) { break; }
            findings.Add(Finding(query, result, threshold, open.TryGetValue(query, out var key) ? key : null));
        }
        return findings;
    }

    /// <summary>
    /// What one named query's result shows: its query, its period (the snapshot's UTC date) and, when a row is out of range, the worst one
    /// as <c>anomaly</c> (key, value, baseline, samples).
    /// <list type="bullet">
    /// <item>Up: a row is out of range when its value is at least <c>Ratio</c> times its baseline (the previous period's value, above zero),
    /// both from at least <c>MinSamples</c> rows. Samples are the current value's.</item>
    /// <item>Down, for rows that are better when higher (<c>quality_guardrails</c> closures): out of range when its baseline, from at least
    /// <c>MinSamples</c> rows, is at least <c>DropRatio</c> times its value. Samples are the baseline's, since few current rows is the signal.</item>
    /// </list>
    /// With no row out of range, <c>inRange</c> is true only when <paramref name="openKey"/> (the row of the query's open item) was measured
    /// with enough samples: too little data is not "back in range".
    /// </summary>
    public static JsonObject Finding(string query, AnalyticsResult result, AnomalyThreshold threshold, string? openKey = null)
    {
        var finding = new JsonObject { ["query"] = query, ["period"] = result.SnapshotAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        int Column(string name) => result.Columns.ToList().IndexOf(name);
        var (key, cur, prev, curN, prevN) = (Column("key"), Column("cur"), Column("prev"), Column("cur_n"), Column("prev_n"));
        if (key < 0 || cur < 0 || prev < 0 || curN < 0 || prevN < 0) return finding;
        (string Key, double Value, double Baseline, long Samples, double Severity)? worst = null;
        var openMeasured = false;
        foreach (var row in result.Rows)
        {
            var name = Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? "";
            var down = query == AnalyticsQueryNames.QualityGuardrails && name == "closures";
            var value = Real(row[cur]) ?? (down ? 0 : null);
            if (value is not { } current || Real(row[prev]) is not { } baseline || Real(row[curN]) is not { } samples || Real(row[prevN]) is not { } baselineSamples) continue;
            var measured = baselineSamples >= threshold.MinSamples && (down || samples >= threshold.MinSamples) && baseline > 0;
            if (name == openKey && measured) openMeasured = true;
            if (!measured) continue;
            var severity = down ? (threshold.DropRatio > 0 && baseline >= current * threshold.DropRatio ? (current <= 0 ? double.MaxValue : baseline / current) : 0)
                : (threshold.Ratio > 0 && current >= baseline * threshold.Ratio ? current / baseline : 0);
            if (severity > 0 && (worst is null || severity > worst.Value.Severity)) worst = (name, current, baseline, (long)(down ? baselineSamples : samples), severity);
        }
        if (worst is { } found)
            finding["anomaly"] = new JsonObject { ["key"] = found.Key, ["value"] = Math.Round(found.Value, 2), ["baseline"] = Math.Round(found.Baseline, 2), ["samples"] = found.Samples };
        else if (openMeasured) finding["inRange"] = true;
        return finding;
    }

    private static double? Real(object? value) => value switch
    {
        null => null,
        double number => double.IsFinite(number) ? number : null,
        IConvertible convertible when value is not string and not bool => convertible.ToDouble(CultureInfo.InvariantCulture),
        _ => null
    };
}

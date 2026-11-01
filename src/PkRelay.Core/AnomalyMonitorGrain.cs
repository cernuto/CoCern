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
        var findings = await FindAsync(queries, repository, options.Checks());
        var first = candidate.Effects.Count;
        var monitor = new RelayCaller("relay-anomaly-monitor", false, [repository], true);
        JsonObject Check(JsonArray found) => new() { ["repositoryId"] = repository, ["intervalSeconds"] = (long)options.Interval.TotalSeconds, ["findings"] = found };
        // A check the rules refuse still schedules the next one, so the monitor never stops on its own.
        if (!CoordinationStateMachine.Apply(candidate, Kind, this.GetPrimaryKeyString(), monitor, Operations.AnomalyCheck, Check(findings), now).Ok)
            CoordinationStateMachine.Apply(candidate, Kind, this.GetPrimaryKeyString(), monitor, Operations.AnomalyCheck, Check([]), now);
        CoordinationStateMachine.StampEffects(candidate.Effects, first, monitor);
        CoordinationStateMachine.ScheduleLeases(candidate);
        return await CommitAsync(candidate);
    }

    /// <summary>
    /// Runs each check's query, within <see cref="RelayLimits.AnomalyCheckBudgetSeconds"/> in all. A query that fails is skipped; with no
    /// snapshot yet, or once the budget is spent, the rest wait for the next check. Queries run off the Orleans scheduler (see the analytics service).
    /// </summary>
    private static async Task<JsonArray> FindAsync(IAnalyticsQueries queries, string repository, IEnumerable<(string Query, AnomalyThreshold Threshold)> checks)
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
            findings.Add(Finding(query, result, threshold));
        }
        return findings;
    }

    /// <summary>
    /// What one named query's result shows: its query, its period (the snapshot's UTC date) and, when a row is out of range, the worst one
    /// as <c>anomaly</c> (key, value, baseline, samples). A row is out of range when its value is at least the threshold's ratio times its
    /// baseline (the previous period's value, above zero), both from at least the minimum sample size. A row that is better when higher
    /// (closures in <c>quality_guardrails</c>) is never an anomaly.
    /// </summary>
    public static JsonObject Finding(string query, AnalyticsResult result, AnomalyThreshold threshold)
    {
        var finding = new JsonObject { ["query"] = query, ["period"] = result.SnapshotAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        int Column(string name) => result.Columns.ToList().IndexOf(name);
        var (key, cur, prev, curN, prevN) = (Column("key"), Column("cur"), Column("prev"), Column("cur_n"), Column("prev_n"));
        if (key < 0 || cur < 0 || prev < 0 || curN < 0 || prevN < 0) return finding;
        (string Key, double Value, double Baseline, long Samples)? worst = null;
        foreach (var row in result.Rows)
        {
            var name = Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? "";
            if (query == AnalyticsQueryNames.QualityGuardrails && name == "closures") continue;
            if (Real(row[cur]) is not { } value || Real(row[prev]) is not { } baseline || Real(row[curN]) is not { } samples || Real(row[prevN]) is not { } baselineSamples) continue;
            if (samples < threshold.MinSamples || baselineSamples < threshold.MinSamples || baseline <= 0 || value < baseline * threshold.Ratio) continue;
            if (worst is null || value / baseline > worst.Value.Value / worst.Value.Baseline) worst = (name, value, baseline, (long)samples);
        }
        if (worst is { } found)
            finding["anomaly"] = new JsonObject { ["key"] = found.Key, ["value"] = Math.Round(found.Value, 2), ["baseline"] = Math.Round(found.Baseline, 2), ["samples"] = found.Samples };
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

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Domain.JsonFields;

namespace CoCern.Core;

/// <summary>
/// One repository's anomaly monitor, started by its first manager session. Each check (durable due work) runs the named queries on the
/// latest snapshot, detached from the grain, and the domain raises or closes <c>anomaly</c> items for managers. Off with analytics,
/// attention or the monitor off.
/// </summary>
[GrainType("cocern.anomaly")]
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
        if (!ApplyInternal(candidate, monitor, Operations.AnomalyCheck, schedule, now).Ok) return true;
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

    /// <summary>One query's result judged by <paramref name="threshold"/>; see <see cref="AnomalyThreshold.Judge"/>.</summary>
    public static JsonObject Finding(string query, AnalyticsResult result, AnomalyThreshold threshold, string? openKey = null) => threshold.Judge(query, result, openKey);
}

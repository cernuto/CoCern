using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Storage;

namespace CoCern.Host.Dashboard;

/// <summary>Viewer-driven bounded live reads. The semaphore coalesces concurrent requests; there is no idle polling.</summary>
internal sealed class DashboardEfficiency(IServiceProvider services, DashboardSecurity security, RepositoryCatalog repositories,
    ReadEfficiencyMeasurements measurements)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);
    private readonly DashboardEfficiencyBaseline? baseline = DashboardEfficiencyBaselineLoader.Load();
    private const int MaximumCachedScopes = 128;

    public async Task<IResult> GetAsync(HttpContext context)
    {
        var supplied = context.Request.Query["repositoryId"];
        string? repository = null;
        if (supplied.Count > 0)
        {
            var value = supplied.ToString();
            if (supplied.Count != 1 || string.IsNullOrWhiteSpace(value) || value.Length > RelayLimits.MaxIdentifierLength || value.Any(char.IsControl))
                return Results.BadRequest(new { message = "Choose one repository, or omit it for the hub-wide view." });
            repository = repositories.Find(value);
            if (repository is null) return Results.StatusCode(403);
        }
        if (!Authorized()) return Results.StatusCode(403);
        var cancellation = context.RequestAborted;
        await gate.WaitAsync(cancellation);
        DashboardEfficiencySnapshot snapshot;
        try
        {
            var key = repository ?? "";
            if (cache.TryGetValue(key, out var cached) && Environment.TickCount64 - cached.AtMilliseconds < 5_000)
                snapshot = cached.Snapshot;
            else
            {
                snapshot = await ReadAsync(repository, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (cache.Count >= MaximumCachedScopes && !cache.ContainsKey(key))
                    cache.Remove(cache.MinBy(entry => entry.Value.AtMilliseconds).Key);
                cache[key] = new(snapshot, Environment.TickCount64);
            }
        }
        finally { gate.Release(); }
        cancellation.ThrowIfCancellationRequested();
        // Enrollment and repository authority can change while the bounded read is in flight.
        if (!Authorized()) return Results.StatusCode(403);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(snapshot);

        bool Authorized()
        {
            var caller = security.Caller(context.User, DateTimeOffset.UtcNow);
            return caller is not null && (repository is null ? caller.IsAdministrator : caller.MayAccess(repository));
        }
    }

    private async Task<DashboardEfficiencySnapshot> ReadAsync(string? repository, CancellationToken cancellation)
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-1);
        DashboardEfficiencySnapshot? activity = null;
        try
        {
            if (services.GetService<SqliteStore>() is { } store)
                activity = await store.ReadDashboardEfficiencyAsync(repository, from, now, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { /* Paths, SQL and exception details never enter the dashboard contract. */ }
        var savings = measurements.Read(repository, from, now);
        var comparable = baseline is { Scope: "hub-wide", DurationSeconds: 3600 } && repository is null
            && activity is { Truncated: false, Unavailable: null, DroppedActivityEvents: 0 }
            && activity.ActivityCoverageStart is { } coverageStart && coverageStart <= from;
        return (activity ?? new DashboardEfficiencySnapshot
        {
            CollectedAt = now, WindowStart = from, WindowEnd = now,
            Scope = repository is null ? "hub-wide" : "repository", RepositoryId = repository,
            Unavailable = "Live activity is unavailable or busy. A later viewer refresh will retry."
        }) with
        {
            TaskBrief = savings.TaskBrief, FocusedDocument = savings.FocusedDocument,
            Truncated = activity?.Truncated == true || savings.Truncated,
            Baseline = baseline, BaselineComparable = comparable,
            BaselineComparisonUnavailable = baseline is null ? "The shipped aggregate baseline is unavailable."
                : repository is not null ? "The frozen baseline is hub-wide; a repository selection is not comparable."
                : activity?.DroppedActivityEvents > 0 ? "Recorded activity was dropped; baseline changes remain unknown because this window may be incomplete."
                : activity?.ActivityCoverageStart is null || activity.ActivityCoverageStart > from ? "Activity history does not yet cover a full hour; baseline changes remain unknown."
                : !comparable ? "A complete live rolling hour is required for a baseline comparison." : null,
            Notes =
            [
                "Activity is raw hub-recorded calls in the rolling hour; older hourly rollups are excluded. The view refreshes only while requested.",
                "Watcher groups are background get_status calls with the reported wake-watcher or node origin, matching the baseline population. Origin is untrusted adapter metadata, not proof of model activity or authorization.",
                "Calls per observed session-hour assigns the one-hour window to each session seen in that group, matching the baseline denominator; it does not measure connected session lifetime.",
                "Cadence is the distribution of consecutive calls within each observed repository/session, excluding window edges. Missing pairs remain unknown.",
                "Savings compare exact source and returned content from the same actual read version. Samples start with this hub process; no historical savings are inferred. Token counts are relay estimates, not provider billing.",
                "Token totals cover only rows with recorded estimates; byte and token sample counts can differ. Negative savings mean the returned projection was larger than its source."
            ]
        };
    }

    private sealed record Cached(DashboardEfficiencySnapshot Snapshot, long AtMilliseconds);
}

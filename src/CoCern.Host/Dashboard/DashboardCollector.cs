using CoCern.Contracts.Dashboard;
using CoCern.Core;
using System.Text;
using System.Text.Json;

namespace CoCern.Host.Dashboard;

public sealed class DashboardCollector : BackgroundService
{
    private DashboardSnapshot current;
    private readonly DashboardLogBuffer logs;
    private readonly DashboardConnections connections;
    private readonly IRelayObservationSource source;
    private readonly DashboardTrafficBuffer traffic;
    private readonly DashboardSecurity security;
    private readonly RelayApplication application;
    private readonly DocCacheActivity? docCache;
    private DateTimeOffset nextDatabaseAt;
    private DateTimeOffset nextStandingAt;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DashboardCollector(DashboardLogBuffer logs, DashboardConnections connections, IRelayObservationSource source, DashboardTrafficBuffer traffic, DashboardSecurity security, RelayApplication application, DocCacheActivity? docCache = null)
    {
        this.logs = logs;
        this.connections = connections;
        this.source = source;
        this.traffic = traffic;
        this.security = security;
        this.application = application;
        this.docCache = docCache;
        var now = DateTimeOffset.UtcNow;
        current = new()
        {
            InstanceId = Guid.NewGuid().ToString("N"), ObservedAt = now,
            Host = new() { Name = Environment.MachineName, StartedAt = now, Version = typeof(DashboardCollector).Assembly.GetName().Version?.ToString() ?? "unknown" },
            Warnings = ["The first observation is pending."]
        };
    }

    public DashboardSnapshot Current => Volatile.Read(ref current);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                // No timer or storage reads exist until a real dashboard stream connects.
                await connections.WaitForViewerAsync(stoppingToken);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                do
                {
                    var snapshot = Current;
                    try
                    {
                        if (security.Enabled && connections.HasViewers)
                        {
                            // SQLite performs synchronous bounded copies; keep that work on the sampler worker.
                            snapshot = await Task.Run(() => CollectAsync(snapshot, stoppingToken), stoppingToken);
                            Volatile.Write(ref current, snapshot);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception) { logs.CollectionFailed(); }
                    // Revalidate stream grants even when sampling failed or the viewer grant was removed.
                    try { connections.Publish(snapshot); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception) { logs.CollectionFailed(); }
                } while (connections.HasViewers && await timer.WaitForNextTickAsync(stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<DashboardSnapshot> CollectAsync(DashboardSnapshot previous, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        DashboardSnapshot snapshot;
        try
        {
            var observation = await source.ReadObservationAsync(now, application.IncarnationId, cancellationToken);
            snapshot = observation is null
                ? previous with { Warnings = [.. previous.Warnings.Where(warning => warning != "Observation is busy; the previous observation is retained."), "Observation is busy; the previous observation is retained."] }
                : previous with
                {
                    ObservedAt = now, Machines = observation.Machines, Sessions = observation.Sessions, SessionsTruncated = observation.SessionsTruncated, Interactions = observation.Interactions, Warnings = observation.Warnings,
                    Host = previous.Host with { MachineCount = observation.Machines.Length, SessionCount = observation.SessionCount, ReachableCount = observation.Sessions.Count(session => session.Reachable) }
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logs.CollectionFailed();
            snapshot = previous with { Warnings = ["Observation is unavailable; the previous observation is retained."] };
        }
        if (now >= nextDatabaseAt)
        {
            nextDatabaseAt = now.AddSeconds(15);
            try
            {
                var database = await source.ReadDatabaseMetricsAsync(cancellationToken: cancellationToken);
                snapshot = snapshot with { Database = database ?? previous.Database with { Warning = "Database metrics are busy; the previous metrics are retained." } };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { snapshot = snapshot with { Database = previous.Database with { Warning = "Database metrics are unavailable; the previous metrics are retained." } }; }
        }
        // Standing preferences change rarely, so they are read on the database-metrics cadence. The read gives up at once when the
        // writer is busy; its next time moves on only after a read succeeds, so a busy or failed read is retried on the next tick.
        if (now >= nextStandingAt)
        {
            try
            {
                if (await source.ReadStandingAsync(cancellationToken) is { } standing)
                {
                    snapshot = snapshot with { Standing = standing.Boards, StandingTruncated = standing.Truncated };
                    nextStandingAt = now.AddSeconds(15);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { logs.CollectionFailed(); }
        }
        // Token totals are counted once at startup and tip rule state is in memory, so this read is free.
        try { snapshot = snapshot with { Tokens = application.TokenSummary() }; }
        catch (Exception) { logs.CollectionFailed(); }
        return Bound(snapshot with { Revision = previous.Revision + 1, Logs = logs.Snapshot(), Traffic = traffic.Snapshot(), DocCache = docCache?.Snapshot(now) ?? new() });
    }

    private static DashboardSnapshot Bound(DashboardSnapshot snapshot)
    {
        // Measure the complete frame once. Exact encoded element lengths then bound trimming in one pass.
        // The remaining KiB accommodates the SignalR event envelope within its 256 KiB transport buffer.
        const int maximumBytes = 255 * 1024;
        const string warning = "The observation was trimmed to its serialized size limit.";
        var encoded = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var bytes = encoded.Length;
        if (bytes <= maximumBytes) return snapshot;
        using var document = JsonDocument.Parse(encoded);
        var root = document.RootElement;
        bytes -= EncodedLength(root.GetProperty("logs")) - 2;
        bytes += JsonSerializer.SerializeToUtf8Bytes(new[] { warning }, JsonOptions).Length - EncodedLength(root.GetProperty("warnings"));
        snapshot = snapshot with { Logs = [], Warnings = [warning] };
        if (bytes <= maximumBytes) return snapshot;

        // Indexed names can give way without losing directory totals or active-grain metadata.
        var encodedGrains = root.GetProperty("docCache").GetProperty("grains");
        var grainBytes = EncodedLength(encodedGrains);
        var grains = snapshot.DocCache.Grains.ToArray();
        for (var index = grains.Length - 1; index >= 0 && bytes > maximumBytes; index--)
        {
            var paths = encodedGrains[index].GetProperty("documentPaths");
            var before = bytes;
            var count = Trim(paths);
            grainBytes -= before - bytes;
            if (count < grains[index].DocumentPaths.Length)
                grains[index] = grains[index] with { DocumentPaths = grains[index].DocumentPaths[..count] };
        }
        snapshot = snapshot with { DocCache = snapshot.DocCache with { Grains = grains } };
        if (bytes <= maximumBytes) return snapshot;

        bytes -= grainBytes - 2;
        if (!snapshot.DocCache.Truncated) bytes--;
        snapshot = snapshot with { DocCache = snapshot.DocCache with { Grains = [], Truncated = true } };
        if (bytes <= maximumBytes) return snapshot;

        // Standing stays readable through task_get, so it gives way before the live inventory.
        bytes -= EncodedLength(root.GetProperty("standing")) - 2;
        if (!snapshot.StandingTruncated) bytes--;
        snapshot = snapshot with { Standing = [], StandingTruncated = true };
        if (bytes <= maximumBytes) return snapshot;

        bytes -= EncodedLength(root.GetProperty("interactions").GetProperty("edges")) - 2;
        if (!snapshot.Interactions.Truncated) bytes--;
        snapshot = snapshot with { Interactions = snapshot.Interactions with { Edges = [], Truncated = true } };
        if (bytes <= maximumBytes) return snapshot;

        if (!snapshot.SessionsTruncated) bytes--; // "false" becomes "true".
        var sessions = Trim(root.GetProperty("sessions"));
        snapshot = snapshot with { Sessions = snapshot.Sessions[..sessions], SessionsTruncated = true };
        if (bytes <= maximumBytes) return snapshot;
        var machines = Trim(root.GetProperty("machines"));
        snapshot = snapshot with { Machines = snapshot.Machines[..machines] };
        if (bytes <= maximumBytes) return snapshot;
        var traffic = Trim(root.GetProperty("traffic"));
        snapshot = snapshot with { Traffic = snapshot.Traffic[..traffic] };
        if (bytes <= maximumBytes) return snapshot;
        if (!snapshot.Database.Truncated) bytes--;
        var tables = Trim(root.GetProperty("database").GetProperty("tables"));
        snapshot = snapshot with { Database = snapshot.Database with { Tables = snapshot.Database.Tables[..tables], Truncated = true } };
        if (bytes <= maximumBytes) return snapshot;

        // Scalar database metadata can itself be oversized. This fixed, explicit fallback cannot export it.
        return snapshot with
        {
            Host = new() { StartedAt = snapshot.Host.StartedAt },
            Machines = [], Sessions = [], SessionsTruncated = true, Traffic = [], Standing = [], StandingTruncated = true,
            Database = new() { ObservedAt = snapshot.Database.ObservedAt, Truncated = true, Warning = warning }
        };

        int Trim(JsonElement array)
        {
            var count = array.GetArrayLength();
            while (bytes > maximumBytes && count > 0)
            {
                bytes -= EncodedLength(array[count - 1]) + (count > 1 ? 1 : 0);
                count--;
            }
            return count;
        }
        static int EncodedLength(JsonElement element) => Encoding.UTF8.GetByteCount(element.GetRawText());
    }
}


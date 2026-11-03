using CoCern.Analytics;
using CoCern.Contracts;
using CoCern.Storage;

namespace CoCern.Host;

/// <summary>Host settings for snapshots (<c>Relay:Analytics:*</c>). Analytics is on unless <see cref="Enabled"/> is turned off.</summary>
internal sealed record AnalyticsSettings(bool Enabled, TimeSpan SnapshotInterval, string Directory)
{
    public const string FilePrefix = "snapshot-";

    public static AnalyticsSettings From(IConfiguration configuration, IHostEnvironment environment, SqliteStore store)
    {
        var interval = configuration.GetValue("Relay:Analytics:SnapshotInterval", TimeSpan.FromMinutes(RelayLimits.DefaultAnalyticsSnapshotMinutes));
        if (interval < TimeSpan.FromMinutes(RelayLimits.MinAnalyticsSnapshotMinutes))
            throw new InvalidOperationException($"Relay:Analytics:SnapshotInterval must be at least {RelayLimits.MinAnalyticsSnapshotMinutes} minute.");
        // Task.Delay takes at most about 49 days; a longer wait would stop the hub.
        if (interval > TimeSpan.FromDays(30)) throw new InvalidOperationException("Relay:Analytics:SnapshotInterval must be at most 30 days.");
        // By default snapshots sit beside the database, which is data/analytics for the default data/relay.db.
        var configured = configuration["Relay:Analytics:Directory"];
        var directory = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "analytics") : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
        return new(configuration.GetValue("Relay:Analytics:Enabled", true), interval, directory);
    }
}

/// <summary>
/// Takes an analytics snapshot at startup and then on each interval: a scrubbed backup of the live database under a new name, which
/// <see cref="AnalyticsService"/> then switches to. The analytics service deletes each replaced snapshot once no query uses it.
/// </summary>
internal sealed class AnalyticsSnapshotService(SqliteStore store, AnalyticsSettings settings, IServiceProvider services, ILogger<AnalyticsSnapshotService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        await Task.Yield();
        var analytics = services.GetRequiredService<AnalyticsService>();
        RemoveStaleSnapshots();
        // Without a usable sqlite extension no snapshot can load: say so once, and analytics_query answers AnalyticsUnavailable.
        if (AnalyticsExtension.Problem(analytics.ExtensionDirectory) is { } problem)
        {
            analytics.MarkUnavailable(problem);
            logger.LogWarning("Analytics is unavailable: {Reason} The hub runs without it. Only windows_amd64 is bundled; elsewhere run 'CoCern.Host --install-analytics-extension <folder>' and set Relay:Analytics:ExtensionDirectory.", problem);
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TakeSnapshotAsync(analytics, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            try { await Task.Delay(settings.SnapshotInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Takes one snapshot and switches to it. Returns its path, or null when it failed (the reason is logged).</summary>
    internal async Task<string?> TakeSnapshotAsync(AnalyticsService analytics, CancellationToken cancellationToken)
    {
        var takenAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(settings.Directory, $"{AnalyticsSettings.FilePrefix}{takenAt:yyyyMMddTHHmmssfff}Z-{Guid.NewGuid():N}.db");
        try
        {
            await store.CreateAnalyticsSnapshotAsync(path, cancellationToken);
            // Capture completion is the report cutoff: activity flushed before the copy must not be
            // excluded merely because it arrived after the filename's start time. This describes
            // the completed snapshot, not the freshness of the live database after it was copied.
            await analytics.SwitchSnapshotAsync(path, DateTimeOffset.UtcNow, cancellationToken);
            return path;
        }
        catch (Exception exception)
        {
            Delete(path);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning("Analytics snapshot failed; queries keep the previous snapshot. {Reason}", exception.Message);
            return null;
        }
    }

    /// <summary>Snapshots left by an earlier run are never read again.</summary>
    private void RemoveStaleSnapshots()
    {
        if (!Directory.Exists(settings.Directory)) return;
        foreach (var file in Directory.EnumerateFiles(settings.Directory, AnalyticsSettings.FilePrefix + "*")) Delete(file);
    }

    private static void Delete(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(path) + "*"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

/// <summary>
/// What the Documents page shows from one doc cache snapshot at one moment, reduced to a key: every count, name and commit, and each time
/// as it reads ("Just now", "3m ago", the minute of a timestamp). The page ticks every second and each snapshot is a new object, so the
/// view compares keys and redraws only when what it shows changed. A key that collides only delays a redraw until the next change, at the
/// latest the next minute.
/// </summary>
public static class DocCacheRender
{
    public static int Key(DashboardDocCache cache, DateTimeOffset now)
    {
        var key = new HashCode();
        // Each snapshot has a new ObservedAt; only how it reads is shown.
        key.Add(cache.ObservedAt is null);
        key.Add(DashboardFormat.Timestamp(cache.ObservedAt));
        key.Add(DashboardFormat.Ago(cache.ObservedAt, now));
        key.Add(cache.ActiveDocuments);
        key.Add(cache.ActiveDirectories);
        key.Add(cache.Truncated);
        key.Add(cache.Grains.Length);
        foreach (var grain in cache.Grains)
        {
            key.Add(grain.Kind);
            key.Add(grain.Repository);
            key.Add(grain.Path);
            key.Add(grain.Loaded);
            key.Add(grain.Commit);
            key.Add(grain.Sections);
            key.Add(grain.Reads);
            key.Add(grain.IndexRuns);
            key.Add(grain.Documents);
            key.Add(grain.ActivatedAt);
            key.Add(DashboardFormat.Ago(grain.ActivatedAt, now));
            key.Add(grain.DocumentPaths.Length);
            foreach (var path in grain.DocumentPaths)
            {
                key.Add(path);
                if (grain.DocumentUsage.GetValueOrDefault(path) is not { } usage) { key.Add(false); continue; }
                key.Add(true);
                key.Add(usage.Searches);
                key.Add(usage.MatchedSections);
                key.Add(usage.SectionReads);
                key.Add(usage.LastUsedAt);
                key.Add(usage.LastUsedAt is null ? null : DashboardFormat.Ago(usage.LastUsedAt, now));
                key.Add(usage.TrackingSince);
            }
        }
        return key.ToHashCode();
    }
}

using System.Collections.Concurrent;
using CoCern.Contracts.Dashboard;

namespace CoCern.Core;

/// <summary>Local activation observations only. Reading this registry never calls or retains a grain.</summary>
public sealed class DocCacheActivity
{
    internal const int MaxDocumentPaths = 200;
    internal const int MaxDocumentPathLength = 1024;
    private readonly ConcurrentDictionary<Guid, DashboardDocGrain> active = new();

    public void Update(Guid activation, DashboardDocGrain grain) => active[activation] = grain;
    public void Remove(Guid activation) => active.TryRemove(activation, out _);

    public DashboardDocCache Snapshot(DateTimeOffset now, int limit = 200)
    {
        var rows = active.Values.ToArray();
        var shown = rows.OrderBy(row => row.Kind == "Directory" ? 0 : 1)
            .ThenBy(row => row.Repository, StringComparer.Ordinal)
            .ThenBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.Path, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 0, 200)).ToArray();
        var candidates = shown.Select(row => row.Kind == "Directory"
            ? row.DocumentPaths.Where(path => !string.IsNullOrEmpty(path) && path.Length <= MaxDocumentPathLength)
                .Order(StringComparer.Ordinal).Take(MaxDocumentPaths).ToArray()
            : []).ToArray();
        var counts = new int[shown.Length];
        var pathsRemaining = MaxDocumentPaths;
        // Share the global budget across displayed directories before giving any directory another path.
        for (var pathIndex = 0; pathsRemaining > 0; pathIndex++)
        {
            var added = false;
            for (var index = 0; index < shown.Length && pathsRemaining > 0; index++)
            {
                if (pathIndex >= candidates[index].Length) continue;
                counts[index]++;
                pathsRemaining--;
                added = true;
            }
            if (!added) break;
        }
        for (var index = 0; index < shown.Length; index++)
        {
            var row = shown[index];
            var paths = candidates[index][..counts[index]];
            shown[index] = row with
            {
                DocumentPaths = paths,
                DocumentUsage = paths.Where(row.DocumentUsage.ContainsKey)
                    .ToDictionary(path => path, path => row.DocumentUsage[path], StringComparer.Ordinal)
            };
        }
        return new()
        {
            ObservedAt = now, ActiveDocuments = rows.Count(row => row.Kind == "Document"),
            ActiveDirectories = rows.Count(row => row.Kind == "Directory"),
            Grains = shown, Truncated = shown.Length < rows.Length
        };
    }
}

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
        var shown = rows.OrderBy(row => row.Repository, StringComparer.Ordinal)
            .ThenBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.Path, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 0, 200)).ToArray();
        var pathsRemaining = MaxDocumentPaths;
        for (var index = 0; index < shown.Length; index++)
        {
            var row = shown[index];
            var paths = row.Kind == "Directory"
                ? row.DocumentPaths.Where(path => !string.IsNullOrEmpty(path) && path.Length <= MaxDocumentPathLength)
                    .Order(StringComparer.Ordinal).Take(pathsRemaining).ToArray()
                : [];
            shown[index] = row with { DocumentPaths = paths };
            pathsRemaining -= paths.Length;
        }
        return new()
        {
            ObservedAt = now, ActiveDocuments = rows.Count(row => row.Kind == "Document"),
            ActiveDirectories = rows.Count(row => row.Kind == "Directory"),
            Grains = shown, Truncated = shown.Length < rows.Length
        };
    }
}

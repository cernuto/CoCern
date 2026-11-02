using System.Collections.Concurrent;
using CoCern.Contracts.Dashboard;

namespace CoCern.Core;

/// <summary>Local activation observations only. Reading this registry never calls or retains a grain.</summary>
public sealed class DocCacheActivity
{
    private readonly ConcurrentDictionary<Guid, DashboardDocGrain> active = new();

    public void Update(Guid activation, DashboardDocGrain grain) => active[activation] = grain;
    public void Remove(Guid activation) => active.TryRemove(activation, out _);

    public DashboardDocCache Snapshot(DateTimeOffset now, int limit = 200)
    {
        var rows = active.Values.ToArray();
        var shown = rows.OrderBy(row => row.Repository, StringComparer.Ordinal)
            .ThenBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.Path, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 0, 200)).ToArray();
        return new()
        {
            ObservedAt = now, ActiveDocuments = rows.Count(row => row.Kind == "Document"),
            ActiveDirectories = rows.Count(row => row.Kind == "Directory"),
            Grains = shown, Truncated = shown.Length < rows.Length
        };
    }
}

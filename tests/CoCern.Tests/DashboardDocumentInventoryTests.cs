using System.Reflection;
using System.Text.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Host.Dashboard;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardDocumentInventoryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Inventory_paths_are_bounded_across_directories_and_do_not_change_active_counts()
    {
        var activity = new DocCacheActivity();
        foreach (var repository in new[] { "a", "b" })
        {
            var paths = Enumerable.Range(0, 150).Select(index => $"docs/{index:D3}.md").Append(new string('x', 1025)).ToArray();
            activity.Update(Guid.NewGuid(), Directory(repository, paths));
        }
        activity.Update(Guid.NewGuid(), new() { Kind = "Document", Repository = "a", Path = "docs/000.md" });
        var snapshot = activity.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(2, snapshot.ActiveDirectories);
        Assert.Equal(1, snapshot.ActiveDocuments);
        var pathsShown = snapshot.Grains.SelectMany(Paths).ToArray();
        Assert.Equal(200, pathsShown.Length);
        Assert.All(pathsShown, path => Assert.InRange(path.Length, 1, 1024));
        Assert.Equal(150, Paths(snapshot.Grains.Single(row => row.Kind == "Directory" && row.Repository == "a")).Length);
        Assert.All(snapshot.Grains.Where(row => row.Kind == "Directory"), row => Assert.Equal(151, row.Documents));
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public void Oversized_inventory_trims_paths_before_discarding_the_directory_observation()
    {
        var paths = Enumerable.Range(0, 200).Select(index => $"docs/{index:D3}/" + new string('\u754c', 1000)).ToArray();
        var snapshot = new DashboardSnapshot { DocCache = new() { ActiveDirectories = 1, Grains = [Directory("repo", paths)] } };
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length > 255 * 1024);
        var bound = typeof(DashboardCollector).GetMethod("Bound", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = Assert.IsType<DashboardSnapshot>(bound.Invoke(null, [snapshot]));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length <= 255 * 1024);
        var directory = Assert.Single(result.DocCache.Grains);
        Assert.Equal("repo", directory.Repository);
        Assert.Equal(200, directory.Documents);
        Assert.True(Paths(directory).Length < 200);
    }

    private static DashboardDocGrain Directory(string repository, string[] paths) => JsonSerializer.Deserialize<DashboardDocGrain>(
        JsonSerializer.Serialize(new { kind = "Directory", repository, documents = paths.Length, documentPaths = paths }), JsonOptions)!;

    private static string[] Paths(DashboardDocGrain grain)
    {
        var json = JsonSerializer.SerializeToElement(grain, JsonOptions);
        return json.TryGetProperty("documentPaths", out var paths) ? paths.EnumerateArray().Select(path => path.GetString()!).ToArray() : [];
    }
}

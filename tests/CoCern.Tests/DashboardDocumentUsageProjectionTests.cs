using System.Reflection;
using System.Text.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Host.Dashboard;
using Xunit;

namespace CoCern.Tests;

public class DashboardDocumentUsageProjectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset TrackingSince = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    [Fact]
    public void Nineteen_repositories_share_the_global_path_budget_in_stable_order()
    {
        var activity = new DocCacheActivity();
        foreach (var index in Enumerable.Range(0, 19).Reverse())
        {
            var paths = Enumerable.Range(0, 150).Reverse().Select(path => $"docs/{path:D3}.md").Append(new string('x', 1025)).ToArray();
            activity.Update(Guid.NewGuid(), Directory($"repo-{index:D2}", paths, paths.ToDictionary(path => path, _ => Usage(1))));
        }

        var snapshot = activity.Snapshot(TrackingSince.AddDays(1));
        Assert.Equal(19, snapshot.ActiveDirectories);
        Assert.Equal(200, snapshot.Grains.Sum(row => row.DocumentPaths.Length));
        Assert.Equal(Enumerable.Range(0, 19).Select(index => $"repo-{index:D2}"), snapshot.Grains.Select(row => row.Repository));
        Assert.All(snapshot.Grains, row =>
        {
            Assert.InRange(row.DocumentPaths.Length, 10, 11);
            Assert.Equal(row.DocumentPaths.Order(StringComparer.Ordinal), row.DocumentPaths);
            Assert.Equal(151, row.Documents);
            var usage = UsageJson(row);
            Assert.Equal(row.DocumentPaths, usage.EnumerateObject().Select(item => item.Name));
            Assert.All(usage.EnumerateObject(), item => Assert.Equal(1, item.Value.GetProperty("searches").GetInt64()));
        });
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public void Active_documents_do_not_crowd_repository_inventory_out_of_the_row_budget()
    {
        var activity = new DocCacheActivity();
        foreach (var index in Enumerable.Range(0, 19).Reverse())
        {
            var paths = Enumerable.Range(0, 30).Select(path => $"docs/{path:D3}.md").ToArray();
            activity.Update(Guid.NewGuid(), Directory($"repo-{index:D2}", paths, paths.ToDictionary(path => path, _ => Usage(1))));
        }
        foreach (var index in Enumerable.Range(0, 220).Reverse())
            activity.Update(Guid.NewGuid(), new()
            {
                Kind = "Document", Repository = "repo-00", Path = $"docs/{index:D3}.md"
            });

        var snapshot = activity.Snapshot(TrackingSince.AddDays(1));
        Assert.Equal(220, snapshot.ActiveDocuments);
        Assert.Equal(19, snapshot.ActiveDirectories);
        Assert.Equal(200, snapshot.Grains.Length);
        Assert.True(snapshot.Truncated);
        var directories = snapshot.Grains.Where(row => row.Kind == "Directory").ToArray();
        Assert.Equal(Enumerable.Range(0, 19).Select(index => $"repo-{index:D2}"), directories.Select(row => row.Repository));
        Assert.Equal(directories, snapshot.Grains.Take(19));
        Assert.Equal(200, directories.Sum(row => row.DocumentPaths.Length));
        Assert.All(directories, row =>
        {
            Assert.InRange(row.DocumentPaths.Length, 10, 11);
            Assert.Equal(row.DocumentPaths, UsageJson(row).EnumerateObject().Select(item => item.Name));
        });
        var documents = snapshot.Grains.Skip(19).ToArray();
        Assert.All(documents, row => Assert.Equal("Document", row.Kind));
        Assert.Equal(Enumerable.Range(0, 181).Select(index => $"docs/{index:D3}.md"), documents.Select(row => row.Path));
    }

    [Fact]
    public void Untracked_paths_stay_unknown_while_tracked_zero_and_tracking_period_survive()
    {
        var activity = new DocCacheActivity();
        activity.Update(Guid.NewGuid(), Directory("repo", ["docs/a.md", "docs/b.md"], new()
        {
            ["docs/a.md"] = Usage(0),
            ["docs/no-longer-indexed.md"] = Usage(7)
        }));

        var row = Assert.Single(activity.Snapshot(TrackingSince.AddDays(1)).Grains);
        Assert.Equal(new[] { "docs/a.md", "docs/b.md" }, row.DocumentPaths);
        var usage = UsageJson(row);
        var tracked = Assert.Single(usage.EnumerateObject());
        Assert.Equal("docs/a.md", tracked.Name);
        Assert.Equal(0, tracked.Value.GetProperty("searches").GetInt64());
        Assert.Equal(0, tracked.Value.GetProperty("matchedSections").GetInt64());
        Assert.Equal(0, tracked.Value.GetProperty("sectionReads").GetInt64());
        Assert.Equal(TrackingSince, tracked.Value.GetProperty("trackingSince").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, tracked.Value.GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public void Encoded_frame_trimming_keeps_usage_with_its_path_and_covers_every_repository()
    {
        var activity = new DocCacheActivity();
        foreach (var index in Enumerable.Range(0, 19))
        {
            var paths = Enumerable.Range(0, 30).Select(path => $"docs/{path:D3}/" + new string('\u754c', 990) + "\\\".md").ToArray();
            activity.Update(Guid.NewGuid(), Directory($"repo-{index:D2}", paths, paths.ToDictionary(path => path, _ => Usage(4))));
        }
        var snapshot = new DashboardSnapshot { DocCache = activity.Snapshot(TrackingSince.AddDays(1)) };
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length > 255 * 1024);

        var bound = typeof(DashboardCollector).GetMethod("Bound", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = Assert.IsType<DashboardSnapshot>(bound.Invoke(null, [snapshot]));

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length <= 255 * 1024);
        Assert.Equal(19, result.DocCache.Grains.Length);
        Assert.Equal(19, result.DocCache.ActiveDirectories);
        Assert.False(result.DocCache.Truncated);
        Assert.All(result.DocCache.Grains, row =>
        {
            Assert.NotEmpty(row.DocumentPaths);
            Assert.True(row.DocumentPaths.Length < row.Documents);
            Assert.Equal(30, row.Documents);
            var usage = UsageJson(row);
            Assert.Equal(row.DocumentPaths, usage.EnumerateObject().Select(item => item.Name));
            Assert.All(usage.EnumerateObject(), item =>
            {
                Assert.Equal(4, item.Value.GetProperty("searches").GetInt64());
                Assert.Equal(8, item.Value.GetProperty("matchedSections").GetInt64());
                Assert.Equal(3, item.Value.GetProperty("sectionReads").GetInt64());
                Assert.Equal(TrackingSince, item.Value.GetProperty("trackingSince").GetDateTimeOffset());
                Assert.Equal(TrackingSince.AddHours(1), item.Value.GetProperty("lastUsedAt").GetDateTimeOffset());
            });
        });
        var counts = result.DocCache.Grains.Select(row => row.DocumentPaths.Length).ToArray();
        Assert.InRange(counts.Max() - counts.Min(), 0, 1);
    }

    private static object Usage(long searches) => new
    {
        searches,
        matchedSections = searches * 2,
        sectionReads = searches == 0 ? 0 : 3,
        lastUsedAt = searches == 0 ? (DateTimeOffset?)null : TrackingSince.AddHours(1),
        trackingSince = TrackingSince
    };

    private static DashboardDocGrain Directory(string repository, string[] paths, Dictionary<string, object> usage) =>
        JsonSerializer.Deserialize<DashboardDocGrain>(JsonSerializer.Serialize(new
        {
            kind = "Directory", repository, documents = paths.Length, documentPaths = paths, documentUsage = usage
        }, JsonOptions), JsonOptions)!;

    private static JsonElement UsageJson(DashboardDocGrain row) =>
        JsonSerializer.SerializeToElement(row, JsonOptions).GetProperty("documentUsage");
}

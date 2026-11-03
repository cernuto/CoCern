using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Services;
using Xunit;

namespace CoCern.Tests;

/// <summary>The Documents view redraws only when what it shows changed: not on every clock tick, nor for a snapshot with the same content.</summary>
public sealed class DashboardDocCacheRenderTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 3, 12, 0, 10, TimeSpan.Zero);

    [Fact]
    public void A_tick_or_a_new_snapshot_of_the_same_content_keeps_the_key()
    {
        var key = DocCacheRender.Key(Cache(Observed), Observed.AddSeconds(1));
        Assert.Equal(key, DocCacheRender.Key(Cache(Observed), Observed.AddSeconds(2)));
        // The next snapshot, two seconds on, still reads "just now" in the same minute.
        Assert.Equal(key, DocCacheRender.Key(Cache(Observed.AddSeconds(2)), Observed.AddSeconds(3)));
        // "Last used 5m ago" and "Activated 2h ago" read the same 30 seconds later.
        Assert.Equal(DocCacheRender.Key(Cache(Observed), Observed.AddSeconds(1)), DocCacheRender.Key(Cache(Observed.AddSeconds(30)), Observed.AddSeconds(31)));
    }

    [Fact]
    public void A_changed_count_name_or_relative_time_changes_the_key()
    {
        var now = Observed.AddSeconds(1);
        var key = DocCacheRender.Key(Cache(Observed), now);
        Assert.NotEqual(key, DocCacheRender.Key(Cache(Observed, searches: 4), now));
        Assert.NotEqual(key, DocCacheRender.Key(Cache(Observed, path: "docs/other.md"), now));
        Assert.NotEqual(key, DocCacheRender.Key(Cache(Observed) with { ActiveDocuments = 2 }, now));
        Assert.NotEqual(key, DocCacheRender.Key(Cache(Observed) with { Truncated = true }, now));
        // A minute later "Last used 5m ago" reads "6m ago"; with no snapshot for a while, "Updated just now" reads "12s ago".
        Assert.NotEqual(key, DocCacheRender.Key(Cache(Observed.AddMinutes(1)), Observed.AddMinutes(1).AddSeconds(1)));
        Assert.NotEqual(key, DocCacheRender.Key(Cache(Observed), Observed.AddSeconds(12)));
        Assert.NotEqual(key, DocCacheRender.Key(new DashboardDocCache(), now));
    }

    private static DashboardDocCache Cache(DateTimeOffset observedAt, long searches = 3, string path = "docs/a.md") => new()
    {
        ObservedAt = observedAt, ActiveDirectories = 1, ActiveDocuments = 1,
        Grains =
        [
            new DashboardDocGrain
            {
                Kind = "Directory", Repository = "repo", ActivatedAt = Observed.AddHours(-2), Loaded = true, Commit = "abc", Documents = 1, DocumentPaths = [path],
                DocumentUsage = new(StringComparer.Ordinal)
                {
                    [path] = new DashboardDocUsage { Searches = searches, MatchedSections = 2, SectionReads = 1, LastUsedAt = Observed.AddMinutes(-5), TrackingSince = Observed.AddDays(-1) }
                }
            },
            new DashboardDocGrain { Kind = "Document", Repository = "repo", Path = path, ActivatedAt = Observed.AddHours(-2), Loaded = true, Commit = "abc", Sections = 4, Reads = 1, IndexRuns = 1 }
        ]
    };
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Orleans;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

/// <summary>These tests start many git processes and hubs; they run on their own so other classes' timing checks keep their margin.</summary>
[CollectionDefinition(nameof(DocCacheTests), DisableParallelization = true)]
public sealed class DocCacheCollection;

[Collection(nameof(DocCacheTests))]
public sealed class DocCacheTests
{
    [Fact]
    public async Task Document_utilization_counts_completed_searches_and_successful_section_reads_per_path()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCached alpha.\n", ["docs/b.md"] = "# Beta\n\nCached beta.\n" });
        await using var host = DocHost(git);
        await SyncAsync(host);
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        var initial = DocumentUsage(host, "repo", "docs/a.md");
        Assert.Equal(0, UsageCount(initial, "Searches"));
        Assert.Equal(0, UsageCount(initial, "MatchedSections"));
        Assert.Equal(0, UsageCount(initial, "SectionReads"));
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("LastUsedAt").ValueKind);
        var since = initial.GetProperty("TrackingSince").GetDateTimeOffset();
        Assert.True(since <= DateTimeOffset.UtcNow);

        var found = await directory.SearchAsync("cached", null, null, 1, TimeSpan.FromSeconds(10));
        Assert.Single(found.Hits); // Each document matched, even though the directory returns only one hit.
        await directory.SearchAsync("unfindablequux", null, "docs/a.md", 3, TimeSpan.FromSeconds(10));
        Assert.NotNull(await directory.SectionAsync("docs/a.md", "alpha"));
        Assert.Null(await directory.SectionAsync("docs/a.md", "missing"));
        Assert.Null(await directory.SectionAsync("docs/missing.md", "alpha"));
        Assert.True((await directory.SearchAsync("cached", null, "docs/missing.md", 3, TimeSpan.FromSeconds(10))).PathUnknown);
        Assert.Empty((await directory.SearchAsync("cached", "not-a-spec", null, 3, TimeSpan.FromSeconds(10))).Hits);

        var a = DocumentUsage(host, "repo", "docs/a.md");
        var b = DocumentUsage(host, "repo", "docs/b.md");
        Assert.Equal((2L, 1L, 1L), (UsageCount(a, "Searches"), UsageCount(a, "MatchedSections"), UsageCount(a, "SectionReads")));
        Assert.Equal((1L, 1L, 0L), (UsageCount(b, "Searches"), UsageCount(b, "MatchedSections"), UsageCount(b, "SectionReads")));
        Assert.Equal(since, a.GetProperty("TrackingSince").GetDateTimeOffset());
        Assert.True(a.GetProperty("LastUsedAt").GetDateTimeOffset() >= since);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => directory.SearchAsync("unfindablequux", null, "docs/a.md", 1, TimeSpan.FromSeconds(10))));
        Assert.Equal(8, UsageCount(DocumentUsage(host, "repo", "docs/a.md"), "Searches"));
        await Task.WhenAll(Enumerable.Range(0, 6).SelectMany(_ => new Task[]
        {
            directory.SearchAsync("unfindablequux", null, "docs/a.md", 1, TimeSpan.FromSeconds(10)),
            directory.SectionAsync("docs/a.md", "alpha")
        }));
        a = DocumentUsage(host, "repo", "docs/a.md");
        Assert.Equal((14L, 7L), (UsageCount(a, "Searches"), UsageCount(a, "SectionReads")));
    }

    [Fact]
    public async Task Document_utilization_survives_restart_and_prunes_removed_paths_without_crossing_repositories()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCached alpha.\n", ["docs/old.md"] = "# Old\n\nCached old.\n" });
        var settings = new Dictionary<string, string?> { ["Relay:DocCache:Repositories:1:Id"] = "repo2", ["Relay:DocCache:Repositories:1:ClonePath"] = git.Clone };
        var database = Path.Combine(Path.GetTempPath(), $"cocern-doc-usage-{Guid.NewGuid():N}.db");
        DateTimeOffset since;
        await using (var host = DocHost(git, settings: settings, databasePath: database))
        {
            await SyncAsync(host);
            await host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo2", null, CancellationToken.None);
            var grains = host.Services.GetRequiredService<IGrainFactory>();
            var directory = grains.GetGrain<IDocDirectoryGrain>("repo");
            await directory.SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(10));
            await directory.SectionAsync("docs/a.md", "alpha");
            since = DocumentUsage(host, "repo", "docs/a.md").GetProperty("TrackingSince").GetDateTimeOffset();
            Assert.Equal(0, UsageCount(DocumentUsage(host, "repo2", "docs/a.md"), "Searches"));
            await grains.GetGrain<IDocumentGrain>(DocKeys.Document("repo", "docs/a.md")).DropAsync();
            await directory.SearchAsync("cached", null, "docs/a.md", 3, TimeSpan.FromSeconds(10));
            Assert.Equal(2, UsageCount(DocumentUsage(host, "repo", "docs/a.md"), "Searches"));
        }
        await using (var host = DocHost(git, settings: settings, databasePath: database))
        {
            var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
            await directory.ReadAsync();
            var retained = DocumentUsage(host, "repo", "docs/a.md");
            Assert.Equal((2L, 1L), (UsageCount(retained, "Searches"), UsageCount(retained, "SectionReads")));
            Assert.Equal(since, retained.GetProperty("TrackingSince").GetDateTimeOffset());
            git.Move("docs/old.md", "docs/new.md");
            await SyncAsync(host);
            var observation = DirectoryObservation(host, "repo");
            Assert.False(observation.GetProperty("DocumentUsage").TryGetProperty("docs/old.md", out _));
            Assert.Equal(0, UsageCount(DocumentUsage(host, "repo", "docs/new.md"), "Searches"));
            Assert.Equal(2, UsageCount(DocumentUsage(host, "repo", "docs/a.md"), "Searches"));
        }
    }

    [Fact]
    public async Task Document_utilization_excludes_failed_and_late_search_answers()
    {
        using var git = GitRepositories.Create(new() { ["docs/fast.md"] = "# Cached\n\nFast cached.\n", ["docs/slow.md"] = "# Cached\n\nSlow cached.\n" });
        var watcher = new WatchingGit { SlowPath = "docs/slow.md" };
        await using var host = DocHost(git, watcher);
        await SyncAsync(host);
        // slow.md's read waits until it is released, after the search gave up on it; fast.md's real git show has a generous budget.
        watcher.HoldSlow();
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var directory = grains.GetGrain<IDocDirectoryGrain>("repo");
        var result = await directory.SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.NotSearched);
        Assert.Equal(1, UsageCount(DocumentUsage(host, "repo", "docs/fast.md"), "Searches"));
        Assert.Equal(0, UsageCount(DocumentUsage(host, "repo", "docs/slow.md"), "Searches"));
        // The late read lands in the search index, and still counts no search of slow.md.
        watcher.ReleaseSlow();
        await WaitUntilAsync(async () => (await grains.GetGrain<IDocSearchGrain>("repo").StatsAsync()).Indexed == 2);
        Assert.Equal(0, UsageCount(DocumentUsage(host, "repo", "docs/slow.md"), "Searches"));
        // The late read did land in the search index: deactivate it, so the next search reads slow.md again, and that read fails.
        var management = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);
        await WaitUntilAsync(async () => !(await management.GetActiveGrains(DocKeys.SearchType)).Any(id => id.Key.ToString() == "repo"));
        watcher.FailPath = "docs/slow.md";
        Assert.Equal(1, (await directory.SearchAsync("cached", null, "docs/slow.md", 3, TimeSpan.FromSeconds(10))).NotSearched);
        Assert.Equal(0, UsageCount(DocumentUsage(host, "repo", "docs/slow.md"), "Searches"));
    }

    [Fact]
    public async Task Document_usage_is_counted_in_memory_and_written_on_deactivation_not_on_every_lookup()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCached alpha.\n" });
        await using var host = DocHost(git, usageWrites: TimeSpan.FromHours(1));
        await SyncAsync(host);
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var directory = grains.GetGrain<IDocDirectoryGrain>("repo");
        for (var i = 0; i < 3; i++) Assert.Single((await directory.SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(10))).Hits);
        Assert.NotNull(await directory.SectionAsync("docs/a.md", "alpha"));

        // The dashboard sees every use at once; storage holds none of them yet.
        var shown = DocumentUsage(host, "repo", "docs/a.md");
        Assert.Equal((3L, 3L, 1L), (UsageCount(shown, "Searches"), UsageCount(shown, "MatchedSections"), UsageCount(shown, "SectionReads")));
        Assert.Equal((0L, 0L), (await PersistedUsageAsync(host, "docs/a.md", "Searches"), await PersistedUsageAsync(host, "docs/a.md", "SectionReads")));

        // Idle collection writes them before the directory goes, and the next activation starts from them.
        var management = grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);
        await WaitUntilAsync(async () => await PersistedUsageAsync(host, "docs/a.md", "Searches") == 3);
        Assert.Equal((3L, 1L), (await PersistedUsageAsync(host, "docs/a.md", "MatchedSections"), await PersistedUsageAsync(host, "docs/a.md", "SectionReads")));
        await directory.ReadAsync();
        shown = DocumentUsage(host, "repo", "docs/a.md");
        Assert.Equal((3L, 1L), (UsageCount(shown, "Searches"), UsageCount(shown, "SectionReads")));
    }

    [Fact]
    public async Task A_document_utilization_write_failure_does_not_fail_the_document_response_and_keeps_the_counts_for_the_next_write()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCached alpha.\n" });
        await using var host = DocHost(git, usageWrites: TimeSpan.FromMilliseconds(100));
        await SyncAsync(host);
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_doc_usage BEFORE UPDATE ON grain_state WHEN NEW.state_name = 'docs' BEGIN SELECT RAISE(FAIL, 'usage write unavailable'); END;";
        await command.ExecuteNonQueryAsync();
        Assert.Single((await directory.SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(10))).Hits);
        Assert.NotNull(await directory.SectionAsync("docs/a.md", "alpha"));
        // Every timed write fails meanwhile; the counts stay, shown and unwritten.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal((1L, 1L), (UsageCount(DocumentUsage(host, "repo", "docs/a.md"), "Searches"), UsageCount(DocumentUsage(host, "repo", "docs/a.md"), "SectionReads")));
        Assert.Equal(0, await PersistedUsageAsync(host, "docs/a.md", "Searches"));

        // Storage is back: the next timed write stores them once, and the next sync still applies.
        command.CommandText = "DROP TRIGGER fail_doc_usage;";
        await command.ExecuteNonQueryAsync();
        await WaitUntilAsync(async () => await PersistedUsageAsync(host, "docs/a.md", "Searches") == 1);
        Assert.Equal(1, await PersistedUsageAsync(host, "docs/a.md", "SectionReads"));
        await directory.SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(10));
        git.Commit(new() { ["docs/a.md"] = "# Alpha\n\nUpdated cached alpha.\n" });
        await SyncAsync(host);
        Assert.Equal(git.Head(), (await directory.ReadAsync()).Commit);
        Assert.Equal((2L, 1L), (UsageCount(DocumentUsage(host, "repo", "docs/a.md"), "Searches"), UsageCount(DocumentUsage(host, "repo", "docs/a.md"), "SectionReads")));
        await WaitUntilAsync(async () => await PersistedUsageAsync(host, "docs/a.md", "Searches") == 2);
    }

    private static JsonElement DirectoryObservation(RelayHostFixture host, string repository) => JsonSerializer.SerializeToElement(
        Assert.Single(host.Services.GetRequiredService<DocCacheActivity>().Snapshot(DateTimeOffset.UtcNow).Grains, row => row.Kind == "Directory" && row.Repository == repository));

    private static JsonElement DocumentUsage(RelayHostFixture host, string repository, string path)
    {
        var observation = DirectoryObservation(host, repository);
        Assert.True(observation.TryGetProperty("DocumentUsage", out var usage), "Directory observations must include persistent per-document utilization.");
        Assert.True(usage.TryGetProperty(path, out var document), $"A tracked zero or utilization record is required for {repository}/{path}.");
        return document;
    }

    private static long UsageCount(JsonElement usage, string name) => usage.GetProperty(name).GetInt64();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_null_persisted_document_usage_record_starts_a_new_period_or_remains_unknown_when_migration_cannot_write(bool failMigration)
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCached alpha.\n", ["docs/b.md"] = "# Beta\n\nCached beta.\n" });
        var database = Path.Combine(Path.GetTempPath(), $"cocern-doc-usage-null-{Guid.NewGuid():N}.db");
        DateTimeOffset retainedSince;
        await using (var host = DocHost(git, databasePath: database))
        {
            await SyncAsync(host);
            await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(10));
            retainedSince = DocumentUsage(host, "repo", "docs/b.md").GetProperty("TrackingSince").GetDateTimeOffset();
        }

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE grain_state SET payload = json_set(payload, '$.Usage.\"docs/a.md\"', NULL) WHERE state_name = 'docs';";
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        if (failMigration)
        {
            command.CommandText = "CREATE TRIGGER fail_usage_migration BEFORE UPDATE ON grain_state WHEN NEW.state_name = 'docs' BEGIN SELECT RAISE(FAIL, 'usage migration unavailable'); END;";
            await command.ExecuteNonQueryAsync();
        }

        var restartedAt = DateTimeOffset.UtcNow;
        await using var restarted = DocHost(git, databasePath: database);
        var directory = restarted.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        Assert.Equal(2, (await directory.ReadAsync()).Entries.Length);
        var observation = DirectoryObservation(restarted, "repo");
        Assert.Equal(1, UsageCount(DocumentUsage(restarted, "repo", "docs/b.md"), "Searches"));
        Assert.Equal(retainedSince, DocumentUsage(restarted, "repo", "docs/b.md").GetProperty("TrackingSince").GetDateTimeOffset());
        if (failMigration)
        {
            Assert.False(observation.GetProperty("DocumentUsage").TryGetProperty("docs/a.md", out _));
            Assert.NotNull(await directory.SectionAsync("docs/a.md", "alpha"));
            // Counted in memory from that read on, which starts a.md's tracking period; written once storage takes it.
            var counted = DocumentUsage(restarted, "repo", "docs/a.md");
            Assert.Equal((0L, 1L), (UsageCount(counted, "Searches"), UsageCount(counted, "SectionReads")));
            Assert.True(counted.GetProperty("TrackingSince").GetDateTimeOffset() >= restartedAt);
            command.CommandText = "DROP TRIGGER fail_usage_migration;";
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            var migrated = DocumentUsage(restarted, "repo", "docs/a.md");
            Assert.Equal((0L, 0L, 0L), (UsageCount(migrated, "Searches"), UsageCount(migrated, "MatchedSections"), UsageCount(migrated, "SectionReads")));
            Assert.Equal(JsonValueKind.Null, migrated.GetProperty("LastUsedAt").ValueKind);
            Assert.True(migrated.GetProperty("TrackingSince").GetDateTimeOffset() >= restartedAt);
        }
        Assert.Single((await directory.SearchAsync("cached", null, "docs/a.md", 3, TimeSpan.FromSeconds(10))).Hits);
        var recovered = DocumentUsage(restarted, "repo", "docs/a.md");
        Assert.Equal((1L, 1L, failMigration ? 1L : 0L), (UsageCount(recovered, "Searches"), UsageCount(recovered, "MatchedSections"), UsageCount(recovered, "SectionReads")));
        Assert.True(recovered.GetProperty("TrackingSince").GetDateTimeOffset() >= restartedAt);
    }

    [Fact]
    public async Task Directory_inventory_names_survive_cold_documents_and_refresh_after_sync()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCached text.\n", ["docs/old.md"] = "# Old\n" });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher);
        await SyncAsync(host);
        var activity = host.Services.GetRequiredService<DocCacheActivity>();
        var commands = watcher.Commands;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var snapshot = activity.Snapshot(DateTimeOffset.UtcNow);
            Assert.Equal(0, snapshot.ActiveDocuments);
            var directory = Assert.Single(snapshot.Grains, row => row.Kind == "Directory");
            Assert.Equal(2, directory.Documents);
            Assert.Equal(["docs/a.md", "docs/old.md"], InventoryPaths(directory));
        }
        Assert.Equal(commands, watcher.Commands);

        git.Move("docs/old.md", "docs/new.md");
        await SyncAsync(host);
        await WaitUntilAsync(() => Task.FromResult(activity.Snapshot(DateTimeOffset.UtcNow).ActiveDocuments == 0));
        var refreshed = Assert.Single(activity.Snapshot(DateTimeOffset.UtcNow).Grains, row => row.Kind == "Directory");
        Assert.Equal(2, refreshed.Documents);
        Assert.Equal(["docs/a.md", "docs/new.md"], InventoryPaths(refreshed));
    }

    private static string[] InventoryPaths(CoCern.Contracts.Dashboard.DashboardDocGrain grain)
    {
        var json = JsonSerializer.SerializeToElement(grain);
        Assert.True(json.TryGetProperty("DocumentPaths", out var paths), "Directory observations must include indexed document paths.");
        return paths.EnumerateArray().Select(path => path.GetString()!).ToArray();
    }

    [Fact]
    public async Task Active_cache_observations_follow_loading_and_deactivation_without_touching_grains()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCached text.\n" });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher);
        await SyncAsync(host);
        var activity = host.Services.GetRequiredService<DocCacheActivity>();
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var first = activity.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, first.ActiveDirectories);
        var directory = Assert.Single(first.Grains, row => row.Kind == "Directory");
        Assert.Equal(1, directory.Documents);
        Assert.Equal(0, first.ActiveDocuments);
        // A search answers from the repository's shared index and loads no document; reading a section loads that one.
        Assert.Single((await grains.GetGrain<IDocDirectoryGrain>("repo").SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(10))).Hits);
        Assert.Equal(0, activity.Snapshot(DateTimeOffset.UtcNow).ActiveDocuments);
        Assert.NotNull(await grains.GetGrain<IDocDirectoryGrain>("repo").SectionAsync("docs/a.md", "alpha"));
        first = activity.Snapshot(DateTimeOffset.UtcNow);
        var document = Assert.Single(first.Grains, row => row.Kind == "Document");
        Assert.Equal(("repo", "docs/a.md", true, 1), (document.Repository, document.Path, document.Loaded, document.IndexRuns));
        Assert.True(document.Sections > 0);
        Assert.Equal(git.Head(), document.Commit);

        await grains.GetGrain<IDocumentGrain>(DocKeys.Document("repo", "docs/a.md")).DropAsync();
        await WaitUntilAsync(() => Task.FromResult(activity.Snapshot(DateTimeOffset.UtcNow).ActiveDocuments == 0));
        var commands = watcher.Commands;
        for (var i = 0; i < 5; i++) Assert.Equal(0, activity.Snapshot(DateTimeOffset.UtcNow).ActiveDocuments);
        Assert.Equal(commands, watcher.Commands);

        // An explicit call activates a new empty cache; a search leaves it empty, and a section read then loads it once.
        var doc = grains.GetGrain<IDocumentGrain>(DocKeys.Document("repo", "docs/a.md"));
        await doc.StatsAsync();
        Assert.False(Assert.Single(activity.Snapshot(DateTimeOffset.UtcNow).Grains, row => row.Kind == "Document").Loaded);
        await grains.GetGrain<IDocDirectoryGrain>("repo").SearchAsync("cached", null, null, 3, TimeSpan.FromSeconds(10));
        Assert.False(Assert.Single(activity.Snapshot(DateTimeOffset.UtcNow).Grains, row => row.Kind == "Document").Loaded);
        await grains.GetGrain<IDocDirectoryGrain>("repo").SectionAsync("docs/a.md", "alpha");
        document = Assert.Single(activity.Snapshot(DateTimeOffset.UtcNow).Grains, row => row.Kind == "Document");
        Assert.True(document.Loaded);
        Assert.Equal(1, document.Reads);
        Assert.Equal(1, document.IndexRuns);
    }

    /// <summary>Grain state whose next <see cref="FailWrites"/> writes throw, as a database fault would; reads return what was last written.</summary>
    private sealed class FlakyState<T>(Func<T, T> copy) : IPersistentState<T> where T : new()
    {
        public T Written { get; private set; } = new();
        public int FailWrites { get; set; }
        public int Writes { get; private set; }
        public T State { get; set; } = new();
        public string? Etag { get; private set; }
        public bool RecordExists { get; private set; }
        public Task ClearStateAsync() { State = new(); Written = new(); return Task.CompletedTask; }
        public Task ReadStateAsync() { State = copy(Written); return Task.CompletedTask; }
        public Task WriteStateAsync()
        {
            Writes++;
            if (FailWrites > 0) { FailWrites--; throw new InvalidOperationException("storage is down"); }
            Written = copy(State); RecordExists = true; Etag = Writes.ToString();
            return Task.CompletedTask;
        }
    }

    private static DocDirectoryState CopyDirectory(DocDirectoryState source) => new()
    {
        Branch = source.Branch, Commit = source.Commit, NeedsResync = source.NeedsResync, SyncedAt = source.SyncedAt, Fingerprint = source.Fingerprint,
        Docs = new(source.Docs, StringComparer.Ordinal), Skipped = new(source.Skipped, StringComparer.Ordinal), CapacitySkipped = new(source.CapacitySkipped, StringComparer.Ordinal),
        Usage = new(source.Usage ?? [], StringComparer.Ordinal)
    };

    [Fact]
    public async Task A_file_waiting_for_room_that_the_full_skipped_list_cannot_hold_marks_a_full_resync()
    {
        var directory = new DocDirectoryGrain(new FlakyState<DocDirectoryState>(CopyDirectory));
        var full = Enumerable.Range(0, RelayLimits.MaxDocsPerRepositoryCeiling).Select(i => $"docs/skip-{i:D5}.md").ToArray();
        await directory.ApplyAsync(new DocDirectoryUpdate("main", "c1", true, [], [], full, []));
        Assert.Equal(RelayLimits.MaxDocsPerRepositoryCeiling, (await directory.ReadAsync()).Skipped.Length);
        // A file skipped for another reason past the ceiling is only not listed: it is read again whenever it changes.
        await directory.ApplyAsync(new DocDirectoryUpdate("main", "c2", false, [], [], ["docs/huge.md"], []));
        Assert.False((await directory.ReadAsync()).NeedsResync);
        // One waiting for room would never be reconsidered, so the next sync is a full one, which lists every file again.
        await directory.ApplyAsync(new DocDirectoryUpdate("main", "c3", false, [], [], ["docs/late.md"], [], "", [new DocWaiting("docs/late.md", 10)]));
        var view = await directory.ReadAsync();
        Assert.True(view.NeedsResync);
        Assert.DoesNotContain(view.CapacitySkipped!, waiting => waiting.Path == "docs/late.md");
    }

    [Fact]
    public async Task A_failed_directory_write_keeps_the_old_commit_active_and_the_next_sync_persists()
    {
        var storage = new FlakyState<DocDirectoryState>(CopyDirectory);
        var directory = new DocDirectoryGrain(storage);
        var entry = new DocEntry("docs/a.md", "", "c1", "h1", 1, 10, 100);
        await directory.ApplyAsync(new DocDirectoryUpdate("main", "c1", true, [entry], [], [], []));

        // A sync to c2 whose write fails: readers still see c1 and its documents, and the failure reaches the sync.
        storage.FailWrites = 1;
        var update = new DocDirectoryUpdate("main", "c2", false, [entry with { Commit = "c2", Hash = "h2" }, new DocEntry("docs/b.md", "", "c2", "h3", 1, 5, 50)], [], ["docs/big.md"], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.ApplyAsync(update));
        var view = await directory.ReadAsync();
        Assert.Equal("c1", view.Commit);
        Assert.Equal([("docs/a.md", "c1")], view.Entries.Select(x => (x.Path, x.Commit)));
        Assert.Empty(view.Skipped);
        Assert.Equal("c1", storage.Written.Commit);

        // The next sync sees c1, not its own head, so it applies again, and this time the change is stored.
        await directory.ApplyAsync(update);
        Assert.Equal("c2", (await directory.ReadAsync()).Commit);
        Assert.Equal("c2", storage.Written.Commit);
        Assert.Equal(["docs/a.md", "docs/b.md"], storage.Written.Docs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["docs/big.md"], storage.Written.Skipped);

        // A resync mark whose write fails is not remembered, so the next mark writes it.
        storage.FailWrites = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.MarkResyncAsync());
        Assert.False((await directory.ReadAsync()).NeedsResync);
        var writes = storage.Writes;
        await directory.MarkResyncAsync();
        Assert.Equal(writes + 1, storage.Writes);
        Assert.True(storage.Written.NeedsResync);
        Assert.True((await directory.ReadAsync()).NeedsResync);
    }


    private static readonly ITokenCounter Tokens = new O200kTokenCounter();
    private const string AdminToken = "doc-cache-admin-test-secret-not-production";

    // ---- Analyzer and query escaping ----

    [Fact]
    public void The_analyzer_splits_api_words_and_keeps_verbs_and_status_codes_exact()
    {
        using var analyzer = new ApiDocAnalyzer();
        var terms = DocQuery.Terms(analyzer, "taskClaim task_claim /v1/tasks/{id}/claim GET returns 404; get the claims");
        foreach (var term in new[] { "task", "claim", "v1", "id", "GET", "404", "get", "return" }) Assert.Contains(term, terms);
        // Upper-case verbs stay exact; the English word "get" is a different term.
        Assert.DoesNotContain("tasks", terms);
        Assert.Equal(["GET"], DocQuery.Terms(analyzer, "GET").ToArray());
        Assert.Equal(["get"], DocQuery.Terms(analyzer, "get").ToArray());
        Assert.True(ApiDocAnalyzer.IsExactTerm("503"));
        Assert.False(ApiDocAnalyzer.IsExactTerm("600"));
    }

    [Theory]
    [InlineData("/v1/tasks/{id}")]
    [InlineData("a:b")]
    [InlineData("\"quoted\" claim")]
    [InlineData("{}")]
    [InlineData("claim AND")]
    [InlineData("OR")]
    [InlineData("NOT claim")]
    [InlineData("(unbalanced [range TO")]
    [InlineData("*?~^!&&||\\")]
    public void Query_text_with_syntax_characters_is_read_as_words_and_never_fails(string text)
    {
        using var analyzer = new ApiDocAnalyzer();
        var query = DocQuery.Parse(analyzer, text);
        if (query is not null) Assert.DoesNotContain(":[", query.ToString());
        Assert.Null(DocQuery.Parse(analyzer, "   "));
    }

    // ---- Splitting ----

    [Fact]
    public void Markdown_splits_at_headings_outside_code_blocks_with_unique_anchors_and_exact_text()
    {
        const string markdown = "Preamble line.\n\n# Tasks\n\nIntro.\n\n## Claim\n\n```bash\n# not a heading\ncurl -X POST /v1/tasks/1/claim\n```\n\n## Claim\n\nAgain.\n### Errors ###\n\nE1\n# Other\n";
        var sections = MarkdownSections.Split(markdown);
        Assert.Equal(new[] { "top", "tasks", "claim", "claim-1", "errors", "other" }, sections.Select(section => section.Anchor));
        Assert.Equal(new[] { "", "Tasks", "Tasks > Claim", "Tasks > Claim", "Tasks > Claim > Errors", "Other" }, sections.Select(section => section.HeadingPath));
        Assert.Equal(markdown, string.Concat(sections.Select(section => section.Text)));
        // The code block, including its "# not a heading" line, stays in its section.
        Assert.Contains("# not a heading\ncurl -X POST /v1/tasks/1/claim\n```", sections[2].Text);
        Assert.Equal("## Claim\n".Length, sections[2].HeadingLength);
    }

    [Fact]
    public void Long_sections_are_chunked_at_paragraphs_and_the_chunks_rebuild_the_section_exactly()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("The claim endpoint takes a task for one session.", 12)) + "\n\n";
        var code = "```\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}")) + "\n\n\nend\n```\n\n";
        var text = "## Long\n\n" + string.Concat(Enumerable.Repeat(paragraph, 20)) + code + paragraph + new string('x', 7_000) + "\n";
        var chunks = DocChunks.Chunk(text, Tokens);
        Assert.True(chunks.Count > 5);
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.All(chunks, chunk => Assert.True(chunk.Tokens <= RelayLimits.DocChunkTokens && chunk.Text.Length <= RelayLimits.DocChunkChars, $"{chunk.Tokens} tokens, {chunk.Text.Length} chars"));
        // The code block with blank lines inside is one piece, not split at its blank lines.
        Assert.Contains(chunks, chunk => chunk.Text.Contains("line 29\n\n\nend\n```"));
    }

    [Fact]
    public void Text_files_split_into_paragraph_chunks_named_after_the_file()
    {
        var text = string.Concat(Enumerable.Range(1, 40).Select(i => $"Paragraph {i} first line about refunds and orders\n" + string.Join(' ', Enumerable.Repeat("words", 60)) + "\n\n"));
        var sections = DocFormats.Build("notes/runbook.txt", text, Tokens, out var reason)!;
        Assert.Equal(DocSkipReason.None, reason);
        Assert.True(sections.Count > 3);
        Assert.Equal(Enumerable.Range(1, sections.Count).Select(i => "p" + i), sections.Select(section => section.Anchor));
        Assert.Equal("runbook.txt > Paragraph 1 first line about refunds and orders", sections[0].HeadingPath);
        Assert.Equal(text, string.Concat(sections.Select(section => section.Text)));
        Assert.All(sections, section => Assert.True(section.Tokens <= RelayLimits.DocChunkTokens + 2));
        var longLine = DocFormats.Build("a.txt", new string('y', 200) + "\n", Tokens, out _)!;
        Assert.Equal("a.txt > " + new string('y', RelayLimits.MaxTextHeadingChars) + "…", longLine[0].HeadingPath);
    }

    private const string OpenApiJson = """
    {
      "openapi": "3.0.1",
      "info": { "title": "Orders", "version": "1" },
      "paths": {
        "/v1/orders": {
          "post": {
            "tags": ["orders"], "operationId": "createOrder", "summary": "Create an order",
            "parameters": [ { "name": "X-Request-Id", "in": "header", "required": true, "schema": { "type": "string", "format": "uuid" } } ],
            "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NewOrder" } } } },
            "responses": { "201": { "description": "Created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Order" } } } },
                           "409": { "description": "Duplicate request" } }
          },
          "get": { "tags": ["orders"], "summary": "List orders", "responses": { "200": { "description": "Orders", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Order" } } } } } } }
        },
        "/v1/orders/{id}": {
          "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
          "get": { "summary": "Read one order", "responses": { "200": { "description": "The order" }, "404": { "description": "No such order" } } },
          "delete": { "tags": ["admin"], "responses": { "204": { "description": "Deleted" }, "default": { "$ref": "#/components/responses/Problem" } } }
        }
      },
      "components": {
        "schemas": {
          "Order": { "description": "A placed order.", "required": ["id"], "properties": { "id": { "type": "string" }, "lines": { "type": "array", "items": { "$ref": "#/components/schemas/Line" } }, "status": { "$ref": "#/components/schemas/Status" } } },
          "NewOrder": { "properties": { "lines": { "type": "array", "items": { "type": "object" } } } },
          "Line": { "allOf": [ { "$ref": "#/components/schemas/Base" } ] }
        }
      }
    }
    """;

    private const string OpenApiYaml = """
    openapi: 3.0.1
    info: { title: Orders, version: "1" }
    paths:
      /v1/orders:
        post:
          tags: [orders]
          operationId: createOrder
          summary: Create an order
          parameters:
            - name: X-Request-Id
              in: header
              required: true
              schema: { type: string, format: uuid }
          requestBody:
            content:
              application/json:
                schema: { $ref: '#/components/schemas/NewOrder' }
          responses:
            "201":
              description: Created
              content:
                application/json:
                  schema: { $ref: '#/components/schemas/Order' }
            "409": { description: Duplicate request }
        get:
          tags: [orders]
          summary: List orders
          responses:
            "200":
              description: Orders
              content:
                application/json:
                  schema:
                    type: array
                    items: { $ref: '#/components/schemas/Order' }
      /v1/orders/{id}:
        parameters:
          - &id { name: id, in: path, required: true, schema: { type: string } }
        get:
          summary: Read one order
          responses:
            "200": { description: The order }
            "404": { description: No such order }
        delete:
          tags: [admin]
          responses:
            "204": { description: Deleted }
            default: { $ref: '#/components/responses/Problem' }
    components:
      schemas:
        Order:
          description: A placed order.
          required: [id]
          properties:
            id: { type: string }
            lines: { type: array, items: { $ref: '#/components/schemas/Line' } }
            status: { $ref: '#/components/schemas/Status' }
        NewOrder:
          properties:
            lines: { type: array, items: { type: object } }
        Line:
          allOf:
            - $ref: '#/components/schemas/Base'
    """;

    [Fact]
    public void OpenApi_gives_one_section_per_operation_and_per_schema_with_ref_names_only()
    {
        var sections = DocFormats.Build("api/orders.json", OpenApiJson, Tokens, out var reason)!;
        Assert.Equal(DocSkipReason.None, reason);
        Assert.Equal(new[] { "orders > GET /v1/orders", "orders > POST /v1/orders", "untagged > GET /v1/orders/{id}", "admin > DELETE /v1/orders/{id}", "Schemas > Order", "Schemas > NewOrder", "Schemas > Line" },
            sections.Select(section => section.HeadingPath));
        Assert.Equal(new[] { "get--v1-orders", "createOrder", "get--v1-orders-id", "delete--v1-orders-id", "schema-order", "schema-neworder", "schema-line" }, sections.Select(section => section.Anchor));
        var post = sections[1].Text;
        Assert.StartsWith("POST /v1/orders\nSummary: Create an order\n", post);
        Assert.Contains("- X-Request-Id (header, required): string (uuid)", post);
        Assert.Contains("Request body: application/json NewOrder", post);
        Assert.Contains("- 201: Created (Order)", post);
        Assert.Contains("- 409: Duplicate request", post);
        Assert.Contains("- 200: Orders (array of Order)", sections[0].Text);
        // Path-level parameters apply to each operation; a referenced response is named, never expanded.
        Assert.Contains("- id (path, required): string", sections[2].Text);
        Assert.Contains("- default: Problem", sections[3].Text);
        Assert.Contains("A placed order.", sections[4].Text);
        Assert.Contains("- id: string (required)", sections[4].Text);
        Assert.Contains("- lines: array of Line", sections[4].Text);
        Assert.Contains("- status: Status", sections[4].Text);
        Assert.Contains("allOf Base", sections[6].Text);
    }

    [Fact]
    public void A_yaml_spec_gives_the_same_sections_as_the_same_json_spec()
    {
        var json = DocFormats.Build("orders.json", OpenApiJson, Tokens, out _)!;
        var yaml = DocFormats.Build("orders.yaml", OpenApiYaml, Tokens, out var reason)!;
        Assert.Equal(DocSkipReason.None, reason);
        Assert.Equal(json.Select(section => (section.Anchor, section.HeadingPath, section.Text)), yaml.Select(section => (section.Anchor, section.HeadingPath, section.Text)));
        Assert.Equal(json.Count, DocFormats.Build("orders.yml", OpenApiYaml, Tokens, out _)!.Count);
    }

    [Theory]
    [InlineData("package.json", """{ "name": "x", "version": "1.0.0" }""", DocSkipReason.NotOpenApi)]
    [InlineData("list.json", """[ { "openapi": "3.0.0" } ]""", DocSkipReason.NotOpenApi)]
    [InlineData("broken.json", """{ "openapi": "3.0.0", "paths": { """, DocSkipReason.Malformed)]
    [InlineData("config.yaml", "name: app\nreplicas: 3\n", DocSkipReason.NotOpenApi)]
    [InlineData("broken.yaml", "openapi: 3.0.0\npaths: [unclosed\n  - : :\n", DocSkipReason.Malformed)]
    [InlineData("repeat.yml", "openapi: 3.0.0\nopenapi: 3.1.0\n", DocSkipReason.Malformed)]
    [InlineData("picture.png", "not text", DocSkipReason.Unsupported)]
    public void Non_openapi_and_malformed_files_are_skipped_with_a_reason(string path, string content, DocSkipReason expected)
    {
        Assert.Null(DocFormats.Build(path, content, Tokens, out var reason));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void Deep_json_and_an_alias_bomb_are_refused_within_the_limits()
    {
        var deep = "{\"openapi\":\"3.0.0\",\"x\":" + string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200)) + "}";
        Assert.Null(DocFormats.Build("deep.json", deep, Tokens, out var reason));
        Assert.Equal(DocSkipReason.Malformed, reason);
        // Ten levels of nine aliases each would expand to 9^10 nodes.
        var bomb = new StringBuilder("openapi: 3.0.0\na0: &a0 [x, x, x, x, x, x, x, x, x]\n");
        for (var level = 1; level <= 10; level++) bomb.Append($"a{level}: &a{level} [{string.Join(", ", Enumerable.Repeat($"*a{level - 1}", 9))}]\n");
        var timer = Stopwatch.StartNew();
        Assert.Null(DocFormats.Build("bomb.yaml", bomb.ToString(), Tokens, out reason));
        Assert.Equal(DocSkipReason.Malformed, reason);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"The alias bomb took {timer.Elapsed}.");
        // An ordinary alias expands normally.
        Assert.NotNull(OpenApiSections.ParseYaml("a: &x {b: 1}\nc: *x\n")?["c"]?["b"]);
    }

    // ---- Search, ranking, fan-out and budgets, end to end against a temporary git repository ----

    [Fact]
    public async Task Search_finds_api_word_forms_ranks_headings_first_and_carries_the_indexed_commit()
    {
        using var git = GitRepositories.Create(new()
        {
            ["docs/camel.md"] = "# Fields\n\nThe taskClaim flag is set.\n",
            ["docs/snake.md"] = "# Columns\n\nThe task_claim column holds it.\n",
            ["docs/url.md"] = "# Routes\n\nCall /v1/tasks/{id}/claim to hold a task.\n",
            ["docs/rank.md"] = "# Guide\n\n## Other\n\nThis mentions claim in passing among many other words about something else.\n\n## Claim\n\nTake a task.\n"
        });
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var hits = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "claim", limit = 10 }));
        foreach (var path in new[] { "docs/camel.md", "docs/snake.md", "docs/url.md" }) Assert.Contains(hits, hit => hit.GetProperty("path").GetString() == path);
        var rank = hits.Where(hit => hit.GetProperty("path").GetString() == "docs/rank.md").Select(hit => hit.GetProperty("anchor").GetString()).ToArray();
        Assert.Equal(new[] { "claim", "other" }, rank);
        Assert.Equal("Guide > Claim", hits[0].GetProperty("heading").GetString());
        var head = git.Head();
        Assert.All(hits, hit => Assert.Equal(head[..12], hit.GetProperty("commit").GetString()));
        Assert.All(hits, hit => Assert.True(hit.GetProperty("tokens").GetInt32() > 0));
        Assert.Contains("[claim]", hits.Single(hit => hit.GetProperty("path").GetString() == "docs/url.md").GetProperty("snippet").GetString());
        // Search syntax is read as words.
        foreach (var query in new[] { "/v1/tasks/{id}: \"claim\"", "a:b", "{}", "claim AND", "\"" })
            Assert.Equal((HttpStatusCode.OK, "Ok"), Status(await Tool(client, "doc_search", new { repositoryId = "repo", query })));
        // spec and path narrow the search.
        Assert.Single(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "claim", path = "docs/snake.md" })));
        Assert.Empty(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "claim", spec = "nothing" })));
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), Status(await Tool(client, "doc_search", new { repositoryId = "repo", query = "claim", path = "docs/none.md" })));
    }

    [Fact]
    public async Task OpenApi_operations_are_found_by_verb_and_path_and_by_status_code_and_large_specs_are_indexed()
    {
        // Pad the spec past MaxDocBytes but under MaxOpenApiBytes.
        var padded = OpenApiJson.Replace("\"A placed order.\"", "\"A placed order. " + string.Concat(Enumerable.Repeat("Orders are placed by customers. ", 9_000)) + "\"");
        Assert.True(Encoding.UTF8.GetByteCount(padded) is > RelayLimits.MaxDocBytes and < RelayLimits.MaxOpenApiBytes);
        using var git = GitRepositories.Create(new() { ["api/orders.json"] = padded, ["api/orders-v2.yaml"] = OpenApiYaml.Replace("/v1/", "/v2/"), ["docs/intro.md"] = "# Orders\n\nPOST requests to the orders API return 404 when the order is gone.\n" });
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        var outcome = await SyncAsync(host);
        Assert.Equal(0, outcome.Skipped);
        var post = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "POST /v1/orders" }));
        Assert.Equal(("api/orders.json", "createOrder"), (post[0].GetProperty("path").GetString(), post[0].GetProperty("anchor").GetString()));
        var notFound = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "404", path = "api/orders.json" }));
        Assert.Equal("untagged > GET /v1/orders/{id}", notFound[0].GetProperty("heading").GetString());
        var v2 = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "DELETE /v2/orders/{id}" }));
        Assert.Equal(("api/orders-v2.yaml", "admin > DELETE /v2/orders/{id}"), (v2[0].GetProperty("path").GetString(), v2[0].GetProperty("heading").GetString()));
        var section = Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "api/orders.json", anchor = "createOrder" }));
        Assert.StartsWith("POST /v1/orders\n", section.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_search_over_more_documents_than_the_fan_out_asks_them_all_at_most_32_at_a_time()
    {
        var files = Enumerable.Range(0, RelayLimits.MaxDocFanOut + 8).ToDictionary(i => $"docs/d{i:00}.md", i => $"# Doc {i}\n\nRefunds are described here{(i == 39 ? " with refunds refunds refunds" : "")}.\n");
        using var git = GitRepositories.Create(files);
        var watcher = new WatchingGit { ShowDelay = TimeSpan.FromMilliseconds(150) };
        await using var host = DocHost(git, watcher);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var outcome = await grains.GetGrain<IDocDirectoryGrain>("repo").SearchAsync("refunds", null, null, 3, TimeSpan.FromSeconds(30));
        Assert.Equal(40, outcome.Documents);
        Assert.Equal(0, outcome.NotSearched);
        Assert.InRange(outcome.PeakConcurrency, 2, RelayLimits.MaxDocFanOut);
        Assert.InRange(watcher.PeakShows, 2, RelayLimits.MaxDocFanOut);
        // The best section is in a document past the first 32.
        Assert.Equal("docs/d39.md", outcome.Hits[0].Path);
        var reply = Data(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds" }));
        Assert.Equal(RelayLimits.DefaultDocSearchResults, reply.GetProperty("hits").GetArrayLength());
        Assert.False(reply.TryGetProperty("partial", out _));
    }

    [Fact]
    public async Task A_search_past_its_timeout_returns_the_hits_so_far_as_partial()
    {
        using var git = GitRepositories.Create(new() { ["docs/fast.md"] = "# Refunds\n\nFast refunds.\n", ["docs/slow.md"] = "# Refunds\n\nSlow refunds.\n" });
        var watcher = new WatchingGit { SlowPath = "docs/slow.md" };
        await using var host = DocHost(git, watcher);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        // slow.md's read waits until released, far past the search timeout; fast.md's real git show has a generous one.
        watcher.HoldSlow();
        host.Services.GetRequiredService<DocCacheTool>().SearchTimeout = TimeSpan.FromSeconds(5);
        var timer = Stopwatch.StartNew();
        var reply = Data(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds" }));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20));
        Assert.True(reply.GetProperty("partial").GetBoolean());
        Assert.Equal(1, reply.GetProperty("notSearched").GetInt32());
        Assert.Equal("docs/fast.md", Assert.Single(reply.GetProperty("hits").EnumerateArray()).GetProperty("path").GetString());
        watcher.ReleaseSlow();
    }

    [Fact]
    public async Task Replies_stay_within_the_token_budget_and_report_what_did_not_fit_as_a_count()
    {
        var heading = string.Join(' ', Enumerable.Repeat("refund", 60));
        var many = string.Concat(Enumerable.Range(0, 12).Select(i => $"# {heading} {i}\n\n## {heading} part\n\n### {heading} detail\n\nRefund body {i}.\n\n"));
        var paragraph = string.Join(' ', Enumerable.Repeat("Each refund needs an order and a reason.", 15)) + "\n\n";
        var longSection = "# Policy\n\n" + string.Concat(Enumerable.Repeat(paragraph, 60)) + "```\ncode stays\n```\n";
        using var git = GitRepositories.Create(new() { ["docs/many.md"] = many, ["docs/long.md"] = longSection });
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var search = Data(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refund", path = "docs/many.md", limit = 10 }));
        Assert.True(Tokens.Count(search.GetRawText()) <= RelayLimits.MaxDocReplyTokens);
        Assert.True(search.GetProperty("more").GetInt32() > 0);
        Assert.Equal(10, search.GetProperty("hits").GetArrayLength() + search.GetProperty("more").GetInt32());
        // A section over the budget comes in whole chunks; following next rebuilds it exactly, code block included.
        var text = new StringBuilder();
        var anchor = "policy";
        var total = 0;
        for (var page = 0; anchor is not null; page++)
        {
            var section = Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/long.md", anchor }));
            Assert.True(Tokens.Count(section.GetRawText()) <= RelayLimits.MaxDocReplyTokens);
            Assert.Equal("Policy", section.GetProperty("heading").GetString());
            total = section.GetProperty("tokens").GetInt32();
            text.Append(section.GetProperty("text").GetString());
            anchor = section.TryGetProperty("next", out var next) ? next.GetString() : null;
            if (anchor is not null) Assert.True(section.GetProperty("moreTokens").GetInt32() > 0);
            Assert.True(page < 50);
        }
        Assert.Equal(longSection, text.ToString());
        Assert.True(total > RelayLimits.MaxDocReplyTokens);
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), Status(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/long.md", anchor = "policy~999" })));
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), Status(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/long.md", anchor = "nope" })));
    }

    [Fact]
    public async Task Doc_section_returns_exactly_the_section_text_with_code_blocks()
    {
        const string markdown = "# Tasks\n\nIntro.\n\n## Claim\n\nUse it:\n\n```json\n{ \"taskClaim\": true }\n\n# still code\n```\n\nAfter.\n\n## Errors\n\nE.\n";
        using var git = GitRepositories.Create(new() { ["docs/tasks.md"] = markdown });
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var section = Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/tasks.md", anchor = "claim" }));
        Assert.Equal("## Claim\n\nUse it:\n\n```json\n{ \"taskClaim\": true }\n\n# still code\n```\n\nAfter.\n\n", section.GetProperty("text").GetString());
        Assert.Equal("Tasks > Claim", section.GetProperty("heading").GetString());
        Assert.Equal(git.Head()[..12], section.GetProperty("commit").GetString());
        Assert.False(section.TryGetProperty("next", out _));
    }

    [Fact]
    public async Task After_idle_deactivation_the_next_search_and_section_reload_the_file_at_the_recorded_commit()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Refunds\n\nRefunds take five days.\n" });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var recorded = git.Head();
        Assert.Single(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds" })));
        Assert.Contains("five days", Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/a.md", anchor = "refunds" })).GetProperty("text").GetString());
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var management = grains.GetGrain<IManagementGrain>(0);
        var document = grains.GetGrain<IDocumentGrain>(DocKeys.Document("repo", "docs/a.md"));
        Assert.Equal(1, (await document.StatsAsync()).IndexRuns);
        // A newer commit in the clone's working tree and branch must not leak into the reload: both read the recorded commit.
        git.WriteInClone("docs/a.md", "# Refunds\n\nUncommitted refunds text.\n");
        await management.ForceActivationCollection(TimeSpan.Zero);
        await WaitUntilAsync(async () => !(await management.GetActiveGrains(DocKeys.DocumentType)).Any(id => id.Key.ToString() == DocKeys.Document("repo", "docs/a.md"))
            && !(await management.GetActiveGrains(DocKeys.SearchType)).Any(id => id.Key.ToString() == "repo"));
        var shows = watcher.Shows.Count;
        var hits = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds" }));
        Assert.Contains("five days", Assert.Single(hits).GetProperty("snippet").GetString());
        Assert.Contains(watcher.Shows.Skip(shows), argument => argument == recorded + ":docs/a.md");
        // The search reloaded the shared index only; the section read reloads the document.
        Assert.DoesNotContain(await management.GetActiveGrains(DocKeys.DocumentType), id => id.Key.ToString() == DocKeys.Document("repo", "docs/a.md"));
        Assert.Contains("five days", Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/a.md", anchor = "refunds" })).GetProperty("text").GetString());
        var stats = await document.StatsAsync();
        Assert.Equal((true, recorded, 1, 1), (stats.Loaded, stats.Commit, stats.IndexRuns, stats.Reads));
    }

    // ---- Sync ----

    [Fact]
    public async Task Sync_indexes_only_what_changed_and_drops_deleted_and_renamed_files()
    {
        using var git = GitRepositories.Create(new()
        {
            ["docs/a.md"] = "# Alpha\n\nAlpha refunds.\n\n## Gone soon\n\nObsolete refunds.\n",
            ["docs/b.md"] = "# Beta\n\nBeta refunds.\n",
            ["docs/c.md"] = "# Gamma\n\nGamma refunds.\n",
            ["docs/old.md"] = "# Old name\n\nRenamed refunds.\n"
        });
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        var service = host.Services.GetServices<IHostedService>().OfType<SpecSyncService>().Single();
        await service.RunOnceAsync(CancellationToken.None);
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        Assert.Equal(4, Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds", limit = 10 })).Count(hit => hit.GetProperty("anchor").GetString() != "gone-soon"));
        // The shared search index read each file once; a section read loads its one document.
        async Task<int> Reads() => (await grains.GetGrain<IDocSearchGrain>("repo").StatsAsync()).Reads;
        async Task<int> Runs(string path) => (await grains.GetGrain<IDocumentGrain>(DocKeys.Document("repo", path)).StatsAsync()).IndexRuns;
        Assert.Equal(4, await Reads());
        Assert.Contains("Alpha refunds", Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/a.md", anchor = "alpha" })).GetProperty("text").GetString());
        Assert.Equal(1, await Runs("docs/a.md"));

        // A timer run with no new commits reads and indexes nothing.
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        Assert.Equal(0, (await runner.SyncAsync("repo", null, CancellationToken.None)).Changed);
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal((4, 1), (await Reads(), await Runs("docs/a.md")));

        // One changed file is re-read alone (and the loaded document re-indexed from the sync's content), and loses the section it no
        // longer has; an unchanged rewrite is not re-read.
        git.Commit(new() { ["docs/a.md"] = "# Alpha\n\nAlpha refunds, revised.\n", ["docs/b.md"] = "# Beta\n\nBeta refunds.\n" });
        var changed = await runner.SyncAsync("repo", null, CancellationToken.None);
        Assert.Equal((git.Head(), 1), (changed.Commit, changed.Changed));
        Assert.Equal((5, 2), (await Reads(), await Runs("docs/a.md")));
        var alpha = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds", path = "docs/a.md" }));
        Assert.Equal(new[] { "alpha" }, alpha.Select(hit => hit.GetProperty("anchor").GetString()));
        Assert.Equal(git.Head()[..12], alpha[0].GetProperty("commit").GetString());
        // b keeps the commit it was indexed from.
        var view = await grains.GetGrain<IDocDirectoryGrain>("repo").ReadAsync();
        Assert.NotEqual(git.Head(), view.Entries.Single(entry => entry.Path == "docs/b.md").Commit);

        // A deleted and a renamed file: their old sections stop appearing.
        git.Delete("docs/c.md");
        git.Move("docs/old.md", "docs/new.md");
        var moved = await runner.SyncAsync("repo", null, CancellationToken.None);
        Assert.Equal(3, moved.Changed);
        var paths = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds", limit = 10 })).Select(hit => hit.GetProperty("path").GetString()).ToArray();
        Assert.DoesNotContain("docs/c.md", paths);
        Assert.DoesNotContain("docs/old.md", paths);
        Assert.Contains("docs/new.md", paths);
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), Status(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/old.md", anchor = "old-name" })));
        await WaitUntilAsync(async () => !(await grains.GetGrain<IManagementGrain>(0).GetActiveGrains(DocKeys.DocumentType)).Any(id => id.Key.ToString() == DocKeys.Document("repo", "docs/c.md")));
    }

    [Fact]
    public async Task Uncommitted_edits_and_unpushed_commits_in_the_clone_never_reach_the_index()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nCommitted text.\n" });
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        git.WriteInClone("docs/a.md", "# Alpha\n\nSecretdraft text.\n");
        git.WriteInClone("docs/untracked.md", "# Untracked\n\nSecretdraft too.\n");
        git.CommitInCloneOnly("docs/local.md", "# Local\n\nSecretdraft committed locally.\n");
        git.Commit(new() { ["docs/b.md"] = "# Beta\n\nPushed text.\n" });
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        Assert.Equal(1, (await runner.SyncAsync("repo", null, CancellationToken.None)).Changed);
        Assert.Empty(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "secretdraft", limit = 10 })));
        Assert.Single(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "pushed" })));
        Assert.Contains("Committed", Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/a.md", anchor = "alpha" })).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Only_configured_repositories_patterns_and_supported_files_are_synced_and_skips_are_counted()
    {
        using var git = GitRepositories.Create(new()
        {
            ["docs/in.md"] = "# In\n\nIncluded refunds.\n",
            ["docs/node_modules/pkg/readme.md"] = "# Dependency\n\nExcluded refunds.\n",
            ["docs/private/secret.md"] = "# Private\n\nExcluded refunds.\n",
            ["docs/notes.txt"] = "Text refunds.\n",
            ["docs/package.json"] = """{ "name": "refunds" }""",
            ["docs/broken.json"] = """{ "openapi": "3.0.0", """,
            ["docs/broken.yaml"] = "openapi: [\n",
            ["docs/dupe.json"] = """{ "openapi": "3.0.0", "paths": { "/a": { "get": {}, "get": {} } } }""",
            ["docs/huge.md"] = "# Huge\n\n" + new string('r', RelayLimits.MaxDocBytes),
            ["src/code.md"] = "# Code\n\nOutside the pattern refunds.\n",
            ["docs/image.png"] = "png"
        });
        await using var host = DocHost(git, settings: new()
        {
            ["Relay:DocCache:Repositories:0:Patterns:0:Glob"] = "docs/**",
            ["Relay:DocCache:Repositories:0:Patterns:0:Spec"] = "guide",
            ["Relay:DocCache:Repositories:0:Exclude:0"] = "docs/private/**"
        });
        using var client = host.AuthorizedClient();
        var outcome = await SyncAsync(host);
        Assert.Equal(5, outcome.Skipped);
        var view = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").ReadAsync();
        Assert.Equal(new[] { "docs/in.md", "docs/notes.txt" }, view.Entries.Select(entry => entry.Path));
        Assert.All(view.Entries, entry => Assert.Equal("guide", entry.Spec));
        Assert.Equal(new[] { "docs/broken.json", "docs/broken.yaml", "docs/dupe.json", "docs/huge.md", "docs/package.json" }, view.Skipped);
        var hits = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds", limit = 10, spec = "guide" }));
        Assert.Equal(new[] { "docs/in.md", "docs/notes.txt" }, hits.Select(hit => hit.GetProperty("path").GetString()).Order());
        // A path the patterns do not cover is never read, even when asked for directly.
        Assert.Null(await host.Services.GetRequiredService<IDocSource>().ReadAsync("repo", "src/code.md", git.Head()));
        Assert.Null(await host.Services.GetRequiredService<IDocSource>().ReadAsync("repo", "docs/private/secret.md", git.Head()));
        Assert.Null(await host.Services.GetRequiredService<IDocSource>().ReadAsync("other", "docs/in.md", git.Head()));
        // A repository without settings has no docs.
        using var admin = Admin(host);
        Assert.Equal((HttpStatusCode.BadRequest, "DocCacheUnavailable"), Status(await Tool(admin, "doc_search", new { repositoryId = "repo2", query = "refunds" })));
        Assert.Equal((HttpStatusCode.BadRequest, "DocCacheUnavailable"), Status(await Tool(admin, "doc_sync", new { repositoryId = "repo2", requestId = "s-0" })));
    }

    [Fact]
    public async Task The_document_limit_is_configurable_and_files_past_it_are_skipped_and_counted()
    {
        using var git = GitRepositories.Create(Enumerable.Range(0, 5).ToDictionary(i => $"docs/{i}.md", i => $"# D{i}\n\nText.\n"));
        await using var host = DocHost(git, settings: new() { ["Relay:DocCache:MaxDocuments"] = "3" });
        var outcome = await SyncAsync(host);
        Assert.Equal(2, outcome.Skipped);
        var view = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").ReadAsync();
        Assert.Equal(3, view.Entries.Length);
        Assert.Equal(new[] { "docs/3.md", "docs/4.md" }, view.Skipped);
        foreach (var bad in new[] { "0", (RelayLimits.MaxDocsPerRepositoryCeiling + 1).ToString() })
            Assert.Contains("MaxDocuments", Assert.Throws<InvalidOperationException>(() => DocCacheSettings.From(Configuration(new() { ["Relay:DocCache:MaxDocuments"] = bad }), new FakeEnvironment())).Message);
    }

    [Fact]
    public async Task A_timer_run_and_doc_sync_never_run_at_once_for_one_repository_and_doc_sync_is_rate_limited()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher);
        using var admin = Admin(host);
        var service = host.Services.GetServices<IHostedService>().OfType<SpecSyncService>().Single();
        watcher.HoldFetch();
        var timerRun = service.RunOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(watcher.FetchesStarted == 1));
        var manual = Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "s-1" });
        await Task.Delay(500);
        Assert.Equal(1, watcher.FetchesStarted);
        Assert.False(manual.IsCompleted);
        watcher.ReleaseFetch();
        await timerRun;
        var reply = await manual;
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(reply));
        Assert.Equal(1, watcher.PeakFetches);
        var data = Data(reply);
        Assert.Equal((git.Head(), 0, true), (data.GetProperty("commit").GetString(), data.GetProperty("changed").GetInt32(), data.GetProperty("fetched").GetBoolean()));
        // A retry with the same requestId gets the same outcome; a new doc_sync right away is refused. doc_sync needs a requestId.
        Assert.Equal(data.GetRawText(), Data(await Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "s-1" })).GetRawText());
        var limited = await Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "s-2" });
        Assert.Equal((HttpStatusCode.BadRequest, "RateLimited"), Status(limited));
        Assert.InRange(Data(limited).GetProperty("retryAfterSeconds").GetInt32(), 1, RelayLimits.DocSyncMinIntervalSeconds);
        Assert.Equal((HttpStatusCode.BadRequest, "RequestIdRequired"), Status(await Tool(admin, "doc_sync", new { repositoryId = "repo" })));
        // The same requestId with another branch is a conflict, not a second sync.
        Assert.Equal((HttpStatusCode.Conflict, "RequestConflict"), Status(await Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "s-1", branch = "main" })));
    }

    [Fact]
    public async Task Doc_sync_follows_a_given_branch_and_refuses_bad_ones()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nMain text.\n" });
        git.CommitOnBranch("feature", "docs/a.md", "# Alpha\n\nFeature text.\n");
        await using var host = DocHost(git);
        using var admin = Admin(host);
        var synced = Data(await Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "b-1", branch = "feature" }));
        Assert.Equal(git.Head("feature"), synced.GetProperty("commit").GetString());
        Assert.Single(Hits(await Tool(admin, "doc_search", new { repositoryId = "repo", query = "feature" })));
        Assert.Equal("feature", (await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").ReadAsync()).Branch);
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        foreach (var bad in new[] { "-delete", "a..b", "nope" })
            Assert.Equal(DocSyncError.UnknownBranch, (await Assert.ThrowsAsync<DocSyncException>(() => runner.SyncAsync("repo", bad, CancellationToken.None))).Error);
    }

    // ---- Access, limits and the off switch ----

    [Fact]
    public async Task Every_tool_checks_the_repository_scope_and_doc_sync_needs_an_administrator()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
        await using var host = DocHost(git);
        using var worker = host.AuthorizedClient();
        await SyncAsync(host);
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_search", new { repositoryId = "other", query = "text" })));
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_section", new { repositoryId = "other", path = "docs/a.md", anchor = "alpha" })));
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_sync", new { repositoryId = "other", requestId = "w-1" })));
        Assert.Equal((HttpStatusCode.Forbidden, "AdministratorRequired"), Status(await Tool(worker, "doc_sync", new { repositoryId = "repo", requestId = "w-2" })));
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(await Tool(worker, "doc_section", new { repositoryId = "repo", path = "docs/a.md", anchor = "alpha" })));
    }

    [Fact]
    public async Task Inputs_over_a_limit_get_the_standard_errors()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
        await using var host = DocHost(git);
        using var admin = Admin(host);
        async Task Refused(string tool, object arguments, string field)
        {
            var reply = await Tool(admin, tool, arguments);
            Assert.Equal((HttpStatusCode.BadRequest, "InvalidArguments"), Status(reply));
            Assert.Equal(field, Data(reply).GetProperty("field").GetString());
        }
        await Refused("doc_search", new { repositoryId = "repo", query = new string('q', RelayLimits.MaxDocQueryLength + 1) }, "query");
        await Refused("doc_search", new { repositoryId = "repo", query = " " }, "query");
        await Refused("doc_search", new { repositoryId = "repo" }, "query");
        await Refused("doc_search", new { repositoryId = "repo", query = "x", spec = new string('s', RelayLimits.MaxDocSpecLength + 1) }, "spec");
        await Refused("doc_search", new { repositoryId = "repo", query = "x", path = new string('p', RelayLimits.MaxDocPathLength + 1) }, "path");
        await Refused("doc_search", new { repositoryId = "repo", query = "x", path = "../etc/passwd" }, "path");
        await Refused("doc_search", new { repositoryId = "repo", query = "x", limit = "five" }, "limit");
        await Refused("doc_section", new { repositoryId = "repo", path = "docs/a.md", anchor = new string('a', RelayLimits.MaxDocAnchorLength + 1) }, "anchor");
        await Refused("doc_section", new { repositoryId = "repo", path = "docs/a.md" }, "anchor");
        await Refused("doc_sync", new { repositoryId = "repo", requestId = "l-1", branch = new string('b', RelayLimits.MaxDocBranchLength + 1) }, "branch");
        await Refused("doc_sync", new { repositoryId = "repo", requestId = "l-2", branch = "--upload-pack=evil" }, "branch");
        using var huge = await admin.PostAsync("/api/tools/doc_search", new StringContent("{\"repositoryId\":\"repo\",\"query\":\"" + new string('x', RelayLimits.MaxPayloadBytes) + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    [Fact]
    public async Task With_the_doc_cache_off_the_tools_answer_DocCacheUnavailable_and_nothing_syncs()
    {
        var git = new WatchingGit();
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo",
            ["Relay:DocCache:Enabled"] = "false",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = "/nonexistent", ["Relay:DocCache:SyncMinutes"] = "1"
        }, configureServices: services => { services.RemoveAll<GitCli>(); services.AddSingleton<GitCli>(git); });
        using var worker = host.AuthorizedClient();
        using var admin = Admin(host);
        Assert.Equal((HttpStatusCode.BadRequest, "DocCacheUnavailable"), Status(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "x" })));
        Assert.Equal((HttpStatusCode.BadRequest, "DocCacheUnavailable"), Status(await Tool(worker, "doc_section", new { repositoryId = "repo", path = "a.md", anchor = "a" })));
        Assert.Equal((HttpStatusCode.Forbidden, "AdministratorRequired"), Status(await Tool(worker, "doc_sync", new { repositoryId = "repo", requestId = "w-2" })));
        Assert.Equal((HttpStatusCode.BadRequest, "DocCacheUnavailable"), Status(await Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "a-1" })));
        // Nothing of the cache runs: the sync service returns at once, git never runs, and the tool and source resolve to null.
        await host.Services.GetServices<IHostedService>().OfType<SpecSyncService>().Single().ExecuteTask!;
        Assert.Equal(0, git.Commands);
        Assert.Null(host.Services.GetService<DocCacheTool>());
        Assert.Null(host.Services.GetService<IDocSource>());
        // The shared token counter is registered either way, for other features to reuse.
        Assert.Equal("o200k_base", host.Services.GetRequiredService<ITokenCounter>().Encoding);
        Assert.Equal(RelayErrorKind.Invalid, RelayErrorCodes.Classify(RelayErrorCodes.DocCacheUnavailable));
    }

    [Fact]
    public async Task With_no_repositories_configured_the_doc_cache_runs_no_git_and_says_so()
    {
        var git = new WatchingGit();
        // The doc cache is on by default; this hub configures no repositories for it.
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo"
        }, configureServices: services => { services.RemoveAll<GitCli>(); services.AddSingleton<GitCli>(git); });
        using var worker = host.AuthorizedClient();
        using var admin = Admin(host);
        var settings = host.Services.GetRequiredService<DocCacheSettings>();
        Assert.True(settings.Enabled);
        Assert.Empty(settings.Repositories);
        Assert.NotNull(host.Services.GetService<DocCacheTool>());
        var search = await Tool(worker, "doc_search", new { repositoryId = "repo", query = "x" });
        Assert.Equal((HttpStatusCode.BadRequest, "NoRepositoriesConfigured"), Status(search));
        Assert.Contains("Relay:DocCache:Repositories", Data(search).GetProperty("message").GetString());
        Assert.Equal((HttpStatusCode.BadRequest, "NoRepositoriesConfigured"), Status(await Tool(worker, "doc_section", new { repositoryId = "repo", path = "a.md", anchor = "a" })));
        Assert.Equal((HttpStatusCode.BadRequest, "NoRepositoriesConfigured"), Status(await Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "none-1" })));
        // The timer has nothing to sync, so it returns at once and git never runs.
        await host.Services.GetServices<IHostedService>().OfType<SpecSyncService>().Single().ExecuteTask!;
        await Task.Delay(200);
        Assert.Equal(0, git.Commands);
        Assert.Equal(RelayErrorKind.Invalid, RelayErrorCodes.Classify(RelayErrorCodes.NoRepositoriesConfigured));
    }

    [Fact]
    public async Task A_clone_without_a_remote_syncs_its_local_branch_head_without_fetching_or_touching_the_working_tree()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Claims\n\nHow to claim a task." });
        git.RemoveCloneRemote();
        var watching = new WatchingGit();
        await using var host = DocHost(git, watching);
        using var worker = host.AuthorizedClient();
        var first = await SyncAsync(host);
        Assert.False(first.Fetched);
        Assert.Equal(git.CloneHead(), first.Commit);
        Assert.Equal(1, first.Changed);
        Assert.Single(Hits(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "claim a task" })));

        // A commit on the local branch is picked up on the next sync; an uncommitted file is not.
        git.CommitInCloneOnly("docs/refunds.md", "# Refunds\n\nRefunds go back to the card.");
        git.WriteInClone("docs/draft.md", "# Draft\n\nzebra crossing notes");
        var second = await SyncAsync(host);
        Assert.Equal((git.CloneHead(), 1, false), (second.Commit, second.Changed, second.Fetched));
        Assert.Equal("docs/refunds.md", Assert.Single(Hits(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "refunds card" }))).GetProperty("path").GetString());
        Assert.Empty(Hits(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "zebra" })));
        Assert.Equal(0, (await SyncAsync(host)).Changed);
        // Nothing was fetched, and the working tree is as it was: only the untracked draft.
        Assert.Empty(watching.Fetches);
        Assert.Equal("?? docs/draft.md", git.CloneStatus().Trim());
    }

    [Fact]
    public async Task An_unpushed_branch_with_origin_present_is_read_locally_and_syncs_quietly()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Claims\n\nHow to claim a task." });
        git.CommitInCloneOnNewBranch("feature", "docs/feature.md", "# Feature\n\nThe unpushed refunds note.");
        await using var host = DocHost(git, settings: new() { ["Relay:DocCache:Repositories:0:Branch"] = "feature" });
        using var worker = host.AuthorizedClient();
        var logger = new ListLogger<DocSyncRunner>();
        var runner = new DocSyncRunner(host.Services.GetRequiredService<DocCacheSettings>(), new GitCli(), host.Services.GetRequiredService<IGrainFactory>(), Tokens, logger);
        // origin has no "feature": the fetch finds nothing, so the local branch is read, with one line and no warning.
        var first = await runner.SyncAsync("repo", null, CancellationToken.None);
        Assert.Equal(git.CloneHead(), first.Commit);
        Assert.Equal("docs/feature.md", Assert.Single(Hits(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "unpushed refunds" }))).GetProperty("path").GetString());
        git.CommitInCloneOnly("docs/more.md", "# More\n\nA second local commit about invoices.");
        var second = await runner.SyncAsync("repo", null, CancellationToken.None);
        Assert.Equal((git.CloneHead(), 1), (second.Commit, second.Changed));
        Assert.Equal(0, (await runner.SyncAsync("repo", null, CancellationToken.None)).Changed);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Single(logger.Entries, entry => entry.Message.Contains("has no branch feature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_fetch_off_the_hub_never_fetches_and_reads_the_local_branch_head()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Claims\n\nHow to claim a task." });
        var watching = new WatchingGit();
        await using var host = DocHost(git, watching, new() { ["Relay:DocCache:Repositories:0:Fetch"] = "false" });
        using var worker = host.AuthorizedClient();
        Assert.False(host.Services.GetRequiredService<DocCacheSettings>().Find("repo")!.Fetch);
        Assert.False((await SyncAsync(host)).Fetched);
        // A commit pushed to origin is not fetched, and the clone's remote-tracking ref stays where it was; a local commit is read.
        var tracking = git.CloneRef("refs/remotes/origin/main");
        git.Commit(new() { ["docs/pushed.md"] = "# Pushed\n\nzebra pushed elsewhere." });
        git.CommitInCloneOnly("docs/local.md", "# Local\n\nrefunds from a local commit.");
        var synced = await SyncAsync(host);
        Assert.Equal((git.CloneHead(), false), (synced.Commit, synced.Fetched));
        Assert.Single(Hits(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "refunds local commit" })));
        Assert.Empty(Hits(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "zebra" })));
        Assert.Equal(tracking, git.CloneRef("refs/remotes/origin/main"));
        Assert.Empty(watching.Fetches);
        Assert.DoesNotContain(watching.Arguments, arguments => arguments[0] is "fetch" or "remote");
    }

    [Fact]
    public void Bad_settings_fail_clearly_and_defaults_cover_every_supported_type()
    {
        void Fails(string key, string value, string expected) =>
            Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => DocCacheSettings.From(Configuration(new() { [key] = value }), new FakeEnvironment())).Message);
        Fails("Relay:DocCache:SyncMinutes", "-1", "SyncMinutes");
        Fails("Relay:DocCache:IdleMinutes", "0", "IdleMinutes");
        Fails("Relay:DocCache:Repositories:0:Patterns:0:Glob", "../**/*.md", "Patterns");
        Fails("Relay:DocCache:Repositories:0:Patterns:0:Glob", ":(top)*.md", "Patterns");
        Fails("Relay:DocCache:Repositories:0:Branch", "-x", "Branch");
        Fails("Relay:DocCache:Repositories:0:Exclude:0", "/abs/**", "Exclude");
        var settings = DocCacheSettings.From(Configuration([]), new FakeEnvironment());
        var repository = settings.Repositories.Single();
        Assert.Equal(DocFormats.DefaultPatterns, repository.Patterns.Select(pattern => pattern.Glob));
        Assert.Equal(DocFormats.DefaultExcludes, repository.Excludes);
        Assert.Equal((RelayLimits.DefaultDocSyncMinutes, RelayLimits.DefaultDocIdleMinutes, RelayLimits.MaxDocsPerRepository, true), (settings.SyncMinutes, settings.IdleMinutes, settings.MaxDocuments, settings.Enabled));
        // Off reads nothing more, so bad settings for a cache that is off never stop the hub.
        Assert.Empty(DocCacheSettings.From(Configuration(new() { ["Relay:DocCache:Enabled"] = "false", ["Relay:DocCache:SyncMinutes"] = "-1" }), new FakeEnvironment()).Repositories);
        foreach (var path in new[] { "README.md", "a/b/c.txt", "api/v1.json", "api/v1.yaml", "api/v1.yml" }) Assert.Equal("docs", repository.SpecOf(path));
        foreach (var path in new[] { "node_modules/x.md", "a/bin/x.md", "a/obj/x.json", ".git/x.txt", "x.cs", "../x.md", "/x.md" }) Assert.Null(repository.SpecOf(path));
    }

    // ---- The tools over HTTP and MCP, and the catalog ----

    [Fact]
    public async Task The_tools_work_over_mcp_and_are_listed_by_role()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Refunds\n\nRefunds take five days.\n" });
        await using var host = DocHost(git);
        await SyncAsync(host);
        using var http = host.AuthorizedClient();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false }, http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = (await mcp.ListToolsAsync(cancellationToken: timeout.Token)).Select(tool => tool.Name).ToArray();
        Assert.Contains("doc_search", tools);
        Assert.Contains("doc_section", tools);
        Assert.DoesNotContain("doc_sync", tools);
        var search = await mcp.CallToolAsync("doc_search", new Dictionary<string, object?> { ["repositoryId"] = "repo", ["query"] = "refunds" }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, search.IsError);
        using var reply = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(search.Content)).Text);
        var data = JsonDocument.Parse(reply.RootElement.GetProperty("dataJson").GetString()!).RootElement;
        Assert.Equal("refunds", data.GetProperty("hits")[0].GetProperty("anchor").GetString());
        var sync = await mcp.CallToolAsync("doc_sync", new Dictionary<string, object?> { ["repositoryId"] = "repo", ["requestId"] = "m-1" }, cancellationToken: timeout.Token);
        Assert.True(sync.IsError);
    }

    [Fact]
    public void Doc_tools_appear_in_hub_and_adapter_catalogs_with_matching_compact_schemas_within_budget()
    {
        using var client = new HttpClient();
        var forwarder = new CoCern.Adapter.RelayForwarder(client, new CoCern.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var adapter = CoCern.Adapter.CoordinationTools.Create(forwarder, admin: true).ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var hub = ToolResults.Tools(true).ToDictionary(tool => tool.Name, tool => tool.InputSchema.GetRawText());
        foreach (var (name, administrator, required, forwarded) in new[]
        {
            (RelayTools.DocSearch, false, new[] { "repositoryId", "query" }, new[] { "path", "query", "repositoryId", "spec" }),
            (RelayTools.DocSection, false, ["repositoryId", "path", "anchor"], ["anchor", "path", "repositoryId"]),
            (RelayTools.DocSync, true, ["repositoryId", "requestId"], ["branch", "repositoryId", "requestId"])
        })
        {
            Assert.True(RelayToolCatalog.TryGet(name, out var tool));
            Assert.Equal(RelayToolCatalog.ForwardingSchema(tool).GetRawText(), adapter[name]);
            Assert.Equal(RelayToolCatalog.InputSchema(tool).GetRawText(), hub[name]);
            // By the client token, with no session handle; doc_sync is a mutation because it can change the branch the hub follows.
            Assert.Equal((administrator, false, administrator), (tool.IsMutation, tool.RequiresSession, tool.AdministratorOnly));
            Assert.DoesNotContain('.', tool.Description.TrimEnd('.'));
            if (!administrator) Assert.Contains("untrusted", tool.Description);
            Assert.Equal(required, RelayToolCatalog.InputSchema(tool).GetProperty("required").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(forwarded, RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
            Assert.Equal(!administrator, CoCern.Adapter.CoordinationTools.NamesForRole(false).Contains(name));
            Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(name, admin: true, chatOnly: true));
        }
        var properties = RelayToolCatalog.InputSchema(RelayToolCatalog.All.Single(tool => tool.Name == RelayTools.DocSearch)).GetProperty("properties");
        Assert.Equal(RelayLimits.MaxDocQueryLength, properties.GetProperty("query").GetProperty("maxLength").GetInt32());
        Assert.Equal(RelayLimits.MaxDocSearchResults, properties.GetProperty("limit").GetProperty("maximum").GetInt32());
        static int Size(bool administrator) => RelayToolCatalog.ForRole(administrator).Sum(tool => tool.Description.Length + RelayToolCatalog.ForwardingSchema(tool).GetRawText().Length);
        Assert.True(Size(false) < 20_000 && Size(true) < 22_600, $"worker {Size(false)}, administrator {Size(true)}");
        // Chunks always fit a reply, with room for its fields.
        Assert.True(RelayLimits.DocChunkTokens * 2 < RelayLimits.MaxDocReplyTokens && RelayLimits.DocChunkChars * 2 < RelayLimits.MaxDocReplyBytes);
    }

    // ---- Review fixes ----

    [Fact]
    public void A_json_file_with_a_repeated_key_or_too_many_nodes_is_skipped_as_malformed_and_never_throws()
    {
        foreach (var json in new[]
        {
            """{ "openapi": "3.0.0", "paths": { "/a": { "get": {}, "get": {} } } }""",
            """{ "openapi": "3.0.0", "openapi": "3.1.0" }""",
            """{ "openapi": "3.0.0", "components": { "schemas": { "A": { "properties": { "x": {}, "x": {} } } } } }""",
            "{\"openapi\":\"3.0.0\",\"x\":[" + string.Join(',', Enumerable.Repeat("0", RelayLimits.MaxOpenApiNodes + 1)) + "]}"
        })
        {
            Assert.Null(DocFormats.Build("api.json", json, Tokens, out var reason));
            Assert.Equal(DocSkipReason.Malformed, reason);
        }
    }

    [Fact]
    public void How_do_I_claim_a_task_ranks_the_claim_heading_first()
    {
        var hits = Search(new()
        {
            ["docs/guide.md"] = "# Guide\n\n## How do I get started\n\nHow do I do it? Do a build, then do a run. I do a lot of it, and a lot more. How do I know? I do, and I do a lot.\n\n## Claim\n\nTake a task for your session.\n\n## Tasks\n\nA task list, with a task per line.\n",
            ["docs/faq.md"] = "# FAQ\n\n## How do I do this\n\nHow do I do a thing? I do it. Do I? How? A lot of how and do and I.\n"
        }, "how do I claim a task");
        Assert.Equal(("docs/guide.md", "claim"), (hits[0].Path, hits[0].Anchor));
    }

    /// <summary>Indexes the files in one document grain each, in a plain silo, and runs one fan-out search.</summary>
    private static DocHit[] Search(Dictionary<string, string> files, string query)
    {
        using var git = GitRepositories.Create(files);
        using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Clone
        });
        host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None).GetAwaiter().GetResult();
        return host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").SearchAsync(query, null, null, 5, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Hits;
    }

    [Fact]
    public async Task Fetch_never_recurses_into_submodules_and_sends_the_token_only_to_the_remote_url()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
        var tokenFile = Path.Combine(Path.GetTempPath(), "cocern-doc-token-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, "read-only-secret\n");
        try
        {
            var watcher = new WatchingGit { FakeFetch = true };
            await using var host = DocHost(git, watcher, new() { ["Relay:DocCache:Repositories:0:TokenFile"] = tokenFile });
            var runner = host.Services.GetRequiredService<DocSyncRunner>();
            // A local-path remote never gets the header.
            await runner.SyncAsync("repo", null, CancellationToken.None);
            var (arguments, environment) = watcher.Fetches.Last();
            Assert.Contains("--no-recurse-submodules", arguments);
            Assert.False(environment?.ContainsKey("GIT_CONFIG_COUNT") ?? false);
            // An HTTPS remote gets it, scoped to that URL, and never on the command line.
            git.SetCloneRemote("https://git.example.com/org/repo.git");
            await runner.SyncAsync("repo", null, CancellationToken.None);
            (arguments, environment) = watcher.Fetches.Last();
            Assert.Equal(new[] { "fetch", "--quiet", "--no-tags", "--no-recurse-submodules", "origin", "+refs/heads/main:refs/remotes/origin/main" }, arguments);
            Assert.Equal("http.https://git.example.com/org/repo.git.extraHeader", environment!["GIT_CONFIG_KEY_0"]);
            Assert.Equal("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:read-only-secret")), environment["GIT_CONFIG_VALUE_0"]);
            Assert.DoesNotContain(arguments, argument => argument.Contains("read-only-secret"));
            // Remote URLs with credentials, and empty or oversized token files, send nothing.
            git.SetCloneRemote("https://user:pass@git.example.com/org/repo.git");
            await runner.SyncAsync("repo", null, CancellationToken.None);
            Assert.False(watcher.Fetches.Last().Environment?.ContainsKey("GIT_CONFIG_COUNT") ?? false);
            git.SetCloneRemote("https://git.example.com/org/repo.git");
            foreach (var content in new[] { "  \n", new string('t', RelayLimits.MaxDocTokenFileBytes + 1) })
            {
                await File.WriteAllTextAsync(tokenFile, content);
                await runner.SyncAsync("repo", null, CancellationToken.None);
                Assert.False(watcher.Fetches.Last().Environment?.ContainsKey("GIT_CONFIG_COUNT") ?? false);
            }
        }
        finally { File.Delete(tokenFile); }
    }

    [Fact]
    public async Task A_known_file_that_becomes_malformed_frees_its_bytes_for_the_rest_of_the_sync()
    {
        const string valid = """{"openapi":"3.0.0","paths":{}}""";
        var prose = "# B\n\n" + new string('b', 50) + "\n";
        using var git = GitRepositories.Create(new() { ["api.json"] = valid, ["docs/b.md"] = prose });
        await using var host = DocHost(git, settings: new() { ["Relay:DocCache:MaxRepositoryBytes"] = "80" });
        await SyncAsync(host);
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        Assert.Equal(new[] { "api.json" }, (await directory.ReadAsync()).Entries.Select(entry => entry.Path));
        Assert.Equal(new[] { "docs/b.md" }, (await directory.ReadAsync()).Skipped);
        git.Commit(new() { ["api.json"] = """{ "openapi": """, ["docs/b.md"] = prose.Replace('b', 'c') });
        await SyncAsync(host);
        var view = await directory.ReadAsync();
        Assert.Equal(new[] { "docs/b.md" }, view.Entries.Select(entry => entry.Path));
        Assert.Equal(new[] { "api.json" }, view.Skipped);
    }

    [Fact]
    public async Task A_caller_giving_up_on_doc_sync_does_not_fail_its_retry()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher);
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        watcher.HoldFetch();
        using var gaveUp = new CancellationTokenSource();
        var first = runner.SyncNowAsync("repo", null, "r-1", gaveUp.Token);
        await WaitUntilAsync(() => Task.FromResult(watcher.FetchesStarted == 1));
        gaveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var retry = runner.SyncNowAsync("repo", null, "r-1", CancellationToken.None);
        watcher.ReleaseFetch();
        Assert.Equal(git.Head(), (await retry).Commit);
        Assert.Equal(1, watcher.FetchesStarted);
    }

    [Theory]
    [InlineData("MaxDocuments", "1")]
    [InlineData("MaxRepositoryBytes", "80")]
    public async Task A_file_skipped_for_room_is_indexed_once_room_frees_up(string setting, string limit)
    {
        var a = "# A\n\n" + new string('a', 50) + "\n";
        var b = "# B\n\n" + new string('b', 50) + "\n";
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = a, ["docs/b.md"] = b });
        await using var host = DocHost(git, settings: new() { ["Relay:DocCache:" + setting] = limit });
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        await SyncAsync(host);
        Assert.Equal(new[] { "docs/a.md" }, (await directory.ReadAsync()).Entries.Select(entry => entry.Path));
        Assert.Equal(new[] { "docs/b.md" }, (await directory.ReadAsync()).Skipped);
        // Deleting a frees the room; the incremental sync sees only a's path change, yet the unchanged b comes in from the current commit.
        git.Delete("docs/a.md");
        var freed = await runner.SyncAsync("repo", null, CancellationToken.None);
        var view = await directory.ReadAsync();
        Assert.Equal(new[] { "docs/b.md" }, view.Entries.Select(entry => entry.Path));
        Assert.Empty(view.Skipped);
        Assert.Equal((git.Head(), 2), (freed.Commit, freed.Changed));

        // A directory already at the head with a file waiting for room (as one synced before this change, or where room came back
        // without a commit): a doc_sync on the unchanged commit reconsiders it.
        var waiting = new DocDirectoryUpdate(view.Branch, view.Commit, false, [], ["docs/b.md"], ["docs/b.md"], [], view.Fingerprint, [new DocWaiting("docs/b.md", 0)]);
        await directory.ApplyAsync(waiting);
        Assert.Empty((await directory.ReadAsync()).Entries);
        var manual = await runner.SyncNowAsync("repo", null, "same-commit");
        Assert.Equal((git.Head(), 1), (manual.Commit, manual.Changed));
        Assert.Equal(new[] { "docs/b.md" }, (await directory.ReadAsync()).Entries.Select(entry => entry.Path));
        Assert.Empty((await directory.ReadAsync()).Skipped);
        // With nothing waiting, an unchanged commit still does nothing.
        Assert.Equal(0, (await runner.SyncAsync("repo", null, CancellationToken.None)).Changed);
    }

    [Fact]
    public async Task Files_waiting_for_room_are_reread_only_once_the_smallest_fits()
    {
        var a = "# A\n\n" + new string('a', 50) + "\n";
        var b = "# B\n\n" + new string('b', 50) + "\n";
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = a, ["docs/b.md"] = b });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher, settings: new() { ["Relay:DocCache:MaxRepositoryBytes"] = "80" });
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        await SyncAsync(host);
        Assert.Equal([new DocWaiting("docs/b.md", Encoding.UTF8.GetByteCount(b))], (await directory.ReadAsync()).CapacitySkipped!);
        int Reads() => watcher.Shows.Count(show => show.EndsWith(":docs/b.md", StringComparison.Ordinal));
        var reads = Reads();
        // Room for 8 more bytes but not for b's 56: a new commit, and a doc_sync on it, leave b unread.
        git.Commit(new() { ["docs/c.md"] = "# C\n\nc\n" });
        Assert.Equal(1, (await runner.SyncAsync("repo", null, CancellationToken.None)).Changed);
        var manual = await runner.SyncNowAsync("repo", null, "unchanged");
        Assert.Equal(0, manual.Changed);
        Assert.Equal(reads, Reads());
        // Once a's bytes are freed, b fits and is read and indexed.
        git.Delete("docs/a.md");
        await runner.SyncAsync("repo", null, CancellationToken.None);
        Assert.Equal(reads + 1, Reads());
        Assert.Equal(new[] { "docs/b.md", "docs/c.md" }, (await directory.ReadAsync()).Entries.Select(entry => entry.Path));
    }

    [Fact]
    public async Task Of_the_files_waiting_for_room_only_those_that_fit_are_reread()
    {
        var a = "# A\n\n" + new string('a', 50) + "\n";
        var b = "# B\n\n" + new string('b', 50) + "\n";
        var c = "# C\n\n" + new string('c', 14) + "\n";
        var d = "# D\n\ndddd\n";
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = a, ["docs/b.md"] = b, ["docs/c.md"] = c, ["docs/d.md"] = d });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher, settings: new() { ["Relay:DocCache:MaxRepositoryBytes"] = "80" });
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        await SyncAsync(host);
        // a (56) and c (20) use 76 bytes; b (56) and d (10) wait.
        Assert.Equal([new DocWaiting("docs/b.md", 56), new DocWaiting("docs/d.md", 10)], (await directory.ReadAsync()).CapacitySkipped!);
        int Reads(string path) => watcher.Shows.Count(show => show.EndsWith(":" + path, StringComparison.Ordinal));
        var (readsB, readsD) = (Reads("docs/b.md"), Reads("docs/d.md"));
        // c shrinks to 8 bytes: 64 used, so d fits and b still cannot. Room a commit frees by shrinking a file counts from the next sync,
        // and then only d is read again.
        git.Commit(new() { ["docs/c.md"] = "# C\n\nc\n" });
        await runner.SyncAsync("repo", null, CancellationToken.None);
        await runner.SyncNowAsync("repo", null, "unchanged");
        Assert.Equal((readsB, readsD + 1), (Reads("docs/b.md"), Reads("docs/d.md")));
        var view = await directory.ReadAsync();
        Assert.Equal(new[] { "docs/a.md", "docs/c.md", "docs/d.md" }, view.Entries.Select(entry => entry.Path));
        Assert.Equal([new DocWaiting("docs/b.md", 56)], view.CapacitySkipped!);
    }

    [Fact]
    public async Task The_repository_byte_cap_skips_and_counts_files_past_it()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# A\n\n" + new string('a', 50) + "\n", ["docs/b.md"] = "# B\n\n" + new string('b', 50) + "\n", ["docs/c.md"] = "# C\n\nc\n" });
        await using var host = DocHost(git, settings: new() { ["Relay:DocCache:MaxRepositoryBytes"] = "100" });
        var outcome = await SyncAsync(host);
        var view = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").ReadAsync();
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(new[] { "docs/a.md", "docs/c.md" }, view.Entries.Select(entry => entry.Path));
        Assert.Equal(new[] { "docs/b.md" }, view.Skipped);
        Assert.True(view.Entries.Sum(entry => entry.Bytes) <= 100);
        Assert.Contains("MaxRepositoryBytes", Assert.Throws<InvalidOperationException>(() => DocCacheSettings.From(Configuration(new() { ["Relay:DocCache:MaxRepositoryBytes"] = (RelayLimits.MaxRepositoryDocBytesCeiling + 1).ToString() }), new FakeEnvironment())).Message);
    }

    [Fact]
    public async Task A_recorded_commit_that_is_gone_falls_back_to_the_branch_head_and_a_failed_read_does_not()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Refunds\n\nRefunds take five days.\n", ["docs/b.md"] = "# Refunds too\n\nMore refunds.\n" });
        var watcher = new WatchingGit();
        await using var host = DocHost(git, watcher);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo");
        // A document that throws (here a git show that times out) is counted as not searched and is no fall-back.
        watcher.FailPath = "docs/b.md";
        var failed = Data(await Tool(client, "doc_search", new { repositoryId = "repo", query = "refunds" }));
        Assert.Equal((true, 1), (failed.GetProperty("partial").GetBoolean(), failed.GetProperty("notSearched").GetInt32()));
        Assert.False((await directory.ReadAsync()).NeedsResync);
        watcher.FailPath = null;
        // A valid commit ID the clone does not have: the grain reads the branch head instead, and the directory asks for a full resync.
        var view = await directory.ReadAsync();
        var gone = view.Entries.Single(entry => entry.Path == "docs/a.md") with { Commit = "0123456789abcdef0123456789abcdef01234567", Hash = "changed" };
        await directory.ApplyAsync(new DocDirectoryUpdate(view.Branch, view.Commit, false, [gone], [], [], [], view.Fingerprint));
        var hits = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "five days", path = "docs/a.md" }));
        Assert.Equal(git.Head()[..12], Assert.Single(hits).GetProperty("commit").GetString());
        await WaitUntilAsync(async () => (await directory.ReadAsync()).NeedsResync);
        var resync = await host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None);
        Assert.Equal(1, resync.Changed);
        Assert.False((await directory.ReadAsync()).NeedsResync);
        Assert.Equal(git.Head(), (await directory.ReadAsync()).Entries.Single(entry => entry.Path == "docs/a.md").Commit);
    }

    [Fact]
    public async Task A_first_chunk_over_the_reply_budget_is_cut_where_it_fits_and_next_points_inside_it()
    {
        // Each '<' is one character but six bytes once JSON-escaped, so one chunk is over the reply's byte budget.
        var section = "# Angles\n\n" + new string('<', 2_900) + "\n\n" + new string('>', 2_900) + "\n";
        using var git = GitRepositories.Create(new() { ["docs/angles.md"] = section });
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var text = new StringBuilder();
        var anchors = new List<string>();
        string? anchor = "angles";
        while (anchor is not null)
        {
            anchors.Add(anchor);
            var reply = await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/angles.md", anchor });
            var data = Data(reply);
            var json = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.ToJsonString();
            Assert.True(Encoding.UTF8.GetByteCount(json) <= RelayLimits.MaxDocReplyBytes && Tokens.Count(json) <= RelayLimits.MaxDocReplyTokens);
            Assert.NotEmpty(data.GetProperty("text").GetString()!);
            text.Append(data.GetProperty("text").GetString());
            anchor = data.TryGetProperty("next", out var next) ? next.GetString() : null;
            Assert.True(anchors.Count < 40);
        }
        Assert.Equal(section, text.ToString());
        Assert.Contains(anchors, item => item.Contains(DocumentGrain.OffsetSeparator));
        Assert.Equal((HttpStatusCode.NotFound, "NotFound"), Status(await Tool(client, "doc_section", new { repositoryId = "repo", path = "docs/angles.md", anchor = "angles~1+999999" })));
    }

    [Fact]
    public async Task A_file_named_like_an_option_is_only_a_path_and_symlinks_are_skipped()
    {
        using var git = GitRepositories.Create(new() { ["--output=x.md"] = "# Dash\n\nDash refunds.\n", ["docs/real.md"] = "# Real\n\nReal refunds.\n" });
        git.SymlinkAndCommit("docs/passwd.md", "/etc/passwd");
        git.SymlinkAndCommit("docs/link.md", "real.md");
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        var outcome = await SyncAsync(host);
        var view = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").ReadAsync();
        Assert.Equal(new[] { "--output=x.md", "docs/real.md" }, view.Entries.Select(entry => entry.Path));
        Assert.Equal(new[] { "docs/link.md", "docs/passwd.md" }, view.Skipped);
        Assert.Equal(2, outcome.Skipped);
        Assert.Contains("Dash refunds", Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "--output=x.md", anchor = "dash" })).GetProperty("text").GetString());
        Assert.False(File.Exists(Path.Combine(git.Clone, "x.md")));
        Assert.Empty(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "root etc passwd" })));
        // A symlink added later is skipped by the incremental sync too.
        git.SymlinkAndCommit("docs/late.md", "real.md");
        Assert.Equal(1, (await host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None)).Skipped);
    }

    [Fact]
    public async Task Changed_sync_settings_force_a_full_resync_without_a_new_commit()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n", ["notes/b.md"] = "# Beta\n\nText.\n" });
        var database = Path.Combine(Path.GetTempPath(), "cocern-doc-settings-" + Guid.NewGuid().ToString("N"), "relay.db");
        await using (var narrow = DocHost(git, settings: new() { ["Relay:DocCache:Repositories:0:Patterns:0:Glob"] = "docs/**" }, databasePath: database))
        {
            await SyncAsync(narrow);
            Assert.Equal(0, (await SyncAsync(narrow)).Changed);
        }
        await using var wide = DocHost(git, databasePath: database);
        var outcome = await SyncAsync(wide);
        Assert.Equal(1, outcome.Changed);
        var view = await wide.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").ReadAsync();
        Assert.Equal(new[] { "docs/a.md", "notes/b.md" }, view.Entries.Select(entry => entry.Path));
        Assert.Equal(0, (await SyncAsync(wide)).Changed);
    }

    // ---- Helpers ----

    /// <param name="usageWrites">How often a directory writes its counted usage; the hub's default when null.</param>
    private static RelayHostFixture DocHost(GitRepositories git, GitCli? cli = null, Dictionary<string, string?>? settings = null, string? databasePath = null, TimeSpan? usageWrites = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo",
            ["Relay:Clients:1:Repositories:1"] = "repo2",
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Clone
        };
        foreach (var (key, value) in settings ?? []) all[key] = value;
        return new RelayHostFixture(databasePath, settings: all, configureServices: services =>
        {
            if (cli is not null) { services.RemoveAll<GitCli>(); services.AddSingleton(cli); }
            if (usageWrites is { } interval) services.AddSingleton(new DocUsageOptions { WriteInterval = interval });
        });
    }

    /// <summary>A document's usage counter as storage holds it now, not as the directory shows it.</summary>
    private static async Task<long> PersistedUsageAsync(RelayHostFixture host, string path, string counter)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_extract(CAST(payload AS TEXT), $path) FROM grain_state WHERE state_name = 'docs' AND grain_key = 'repo';";
        command.Parameters.AddWithValue("$path", $"$.Usage.\"{path}\".{counter}");
        return await command.ExecuteScalarAsync() is long value ? value : 0;
    }

    private static Task<DocSyncOutcome> SyncAsync(RelayHostFixture host)
    {
        _ = host.Services;
        return host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None);
    }

    private static HttpClient Admin(RelayHostFixture host)
    {
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Tool(HttpClient client, string tool, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, arguments);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone());
    }

    private static (HttpStatusCode, string) Status((HttpStatusCode Status, JsonElement Body) reply) => (reply.Status, reply.Body.GetProperty("code").GetString()!);

    private static JsonElement Data((HttpStatusCode Status, JsonElement Body) reply)
    {
        using var document = JsonDocument.Parse(reply.Body.GetProperty("dataJson").GetString()!);
        return document.RootElement.Clone();
    }

    private static JsonElement[] Hits((HttpStatusCode Status, JsonElement Body) reply)
    {
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(reply));
        return Data(reply).GetProperty("hits").EnumerateArray().ToArray();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(50);
        }
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Configuration(Dictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?> { ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = "clone" };
        foreach (var (key, value) in extra) values[key] = value;
        return new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>Watches git commands: counts concurrent fetches and shows, can hold fetches, and can slow every show or one path's.</summary>
    private sealed class WatchingGit : GitCli
    {
        private int fetches, shows;
        public int Commands;
        private TaskCompletionSource? hold;
        public int FetchesStarted;
        public int PeakFetches;
        public int PeakShows;
        public TimeSpan ShowDelay { get; set; }
        public string? SlowPath { get; init; }
        public string? FailPath { get; set; }
        /// <summary>Answer fetch as done without running it, so a remote URL that is never reached can still be checked.</summary>
        public bool FakeFetch { get; init; }
        public System.Collections.Concurrent.ConcurrentQueue<(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment)> Fetches { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> Shows { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<string>> Arguments { get; } = new();

        public void HoldFetch() => hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseFetch() => hold?.TrySetResult();

        private TaskCompletionSource? slowHold;
        /// <summary>Holds every read of <see cref="SlowPath"/> until <see cref="ReleaseSlow"/>, so a search's timeout passes however slow the machine is.</summary>
        public void HoldSlow() => slowHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseSlow() => slowHold?.TrySetResult();

        public override async Task<GitResult> RunAsync(string directory, IReadOnlyList<string> arguments, int maxOutputBytes, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Commands);
            Arguments.Enqueue(arguments.ToArray());
            if (arguments[0] == "fetch")
            {
                Interlocked.Increment(ref FetchesStarted);
                Fetches.Enqueue((arguments.ToArray(), environment is null ? null : new Dictionary<string, string>(environment)));
                if (FakeFetch) return new GitResult(0, [], false, "");
                Max(ref PeakFetches, Interlocked.Increment(ref fetches));
                try { if (hold is { } wait) await wait.Task; return await base.RunAsync(directory, arguments, maxOutputBytes, environment, cancellationToken); }
                finally { Interlocked.Decrement(ref fetches); }
            }
            if (arguments[0] == "show")
            {
                Shows.Enqueue(arguments[^1]);
                if (FailPath is { } fail && arguments[^1].EndsWith(":" + fail, StringComparison.Ordinal)) throw new TimeoutException("git show ran too long.");
                Max(ref PeakShows, Interlocked.Increment(ref shows));
                try
                {
                    if (ShowDelay > TimeSpan.Zero && (SlowPath is null || arguments[^1].EndsWith(":" + SlowPath, StringComparison.Ordinal))) await Task.Delay(ShowDelay, cancellationToken);
                    if (slowHold is { } slow && SlowPath is not null && arguments[^1].EndsWith(":" + SlowPath, StringComparison.Ordinal)) await slow.Task.WaitAsync(cancellationToken);
                    return await base.RunAsync(directory, arguments, maxOutputBytes, environment, cancellationToken);
                }
                finally { Interlocked.Decrement(ref shows); }
            }
            return await base.RunAsync(directory, arguments, maxOutputBytes, environment, cancellationToken);
        }

        private static void Max(ref int target, int value)
        {
            for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
                if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }

    /// <summary>
    /// A temporary origin (bare), a work clone that commits and pushes, and the hub's clone. Everything is local: git fetches from a path,
    /// with no network.
    /// </summary>
    private sealed class GitRepositories : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-doc-" + Guid.NewGuid().ToString("N"));
        private string Work => Path.Combine(root, "work");
        private string Origin => Path.Combine(root, "origin.git");
        public string Clone => Path.Combine(root, "clone");

        public static GitRepositories Create(Dictionary<string, string> files)
        {
            var repositories = new GitRepositories();
            Directory.CreateDirectory(repositories.Work);
            Run(repositories.Work, "init", "-q", "-b", "main");
            repositories.Write(files);
            Run(repositories.Work, "add", "-A");
            Run(repositories.Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "first");
            Run(repositories.root, "clone", "-q", "--bare", repositories.Work, repositories.Origin);
            Run(repositories.Work, "remote", "add", "origin", repositories.Origin);
            Run(repositories.Work, "fetch", "-q", "origin");
            Run(repositories.root, "clone", "-q", repositories.Origin, repositories.Clone);
            return repositories;
        }

        public string Head(string branch = "main") => Run(Work, "rev-parse", branch).Trim();

        public void Commit(Dictionary<string, string> files)
        {
            Write(files);
            Push("change");
        }

        public void Delete(string path)
        {
            Run(Work, "rm", "-q", path);
            Push("delete");
        }

        public void Move(string from, string to)
        {
            Run(Work, "mv", from, to);
            Push("move");
        }

        public void CommitOnBranch(string branch, string path, string text)
        {
            Run(Work, "checkout", "-q", "-b", branch);
            Write(new() { [path] = text });
            Run(Work, "add", "-A");
            Run(Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", branch);
            Run(Work, "push", "-q", "origin", branch);
            Run(Work, "checkout", "-q", "main");
        }

        public void SymlinkAndCommit(string link, string target)
        {
            var full = Path.Combine(Work, link);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.CreateSymbolicLink(full, target);
            Push("symlink");
        }

        public void SetCloneRemote(string url) => Run(Clone, "remote", "set-url", "origin", url);

        /// <summary>Makes the hub's clone a local-only repository, with no remote to fetch from.</summary>
        public void RemoveCloneRemote() => Run(Clone, "remote", "remove", "origin");

        public string CloneHead() => Run(Clone, "rev-parse", "HEAD").Trim();

        public string CloneRef(string reference) => Run(Clone, "rev-parse", reference).Trim();

        /// <summary>A branch made and committed in the hub's clone only, never pushed; the clone stays on it.</summary>
        public void CommitInCloneOnNewBranch(string branch, string path, string text)
        {
            Run(Clone, "checkout", "-q", "-b", branch);
            CommitInCloneOnly(path, text);
        }

        public string CloneStatus() => Run(Clone, "status", "--porcelain", "--untracked-files=all");

        public void WriteInClone(string path, string text)
        {
            var full = Path.Combine(Clone, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public void CommitInCloneOnly(string path, string text)
        {
            WriteInClone(path, text);
            Run(Clone, "add", path);
            Run(Clone, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "local only");
        }

        private void Push(string message)
        {
            Run(Work, "add", "-A");
            Run(Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", message);
            Run(Work, "push", "-q", "origin", "main");
        }

        private void Write(Dictionary<string, string> files)
        {
            foreach (var (path, text) in files)
            {
                var full = Path.Combine(Work, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
        }

        private static string Run(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
            return output.Result;
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

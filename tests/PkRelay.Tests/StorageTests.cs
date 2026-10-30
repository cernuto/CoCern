using Microsoft.Data.Sqlite;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using PkRelay.Core;
using PkRelay.Domain;
using PkRelay.Storage;
using Xunit;

namespace PkRelay.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pkrelay-storage-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "relay.db");
    private static readonly GrainId Grain = GrainId.Create("test.board", "repository/default");
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public StorageTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Create_read_and_reopen_preserve_opaque_state_and_due_work()
    {
        string etag;
        string serviceId;
        string incarnationId;
        using (var store = new SqliteStore(Database))
        {
            serviceId = store.ServiceId;
            incarnationId = store.IncarnationId;
            var state = State("first", new DueWorkItem("retry", "effect", Now));
            await new SqliteGrainStorage(store).WriteStateAsync("board", Grain, state);
            Assert.True(state.RecordExists);
            etag = Assert.IsType<string>(state.ETag);
        }
        using var reopened = new SqliteStore(Database);
        Assert.Equal(serviceId, reopened.ServiceId);
        Assert.NotEqual(incarnationId, reopened.IncarnationId);
        var loaded = State("placeholder");
        await new SqliteGrainStorage(reopened).ReadStateAsync("board", Grain, loaded);
        Assert.Equal("first", loaded.State.Value.Text);
        Assert.Equal(etag, loaded.ETag);
        Assert.True(loaded.RecordExists);
        var due = Assert.Single(await reopened.ReadDueAsync(Now, 10));
        Assert.Equal(new DueWorkReference("board", "test.board", "repository/default", "retry", "effect", Now), due);
    }

    [Fact]
    public async Task Stale_writes_and_clears_leave_committed_state_and_caller_metadata_unchanged()
    {
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        var first = State("first");
        await provider.WriteStateAsync("board", Grain, first);
        var stale = State("stale");
        await provider.ReadStateAsync("board", Grain, stale);
        var staleEtag = stale.ETag;
        first.State.Value.Text = "second";
        await provider.WriteStateAsync("board", Grain, first);
        await Assert.ThrowsAsync<InconsistentStateException>(() => provider.WriteStateAsync("board", Grain, stale));
        await Assert.ThrowsAsync<InconsistentStateException>(() => provider.ClearStateAsync("board", Grain, stale));
        Assert.Equal(staleEtag, stale.ETag);
        Assert.True(stale.RecordExists);
        var loaded = State("");
        await provider.ReadStateAsync("board", Grain, loaded);
        Assert.Equal("second", loaded.State.Value.Text);
    }

    [Fact]
    public async Task Tombstone_recreation_rejects_old_generation_and_unobserved_creation()
    {
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        var current = State("first", new DueWorkItem("retry", "effect", Now));
        await provider.WriteStateAsync("board", Grain, current);
        var stale = State("stale") ;
        await provider.ReadStateAsync("board", Grain, stale);
        await provider.ClearStateAsync("board", Grain, current);
        Assert.False(current.RecordExists);
        Assert.Empty(await store.ReadDueAsync(Now, 10));
        var tombstone = State("reset me");
        await provider.ReadStateAsync("board", Grain, tombstone);
        Assert.False(tombstone.RecordExists);
        Assert.Equal(current.ETag, tombstone.ETag);
        Assert.Equal("", tombstone.State.Value.Text);
        await Assert.ThrowsAsync<InconsistentStateException>(() => provider.WriteStateAsync("board", Grain, State("unobserved")));
        tombstone.State.Value.Text = "recreated";
        await provider.WriteStateAsync("board", Grain, tombstone);
        await Assert.ThrowsAsync<InconsistentStateException>(() => provider.WriteStateAsync("board", Grain, stale));
        await Assert.ThrowsAsync<InconsistentStateException>(() => provider.ClearStateAsync("board", Grain, stale));
        Assert.NotEqual(current.ETag, tombstone.ETag);
    }

    [Fact]
    public async Task Due_index_is_replaced_atomically_and_namespaced_by_state_and_grain()
    {
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        var state = State("first", new DueWorkItem("old", "effect", Now));
        await provider.WriteStateAsync("board", Grain, state);
        await provider.WriteStateAsync("other", Grain, State("other", new DueWorkItem("other", "lease", Now)));
        await provider.WriteStateAsync("board", GrainId.Create("test.session", "repository/default"), State("session", new DueWorkItem("session", "lease", Now)));
        state.State.DueWork = [new("new", "effect", Now.AddMinutes(1))];
        await provider.WriteStateAsync("board", Grain, state);
        Assert.Equal(2, (await store.ReadDueAsync(Now, 10)).Count);
        var all = await store.ReadDueAsync(Now.AddMinutes(1), 10);
        Assert.Equal(3, all.Count);
        Assert.DoesNotContain(all, item => item.WorkId == "old");
        Assert.Single(await store.ReadDueAsync(Now.AddMinutes(1), 1));
        var committedEtag = state.ETag;
        state.State.Value.Text = "must roll back";
        state.State.DueWork = [new("duplicate", "effect", Now), new("duplicate", "effect", Now)];
        await Assert.ThrowsAsync<SqliteException>(() => provider.WriteStateAsync("board", Grain, state));
        Assert.Equal(committedEtag, state.ETag);
        var loaded = State("");
        await provider.ReadStateAsync("board", Grain, loaded);
        Assert.Equal("first", loaded.State.Value.Text);
        Assert.Contains(await store.ReadDueAsync(Now.AddMinutes(1), 10), item => item.WorkId == "new");
    }

    [Fact]
    public async Task Backup_recovers_committed_wal_state_and_due_work()
    {
        var backup = Path.Combine(_directory, "backup.db");
        using (var store = new SqliteStore(Database))
        {
            var state = State("backed up", new DueWorkItem("retry", "effect", Now));
            await new SqliteGrainStorage(store).WriteStateAsync("board", Grain, state);
            await store.BackupAsync(backup);
        }
        using var recovered = new SqliteStore(backup);
        var loaded = State("");
        await new SqliteGrainStorage(recovered).ReadStateAsync("board", Grain, loaded);
        Assert.Equal("backed up", loaded.State.Value.Text);
        Assert.Single(await recovered.ReadDueAsync(Now, 10));
    }

    [Fact]
    public async Task Due_pages_expose_later_work_while_first_page_remains_due()
    {
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        for (var index = 0; index < 80; index++)
        {
            var stateName = index % 2 == 0 ? "a" : "b";
            var grainId = GrainId.Create("test.kind" + index % 3, index.ToString("D3"));
            await provider.WriteStateAsync(stateName, grainId, State("queued", new DueWorkItem("retry", "effect", Now)));
        }
        IDueWorkSource source = store;
        var first = await source.ReadDueAsync(Now, 64);
        var later = await source.ReadDueAsync(Now, 64, offset: 64);
        Assert.Equal(64, first.Count);
        Assert.Equal(16, later.Count);
        Assert.Empty(first.Intersect(later));
        Assert.Equal(80, first.Concat(later).Distinct().Count());
        Assert.Equal(first.ToArray(), (await source.ReadDueAsync(Now, 64)).ToArray());
        Assert.Empty(await source.ReadDueAsync(Now, 64, offset: 80));
        Assert.Empty(await source.ReadDueAsync(Now, 64, offset: int.MaxValue));
    }

    [Fact]
    public async Task Due_pages_reject_negative_offset()
    {
        using var store = new SqliteStore(Database);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadDueAsync(Now, 64, offset: -1));
    }

    [Fact]
    public async Task Backup_overwrites_existing_snapshot_and_rejects_live_destination()
    {
        var backup = Path.Combine(_directory, "backup.db");
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        var state = State("first");
        await provider.WriteStateAsync("board", Grain, state);
        await store.BackupAsync(backup);
        state.State.Value.Text = "new snapshot";
        await provider.WriteStateAsync("board", Grain, state);
        await store.BackupAsync(backup);
        using var recovered = new SqliteStore(backup);
        var loaded = State("");
        await new SqliteGrainStorage(recovered).ReadStateAsync("board", Grain, loaded);
        Assert.Equal("new snapshot", loaded.State.Value.Text);
        await Assert.ThrowsAsync<IOException>(() => store.BackupAsync(backup));
        await Assert.ThrowsAsync<ArgumentException>(() => store.BackupAsync(Database));
    }

    [Fact]
    public void Database_uses_wal_and_rejects_newer_schema()
    {
        using (var store = new SqliteStore(Database))
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", command.ExecuteScalar());
            command.CommandText = "PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new SqliteStore(Database));
        // A failed constructor released ownership, so another attempt reaches schema validation again.
        Assert.Throws<InvalidOperationException>(() => new SqliteStore(Database));
    }

    [Fact]
    public void Second_host_is_rejected_until_first_owner_disposes()
    {
        using (var first = new SqliteStore(Database))
            Assert.Throws<IOException>(() => new SqliteStore(Database));
        using var next = new SqliteStore(Database);
    }

    [Fact]
    public async Task Missing_read_resets_state_and_reports_no_etag()
    {
        using var store = new SqliteStore(Database);
        var state = State("reset me");
        state.ETag = "old";
        state.RecordExists = true;
        await new SqliteGrainStorage(store).ReadStateAsync("missing", Grain, state);
        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.Equal("", state.State.Value.Text);
    }

    private static TestGrainState<StateEnvelope<Payload>> State(string text, params DueWorkItem[] work) =>
        new() { State = new() { Value = new() { Text = text }, DueWork = work.ToList() } };

    public sealed class Payload { public string Text { get; set; } = ""; }

    private sealed class TestGrainState<T> : IGrainState<T>
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public T State { get; set; } = default!;
        public string? ETag { get; set; }
        public bool RecordExists { get; set; }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

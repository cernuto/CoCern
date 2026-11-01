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

    [Fact]
    public async Task Version_1_database_upgrades_to_version_2_after_a_backup()
    {
        CreateVersion1Database("kept");
        using (var store = new SqliteStore(Database))
        {
            var loaded = State("");
            await new SqliteGrainStorage(store).ReadStateAsync("board", Grain, loaded);
            Assert.Equal("kept", loaded.State.Value.Text);
        }
        var backup = Assert.Single(Directory.GetFiles(_directory, "relay.db.pre-migration-*.db"));
        Assert.Equal(1L, Scalar(backup, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(backup, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('activity_log', 'search_index');"));
        Assert.Equal(2L, Scalar(Database, "PRAGMA user_version;"));
        Assert.Equal(3L, Scalar(Database, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('activity_log', 'activity_daily', 'search_index');"));
        // The index table exists from this step; it starts filling when search writes arrive.
        Assert.Equal(0L, Scalar(Database, "SELECT COUNT(*) FROM search_index;"));
    }

    [Fact]
    public async Task Version_2_database_reopens_unchanged_without_another_backup()
    {
        using (var store = new SqliteStore(Database))
            await new SqliteGrainStorage(store).WriteStateAsync("board", Grain, State("first"));
        var schema = Text(Database, "SELECT group_concat(sql, ';') FROM (SELECT sql FROM sqlite_schema ORDER BY name);");
        using (var reopened = new SqliteStore(Database))
        {
            var loaded = State("");
            await new SqliteGrainStorage(reopened).ReadStateAsync("board", Grain, loaded);
            Assert.Equal("first", loaded.State.Value.Text);
        }
        Assert.Empty(Directory.GetFiles(_directory, "*.pre-migration-*"));
        Assert.Equal(2L, Scalar(Database, "PRAGMA user_version;"));
        Assert.Equal(schema, Text(Database, "SELECT group_concat(sql, ';') FROM (SELECT sql FROM sqlite_schema ORDER BY name);"));
    }

    [Fact]
    public void Version_3_database_is_still_refused()
    {
        using (new SqliteStore(Database)) { }
        Execute(Database, "PRAGMA user_version=3;");
        var refused = Assert.Throws<InvalidOperationException>(() => new SqliteStore(Database));
        Assert.Contains("newer", refused.Message);
        Assert.Equal(3L, Scalar(Database, "PRAGMA user_version;"));
    }

    [Fact]
    public void Missing_search_module_fails_clearly_before_migrating()
    {
        CreateVersion1Database("untouched");
        var failure = Assert.Throws<InvalidOperationException>(() => new SqliteStore(Database, null, "pkrelay_missing_module"));
        Assert.Contains("PKRELAY_MISSING_MODULE module", failure.Message);
        Assert.Contains("search", failure.Message);
        Assert.Equal(1L, Scalar(Database, "PRAGMA user_version;"));
        Assert.Empty(Directory.GetFiles(_directory, "*.pre-migration-*"));
        // The failed start released ownership, and the real module check passes.
        using var store = new SqliteStore(Database);
    }

    [Fact]
    public async Task Activity_events_are_batched_and_written_in_their_own_transaction()
    {
        using (var store = new SqliteStore(Database))
        {
            for (var index = 0; index < 700; index++) store.Record(Call(Now.AddSeconds(index), "get_status", session: "s" + index % 3));
            await store.FlushActivityAsync();
            Assert.Equal(700L, Scalar(Database, "SELECT COUNT(*) FROM activity_log;"));
            store.Record(Call(Now, "task_update", inputTokens: 5));
        }
        // Disposal drains what is still queued.
        Assert.Equal(701L, Scalar(Database, "SELECT COUNT(*) FROM activity_log;"));
        Assert.Equal(5L, Scalar(Database, "SELECT input_tokens FROM activity_log WHERE tool = 'task_update';"));
    }

    [Fact]
    public async Task Full_activity_queue_drops_events_instead_of_blocking_calls()
    {
        using var store = new SqliteStore(Database, new ActivityOptions { QueueCapacity = 4, BatchInterval = TimeSpan.FromHours(1) });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++) store.Record(Call(Now, "get_status"));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"Recording took {timer.Elapsed}.");
        Assert.InRange(store.DroppedActivityEvents, 995, 996);
        await store.FlushActivityAsync();
        Assert.InRange(Scalar(Database, "SELECT COUNT(*) FROM activity_log;"), 4, 5);
    }

    [Fact]
    public async Task Retention_rolls_old_rows_into_daily_totals_and_deletes_them()
    {
        using var store = new SqliteStore(Database, new ActivityOptions { RetentionDays = 30 });
        var old = Now.AddDays(-40);
        store.Record(Call(old, "task_update", inputTokens: 10, outputTokens: 2));
        store.Record(Call(old.AddMinutes(1), "task_update", inputTokens: 20, outputTokens: 3));
        store.Record(Call(old.AddMinutes(2), "task_update", ok: false));
        store.Record(Call(Now.AddDays(-1), "task_update", inputTokens: 7));
        await store.FlushActivityAsync();
        Assert.Equal(3, await store.RollUpActivityAsync(Now));
        Assert.Equal(1L, Scalar(Database, "SELECT COUNT(*) FROM activity_log;"));
        Assert.Equal("2026-09-19|task_update|3|1|30|5", Text(Database, "SELECT day || '|' || tool || '|' || calls || '|' || errors || '|' || input_tokens || '|' || output_tokens FROM activity_daily;"));
        Assert.Equal(0, await store.RollUpActivityAsync(Now));
        Assert.Equal(3L, Scalar(Database, "SELECT calls FROM activity_daily;"));
    }

    [Fact]
    public async Task Each_counter_increments_once_per_event()
    {
        using var store = new SqliteStore(Database);
        var task = "task:repo/default/T1";
        var offered = "task:repo/default/T2";
        ActivityEvent[] events =
        [
            // Manager turns: a burst of calls is one turn; a call after a 60-second gap starts another.
            Call(Now, "get_status", session: "manager", role: "manager"),
            Call(Now.AddSeconds(10), "task_get", session: "manager", role: "manager"),
            Call(Now.AddSeconds(200), "task_update", session: "manager", role: "manager", target: task, state: "ready", request: "ready"),
            // Inbox reads: a first page counts; a continuation page and a failed read do not.
            Call(Now.AddSeconds(1), "check_inbox", session: "worker", target: "inbox:worker"),
            Call(Now.AddSeconds(2), "check_inbox", session: "worker"),
            Call(Now.AddSeconds(3), "check_inbox", session: "worker", target: "inbox:worker", ok: false),
            // Wakes are explicit events from inbox delivery.
            new() { Timestamp = Now.AddSeconds(4), Kind = ActivityEvent.Wake, RepositoryId = "repo", SessionId = "worker", Ok = true, Code = "Ok" },
            // Duplicate status writes: the same status text to the same task again; an exact retry and a changed status are not duplicates.
            Call(Now.AddSeconds(300), "task_update", session: "worker", target: task, hash: "A", request: "a1"),
            Call(Now.AddSeconds(301), "task_update", session: "worker", target: task, hash: "A", request: "a1"),
            Call(Now.AddSeconds(302), "task_update", session: "worker", target: task, hash: "A", request: "a2"),
            Call(Now.AddSeconds(303), "task_update", session: "worker", target: task, hash: "B", request: "b1"),
            // An offer that the task becoming blocked ends, and one the session claims successfully.
            Call(Now.AddSeconds(400), "task_update", session: "worker", target: task, offer: offered, request: "o1"),
            Call(Now.AddSeconds(401), "task_update", session: "manager", role: "manager", target: offered, state: "blocked", request: "block"),
            Call(Now.AddSeconds(402), "task_update", session: "other", target: task, offer: "task:repo/default/T3", request: "o2"),
            Call(Now.AddSeconds(403), "task_claim", session: "other", target: "task:repo/default/T3"),
            // Closure and time to accepted result: ready at +200 s, completed at +800 s; a repeated completed write is not a second closure.
            Call(Now.AddSeconds(800), "task_update", session: "worker", target: task, state: "completed", request: "done", inputTokens: 100, outputTokens: 10),
            Call(Now.AddSeconds(801), "task_update", session: "worker", target: task, state: "completed", request: "done", inputTokens: 100, outputTokens: 10),
        ];
        foreach (var item in events) store.Record(item);
        await store.FlushActivityAsync();
        var counters = await store.ReadActivityCountersAsync("repo", Now, Now.AddHours(1));
        Assert.Equal(1, counters.InboxReads);
        Assert.Equal(1, counters.Wakes);
        Assert.Equal(1, counters.DuplicateStatusWrites);
        Assert.Equal(2, counters.OffersMade);
        Assert.Equal(1, counters.OffersEndedBlocked);
        Assert.Equal(3, counters.ManagerTurns);
        Assert.Equal(1, counters.Closures);
        Assert.Equal(3.0, counters.ManagerTurnsPerClosure);
        Assert.Equal(600.0, counters.MedianSecondsToAcceptedResult);
        Assert.Equal(100, counters.InputTokens);
        Assert.Equal(10, counters.OutputTokens);
        Assert.Equal(0, (await store.ReadActivityCountersAsync("other-repo", Now, Now.AddHours(1))).Calls);
    }

    private static ActivityEvent Call(DateTimeOffset at, string tool, string? session = null, string? role = null, bool ok = true, string? target = null, string? hash = null,
        string? state = null, string? offer = null, string? request = null, long? inputTokens = null, long? outputTokens = null) => new()
    {
        Timestamp = at, RepositoryId = "repo", SessionId = session, Role = role, Tool = tool, Ok = ok, Code = ok ? "Ok" : "Busy", RequestBytes = 10, ReplyBytes = 20,
        DurationMilliseconds = 1.5, Target = target, ContentHash = hash, WorkflowState = state, OfferTarget = offer, RequestId = request, InputTokens = inputTokens, OutputTokens = outputTokens
    };

    private void CreateVersion1Database(string text)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new StateEnvelope<Payload> { Value = new() { Text = text } }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
        Execute(Database, $"""
            CREATE TABLE grain_state (state_name TEXT NOT NULL, grain_type TEXT NOT NULL, grain_key TEXT NOT NULL, generation INTEGER NOT NULL, etag TEXT NOT NULL, payload TEXT, PRIMARY KEY (state_name, grain_type, grain_key));
            CREATE TABLE due_work (state_name TEXT NOT NULL, grain_type TEXT NOT NULL, grain_key TEXT NOT NULL, work_id TEXT NOT NULL, kind TEXT NOT NULL, due_ticks INTEGER NOT NULL,
                PRIMARY KEY (state_name, grain_type, grain_key, work_id), FOREIGN KEY (state_name, grain_type, grain_key) REFERENCES grain_state(state_name, grain_type, grain_key));
            CREATE INDEX ix_due_work_due ON due_work(due_ticks, state_name, grain_type, grain_key, work_id);
            CREATE TABLE hub_metadata (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO hub_metadata(name, value) VALUES ('service_id', 'version-one');
            INSERT INTO grain_state VALUES ('board', '{Grain.Type}', '{Grain.Key}', 1, 'etag-one', '{payload.Replace("'", "''")}');
            PRAGMA user_version=1;
            """);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql) { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static long Scalar(string path, string sql) { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static string Text(string path, string sql) { using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!; }

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

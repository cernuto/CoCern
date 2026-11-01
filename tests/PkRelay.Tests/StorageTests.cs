using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using PkRelay.Contracts;
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

    [Fact]
    public async Task One_burst_of_inbox_reads_by_a_session_counts_as_one_read()
    {
        using var store = new SqliteStore(Database);
        // One adapter wait: a first-page read every 2 seconds for 50 seconds.
        for (var second = 0; second <= 50; second += 2) store.Record(Call(Now.AddSeconds(second), "check_inbox", session: "worker", target: "inbox:worker"));
        // Another session reading at the same time is its own read.
        store.Record(Call(Now.AddSeconds(10), "check_inbox", session: "other", target: "inbox:other"));
        // A read after a full gap starts a new burst.
        store.Record(Call(Now.AddSeconds(50 + RelayLimits.ActivityTurnGapSeconds), "check_inbox", session: "worker", target: "inbox:worker"));
        await store.FlushActivityAsync();
        Assert.Equal(3, (await store.ReadActivityCountersAsync("repo", Now, Now.AddHours(1))).InboxReads);
    }

    private static ActivityEvent Call(DateTimeOffset at, string tool, string? session = null, string? role = null, bool ok = true, string? target = null, string? hash = null,
        string? state = null, string? offer = null, string? request = null, long? inputTokens = null, long? outputTokens = null) => new()
    {
        Timestamp = at, RepositoryId = "repo", SessionId = session, Role = role, Tool = tool, Ok = ok, Code = ok ? "Ok" : "Busy", RequestBytes = 10, ReplyBytes = 20,
        DurationMilliseconds = 1.5, Target = target, ContentHash = hash, WorkflowState = state, OfferTarget = offer, RequestId = request, InputTokens = inputTokens, OutputTokens = outputTokens
    };

    // ---- Search index (build step 5) ----

    private static readonly RelayCaller Internal = new("alice", true, ["repo", "other"], true);
    private static readonly System.Text.Json.JsonSerializerOptions EnvelopeJson = new() { IncludeFields = true };

    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string kind, string operation, System.Text.Json.Nodes.JsonObject args, string repository = "repo")
    {
        args["repositoryId"] = repository; args["requestId"] ??= Guid.NewGuid().ToString("N");
        var reply = CoordinationStateMachine.Apply(state, kind, "key", Internal, operation, args, Now);
        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
        return reply;
    }

    private static GrainId BoardGrain(string repository = "repo", string board = "default") => GrainId.Create("board", Uri.EscapeDataString(repository) + "/" + Uri.EscapeDataString(board));
    private static readonly GrainId SessionGrain = GrainId.Create("session", "s1");

    private static StateEnvelope<CoordinationDocument> Board(string repository = "repo", params (string Task, string Notes)[] tasks)
    {
        var board = new StateEnvelope<CoordinationDocument>();
        foreach (var (task, notes) in tasks) Apply(board, "board", "task_update", new() { ["taskId"] = task, ["expectedVersion"] = 0, ["notes"] = notes }, repository);
        return board;
    }

    private static StateEnvelope<CoordinationDocument> Inbox(string session, params (string Id, string Text)[] messages)
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Apply(state, "session", "__open", new() { ["sessionId"] = session, ["principalId"] = "alice", ["sessionToken"] = "secret" });
        foreach (var (id, text) in messages) Apply(state, "session", "__deliver", new() { ["messageId"] = id, ["text"] = text, ["sentAt"] = Now.ToString("O") });
        return state;
    }

    private static TestGrainState<StateEnvelope<CoordinationDocument>> Grain_(StateEnvelope<CoordinationDocument> state) => new() { State = state };

    private static async Task<string[]> Find(SqliteStore store, string query, string session = "s1", string repository = "repo", string[]? kinds = null, int limit = 10, string? status = null) =>
        (await store.SearchAsync(repository, session, query, kinds ?? [], limit, status)).Select(hit => hit.Kind + ":" + hit.DocId).ToArray();

    /// <summary>The indexed rows of one grain as "kind:doc_id|title|body|status", in key order.</summary>
    private static string[] IndexRows(string path, string grainKey)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, doc_id, title, body, status FROM search_index WHERE grain_key=$key ORDER BY kind, doc_id;";
        command.Parameters.AddWithValue("$key", grainKey);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add($"{reader.GetString(0)}:{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}|{reader.GetString(4)}");
        return rows.ToArray();
    }

    private static string[] Expected(string stateName, StateEnvelope<CoordinationDocument> state) =>
        state.GetSearchDocuments(stateName).OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.DocId, StringComparer.Ordinal).Select(x => $"{x.Kind}:{x.DocId}|{x.Title}|{x.Body}|{x.Status}").ToArray();

    [Fact]
    public async Task Version_1_database_upgrades_to_version_2_with_the_search_index_backfilled()
    {
        var board = Board("repo", ("T-1", "token refresh times out after idle"), ("T-2", "unrelated cleanup"));
        var inbox = Inbox("s1", ("m1", "the refresh fix is ready for review"));
        CreateVersion1Database("kept", ("board", BoardGrain(), board), ("session", SessionGrain, inbox));
        using (var store = new SqliteStore(Database))
        {
            Assert.Equal(new[] { "task:T-1", "message:m1" }.Order(), (await Find(store, "refresh")).Order());
            Assert.Equal(new[] { "task:T-1" }, await Find(store, "refresh", session: "s2"));
            Assert.Empty(await Find(store, "refresh", repository: "other"));
        }
        var backup = Assert.Single(Directory.GetFiles(_directory, "relay.db.pre-migration-*.db"));
        Assert.Equal(0L, Scalar(backup, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('search_docs', 'search_index');"));
        Assert.Equal(Expected("board", board), IndexRows(Database, BoardGrain().Key.ToString()));
        Assert.Equal(3L, Scalar(Database, "SELECT COUNT(*) FROM search_docs;"));
        Assert.Equal("1", Text(Database, "SELECT value FROM hub_metadata WHERE name='search_index_version';"));
    }

    [Fact]
    public async Task Version_2_database_without_a_built_index_is_backfilled_once_and_turning_search_off_empties_it()
    {
        var board = Board("repo", ("T-1", "token refresh times out after idle"));
        using (var store = new SqliteStore(Database, search: new SearchOptions { Enabled = false }))
        {
            await new SqliteGrainStorage(store).WriteStateAsync("board", BoardGrain(), Grain_(board));
            Assert.False(store.SearchEnabled);
            Assert.Empty(await Find(store, "refresh"));
        }
        Assert.Equal(0L, Scalar(Database, "SELECT COUNT(*) FROM search_index;"));
        // A database from build steps 1 to 4 is at version 2 with an empty search_index and no search_docs table.
        Execute(Database, "DROP TABLE search_docs;");
        using (var store = new SqliteStore(Database))
            Assert.Equal(new[] { "task:T-1" }, await Find(store, "refresh"));
        Assert.Empty(Directory.GetFiles(_directory, "*.pre-migration-*"));
        // Built once: a later start leaves the index alone (a marker edited by hand survives it).
        Execute(Database, "UPDATE search_docs SET marker='kept';");
        using (new SqliteStore(Database)) { }
        Assert.Equal("kept", Text(Database, "SELECT marker FROM search_docs;"));
        using (new SqliteStore(Database, search: new SearchOptions { Enabled = false })) { }
        Assert.Equal((0L, 0L, 0L), (Scalar(Database, "SELECT COUNT(*) FROM search_index;"), Scalar(Database, "SELECT COUNT(*) FROM search_docs;"), Scalar(Database, "SELECT COUNT(*) FROM hub_metadata WHERE name='search_index_version';")));
        using (var store = new SqliteStore(Database))
            Assert.Equal(new[] { "task:T-1" }, await Find(store, "refresh"));
        Assert.NotEqual("kept", Text(Database, "SELECT marker FROM search_docs;"));
    }

    [Fact]
    public async Task Index_matches_state_after_write_change_clear_conflict_and_a_commit_canceled_before_it_commits()
    {
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        var key = BoardGrain().Key.ToString();
        var state = Grain_(Board("repo", ("T-1", "token refresh times out"), ("T-2", "cleanup")));
        Apply(state.State, "board", "context_put", new() { ["contextId"] = "n1", ["expectedVersion"] = 0, ["content"] = "rig notes" });
        await provider.WriteStateAsync("board", BoardGrain(), state);
        Assert.Equal(Expected("board", state.State), IndexRows(Database, key));
        var rowids = Text(Database, "SELECT group_concat(kind || ':' || doc_id || '=' || id, ',') FROM (SELECT * FROM search_docs ORDER BY kind, doc_id);");

        // A change rewrites only its own document; the other rows keep their FTS rowids and markers.
        var stale = Grain_(new StateEnvelope<CoordinationDocument> { Value = new() { Json = state.State.Value.Json } }); stale.ETag = state.ETag;
        Apply(state.State, "board", "task_update", new() { ["taskId"] = "T-2", ["expectedVersion"] = 1, ["notes"] = "cleanup of the refresh cache" });
        var markers = Text(Database, "SELECT group_concat(marker, ',') FROM (SELECT marker FROM search_docs WHERE doc_id <> 'T-2' ORDER BY kind, doc_id);");
        await provider.WriteStateAsync("board", BoardGrain(), state);
        Assert.Equal(Expected("board", state.State), IndexRows(Database, key));
        Assert.Equal(rowids, Text(Database, "SELECT group_concat(kind || ':' || doc_id || '=' || id, ',') FROM (SELECT * FROM search_docs ORDER BY kind, doc_id);"));
        Assert.Equal(markers, Text(Database, "SELECT group_concat(marker, ',') FROM (SELECT marker FROM search_docs WHERE doc_id <> 'T-2' ORDER BY kind, doc_id);"));
        Assert.Equal(new[] { "task:T-1", "task:T-2" }.Order(), (await Find(store, "refresh")).Order());
        var committed = IndexRows(Database, key);

        // A stale etag is refused before anything is written.
        Apply(stale.State, "board", "task_update", new() { ["taskId"] = "T-1", ["expectedVersion"] = 1, ["notes"] = "conflicting text" });
        await Assert.ThrowsAsync<InconsistentStateException>(() => provider.WriteStateAsync("board", BoardGrain(), stale));
        Assert.Equal(committed, IndexRows(Database, key));

        // Canceled after the index rows were written but before Commit(): the transaction rolls them back with the state.
        using (var canceled = new CancellationTokenSource())
        {
            var canceling = new TestGrainState<CancelingState> { State = new CancelingState(state.State, canceled), ETag = state.ETag };
            Apply(state.State, "board", "task_update", new() { ["taskId"] = "T-1", ["expectedVersion"] = 1, ["notes"] = "never committed" });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.WriteStateAsync("board", BoardGrain(), canceling, canceled.Token));
            Assert.True(canceling.State.Wrote);
        }
        Assert.Equal(committed, IndexRows(Database, key));
        Assert.Empty(await Find(store, "committed"));

        await provider.ClearStateAsync("board", BoardGrain(), state);
        Assert.Empty(IndexRows(Database, key));
        Assert.Equal(0L, Scalar(Database, "SELECT COUNT(*) FROM search_docs;"));
        Assert.Empty(await Find(store, "refresh"));
    }

    [Fact]
    public async Task Outbox_only_commits_skip_the_index_and_a_new_state_repairs_it()
    {
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        var state = Grain_(Board("repo", ("T-1", "token refresh times out")));
        await provider.WriteStateAsync("board", BoardGrain(), state);
        Execute(Database, "UPDATE search_docs SET marker='stale';");
        // Same state document (as when the grain only removes a delivered effect): nothing is re-read or rewritten.
        state.State.DueWork.Add(new DueWorkItem("retry", "effect", Now));
        await provider.WriteStateAsync("board", BoardGrain(), state);
        Assert.Equal("stale", Text(Database, "SELECT marker FROM search_docs;"));
        // A new state document is compared with the stored markers, so the stale row is rebuilt.
        state.State.Value = new() { Json = new string(state.State.Value.Json.AsSpan()) };
        await provider.WriteStateAsync("board", BoardGrain(), state);
        Assert.NotEqual("stale", Text(Database, "SELECT marker FROM search_docs;"));
        Assert.Equal(Expected("board", state.State), IndexRows(Database, BoardGrain().Key.ToString()));
    }

    [Theory]
    [InlineData("title:secret refresh")]
    [InlineData("\"refresh")]
    [InlineData("NEAR(refresh secret, 2)")]
    [InlineData("refresh* OR secret AND NOT x")]
    [InlineData("{title body}: refresh ^secret -x +y")]
    [InlineData("refresh)) ((")]
    public async Task Search_syntax_in_a_query_is_read_as_plain_words(string query)
    {
        using var store = new SqliteStore(Database);
        var board = Board("repo", ("T-1", "token refresh times out"));
        Apply(board, "board", "task_update", new() { ["taskId"] = "T-2", ["expectedVersion"] = 0, ["patch"] = new System.Text.Json.Nodes.JsonObject { ["title"] = "secret rotation" } });
        await new SqliteGrainStorage(store).WriteStateAsync("board", BoardGrain(), Grain_(board));
        var hits = await Find(store, query);
        Assert.Contains("task:T-1", hits);
        Assert.Matches("^(\"[\\p{L}\\p{N}]+\")( OR \"[\\p{L}\\p{N}]+\")*$", SqliteStore.MatchExpression(query)!);
    }

    [Fact]
    public void Query_words_are_quoted_capped_and_deduplicated()
    {
        Assert.Equal("\"NEAR\" OR \"a\" OR \"b\"", SqliteStore.MatchExpression("NEAR(a b)"));
        Assert.Equal("\"title\" OR \"x\"", SqliteStore.MatchExpression("title:x TITLE"));
        Assert.Equal("\"été\" OR \"日本\"", SqliteStore.MatchExpression("été, 日本!"));
        Assert.Null(SqliteStore.MatchExpression(" \"*:()- "));
        Assert.Equal(RelayLimits.MaxSearchQueryTerms, SqliteStore.MatchExpression(string.Join(' ', Enumerable.Range(0, 40).Select(i => "w" + i)))!.Split(" OR ").Length);
    }

    [Fact]
    public async Task Search_is_scoped_filtered_ranked_and_trimmed()
    {
        using var store = new SqliteStore(Database);
        var provider = new SqliteGrainStorage(store);
        var board = Board("repo", ("strong", "alpha beta gamma alpha beta gamma"), ("weak", string.Join(' ', Enumerable.Repeat("filler words about other things", 60)) + " gamma"));
        Apply(board, "board", "task_update", new() { ["taskId"] = "done", ["expectedVersion"] = 0, ["notes"] = "alpha beta", ["workflow"] = new System.Text.Json.Nodes.JsonObject
        {
            ["state"] = "blocked", ["packet"] = new System.Text.Json.Nodes.JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "a", ["restoration"] = "r", ["stopCondition"] = "s" },
            ["criteria"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }),
            ["blockers"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "b", ["kind"] = "other", ["summary"] = "waiting", ["recheckCondition"] = "later" })
        } });
        Apply(board, "board", "context_put", new() { ["contextId"] = "n1", ["expectedVersion"] = 0, ["content"] = "alpha beta gamma notes" });
        await provider.WriteStateAsync("board", BoardGrain(), Grain_(board));
        await provider.WriteStateAsync("board", BoardGrain("repo", "side board"), Grain_(Board("repo", ("side", "alpha beta gamma on another board"))));
        await provider.WriteStateAsync("board", BoardGrain("other"), Grain_(Board("other", ("foreign", "alpha beta gamma elsewhere"))));
        await provider.WriteStateAsync("session", SessionGrain, Grain_(Inbox("s1", ("m1", "alpha beta gamma in a private message"))));

        var all = await Find(store, "alpha beta gamma");
        Assert.Contains("message:m1", all);
        Assert.Contains("task:strong", all);
        Assert.DoesNotContain("task:foreign", all);
        // A hit far weaker than the best one (one common word in a long body) is dropped.
        Assert.DoesNotContain("task:weak", all);
        Assert.Contains("task:weak", await Find(store, "filler"));
        Assert.DoesNotContain("message:m1", await Find(store, "alpha beta gamma", session: "s2"));
        Assert.Empty(await Find(store, "alpha", session: "s2", repository: "nobody"));
        Assert.Equal(new[] { "context:n1" }, await Find(store, "alpha", kinds: [SearchKinds.Context]));
        Assert.Equal(new[] { "message:m1" }, await Find(store, "alpha", kinds: [SearchKinds.Message, "unknown"]));
        Assert.Equal(new[] { "task:done" }, await Find(store, "alpha", status: "blocked"));
        Assert.Single(await Find(store, "alpha", limit: 1));
        Assert.Single(await Find(store, "alpha", limit: -5));
        Assert.True((await Find(store, "alpha", limit: 500)).Length <= RelayLimits.MaxSearchResults);
        Assert.Empty(await Find(store, "   "));
        var hits = await store.SearchAsync("repo", "s1", "another", [], 10);
        var side = Assert.Single(hits);
        Assert.Equal(("side", "side board", "open"), (side.DocId, side.BoardId, side.Status));
        Assert.Contains("[another]", side.Snippet);
        Assert.Null((await store.SearchAsync("repo", "s1", "strong", [], 10)).SingleOrDefault()?.BoardId);
        using var off = new SqliteStore(Path.Combine(_directory, "off.db"), search: new SearchOptions { Enabled = false });
        Assert.Empty(await Find(off, "alpha"));
    }

    [Fact]
    public async Task A_search_completes_while_a_long_write_holds_the_writer()
    {
        using var store = new SqliteStore(Database);
        await new SqliteGrainStorage(store).WriteStateAsync("board", BoardGrain(), Grain_(Board("repo", ("T-1", "token refresh times out"))));
        await store.WriterGate.WaitAsync();
        using var writer = Open(Database);
        using var transaction = writer.BeginTransaction(deferred: false);
        try
        {
            using (var write = writer.CreateCommand()) { write.Transaction = transaction; write.CommandText = "INSERT INTO hub_metadata(name, value) VALUES ('long-write', 'x');"; write.ExecuteNonQuery(); }
            var search = store.SearchAsync("repo", "s1", "refresh", [], 3);
            Assert.Same(search, await Task.WhenAny(search, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.Equal("T-1", Assert.Single(await search).DocId);
        }
        finally { transaction.Rollback(); store.WriterGate.Release(); }
    }

    [Fact]
    public async Task Silo_restart_returns_the_same_search_hits()
    {
        async Task<string[]> RunAsync(bool write)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            var siloPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var gateway = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); gateway.Start();
            var gatewayPort = ((System.Net.IPEndPoint)gateway.LocalEndpoint).Port;
            listener.Stop(); gateway.Stop();
            using var host = new HostBuilder()
                .ConfigureLogging(log => log.ClearProviders())
                .UseOrleans(silo => silo.UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "search-restart", clusterId: Guid.NewGuid().ToString("N")).AddPkRelaySqlite(Database))
                .ConfigureServices(services => services.AddPkRelayCore()).Build();
            await host.StartAsync();
            try
            {
                if (write)
                {
                    var application = host.Services.GetRequiredService<RelayApplication>();
                    async Task<System.Text.Json.Nodes.JsonObject> Call(string tool, System.Text.Json.Nodes.JsonObject args)
                    {
                        args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
                        var reply = await application.ExecuteAsync(Internal with { IsInternal = false }, tool, System.Text.Json.JsonSerializer.SerializeToElement(args));
                        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
                        return System.Text.Json.Nodes.JsonNode.Parse(reply.DataJson)!.AsObject();
                    }
                    var session = await Call("session_open", new());
                    System.Text.Json.Nodes.JsonObject For(System.Text.Json.Nodes.JsonObject args) { args["sessionId"] = session["sessionId"]!.DeepClone(); args["sessionToken"] = session["sessionToken"]!.DeepClone(); return args; }
                    await Call("task_update", For(new() { ["taskId"] = "T-1", ["expectedVersion"] = 0, ["notes"] = "token refresh times out" }));
                    await Call("task_update", For(new() { ["taskId"] = "T-2", ["expectedVersion"] = 0, ["notes"] = "refresh cache cleanup" }));
                    await Call("context_put", For(new() { ["contextId"] = "n1", ["expectedVersion"] = 0, ["content"] = "refresh runbook" }));
                }
                var search = host.Services.GetRequiredService<IFullTextSearch>();
                return (await search.SearchAsync("repo", "s1", "refresh", [], 10)).Select(hit => $"{hit.Kind}:{hit.DocId}:{hit.Snippet}").ToArray();
            }
            finally { await host.StopAsync(); }
        }

        var before = await RunAsync(write: true);
        Assert.Equal(3, before.Length);
        Assert.Equal(before, await RunAsync(write: false));
    }

    /// <summary>A board state whose search update cancels the commit's token after it was computed, to cancel between the index writes and Commit().</summary>
    public sealed class CancelingState : IHasSearchDocuments
    {
        private readonly StateEnvelope<CoordinationDocument> _inner = new();
        private readonly CancellationTokenSource? _cancel;
        public CancelingState() { }
        public CancelingState(StateEnvelope<CoordinationDocument> inner, CancellationTokenSource cancel) { _inner = inner; _cancel = cancel; Json = inner.Value.Json; }
        public string Json { get; set; } = "{}";
        [System.Text.Json.Serialization.JsonIgnore] public bool Wrote { get; private set; }
        public IReadOnlyList<SearchDocument> GetSearchDocuments(string stateName) => _inner.GetSearchDocuments(stateName);
        public SearchDelta GetSearchChanges(string stateName, IHasSearchDocuments? previous) => _inner.GetSearchChanges(stateName, previous);
        public SearchUpdate GetSearchUpdate(string stateName, IReadOnlyDictionary<SearchDocumentKey, string> indexed)
        {
            var update = _inner.GetSearchUpdate(stateName, indexed);
            Assert.NotEmpty(update.Upserts);
            Wrote = true;
            _cancel?.Cancel();
            return update;
        }
        public object? GetSearchSource() => null;
        public void ReleaseSearchState() { }
    }

    private void CreateVersion1Database(string text, params (string StateName, GrainId Grain, StateEnvelope<CoordinationDocument> State)[] rows)
    {
        var extraRows = string.Concat(rows.Select(row => $"INSERT INTO grain_state VALUES ('{row.StateName}', '{row.Grain.Type}', '{row.Grain.Key}', 1, 'etag-{row.StateName}', '{System.Text.Json.JsonSerializer.Serialize(row.State, EnvelopeJson).Replace("'", "''")}');\n"));
        var payload = System.Text.Json.JsonSerializer.Serialize(new StateEnvelope<Payload> { Value = new() { Text = text } }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
        Execute(Database, $"""
            CREATE TABLE grain_state (state_name TEXT NOT NULL, grain_type TEXT NOT NULL, grain_key TEXT NOT NULL, generation INTEGER NOT NULL, etag TEXT NOT NULL, payload TEXT, PRIMARY KEY (state_name, grain_type, grain_key));
            CREATE TABLE due_work (state_name TEXT NOT NULL, grain_type TEXT NOT NULL, grain_key TEXT NOT NULL, work_id TEXT NOT NULL, kind TEXT NOT NULL, due_ticks INTEGER NOT NULL,
                PRIMARY KEY (state_name, grain_type, grain_key, work_id), FOREIGN KEY (state_name, grain_type, grain_key) REFERENCES grain_state(state_name, grain_type, grain_key));
            CREATE INDEX ix_due_work_due ON due_work(due_ticks, state_name, grain_type, grain_key, work_id);
            CREATE TABLE hub_metadata (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO hub_metadata(name, value) VALUES ('service_id', 'version-one');
            INSERT INTO grain_state VALUES ('board', '{Grain.Type}', '{Grain.Key}', 1, 'etag-one', '{payload.Replace("'", "''")}');
            {extraRows}PRAGMA user_version=1;
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

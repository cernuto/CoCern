using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using CoCern.Analytics;
using Xunit;

namespace CoCern.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cocern-storage-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "relay.db");
    private static readonly GrainId Grain = GrainId.Create("test.board", "repository/default");
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public StorageTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Background_rollup_is_atomic_preserves_measurements_and_keeps_semantic_events()
    {
        using var store = new SqliteStore(Database, new ActivityOptions { Enabled = false });
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        void Run(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        long Count(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        void Seed(string session, DateTimeOffset at)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO activity_log(at_ticks,kind,repository_id,session_id,tool,ok,code,request_bytes,reply_bytes,duration_ms,
                    background_call,call_origin,request_tokens,reply_tokens)
                VALUES($at,'call','repo',$session,'get_status',1,'Ok',10,20,2,1,'wake-watcher',2,4);
                """;
            command.Parameters.AddWithValue("$at", at.UtcTicks);
            command.Parameters.AddWithValue("$session", session);
            command.ExecuteNonQuery();
        }
        var old = Now.AddDays(-3);
        Seed("quiet", old); Seed("quiet", old.AddMinutes(1));
        foreach (var session in new[] { "failed", "foreground", "unknown", "manager", "usage", "target", "offer", "budget" }) Seed(session, old);
        Seed("recent", Now.AddDays(-1));
        Seed("expired", Now.AddDays(-31));
        Run("""
            UPDATE activity_log SET ok=0,code='SessionDenied' WHERE session_id='failed';
            UPDATE activity_log SET background_call=0 WHERE session_id='foreground';
            UPDATE activity_log SET background_call=NULL WHERE session_id='unknown';
            UPDATE activity_log SET role='manager' WHERE session_id='manager';
            UPDATE activity_log SET input_tokens=9 WHERE session_id='usage';
            UPDATE activity_log SET target='inbox:first' WHERE session_id='target';
            UPDATE activity_log SET offer_target='task:T-1' WHERE session_id='offer';
            UPDATE activity_log SET kind='budget' WHERE session_id='budget';
            UPDATE activity_log SET tool='task_update',background_call=0 WHERE session_id='expired';
            CREATE TRIGGER refuse_origin_rollup BEFORE INSERT ON activity_origin_daily BEGIN SELECT RAISE(ABORT,'fixture failure'); END;
            """);

        // A failure between summary writes must preserve every raw row and roll back all preceding summaries.
        await Assert.ThrowsAsync<SqliteException>(() => store.RollUpActivityAsync(Now));
        Assert.Equal(12, Count("SELECT COUNT(*) FROM activity_log;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM activity_daily;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM activity_background_hourly;"));
        Run("DROP TRIGGER refuse_origin_rollup;");
        Assert.Equal(3, await store.RollUpActivityAsync(Now));
        Assert.Equal(9, Count("SELECT COUNT(*) FROM activity_log;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM activity_log WHERE session_id IN ('quiet','expired');"));
        Assert.Equal(3, Count("SELECT SUM(calls) FROM activity_daily;"));
        Assert.Equal(3, Count("SELECT SUM(calls) FROM activity_origin_daily;"));
        Assert.Equal(90, Count("SELECT SUM(request_bytes+reply_bytes) FROM activity_daily;"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM activity_background_hourly;"));
        Assert.Equal(2, Count("SELECT SUM(calls) FROM activity_background_hourly;"));
        Assert.Equal(12, Count("SELECT SUM(measured_tokens) FROM activity_background_hourly;"));
        Assert.Equal(2, Count("SELECT SUM(measured_calls) FROM activity_background_hourly;"));
        Assert.Equal(40, Count("SELECT SUM(reply_bytes) FROM activity_background_hourly;"));
        var counters = await store.ReadActivityCountersAsync("repo", old.AddHours(-1), Now);
        Assert.Equal(10, counters.Calls); // All retained calls, plus two quiet reads; the budget event is not a call.
        Assert.Equal(0, await store.RollUpActivityAsync(Now));
        Assert.Equal(3, Count("SELECT SUM(calls) FROM activity_daily;"));
        // Reconstruct the real DuckDB views from a scrubbed snapshot, so lost sample weights cannot hide behind correct SQLite sums.
        var snapshot = Path.Combine(_directory, "analytics.db");
        await store.CreateAnalyticsSnapshotAsync(snapshot);
        using (var analytics = new AnalyticsService(new AnalyticsOptions { LiveDatabasePath = Database, ExtensionDirectory = AnalyticsTestExtension.InstalledDirectory() }))
        {
            await analytics.SwitchSnapshotAsync(snapshot, Now);
            var tokens = await analytics.RunNamedAsync(NamedQueries.RelayTokens, "repo");
            var background = Assert.Single(tokens.Rows, row => Equals(row[0], "background"));
            Assert.Equal(48, Convert.ToInt64(background[1]));
            Assert.Equal(8, Convert.ToInt64(background[3]));
            var context = await analytics.RunNamedAsync(NamedQueries.ContextByRole, "repo");
            var worker = Assert.Single(context.Rows, row => Equals(row[0], ""));
            Assert.Equal(22.5, Convert.ToDouble(worker[1]));
            Assert.Equal(8, Convert.ToInt64(worker[3]));
            var compaction = await analytics.RunNamedAsync(AnalyticsQueryNames.CompactionRateByTool, "repo");
            Assert.Equal(10, Convert.ToInt64(Assert.Single(compaction.Rows)[3]));
        }
        await store.RollUpActivityAsync(Now.AddDays(31));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM activity_background_hourly;"));
    }

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
    public void Second_host_is_rejected_until_first_owner_disposes()
    {
        using (var first = new SqliteStore(Database))
            Assert.Throws<IOException>(() => new SqliteStore(Database));
        using var next = new SqliteStore(Database);
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

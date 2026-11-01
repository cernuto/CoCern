using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PkRelay.Analytics;
using PkRelay.Core;
using PkRelay.Host;
using PkRelay.Storage;
using Xunit;

namespace PkRelay.Tests;

public class AnalyticsTests
{
    [Fact]
    public async Task Snapshot_switch_while_a_query_runs_keeps_the_old_file_until_the_query_ends_then_removes_it()
    {
        var root = TempDirectory();
        using var store = new SqliteStore(Path.Combine(root, "relay.db"));
        var first = Path.Combine(root, "analytics", "snapshot-first.db");
        var second = Path.Combine(root, "analytics", "snapshot-second.db");
        await store.CreateAnalyticsSnapshotAsync(first);
        using var analytics = await Service(store, timeout: TimeSpan.FromSeconds(3));
        await analytics.SwitchSnapshotAsync(first, DateTimeOffset.UtcNow);
        // The SQLite copy is replaced by its DuckDB copy at once.
        var firstCopy = analytics.SnapshotPath!;
        Assert.Equal(Path.GetFullPath(first) + ".duckdb", firstCopy);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(firstCopy));

        // The query holds the first snapshot from the moment it is started.
        var running = analytics.RunSqlAsync("SELECT count(*) FROM range(1000000000000) t(a) WHERE a % 7 = 3");
        await store.CreateAnalyticsSnapshotAsync(second);
        var secondAt = DateTimeOffset.UtcNow;
        await analytics.SwitchSnapshotAsync(second, secondAt);
        Assert.False(running.IsCompleted);
        Assert.True(File.Exists(firstCopy));
        var secondCopy = analytics.SnapshotPath!;
        Assert.Equal(Path.GetFullPath(second) + ".duckdb", secondCopy);
        Assert.Equal(secondAt, (await analytics.RunSqlAsync("SELECT 1 AS one")).SnapshotAt);

        var timedOut = await Assert.ThrowsAsync<AnalyticsException>(() => running);
        Assert.Equal(AnalyticsError.Timeout, timedOut.Error);
        // The last query on the first snapshot has ended, so its file is gone; the current one stays.
        Assert.False(File.Exists(firstCopy));
        Assert.True(File.Exists(secondCopy));
        analytics.Dispose();
        Assert.False(File.Exists(secondCopy));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "analytics"), "snapshot-*"));
    }

    [Fact]
    public async Task Snapshots_are_scrubbed_copies_and_the_live_database_is_never_opened_by_DuckDB()
    {
        var root = TempDirectory();
        await using var host = new RelayHostFixture(databasePath: Path.Combine(root, "relay.db"), settings: await Enabled());
        using var client = host.AuthorizedClient();
        var session = await Open(host, client, "scrub", "worker");
        var analytics = host.Services.GetRequiredService<AnalyticsService>();
        var snapshot = await TakeSnapshot(host);
        Assert.StartsWith(Path.Combine(root, "analytics", "snapshot-"), snapshot);

        await Assert.ThrowsAsync<ArgumentException>(() => analytics.SwitchSnapshotAsync(host.DatabasePath));
        var attached = await analytics.RunSqlAsync("SELECT path FROM duckdb_databases() WHERE database_name = 'relay'");
        Assert.Equal(snapshot + ".duckdb", Assert.Single(attached.Rows)[0]);
        Assert.Equal(snapshot + ".duckdb", analytics.SnapshotPath);
        // Queries run without the sqlite extension, whose sqlite_scan could otherwise read the live file.
        Assert.Equal(0L, Single(await analytics.RunSqlAsync("SELECT count(*) FROM duckdb_functions() WHERE function_name LIKE 'sqlite%'")));

        // Session state (private inboxes, attention notes, bindings), control and discovery state, session tokens and idempotency
        // caches stay in the live database only.
        var token = session.GetProperty("sessionToken").GetString()!;
        Assert.True(Count(host.DatabasePath, "SELECT count(*) FROM grain_state WHERE state_name = 'control'") > 0);
        Assert.True(Count(host.DatabasePath, "SELECT count(*) FROM grain_state WHERE state_name = 'session'") > 0);
        Assert.True(Count(host.DatabasePath, $"SELECT count(*) FROM grain_state WHERE payload LIKE '%{token}%'") > 0);
        Assert.Equal(0L, Single(await analytics.RunSqlAsync("SELECT count(*) FROM relay.grain_state WHERE state_name = 'session'")));
        Assert.Equal(0L, Single(await analytics.RunSqlAsync("SELECT count(*) FROM relay.grain_state WHERE state_name NOT IN ('board', 'lock')")));
        Assert.Equal(0L, Single(await analytics.RunSqlAsync("SELECT count(*) FROM relay.due_work WHERE state_name NOT IN ('board', 'lock')")));
        Assert.Equal(0L, Single(await analytics.RunSqlAsync($"SELECT count(*) FROM relay.grain_state WHERE payload LIKE '%{token}%' OR payload LIKE '%requests%'")));
    }

    [Fact]
    public async Task A_snapshot_completes_while_a_long_write_holds_the_writer_and_holds_only_committed_state()
    {
        var root = TempDirectory();
        using var store = new SqliteStore(Path.Combine(root, "relay.db"));
        var snapshot = Path.Combine(root, "analytics", "snapshot-busy.db");
        await store.WriterGate.WaitAsync();
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
        writer.Open();
        using var transaction = writer.BeginTransaction(deferred: false);
        try
        {
            using (var write = writer.CreateCommand())
            {
                write.Transaction = transaction;
                write.CommandText = "INSERT INTO activity_log(at_ticks, kind, repository_id, ok, code, request_bytes, reply_bytes, duration_ms) VALUES (1, 'call', 'uncommitted', 1, 'Ok', 0, 0, 0);";
                write.ExecuteNonQuery();
            }
            var copy = store.CreateAnalyticsSnapshotAsync(snapshot);
            Assert.Same(copy, await Task.WhenAny(copy, Task.Delay(TimeSpan.FromSeconds(10))));
            await copy;
        }
        finally { transaction.Rollback(); store.WriterGate.Release(); }
        Assert.Equal(0L, Count(snapshot, "SELECT count(*) FROM activity_log WHERE repository_id = 'uncommitted'"));
        Assert.Equal(2L, Count(snapshot, "SELECT count(*) FROM pragma_table_list WHERE name IN ('grain_state', 'activity_log')"));
    }

    [Fact]
    public async Task Analytics_refuses_writes_attach_file_reads_and_setting_changes()
    {
        var root = TempDirectory();
        using var store = new SqliteStore(Path.Combine(root, "relay.db"));
        var snapshot = Path.Combine(root, "analytics", "snapshot-safety.db");
        await store.CreateAnalyticsSnapshotAsync(snapshot);
        using var analytics = await Service(store);
        await analytics.SwitchSnapshotAsync(snapshot);
        var live = AnalyticsService.Literal(store.DatabasePath);

        string[] refused =
        [
            "CREATE TABLE copied AS SELECT * FROM relay.grain_state", "INSERT INTO relay.grain_state VALUES ('a', 'b', 'c', 1, 'e', 'f')",
            "DELETE FROM relay.activity_log", "DROP VIEW tasks", "DETACH relay", $"ATTACH {live} AS live (TYPE sqlite)",
            "SET enable_external_access = true", "RESET lock_configuration", "PRAGMA database_list", "INSTALL httpfs", "LOAD httpfs",
            "COPY (SELECT 1) TO 'out.csv'", "SELECT 1; DETACH relay", "SELECT 1; SELECT 2"
        ];
        foreach (var sql in refused)
            Assert.Equal(AnalyticsError.Refused, (await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunSqlAsync(sql))).Error);

        // A SELECT that reads a file is refused by DuckDB itself: file access is off.
        foreach (var sql in new[] { $"SELECT * FROM read_text({live})", $"SELECT * FROM read_csv({live})", $"SELECT * FROM sqlite_scan({live}, 'grain_state')" })
        {
            var failed = await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunSqlAsync(sql));
            Assert.Equal(AnalyticsError.Failed, failed.Error);
        }

        // Even without the SELECT check, the locked settings refuse file access, attaching and setting changes.
        foreach (var sql in new[] { "SET enable_external_access = true", "SET lock_configuration = false", $"ATTACH {live} AS live (TYPE sqlite)", "LOAD httpfs", "COPY (SELECT 1) TO 'out.csv'" })
            await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunUncheckedAsync(sql));
        Assert.Equal("False", (await analytics.RunSqlAsync("SELECT current_setting('enable_external_access')::VARCHAR")).Rows[0][0]?.ToString(), ignoreCase: true);

        Assert.Equal(AnalyticsError.InvalidArguments, (await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunSqlAsync(new string('x', 4_097)))).Error);
        Assert.Equal(AnalyticsError.UnknownQuery, (await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunNamedAsync("drop_everything", "repo"))).Error);
        using var badDays = JsonDocument.Parse("""{"days":15}""");
        Assert.Equal(AnalyticsError.InvalidArguments, (await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunNamedAsync(NamedQueries.CallsPerTask, "repo", badDays.RootElement))).Error);
        using var unknown = JsonDocument.Parse("""{"sql":"SELECT 1"}""");
        Assert.Equal(AnalyticsError.InvalidArguments, (await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunNamedAsync(NamedQueries.CallsPerTask, "repo", unknown.RootElement))).Error);
    }

    [Fact]
    public async Task Analytics_enforces_the_timeout_and_the_row_cap()
    {
        var root = TempDirectory();
        using var store = new SqliteStore(Path.Combine(root, "relay.db"));
        var snapshot = Path.Combine(root, "analytics", "snapshot-limits.db");
        await store.CreateAnalyticsSnapshotAsync(snapshot);
        using var analytics = await Service(store, timeout: TimeSpan.FromMilliseconds(500));
        Assert.Equal(AnalyticsError.Unavailable, (await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunSqlAsync("SELECT 1"))).Error);
        await analytics.SwitchSnapshotAsync(snapshot);

        var capped = await analytics.RunSqlAsync("SELECT range AS n FROM range(100)");
        Assert.Equal(50, capped.Rows.Count);
        Assert.True(capped.Truncated);
        var exact = await analytics.RunSqlAsync("SELECT range AS n FROM range(50)");
        Assert.Equal(50, exact.Rows.Count);
        Assert.False(exact.Truncated);
        Assert.Equal(["n"], exact.Columns);
        Assert.Equal(49L, exact.Rows[^1][0]);

        var started = DateTimeOffset.UtcNow;
        var timedOut = await Assert.ThrowsAsync<AnalyticsException>(() => analytics.RunSqlAsync("SELECT count(*) FROM range(1000000000000) t(a) WHERE a % 7 = 3"));
        Assert.Equal(AnalyticsError.Timeout, timedOut.Error);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        // The interrupted connection does not poison the next query.
        Assert.Equal(1L, Single(await analytics.RunSqlAsync("SELECT 1")));
    }

    [Fact]
    public async Task Each_named_query_runs_against_a_fixture_snapshot_and_returns_the_expected_columns()
    {
        var settings = await Enabled();
        settings["Relay:Clients:0:IsAdministrator"] = "true";
        await using var host = new RelayHostFixture(settings: settings);
        using var client = host.AuthorizedClient();
        var store = host.Services.GetRequiredService<SqliteStore>();
        RecordPastActivity(store, DateTimeOffset.UtcNow);
        await RunTaskThroughRest(host, client);
        await TakeSnapshot(host);
        var analytics = host.Services.GetRequiredService<IAnalyticsQueries>();

        Assert.Equal(NamedQueries.Names, analytics.QueryNames);
        Assert.Contains(NamedQueries.AttentionOutcomesByKind, analytics.QueryNames);
        var results = new Dictionary<string, Dictionary<string, IReadOnlyList<object?>>>();
        foreach (var name in analytics.QueryNames)
        {
            var result = await analytics.RunNamedAsync(name, "past");
            Assert.Equal(NamedQueries.Columns, result.Columns);
            Assert.False(result.Truncated);
            results[name] = result.Rows.ToDictionary(row => (string)row[0]!, row => row);
            // Every query also runs, with the same columns, for a repository with board data and for one with none.
            Assert.Equal(NamedQueries.Columns, (await analytics.RunNamedAsync(name, "repo")).Columns);
            Assert.Equal(NamedQueries.Columns, (await analytics.RunNamedAsync(name, "nothing-here")).Columns);
        }

        // key -> cur, prev, cur_n, prev_n
        void Row(string query, string key, object? cur, object? prev, long curN, long prevN) =>
            Assert.Equal([key, cur, prev, curN, prevN], results[query][key]);
        Row(NamedQueries.TokensPerTask, "all", 1050.0, 330.0, 1, 1);
        Row(NamedQueries.CallsPerTask, "all", 5.0, 5.0, 1, 2);
        Row(NamedQueries.MessagesPerTask, "all", 0.0, 0.0, 1, 2);
        Row(NamedQueries.ContextByRole, "worker", 350.0, 1100.0, 2, 1);
        Row(NamedQueries.BlockedTime, "all", null, 3.0, 0, 1);
        // P-2 has been blocked since the previous period, so it is open at the end of both.
        Row(NamedQueries.BlockedTime, "open", 1.0, 1.0, 1, 1);
        Row(NamedQueries.ClaimConflictsByResource, "build-agent", 2L, 1L, 2, 1);
        Row(NamedQueries.ClaimConflictsByResource, "task:C-1", 1L, 0L, 1, 0);
        // A stale version (VersionConflict) is not a conflict, so C-1 counts once.
        Assert.Equal("build-agent", (await analytics.RunNamedAsync(NamedQueries.ClaimConflictsByResource, "past")).Rows[0][0]);
        Row(NamedQueries.NoisiestTools, "task_update", 200L, 900L, 2, 9);
        Row(NamedQueries.ManagerAssignmentLatency, "all", 20.0, 30.0, 1, 1);
        Row(NamedQueries.QualityGuardrails, "closures", 0L, 1L, 0, 1);
        Row(NamedQueries.QualityGuardrails, "reopened", 0L, 1L, 0, 1);
        Row(NamedQueries.QualityGuardrails, "failed_actions", 0L, 0L, 0, 0);
        Row(NamedQueries.AttentionOutcomesByKind, "blocked:shown", 2L, 0L, 3, 0);
        Row(NamedQueries.AttentionOutcomesByKind, "blocked:dismissed", 1L, 0L, 3, 0);
        Row(NamedQueries.AttentionOutcomesByKind, "review_ready:accepted", 0L, 1L, 0, 1);

        // With days = 1 the previous period (1 to 2 days ago) holds none of the older activity.
        using var oneDay = JsonDocument.Parse("""{"days":1}""");
        Assert.Equal(["all", 1050.0, null, 1L, 0L], (await analytics.RunNamedAsync(NamedQueries.TokensPerTask, "past", oneDay.RootElement)).Rows[0]);

        // Board-derived data from the task driven through REST.
        Assert.Equal(["all", 1.0, null, 1L, 0L], (await analytics.RunNamedAsync(NamedQueries.MessagesPerTask, "repo")).Rows[0]);
        var quality = (await analytics.RunNamedAsync(NamedQueries.QualityGuardrails, "repo")).Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal(1L, quality["closures"]);
        Assert.Equal(1L, quality["failed_actions"]);
        Assert.Equal(1L, quality["retried_actions"]);
        Assert.Equal(0L, quality["reopened"]);
        Assert.Equal(["all", 940.0, null, 1L, 0L], (await analytics.RunNamedAsync(NamedQueries.TokensPerTask, "repo")).Rows[0]);
        Assert.Equal(["bench", 1L, 0L, 1L, 0L], Assert.Single((await analytics.RunNamedAsync(NamedQueries.ClaimConflictsByResource, "repo")).Rows));

        var task = Assert.Single((await analytics.RunSqlAsync("SELECT task_id, state, claim_count, message_count, report_count, created IS NOT NULL, closed IS NOT NULL FROM tasks WHERE repository_id = 'repo'")).Rows);
        Assert.Equal(["T-1", "completed", 1L, 1L, 2L, true, true], task);
        Assert.Equal(1L, Single(await analytics.RunSqlAsync("SELECT count(*) FROM messages WHERE repository_id = 'repo' AND task_id = 'T-1' AND kind IS NOT NULL")));
        Assert.Equal(3L, Single(await analytics.RunSqlAsync("SELECT count(*) FROM claim_conflicts WHERE repository_id = 'past' AND tool = 'claim_acquire' AND resource_id = 'build-agent'")));
    }

    [Fact]
    public async Task Analytics_is_off_by_default_and_takes_a_snapshot_at_startup_when_enabled()
    {
        var root = TempDirectory();
        await using (var off = new RelayHostFixture(databasePath: Path.Combine(root, "off", "relay.db")))
        {
            using var client = off.AuthorizedClient();
            await Open(off, client, "off", "worker");
            Assert.Null(off.Services.GetService<IAnalyticsQueries>());
            Assert.False(Directory.Exists(Path.Combine(root, "off", "analytics")));
        }

        var stale = Path.Combine(root, "on", "analytics", "snapshot-stale.db");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "left by an earlier run");
        await using var on = new RelayHostFixture(databasePath: Path.Combine(root, "on", "relay.db"), settings: await Enabled());
        using var onClient = on.AuthorizedClient();
        var analytics = on.Services.GetRequiredService<AnalyticsService>();
        Assert.Same(analytics, on.Services.GetRequiredService<IAnalyticsQueries>());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (analytics.SnapshotAt is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        Assert.NotNull(analytics.SnapshotAt);
        Assert.StartsWith(Path.Combine(root, "on", "analytics", "snapshot-"), analytics.SnapshotPath);
        Assert.False(File.Exists(stale));
        Assert.Equal(NamedQueries.Columns, (await analytics.RunNamedAsync(NamedQueries.NoisiestTools, "repo")).Columns);
    }

    [Fact]
    public async Task Every_ad_hoc_query_is_logged_whether_it_runs_or_is_refused()
    {
        var caller = new PkRelay.Domain.RelayCaller("admin", true, ["repo"]);
        var queries = new FixedQueries();
        System.Text.Json.Nodes.JsonObject Args(string name, string? sql = null)
        {
            var args = new System.Text.Json.Nodes.JsonObject { ["repositoryId"] = "repo", ["name"] = name };
            if (sql is not null) args["sql"] = sql;
            return args;
        }

        var offLog = new ListLogger();
        var refused = await new AnalyticsTool(queries, allowAdHocSql: false, offLog).RunAsync(caller, Args("sql", "SELECT secret FROM somewhere"), CancellationToken.None);
        Assert.Equal("AnalyticsRefused", refused.Code);
        Assert.Contains(offLog.Lines, line => line.Contains("refused") && line.Contains("SELECT secret FROM somewhere") && line.Contains("admin"));
        Assert.Equal(0, queries.SqlCalls);

        var onLog = new ListLogger();
        var tool = new AnalyticsTool(queries, allowAdHocSql: true, onLog);
        Assert.True((await tool.RunAsync(caller, Args("sql", "SELECT 1"), CancellationToken.None)).Ok);
        Assert.Contains(onLog.Lines, line => line.Contains("SELECT 1") && line.Contains("admin"));
        Assert.Equal(1, queries.SqlCalls);
        var named = await tool.RunAsync(caller, Args(NamedQueries.NoisiestTools), CancellationToken.None);
        Assert.True(named.Ok);
        Assert.Single(onLog.Lines);
        Assert.Equal("{\"cols\":[\"key\",\"cur\"],\"rows\":[[\"all\",1.5]],\"at\":\"2026-11-01T10:00:00Z\"}", named.DataJson);
    }

    private sealed class FixedQueries : IAnalyticsQueries
    {
        public int SqlCalls;
        private static readonly AnalyticsResult Result = new(["key", "cur"], [["all", 1.5]], false, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        public IReadOnlyList<string> QueryNames => NamedQueries.Names;
        public Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<AnalyticsResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default) { SqlCalls++; return Task.FromResult(Result); }
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    [Fact]
    public void Plain_values_are_json_friendly()
    {
        Assert.Equal(5L, AnalyticsService.Plain(5));
        Assert.Equal(7L, AnalyticsService.Plain(new System.Numerics.BigInteger(7)));
        Assert.Equal("170141183460469231731687303715884105727", AnalyticsService.Plain(System.Numerics.BigInteger.Pow(2, 127) - 1));
        Assert.Equal(1.235, AnalyticsService.Plain(1.23456));
        Assert.Equal("2026-11-01T10:00:00.0000000Z", AnalyticsService.Plain(new DateTime(2026, 11, 1, 10, 0, 0)));
        Assert.Null(AnalyticsService.Plain(DBNull.Value));
        Assert.Equal("'it''s'", AnalyticsService.Literal("it's"));
    }

    /// <summary>
    /// Activity for repository "past" at fixed offsets from <paramref name="now"/>: task P-1 ten days ago (the previous period) and C-1
    /// three hours ago (the current one), with tokens, claims, refused claims and attention outcomes. P-2 is blocked ten days ago and
    /// never unblocked. Each row has 100 reply bytes.
    /// </summary>
    private static void RecordPastActivity(IActivitySink sink, DateTimeOffset now)
    {
        var old = now.AddDays(-10);
        var recent = now.AddHours(-3);
        void Call(DateTimeOffset at, string tool, string? target, string? workflow = null, bool ok = true, string code = "Ok", long? input = null, long? output = null, string? requestId = null, string session = "w-old") =>
            sink.Record(new ActivityEvent { Timestamp = at, Kind = ActivityEvent.Call, RepositoryId = "past", SessionId = session, Role = "worker", Tool = tool, Ok = ok, Code = code, ReplyBytes = 100, Target = target, WorkflowState = workflow, InputTokens = input, OutputTokens = output, RequestId = requestId });
        void Outcome(DateTimeOffset at, string kind, string outcome) =>
            sink.Record(new ActivityEvent { Timestamp = at, Kind = ActivityEvent.Attention, RepositoryId = "past", SessionId = "w-old", Tool = kind, Ok = true, Code = outcome, Target = "a-1" });
        const string P1 = "task:past/default/P-1", C1 = "task:past/default/C-1", Agent = "resource:past/build-agent";

        Call(old, "task_update", P1, "ready");
        Call(old.AddMinutes(30), "task_claim", P1);
        Call(old.AddHours(1), "task_update", P1, "blocked");
        Call(old.AddHours(2), "task_update", "task:past/default/P-2", "blocked");
        Call(old.AddHours(4), "task_update", P1, "ready");
        Call(old.AddHours(5), "task_update", P1, "completed");
        Call(old.AddHours(6), "task_update", P1, "ready");
        Call(old.AddHours(7), "task_update", P1, input: 100, output: 10, requestId: "r1");
        Call(old.AddHours(7).AddMinutes(1), "task_update", P1, input: 200, output: 20, requestId: "r2");
        Call(old.AddHours(7).AddMinutes(2), "task_update", P1, input: 200, output: 20, requestId: "r2");
        Call(old.AddHours(8), "claim_acquire", Agent, ok: false, code: "Busy");
        Outcome(old.AddHours(9), "review_ready", "accepted");

        Call(recent, "task_update", C1, "ready");
        Call(recent.AddMinutes(20), "task_claim", C1);
        Call(recent.AddMinutes(21), "task_update", C1, input: 1000, output: 50, requestId: "r3");
        Call(recent.AddMinutes(25), "task_claim", C1, ok: false, code: "Busy", session: "w-2");
        Call(recent.AddMinutes(26), "task_claim", C1, ok: false, code: "VersionConflict", session: "w-2");
        Call(recent.AddMinutes(30), "claim_acquire", Agent, ok: false, code: "Busy");
        Call(recent.AddMinutes(40), "claim_acquire", Agent, ok: false, code: "Busy");
        Outcome(recent.AddMinutes(50), "blocked", "shown");
        Outcome(recent.AddMinutes(51), "blocked", "shown");
        Outcome(recent.AddMinutes(52), "blocked", "dismissed");
    }

    /// <summary>
    /// Task T-1 in "repo": ready, claimed, a failed build and its retry, a message, then completed. Also one refused claim of the
    /// resource "bench", which the worker holds.
    /// </summary>
    private static async Task RunTaskThroughRest(RelayHostFixture host, HttpClient client)
    {
        var manager = await Open(host, client, "rest-manager", "manager");
        var worker = await Open(host, client, "rest-worker", "worker");
        object Claim(JsonElement who) => new { repositoryId = "repo", requestId = Guid.NewGuid().ToString("N"), sessionId = who.GetProperty("sessionId").GetString(), sessionToken = who.GetProperty("sessionToken").GetString(), resourceId = "bench" };
        Assert.True((await host.Call(client, "control_request", new { repositoryId = "repo", requestId = "register-bench", command = "register_resource", resourceId = "bench", machineId = "pc" })).GetProperty("ok").GetBoolean());
        Assert.True((await host.Call(client, "claim_acquire", Claim(worker))).GetProperty("ok").GetBoolean());
        using (var refused = await client.PostAsJsonAsync("/api/tools/claim_acquire", Claim(manager)))
            Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);
        var session = new { sessionId = worker.GetProperty("sessionId").GetString(), sessionToken = worker.GetProperty("sessionToken").GetString() };
        var version = 0L;
        async Task Step(string tool, object arguments)
        {
            var merged = JsonSerializer.SerializeToNode(arguments)!.AsObject();
            merged["repositoryId"] = "repo"; merged["requestId"] = Guid.NewGuid().ToString("N"); merged["sessionId"] = session.sessionId; merged["sessionToken"] = session.sessionToken;
            merged["taskId"] = "T-1"; merged["expectedVersion"] = version;
            using var response = await client.PostAsJsonAsync("/api/tools/" + tool, merged);
            var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(reply.GetProperty("ok").GetBoolean(), tool + ": " + reply);
            version = reply.GetProperty("version").GetInt64();
        }
        await Step("task_update", new { workflow = Workflow("ready"), usage = new { inputTokens = 900, outputTokens = 40 } });
        await Step("task_claim", new { resourceIds = Array.Empty<string>() });
        await Step("action_begin", new { actionId = "build", inputFingerprint = "abc" });
        await Step("action_report", new { actionId = "build", attempt = 1, outcome = "failed" });
        await Step("action_begin", new { actionId = "build", inputFingerprint = "abc", attempt = 2, explicitRetry = true });
        await Step("action_report", new { actionId = "build", attempt = 2, outcome = "completed" });
        await Step("task_update", new { message = new { text = "build passes now" } });
        await Step("task_update", new { workflow = Workflow("completed") });
    }

    private static object Workflow(string state) => new
    {
        state,
        packet = new { goal = "Make the build pass", target = "repo at revision abc", authorityRef = "human-grant:9", restoration = "None needed", stopCondition = "Stop on target mismatch" },
        criteria = new[] { state == "completed" ? (object)new { id = "build", description = "Build passes", result = "passed", evidenceRef = "build-log:9" } : new { id = "build", description = "Build passes", result = "not_run" } },
        blockers = Array.Empty<object>()
    };

    [Fact]
    public async Task The_installer_caps_the_decompressed_extension_size()
    {
        using var small = new MemoryStream(new byte[100]);
        using var copied = new MemoryStream();
        await AnalyticsExtension.CopyCappedAsync(small, copied, 100, CancellationToken.None);
        Assert.Equal(100, copied.Length);
        using var large = new MemoryStream(new byte[101]);
        await Assert.ThrowsAsync<InvalidDataException>(() => AnalyticsExtension.CopyCappedAsync(large, Stream.Null, 100, CancellationToken.None));
    }

    private static async Task<JsonElement> Open(RelayHostFixture host, HttpClient client, string requestId, string role)
    {
        var reply = await host.Call(client, "session_open", new { repositoryId = "repo", requestId, role });
        return JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!).RootElement.Clone();
    }

    /// <summary>Takes one snapshot now through the host's snapshot service and returns its path.</summary>
    private static async Task<string> TakeSnapshot(RelayHostFixture host)
    {
        var service = host.Services.GetServices<IHostedService>().OfType<AnalyticsSnapshotService>().Single();
        var path = await service.TakeSnapshotAsync(host.Services.GetRequiredService<AnalyticsService>(), CancellationToken.None);
        return Assert.IsType<string>(path);
    }

    private static async Task<Dictionary<string, string?>> Enabled() => new()
    {
        ["Relay:Analytics:Enabled"] = "true",
        // Long enough that only the startup snapshot and the ones a test takes exist.
        ["Relay:Analytics:SnapshotInterval"] = "1.00:00:00",
        ["Relay:Analytics:ExtensionDirectory"] = await AnalyticsTestExtension.DirectoryAsync()
    };

    private static async Task<AnalyticsService> Service(SqliteStore store, TimeSpan? timeout = null) => new(new AnalyticsOptions
    {
        ExtensionDirectory = await AnalyticsTestExtension.DirectoryAsync(),
        QueryTimeout = timeout ?? TimeSpan.FromSeconds(10),
        LiveDatabasePath = store.DatabasePath
    });

    private static object? Single(AnalyticsResult result) => Assert.Single(Assert.Single(result.Rows));

    private static long Count(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pkrelay-analytics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>
/// The DuckDB sqlite extension for tests: the folder in <c>PKRELAY_DUCKDB_EXTENSIONS</c>, or a per-machine cache in the temp folder.
/// It is downloaded once when missing; after that the tests need no network.
/// </summary>
internal static class AnalyticsTestExtension
{
    private static readonly Lazy<Task<string>> Ready = new(async () =>
    {
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("PKRELAY_DUCKDB_EXTENSIONS") is { Length: > 0 } configured ? configured : Path.Combine(Path.GetTempPath(), "pkrelay-duckdb-extensions"));
        if (!AnalyticsExtension.IsInstalled(directory)) await AnalyticsExtension.InstallAsync(directory);
        return directory;
    });

    public static Task<string> DirectoryAsync() => Ready.Value;
}

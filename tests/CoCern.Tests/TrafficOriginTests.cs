using System.Text.Json;
using CoCern.Adapter;
using CoCern.Analytics;
using CoCern.Core;
using CoCern.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public sealed class TrafficOriginTests
{
    [Fact]
    public async Task Model_reads_including_attention_opt_out_are_classified_and_legacy_calls_remain_unknown()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var forwarder = new RelayForwarder(client, new AdapterOptions(client.BaseAddress!.AbsoluteUri, RelayHostFixture.Token));
        // Use the fixture's authenticated transport; the adapter's token is the same fixture credential.
        var calls = new ChatCalls(forwarder, null, null, "origin-test");
        Assert.True((await calls.CallAsync("task_get", JsonSerializer.SerializeToElement(new { repositoryId = "repo" }), CancellationToken.None)).Ok);
        Assert.True((await calls.CallAsync("task_get", JsonSerializer.SerializeToElement(new { repositoryId = "repo", attention = false, backgroundCall = true }), CancellationToken.None)).Ok);
        await host.Call(client, "task_get", new { repositoryId = "repo" });
        await host.Call(client, "task_get", new { repositoryId = "repo", backgroundCall = true });
        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        using var db = Open(store.DatabasePath);
        Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM pragma_table_info('activity_log') WHERE name = 'background_call'"));
        Assert.Equal("0,0,unknown,1", Scalar(db, "SELECT group_concat(coalesce(CAST(background_call AS TEXT), 'unknown')) FROM (SELECT background_call FROM activity_log WHERE tool = 'task_get' ORDER BY id)"));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM grain_state WHERE payload LIKE '%backgroundCall%'"));
    }

    [Fact]
    public async Task Old_database_migration_and_old_snapshot_keep_unrecorded_origin_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "relay.db");
        using (var original = new SqliteStore(path))
        {
            original.Record(Event(null, DateTimeOffset.UtcNow, 7));
            await original.FlushActivityAsync();
        }
        using (var db = Open(path))
            Scalar(db, "INSERT INTO activity_daily(day, repository_id, kind, role, tool, calls, errors, request_bytes, reply_bytes, duration_ms, input_tokens, output_tokens) VALUES (date('now', '-2 day'), 'repo', 'call', '', 'task_get', 4, 0, 26, 13, 0, 0, 0)");
        RemoveOriginColumns(path);
        using (var migrated = new SqliteStore(path))
        using (var db = Open(path))
        {
            Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM pragma_table_info('activity_log') WHERE name = 'background_call'"));
            Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM activity_log WHERE background_call IS NULL"));
            await migrated.CreateAnalyticsSnapshotAsync(Path.Combine(root, "old.db"));
        }
        RemoveOriginColumns(Path.Combine(root, "old.db"));
        using var analytics = new AnalyticsService(new AnalyticsOptions { ExtensionDirectory = await AnalyticsTestExtension.DirectoryAsync() });
        await analytics.SwitchSnapshotAsync(Path.Combine(root, "old.db"));
        var result = await analytics.RunNamedAsync(NamedQueries.NoisiestTools, "repo");
        Assert.Equal(new object?[] { "task_get [unknown]", 20L, null, 5L, 0L }, Assert.Single(result.Rows));
        Assert.Equal(1L, (await analytics.RunSqlAsync("SELECT count(*) FROM tool_calls WHERE background_call IS NULL")).Rows[0][0]);
    }

    [Fact]
    public async Task Raw_and_daily_origin_categories_preserve_all_calls_and_bytes_across_repeated_rollups()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new SqliteStore(Path.Combine(root, "relay.db"), new ActivityOptions { RetentionDays = 1 });
        var now = DateTimeOffset.UtcNow;
        foreach (var (origin, bytes) in new (bool?, int)[] { (false, 10), (true, 20), (null, 30) })
            store.Record(Event(origin, now.AddDays(-2), bytes));
        await store.FlushActivityAsync();
        Assert.Equal(3, await store.RollUpActivityAsync(now));
        store.Record(Event(false, now.AddDays(-2), 5));
        store.Record(Event(true, now.AddMinutes(-1), 40));
        await store.FlushActivityAsync();
        Assert.Equal(1, await store.RollUpActivityAsync(now));
        using (var db = Open(store.DatabasePath))
        {
            Assert.Equal(4L, Scalar(db, "SELECT sum(calls) FROM activity_daily"));
            Assert.Equal(65L, Scalar(db, "SELECT sum(reply_bytes) FROM activity_daily"));
            Assert.Equal(130L, Scalar(db, "SELECT sum(request_bytes) FROM activity_daily"));
        }
        var snapshot = Path.Combine(root, "snapshot.db");
        await store.CreateAnalyticsSnapshotAsync(snapshot);
        using var analytics = new AnalyticsService(new AnalyticsOptions { ExtensionDirectory = await AnalyticsTestExtension.DirectoryAsync() });
        await analytics.SwitchSnapshotAsync(snapshot, now);
        var rows = (await analytics.RunNamedAsync(NamedQueries.NoisiestTools, "repo")).Rows.ToDictionary(row => (string)row[0]!);
        Assert.Equal(new object?[] { "task_get [model-facing]", 15L, null, 2L, 0L }, rows["task_get [model-facing]"]);
        Assert.Equal(new object?[] { "task_get [background]", 60L, null, 2L, 0L }, rows["task_get [background]"]);
        Assert.Equal(new object?[] { "task_get [unknown]", 30L, null, 1L, 0L }, rows["task_get [unknown]"]);
        Assert.Equal(105L, rows.Values.Sum(row => Convert.ToInt64(row[1])));
        Assert.Equal(5L, rows.Values.Sum(row => Convert.ToInt64(row[3])));
        Assert.Equal(210L, (await analytics.RunSqlAsync("SELECT sum(request_bytes) FROM tool_traffic WHERE repository_id = 'repo'")).Rows[0][0]);
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("claude-stop")]
    [InlineData("claude-waiter")]
    [InlineData("wake-watcher")]
    [InlineData("node")]
    [InlineData("dashboard")]
    public async Task Explicit_call_origin_is_recorded_without_affecting_classification_or_entering_grain_state(string origin)
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var reply = await host.Call(client, "task_get", new { repositoryId = "repo", backgroundCall = true, callOrigin = origin });
        Assert.True(reply.GetProperty("ok").GetBoolean());
        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        using var db = Open(store.DatabasePath);
        Assert.Equal(origin, Scalar(db, "SELECT call_origin FROM activity_log WHERE kind = 'call' AND tool = 'task_get'"));
        Assert.Equal(1L, Scalar(db, "SELECT background_call FROM activity_log WHERE kind = 'call' AND tool = 'task_get'"));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM grain_state WHERE payload LIKE '%callOrigin%'"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("future-provider")]
    [InlineData("MCP")]
    [InlineData(42)]
    public async Task Missing_or_unrecognized_origin_stays_unknown_without_rejecting_the_call(object? origin)
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var reply = await host.Call(client, "task_get", new { repositoryId = "repo", callOrigin = origin });
        Assert.True(reply.GetProperty("ok").GetBoolean());
        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        using var db = Open(store.DatabasePath);
        Assert.Equal("unknown", Scalar(db, "SELECT coalesce(call_origin, 'unknown') FROM activity_log WHERE kind = 'call' AND tool = 'task_get'"));
        Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM activity_log WHERE kind = 'call' AND background_call IS NULL"));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM grain_state WHERE payload LIKE '%callOrigin%'"));
    }

    [Fact]
    public async Task Explicit_origin_counts_survive_repeated_rollups_without_changing_existing_traffic_totals()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-call-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new SqliteStore(Path.Combine(root, "relay.db"), new ActivityOptions { RetentionDays = 1 });
        var now = DateTimeOffset.UtcNow;
        ActivityEvent Report(string? origin, DateTimeOffset at, int bytes)
        {
            var json = JsonSerializer.SerializeToNode(Event(null, at, bytes))!.AsObject();
            json["CallOrigin"] = origin;
            return json.Deserialize<ActivityEvent>()!;
        }
        store.Record(Report("claude-stop", now.AddDays(-2), 10));
        store.Record(Report("mcp", now.AddDays(-2), 20));
        store.Record(Report(null, now.AddDays(-2), 30));
        await store.FlushActivityAsync();
        Assert.Equal(3, await store.RollUpActivityAsync(now));
        store.Record(Report("claude-stop", now.AddDays(-2), 5));
        store.Record(Report("node", now.AddMinutes(-1), 40));
        await store.FlushActivityAsync();
        Assert.Equal(1, await store.RollUpActivityAsync(now));
        using (var db = Open(store.DatabasePath))
        {
            Assert.Equal(4L, Scalar(db, "SELECT sum(calls) FROM activity_daily"));
            Assert.Equal(65L, Scalar(db, "SELECT sum(reply_bytes) FROM activity_daily"));
            Assert.Equal(130L, Scalar(db, "SELECT sum(request_bytes) FROM activity_daily"));
            Assert.Equal(4L, Scalar(db, "SELECT sum(calls) FROM activity_origin_daily"));
            Assert.Equal(65L, Scalar(db, "SELECT sum(reply_bytes) FROM activity_origin_daily"));
            Assert.Equal(0L, Scalar(db, "SELECT sum(model_calls + background_calls) FROM activity_daily"));
        }
        var snapshot = Path.Combine(root, "snapshot.db");
        await store.CreateAnalyticsSnapshotAsync(snapshot);
        using var analytics = new AnalyticsService(new AnalyticsOptions { ExtensionDirectory = await AnalyticsTestExtension.DirectoryAsync() });
        await analytics.SwitchSnapshotAsync(snapshot, now);
        var byOrigin = (await analytics.RunSqlAsync("SELECT call_origin, sum(calls), sum(request_bytes), sum(reply_bytes) FROM tool_origin_traffic WHERE repository_id = 'repo' GROUP BY call_origin ORDER BY call_origin")).Rows;
        Assert.Equal(4, byOrigin.Count);
        Assert.Equal(new object?[] { "claude-stop", 2L, 30L, 15L }, byOrigin[0]);
        Assert.Equal(new object?[] { "mcp", 1L, 40L, 20L }, byOrigin[1]);
        Assert.Equal(new object?[] { "node", 1L, 80L, 40L }, byOrigin[2]);
        Assert.Equal(new object?[] { "unknown", 1L, 60L, 30L }, byOrigin[3]);
        Assert.Equal(105L, (await analytics.RunSqlAsync("SELECT sum(reply_bytes) FROM tool_traffic WHERE repository_id = 'repo'")).Rows[0][0]);
        Assert.Equal(5L, (await analytics.RunSqlAsync("SELECT sum(calls) FROM tool_traffic WHERE repository_id = 'repo'")).Rows[0][0]);
    }

    [Fact]
    public async Task Historical_snapshots_without_origin_column_or_companion_keep_all_unattributed_totals()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-legacy-call-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "relay.db");
        using (var store = new SqliteStore(path))
        {
            store.Record(Event(null, DateTimeOffset.UtcNow, 7));
            await store.FlushActivityAsync();
            using (var db = Open(path))
                Scalar(db, "INSERT INTO activity_daily(day, repository_id, kind, role, tool, calls, errors, request_bytes, reply_bytes, duration_ms, input_tokens, output_tokens) VALUES (date('now', '-2 day'), 'repo', 'call', '', 'task_get', 4, 0, 26, 13, 0, 0, 0)");
            await store.CreateAnalyticsSnapshotAsync(Path.Combine(root, "old.db"));
        }
        using (var db = Open(Path.Combine(root, "old.db")))
        {
            if (Convert.ToInt64(Scalar(db, "SELECT count(*) FROM pragma_table_info('activity_log') WHERE name = 'call_origin'")) != 0)
                Scalar(db, "ALTER TABLE activity_log DROP COLUMN call_origin");
            Scalar(db, "DROP TABLE IF EXISTS activity_origin_daily");
        }
        using var analytics = new AnalyticsService(new AnalyticsOptions { ExtensionDirectory = await AnalyticsTestExtension.DirectoryAsync() });
        await analytics.SwitchSnapshotAsync(Path.Combine(root, "old.db"));
        var rows = (await analytics.RunSqlAsync("SELECT call_origin, sum(calls), sum(request_bytes), sum(reply_bytes) FROM tool_origin_traffic WHERE repository_id = 'repo' GROUP BY call_origin")).Rows;
        Assert.Equal(new object?[] { "unknown", 5L, 40L, 20L }, Assert.Single(rows));
    }

    private static ActivityEvent Event(bool? background, DateTimeOffset at, int bytes) => JsonSerializer.Deserialize<ActivityEvent>(JsonSerializer.Serialize(new
    {
        Timestamp = at, Kind = "call", RepositoryId = "repo", Tool = "task_get", Ok = true, Code = "Ok", BackgroundCall = background, RequestBytes = bytes * 2, ReplyBytes = bytes
    }))!;

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void RemoveOriginColumns(string path)
    {
        using var db = Open(path);
        foreach (var (table, column) in new[] { ("activity_log", "background_call"), ("activity_daily", "model_calls"), ("activity_daily", "background_calls"), ("activity_daily", "model_request_bytes"), ("activity_daily", "background_request_bytes"), ("activity_daily", "model_reply_bytes"), ("activity_daily", "background_reply_bytes") })
            if (Convert.ToInt64(Scalar(db, $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = '{column}'")) > 0)
                Scalar(db, $"ALTER TABLE {table} DROP COLUMN {column}");
    }
}

using System.Text.Json;
using CoCern.Analytics;
using CoCern.Contracts;
using DuckDB.NET.Data;
using Xunit;

namespace CoCern.Tests;

/// <summary>Fixed-cutoff SQL fixtures; no live hub, extension download, or shared output is needed.</summary>
public sealed class AnalyticsCorrectnessTests
{
    private static readonly long Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero).Ticks;
    private const long Day = TimeSpan.TicksPerDay;

    [Theory]
    [InlineData(-15, -13, null, null, 0, 0)]
    [InlineData(-8, -6, null, 100.0, 0, 1)]
    [InlineData(-14, -13, null, 100.0, 0, 1)]
    [InlineData(-7, -6, 100.0, null, 1, 0)]
    [InlineData(-1, 0, 100.0, null, 1, 0)]
    public void Usage_retry_identity_is_chosen_before_both_comparison_boundaries(int originalDay, int retryDay, double? cur, double? prev, long curN, long prevN)
    {
        using var f = new Fixture();
        f.Call(Now + originalDay * Day, request: "same", input: 100);
        f.Call(Now + retryDay * Day, request: "same", input: 100);
        Assert.Equal(new object?[] { "all", cur, prev, curN, prevN }, f.Named(NamedQueries.TokensPerTask).Single());
    }

    [Fact]
    public void Retry_with_usage_uses_original_successful_call_time_even_if_original_had_no_usage()
    {
        using var f = new Fixture();
        f.Call(Now - 15 * Day, request: "same");
        f.Call(Now - Day, request: "same", input: 100);
        Assert.Equal(new object?[] { "all", null, null, 0L, 0L }, f.Named(NamedQueries.TokensPerTask).Single());
    }

    [Fact]
    public void Missing_request_ids_are_distinct_from_each_other_and_literal_numeric_request_ids()
    {
        using var f = new Fixture();
        f.Call(Now - Day, input: 10); // id 1
        f.Call(Now - Day, input: 20);
        f.Call(Now - Day, request: "1", input: 30);
        f.Call(Now - Day, request: "1", input: 30);
        Assert.Equal(new object?[] { "all", 60.0, null, 1L, 0L }, f.Named(NamedQueries.TokensPerTask).Single());
    }

    [Fact]
    public void Every_registered_report_retains_the_five_column_shape_for_an_empty_repository_and_board()
    {
        using var f = new Fixture();
        foreach (var name in NamedQueries.Names) f.Named(name);
        f.Board("blocked");
        foreach (var name in NamedQueries.Names) f.Named(name);
        var open = f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "open"));
        Assert.Equal(1.0, open[1]);
        Assert.Null(open[2]);
        Assert.Null(f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "all"))[1]);
    }

    [Fact]
    public void Usage_coverage_places_a_retry_in_its_canonical_period_and_ignores_other_repositories()
    {
        using var f = new Fixture();
        f.Call(Now - 8 * Day, request: "same", input: 100);
        f.Call(Now - Day, request: "same", input: 100);
        f.Call(Now - 15 * Day, request: "outside", task: "outside", input: 100);
        f.Call(Now - 8 * Day, request: "outside", task: "outside", input: 100);
        f.Call(Now - Day, repository: "other", input: 100);
        Assert.Equal(new object?[] { "all", 0.0, 50.0, 1L, 2L }, f.Named(AnalyticsQueryNames.UsageCoverage).Single());
    }

    [Fact]
    public void Conflict_attribution_survives_packet_edits_without_fanout_or_retroactive_resource_names()
    {
        using var f = new Fixture();
        f.Board("blocked", "A");
        f.Call(Now - Day, tool: "task_claim", ok: false, code: "Busy");
        f.Call(Now - Day, tool: "claim_acquire", target: "resource:repo/A", ok: false, code: "OwnerConflict");
        f.Call(Now - Day, tool: "task_claim", ok: false, code: "VersionConflict");
        var before = f.Named(NamedQueries.ClaimConflictsByResource);
        Assert.Equal(new[] { "A", "task:T" }, before.Select(r => (string)r[0]!).Order().ToArray());
        f.Board("blocked", "B", "C");
        var after = f.Named(NamedQueries.ClaimConflictsByResource);
        Assert.Equal(before.Select(r => JsonSerializer.Serialize(r)), after.Select(r => JsonSerializer.Serialize(r)));
        Assert.All(after, r => Assert.Equal(1L, r[1]));
    }

    [Fact]
    public void Retained_current_board_preserves_blocked_count_after_31_day_start_is_rolled_up()
    {
        using var f = new Fixture();
        f.Board("blocked");
        f.Call(Now - 31 * Day, workflow: "blocked");
        Assert.Equal(1.0, f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "open"))[1]);
        f.RollupOldCalls();
        var open = f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "open"));
        Assert.Equal(1.0, open[1]);
        Assert.Null(open[2]); // Prior cutoff cannot be reconstructed from today's board.
        Assert.Equal(true, f.Sql("SELECT history_incomplete FROM workflow_history_coverage WHERE repository_id = 'repo'").Single()[0]);
        f.Call(Now - Day, workflow: "ready");
        f.Board("ready");
        Assert.Equal(new object?[] { "all", null, null, 0L, 0L }, f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "all")));
        Assert.Equal(0.0, f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "open"))[1]);
    }

    [Fact]
    public void Lost_ready_start_does_not_invent_claim_latency_and_observed_new_transition_still_measures()
    {
        using var f = new Fixture();
        f.Board("ready");
        f.Call(Now - 31 * Day, workflow: "ready");
        f.RollupOldCalls();
        f.Call(Now - Day, tool: "task_claim");
        Assert.Equal(new object?[] { "all", null, null, 0L, 0L }, f.Named(NamedQueries.ManagerAssignmentLatency).Single());
        // A repeated ready write after retention is not evidence of a new ready spell.
        f.Call(Now - Day / 2, workflow: "ready");
        f.Call(Now - Day / 2 + TimeSpan.TicksPerMinute * 5, tool: "task_claim");
        Assert.Null(f.Named(NamedQueries.ManagerAssignmentLatency).Single()[1]);
        f.Call(Now - Day / 4, workflow: "blocked");
        f.Call(Now - Day / 4 + TimeSpan.TicksPerMinute, workflow: "ready");
        f.Call(Now - Day / 4 + TimeSpan.TicksPerMinute * 6, tool: "task_claim");
        Assert.Equal(new object?[] { "all", 5.0, null, 1L, 0L }, f.Named(NamedQueries.ManagerAssignmentLatency).Single());
    }

    [Fact]
    public void Partial_blocked_start_is_not_a_duration_but_later_observed_spell_is()
    {
        using var f = new Fixture();
        f.Call(Now - 31 * Day, workflow: "blocked");
        f.RollupOldCalls();
        f.Call(Now - Day, workflow: "blocked");
        f.Call(Now - Day + TimeSpan.TicksPerHour, workflow: "ready");
        Assert.Null(f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "all"))[1]);
        f.Call(Now - Day / 2, workflow: "blocked");
        f.Call(Now - Day / 2 + TimeSpan.TicksPerHour * 2, workflow: "ready");
        Assert.Equal(2.0, f.Named(NamedQueries.BlockedTime).Single(r => Equals(r[0], "all"))[1]);
    }

    [Fact]
    public void Relay_totals_use_complete_measurements_split_by_origin_and_never_reported_model_tokens()
    {
        using var f = new Fixture();
        f.Call(Now - Day, requestTokens: 10, replyTokens: 20, background: false, input: 999999);
        f.Call(Now - Day, requestTokens: 4, replyTokens: 6, background: true);
        f.Call(Now - 8 * Day, requestTokens: 2, replyTokens: 3);
        f.Call(Now - Day, requestTokens: 900); // Incomplete measurement is excluded.
        f.Call(Now - Day, replyTokens: 900);
        var rows = f.Named(AnalyticsQueryNames.RelayTokens).ToDictionary(r => (string)r[0]!);
        Assert.Equal(new object?[] { "model-facing", 30L, null, 1L, 0L }, rows["model-facing"]);
        Assert.Equal(new object?[] { "background", 10L, null, 1L, 0L }, rows["background"]);
        Assert.Equal(new object?[] { "unknown", null, 5L, 0L, 1L }, rows["unknown"]);
    }

    [Fact]
    public void Usage_coverage_is_percent_of_touched_tasks_and_empty_denominator_is_unknown()
    {
        using var f = new Fixture();
        f.Call(Now - Day, input: 0, request: "r"); // Reported zero is measured.
        f.Call(Now - Day, input: 0, request: "r");
        f.Call(Now - Day, task: "unreported", requestTokens: 500, replyTokens: 500);
        f.Call(Now - Day, task: "refused", ok: false, input: 700);
        f.Call(Now - Day, task: "other", repository: "other", input: 100);
        Assert.Equal(new object?[] { "all", 100.0 / 3, null, 3L, 0L }, f.Named(AnalyticsQueryNames.UsageCoverage).Single());
        using var empty = new Fixture();
        Assert.Equal(new object?[] { "all", null, null, 0L, 0L }, empty.Named(AnalyticsQueryNames.UsageCoverage).Single());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DuckDBConnection connection = new("DataSource=:memory:");
        private long id;

        public Fixture()
        {
            connection.Open();
            Execute("""
                CREATE SCHEMA relay;
                CREATE TABLE relay.grain_state(grain_key VARCHAR, state_name VARCHAR, payload VARCHAR);
                CREATE TABLE relay.task_archive(repository_id VARCHAR, board_id VARCHAR, task_id VARCHAR, completed_ticks BIGINT, archived_ticks BIGINT, json VARCHAR);
                CREATE TABLE relay.activity_log(id BIGINT, at_ticks BIGINT, kind VARCHAR, repository_id VARCHAR, session_id VARCHAR, role VARCHAR, tool VARCHAR, ok INTEGER, code VARCHAR,
                    request_bytes BIGINT, reply_bytes BIGINT, duration_ms DOUBLE, input_tokens BIGINT, output_tokens BIGINT, request_id VARCHAR, target VARCHAR, workflow_state VARCHAR,
                    offer_target VARCHAR, request_tokens BIGINT, reply_tokens BIGINT, background_call INTEGER, call_origin VARCHAR);
                CREATE TABLE relay.activity_daily(repository_id VARCHAR, day VARCHAR, tool VARCHAR, kind VARCHAR, calls BIGINT, request_bytes BIGINT, reply_bytes BIGINT,
                    model_calls BIGINT, model_request_bytes BIGINT, model_reply_bytes BIGINT, background_calls BIGINT, background_request_bytes BIGINT, background_reply_bytes BIGINT);
                CREATE TABLE relay.activity_origin_daily(day VARCHAR, repository_id VARCHAR, tool VARCHAR, call_origin VARCHAR, calls BIGINT, request_bytes BIGINT, reply_bytes BIGINT);
                """);
            using var resource = typeof(NamedQueries).Assembly.GetManifestResourceStream("CoCern.Analytics.Views.sql")!;
            using var reader = new StreamReader(resource);
            Execute(reader.ReadToEnd());
        }

        public void Call(long at, string? request = null, long? input = null, string tool = "task_update", bool ok = true, string code = "Ok", string? workflow = null,
            string task = "T", string repository = "repo", string? target = null, long? requestTokens = null, long? replyTokens = null, bool? background = null)
        {
            Execute("""
                INSERT INTO relay.activity_log(id, at_ticks, kind, repository_id, session_id, role, tool, ok, code,
                    request_bytes, reply_bytes, duration_ms, input_tokens, output_tokens, request_id, target, workflow_state,
                    offer_target, request_tokens, reply_tokens, background_call)
                VALUES ($id, $at, 'call', $repository, 'session', 'worker', $tool, $ok, $code,
                    0, 0, 0, $input, NULL, $request, $target, $workflow, NULL, $requestTokens, $replyTokens, $background)
                """, ("id", ++id), ("at", at), ("repository", repository), ("tool", tool), ("ok", ok ? 1 : 0), ("code", code),
                ("input", input), ("request", request), ("target", target ?? $"task:{repository}/default/{task}"), ("workflow", workflow),
                ("requestTokens", requestTokens), ("replyTokens", replyTokens), ("background", background is null ? null : background.Value ? 1 : 0));
        }

        public void Board(string state, params string[] resources)
        {
            var doc = JsonSerializer.Serialize(new { tasks = new Dictionary<string, object> { ["T"] = new { workflow = new { state, packet = new { resources } } } } });
            var payload = JsonSerializer.Serialize(new { Value = new { Json = doc } });
            Execute("DELETE FROM relay.grain_state; INSERT INTO relay.grain_state VALUES ('repo/default', 'board', $payload)", ("payload", payload));
        }

        public void RollupOldCalls() => Execute("""
            INSERT INTO relay.activity_daily
                SELECT repository_id, CAST(CAST(cocern_at(at_ticks) AS DATE) AS VARCHAR), tool, kind, count(*), 0, 0, 0, 0, 0, 0, 0, 0
                FROM relay.activity_log WHERE at_ticks < $cutoff GROUP BY ALL;
            DELETE FROM relay.activity_log WHERE at_ticks < $cutoff;
            """, ("cutoff", Now - 30 * Day));

        public List<object?[]> Named(string name)
        {
            Assert.True(NamedQueries.TryGet(name, out var sql));
            using var command = Command(sql, ("repository", "repo"), ("now", Now), ("from", Now - 7 * Day), ("prev", Now - 14 * Day));
            using var reader = command.ExecuteReader();
            Assert.Equal(NamedQueries.Columns, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
            return Rows(reader);
        }

        public List<object?[]> Sql(string sql)
        {
            using var command = Command(sql);
            using var reader = command.ExecuteReader();
            return Rows(reader);
        }

        private static List<object?[]> Rows(System.Data.Common.DbDataReader reader)
        {
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
            return rows;
        }

        private DuckDBCommand Command(string sql, params (string Name, object? Value)[] parameters)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                if (!sql.Contains("$" + name, StringComparison.Ordinal)) continue;
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        private void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = Command(sql, parameters);
            command.ExecuteNonQuery();
        }

        public void Dispose() => connection.Dispose();
    }
}

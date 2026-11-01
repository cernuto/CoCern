using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PkRelay.Contracts;
using PkRelay.Core;

namespace PkRelay.Storage;

/// <summary>Activity telemetry settings. Recording never blocks a call; a full queue drops events.</summary>
public sealed record ActivityOptions
{
    /// <summary>Record tool calls and wakes. Default on: telemetry changes no reply.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Raw <c>activity_log</c> rows older than this are rolled into <c>activity_daily</c> and deleted.</summary>
    public int RetentionDays { get; init; } = 30;
    internal int QueueCapacity { get; init; } = RelayLimits.ActivityQueueCapacity;
    internal TimeSpan BatchInterval { get; init; } = TimeSpan.FromMilliseconds(RelayLimits.ActivityBatchMilliseconds);
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Telemetry beside the grain state: an <c>activity_log</c> fed by a bounded queue and a background writer that inserts batches in its
/// own short transactions, a daily rollup into <c>activity_daily</c>, and the hub-side counters derived from those rows.
/// </summary>
public sealed partial class SqliteStore : IActivitySink, IActivityCounterSource
{
    private static readonly TimeSpan RollupInterval = TimeSpan.FromDays(1);

    private const string ActivitySchema = """
        CREATE TABLE IF NOT EXISTS activity_log (
            id INTEGER PRIMARY KEY,
            at_ticks INTEGER NOT NULL,
            kind TEXT NOT NULL,
            repository_id TEXT NOT NULL,
            session_id TEXT,
            role TEXT,
            tool TEXT,
            ok INTEGER NOT NULL,
            code TEXT NOT NULL,
            request_bytes INTEGER NOT NULL,
            reply_bytes INTEGER NOT NULL,
            duration_ms REAL NOT NULL,
            input_tokens INTEGER,
            output_tokens INTEGER,
            request_id TEXT,
            target TEXT,
            content_hash TEXT,
            workflow_state TEXT,
            offer_target TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_activity_log_at ON activity_log(at_ticks);
        CREATE INDEX IF NOT EXISTS ix_activity_log_repository ON activity_log(repository_id, at_ticks);
        CREATE TABLE IF NOT EXISTS activity_daily (
            day TEXT NOT NULL,
            repository_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            role TEXT NOT NULL,
            tool TEXT NOT NULL,
            calls INTEGER NOT NULL,
            errors INTEGER NOT NULL,
            request_bytes INTEGER NOT NULL,
            reply_bytes INTEGER NOT NULL,
            duration_ms REAL NOT NULL,
            input_tokens INTEGER NOT NULL,
            output_tokens INTEGER NOT NULL,
            PRIMARY KEY (day, repository_id, kind, role, tool)
        );

        """;

    private ActivityOptions _activityOptions = new();
    // Telemetry has its own connection and lock, so it never holds the grain writer's gate; under WAL the two writers wait on each other
    // only for SQLite's write lock, inside busy_timeout, and counter reads never wait on either.
    private SqliteConnection? _activityConnection;
    private readonly SemaphoreSlim _activityGate = new(1, 1);
    private Channel<ActivityEvent>? _activity;
    private readonly SemaphoreSlim _activityDrain = new(1, 1);
    private readonly CancellationTokenSource _activityStop = new();
    private Task _activityWriter = Task.CompletedTask;
    private DateTimeOffset _lastRollup = DateTimeOffset.MinValue;
    private long _droppedActivity;

    /// <summary>Events dropped by this process because the queue was full or a batch could not be written.</summary>
    public long DroppedActivityEvents => Interlocked.Read(ref _droppedActivity);

    private void StartActivity(ActivityOptions options)
    {
        _activityOptions = options;
        _activityConnection = new SqliteConnection(ConnectionString(DatabasePath));
        _activityConnection.Open();
        using (var pragmas = ActivityCommand("PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;"))
            pragmas.ExecuteNonQuery();
        if (!options.Enabled) return;
        // Roll up once at startup, since a hub restarted more often than daily would otherwise never reach the daily rollup.
        _lastRollup = options.Clock.GetUtcNow();
        try { RollUpActivityAsync(_lastRollup).GetAwaiter().GetResult(); }
        catch (SqliteException) { }
        _activity = Channel.CreateBounded<ActivityEvent>(new BoundedChannelOptions(Math.Max(1, options.QueueCapacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
        _activityWriter = Task.Run(WriteActivityAsync);
    }

    /// <summary>Queues one event. Never blocks and never throws: when the queue is full the event is dropped and counted.</summary>
    public void Record(ActivityEvent activity)
    {
        if (_activity is not { } channel || activity is null) return;
        if (!channel.Writer.TryWrite(activity)) Interlocked.Increment(ref _droppedActivity);
    }

    /// <summary>Writes every event queued before this call. Snapshots and tests use it; the background writer does the same each second.</summary>
    public Task FlushActivityAsync(CancellationToken cancellationToken = default) => DrainActivityAsync(cancellationToken);

    private async Task WriteActivityAsync()
    {
        var reader = _activity!.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                // Collect for one batch interval; shutdown skips the wait and drains at once.
                try { await Task.Delay(_activityOptions.BatchInterval, _activityOptions.Clock, _activityStop.Token); }
                catch (OperationCanceledException) { }
                await DrainActivityAsync(CancellationToken.None);
                await RollUpIfDueAsync();
            }
        }
        catch (Exception) { /* Telemetry never takes the hub down. */ }
    }

    private async Task DrainActivityAsync(CancellationToken cancellationToken)
    {
        if (_activity is not { } channel) return;
        await _activityDrain.WaitAsync(cancellationToken);
        try
        {
            var batch = new List<ActivityEvent>(RelayLimits.ActivityBatchSize);
            while (true)
            {
                batch.Clear();
                while (batch.Count < RelayLimits.ActivityBatchSize && channel.Reader.TryRead(out var item)) batch.Add(item);
                if (batch.Count == 0) return;
                try { await InsertActivityAsync(batch); }
                catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException) { Interlocked.Add(ref _droppedActivity, batch.Count); }
            }
        }
        finally { _activityDrain.Release(); }
    }

    private async Task InsertActivityAsync(List<ActivityEvent> batch)
    {
        await _activityGate.WaitAsync();
        try
        {
            var connection = _activityConnection ?? throw new ObjectDisposedException(nameof(SqliteStore));
            using var transaction = connection.BeginTransaction(deferred: false);
            using var insert = ActivityCommand("""
                INSERT INTO activity_log(at_ticks, kind, repository_id, session_id, role, tool, ok, code, request_bytes, reply_bytes, duration_ms,
                    input_tokens, output_tokens, request_id, target, content_hash, workflow_state, offer_target)
                VALUES ($at, $kind, $repository, $session, $role, $tool, $ok, $code, $requestBytes, $replyBytes, $duration,
                    $input, $output, $request, $target, $hash, $state, $offer);
                """, transaction);
            string[] names = ["$at", "$kind", "$repository", "$session", "$role", "$tool", "$ok", "$code", "$requestBytes", "$replyBytes", "$duration", "$input", "$output", "$request", "$target", "$hash", "$state", "$offer"];
            var parameters = names.Select(name => insert.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
            foreach (var item in batch)
            {
                object?[] values =
                [
                    item.Timestamp.UtcTicks, item.Kind, item.RepositoryId, item.SessionId, item.Role, item.Tool, item.Ok ? 1 : 0, item.Code,
                    item.RequestBytes, item.ReplyBytes, item.DurationMilliseconds, item.InputTokens, item.OutputTokens, item.RequestId, item.Target,
                    item.ContentHash, item.WorkflowState, item.OfferTarget
                ];
                for (var index = 0; index < values.Length; index++) parameters[index].Value = values[index] ?? DBNull.Value;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _activityGate.Release(); }
    }

    private async Task RollUpIfDueAsync()
    {
        var now = _activityOptions.Clock.GetUtcNow();
        if (now - _lastRollup < RollupInterval) return;
        _lastRollup = now;
        try { await RollUpActivityAsync(now); }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException) { }
    }

    /// <summary>Rolls raw rows older than the retention period into per-day totals, then deletes them, in one transaction. Returns the rows rolled up.</summary>
    internal async Task<int> RollUpActivityAsync(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Max(1, _activityOptions.RetentionDays)).UtcTicks;
        await _activityGate.WaitAsync();
        try
        {
            var connection = _activityConnection ?? throw new ObjectDisposedException(nameof(SqliteStore));
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var rollup = ActivityCommand($"""
                INSERT INTO activity_daily(day, repository_id, kind, role, tool, calls, errors, request_bytes, reply_bytes, duration_ms, input_tokens, output_tokens)
                SELECT date((at_ticks - {UnixEpochTicks}) / 10000000, 'unixepoch'), repository_id, kind, COALESCE(role, ''), COALESCE(tool, ''),
                       COUNT(*), SUM(1 - ok), SUM(request_bytes), SUM(reply_bytes), SUM(duration_ms), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0)
                FROM activity_log WHERE at_ticks < $cutoff
                GROUP BY 1, 2, 3, 4, 5
                ON CONFLICT(day, repository_id, kind, role, tool) DO UPDATE SET
                    calls = calls + excluded.calls, errors = errors + excluded.errors, request_bytes = request_bytes + excluded.request_bytes,
                    reply_bytes = reply_bytes + excluded.reply_bytes, duration_ms = duration_ms + excluded.duration_ms,
                    input_tokens = input_tokens + excluded.input_tokens, output_tokens = output_tokens + excluded.output_tokens;
                """, transaction))
            {
                rollup.Parameters.AddWithValue("$cutoff", cutoff);
                rollup.ExecuteNonQuery();
            }
            int deleted;
            using (var delete = ActivityCommand("DELETE FROM activity_log WHERE at_ticks < $cutoff;", transaction))
            {
                delete.Parameters.AddWithValue("$cutoff", cutoff);
                deleted = delete.ExecuteNonQuery();
            }
            transaction.Commit();
            return deleted;
        }
        finally { _activityGate.Release(); }
    }

    private const long UnixEpochTicks = 621_355_968_000_000_000;

    /// <summary>The hub-side counters for one repository over [from, to). Definitions are in docs/activity.md.</summary>
    public async Task<ActivityCounters> ReadActivityCountersAsync(string repositoryId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryId);
        await _activityGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_activityConnection is null, this);
            SqliteCommand Query(string sql)
            {
                var command = ActivityCommand(sql);
                command.Parameters.AddWithValue("$repository", repositoryId);
                command.Parameters.AddWithValue("$from", from.UtcTicks);
                command.Parameters.AddWithValue("$to", to.UtcTicks);
                return command;
            }
            long Scalar(string sql) { using var command = Query(sql); return Convert.ToInt64(command.ExecuteScalar() ?? 0L); }
            const string Window = "repository_id = $repository AND at_ticks >= $from AND at_ticks < $to";

            long calls, inboxReads, wakes, offers;
            using (var totals = Query($"""
                SELECT COALESCE(SUM(kind = 'call'), 0), COALESCE(SUM(kind = 'call' AND ok = 1 AND target LIKE 'inbox:%'), 0),
                       COALESCE(SUM(kind = 'wake'), 0), COALESCE(SUM(offer_target IS NOT NULL), 0)
                FROM activity_log WHERE {Window};
                """))
            using (var reader = totals.ExecuteReader())
            {
                reader.Read();
                calls = reader.GetInt64(0); inboxReads = reader.GetInt64(1); wakes = reader.GetInt64(2); offers = reader.GetInt64(3);
            }

            // A retried request replays its reply and is recorded again; count its reported tokens once.
            long input, output;
            using (var tokens = Query($"""
                SELECT COALESCE(SUM(i), 0), COALESCE(SUM(o), 0) FROM (
                    SELECT MAX(input_tokens) AS i, MAX(output_tokens) AS o FROM activity_log
                    WHERE {Window} AND (input_tokens IS NOT NULL OR output_tokens IS NOT NULL)
                    GROUP BY session_id, tool, COALESCE(request_id, CAST(id AS TEXT)));
                """))
            using (var reader = tokens.ExecuteReader())
            {
                reader.Read();
                input = reader.GetInt64(0); output = reader.GetInt64(1);
            }

            var duplicates = Scalar("""
                SELECT COUNT(*) FROM (
                    SELECT at_ticks, content_hash, request_id,
                           LAG(content_hash) OVER w AS previous_hash, LAG(request_id) OVER w AS previous_request
                    FROM activity_log
                    WHERE repository_id = $repository AND kind = 'call' AND ok = 1 AND content_hash IS NOT NULL AND target IS NOT NULL AND at_ticks < $to
                    WINDOW w AS (PARTITION BY target ORDER BY at_ticks, id))
                WHERE at_ticks >= $from AND content_hash = previous_hash AND request_id IS NOT previous_request;
                """);

            using var turnsCommand = Query("""
                SELECT COUNT(*) FROM (
                    SELECT at_ticks, LAG(at_ticks) OVER (PARTITION BY session_id ORDER BY at_ticks, id) AS previous
                    FROM activity_log
                    WHERE repository_id = $repository AND kind = 'call' AND role = 'manager' AND session_id IS NOT NULL AND at_ticks < $to)
                WHERE at_ticks >= $from AND (previous IS NULL OR at_ticks - previous >= $gap);
                """);
            turnsCommand.Parameters.AddWithValue("$gap", TimeSpan.FromSeconds(RelayLimits.ActivityTurnGapSeconds).Ticks);
            var managerTurns = Convert.ToInt64(turnsCommand.ExecuteScalar() ?? 0L);

            var (closures, medianSeconds) = Closures(Query($"""
                SELECT target, at_ticks, workflow_state FROM activity_log
                WHERE repository_id = $repository AND kind = 'call' AND tool = '{RelayTools.TaskUpdate}' AND ok = 1 AND workflow_state IS NOT NULL AND target IS NOT NULL AND at_ticks < $to
                ORDER BY target, at_ticks, id;
                """), from.UtcTicks);

            var blocked = BlockedOffers(Query($"""
                SELECT session_id, tool, ok, target, workflow_state, offer_target FROM activity_log
                WHERE {Window} AND kind = 'call'
                  AND (offer_target IS NOT NULL OR (target IS NOT NULL AND tool IN ('{RelayTools.TaskClaim}', '{RelayTools.TaskUpdate}')))
                ORDER BY at_ticks, id;
                """));

            return new()
            {
                Calls = calls, InputTokens = input, OutputTokens = output, InboxReads = inboxReads, Wakes = wakes, OffersMade = offers,
                OffersEndedBlocked = blocked, DuplicateStatusWrites = duplicates, ManagerTurns = managerTurns, Closures = closures,
                ManagerTurnsPerClosure = closures == 0 ? null : (double)managerTurns / closures, MedianSecondsToAcceptedResult = medianSeconds,
                DroppedEvents = DroppedActivityEvents
            };
        }
        finally { _activityGate.Release(); }
    }

    /// <summary>A closure is a task's workflow becoming completed; its time to accepted result runs from the first logged ready since the previous closure.</summary>
    private static (long Closures, double? MedianSeconds) Closures(SqliteCommand command, long from)
    {
        using (command)
        using (var reader = command.ExecuteReader())
        {
            long closures = 0;
            var durations = new List<double>();
            string? target = null;
            string? previousState = null;
            long? readyAt = null;
            while (reader.Read())
            {
                var rowTarget = reader.GetString(0);
                var at = reader.GetInt64(1);
                var state = reader.GetString(2);
                if (rowTarget != target) { target = rowTarget; previousState = null; readyAt = null; }
                if (state == "ready") readyAt ??= at;
                if (state == "completed" && previousState != "completed")
                {
                    if (at >= from)
                    {
                        closures++;
                        if (readyAt is { } start) durations.Add(TimeSpan.FromTicks(at - start).TotalSeconds);
                    }
                    readyAt = null;
                }
                previousState = state;
            }
            if (durations.Count == 0) return (closures, null);
            durations.Sort();
            var middle = durations.Count / 2;
            return (closures, durations.Count % 2 == 1 ? durations[middle] : (durations[middle - 1] + durations[middle]) / 2);
        }
    }

    /// <summary>
    /// An offer of task X to session S ends blocked when S's next claim of X fails, or X's workflow is set to blocked before S claims it or it
    /// completes. Offers not yet resolved are not counted.
    /// </summary>
    private static long BlockedOffers(SqliteCommand command)
    {
        using (command)
        using (var reader = command.ExecuteReader())
        {
            long blocked = 0;
            var pending = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var session = reader.IsDBNull(0) ? null : reader.GetString(0);
                var tool = reader.IsDBNull(1) ? null : reader.GetString(1);
                var ok = reader.GetInt64(2) == 1;
                var target = reader.IsDBNull(3) ? null : reader.GetString(3);
                var state = reader.IsDBNull(4) ? null : reader.GetString(4);
                var offer = reader.IsDBNull(5) ? null : reader.GetString(5);
                if (target is not null && pending.TryGetValue(target, out var offered))
                {
                    if (tool == RelayTools.TaskClaim)
                    {
                        var resolved = offered.RemoveAll(candidate => candidate == session);
                        if (!ok) blocked += resolved;
                    }
                    else if (ok && state == "blocked") { blocked += offered.Count; offered.Clear(); }
                    else if (ok && state == "completed") offered.Clear();
                }
                if (offer is not null)
                {
                    if (!pending.TryGetValue(offer, out var list)) pending[offer] = list = [];
                    list.Add(session);
                }
            }
            return blocked;
        }
    }

    private SqliteCommand ActivityCommand(string sql, SqliteTransaction? transaction = null)
    {
        var command = (_activityConnection ?? throw new ObjectDisposedException(nameof(SqliteStore))).CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    /// <summary>Drains queued events, then closes the telemetry connection. Safe to call more than once.</summary>
    private void StopActivity()
    {
        if (_activity is { } channel && channel.Writer.TryComplete())
        {
            _activityStop.Cancel();
            try { _activityWriter.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
        }
        _activityGate.Wait();
        try { _activityConnection?.Dispose(); _activityConnection = null; }
        finally { _activityGate.Release(); }
    }
}

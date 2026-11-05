using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CoCern.Adapter;

/// <summary>
/// How often a route may be woken: at least <paramref name="MinInterval"/> apart, and at most <paramref name="MaxPerWindow"/> times in any <paramref name="Window"/>.
/// A wake whose outcome is unknown holds its route for <paramref name="UncertainHold"/>; its messages are never sent again either way.
/// </summary>
public sealed record WakeLimits(TimeSpan MinInterval, int MaxPerWindow, TimeSpan Window, TimeSpan UncertainHold = default)
{
    public TimeSpan UncertainHold { get; init; } = UncertainHold == default ? TimeSpan.FromMinutes(10) : UncertainHold;

    public static WakeLimits Default { get; } = new(TimeSpan.FromSeconds(3), 20, TimeSpan.FromMinutes(10));
}

/// <summary>
/// One owned database, with durable reservations deliberately never retried automatically. A wake whose outcome is unknown (the dispatch failed
/// or was cut off) holds its route for a while rather than for good: new mail then wakes the chat again, and the dispatcher still refuses
/// while Codex holds a pending wake for the thread, so nothing is queued twice.
/// </summary>
public sealed class WakeState : IDisposable
{
    private readonly FileStream _ownership;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly WakeLimits _limits;
    public WakeState(string path, WakeLimits? limits = null)
    {
        _limits = limits ?? WakeLimits.Default;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _connection.Open();
            Execute("PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS batches (route TEXT NOT NULL, batch TEXT PRIMARY KEY, status TEXT NOT NULL, at INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS messages (route TEXT NOT NULL, message TEXT NOT NULL, batch TEXT NOT NULL, PRIMARY KEY(route,message)); CREATE TABLE IF NOT EXISTS printed (route TEXT NOT NULL, message TEXT NOT NULL, PRIMARY KEY(route,message)); UPDATE batches SET status='uncertain' WHERE status='reserved';");
        }
        catch { _connection.Dispose(); _ownership.Dispose(); throw; }
    }
    public static string Key(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(parts))));
    public bool IsUncertain(string route, DateTimeOffset? now = null)
    {
        var since = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds() - (long)_limits.UncertainHold.TotalMilliseconds;
        lock (_gate) return Scalar("SELECT COUNT(*) FROM batches WHERE route=$route AND status IN ('reserved','uncertain') AND at > $value", route, since) > 0;
    }
    /// <param name="busy">Asked only when there is unseen mail; while it says the chat is mid-turn nothing is reserved and the status is "busy".</param>
    public (string Status, string? Batch) Reserve(string route, IEnumerable<string> ids, DateTimeOffset now, Func<bool>? busy = null)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (IsUncertain(route, now)) return ("uncertain", null);
            var unseen = Unseen(route, ids);
            if (unseen.Length == 0) return ("empty", null);
            if (busy?.Invoke() == true) return ("busy", null);
            var time = now.ToUnixTimeMilliseconds();
            if (Scalar("SELECT COUNT(*) FROM batches WHERE route=$route AND at > $value", route, time - (long)_limits.MinInterval.TotalMilliseconds) > 0 ||
                Scalar("SELECT COUNT(*) FROM batches WHERE route=$route AND at > $value", route, time - (long)_limits.Window.TotalMilliseconds) >= _limits.MaxPerWindow) return ("rate-limited", null);
            var batch = Guid.NewGuid().ToString("D");
            Execute("INSERT INTO batches(route,batch,status,at) VALUES($route,$value,'reserved',$at)", route, batch, time);
            foreach (var id in unseen) Execute("INSERT INTO messages(route,message,batch) VALUES($route,$value,$batch)", route, id, batch: batch);
            transaction.Commit();
            return ("reserved", batch);
        }
    }
    /// <summary>Whether <see cref="Reserve"/> would find anything new in <paramref name="ids"/>; reserves nothing.</summary>
    public bool HasUnseen(string route, IEnumerable<string> ids)
    {
        lock (_gate) return Unseen(route, ids).Length > 0;
    }
    private string[] Unseen(string route, IEnumerable<string> ids) => ids.Select(id => Key(id)).Distinct().Where(id => Scalar("SELECT COUNT(*) FROM messages WHERE route=$route AND message=$value", route, id) == 0).ToArray();
    /// <summary>Forgets a reserved batch that was never sent (stopped or canceled first), so its route and messages can wake again.</summary>
    public void Release(string batch)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("DELETE FROM messages WHERE batch=$batch", batch: batch);
            Execute("DELETE FROM batches WHERE batch=$value AND status='reserved'", value: batch);
            transaction.Commit();
        }
    }
    public void Complete(string batch, bool queued)
    {
        lock (_gate) Execute("UPDATE batches SET status=$route WHERE batch=$value", queued ? "queued" : "uncertain", batch);
    }
    /// <summary>Those of <paramref name="ids"/> whose text has already been handed to this route's chat.</summary>
    public IReadOnlySet<string> Printed(string route, IEnumerable<string> ids)
    {
        lock (_gate) return ids.Where(id => Scalar("SELECT COUNT(*) FROM printed WHERE route=$route AND message=$value", route, Key(id)) > 0).ToHashSet(StringComparer.Ordinal);
    }
    public void MarkPrinted(string route, IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var id in ids) Execute("INSERT OR IGNORE INTO printed(route,message) VALUES($route,$value)", route, Key(id));
            transaction.Commit();
        }
    }
    /// <summary>How long a hook's ledger call waits for a waiter's write to finish; a hook must stay short, and these writes take milliseconds.</summary>
    private const int LedgerBusySeconds = 1;

    /// <summary>
    /// Those of <paramref name="ids"/> whose text has already been handed to this route's chat, read from the database at <paramref name="path"/> on a connection of its own.
    /// A waiter owns its state for the whole wait, across the chat's later turns, so an ordinary-turn hook could never open a <see cref="WakeState"/> to ask. The ownership
    /// lock guards the wake reservations; the ledger is one small table that SQLite's own file locks keep consistent. A database not created yet, or from before the ledger, has printed nothing.
    /// </summary>
    internal static IReadOnlySet<string> ReadPrinted(string path, string route, IEnumerable<string> ids)
    {
        var printed = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return printed;
        using var connection = new SqliteConnection(LedgerConnection(path, SqliteOpenMode.ReadWrite));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM printed WHERE route=$route AND message=$message";
        command.Parameters.AddWithValue("$route", route);
        var message = command.Parameters.AddWithValue("$message", "");
        try
        {
            foreach (var id in ids)
            {
                message.Value = Key(id);
                if ((long)command.ExecuteScalar()! > 0) printed.Add(id);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == NoSuchTable) { }
        return printed;
    }

    /// <summary>Records <paramref name="ids"/> as handed to this route's chat, the way <see cref="ReadPrinted"/> reads them: without the ownership lock, creating the database if no waiter has yet.</summary>
    internal static void RecordPrinted(string path, string route, IEnumerable<string> ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var connection = new SqliteConnection(LedgerConnection(path, SqliteOpenMode.ReadWriteCreate));
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS printed (route TEXT NOT NULL, message TEXT NOT NULL, PRIMARY KEY(route,message))";
            create.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO printed(route,message) VALUES($route,$message)";
            insert.Parameters.AddWithValue("$route", route);
            var message = insert.Parameters.AddWithValue("$message", "");
            foreach (var id in ids)
            {
                message.Value = Key(id);
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    /// <summary>SQLite's generic error, which is what reading a table that does not exist gives.</summary>
    private const int NoSuchTable = 1;

    private static string LedgerConnection(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = LedgerBusySeconds }.ToString();

    private long Scalar(string sql, string route, object? value = null)
    {
        using var command = Command(sql, route, value);
        return (long)command.ExecuteScalar()!;
    }
    private void Execute(string sql, string? route = null, object? value = null, long at = 0, string? batch = null)
    {
        using var command = Command(sql, route, value);
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$batch", batch ?? "");
        command.ExecuteNonQuery();
    }
    private SqliteCommand Command(string sql, string? route, object? value)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$route", route ?? "");
        command.Parameters.AddWithValue("$value", value ?? "");
        return command;
    }
    public void Dispose() { _connection.Dispose(); _ownership.Dispose(); }
}

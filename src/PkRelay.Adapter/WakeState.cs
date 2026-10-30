using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PkRelay.Adapter;

/// <summary>How often a route may be woken: at least <paramref name="MinInterval"/> apart, and at most <paramref name="MaxPerWindow"/> times in any <paramref name="Window"/>.</summary>
public sealed record WakeLimits(TimeSpan MinInterval, int MaxPerWindow, TimeSpan Window)
{
    public static WakeLimits Default { get; } = new(TimeSpan.FromSeconds(3), 20, TimeSpan.FromMinutes(10));
}

/// <summary>One owned database, with durable reservations deliberately never retried automatically.</summary>
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
            Execute("PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS batches (route TEXT NOT NULL, batch TEXT PRIMARY KEY, status TEXT NOT NULL, at INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS messages (route TEXT NOT NULL, message TEXT NOT NULL, batch TEXT NOT NULL, PRIMARY KEY(route,message)); UPDATE batches SET status='uncertain' WHERE status='reserved';");
        }
        catch { _connection.Dispose(); _ownership.Dispose(); throw; }
    }
    public static string Key(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(parts))));
    public bool IsUncertain(string route)
    {
        lock (_gate) return Scalar("SELECT COUNT(*) FROM batches WHERE route=$route AND status IN ('reserved','uncertain')", route) > 0;
    }
    /// <param name="busy">Asked only when there is unseen mail; while it says the chat is mid-turn nothing is reserved and the status is "busy".</param>
    public (string Status, string? Batch) Reserve(string route, IEnumerable<string> ids, DateTimeOffset now, Func<bool>? busy = null)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (IsUncertain(route)) return ("uncertain", null);
            var unseen = ids.Select(id => Key(id)).Distinct().Where(id => Scalar("SELECT COUNT(*) FROM messages WHERE route=$route AND message=$value", route, id) == 0).ToArray();
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
    public void Complete(string batch, bool queued)
    {
        lock (_gate) Execute("UPDATE batches SET status=$route WHERE batch=$value", queued ? "queued" : "uncertain", batch);
    }
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

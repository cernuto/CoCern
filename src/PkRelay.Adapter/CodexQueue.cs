using Microsoft.Data.Sqlite;

namespace PkRelay.Adapter;

/// <summary>
/// Whether a PkRelay wake is still waiting in Codex's own queue for a thread, read-only from <c>&lt;codex home&gt;\queue_N.sqlite</c>
/// (<c>queued_items</c>: <c>thread_id</c>, <c>payload_json</c>). <c>codex queue</c> accepting a wake says nothing about Codex having run it;
/// the item leaving this queue does. While one is pending another is never queued: the pending prompt already asks the chat to check its
/// whole inbox, so it covers every message since. Without this, 23 wakes piled up behind one long turn. Any database it cannot read, or
/// read in the expected shape, holds nothing back.
/// </summary>
public sealed class CodexQueue(string codexHome)
{
    /// <summary>Codex's home folder under the user's profile.</summary>
    public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public bool HasPendingWake(string threadId)
    {
        try
        {
            if (!Directory.Exists(codexHome)) return false;
            // The newest schema version's database is the live one.
            var database = Directory.EnumerateFiles(codexHome, "queue_*.sqlite").Select(path => (path, version: Version(path))).Where(x => x.version >= 0)
                .OrderByDescending(x => x.version).Select(x => x.path).FirstOrDefault();
            if (database is null) return false;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM queued_items WHERE thread_id = $thread AND instr(payload_json, 'PkRelay inbox wake') > 0)";
            command.Parameters.AddWithValue("$thread", threadId);
            return command.ExecuteScalar() is long found && found == 1;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    private static int Version(string path) =>
        Path.GetFileNameWithoutExtension(path) is var name && int.TryParse(name.AsSpan("queue_".Length), out var version) ? version : -1;
}

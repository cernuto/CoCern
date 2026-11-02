using Microsoft.Data.Sqlite;

namespace CoCern.Storage;

/// <summary>Analytics snapshots: a consistent copy of the database with private and credential data removed, for DuckDB to read.</summary>
public sealed partial class SqliteStore
{
    /// <summary>
    /// The states an analytics snapshot keeps. Session state (private inboxes, attention notes, bindings), control (the session
    /// directory) and discovery (machine credentials) never leave the live database; analytics reads sessions only through
    /// <c>activity_log</c>.
    /// </summary>
    private const string SnapshotStates = "('board', 'lock')";

    /// <summary>
    /// Writes queued activity, copies the database to <paramref name="destination"/> with the backup API, then scrubs the copy: it keeps only
    /// board and lock state, removes idempotency caches (<c>requests</c>, which hold earlier replies, and their <c>requestTombstones</c>) and any session token, drops the search
    /// index, and vacuums so removed data does not stay in free pages. The live database is never changed.
    /// </summary>
    /// <remarks>
    /// The copy is read on its own read-only connection, not under the grain writer's gate: under WAL it reads the last committed
    /// state as one consistent snapshot, and commits keep going while it runs.
    /// </remarks>
    public async Task CreateAnalyticsSnapshotAsync(string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var target = Path.GetFullPath(destination);
        if (string.Equals(target, DatabasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Snapshot destination must differ from the live database.", nameof(destination));
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushActivityAsync(cancellationToken);
        // The copy holds sessions, tokens and inboxes until it is scrubbed, so the folder and file are this user's only, like the database.
        CreatePrivateDirectory(Path.GetDirectoryName(target)!, tightenExisting: true);
        RestrictToOwner(target);
        try
        {
            await Task.Run(() =>
            {
                using (AcquireOwnership(target))
                {
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
                    source.Open();
                    using var copy = new SqliteConnection(ConnectionString(target));
                    copy.Open();
                    cancellationToken.ThrowIfCancellationRequested();
                    // One backup step holds one read transaction, so the copy is a single committed state.
                    source.BackupDatabase(copy);
                }
                cancellationToken.ThrowIfCancellationRequested();
                ScrubSnapshot(target);
            }, cancellationToken);
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void ScrubSnapshot(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        void Run(string sql, SqliteTransaction? transaction = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        // A rollback journal, so the copy is one self-contained file that a read-only reader can open.
        Run("PRAGMA journal_mode=DELETE;");
        using (var transaction = connection.BeginTransaction())
        {
            // `|| ''` keeps the inner document a JSON string, as the hub stores it, rather than an embedded object.
            Run($"""
                DELETE FROM due_work WHERE state_name NOT IN {SnapshotStates};
                DELETE FROM grain_state WHERE state_name NOT IN {SnapshotStates};
                UPDATE grain_state
                SET payload = json_set(payload, '$.Value.Json', json_remove(json_extract(payload, '$.Value.Json'), '$.requests', '$.requestTombstones', '$.sessionToken') || '')
                WHERE payload IS NOT NULL AND json_valid(payload) AND json_valid(json_extract(payload, '$.Value.Json'));
                DROP TABLE IF EXISTS search_index;
                DROP TABLE IF EXISTS search_docs;
                DELETE FROM hub_metadata;
                """, transaction);
            transaction.Commit();
        }
        Run("VACUUM;");
    }
}

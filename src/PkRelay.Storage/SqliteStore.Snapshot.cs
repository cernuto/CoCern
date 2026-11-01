using Microsoft.Data.Sqlite;

namespace PkRelay.Storage;

/// <summary>Analytics snapshots: a consistent copy of the database with credentials and caches removed, for DuckDB to read.</summary>
public sealed partial class SqliteStore
{
    /// <summary>The states an analytics snapshot keeps. Control and discovery hold session directories and machine credentials.</summary>
    private const string SnapshotStates = "('board', 'session', 'lock')";

    /// <summary>
    /// Writes queued activity, copies the database to <paramref name="destination"/> with the backup API, then scrubs the copy: it keeps only
    /// board, session and lock state, removes session tokens and idempotency caches (<c>requests</c>, which hold earlier replies), drops the
    /// search index, and vacuums so removed data does not stay in free pages. The live database is never changed.
    /// </summary>
    public async Task CreateAnalyticsSnapshotAsync(string destination, CancellationToken cancellationToken = default)
    {
        await FlushActivityAsync(cancellationToken);
        await BackupAsync(destination, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScrubSnapshot(Path.GetFullPath(destination));
        }
        catch
        {
            try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
                SET payload = json_set(payload, '$.Value.Json', json_remove(json_extract(payload, '$.Value.Json'), '$.requests', '$.sessionToken') || '')
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

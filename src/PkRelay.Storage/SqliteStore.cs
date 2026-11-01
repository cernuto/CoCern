using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using Orleans.Storage;
using PkRelay.Core;
using PkRelay.Domain;

namespace PkRelay.Storage;

public sealed partial class SqliteStore : IDueWorkSource, IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FileStream _ownership;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public string DatabasePath { get; }
    public string ServiceId { get; private set; } = "";
    public string IncarnationId { get; private set; } = "";

    public SqliteStore(string databasePath, ActivityOptions? activity = null, SearchOptions? search = null) : this(databasePath, activity, SearchModule, search) { }

    /// <summary>Tests name a missing module here to exercise the startup check.</summary>
    internal SqliteStore(string databasePath, ActivityOptions? activity, string searchModule, SearchOptions? search = null)
    {
        _searchOptions = search ?? new();
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var existingDatabase = File.Exists(DatabasePath) && new FileInfo(DatabasePath).Length != 0;
        _ownership = AcquireOwnership(DatabasePath);
        _connection = new SqliteConnection(ConnectionString(DatabasePath));
        try
        {
            _connection.Open();
            Initialize(existingDatabase, searchModule);
            OpenSearchConnection();
            StartActivity(activity ?? new());
        }
        catch
        {
            StopActivity();
            CloseSearchConnection();
            _connection.Dispose();
            _ownership.Dispose();
            throw;
        }
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        DefaultTimeout = 5
    }.ToString();

    // Keep the lease file after closing: unlinking it could create a second independent lock inode.
    private static FileStream AcquireOwnership(string path) =>
        new(path + ".hub.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>The schema this hub writes. Version 1 databases are upgraded in place after a backup; newer ones are refused.</summary>
    public const int SchemaVersion = 2;
    private const string SearchModule = "fts5";

    private void Initialize(bool existingDatabase, string searchModule)
    {
        using var versionCommand = Command("PRAGMA user_version;");
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > SchemaVersion)
            throw new InvalidOperationException($"SQLite schema version {version} is newer than this hub supports.");
        RequireModule(_connection, searchModule);
        if (version < SchemaVersion && existingDatabase)
        {
            using var backup = new SqliteConnection(ConnectionString(DatabasePath + ".pre-migration-" + Guid.NewGuid().ToString("N") + ".db"));
            backup.Open();
            _connection.BackupDatabase(backup);
        }
        using (var pragmas = Command("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"))
            pragmas.ExecuteNonQuery();
        using var transaction = _connection.BeginTransaction(deferred: false);
        using (var schema = Command("""
            CREATE TABLE IF NOT EXISTS grain_state (
                state_name TEXT NOT NULL,
                grain_type TEXT NOT NULL,
                grain_key TEXT NOT NULL,
                generation INTEGER NOT NULL,
                etag TEXT NOT NULL,
                payload TEXT,
                PRIMARY KEY (state_name, grain_type, grain_key)
            );
            CREATE TABLE IF NOT EXISTS due_work (
                state_name TEXT NOT NULL,
                grain_type TEXT NOT NULL,
                grain_key TEXT NOT NULL,
                work_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                due_ticks INTEGER NOT NULL,
                PRIMARY KEY (state_name, grain_type, grain_key, work_id),
                FOREIGN KEY (state_name, grain_type, grain_key) REFERENCES grain_state(state_name, grain_type, grain_key)
            );
            CREATE INDEX IF NOT EXISTS ix_due_work_due ON due_work(due_ticks, state_name, grain_type, grain_key, work_id);
            CREATE TABLE IF NOT EXISTS hub_metadata (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            """ + ActivitySchema + SearchSchema + """
            CREATE VIRTUAL TABLE IF NOT EXISTS search_index USING fts5(
                state_name UNINDEXED, grain_key UNINDEXED, doc_id UNINDEXED,
                kind UNINDEXED, repository_id UNINDEXED, owner_session UNINDEXED,
                status UNINDEXED, updated_ticks UNINDEXED,
                title, body,
                tokenize = 'porter unicode61');
            PRAGMA user_version=2;
            """, transaction))
            schema.ExecuteNonQuery();
        using (var createService = Command("INSERT OR IGNORE INTO hub_metadata(name, value) VALUES ('service_id', $value);", transaction))
        {
            createService.Parameters.AddWithValue("$value", Guid.NewGuid().ToString("N"));
            createService.ExecuteNonQuery();
        }
        using (var readService = Command("SELECT value FROM hub_metadata WHERE name='service_id';", transaction))
            ServiceId = (string)readService.ExecuteScalar()!;
        IncarnationId = Guid.NewGuid().ToString("N");
        using (var incarnation = Command("INSERT INTO hub_metadata(name, value) VALUES ('incarnation_id', $value) ON CONFLICT(name) DO UPDATE SET value=excluded.value;", transaction))
        {
            incarnation.Parameters.AddWithValue("$value", IncarnationId);
            incarnation.ExecuteNonQuery();
        }
        // The backfill shares the migration transaction, so a database is never left at version 2 with a half-built index.
        PrepareSearchIndex(transaction);
        transaction.Commit();
    }

    /// <summary>Fails with a clear message when this host's SQLite build lacks a required virtual-table module, before anything is migrated.</summary>
    internal static void RequireModule(SqliteConnection connection, string module)
    {
        using var probe = connection.CreateCommand();
        // The module name is a code constant, never caller input.
        probe.CommandText = $"CREATE VIRTUAL TABLE temp.pkrelay_module_probe USING {module}(probe); DROP TABLE temp.pkrelay_module_probe;";
        try { probe.ExecuteNonQuery(); }
        catch (SqliteException exception)
        {
            throw new InvalidOperationException($"The SQLite library loaded by this hub lacks the {module.ToUpperInvariant()} module, which PkRelay requires for search. Use a SQLite build with {module.ToUpperInvariant()} enabled; the database was not changed.", exception);
        }
    }

    internal async Task<StoredState?> ReadAsync(string stateName, GrainId grainId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = KeyCommand("SELECT etag, payload FROM grain_state WHERE state_name=$state AND grain_type=$type AND grain_key=$key;", stateName, grainId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Commits a grain's state, its due-work index and its search rows in one transaction. <paramref name="search"/> describes the
    /// state's searchable text; a cleared state (null <paramref name="payload"/>) deletes the grain's search rows.
    /// </summary>
    internal async Task<string> CommitAsync(string stateName, GrainId grainId, string? expectedEtag, string? payload, IReadOnlyList<DueWorkItem> dueWork, CancellationToken cancellationToken, IHasSearchDocuments? search = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var transaction = _connection.BeginTransaction(deferred: false);
            string? storedEtag = null;
            long generation = 0;
            using (var read = KeyCommand("SELECT etag, generation FROM grain_state WHERE state_name=$state AND grain_type=$type AND grain_key=$key;", stateName, grainId, transaction))
            using (var reader = read.ExecuteReader())
            {
                if (reader.Read())
                {
                    storedEtag = reader.GetString(0);
                    generation = reader.GetInt64(1);
                }
            }
            expectedEtag = string.IsNullOrEmpty(expectedEtag) ? null : expectedEtag;
            if (!string.Equals(expectedEtag, storedEtag, StringComparison.Ordinal))
                throw new InconsistentStateException("The grain state changed; reload before retrying.", storedEtag!, expectedEtag!);
            var etag = Guid.NewGuid().ToString("N");
            using (var write = KeyCommand("""
                INSERT INTO grain_state(state_name, grain_type, grain_key, generation, etag, payload)
                VALUES ($state, $type, $key, $generation, $etag, $payload)
                ON CONFLICT(state_name, grain_type, grain_key) DO UPDATE SET generation=excluded.generation, etag=excluded.etag, payload=excluded.payload;
                """, stateName, grainId, transaction))
            {
                write.Parameters.AddWithValue("$generation", checked(generation + 1));
                write.Parameters.AddWithValue("$etag", etag);
                write.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                write.ExecuteNonQuery();
            }
            using (var deleteDue = KeyCommand("DELETE FROM due_work WHERE state_name=$state AND grain_type=$type AND grain_key=$key;", stateName, grainId, transaction))
                deleteDue.ExecuteNonQuery();
            foreach (var work in dueWork)
            {
                using var due = KeyCommand("INSERT INTO due_work(state_name, grain_type, grain_key, work_id, kind, due_ticks) VALUES ($state, $type, $key, $id, $kind, $ticks);", stateName, grainId, transaction);
                due.Parameters.AddWithValue("$id", work.Id);
                due.Parameters.AddWithValue("$kind", work.Kind);
                due.Parameters.AddWithValue("$ticks", work.DueAt.UtcTicks);
                due.ExecuteNonQuery();
            }
            var indexedSource = WriteSearch(transaction, stateName, grainId, expectedEtag, payload, search);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            RememberSearch(indexedSource, stateName, grainId, etag);
            return etag;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DueWorkReference>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default, int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = Command("SELECT state_name, grain_type, grain_key, work_id, kind, due_ticks FROM due_work WHERE due_ticks <= $now ORDER BY due_ticks, state_name, grain_type, grain_key, work_id LIMIT $limit OFFSET $offset;");
            command.Parameters.AddWithValue("$now", now.UtcTicks);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            var result = new List<DueWorkReference>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero)));
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task BackupAsync(string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var target = Path.GetFullPath(destination);
        if (string.Equals(target, DatabasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Backup destination must differ from the live database.", nameof(destination));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var targetOwnership = AcquireOwnership(target);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            // Let SQLite replace the destination transactionally, including any old WAL frames.
            // Renaming a database file over an old snapshot can leave its old WAL alongside it.
            using var backup = new SqliteConnection(ConnectionString(target));
            backup.Open();
            _connection.BackupDatabase(backup);
        }
        finally { _gate.Release(); }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private SqliteCommand KeyCommand(string sql, string stateName, GrainId grainId, SqliteTransaction? transaction = null)
    {
        var command = Command(sql, transaction);
        command.Parameters.AddWithValue("$state", stateName);
        command.Parameters.AddWithValue("$type", grainId.Type.ToString());
        command.Parameters.AddWithValue("$key", grainId.Key.ToString());
        return command;
    }

    public void Dispose()
    {
        // Drain queued telemetry before closing; the writer takes the gate itself.
        StopActivity();
        CloseSearchConnection();
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { _connection.Dispose(); }
            finally { _ownership.Dispose(); }
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal sealed record StoredState(string Etag, string? Payload);
}

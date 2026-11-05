using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using Orleans.Storage;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Storage;

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
        // A folder this creates is this user's only; an existing one (which may be shared on purpose) is left as it is.
        CreatePrivateDirectory(Path.GetDirectoryName(DatabasePath)!, tightenExisting: false);
        RestrictToOwner(DatabasePath);
        var existingDatabase = File.Exists(DatabasePath) && new FileInfo(DatabasePath).Length != 0;
        _ownership = AcquireOwnership(DatabasePath);
        _connection = new SqliteConnection(ConnectionString(DatabasePath));
        try
        {
            _connection.Open();
            Initialize(existingDatabase, searchModule);
            CompactPendingCoordinationReset();
            // New -wal/-shm files inherit the database's mode; ones left by an older hub are tightened here.
            RestrictToOwner(DatabasePath + "-wal", createIfMissing: false);
            RestrictToOwner(DatabasePath + "-shm", createIfMissing: false);
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

    /// <summary>The database holds session tokens and messages, so on Unix it is created, or tightened to, owner read/write only; Windows keeps its inherited ACL.</summary>
    /// <summary>Creates the folder owner-only on Unix (Windows keeps its inherited ACL); <paramref name="tightenExisting"/> also tightens one already there, best effort.</summary>
    internal static void CreatePrivateDirectory(string path, bool tightenExisting)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); return; }
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!Directory.Exists(path)) { Directory.CreateDirectory(path, owner); return; }
        if (!tightenExisting) return;
        try { if ((File.GetUnixFileMode(path) & ~owner) != 0) File.SetUnixFileMode(path, owner); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
    }

    internal static void RestrictToOwner(string path, bool createIfMissing = true)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (!File.Exists(path))
        {
            if (!createIfMissing) return;
            try { using var _ = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = owner }); }
            catch (IOException) when (File.Exists(path)) { }
        }
        // Tightening is best effort: a file this user can use but not chmod (another owner, a read-only mount) must not stop the hub.
        try { if ((File.GetUnixFileMode(path) & ~owner) != 0) File.SetUnixFileMode(path, owner); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
    }

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
            var backupPath = DatabasePath + ".pre-migration-" + Guid.NewGuid().ToString("N") + ".db";
            RestrictToOwner(backupPath);
            using var backup = new SqliteConnection(ConnectionString(backupPath));
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
                closed INTEGER NOT NULL DEFAULT 0,
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
            """ + ActivitySchema + SearchSchema + ArchiveSchema + """
            CREATE VIRTUAL TABLE IF NOT EXISTS search_index USING fts5(
                state_name UNINDEXED, grain_key UNINDEXED, doc_id UNINDEXED,
                kind UNINDEXED, repository_id UNINDEXED, owner_session UNINDEXED,
                status UNINDEXED, updated_ticks UNINDEXED,
                title, body,
                tokenize = 'porter unicode61');
            PRAGMA user_version=2;
            """, transaction))
            schema.ExecuteNonQuery();
        AddLateActivityColumns(transaction);
        AddClosedColumn(transaction);
        ApplyPendingCoordinationReset(transaction);
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

    /// <summary>
    /// The dashboard lists open sessions by <c>grain_state.closed</c>, which each commit sets from the Domain's rule
    /// (<see cref="ReadModels.IsClosed(string)"/>), instead of searching each record's JSON for expiredAt, which also matched one inside a
    /// client's binding. A database from before the column gets it here, set for each stored session by the same rule. The index lets the
    /// list skip closed records, which are kept and pile up, without reading them.
    /// </summary>
    private void AddClosedColumn(SqliteTransaction transaction)
    {
        bool present;
        using (var column = Command("SELECT 1 FROM pragma_table_info('grain_state') WHERE name='closed';", transaction))
            present = column.ExecuteScalar() is not null;
        if (!present)
        {
            using (var alter = Command("ALTER TABLE grain_state ADD COLUMN closed INTEGER NOT NULL DEFAULT 0;", transaction))
                alter.ExecuteNonQuery();
            var closed = new List<(string Type, string Key)>();
            using (var rows = Command("SELECT grain_type, grain_key, payload FROM grain_state WHERE state_name=$session AND payload IS NOT NULL;", transaction))
            {
                rows.Parameters.AddWithValue("$session", AggregateKinds.Session);
                using var reader = rows.ExecuteReader();
                while (reader.Read())
                    if (StateDocument(reader.GetString(2)) is { } state && ReadModels.IsClosed(state)) closed.Add((reader.GetString(0), reader.GetString(1)));
            }
            foreach (var (type, key) in closed)
            {
                using var mark = Command("UPDATE grain_state SET closed=1 WHERE state_name=$session AND grain_type=$type AND grain_key=$key;", transaction);
                mark.Parameters.AddWithValue("$session", AggregateKinds.Session);
                mark.Parameters.AddWithValue("$type", type);
                mark.Parameters.AddWithValue("$key", key);
                mark.ExecuteNonQuery();
            }
        }
        using var index = Command("CREATE INDEX IF NOT EXISTS ix_grain_state_closed ON grain_state(state_name, grain_type, closed, grain_key);", transaction);
        index.ExecuteNonQuery();
    }

    /// <summary>Fails with a clear message when this host's SQLite build lacks a required virtual-table module, before anything is migrated.</summary>
    internal static void RequireModule(SqliteConnection connection, string module)
    {
        using var probe = connection.CreateCommand();
        // The module name is a code constant, never caller input.
        probe.CommandText = $"CREATE VIRTUAL TABLE temp.cocern_module_probe USING {module}(probe); DROP TABLE temp.cocern_module_probe;";
        try { probe.ExecuteNonQuery(); }
        catch (SqliteException exception)
        {
            throw new InvalidOperationException($"The SQLite library loaded by this hub lacks the {module.ToUpperInvariant()} module, which CoCern requires for search. Use a SQLite build with {module.ToUpperInvariant()} enabled; the database was not changed.", exception);
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
    /// state's searchable text; a cleared state (null <paramref name="payload"/>) deletes the grain's search rows. <paramref name="closed"/>
    /// marks a session's record as closed (see <see cref="AddClosedColumn"/>).
    /// </summary>
    internal async Task<string> CommitAsync(string stateName, GrainId grainId, string? expectedEtag, string? payload, IReadOnlyList<DueWorkItem> dueWork, CancellationToken cancellationToken, IHasSearchDocuments? search = null,
        IReadOnlyList<ArchivedTask>? archived = null, bool closed = false)
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
                INSERT INTO grain_state(state_name, grain_type, grain_key, generation, etag, payload, closed)
                VALUES ($state, $type, $key, $generation, $etag, $payload, $closed)
                ON CONFLICT(state_name, grain_type, grain_key) DO UPDATE SET generation=excluded.generation, etag=excluded.etag, payload=excluded.payload, closed=excluded.closed;
                """, stateName, grainId, transaction))
            {
                write.Parameters.AddWithValue("$generation", checked(generation + 1));
                write.Parameters.AddWithValue("$etag", etag);
                write.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                write.Parameters.AddWithValue("$closed", closed ? 1 : 0);
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
            // Tasks leaving the board in this commit: their archive rows commit or roll back with it. Their search rows are marked
            // archived first, so the index update below keeps them although the board no longer holds the tasks.
            if (archived is { Count: > 0 }) WriteArchive(transaction, stateName, grainId, archived);
            var indexedSource = WriteSearch(transaction, stateName, grainId, expectedEtag, payload, search);
            ArchiveFault?.Invoke();
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

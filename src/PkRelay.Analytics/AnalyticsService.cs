using System.Data;
using System.Numerics;
using System.Text.Json;
using DuckDB.NET.Data;
using PkRelay.Contracts;
using PkRelay.Core;

namespace PkRelay.Analytics;

/// <summary>
/// Read-only DuckDB queries over the latest analytics snapshot. Each SQLite snapshot is copied into a DuckDB file with the sqlite
/// extension, and gets its own in-memory query database without that extension: the copy is attached read-only, the views are
/// created, and then file access is turned off and the configuration locked, so a query can neither read other files nor undo those
/// settings. Each query runs on its own connection off the caller's thread, with a timeout and a row cap. A replaced snapshot is
/// closed and its file deleted once its last query finishes.
/// </summary>
public sealed class AnalyticsService(AnalyticsOptions options) : IAnalyticsQueries, IDisposable
{
    private static readonly Lazy<string> Views = new(() =>
    {
        using var stream = typeof(AnalyticsService).Assembly.GetManifestResourceStream("PkRelay.Analytics.Views.sql")
            ?? throw new InvalidOperationException("The analytics views are missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly AnalyticsOptions _options = options.Validated();
    private readonly Lock _sync = new();
    private Snapshot? _current;
    private bool _disposed;

    public IReadOnlyList<string> QueryNames => NamedQueries.Names;

    /// <summary>When the loaded snapshot was taken, or null before the first one.</summary>
    public DateTimeOffset? SnapshotAt { get { lock (_sync) return _current?.TakenAt; } }

    /// <summary>The DuckDB copy of the loaded snapshot, or null before the first one.</summary>
    public string? SnapshotPath { get { lock (_sync) return _current?.Path; } }

    /// <summary>
    /// Loads the SQLite snapshot at <paramref name="path"/> and makes it the one queries read. The service then owns the file: it is
    /// replaced by its DuckDB copy at once, the previous snapshot's copy is deleted once no query uses it, and this one when it is
    /// replaced in turn. On failure the current snapshot stays.
    /// </summary>
    public Task SwitchSnapshotAsync(string path, DateTimeOffset? takenAt = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        if (_options.LiveDatabasePath is { } live && string.Equals(full, System.IO.Path.GetFullPath(live), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Analytics reads snapshots only, never the live database.", nameof(path));
        if (!File.Exists(full)) throw new FileNotFoundException("The analytics snapshot does not exist.", full);
        var at = takenAt ?? DateTimeOffset.UtcNow;
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = Snapshot.Open(full, at, _options);
            Snapshot? previous;
            lock (_sync)
            {
                if (_disposed) { next.Release(); throw new ObjectDisposedException(nameof(AnalyticsService)); }
                previous = _current;
                _current = next;
            }
            previous?.Release();
        }, cancellationToken);
    }

    public Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default)
    {
        if (!NamedQueries.TryGet(name ?? "", out var sql)) throw new AnalyticsException(AnalyticsError.UnknownQuery, $"Unknown query. Known queries: {string.Join(", ", NamedQueries.Names)}.");
        if (string.IsNullOrEmpty(repositoryId)) throw new AnalyticsException(AnalyticsError.InvalidArguments, "A repository is required.");
        var days = Days(parameters);
        var snapshot = Acquire();
        try
        {
            var now = snapshot.TakenAt.UtcTicks;
            var period = TimeSpan.FromDays(days).Ticks;
            DuckDBParameter[] bound = [new("repository", repositoryId), new("now", now), new("from", now - period), new("prev", now - 2 * period)];
            return RunAsync(snapshot, sql, bound, checkSelect: false, cancellationToken);
        }
        catch { snapshot.Release(); throw; }
    }

    public Task<AnalyticsResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > RelayLimits.MaxAnalyticsSqlLength || sql.Contains('\0'))
            throw new AnalyticsException(AnalyticsError.InvalidArguments, $"SQL must be 1 to {RelayLimits.MaxAnalyticsSqlLength} characters.");
        return RunAsync(Acquire(), sql, [], checkSelect: true, cancellationToken);
    }

    /// <summary>Runs SQL without the one-SELECT check. Tests use it to show the locked settings hold even without that check.</summary>
    internal Task<AnalyticsResult> RunUncheckedAsync(string sql, CancellationToken cancellationToken = default) =>
        RunAsync(Acquire(), sql, [], checkSelect: false, cancellationToken, bounded: false);

    private static int Days(JsonElement parameters)
    {
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return RelayLimits.DefaultAnalyticsPeriodDays;
        if (parameters.ValueKind != JsonValueKind.Object) throw new AnalyticsException(AnalyticsError.InvalidArguments, "params must be an object.");
        var days = RelayLimits.DefaultAnalyticsPeriodDays;
        foreach (var property in parameters.EnumerateObject())
        {
            if (property.Name != "days") throw new AnalyticsException(AnalyticsError.InvalidArguments, $"Unknown parameter. Named queries accept: {string.Join(", ", NamedQueries.Parameters)}.");
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out days) || days is < 1 or > RelayLimits.MaxAnalyticsPeriodDays)
                throw new AnalyticsException(AnalyticsError.InvalidArguments, $"days must be a whole number from 1 to {RelayLimits.MaxAnalyticsPeriodDays}.");
        }
        return days;
    }

    /// <summary>Takes a reference on the current snapshot synchronously, so a switch that follows cannot delete it under this query.</summary>
    private Snapshot Acquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is not { } snapshot || !snapshot.TryAddReference())
                throw new AnalyticsException(AnalyticsError.Unavailable, "No analytics snapshot is loaded yet.");
            return snapshot;
        }
    }

    /// <summary>Runs one query on its own connection on the thread pool, never on the caller's (Orleans) scheduler. Releases the reference.</summary>
    private async Task<AnalyticsResult> RunAsync(Snapshot snapshot, string sql, DuckDBParameter[] parameters, bool checkSelect, CancellationToken cancellationToken, bool bounded = true)
    {
        try { return await Task.Run(() => Execute(snapshot, sql, parameters, checkSelect, bounded, cancellationToken), CancellationToken.None); }
        finally { snapshot.Release(); }
    }

    private AnalyticsResult Execute(Snapshot snapshot, string sql, DuckDBParameter[] parameters, bool checkSelect, bool bounded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = snapshot.Connect();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.QueryTimeout);
        try
        {
            if (checkSelect) sql = RequireOneSelect(connection, sql, timeout.Token);
            // A bounded query carries one more column, its row's size in bytes computed inside DuckDB, so a huge value is refused before
            // it is copied into this process.
            using var command = connection.CreateCommand();
            command.CommandText = bounded ? Bounded(connection, sql, parameters, timeout.Token) : sql;
            command.UseStreamingMode = true;
            foreach (var parameter in parameters) command.Parameters.Add(new DuckDBParameter(parameter.ParameterName, parameter.Value));
            // Cancel interrupts the running query in DuckDB.
            using var interrupt = timeout.Token.Register(() => { try { command.Cancel(); } catch (Exception) { } });
            using var reader = command.ExecuteReader();
            var width = bounded ? reader.FieldCount - 1 : reader.FieldCount;
            var columns = Enumerable.Range(0, width).Select(reader.GetName).ToArray();
            var rows = new List<IReadOnlyList<object?>>();
            var truncated = false;
            long bytes = 0;
            while (reader.Read())
            {
                if (rows.Count == _options.MaxRows) { truncated = true; break; }
                if (bounded && (bytes += reader.GetInt64(width)) > RelayLimits.MaxAnalyticsReplyBytes)
                    throw new AnalyticsException(AnalyticsError.TooLarge, $"The result is larger than {RelayLimits.MaxAnalyticsReplyBytes} bytes; ask a narrower query (fewer rows or columns, shorter values).");
                var row = new object?[columns.Length];
                for (var index = 0; index < row.Length; index++) row[index] = reader.IsDBNull(index) ? null : Plain(reader.GetValue(index));
                rows.Add(row);
            }
            timeout.Token.ThrowIfCancellationRequested();
            return new(columns, rows, truncated, snapshot.TakenAt);
        }
        catch (Exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AnalyticsException(AnalyticsError.Timeout, $"The query ran longer than {_options.QueryTimeout.TotalSeconds:0.#} seconds. Ask a narrower question.");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (DuckDBException exception) { throw new AnalyticsException(AnalyticsError.Failed, FirstLine(exception.Message), exception); }
    }

    private const string RowBytesColumn = "__pkrelay_row_bytes";

    /// <summary>
    /// Wraps <paramref name="sql"/> so each row also reports its size: the byte length of each text value (read from DuckDB's string
    /// header, without copying), a BLOB's length, and the text length of any other value, plus a few bytes per value for the JSON around
    /// it. The column names and types come from a LIMIT 0 run of the same query.
    /// </summary>
    private static string Bounded(DuckDBConnection connection, string sql, DuckDBParameter[] parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Ad hoc SQL arrives here as DuckDB printed it back (one statement, no comments); the newline still guards a named query.
        var from = $"FROM ({sql}\n) pkrelay_q";
        using var probe = connection.CreateCommand();
        probe.CommandText = $"SELECT pkrelay_q.* {from} LIMIT 0";
        foreach (var parameter in parameters) probe.Parameters.Add(new DuckDBParameter(parameter.ParameterName, parameter.Value));
        using var interrupt = cancellationToken.Register(() => { try { probe.Cancel(); } catch (Exception) { } });
        using var reader = probe.ExecuteReader();
        var sizes = Enumerable.Range(0, reader.FieldCount).Select(index =>
        {
            var column = "pkrelay_q.\"" + reader.GetName(index).Replace("\"", "\"\"") + "\"";
            var length = reader.GetDataTypeName(index) switch
            {
                "Varchar" => $"strlen({column})",
                "Blob" => $"octet_length({column})",
                _ => $"strlen(CAST({column} AS VARCHAR))"
            };
            return $"coalesce({length}, 0) + 8";
        }).ToArray();
        return $"SELECT pkrelay_q.*, CAST({(sizes.Length == 0 ? "0" : string.Join(" + ", sizes))} AS BIGINT) AS {RowBytesColumn} {from}";
    }

    /// <summary>
    /// Accepts exactly one SELECT statement and returns it as DuckDB prints it back, without comments or a trailing semicolon, so it can
    /// be wrapped. DuckDB's parser decides: <c>json_serialize_sql</c> serializes SELECT statements only, so any write, ATTACH, SET,
    /// PRAGMA or COPY is refused before it runs.
    /// </summary>
    private static string RequireOneSelect(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT json_serialize_sql(" + Literal(sql) + ")";
        var serialized = (string)check.ExecuteScalar()!;
        using var parsed = JsonDocument.Parse(serialized);
        var root = parsed.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            var message = root.TryGetProperty("error_message", out var text) ? text.GetString() ?? "" : "";
            if (message.Contains("Only SELECT", StringComparison.Ordinal)) throw new AnalyticsException(AnalyticsError.Refused, "Only one SELECT statement is allowed.");
            throw new AnalyticsException(AnalyticsError.Failed, FirstLine(message));
        }
        if (!root.TryGetProperty("statements", out var statements) || statements.GetArrayLength() != 1)
            throw new AnalyticsException(AnalyticsError.Refused, "Only one SELECT statement is allowed.");
        using var canonical = connection.CreateCommand();
        canonical.CommandText = "SELECT json_deserialize_sql(" + Literal(serialized) + ")";
        return (string)canonical.ExecuteScalar()!;
    }

    /// <summary>A DuckDB value as a plain JSON-friendly value: bool, long, double (3 decimals), string or null.</summary>
    internal static object? Plain(object? value) => value switch
    {
        null or DBNull => null,
        bool or string or long => value,
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value),
        ulong unsigned => unsigned <= long.MaxValue ? (long)unsigned : unsigned.ToString(),
        BigInteger big => big >= long.MinValue && big <= long.MaxValue ? (long)big : big.ToString(),
        float single => Number(single),
        double number => Number(number),
        decimal number => Math.Round((double)number, 3),
        DateTime time => DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("O"),
        DateTimeOffset time => time.ToUniversalTime().ToString("O"),
        DateOnly day => day.ToString("yyyy-MM-dd"),
        TimeSpan span => span.ToString("c"),
        _ => Serialized(value)
    };

    private static object Number(double value) => double.IsFinite(value) ? Math.Round(value, 3) : value.ToString();

    private static string Serialized(object value)
    {
        try { return JsonSerializer.Serialize(value); }
        catch (Exception) { return value.ToString() ?? ""; }
    }

    internal static string Literal(string text) => "'" + text.Replace("'", "''") + "'";

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();

    public void Dispose()
    {
        Snapshot? current;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            current = _current;
            _current = null;
        }
        current?.Release();
    }

    /// <summary>One loaded snapshot: its own in-memory DuckDB database, reference-counted by running queries.</summary>
    private sealed class Snapshot
    {
        private readonly DuckDBConnection _root;
        private readonly Lock _sync = new();
        private int _references = 1;

        private Snapshot(DuckDBConnection root, string path, DateTimeOffset takenAt) { _root = root; Path = path; TakenAt = takenAt; }

        public string Path { get; }
        public DateTimeOffset TakenAt { get; }

        /// <summary>The tables copied out of the SQLite snapshot. Everything the views read is here.</summary>
        private static readonly string[] Tables = ["grain_state", "due_work", "activity_log", "activity_daily"];

        /// <summary>
        /// Copies the SQLite snapshot at <paramref name="path"/> into a DuckDB file beside it and deletes the SQLite copy, then opens a
        /// query database on that file. The query database never loads the sqlite extension: its <c>sqlite_scan</c> reads any SQLite
        /// file even with file access turned off, so it must not be available to queries.
        /// </summary>
        public static Snapshot Open(string path, DateTimeOffset takenAt, AnalyticsOptions options)
        {
            var converted = path + ".duckdb";
            Convert(path, converted, options);
            DeleteFiles(path, exceptPrefix: converted);
            var root = new DuckDBConnection("DataSource=:memory:");
            try
            {
                root.Open();
                Run(root, Limits);
                Run(root, $"ATTACH {Literal(converted)} AS relay (READ_ONLY);");
                Run(root, Views.Value);
                // After this no query can read or write another file, attach a database, load an extension or change a setting.
                Run(root, "SET enable_external_access = false; SET lock_configuration = true;");
                return new(root, converted, takenAt);
            }
            catch
            {
                root.Dispose();
                DeleteFiles(converted);
                throw;
            }
        }

        private static readonly string Limits = $"""
            SET autoinstall_known_extensions = false;
            SET autoload_known_extensions = false;
            SET allow_community_extensions = false;
            SET memory_limit = '{RelayLimits.AnalyticsMemoryLimitMegabytes}MB';
            SET temp_directory = '';
            SET threads = {RelayLimits.AnalyticsThreads};
            """;

        /// <summary>Copies the snapshot's tables with the preinstalled sqlite extension, in a short-lived DuckDB database of its own.</summary>
        private static void Convert(string source, string target, AnalyticsOptions options)
        {
            DeleteFiles(target);
            using var builder = new DuckDBConnection("DataSource=:memory:");
            builder.Open();
            Run(builder, $"SET extension_directory = {Literal(System.IO.Path.GetFullPath(options.ExtensionDirectory))};" + Limits);
            try { Run(builder, "LOAD sqlite;"); }
            catch (DuckDBException exception)
            {
                throw new AnalyticsException(AnalyticsError.Unavailable,
                    $"The DuckDB sqlite extension is not installed in {options.ExtensionDirectory}. Run 'PkRelay.Host --install-analytics-extension <folder>' once with internet access, or copy a folder made that way.", exception);
            }
            try
            {
                Run(builder, $"ATTACH {Literal(source)} AS source (TYPE sqlite, READ_ONLY); ATTACH {Literal(target)} AS relay;");
                foreach (var table in Tables) Run(builder, $"CREATE TABLE relay.{table} AS SELECT * FROM source.{table};");
                Run(builder, "DETACH relay; DETACH source;");
            }
            catch
            {
                builder.Close();
                DeleteFiles(target);
                throw;
            }
        }

        /// <summary>Deletes a file and its side files (journal, WAL, lock), skipping names that start with <paramref name="exceptPrefix"/>.</summary>
        public static void DeleteFiles(string path, string? exceptPrefix = null)
        {
            var directory = System.IO.Path.GetDirectoryName(path)!;
            var keep = exceptPrefix is null ? null : System.IO.Path.GetFileName(exceptPrefix);
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, System.IO.Path.GetFileName(path) + "*"))
                {
                    if (keep is not null && System.IO.Path.GetFileName(file).StartsWith(keep, StringComparison.Ordinal)) continue;
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
        }

        private static void Run(DuckDBConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_references == 0) return false;
                _references++;
                return true;
            }
        }

        /// <summary>A new connection to this snapshot's database. The caller disposes it.</summary>
        public DuckDBConnection Connect()
        {
            lock (_sync)
            {
                var connection = _root.Duplicate();
                if (connection.State != ConnectionState.Open) connection.Open();
                return connection;
            }
        }

        /// <summary>Drops one reference. The last one closes the database and deletes the snapshot file and its side files.</summary>
        public void Release()
        {
            lock (_sync)
            {
                if (_references == 0 || --_references > 0) return;
                _root.Dispose();
            }
            DeleteFiles(Path);
        }
    }
}

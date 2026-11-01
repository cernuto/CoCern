using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Storage;

/// <summary>Search settings. Search is on by default: it only reads committed state and changes no reply.</summary>
public sealed record SearchOptions
{
    /// <summary>Keep <c>search_index</c> in step with grain state. Turning it off empties the index; turning it on again rebuilds it at startup.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Full-text search over committed grain state. Index rows are written inside <see cref="CommitAsync"/>, in the grain's own transaction,
/// so search never disagrees with committed state. Each row of the FTS5 table <c>search_index</c> has a row in <c>search_docs</c>, keyed
/// by (grain, kind, doc_id), holding its FTS rowid and the change marker it was built from; a commit compares the state's markers with
/// these and rewrites only the documents that changed. Queries use their own read-only connection, which under WAL reads the last
/// committed snapshot without waiting on the writer.
/// </summary>
public sealed partial class SqliteStore : IFullTextSearch
{
    /// <summary>Bump when extraction rules change: the next start rebuilds the index from grain state.</summary>
    private const string SearchIndexVersion = "1";
    private const string SearchVersionKey = "search_index_version";

    // FTS5 cannot index its UNINDEXED columns, so keyed deletes go through this table instead of scanning the index.
    private const string SearchSchema = """
        CREATE TABLE IF NOT EXISTS search_docs (
            id INTEGER PRIMARY KEY,
            state_name TEXT NOT NULL,
            grain_type TEXT NOT NULL,
            grain_key TEXT NOT NULL,
            kind TEXT NOT NULL,
            doc_id TEXT NOT NULL,
            marker TEXT NOT NULL,
            UNIQUE (state_name, grain_type, grain_key, kind, doc_id),
            FOREIGN KEY (state_name, grain_type, grain_key) REFERENCES grain_state(state_name, grain_type, grain_key)
        );

        """;

    private SearchOptions _searchOptions = new();
    private SqliteConnection? _searchConnection;
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    // The state JSON instance each grain's index was last built from, with the etag that commit produced. A later commit of the same
    // instance under that etag only changed the outbox or timers, so its index is already current.
    private readonly ConditionalWeakTable<object, IndexedState> _indexed = new();

    private sealed record IndexedState(string StateName, string GrainType, string GrainKey, string Etag);

    /// <summary>Whether this hub keeps the search index; when off, <see cref="SearchAsync"/> finds nothing.</summary>
    public bool SearchEnabled => _searchOptions.Enabled;

    /// <summary>Tests hold the writer's gate through this to show that searches do not wait on it.</summary>
    internal SemaphoreSlim WriterGate => _gate;

    /// <summary>
    /// Runs inside the startup migration transaction. With search on and the index not built by this version (a version 1 upgrade, a
    /// version 2 database whose index was never filled, or new extraction rules), rebuilds it from every board and session row. With
    /// search off, empties it, so turning search on later rebuilds it.
    /// </summary>
    private void PrepareSearchIndex(SqliteTransaction transaction)
    {
        string? built;
        using (var read = Command("SELECT value FROM hub_metadata WHERE name=$name;", transaction))
        {
            read.Parameters.AddWithValue("$name", SearchVersionKey);
            built = read.ExecuteScalar() as string;
        }
        if (_searchOptions.Enabled && built == SearchIndexVersion) return;
        if (!_searchOptions.Enabled && built is null) return;
        using (var clear = Command("DELETE FROM search_index; DELETE FROM search_docs;", transaction))
            clear.ExecuteNonQuery();
        if (!_searchOptions.Enabled)
        {
            using var forget = Command("DELETE FROM hub_metadata WHERE name=$name;", transaction);
            forget.Parameters.AddWithValue("$name", SearchVersionKey);
            forget.ExecuteNonQuery();
            return;
        }
        using var writer = new SearchWriter(this, transaction);
        using (var rows = Command("SELECT state_name, grain_type, grain_key, payload FROM grain_state WHERE state_name IN ($board, $session) AND payload IS NOT NULL;", transaction))
        {
            rows.Parameters.AddWithValue("$board", AggregateKinds.Board);
            rows.Parameters.AddWithValue("$session", AggregateKinds.Session);
            using var reader = rows.ExecuteReader();
            while (reader.Read())
            {
                var stateName = reader.GetString(0);
                if (StateDocument(reader.GetString(3)) is not { } state) continue;
                foreach (var entry in SearchExtraction.Update(stateName, state, Empty).Upserts)
                    writer.Upsert(stateName, reader.GetString(1), reader.GetString(2), entry);
            }
        }
        using var record = Command("INSERT INTO hub_metadata(name, value) VALUES ($name, $value) ON CONFLICT(name) DO UPDATE SET value=excluded.value;", transaction);
        record.Parameters.AddWithValue("$name", SearchVersionKey);
        record.Parameters.AddWithValue("$value", SearchIndexVersion);
        record.ExecuteNonQuery();
    }

    private static readonly IReadOnlyDictionary<SearchDocumentKey, string> Empty = new Dictionary<SearchDocumentKey, string>();

    /// <summary>The aggregate document inside a stored envelope (<c>Value.Json</c>), or null when the row holds something else.</summary>
    private static JsonObject? StateDocument(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) is JsonObject envelope && envelope["Value"]?["Json"] is JsonValue value && value.TryGetValue<string>(out var json)
                ? JsonNode.Parse(json) as JsonObject
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Brings one grain's index rows in step with the state being committed, inside its transaction. A cleared state (no payload) or a
    /// state without search documents removes the grain's rows. Returns what to remember once the transaction commits.
    /// </summary>
    private object? WriteSearch(SqliteTransaction transaction, string stateName, GrainId grainId, string? expectedEtag, string? payload, IHasSearchDocuments? search)
    {
        if (!_searchOptions.Enabled || !SearchExtraction.IsIndexed(stateName)) return null;
        var type = grainId.Type.ToString();
        var key = grainId.Key.ToString();
        using var writer = new SearchWriter(this, transaction);
        if (payload is null || search is null)
        {
            writer.RemoveGrain(stateName, type, key);
            return null;
        }
        var source = search.GetSearchSource();
        if (source is not null && expectedEtag is not null && _indexed.TryGetValue(source, out var indexed)
            && indexed == new IndexedState(stateName, type, key, expectedEtag))
            return source;
        var update = search.GetSearchUpdate(stateName, writer.Markers(stateName, type, key));
        foreach (var removed in update.Removed) writer.Remove(stateName, type, key, removed);
        foreach (var entry in update.Upserts) writer.Upsert(stateName, type, key, entry);
        return source;
    }

    private void RememberSearch(object? source, string stateName, GrainId grainId, string etag)
    {
        if (source is not null) _indexed.AddOrUpdate(source, new IndexedState(stateName, grainId.Type.ToString(), grainId.Key.ToString(), etag));
    }

    private void OpenSearchConnection()
    {
        _searchConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        _searchConnection.Open();
        using var pragmas = _searchConnection.CreateCommand();
        pragmas.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
        pragmas.ExecuteNonQuery();
    }

    private void CloseSearchConnection()
    {
        _searchGate.Wait();
        try { _searchConnection?.Dispose(); _searchConnection = null; }
        finally { _searchGate.Release(); }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string repositoryId, string sessionId, string query, IReadOnlyList<string> kinds, int limit,
        string? status = null, CancellationToken ct = default)
    {
        if (!_searchOptions.Enabled || string.IsNullOrEmpty(repositoryId) || Phrases(query) is not { Count: > 0 } phrases) return [];
        limit = Math.Clamp(limit, 1, RelayLimits.MaxSearchResults);
        var wanted = kinds.Where(kind => SearchKinds.All.Contains(kind)).Distinct(StringComparer.Ordinal).ToArray();
        await _searchGate.WaitAsync(ct);
        try
        {
            if (_searchConnection is not { } connection) return [];
            var hits = new List<SearchHit>();
            var seen = new HashSet<(string, string, string, string)>();
            // Every phrase first; only when that finds too few, any phrase, appended after the stronger hits.
            await ReadHitsAsync(connection, MatchAll(phrases), repositoryId, sessionId, wanted, status, limit, hits, seen, ct);
            if (hits.Count < limit && phrases.Count > 1)
                await ReadHitsAsync(connection, MatchAny(phrases), repositoryId, sessionId, wanted, status, limit, hits, seen, ct);
            return hits;
        }
        finally { _searchGate.Release(); }
    }

    /// <summary>Only documents with every phrase of the query, best first, with their relevance scores; no any-word fallback.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAllWordsAsync(string repositoryId, string sessionId, string query, IReadOnlyList<string> kinds, int limit, CancellationToken ct = default)
    {
        if (!_searchOptions.Enabled || string.IsNullOrEmpty(repositoryId) || Phrases(query) is not { Count: > 0 } phrases) return [];
        limit = Math.Clamp(limit, 1, RelayLimits.MaxSearchResults);
        var wanted = kinds.Where(kind => SearchKinds.All.Contains(kind)).Distinct(StringComparer.Ordinal).ToArray();
        await _searchGate.WaitAsync(ct);
        try
        {
            if (_searchConnection is not { } connection) return [];
            var hits = new List<SearchHit>();
            await ReadHitsAsync(connection, MatchAll(phrases), repositoryId, sessionId, wanted, null, limit, hits, [], ct);
            return hits;
        }
        finally { _searchGate.Release(); }
    }

    /// <summary>Appends the best hits for one match expression until <paramref name="limit"/>, skipping weak hits and ones already found.</summary>
    private static async Task ReadHitsAsync(SqliteConnection connection, string match, string repositoryId, string? sessionId, string[] kinds, string? status, int limit,
        List<SearchHit> hits, HashSet<(string, string, string, string)> seen, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT state_name, grain_key, doc_id, kind, status, updated_ticks,
                   snippet(search_index, 9, '[', ']', '…', 12) AS snip,
                   bm25(search_index, 0, 0, 0, 0, 0, 0, 0, 0, 4.0, 1.0) AS score
            FROM search_index
            WHERE search_index MATCH $match
              AND repository_id = $repository
              AND (owner_session IS NULL OR owner_session = $session)
            """);
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$repository", repositoryId);
        command.Parameters.AddWithValue("$session", sessionId ?? "");
        if (kinds.Length > 0)
        {
            sql.Append(" AND kind IN (").Append(string.Join(", ", kinds.Select((_, index) => "$kind" + index))).Append(')');
            for (var index = 0; index < kinds.Length; index++) command.Parameters.AddWithValue("$kind" + index, kinds[index]);
        }
        if (!string.IsNullOrEmpty(status))
        {
            sql.Append(" AND status = $status");
            command.Parameters.AddWithValue("$status", status);
        }
        sql.Append(" ORDER BY score LIMIT $rows;");
        command.Parameters.AddWithValue("$rows", RelayLimits.SearchCandidateRows);
        command.CommandText = sql.ToString();
        double? best = null;
        using var reader = await command.ExecuteReaderAsync(ct);
        while (hits.Count < limit && await reader.ReadAsync(ct))
        {
            var score = reader.GetDouble(7);
            best ??= score;
            // bm25 scores are negative, and lower is better. Hits far weaker than this query's best one are noise; rows come best first.
            if (best < 0 && score > best * RelayLimits.SearchMinRelativeScore) break;
            var (stateName, grainKey, docId, kind) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            // Each (grain, kind, doc_id) has one row, so this only drops what the AND pass already returned when the OR pass runs.
            if (!seen.Add((stateName, grainKey, kind, docId))) continue;
            hits.Add(new SearchHit(docId, kind, reader.GetString(4), new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero), reader.GetString(6), BoardId(stateName, grainKey), -score));
        }
    }

    /// <summary>
    /// The query's phrases: each whitespace-separated chunk, reduced to its runs of letters and digits, so <c>T-118</c> is the phrase
    /// <c>T 118</c> and <c>title:x</c> is <c>title x</c>. Only letters and digits survive, so operators (<c>AND</c>, <c>NEAR</c>), quotes,
    /// prefixes and column filters in user input are plain words. Repeated phrases are dropped (ignoring case), and at most
    /// <see cref="RelayLimits.MaxSearchQueryTerms"/> words are kept in all.
    /// </summary>
    internal static IReadOnlyList<string> Phrases(string? query)
    {
        var phrases = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return phrases;
        var words = 0;
        foreach (var chunk in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = new List<string>();
            var word = new StringBuilder();
            foreach (var rune in chunk.EnumerateRunes())
            {
                if (Rune.IsLetterOrDigit(rune)) { word.Append(rune.ToString()); continue; }
                if (word.Length > 0) { parts.Add(word.ToString()); word.Clear(); }
            }
            if (word.Length > 0) parts.Add(word.ToString());
            parts = parts.Take(RelayLimits.MaxSearchQueryTerms - words).ToList();
            if (parts.Count == 0) continue;
            var phrase = string.Join(' ', parts);
            if (!phrases.Contains(phrase, StringComparer.OrdinalIgnoreCase)) { phrases.Add(phrase); words += parts.Count; }
            if (words >= RelayLimits.MaxSearchQueryTerms) break;
        }
        return phrases;
    }

    /// <summary>FTS5 text matching every phrase (quoted phrases side by side are an implicit AND).</summary>
    internal static string MatchAll(IReadOnlyList<string> phrases) => string.Join(" ", phrases.Select(Quote));

    /// <summary>FTS5 text matching any phrase; bm25 ranks documents with more of them first.</summary>
    internal static string MatchAny(IReadOnlyList<string> phrases) => string.Join(" OR ", phrases.Select(Quote));

    // A phrase holds only letters, digits and single spaces, so quoting it needs no escaping.
    private static string Quote(string phrase) => "\"" + phrase + "\"";

    /// <summary>The board of a board hit when it is not <c>default</c>. Board grain keys are <c>escaped-repository/escaped-board</c>.</summary>
    private static string? BoardId(string stateName, string grainKey)
    {
        if (stateName != AggregateKinds.Board || grainKey.Split('/') is not [_, var escaped]) return null;
        var board = Uri.UnescapeDataString(escaped);
        return board == "default" ? null : board;
    }

    /// <summary>Prepared index commands for one transaction.</summary>
    private sealed class SearchWriter : IDisposable
    {
        private readonly SqliteCommand _side;
        private readonly SqliteCommand _index;

        public SearchWriter(SqliteStore store, SqliteTransaction transaction)
        {
            _side = store.Command("""
                INSERT INTO search_docs(state_name, grain_type, grain_key, kind, doc_id, marker) VALUES ($state, $type, $key, $kind, $doc, $marker)
                ON CONFLICT(state_name, grain_type, grain_key, kind, doc_id) DO UPDATE SET marker=excluded.marker
                RETURNING id;
                """, transaction);
            foreach (var name in new[] { "$state", "$type", "$key", "$kind", "$doc", "$marker" }) _side.Parameters.Add(name, SqliteType.Text);
            _index = store.Command("""
                DELETE FROM search_index WHERE rowid=$id;
                INSERT INTO search_index(rowid, state_name, grain_key, doc_id, kind, repository_id, owner_session, status, updated_ticks, title, body)
                VALUES ($id, $state, $key, $doc, $kind, $repository, $owner, $status, $ticks, $title, $body);
                """, transaction);
            _index.Parameters.Add("$id", SqliteType.Integer);
            foreach (var name in new[] { "$state", "$key", "$doc", "$kind", "$repository", "$owner", "$status", "$title", "$body" }) _index.Parameters.Add(name, SqliteType.Text);
            _index.Parameters.Add("$ticks", SqliteType.Integer);
            Transaction = transaction;
            Store = store;
        }

        private SqliteTransaction Transaction { get; }
        private SqliteStore Store { get; }

        public Dictionary<SearchDocumentKey, string> Markers(string stateName, string type, string key)
        {
            using var command = Store.Command("SELECT kind, doc_id, marker FROM search_docs WHERE state_name=$state AND grain_type=$type AND grain_key=$key;", Transaction);
            Bind(command, stateName, type, key);
            using var reader = command.ExecuteReader();
            var markers = new Dictionary<SearchDocumentKey, string>();
            while (reader.Read()) markers[new(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
            return markers;
        }

        public void Upsert(string stateName, string type, string key, SearchEntry entry)
        {
            var document = entry.Document;
            _side.Parameters["$state"].Value = stateName; _side.Parameters["$type"].Value = type; _side.Parameters["$key"].Value = key;
            _side.Parameters["$kind"].Value = document.Kind; _side.Parameters["$doc"].Value = document.DocId; _side.Parameters["$marker"].Value = entry.Marker;
            var id = Convert.ToInt64(_side.ExecuteScalar());
            _index.Parameters["$id"].Value = id; _index.Parameters["$state"].Value = stateName; _index.Parameters["$key"].Value = key;
            _index.Parameters["$doc"].Value = document.DocId; _index.Parameters["$kind"].Value = document.Kind; _index.Parameters["$repository"].Value = document.RepositoryId;
            _index.Parameters["$owner"].Value = (object?)document.OwnerSessionId ?? DBNull.Value; _index.Parameters["$status"].Value = document.Status;
            _index.Parameters["$ticks"].Value = document.UpdatedAt.UtcTicks; _index.Parameters["$title"].Value = document.Title; _index.Parameters["$body"].Value = document.Body;
            _index.ExecuteNonQuery();
        }

        public void Remove(string stateName, string type, string key, SearchDocumentKey document)
        {
            using var command = Store.Command("""
                DELETE FROM search_index WHERE rowid IN (SELECT id FROM search_docs WHERE state_name=$state AND grain_type=$type AND grain_key=$key AND kind=$kind AND doc_id=$doc);
                DELETE FROM search_docs WHERE state_name=$state AND grain_type=$type AND grain_key=$key AND kind=$kind AND doc_id=$doc;
                """, Transaction);
            Bind(command, stateName, type, key);
            command.Parameters.AddWithValue("$kind", document.Kind);
            command.Parameters.AddWithValue("$doc", document.DocId);
            command.ExecuteNonQuery();
        }

        public void RemoveGrain(string stateName, string type, string key)
        {
            using var command = Store.Command("""
                DELETE FROM search_index WHERE rowid IN (SELECT id FROM search_docs WHERE state_name=$state AND grain_type=$type AND grain_key=$key);
                DELETE FROM search_docs WHERE state_name=$state AND grain_type=$type AND grain_key=$key;
                """, Transaction);
            Bind(command, stateName, type, key);
            command.ExecuteNonQuery();
        }

        private static void Bind(SqliteCommand command, string stateName, string type, string key)
        {
            command.Parameters.AddWithValue("$state", stateName);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$key", key);
        }

        public void Dispose() { _side.Dispose(); _index.Dispose(); }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Microsoft.Data.Sqlite;
using static CoCern.Domain.Replies;

namespace CoCern.Storage;

public sealed partial class SqliteStore : ISessionContextCatalog
{
    private const string SessionContextSchema = """
        CREATE TABLE IF NOT EXISTS session_context_catalog (
            repository_id TEXT NOT NULL, context_id TEXT NOT NULL, revision INTEGER NOT NULL,
            title TEXT NOT NULL, summary TEXT NOT NULL, task_ids TEXT NOT NULL,
            updated_at TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(repository_id,context_id));
        CREATE INDEX IF NOT EXISTS ix_session_context_recent ON session_context_catalog(repository_id,deleted,updated_at DESC,context_id);
        CREATE TABLE IF NOT EXISTS session_context_catalog_revision(repository_id TEXT PRIMARY KEY,revision INTEGER NOT NULL);
        """;

    private void WriteSessionContextCatalog(SqliteTransaction transaction, SessionContextCatalogProjection? projection)
    {
        if (projection is null) return;
        using (var current = Command("SELECT revision,deleted FROM session_context_catalog WHERE repository_id=$repo AND context_id=$id;", transaction))
        {
            current.Parameters.AddWithValue("$repo", projection.RepositoryId); current.Parameters.AddWithValue("$id", projection.ContextId);
            using var reader = current.ExecuteReader();
            if (reader.Read() && reader.GetInt64(0) == projection.Revision && reader.GetInt32(1) == (projection.Deleted ? 1 : 0)) return;
        }
        using var write = Command("""
            INSERT INTO session_context_catalog(repository_id,context_id,revision,title,summary,task_ids,updated_at,deleted)
            VALUES($repo,$id,$revision,$title,$summary,$tasks,$at,$deleted)
            ON CONFLICT(repository_id,context_id) DO UPDATE SET revision=excluded.revision,title=excluded.title,
                summary=excluded.summary,task_ids=excluded.task_ids,updated_at=excluded.updated_at,deleted=excluded.deleted;
            """, transaction);
        write.Parameters.AddWithValue("$repo", projection.RepositoryId); write.Parameters.AddWithValue("$id", projection.ContextId);
        write.Parameters.AddWithValue("$revision", projection.Revision); write.Parameters.AddWithValue("$title", projection.Deleted ? "" : projection.Title);
        write.Parameters.AddWithValue("$summary", projection.Deleted ? "" : projection.Summary); write.Parameters.AddWithValue("$tasks", projection.Deleted ? "[]" : projection.TaskIdsJson);
        write.Parameters.AddWithValue("$at", projection.UpdatedAt); write.Parameters.AddWithValue("$deleted", projection.Deleted ? 1 : 0); write.ExecuteNonQuery();
        using var bump = Command("INSERT INTO session_context_catalog_revision(repository_id,revision) VALUES($repo,1) ON CONFLICT(repository_id) DO UPDATE SET revision=revision+1;", transaction);
        bump.Parameters.AddWithValue("$repo", projection.RepositoryId); bump.ExecuteNonQuery();
    }

    private sealed record ContextCursor(string Repository, string Task, string Incarnation, long Revision, int Offset);

    private static SessionContextCatalogProjection? ClearedSessionContext(string? payload)
    {
        if (payload is null) return null;
        var envelope = JsonSerializer.Deserialize<StateEnvelope<CoordinationDocument>>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid retained context state.");
        var projection = SessionContextReadModels.Catalog(envelope.Value.Json);
        return projection is null ? null : projection with { Deleted = true, Title = "", Summary = "", TaskIdsJson = "[]" };
    }

    public async Task<RelayReply> ReadSessionContextsAsync(string repositoryId, string? taskId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long revision;
            using (var version = Command("SELECT revision FROM session_context_catalog_revision WHERE repository_id=$repo;"))
            {
                version.Parameters.AddWithValue("$repo", repositoryId);
                revision = version.ExecuteScalar() is long stored ? stored : 0;
            }
            var offset = 0;
            if (!string.IsNullOrEmpty(cursor))
            {
                try
                {
                    if (cursor.Length > 2048) return Error(RelayErrorCodes.InvalidArguments);
                    var old = JsonSerializer.Deserialize<ContextCursor>(Convert.FromBase64String(cursor));
                    if (old is null || old.Repository != repositoryId || old.Task != (taskId ?? "") || old.Incarnation != IncarnationId || old.Revision != revision || old.Offset < 0)
                        return Error(SessionContextLimits.ListRestartRequired);
                    offset = old.Offset;
                }
                catch (Exception e) when (e is FormatException or JsonException) { return Error(RelayErrorCodes.InvalidArguments); }
            }
            limit = Math.Clamp(limit, 1, SessionContextLimits.DiscoveryRows);
            using var command = Command("""
                SELECT context_id,revision,title,summary,task_ids,updated_at FROM session_context_catalog
                WHERE repository_id=$repo AND deleted=0
                  AND ($task='' OR EXISTS(SELECT 1 FROM json_each(task_ids) WHERE value=$task))
                ORDER BY updated_at DESC,context_id LIMIT $limit OFFSET $offset;
                """);
            command.Parameters.AddWithValue("$repo", repositoryId); command.Parameters.AddWithValue("$task", taskId ?? "");
            command.Parameters.AddWithValue("$limit", limit + 1); command.Parameters.AddWithValue("$offset", offset);
            var rows = new List<JsonObject>();
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var summary = reader.GetString(3);
                    rows.Add(new JsonObject
                    {
                        ["contextId"] = reader.GetString(0), ["revision"] = reader.GetInt64(1), ["title"] = reader.GetString(2),
                        ["summary"] = summary[..Math.Min(summary.Length, 160)], ["taskIds"] = JsonNode.Parse(reader.GetString(4)), ["updatedAt"] = reader.GetString(5),
                        ["uri"] = SessionContextResourceUri.ManifestUri(repositoryId, reader.GetString(0))
                    });
                }
            var items = new JsonArray();
            var result = new JsonObject { ["items"] = items, ["catalogRevision"] = revision };
            for (var n = 0; n < Math.Min(rows.Count, limit); n++)
            {
                items.Add(rows[n]);
                result["nextCursor"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ContextCursor(repositoryId, taskId ?? "", IncarnationId, revision, offset + items.Count)));
                var json = result.ToJsonString();
                if (Encoding.UTF8.GetByteCount(json) > SessionContextLimits.ReplyBytes - 512 || TokenCounter is { } tokens && tokens.Count(json) > SessionContextLimits.ReplyTokens - 300)
                { items.RemoveAt(items.Count - 1); break; }
            }
            if (items.Count == 0 && rows.Count > 0) return Error(SessionContextLimits.BudgetExceeded);
            if (items.Count < rows.Count)
            {
                result["nextCursor"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ContextCursor(repositoryId, taskId ?? "", IncarnationId, revision, offset + items.Count)));
                result["truncated"] = true;
            }
            else result.Remove("nextCursor");
            return Ok(result);
        }
        finally { _gate.Release(); }
    }
}

using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Storage;

/// <summary>
/// Archived tasks (<c>task_archive</c>): finished tasks that left their board. Each row is written inside the board's own commit, so a task
/// is on its board or in the archive, never both and never neither. Rows keep the board's grain identity so search can rebuild from them.
/// </summary>
public sealed partial class SqliteStore : ITaskArchive
{
    // Part of schema v2 (not yet shipped), created idempotently on every start.
    private const string ArchiveSchema = """
        CREATE TABLE IF NOT EXISTS task_archive (
            repository_id TEXT NOT NULL,
            board_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            completed_ticks INTEGER NOT NULL,
            archived_ticks INTEGER NOT NULL,
            grain_type TEXT NOT NULL,
            grain_key TEXT NOT NULL,
            json TEXT NOT NULL,
            PRIMARY KEY (repository_id, board_id, task_id)
        );

        """;

    /// <summary>Tests set this to fail a commit after its archive rows were written, before the transaction commits.</summary>
    internal Action? ArchiveFault { get; set; }

    /// <summary>The board of a grain key, <c>escaped-repository/escaped-board</c>; <c>default</c> for any other shape.</summary>
    private static string BoardOf(string grainKey) => grainKey.Split('/') is [_, var escaped] ? Uri.UnescapeDataString(escaped) : "default";

    private void WriteArchive(SqliteTransaction transaction, string stateName, GrainId grainId, IReadOnlyList<ArchivedTask> archived)
    {
        var type = grainId.Type.ToString();
        var key = grainId.Key.ToString();
        using var insert = Command("""
            INSERT INTO task_archive(repository_id, board_id, task_id, completed_ticks, archived_ticks, grain_type, grain_key, json)
            VALUES ($repository, $board, $task, $completed, $archived, $type, $key, $json)
            ON CONFLICT(repository_id, board_id, task_id) DO UPDATE SET completed_ticks=excluded.completed_ticks, archived_ticks=excluded.archived_ticks,
                grain_type=excluded.grain_type, grain_key=excluded.grain_key, json=excluded.json;
            """, transaction);
        // The task's search row stays, marked archived; the board no longer lists it, and its marker never matches a live task again.
        using var mark = Command("""
            UPDATE search_index SET status='archived' WHERE rowid IN (SELECT id FROM search_docs WHERE state_name=$state AND grain_type=$type AND grain_key=$key AND kind='task' AND doc_id=$task);
            UPDATE search_docs SET marker='archived' WHERE state_name=$state AND grain_type=$type AND grain_key=$key AND kind='task' AND doc_id=$task;
            """, transaction);
        foreach (var task in archived)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$repository", task.RepositoryId);
            insert.Parameters.AddWithValue("$board", BoardOf(key));
            insert.Parameters.AddWithValue("$task", task.TaskId);
            insert.Parameters.AddWithValue("$completed", task.CompletedAt.UtcTicks);
            insert.Parameters.AddWithValue("$archived", task.ArchivedAt.UtcTicks);
            insert.Parameters.AddWithValue("$type", type);
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$json", task.Json);
            insert.ExecuteNonQuery();
            if (!_searchOptions.Enabled) continue;
            mark.Parameters.Clear();
            mark.Parameters.AddWithValue("$state", stateName);
            mark.Parameters.AddWithValue("$type", type);
            mark.Parameters.AddWithValue("$key", key);
            mark.Parameters.AddWithValue("$task", task.TaskId);
            mark.ExecuteNonQuery();
        }
    }

    /// <summary>Search rebuilds index archived tasks from their rows, with status <c>archived</c>.</summary>
    private void IndexArchive(SqliteTransaction transaction, SearchWriter writer)
    {
        using var rows = Command("SELECT repository_id, task_id, grain_type, grain_key, json FROM task_archive;", transaction);
        using var reader = rows.ExecuteReader();
        while (reader.Read())
            if (SearchExtraction.ArchivedTaskDocument(reader.GetString(0), reader.GetString(1), reader.GetString(4)) is { } document)
                writer.Upsert(AggregateKinds.Board, reader.GetString(2), reader.GetString(3), new SearchEntry(document, "archived"));
    }

    /// <summary>Reads on the read-only connection, which sees the last commit without waiting on the writer.</summary>
    public async Task<string?> ReadArchivedTaskAsync(string repositoryId, string boardId, string taskId, CancellationToken cancellationToken = default)
    {
        await _searchGate.WaitAsync(cancellationToken);
        try
        {
            if (_searchConnection is not { } connection) return null;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM task_archive WHERE repository_id=$repository AND board_id=$board AND task_id=$task;";
            command.Parameters.AddWithValue("$repository", repositoryId);
            command.Parameters.AddWithValue("$board", boardId);
            command.Parameters.AddWithValue("$task", taskId);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        finally { _searchGate.Release(); }
    }

    /// <summary>How many tasks the archive holds, for the dashboard.</summary>
    private long CountArchived()
    {
        using var command = Command("SELECT COUNT(*) FROM task_archive;");
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

using System.Text.Json;
using CoCern.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CoCern.Tests;

public sealed class AnalyticsSnapshotScrubTests
{
    [Theory]
    [InlineData("session")]
    [InlineData("control")]
    [InlineData("discovery")]
    public async Task Indexed_private_state_is_scrubbed_without_changing_the_live_database(string privateState)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cocern-snapshot-scrub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var livePath = Path.Combine(directory, "live.db");
            var snapshotPath = Path.Combine(directory, "snapshot.db");
            using var store = new SqliteStore(livePath);
            using var live = Open(livePath);
            var payload = JsonSerializer.Serialize(new
            {
                Value = new
                {
                    Json = JsonSerializer.Serialize(new
                    {
                        retained = "analytics-value",
                        requests = new[] { "synthetic-replay" },
                        requestTombstones = new[] { "synthetic-tombstone" },
                        sessionToken = "synthetic-token",
                        inbox = new[] { "synthetic-private-message" }
                    })
                }
            });
            using (var seed = live.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO grain_state VALUES ($state, 'fixture', 'private', 1, 'etag', $payload);
                    INSERT INTO grain_state VALUES ('board', 'fixture', 'board', 1, 'etag', $payload);
                    INSERT INTO grain_state VALUES ('lock', 'fixture', 'lock', 1, 'etag', $payload);
                    INSERT INTO due_work VALUES ($state, 'fixture', 'private', 'timer', 'timer', 1);
                    INSERT INTO due_work VALUES ('board', 'fixture', 'board', 'timer', 'timer', 1);
                    INSERT INTO search_docs (state_name, grain_type, grain_key, kind, doc_id, marker)
                    VALUES ($state, 'fixture', 'private', 'message', 'private-message', 'synthetic-private-message');
                    INSERT INTO search_index (state_name, grain_key, doc_id, kind, title, body)
                    VALUES ($state, 'private', 'private-message', 'message', 'private', 'synthetic-private-message');
                    INSERT INTO task_archive VALUES ('repo', 'board', 'archived-task', 1, 2, 'fixture', 'board', '{"title":"archived"}');
                    """;
                seed.Parameters.AddWithValue("$state", privateState);
                seed.Parameters.AddWithValue("$payload", payload);
                seed.ExecuteNonQuery();
            }

            // search_docs references the private grain: removing its parent before dropping the index fails with SQLite error 19.
            await store.CreateAnalyticsSnapshotAsync(snapshotPath);

            using var snapshot = Open(snapshotPath);
            Assert.Equal(2L, Count(snapshot, "SELECT count(*) FROM grain_state"));
            Assert.Equal(0L, Count(snapshot, "SELECT count(*) FROM grain_state WHERE state_name NOT IN ('board', 'lock')"));
            Assert.Equal(1L, Count(snapshot, "SELECT count(*) FROM due_work"));
            Assert.Equal(0L, Count(snapshot, "SELECT count(*) FROM due_work WHERE state_name NOT IN ('board', 'lock')"));
            Assert.Equal(0L, Count(snapshot, "SELECT count(*) FROM grain_state WHERE payload LIKE '%synthetic-token%' OR payload LIKE '%synthetic-replay%' OR payload LIKE '%synthetic-tombstone%'"));
            Assert.Equal(2L, Count(snapshot, "SELECT count(*) FROM grain_state WHERE payload LIKE '%analytics-value%'"));
            Assert.Equal(0L, Count(snapshot, "SELECT count(*) FROM sqlite_master WHERE name IN ('search_docs', 'search_index')"));
            Assert.Equal(0L, Count(snapshot, "SELECT count(*) FROM hub_metadata"));
            Assert.Equal(1L, Count(snapshot, "SELECT count(*) FROM task_archive WHERE task_id = 'archived-task'"));
            Assert.Equal(0L, Count(snapshot, "SELECT count(*) FROM pragma_foreign_key_check"));

            Assert.Equal(3L, Count(live, "SELECT count(*) FROM grain_state WHERE payload LIKE '%synthetic-token%' AND payload LIKE '%synthetic-replay%' AND payload LIKE '%synthetic-tombstone%'"));
            Assert.Equal(2L, Count(live, "SELECT count(*) FROM due_work"));
            Assert.Equal(1L, Count(live, "SELECT count(*) FROM search_docs"));
            Assert.Equal(1L, Count(live, "SELECT count(*) FROM search_index"));
            Assert.True(Count(live, "SELECT count(*) FROM hub_metadata") > 0);
            Assert.Equal(1L, Count(live, "SELECT count(*) FROM task_archive"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}

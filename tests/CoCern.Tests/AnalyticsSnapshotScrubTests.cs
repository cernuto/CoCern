using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Storage;
using Microsoft.Data.Sqlite;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

public sealed class AnalyticsSnapshotScrubTests
{
    /// <summary>
    /// State the Domain built through its own operations, written as the hub writes it. The snapshot keeps only the kinds the Domain lets
    /// leave (<see cref="ReadModels.SnapshotKinds"/>), each without the top-level keys it names private (<see cref="ReadModels.PrivateStateKeys"/>)
    /// and otherwise as it was, so the board's tasks survive. Every private key was there to remove: the board's and the lock's stored
    /// replies, the lock's tombstones once its replies overflowed, and the session's secret.
    /// </summary>
    [Fact]
    public async Task Domain_built_state_leaves_only_as_the_kinds_the_domain_allows_without_the_keys_it_names_private()
    {
        var now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
        var hub = new AggregateHub();
        var chat = hub.Open("worker", now);
        Assert.True(hub.Board(RelayTools.TaskUpdate, new() { ["taskId"] = "T-1", ["expectedVersion"] = 0 }, now).Ok);
        Assert.True(hub.Board(RelayTools.TaskUpdate, new() { ["taskId"] = "T-2", ["expectedVersion"] = 0 }, now).Ok);
        Assert.True(hub.Board(RelayTools.SendMessage, new() { ["taskId"] = "T-1", ["sessionId"] = chat.Id, ["text"] = "Starting." }, now).Ok);
        const string Resource = "rig-1";
        RelayReply Claim(string operation, JsonObject args)
        {
            args["sessionId"] = chat.Id; args["leaseSeconds"] = 3600;
            return hub.Apply(AggregateKinds.Lock, Resource, operation, args, AggregateHub.Alice, now);
        }
        Assert.True(Claim(RelayTools.ClaimAcquire, new() { ["requestId"] = "acquire" }).Ok);
        // Enough renewals that the lock's stored replies overflow: it drops the oldest before their window ends and keeps tombstones for them.
        for (var renewal = 0; renewal < RelayLimits.MaxRecords; renewal++)
            Assert.True(Claim(RelayTools.ClaimRenew, new() { ["requestId"] = "renew-" + renewal, ["generation"] = 1 }).Ok);

        var directory = Path.Combine(Path.GetTempPath(), "cocern-snapshot-domain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var snapshotPath = Path.Combine(directory, "snapshot.db");
            using var store = new SqliteStore(Path.Combine(directory, "live.db"));
            var storage = new SqliteGrainStorage(store);
            foreach (var (kind, grainType, key) in new[]
            {
                (AggregateKinds.Board, "cocern.board", AggregateHub.BoardKey), (AggregateKinds.Lock, "cocern.lock", Resource),
                (AggregateKinds.Session, "cocern.session", chat.Id), (AggregateKinds.Control, "cocern.control", AggregateKinds.ControlKey)
            })
                await storage.WriteStateAsync(kind, GrainId.Create(grainType, key), new GrainState<StateEnvelope<CoordinationDocument>>(hub.Of(kind, key)));
            using var live = Open(Path.Combine(directory, "live.db"));
            var stored = States(live);
            var lastRenewal = CoordinationStateMachine.RequestKey(AggregateHub.Alice.PrincipalId, chat.Id, RelayTools.ClaimRenew, "renew-" + (RelayLimits.MaxRecords - 1));
            Assert.True(stored[AggregateKinds.Board].ContainsKey(ReadModels.RequestsKey));
            Assert.True(stored[AggregateKinds.Lock][ReadModels.RequestsKey]!.AsObject().ContainsKey(lastRenewal));
            Assert.Equal(1L, Count(live, $"SELECT count(*) FROM grain_state WHERE instr(payload, '{lastRenewal}') > 0"));
            Assert.True(stored[AggregateKinds.Lock].ContainsKey(ReadModels.RequestTombstonesKey));
            Assert.Equal(chat.Token, stored[AggregateKinds.Session][ReadModels.SessionTokenKey]!.GetValue<string>());
            Assert.All(ReadModels.PrivateStateKeys, key => Assert.Contains(stored.Values, state => state.ContainsKey(key)));

            await store.CreateAnalyticsSnapshotAsync(snapshotPath);

            using (var snapshot = Open(snapshotPath))
            {
                var copied = States(snapshot);
                Assert.Equal(ReadModels.SnapshotKinds.Order(), copied.Keys.Order());
                foreach (var (kind, state) in copied)
                {
                    Assert.DoesNotContain(state, entry => ReadModels.PrivateStateKeys.Contains(entry.Key));
                    var expected = stored[kind].DeepClone().AsObject();
                    foreach (var key in ReadModels.PrivateStateKeys) expected.Remove(key);
                    Assert.True(JsonNode.DeepEquals(expected, state), $"The snapshot changed more of the {kind} state than its private keys.");
                }
                Assert.Equal(["T-1", "T-2"], copied[AggregateKinds.Board]["tasks"]!.AsObject().Select(task => task.Key));
                Assert.Equal("Starting.", copied[AggregateKinds.Board]["tasks"]!["T-1"]!["messages"]![0]!["text"]!.GetValue<string>());
                Assert.Equal(0L, Count(snapshot, $"SELECT count(*) FROM grain_state WHERE instr(payload, '{lastRenewal}') > 0"));
            }
            // Nor are the session's secret and the lock's latest stored reply anywhere in the file, free pages included.
            var file = File.ReadAllBytes(snapshotPath);
            Assert.Equal(-1, file.AsSpan().IndexOf(Encoding.UTF8.GetBytes(chat.Token)));
            Assert.Equal(-1, file.AsSpan().IndexOf(Encoding.UTF8.GetBytes(lastRenewal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Each stored state's document (<c>$.Value.Json</c>), by state name.</summary>
    private static Dictionary<string, JsonObject> States(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_name, payload FROM grain_state";
        using var reader = command.ExecuteReader();
        var states = new Dictionary<string, JsonObject>();
        while (reader.Read()) states.Add(reader.GetString(0), JsonNode.Parse(JsonNode.Parse(reader.GetString(1))!["Value"]!["Json"]!.GetValue<string>())!.AsObject());
        return states;
    }

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
                    INSERT INTO grain_state(state_name, grain_type, grain_key, generation, etag, payload) VALUES ($state, 'fixture', 'private', 1, 'etag', $payload);
                    INSERT INTO grain_state(state_name, grain_type, grain_key, generation, etag, payload) VALUES ('board', 'fixture', 'board', 1, 'etag', $payload);
                    INSERT INTO grain_state(state_name, grain_type, grain_key, generation, etag, payload) VALUES ('lock', 'fixture', 'lock', 1, 'etag', $payload);
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

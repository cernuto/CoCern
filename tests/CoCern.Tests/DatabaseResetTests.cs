using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using CoCern.Domain.State;
using CoCern.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CoCern.Tests;

public sealed class DatabaseResetTests
{
    [Fact]
    public async Task Reset_waits_for_restart_can_be_canceled_and_preserves_enrollment_credentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cocern-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "relay.db");
        var credential = Path.Combine(directory, "local-client-token");
        await File.WriteAllTextAsync(credential, "fixture-credential");
        try
        {
            string serviceId;
            var lastSeen = DateTimeOffset.UtcNow;
            using (var store = new SqliteStore(database))
            {
                serviceId = store.ServiceId;
                var discovery = new JsonObject();
                new DiscoveryState(discovery).Enroll("fixture-pc", "Preview PC", "preserved-hash", lastSeen.AddDays(-45));
                discovery[DiscoveryState.MachinesKey]!["fixture-pc"]![EnrolledMachine.LastSeenKey] = lastSeen.ToString("O");
                discovery[DiscoveryState.MachinesKey]!["fixture-pc"]![EnrolledMachine.ChatsKey] = new JsonArray(new JsonObject { ["sessionId"] = "old-chat" });
                var payload = JsonSerializer.Serialize(new StateEnvelope<CoordinationDocument> { Value = new() { Json = discovery.ToJsonString() } });
                using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                await connection.OpenAsync();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO grain_state(state_name,grain_type,grain_key,generation,etag,payload) VALUES ('discovery','cocern.discovery','hub',1,'enrollment',$payload);
                    INSERT INTO grain_state(state_name,grain_type,grain_key,generation,etag,payload) VALUES ('board','cocern.board','repo/default',1,'board',$board);
                    INSERT INTO due_work VALUES ('board','cocern.board','repo/default','work','effect',0);
                    INSERT INTO hub_metadata VALUES ('feedback_rule:fixture','{}');
                    """;
                seed.Parameters.AddWithValue("$payload", payload);
                seed.Parameters.AddWithValue("$board", "{\"padding\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
                await seed.ExecuteNonQueryAsync();
                await store.ScheduleCoordinationResetAsync(cancel: false);
                Assert.True(await store.CoordinationResetPendingAsync());
                Assert.Single(await store.ReadDueAsync(DateTimeOffset.UtcNow, 10));
                await store.ScheduleCoordinationResetAsync(cancel: true);
            }
            using (var canceled = new SqliteStore(database))
            {
                Assert.False(await canceled.CoordinationResetPendingAsync());
                Assert.Single(await canceled.ReadDueAsync(DateTimeOffset.UtcNow, 10));
                await canceled.ScheduleCoordinationResetAsync(cancel: false);
            }
            var allocatedBeforeReset = new FileInfo(database).Length;
            Assert.True(allocatedBeforeReset > 2 * 1024 * 1024);
            using (var reset = new SqliteStore(database))
            {
                Assert.True(new FileInfo(database).Length < allocatedBeforeReset / 2);
                Assert.Equal(serviceId, reset.ServiceId);
                Assert.False(await reset.CoordinationResetPendingAsync());
                Assert.Empty(await reset.ReadDueAsync(DateTimeOffset.UtcNow, 10));
                using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                await connection.OpenAsync();
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT payload FROM grain_state;";
                using var rows = await read.ExecuteReaderAsync();
                Assert.True(await rows.ReadAsync());
                var envelope = JsonSerializer.Deserialize<StateEnvelope<CoordinationDocument>>(rows.GetString(0))!;
                var machine = new DiscoveryState(JsonNode.Parse(envelope.Value.Json)!.AsObject()).Machine("fixture-pc")!;
                Assert.Equal("preserved-hash", machine.CredentialHash);
                Assert.Empty(machine.Chats);
                Assert.Equal(lastSeen, machine.LastSeen);
                Assert.False(await rows.ReadAsync());
            }
            Assert.Equal("fixture-credential", await File.ReadAllTextAsync(credential));
            // Simulate a reset committed before compaction finished. Retrying
            // compaction must preserve coordination data from the later run.
            using (var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
            {
                await connection.OpenAsync();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO grain_state(state_name,grain_type,grain_key,generation,etag,payload) VALUES ('board','cocern.board','new/default',1,'new','{}');
                    INSERT INTO hub_metadata VALUES ('coordination_reset_compaction_pending','1');
                    """;
                await seed.ExecuteNonQueryAsync();
            }
            using var reopened = new SqliteStore(database);
            Assert.Equal(serviceId, reopened.ServiceId);
            Assert.False(await reopened.CoordinationResetPendingAsync());
            using var verifyConnection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
            await verifyConnection.OpenAsync();
            using var verify = verifyConnection.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM grain_state WHERE grain_key='new/default';";
            Assert.Equal(1L, await verify.ExecuteScalarAsync());
            verify.CommandText = "SELECT COUNT(*) FROM hub_metadata WHERE name='coordination_reset_compaction_pending';";
            Assert.Equal(0L, await verify.ExecuteScalarAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }
}

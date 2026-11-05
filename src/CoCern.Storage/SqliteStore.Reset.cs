using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using CoCern.Domain.State;
using Microsoft.Data.Sqlite;

namespace CoCern.Storage;

public sealed partial class SqliteStore
{
    private const string ResetKey = "coordination_reset_pending";
    private const string ResetCompactionKey = "coordination_reset_compaction_pending";

    public async Task<bool> CoordinationResetPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = Command("SELECT 1 FROM hub_metadata WHERE name=$name;");
            command.Parameters.AddWithValue("$name", ResetKey);
            return command.ExecuteScalar() is not null;
        }
        finally { _gate.Release(); }
    }

    // Records intent only. Live grains and writers can continue until shutdown.
    public async Task ScheduleCoordinationResetAsync(bool cancel, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var command = Command(cancel
                ? "DELETE FROM hub_metadata WHERE name=$name;"
                : "INSERT INTO hub_metadata(name,value) VALUES ($name,'1') ON CONFLICT(name) DO UPDATE SET value='1';");
            command.Parameters.AddWithValue("$name", ResetKey);
            command.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    // Runs under exclusive database ownership, before grains or writers exist.
    // Enrollment preservation, deletion and marker consumption commit atomically.
    private void ApplyPendingCoordinationReset(SqliteTransaction transaction)
    {
        using (var pending = Command("SELECT 1 FROM hub_metadata WHERE name=$name;", transaction))
        {
            pending.Parameters.AddWithValue("$name", ResetKey);
            if (pending.ExecuteScalar() is null) return;
        }

        var enrollments = new List<(string Type, string Key, string Payload)>();
        using (var command = Command("SELECT grain_type,grain_key,payload FROM grain_state WHERE state_name=$name;", transaction))
        {
            command.Parameters.AddWithValue("$name", AggregateKinds.Discovery);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var envelope = JsonSerializer.Deserialize<StateEnvelope<CoordinationDocument>>(reader.GetString(2))
                    ?? throw new InvalidDataException("Cannot preserve PC enrollment while resetting coordination data.");
                var discovery = JsonNode.Parse(envelope.Value.Json)?.AsObject()
                    ?? throw new InvalidDataException("Cannot preserve PC enrollment while resetting coordination data.");
                if (discovery[DiscoveryState.MachinesKey] is JsonObject machines)
                {
                    foreach (var machine in machines.Select(item => item.Value).OfType<JsonObject>())
                    {
                        // LastSeen is also the credential-retention clock. Keep it so a recently
                        // active PC with an old enrollment is not evicted before it reconnects.
                        machine[EnrolledMachine.ClientsKey] = new JsonArray();
                        machine[EnrolledMachine.ChatsKey] = new JsonArray();
                        machine[EnrolledMachine.NodeBuildKey] = null;
                    }
                }
                envelope.Value.Json = discovery.ToJsonString();
                envelope.Effects.Clear();
                envelope.DueWork.Clear();
                enrollments.Add((reader.GetString(0), reader.GetString(1), JsonSerializer.Serialize(envelope)));
            }
        }

        using (var clear = Command("""
            DELETE FROM due_work;
            DELETE FROM search_docs;
            DELETE FROM search_index;
            DELETE FROM task_archive;
            DELETE FROM grain_state;
            DELETE FROM activity_log;
            DELETE FROM activity_daily;
            DELETE FROM activity_origin_daily;
            DELETE FROM hub_metadata WHERE name NOT IN ('service_id','incarnation_id');
            """, transaction))
            clear.ExecuteNonQuery();

        foreach (var enrollment in enrollments)
        {
            using var insert = Command("INSERT INTO grain_state(state_name,grain_type,grain_key,generation,etag,payload,closed) VALUES ($name,$type,$key,1,$etag,$payload,0);", transaction);
            insert.Parameters.AddWithValue("$name", AggregateKinds.Discovery);
            insert.Parameters.AddWithValue("$type", enrollment.Type);
            insert.Parameters.AddWithValue("$key", enrollment.Key);
            insert.Parameters.AddWithValue("$etag", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$payload", enrollment.Payload);
            insert.ExecuteNonQuery();
        }

        // Keep compaction separate from reset intent: retrying it must never erase
        // coordination data created after the reset transaction committed.
        using var compact = Command("INSERT INTO hub_metadata(name,value) VALUES ($name,'1');", transaction);
        compact.Parameters.AddWithValue("$name", ResetCompactionKey);
        compact.ExecuteNonQuery();
    }

    // Initialize has returned, so its transaction and prepared statements are
    // disposed. No search connection, activity writer or grain exists yet.
    private void CompactPendingCoordinationReset()
    {
        using (var pending = Command("SELECT 1 FROM hub_metadata WHERE name=$name;"))
        {
            pending.Parameters.AddWithValue("$name", ResetCompactionKey);
            if (pending.ExecuteScalar() is null) return;
        }
        using (var vacuum = Command("VACUUM;"))
            vacuum.ExecuteNonQuery();
        using (var checkpoint = Command("PRAGMA wal_checkpoint(TRUNCATE);"))
        {
            if (Convert.ToInt32(checkpoint.ExecuteScalar()) != 0)
                throw new IOException("Cannot finish reclaiming SQLite space after the coordination reset. Restart the hub to retry compaction.");
        }
        using var complete = Command("DELETE FROM hub_metadata WHERE name=$name;");
        complete.Parameters.AddWithValue("$name", ResetCompactionKey);
        complete.ExecuteNonQuery();
    }
}

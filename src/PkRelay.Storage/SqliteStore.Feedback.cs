using System.Text.Json;
using PkRelay.Core;

namespace PkRelay.Storage;

/// <summary>
/// Each feedback rule's on/off state and follow totals, as one <c>hub_metadata</c> row per rule (<c>feedback_rule:&lt;rule&gt;</c>), so a rule
/// switched off stays off after a restart. Written on the telemetry connection, never under the grain writer's gate.
/// </summary>
public sealed partial class SqliteStore : IFeedbackRuleStore
{
    private const string FeedbackRulePrefix = "feedback_rule:";

    public IReadOnlyList<FeedbackRuleState> LoadFeedbackRules()
    {
        _activityGate.Wait();
        try
        {
            using var command = ActivityCommand("SELECT value FROM hub_metadata WHERE name LIKE 'feedback_rule:%';");
            using var reader = command.ExecuteReader();
            var rules = new List<FeedbackRuleState>();
            while (reader.Read())
            {
                try { if (JsonSerializer.Deserialize<FeedbackRuleState>(reader.GetString(0)) is { Rule.Length: > 0 } rule) rules.Add(rule); }
                catch (JsonException) { /* A damaged row starts that rule afresh. */ }
            }
            return rules;
        }
        finally { _activityGate.Release(); }
    }

    public async Task SaveFeedbackRuleAsync(FeedbackRuleState state)
    {
        await _activityGate.WaitAsync();
        try
        {
            using var command = ActivityCommand("INSERT INTO hub_metadata(name, value) VALUES ($name, $value) ON CONFLICT(name) DO UPDATE SET value = excluded.value;");
            command.Parameters.AddWithValue("$name", FeedbackRulePrefix + state.Rule);
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(state));
            command.ExecuteNonQuery();
        }
        finally { _activityGate.Release(); }
    }
}

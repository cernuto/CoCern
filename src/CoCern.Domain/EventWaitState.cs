using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain;

internal sealed class EventWaitState(JsonObject state)
{
    internal const int MaxActive = 64;
    internal const int MaxRetained = 256;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public JsonObject? Entries => state["eventWaits"] switch
    {
        null => null,
        JsonObject entries when entries.Count <= MaxRetained => entries,
        JsonObject => throw new InvalidOperationException("Oversized event wait state"),
        _ => throw new InvalidOperationException("Invalid event wait state")
    };
    public JsonObject CreateEntries()
    {
        if (state["eventWaits"] is not null && Entries is null) throw new InvalidOperationException("Invalid event wait state");
        return Entries ?? (JsonObject)(state["eventWaits"] = new JsonObject());
    }
    public static EventWaitRequest Request(JsonObject entry)
    {
        try { return entry["request"]?.Deserialize<EventWaitRequest>(Json) ?? throw new InvalidOperationException("Invalid event wait record"); }
        catch (JsonException error) { throw new InvalidOperationException("Invalid event wait record", error); }
    }
}

// Event retirement cannot share the generic request pool: that pool may evict
// its oldest tombstones under pressure. These fences instead refuse saturation
// until their normal replay window expires. No unexpired fence is discarded.
internal static class EventWaitRetirement
{
    internal const string Key = "eventWaitRetired";

    internal static bool Contains(JsonObject state, string identity, DateTimeOffset now) =>
        Read(state) is { } map && map[Hash(identity)] is JsonValue value && value.GetValue<long>() > now.ToUnixTimeSeconds();

    internal static bool TryRemember(JsonObject state, IReadOnlyList<(string Key, DateTimeOffset At)> records, DateTimeOffset now)
    {
        if (records.Count == 0) return true;
        var map = Read(state) ?? new JsonObject();
        var expired = map.Where(pair => pair.Value!.GetValue<long>() <= now.ToUnixTimeSeconds()).Select(pair => pair.Key).ToArray();
        var additions = records.Select(record => (Key: Hash(record.Key), Until: (record.At + Retention.Window).ToUnixTimeSeconds())).ToArray();
        var fresh = additions.Select(record => record.Key).Distinct(StringComparer.Ordinal).Count(key => !map.ContainsKey(key) || expired.Contains(key, StringComparer.Ordinal));
        if (map.Count - expired.Length + fresh > RelayLimits.MaxRequestTombstones) return false;
        foreach (var key in expired) map.Remove(key);
        foreach (var (key, until) in additions)
            map[key] = Math.Max(until, map[key]?.GetValue<long>() ?? long.MinValue);
        if (state[Key] is null) state[Key] = map;
        return true;
    }

    private static JsonObject? Read(JsonObject state)
    {
        if (state[Key] is null) return null;
        if (state[Key] is not JsonObject map || map.Count > RelayLimits.MaxRequestTombstones) throw new InvalidOperationException("Invalid event retirement state");
        foreach (var pair in map)
            if (pair.Key.Length != 64 || pair.Key.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
                || pair.Value is not JsonValue value || !value.TryGetValue<long>(out _)) throw new InvalidOperationException("Invalid event retirement fence");
        return map;
    }

    private static string Hash(string identity) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
}

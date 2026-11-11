using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain;

/// <summary>
/// Bounded, age-based memory for the per-aggregate maps that guard against retries: stored request replies, acknowledged messages and
/// resolved actions. Successful mutations prune expired entries at most hourly. Capacity pressure also removes the oldest entries,
/// down to <see cref="RelayLimits.RetainedEntriesAfterPrune"/>, retaining tombstones for requests evicted within their retry window.
/// </summary>
internal static class Retention
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(RelayLimits.RequestReplayRetentionHours);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Age cleanup on a successful mutation, independent of capacity. Reads and cached retries do not scan or commit housekeeping.
    /// Undated legacy entries remain protected until the existing capacity path can replace their replies with fresh tombstones.
    /// </summary>
    public static void PruneExpired(JsonObject state, DateTimeOffset now)
    {
        var checkedAt = WrittenAt(state["retentionCheckedAt"]);
        if (checkedAt <= now && now - checkedAt < MaintenanceInterval) return;
        var cutoff = now - Window;
        foreach (var key in new[] { ReadModels.RequestsKey, "acked", "resolvedActions" })
        {
            if (state[key] is not JsonObject map) continue;
            List<(string Key, JsonNode? Value, DateTimeOffset At)> entries = [with(capacity: map.Count)];
            foreach (var entry in map) entries.Add((entry.Key, entry.Value, WrittenAt(entry.Value)));
            if (!entries.Any(entry => entry.At != DateTimeOffset.MinValue && entry.At <= cutoff)) continue;
            // Rebuild once: individual removals repeatedly shift JsonObject's ordered storage.
            map.Clear();
            foreach (var entry in entries.Where(entry => entry.At == DateTimeOffset.MinValue || entry.At > cutoff))
                map[entry.Key] = entry.Value;
        }
        Bury(state, [], now);
        state["retentionCheckedAt"] = Stamp(now);
    }

    /// <summary>The value a timestamped entry stores: the time as round-trip text.</summary>
    public static JsonNode Stamp(DateTimeOffset now) => JsonValue.Create(now.ToString("O", CultureInfo.InvariantCulture))!;

    /// <summary>
    /// When an entry was written: an object's <c>at</c>, a text timestamp, or Unix seconds (tombstones). Anything else, such as the
    /// <c>true</c> older states stored, reads as the earliest time.
    /// </summary>
    public static DateTimeOffset WrittenAt(JsonNode? entry)
    {
        var value = entry is JsonObject record ? record["at"] as JsonValue : entry as JsonValue;
        if (value is null) return DateTimeOffset.MinValue;
        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var seconds)) return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Makes room for one more entry when <paramref name="map"/> holds <paramref name="limit"/> or more. Drops entries past the window,
    /// then, if more than <paramref name="target"/> remain, the oldest down to that many. Returns the entries dropped before their window
    /// ended (key and write time; for entries without a time, now), so a caller can remember them. Below the limit it does nothing, so
    /// most writes never scan the map.
    /// </summary>
    public static List<(string Key, DateTimeOffset At)> MakeRoom(JsonObject map, DateTimeOffset now, int limit = RelayLimits.MaxRecords, int target = RelayLimits.RetainedEntriesAfterPrune)
    {
        var evicted = new List<(string Key, DateTimeOffset At)>();
        if (map.Count < limit) return evicted;
        var cutoff = now - Window;
        var entries = map.Select((entry, index) => (entry.Key, Value: entry.Value, At: WrittenAt(entry.Value), Index: index)).ToList();
        var young = entries.Where(entry => entry.At > cutoff).ToList();
        var drop = new HashSet<string>(entries.Where(entry => entry.At <= cutoff).Select(entry => entry.Key), StringComparer.Ordinal);
        // An entry from before retention has no time, so it may be recent: it is dropped, but reported as evicted now, so a replay's
        // retry still meets a tombstone for a full window.
        evicted.AddRange(entries.Where(entry => entry.At == DateTimeOffset.MinValue).Select(entry => (entry.Key, now)));
        if (young.Count > target)
            foreach (var entry in young.OrderBy(entry => entry.At).ThenBy(entry => entry.Index).Take(young.Count - target))
            {
                drop.Add(entry.Key);
                evicted.Add((entry.Key, entry.At));
            }
        // Rebuild rather than remove one by one: removing from a JsonObject shifts its ordered list each time.
        map.Clear();
        foreach (var entry in entries.Where(entry => !drop.Contains(entry.Key))) map[entry.Key] = entry.Value;
        return evicted;
    }

    /// <summary>A compact, fixed-length stand-in for a request key: 96 bits of its SHA-256, as 16 URL-safe characters.</summary>
    public static string Hash(string key) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 12));

    /// <summary>
    /// Remembers replays evicted before their window ended (hashed key, Unix seconds of the original request) and drops tombstones past
    /// the window. Past <see cref="RelayLimits.MaxRequestTombstones"/>, the oldest are dropped down to <see cref="RelayLimits.RequestTombstonesAfterPrune"/>:
    /// a retry of one of those would run again. That takes about 7,700 writes (1,536 replays plus 6,144 tombstones) to one aggregate
    /// between the request and its retry, within the window.
    /// </summary>
    public static void Bury(JsonObject state, IReadOnlyList<(string Key, DateTimeOffset At)> evicted, DateTimeOffset now)
    {
        if (evicted.Count == 0 && state[ReadModels.RequestTombstonesKey] is null) return;
        var tombstones = JsonFields.ObjectAt(state, ReadModels.RequestTombstonesKey);
        foreach (var (key, at) in evicted) tombstones[Hash(key)] = at.ToUnixTimeSeconds();
        // Called on capacity pruning or hourly mutation housekeeping, never on ordinary status reads.
        var cutoff = now - Window;
        var keep = tombstones.Where(entry => WrittenAt(entry.Value) > cutoff).Select(entry => (entry.Key, entry.Value)).ToList();
        if (keep.Count > RelayLimits.MaxRequestTombstones) keep = keep.OrderBy(entry => WrittenAt(entry.Value)).Skip(keep.Count - RelayLimits.RequestTombstonesAfterPrune).ToList();
        tombstones.Clear();
        foreach (var (key, value) in keep) tombstones[key] = value;
        if (tombstones.Count == 0) state.Remove(ReadModels.RequestTombstonesKey);
    }

    /// <summary>Whether a retry names a request, still within its window, whose replay was evicted early. One lookup.</summary>
    public static bool IsBuried(JsonObject state, string key, DateTimeOffset now) =>
        state[ReadModels.RequestTombstonesKey] is JsonObject { Count: > 0 } tombstones && tombstones[Hash(key)] is JsonNode tombstone && WrittenAt(tombstone) > now - Window;
}

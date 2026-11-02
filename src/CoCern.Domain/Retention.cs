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
/// resolved actions. Each entry records when it was written. A map is pruned only when it reaches <see cref="RelayLimits.MaxRecords"/>:
/// entries past the retry window go first, then the oldest, down to <see cref="RelayLimits.RetainedEntriesAfterPrune"/>. Entries written
/// before retention existed carry no time and count as past the window.
/// </summary>
internal static class Retention
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(RelayLimits.RequestReplayRetentionHours);

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
        if (evicted.Count == 0 && state["requestTombstones"] is null) return;
        var tombstones = JsonFields.ObjectAt(state, "requestTombstones");
        foreach (var (key, at) in evicted) tombstones[Hash(key)] = at.ToUnixTimeSeconds();
        // Called only when the replay map was pruned, so this scan is amortized the same way.
        var cutoff = now - Window;
        var keep = tombstones.Where(entry => WrittenAt(entry.Value) > cutoff).Select(entry => (entry.Key, entry.Value)).ToList();
        if (keep.Count > RelayLimits.MaxRequestTombstones) keep = keep.OrderBy(entry => WrittenAt(entry.Value)).Skip(keep.Count - RelayLimits.RequestTombstonesAfterPrune).ToList();
        tombstones.Clear();
        foreach (var (key, value) in keep) tombstones[key] = value;
        if (tombstones.Count == 0) state.Remove("requestTombstones");
    }

    /// <summary>Whether a retry names a request, still within its window, whose replay was evicted early. One lookup.</summary>
    public static bool IsBuried(JsonObject state, string key, DateTimeOffset now) =>
        state["requestTombstones"] is JsonObject { Count: > 0 } tombstones && tombstones[Hash(key)] is JsonNode tombstone && WrittenAt(tombstone) > now - Window;
}

using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

/// <summary>
/// The unresolved actions one session answers for, kept from boards' <see cref="Messages.ActionWorkReport"/>s. Each action is a short
/// hash of its key with the board time of its last report, at most <see cref="RelayLimits.MaxSessionOpenActions"/>. Reports can arrive
/// reordered (effects are retried one by one), so:
/// <list type="bullet">
/// <item>a close leaves a tombstone (at most <see cref="RelayLimits.MaxSessionClosedActions"/>); an open reported no later than it is
/// ignored, one reported after it (the action came back to this session) opens it again;</item>
/// <item>entries, tombstones and the overflow mark lapse after <see cref="RelayLimits.SessionWorkRetentionDays"/> without a report, so a
/// lost report never protects a session for good. A report older than that is ignored.</item>
/// </list>
/// </summary>
internal static class WorkLedger
{
    private const string OpenKey = "openActions";
    private const string ClosedKey = "closedActions";
    private const string OverflowKey = "openActionsOverflow";

    public static long Cutoff(DateTimeOffset now) => now.AddDays(-RelayLimits.SessionWorkRetentionDays).ToUnixTimeSeconds();

    public static long Seconds(JsonNode? value) => value is JsonValue number && number.TryGetValue<long>(out var seconds) ? seconds : long.MinValue;

    /// <summary>A report's time in seconds, never later than now: a sender's clock ahead of this aggregate's cannot date a report into the future.</summary>
    public static long ReportedAt(DateTimeOffset reported, DateTimeOffset now) => Math.Min(reported.ToUnixTimeSeconds(), now.ToUnixTimeSeconds());

    /// <summary>Whether the record answers for an action reported (or overflowed) within the retention window.</summary>
    public static bool Owns(JsonObject record, DateTimeOffset now)
    {
        var cutoff = Cutoff(now);
        return record[OpenKey] is JsonObject open && open.Any(entry => Seconds(entry.Value) > cutoff) || Seconds(record[OverflowKey]) > cutoff;
    }

    /// <summary>Applies one open or close report; returns whether it changed what the record answers for.</summary>
    public static bool Apply(JsonObject record, string actionKey, bool opening, long at, DateTimeOffset now)
    {
        var key = Retention.Hash(actionKey);
        var cutoff = Cutoff(now);
        var open = ObjectAt(record, OpenKey);
        var closed = ObjectAt(record, ClosedKey);
        foreach (var map in new[] { open, closed })
            foreach (var stale in map.Where(entry => Seconds(entry.Value) <= cutoff).Select(entry => entry.Key).ToArray()) map.Remove(stale);
        if (Seconds(record[OverflowKey]) <= cutoff) record.Remove(OverflowKey);
        var tracked = false;
        if (at > cutoff)
        {
            if (opening)
            {
                if (!closed.ContainsKey(key) || at > Seconds(closed[key]))
                {
                    closed.Remove(key);
                    tracked = true;
                    if (open.ContainsKey(key) || open.Count < RelayLimits.MaxSessionOpenActions) open[key] = Math.Max(at, Seconds(open[key]));
                    else record[OverflowKey] = Math.Max(at, Seconds(record[OverflowKey]));
                }
            }
            else
            {
                // An open reported after this close (the action came back) outlives it; a reordered, older close leaves it open.
                tracked = open.ContainsKey(key) && Seconds(open[key]) <= at && open.Remove(key);
                if (!closed.ContainsKey(key) && closed.Count >= RelayLimits.MaxSessionClosedActions)
                    closed.Remove(closed.OrderBy(entry => Seconds(entry.Value)).ThenBy(entry => entry.Key, StringComparer.Ordinal).First().Key);
                closed[key] = Math.Max(at, Seconds(closed[key])); // a late, older close never lowers it
            }
        }
        if (open.Count == 0) record.Remove(OpenKey);
        if (closed.Count == 0) record.Remove(ClosedKey);
        return tracked;
    }
}

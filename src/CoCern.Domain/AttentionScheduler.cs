using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

/// <summary>
/// One open attention item, read from a session's queue as it stores it (<see cref="QueuedItem"/>). <see cref="SnoozeUntil"/> ends any snooze when it passes;
/// <see cref="SnoozeBoundaries"/> counts the boundaries a "later" item still sits out.
/// </summary>
public sealed record AttentionItem(
    string Id, string Kind, string TaskId, string BoardId, string Subject, string? Blocker, bool Blocking, string Priority,
    IReadOnlyList<string> Refs, string Note, DateTimeOffset CreatedAt, int SnoozeBoundaries, DateTimeOffset? SnoozeUntil,
    DateTimeOffset? ShownAt, DateTimeOffset ExpiresAt, int Snoozes)
{
    /// <summary>The ranking tier, from structure only: the kind, the blocker kind and whether it blocks another session.</summary>
    public int Tier => AttentionWeights.Tier(Kind, Blocker, Blocking);

    /// <summary>One open item per kind, task and subject: a second raise with the same key refreshes the first.</summary>
    public string Key => AttentionScheduler.Key(Kind, BoardId, TaskId, Subject);

    /// <summary>A missing or unreadable expiry counts as expired, so a damaged item cannot stay open forever.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsSnoozed(DateTimeOffset now) => SnoozeUntil is { } until ? now < until : SnoozeBoundaries > 0;

    /// <summary>Reads one stored item (<see cref="QueuedItem"/>).</summary>
    public static AttentionItem From(string id, JsonObject item) => new(
        id, Text(item, QueuedItem.KindKey), Text(item, QueuedItem.TaskIdKey), Text(item, QueuedItem.BoardIdKey, AggregateKinds.DefaultBoard), Text(item, QueuedItem.SubjectKey),
        Text(item, QueuedItem.BlockerKey) is { Length: > 0 } blocker ? blocker : null, IsTrue(item, QueuedItem.BlockingKey), Text(item, QueuedItem.PriorityKey, AttentionWeights.Normal),
        item[QueuedItem.RefsKey] is JsonArray refs ? refs.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray() : [],
        Text(item, QueuedItem.NoteKey), Date(item, QueuedItem.CreatedAtKey), (int)Math.Max(0, Number(item, QueuedItem.SnoozeBoundariesKey)), OptionalDate(item, QueuedItem.SnoozeUntilKey),
        OptionalDate(item, QueuedItem.ShownAtKey), Date(item, QueuedItem.ExpiresAtKey), (int)Math.Max(0, Number(item, QueuedItem.SnoozesKey)));

}

/// <summary>
/// Picks the one attention item a session should see next. Pure and read-only: it never changes the items it is given.
/// Order: tier (from <see cref="AttentionWeights"/>), then priority weight plus aging bonus, then oldest, then item ID.
/// </summary>
public static class AttentionScheduler
{
    /// <summary>The best eligible item, or null: not expired, not snoozed, and not for the focused task.</summary>
    public static AttentionItem? Pick(IEnumerable<AttentionItem> items, DateTimeOffset now, string? focusTaskId = null, string focusBoardId = "default") =>
        Order(Eligible(items, now, focusTaskId, focusBoardId), now).FirstOrDefault();

    /// <summary>Reads a session's <c>attention</c> object and picks from it, without changing it.</summary>
    public static AttentionItem? Pick(JsonObject attention, DateTimeOffset now, string? focusTaskId = null, string focusBoardId = "default") =>
        Pick(Items(attention), now, focusTaskId, focusBoardId);

    public static IEnumerable<AttentionItem> Eligible(IEnumerable<AttentionItem> items, DateTimeOffset now, string? focusTaskId = null, string focusBoardId = "default") =>
        items.Where(item => !item.IsExpired(now) && !item.IsSnoozed(now)
            && !(!string.IsNullOrEmpty(focusTaskId) && item.TaskId == focusTaskId && item.BoardId == focusBoardId));

    public static IOrderedEnumerable<AttentionItem> Order(IEnumerable<AttentionItem> items, DateTimeOffset now) => items
        .OrderBy(item => item.Tier)
        .ThenByDescending(item => AttentionWeights.PriorityWeight(item.Priority) + AttentionWeights.AgingBonus(now - item.CreatedAt))
        .ThenBy(item => item.CreatedAt)
        .ThenBy(item => item.Id, StringComparer.Ordinal);

    /// <summary>The item a full queue drops first: the lowest tier, and within it the oldest.</summary>
    public static AttentionItem? DropCandidate(IEnumerable<AttentionItem> items) => items
        .OrderByDescending(item => item.Tier).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Every readable item in a session's <c>attention</c> object; malformed entries are skipped.</summary>
    public static IReadOnlyList<AttentionItem> Items(JsonObject attention) =>
        attention.Where(entry => entry.Value is JsonObject).Select(entry => AttentionItem.From(entry.Key, entry.Value!.AsObject())).ToArray();

    public static string Key(string kind, string boardId, string taskId, string subject) =>
        string.Join("|", new[] { kind, boardId, taskId, subject }.Select(Uri.EscapeDataString));
}

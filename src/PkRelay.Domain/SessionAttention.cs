using System.Globalization;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>
/// A session's attention queue, kept in its own state under <c>attention</c> beside the inbox, so items commit atomically and survive
/// restarts. Raise and close arrive through other aggregates' outboxes; offer runs once per boundary reply; respond is the agent's answer.
/// Nothing here touches the inbox: postponing or closing an item never acknowledges a message.
/// </summary>
internal static class SessionAttention
{
    /// <summary>Adds an item, or refreshes the open one with the same key. An older source revision than one already seen is ignored.</summary>
    public static RelayReply Raise(AggregateContext context) => Add(context.State, context.Args, context.Now);

    /// <summary>
    /// A wakeable inbox message is an <c>info</c> item (subject = the message ID), so like any lower-tier item it waits for a boundary
    /// reply or the idle-hold wake. Its text stays in the inbox; the offer only points to it.
    /// </summary>
    public static void AddMessage(JsonObject state, JsonObject message, int expiryDays, DateTimeOffset now)
    {
        var item = new JsonObject
        {
            ["kind"] = AttentionKinds.Info, ["subject"] = Text(message, "messageId"), ["expiryDays"] = expiryDays,
            ["note"] = Text(message, "taskId") is { Length: > 0 } task ? "task " + task + " changed; read it with check_inbox" : "unread message; read it with check_inbox"
        };
        if (Text(message, "boardId") is { Length: > 0 } board) item["boardId"] = board;
        Add(state, item, now);
    }

    /// <summary>Closes the <c>info</c> items of messages that were read or acknowledged.</summary>
    public static void CloseMessages(JsonObject state, IEnumerable<string> messageIds, DateTimeOffset now)
    {
        if (state["attention"] is not JsonObject items) return;
        var ids = messageIds.ToHashSet(StringComparer.Ordinal);
        foreach (var item in AttentionScheduler.Items(items).Where(item => item.Kind == AttentionKinds.Info && ids.Contains(item.Subject)).ToArray())
        {
            Tombstone(state, item.Key, items[item.Id]!.AsObject(), now);
            items.Remove(item.Id);
        }
    }

    private static RelayReply Add(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        if (!state.ContainsKey("principalId")) return Error(SessionUnknown);
        var kind = Text(args, "kind");
        if (!AttentionKinds.All.Contains(kind)) return Error(InvalidArguments, new { field = "kind" });
        var (taskId, boardId, subject) = (Optional(args, "taskId"), Optional(args, "boardId") is { Length: > 0 } board ? board : "default", Optional(args, "subject"));
        var key = AttentionScheduler.Key(kind, boardId, taskId, subject);
        var items = ObjectAt(state, "attention");
        RemoveExpired(items, now);
        var revision = Math.Max(0, Number(args, "revision"));
        if (revision > 0 && Number(ObjectAt(state, "attentionClosed"), key) >= revision) return Ok(new { raised = false, reason = "stale" });
        var existing = Find(items, key);
        if (existing is { } open && revision > 0 && Number(open.Item, "revision") > revision) return Ok(new { raised = false, reason = "stale", itemId = open.Id });
        var content = new JsonObject { ["kind"] = kind };
        Set(content, "taskId", taskId); Set(content, "boardId", boardId == "default" ? "" : boardId); Set(content, "subject", subject);
        Set(content, "blocker", Text(args, "blocker") is { Length: > 0 and <= RelayLimits.MaxAttentionBlockerLength } blocker ? blocker : "");
        if (AttentionItem.IsTrue(args, "blocking")) content["blocking"] = true;
        content["priority"] = Priority(args["priority"]);
        content["refs"] = new JsonArray(Refs(args["refs"]).Select(x => (JsonNode)x).ToArray());
        content["note"] = Cap(Text(args, "note"));
        // Without a revision, a redelivered copy of a raise for an item just closed is recognized by its content and ignored.
        if (revision == 0 && existing is null && RecentlyClosed(state, key, Fingerprint(content), now)) return Ok(new { raised = false, reason = "recentlyClosed" });

        var id = existing?.Id ?? "a-" + (Number(state, "attentionSeq") + 1);
        var item = existing?.Item ?? new JsonObject
        {
            ["id"] = id, ["createdAt"] = now.ToString("O"), ["snoozes"] = 0, ["snoozeBoundaries"] = 0, ["snoozeUntil"] = null, ["shownAt"] = null
        };
        foreach (var field in ContentFields)
            if (content[field] is { } value) item[field] = value.DeepClone(); else item.Remove(field);
        if (revision > 0) item["revision"] = revision;
        item["updatedAt"] = now.ToString("O");
        item["expiresAt"] = Expiry(args, now).ToString("O");
        if (existing is not null) return Ok(new { raised = true, itemId = id, refreshed = true });

        state["attentionSeq"] = Number(state, "attentionSeq") + 1;
        items[id] = item;
        if (items.Count <= RelayLimits.MaxAttentionItems) return Ok(new { raised = true, itemId = id, refreshed = false });
        var dropped = AttentionScheduler.DropCandidate(AttentionScheduler.Items(items))!.Id;
        items.Remove(dropped);
        return dropped == id ? Ok(new { raised = false, reason = "full" }) : Ok(new { raised = true, itemId = id, refreshed = false, dropped });
    }

    /// <summary>Closes the items of one task (all kinds, or only <c>kinds</c>), of one subject, or one item by ID.</summary>
    public static RelayReply Close(AggregateContext context)
    {
        var state = context.State;
        if (!state.ContainsKey("principalId")) return Error(SessionUnknown);
        var args = context.Args;
        var (taskId, boardId, subject, itemId) = (Optional(args, "taskId"), Optional(args, "boardId") is { Length: > 0 } board ? board : "default", Optional(args, "subject"), Optional(args, "itemId"));
        if (taskId.Length == 0 && subject.Length == 0 && itemId.Length == 0) return Error(InvalidArguments, new { required = "taskId" });
        var kinds = args["kinds"] is JsonArray list ? list.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var kind) ? kind : "").Where(AttentionKinds.All.Contains).ToHashSet(StringComparer.Ordinal) : null;
        var items = ObjectAt(state, "attention");
        RemoveExpired(items, context.Now);
        var revision = Math.Max(0, Number(args, "revision"));
        var closed = 0;
        foreach (var item in AttentionScheduler.Items(items))
        {
            var matches = itemId.Length > 0 ? item.Id == itemId
                : (taskId.Length == 0 || item.TaskId == taskId && item.BoardId == boardId) && (subject.Length == 0 || item.Subject == subject) && (kinds is null || kinds.Contains(item.Kind));
            if (!matches) continue;
            RememberClosed(state, item.Key, Math.Max(revision, Number(items[item.Id]!.AsObject(), "revision")));
            Tombstone(state, item.Key, items[item.Id]!.AsObject(), context.Now);
            items.Remove(item.Id); closed++;
        }
        // A close with a revision and named kinds also guards keys with no open item, so an older raise arriving later stays closed.
        if (revision > 0 && kinds is not null && itemId.Length == 0)
            foreach (var kind in kinds) RememberClosed(state, AttentionScheduler.Key(kind, boardId, taskId, subject), revision);
        return Ok(new { closed });
    }

    /// <summary>
    /// One boundary, in one fixed order: the shown, unanswered item counts as later; one boundary passes for snoozed items; the best
    /// eligible item is picked; it is marked shown. Call it only for a reply that will carry the offer, so an item counts as seen only
    /// when it was delivered.
    /// </summary>
    public static RelayReply Offer(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        var now = context.Now;
        var items = ObjectAt(state, "attention");
        RemoveExpired(items, now);
        // An idle chat is one with no boundary for a while; see Status.
        state["attentionBoundaryAt"] = now.ToString("O");
        // The control directory learns, at most hourly, that this chat is still active, so routed manager items keep reaching it.
        if (now - (DateTimeOffset.TryParse(Text(state, "activeReportedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reported) ? reported : DateTimeOffset.MinValue) >= TimeSpan.FromMinutes(RelayLimits.AttentionActivityReportMinutes)
            && Text(state, "sessionId") is { Length: > 0 } self)
        {
            state["activeReportedAt"] = now.ToString("O");
            context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.Register, new JsonObject { ["repositoryId"] = context.Repository, ["sessionId"] = self, ["activeAt"] = now.ToString("O") });
        }
        // Messages a model-visible check_inbox reply lists were seen, so their info items close before anything is picked.
        if (args["seenMessageIds"] is JsonArray seen) CloseMessages(state, Strings(seen), now);
        var boundaries = SnoozeBoundaries(args);
        foreach (var item in items.Select(x => x.Value).OfType<JsonObject>())
            if (item["shownAt"] is JsonValue)
            {
                item["shownAt"] = null;
                // A blocking item past its snooze cap stays eligible and may be shown again at once.
                Snooze(item, now, boundaries, null);
            }
        var sittingOut = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in items.Where(x => x.Value is JsonObject).ToArray())
        {
            var item = entry.Value!.AsObject();
            var view = AttentionItem.From(entry.Key, item);
            if (view.SnoozeUntil is { } until && now >= until) { EndSnooze(item); continue; }
            if (view.SnoozeBoundaries <= 0 || !view.IsSnoozed(now)) continue;
            item["snoozeBoundaries"] = view.SnoozeBoundaries - 1;
            if (view.SnoozeBoundaries == 1) EndSnooze(item);
            sittingOut.Add(entry.Key);
        }
        var candidates = AttentionScheduler.Items(items).Where(item => !sittingOut.Contains(item.Id)).ToArray();
        var actionable = AttentionScheduler.Eligible(candidates, now).Count();
        // "more" counts from the set Pick chooses from, so it never includes items for the focused task that cannot be offered now.
        var offerable = AttentionScheduler.Eligible(candidates, now, Optional(args, "focusTaskId"), Optional(args, "focusBoardId") is { Length: > 0 } board ? board : "default").ToArray();
        var picked = AttentionScheduler.Pick(offerable, now);
        if (picked is null) return Ok(new JsonObject { ["actionable"] = actionable });
        items[picked.Id]!["shownAt"] = now.ToString("O");
        return Ok(new JsonObject { ["attention"] = OfferJson(picked, offerable.Length - 1), ["actionable"] = actionable });
    }

    /// <summary>
    /// Read-only, for <c>get_status</c>: the count of actionable items (never snoozed or expired ones). With <paramref name="detail"/>
    /// it also gives the top item (ID, kind, task), the count of top-tier items, and <c>wake</c>: the item that may wake the chat now.
    /// That is the newest top-tier item's ID, or else, when an item has waited <c>maxHoldMinutes</c> since the chat's last boundary,
    /// <c>hold:&lt;ticks of that boundary&gt;</c>: one ID per idle period, so however many items wait, an idle chat gets one wake until
    /// its next boundary. The adapter wakes at most once per ID.
    /// </summary>
    public static JsonObject Status(JsonObject state, JsonObject args, DateTimeOffset now, bool detail)
    {
        var eligible = AttentionScheduler.Eligible(AttentionScheduler.Items(state["attention"] as JsonObject ?? new JsonObject()), now).ToArray();
        var status = new JsonObject { ["actionable"] = eligible.Length };
        if (!detail) return status;
        var urgent = eligible.Where(item => AttentionWeights.Wakes(item.Tier)).ToArray();
        status["urgent"] = urgent.Length;
        status["inbox"] = (state["inbox"] as JsonObject)?.Count ?? 0;
        if (AttentionScheduler.Pick(eligible, now) is { } top)
        {
            var shown = new JsonObject { ["id"] = top.Id, ["k"] = top.Kind };
            if (top.TaskId.Length > 0) shown["t"] = top.TaskId;
            if (top.BoardId != "default") shown["b"] = top.BoardId;
            status["top"] = shown;
        }
        var hold = TimeSpan.FromMinutes(Math.Clamp(Number(args, "maxHoldMinutes", RelayLimits.DefaultAttentionMaxHoldMinutes), 1, RelayLimits.MaxAttentionMaxHoldMinutes));
        var boundary = OptionalDate(state, "attentionBoundaryAt");
        DateTimeOffset Waiting(AttentionItem item) => boundary is { } at && at > item.CreatedAt ? at : item.CreatedAt;
        var wake = urgent.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id, StringComparer.Ordinal).FirstOrDefault()?.Id
            ?? (eligible.Any(item => now - Waiting(item) >= hold) ? AttentionWeights.HoldWakePrefix + (boundary?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture) : null);
        if (wake is not null) status["wake"] = wake;
        return status;
    }

    private static DateTimeOffset? OptionalDate(JsonObject node, string key) =>
        DateTimeOffset.TryParse(Text(node, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>The agent's answer: accept and dismiss close the item; later snoozes it for a number of boundaries or until a time.</summary>
    public static RelayReply Respond(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        var now = context.Now;
        var items = ObjectAt(state, "attention");
        RemoveExpired(items, now);
        var id = Required(args, "itemId");
        if (items[id] is not JsonObject item) return Error(NotFound, new { itemId = id });
        var action = Text(args, "action");
        switch (action)
        {
            case AttentionActions.Accept or AttentionActions.Dismiss:
                RememberClosed(state, AttentionItem.From(id, item).Key, Number(item, "revision"));
                Tombstone(state, AttentionItem.From(id, item).Key, item, now);
                items.Remove(id);
                return Ok(new { itemId = id, outcome = action == AttentionActions.Accept ? "accepted" : "dismissed", kind = Text(item, "kind") });
            case AttentionActions.Later:
                DateTimeOffset? until = null;
                if (args.ContainsKey("until"))
                {
                    if (!DateTimeOffset.TryParse(Text(args, "until"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) || time <= now) return Error(InvalidArguments, new { field = "until" });
                    until = time;
                }
                item["shownAt"] = null;
                if (!Snooze(item, now, SnoozeBoundaries(args), until))
                    return Error(SnoozeLimit, new { itemId = id, maxSnoozes = RelayLimits.MaxBlockingSnoozes, maxMinutes = RelayLimits.MaxBlockingSnoozeMinutes });
                var snoozed = AttentionItem.From(id, item);
                var reply = new JsonObject { ["itemId"] = id, ["outcome"] = "later", ["kind"] = Text(item, "kind") };
                if (snoozed.SnoozeBoundaries > 0) reply["snoozeBoundaries"] = snoozed.SnoozeBoundaries;
                if (snoozed.SnoozeUntil is { } end) reply["snoozeUntil"] = end.ToString("O");
                return Ok(reply);
            default:
                return Error(InvalidArguments, new { field = "action" });
        }
    }

    /// <summary>
    /// Snoozes an item for <paramref name="boundaries"/> boundaries, or until a time. An item that blocks another session gets at most
    /// <see cref="RelayLimits.MaxBlockingSnoozes"/> snoozes, all ending within <see cref="RelayLimits.MaxBlockingSnoozeMinutes"/> of the
    /// first; past that it is not snoozed and false is returned.
    /// </summary>
    private static bool Snooze(JsonObject item, DateTimeOffset now, int boundaries, DateTimeOffset? until)
    {
        DateTimeOffset? ceiling = null;
        if (AttentionItem.IsTrue(item, "blocking"))
        {
            var first = item.ContainsKey("firstSnoozedAt") ? Date(item, "firstSnoozedAt") : now;
            ceiling = first.AddMinutes(RelayLimits.MaxBlockingSnoozeMinutes);
            if (Number(item, "snoozes") >= RelayLimits.MaxBlockingSnoozes || now >= ceiling) return false;
            item["firstSnoozedAt"] = first.ToString("O");
        }
        item["snoozes"] = Number(item, "snoozes") + 1;
        if (until is { } time)
        {
            item["snoozeBoundaries"] = 0;
            item["snoozeUntil"] = (ceiling is { } cap && cap < time ? cap : time).ToString("O");
        }
        else
        {
            item["snoozeBoundaries"] = boundaries;
            // For a blocking item the time cap still ends a boundary snooze early.
            item["snoozeUntil"] = ceiling?.ToString("O");
        }
        return true;
    }

    private static void EndSnooze(JsonObject item) { item["snoozeBoundaries"] = 0; item["snoozeUntil"] = null; }

    /// <summary>The compact offer a boundary reply carries: ID, kind, task, board when not default, text with refs, and how many more wait.</summary>
    private static JsonObject OfferJson(AttentionItem item, int more)
    {
        var offer = new JsonObject { ["id"] = item.Id, ["k"] = item.Kind };
        if (item.TaskId.Length > 0) offer["t"] = item.TaskId;
        if (item.BoardId != "default") offer["b"] = item.BoardId;
        var head = item.TaskId.Length > 0 ? item.TaskId + " " + item.Kind : item.Kind;
        var text = item.Note.Length > 0 ? head + ": " + item.Note : head;
        if (item.Refs.Count > 0) text += " refs " + string.Join(",", item.Refs);
        offer["x"] = Cap(text);
        offer["more"] = Math.Max(0, more);
        return offer;
    }

    private static void RemoveExpired(JsonObject items, DateTimeOffset now)
    {
        foreach (var id in items.Where(x => x.Value is not JsonObject item || AttentionItem.From(x.Key, item).IsExpired(now)).Select(x => x.Key).ToArray()) items.Remove(id);
    }

    private static (string Id, JsonObject Item)? Find(JsonObject items, string key)
    {
        foreach (var entry in items)
            if (entry.Value is JsonObject item && AttentionItem.From(entry.Key, item).Key == key) return (entry.Key, item);
        return null;
    }

    /// <summary>Remembers the newest source revision a closed key had, newest last, bounded by <see cref="RelayLimits.MaxAttentionClosedKeys"/>.</summary>
    private static void RememberClosed(JsonObject state, string key, long revision)
    {
        if (revision <= 0) return;
        var closed = ObjectAt(state, "attentionClosed");
        var newest = Math.Max(revision, Number(closed, key));
        closed.Remove(key);
        closed[key] = newest;
        while (closed.Count > RelayLimits.MaxAttentionClosedKeys) closed.Remove(closed.First().Key);
    }

    /// <summary>The fields that make up an item's content; two raises with the same values are the same raise.</summary>
    private static readonly string[] ContentFields = ["kind", "taskId", "boardId", "subject", "blocker", "blocking", "priority", "refs", "note"];

    private static string Fingerprint(JsonObject item)
    {
        var content = new JsonObject();
        foreach (var field in ContentFields) content[field] = item[field]?.DeepClone();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content.ToJsonString())), 0, 12);
    }

    /// <summary>
    /// Remembers a closed item's content for <see cref="RelayLimits.AttentionTombstoneMinutes"/>, at most
    /// <see cref="RelayLimits.MaxAttentionTombstones"/> keys, so an outbox raise delivered twice cannot reopen it.
    /// </summary>
    private static void Tombstone(JsonObject state, string key, JsonObject item, DateTimeOffset now)
    {
        var recent = PruneTombstones(state, now);
        recent.Remove(key);
        recent[key] = new JsonObject { ["at"] = now.ToString("O"), ["fingerprint"] = Fingerprint(item) };
        while (recent.Count > RelayLimits.MaxAttentionTombstones) recent.Remove(recent.First().Key);
    }

    private static bool RecentlyClosed(JsonObject state, string key, string fingerprint, DateTimeOffset now) =>
        PruneTombstones(state, now)[key] is JsonObject tombstone && Text(tombstone, "fingerprint") == fingerprint;

    private static JsonObject PruneTombstones(JsonObject state, DateTimeOffset now)
    {
        var recent = ObjectAt(state, "attentionRecent");
        foreach (var stale in recent.Where(x => x.Value is not JsonObject tombstone || now >= Date(tombstone, "at").AddMinutes(RelayLimits.AttentionTombstoneMinutes)).Select(x => x.Key).ToArray())
            recent.Remove(stale);
        return recent;
    }

    private static DateTimeOffset Expiry(JsonObject args, DateTimeOffset now)
    {
        var latest = now.AddDays(RelayLimits.MaxAttentionExpiryDays);
        if (DateTimeOffset.TryParse(Text(args, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at > now) return at < latest ? at : latest;
        return now.AddDays(Math.Clamp(Number(args, "expiryDays", RelayLimits.DefaultAttentionExpiryDays), 1, RelayLimits.MaxAttentionExpiryDays));
    }

    private static int SnoozeBoundaries(JsonObject args) =>
        (int)Math.Clamp(Number(args, "snoozeBoundaries", RelayLimits.DefaultAttentionSnoozeBoundaries), 1, RelayLimits.MaxAttentionSnoozeBoundaries);

    /// <summary>A level from <see cref="AttentionWeights.Priorities"/>, or a task's numeric priority mapped to one; anything else is normal.</summary>
    private static string Priority(JsonNode? node)
    {
        if (node is not JsonValue value) return AttentionWeights.Normal;
        if (value.TryGetValue<string>(out var level)) return AttentionWeights.Priorities.Contains(level) ? level : AttentionWeights.Normal;
        return long.TryParse(value.ToJsonString(), out var number) ? AttentionWeights.PriorityLevel(number) : AttentionWeights.Normal;
    }

    private static IEnumerable<string> Refs(JsonNode? node) => (node as JsonArray ?? [])
        .OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var id) ? id : "")
        .Where(id => id.Length is > 0 and <= RelayLimits.MaxIdentifierLength).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxAttentionRefs);

    /// <summary>An optional identifier: empty when absent, refused when longer than the shared bound.</summary>
    private static string Optional(JsonObject args, string key) =>
        Text(args, key) is { Length: <= RelayLimits.MaxIdentifierLength } value ? value : throw new ArgumentException(key + " is at most 256 characters.");

    private static void Set(JsonObject item, string key, string value) { if (value.Length > 0) item[key] = value; else item.Remove(key); }

    private static string Cap(string text)
    {
        if (text.Length <= RelayLimits.MaxAttentionTextLength) return text;
        var length = RelayLimits.MaxAttentionTextLength - 1;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
}

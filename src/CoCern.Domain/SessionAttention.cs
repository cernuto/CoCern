using System.Globalization;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// A session's attention queue (<see cref="AttentionQueue"/>), kept in its own state beside the inbox, so items commit atomically and
/// survive restarts. Raise and close arrive through other aggregates' outboxes, or from the hub for the session whose call it serves; offer
/// runs once per boundary reply; respond is the agent's answer. Nothing here touches the inbox: postponing or closing an item never
/// acknowledges a message.
/// </summary>
internal static class SessionAttention
{
    /// <summary>
    /// An outbox's raise (<see cref="AttentionRaise"/>): adds an item, or refreshes the open one with the same key. An older source revision
    /// than one already seen is ignored.
    /// </summary>
    public static RelayReply Raise(AggregateContext context) =>
        AttentionRaise.Decode(context.Args).TryGet(out var raise, out var malformed) ? Raise(context, raise.Item) : Malformed(malformed);

    /// <summary>The hub's own item for this session (<see cref="AttentionSuggestion"/>), applied as a raise.</summary>
    public static RelayReply Suggest(AggregateContext context) =>
        AttentionSuggestion.Decode(context.Args).TryGet(out var suggestion, out var malformed) ? Raise(context, suggestion.Item) : Malformed(malformed);

    /// <summary>An outbox's close (<see cref="AttentionClose"/>).</summary>
    public static RelayReply Close(AggregateContext context) =>
        AttentionClose.Decode(context.Args).TryGet(out var close, out var malformed) ? Close(context, close.Selection) : Malformed(malformed);

    /// <summary>The hub's close of this session's <c>next_work</c> items for a task it claimed (<see cref="AttentionClaimed"/>).</summary>
    public static RelayReply Claimed(AggregateContext context) =>
        AttentionClaimed.Decode(context.Args).TryGet(out var claimed, out var malformed) ? Close(context, claimed.Selection) : Malformed(malformed);

    /// <summary>A closed session refuses attention, and its sources drop it.</summary>
    private static RelayReply Raise(AggregateContext context, AttentionContent item)
    {
        var state = new SessionState(context.State);
        return state.Closed ? Error(SessionUnknown, new { expired = true }) : Add(state, item, context.Now);
    }

    private static RelayReply Close(AggregateContext context, AttentionSelection selection)
    {
        var state = new SessionState(context.State);
        return state.Closed ? Error(SessionUnknown, new { expired = true }) : Close(state, selection, context.Now);
    }

    /// <summary>
    /// A wakeable inbox message is an <c>info</c> item (subject = the message ID). Direct messages from another session may wake it at
    /// once; other notices wait for a boundary reply or the idle hold. Its text stays in the inbox; the offer only points to it.
    /// </summary>
    public static void AddMessage(SessionState state, InboxMessage message, int expiryDays, DateTimeOffset now) => Add(state, new AttentionContent
    {
        Kind = AttentionKinds.Info, Subject = message.MessageId, ExpiryDays = expiryDays,
        Note = message.TaskId is { Length: > 0 } task ? "task " + task + " changed; read it with check_inbox" : "unread message; read it with check_inbox",
        BoardId = message.BoardId is { Length: > 0 } board ? board : null
    }, now);

    /// <summary>Closes the <c>info</c> items of messages that were read or acknowledged.</summary>
    public static void CloseMessages(SessionState state, IEnumerable<string> messageIds, DateTimeOffset now)
    {
        var queue = state.Attention;
        if (queue.StoredItems is not { } items) return;
        var ids = messageIds.ToHashSet(StringComparer.Ordinal);
        foreach (var item in AttentionScheduler.Items(items).Where(item => item.Kind == AttentionKinds.Info && ids.Contains(item.Subject)).ToArray())
        {
            Tombstone(queue, item.Key, items[item.Id]!.AsObject(), now);
            items.Remove(item.Id);
        }
    }

    private static RelayReply Add(SessionState state, AttentionContent raised, DateTimeOffset now)
    {
        if (!state.Opened) return Error(SessionUnknown);
        var kind = raised.Kind;
        if (!AttentionKinds.All.Contains(kind)) return Error(InvalidArguments, new { field = "kind" });
        var (taskId, boardId, subject) = (Optional(raised.TaskId, "taskId"), Optional(raised.BoardId, "boardId") is { Length: > 0 } board ? board : "default", Optional(raised.Subject, "subject"));
        var key = AttentionScheduler.Key(kind, boardId, taskId, subject);
        var queue = state.Attention;
        var items = queue.Items;
        RemoveExpired(items, now);
        var revision = Math.Max(0, raised.Revision ?? 0);
        if (revision > 0 && (queue.ClosedRevision(key) >= revision || taskId.Length > 0 && queue.ArchivedRevision(boardId, taskId) >= revision))
            return Ok(new { raised = false, reason = "stale" });
        var existing = Find(items, key);
        if (existing is { } open && revision > 0 && new QueuedItem(open.Item).Revision > revision) return Ok(new { raised = false, reason = "stale", itemId = open.Id });
        var content = new JsonObject { [QueuedItem.KindKey] = kind };
        Set(content, QueuedItem.TaskIdKey, taskId); Set(content, QueuedItem.BoardIdKey, boardId == "default" ? "" : boardId); Set(content, QueuedItem.SubjectKey, subject);
        Set(content, QueuedItem.BlockerKey, raised.Blocker is { Length: > 0 and <= RelayLimits.MaxAttentionBlockerLength } blocker ? blocker : "");
        if (raised.Blocking) content[QueuedItem.BlockingKey] = true;
        content[QueuedItem.PriorityKey] = raised.Priority is { } level && AttentionWeights.Priorities.Contains(level) ? level : AttentionWeights.Normal;
        content[QueuedItem.RefsKey] = AttentionContent.Strings(Refs(raised.Refs));
        content[QueuedItem.NoteKey] = Cap(raised.Note ?? "");
        // Without a revision, a redelivered copy of a raise for an item just closed is recognized by its content and ignored.
        if (revision == 0 && existing is null && RecentlyClosed(queue, key, Fingerprint(content), now)) return Ok(new { raised = false, reason = "recentlyClosed" });

        var id = existing?.Id ?? "a-" + (queue.Seq + 1);
        var stored = existing?.Item ?? QueuedItem.New(id, now);
        var item = new QueuedItem(stored);
        item.SetContent(content);
        if (revision > 0) item.Revision = revision;
        item.UpdatedAt = now;
        item.ExpiresAt = now.AddDays(Math.Clamp(raised.ExpiryDays ?? RelayLimits.DefaultAttentionExpiryDays, 1, RelayLimits.MaxAttentionExpiryDays));
        if (existing is not null) return Ok(new { raised = true, itemId = id, refreshed = true });

        queue.Seq += 1;
        items[id] = stored;
        if (items.Count <= RelayLimits.MaxAttentionItems) return Ok(new { raised = true, itemId = id, refreshed = false });
        var dropped = AttentionScheduler.DropCandidate(AttentionScheduler.Items(items))!.Id;
        items.Remove(dropped);
        return dropped == id ? Ok(new { raised = false, reason = "full" }) : Ok(new { raised = true, itemId = id, refreshed = false, dropped });
    }

    /// <summary>Closes the items of one task, or of several (<c>taskIds</c>, as archiving sends; all kinds, or only <c>kinds</c>), or of one subject.</summary>
    private static RelayReply Close(SessionState state, AttentionSelection selection, DateTimeOffset now)
    {
        if (!state.Opened) return Error(SessionUnknown);
        var (taskId, boardId, subject) = (Optional(selection.TaskId, "taskId"), Optional(selection.BoardId, "boardId") is { Length: > 0 } board ? board : "default", Optional(selection.Subject, "subject"));
        var taskIds = selection.TaskIds?.Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (taskId.Length == 0 && taskIds is not { Count: > 0 } && subject.Length == 0) return Error(InvalidArguments, new { required = "taskId" });
        var kinds = selection.Kinds?.Where(AttentionKinds.All.Contains).ToHashSet(StringComparer.Ordinal);
        var queue = state.Attention;
        var items = queue.Items;
        RemoveExpired(items, now);
        var revision = Math.Max(0, selection.Revision ?? 0);
        var closed = 0;
        foreach (var item in AttentionScheduler.Items(items))
        {
            var matches = (taskId.Length == 0 || item.TaskId == taskId && item.BoardId == boardId) && (taskIds is null || taskIds.Contains(item.TaskId) && item.BoardId == boardId)
                && (subject.Length == 0 || item.Subject == subject) && (kinds is null || kinds.Contains(item.Kind));
            if (!matches) continue;
            var stored = items[item.Id]!.AsObject();
            var itemRevision = new QueuedItem(stored).Revision;
            // An item with its own subject (an order's offer) counts revisions apart from a close that names no subject.
            var sameCounter = revision > 0 && (subject.Length > 0 || item.Subject.Length == 0);
            // A reordered close older than the item leaves it open.
            if (sameCounter && itemRevision > revision) continue;
            queue.RememberClosed(item.Key, sameCounter ? revision : itemRevision);
            Tombstone(queue, item.Key, stored, now);
            items.Remove(item.Id); closed++;
        }
        // A close with a revision and named kinds also guards keys with no open item, so an older raise arriving later stays closed.
        if (revision > 0 && kinds is not null)
            foreach (var kind in kinds) queue.RememberClosed(AttentionScheduler.Key(kind, boardId, taskId, subject), revision);
        // Archiving names tasks, not kinds: each is guarded as a whole, apart from the per-key guards it would otherwise flush.
        if (revision > 0 && kinds is null && taskIds is not null)
            foreach (var id in taskIds) queue.RememberArchived(boardId, id, revision);
        return Ok(new { closed });
    }

    /// <summary>
    /// Tells the control directory, at most once per <see cref="RelayLimits.AttentionActivityReportMinutes"/>, that this chat is still active,
    /// so routed manager items keep reaching it and a full directory never drops it. Runs on every verified call (and on offers), with or
    /// without attention.
    /// </summary>
    public static void ReportActive(AggregateContext context)
    {
        var state = new SessionState(context.State);
        var now = context.Now;
        if (now - (state.ActiveReportedAt ?? DateTimeOffset.MinValue) < TimeSpan.FromMinutes(RelayLimits.AttentionActivityReportMinutes) || state.SessionId is not { Length: > 0 } self) return;
        state.ActiveReportedAt = now;
        context.Send(AggregateKinds.ControlKey, new SessionActivity(context.Repository, self, now));
    }

    /// <summary>
    /// One boundary, in one fixed order: the shown, unanswered item counts as later; one boundary passes for snoozed items; the best
    /// eligible item is picked; it is marked shown. Call it only for a reply that will carry the offer, so an item counts as seen only
    /// when it was delivered.
    /// </summary>
    public static RelayReply Offer(AggregateContext context)
    {
        // Preview the same selection/aging rules on a private copy: no shown marker, snooze or boundary can move.
        if (context.Args["preview"] is JsonValue preview && preview.TryGetValue<bool>(out var previewOnly) && previewOnly)
        {
            var args = context.Args.DeepClone().AsObject(); args.Remove("preview");
            return Offer(new AggregateContext(context.State.DeepClone().AsObject(), context.Caller, context.Operation, args, context.Now));
        }
        if (!AttentionOffer.Decode(context.Args).TryGet(out var offer, out var malformed)) return Malformed(malformed);
        var queue = new SessionState(context.State).Attention;
        var now = context.Now;
        var items = queue.Items;
        RemoveExpired(items, now);
        // An idle chat is one with no boundary for a while; see Status.
        queue.BoundaryAt = now;
        ReportActive(context);
        var boundaries = SnoozeBoundaries(offer.SnoozeBoundaries ?? RelayLimits.DefaultAttentionSnoozeBoundaries);
        foreach (var item in items.Select(x => x.Value).OfType<JsonObject>().Select(stored => new QueuedItem(stored)))
            if (item.Shown)
            {
                item.ShownAt = null;
                // A blocking item past its snooze cap stays eligible and may be shown again at once.
                Snooze(item, now, boundaries, null);
            }
        var sittingOut = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in items.Where(x => x.Value is JsonObject).ToArray())
        {
            var item = new QueuedItem(entry.Value!.AsObject());
            var view = AttentionItem.From(entry.Key, entry.Value!.AsObject());
            if (view.SnoozeUntil is { } until && now >= until) { EndSnooze(item); continue; }
            if (view.SnoozeBoundaries <= 0 || !view.IsSnoozed(now)) continue;
            item.SnoozeBoundaries = view.SnoozeBoundaries - 1;
            if (view.SnoozeBoundaries == 1) EndSnooze(item);
            sittingOut.Add(entry.Key);
        }
        // Messages on the page this offer rides are not offered again; the hub closes them once it knows which ones the reply kept.
        if (offer.PageMessageIds is { } page)
        {
            var onPage = page.ToHashSet(StringComparer.Ordinal);
            foreach (var item in AttentionScheduler.Items(items).Where(item => item.Kind == AttentionKinds.Info && onPage.Contains(item.Subject))) sittingOut.Add(item.Id);
        }
        var candidates = AttentionScheduler.Items(items).Where(item => item.Kind != AttentionKinds.StandingProposal && !sittingOut.Contains(item.Id)).ToArray();
        var actionable = AttentionScheduler.Eligible(candidates, now).Count();
        // "more" counts from the set Pick chooses from, so it never includes items for the focused task that cannot be offered now.
        var offerable = AttentionScheduler.Eligible(candidates, now, Optional(offer.FocusTaskId, "focusTaskId"), Optional(offer.FocusBoardId, "focusBoardId") is { Length: > 0 } board ? board : "default").ToArray();
        // A requested Standing question reserves the ordinary slot without marking a tip shown and then replacing it.
        var urgentOnly = context.Args["urgentOnly"] is JsonValue urgent && urgent.TryGetValue<bool>(out var only) && only;
        var picked = AttentionScheduler.Pick(urgentOnly ? offerable.Where(item => AttentionWeights.Wakes(item.Tier)) : offerable, now);
        if (picked is null) return Ok(new JsonObject { ["actionable"] = actionable });
        new QueuedItem(items[picked.Id]!.AsObject()).ShownAt = now;
        return Ok(new JsonObject { ["attention"] = OfferJson(picked, offerable.Length - 1), ["actionable"] = actionable });
    }

    /// <summary>
    /// Read-only, for <c>get_status</c>: the count of actionable items (never snoozed or expired ones). With <paramref name="detail"/>
    /// it also gives the top item (ID, kind, task), the count of top-tier items, and <c>wake</c>: the item that may wake the chat now.
    /// That is the newest top-tier item's ID, an unread direct message's item ID, or else, when a non-tip item has waited
    /// <c>maxHoldMinutes</c> since the chat's last boundary,
    /// <c>hold:&lt;ticks of that boundary&gt;</c>: one ID per idle period, so however many items wait, an idle chat gets one wake until
    /// its next boundary. The adapter wakes at most once per ID.
    /// </summary>
    public static JsonObject Status(SessionState state, JsonObject args, DateTimeOffset now, bool detail)
    {
        var queue = state.Attention;
        var eligible = AttentionScheduler.Eligible(AttentionScheduler.Items(queue.StoredItems ?? new JsonObject()).Where(item => item.Kind != AttentionKinds.StandingProposal), now).ToArray();
        var status = new JsonObject { ["actionable"] = eligible.Length };
        if (!detail) return status;
        var urgent = eligible.Where(item => AttentionWeights.Wakes(item.Tier)).ToArray();
        status["urgent"] = urgent.Length;
        status["inbox"] = state.UnreadCount;
        if (AttentionScheduler.Pick(eligible, now) is { } top)
        {
            var shown = new JsonObject { ["id"] = top.Id, ["k"] = top.Kind };
            if (top.TaskId.Length > 0) shown["t"] = top.TaskId;
            if (top.BoardId != "default") shown["b"] = top.BoardId;
            status["top"] = shown;
        }
        var hold = TimeSpan.FromMinutes(Math.Clamp(Number(args, "maxHoldMinutes", RelayLimits.DefaultAttentionMaxHoldMinutes), 1, RelayLimits.MaxAttentionMaxHoldMinutes));
        var boundary = queue.BoundaryAt;
        DateTimeOffset Waiting(AttentionItem item) => boundary is { } at && at > item.CreatedAt ? at : item.CreatedAt;
        // Keep the existing info-item identity for deduplication, and match the inbox's structured sender/kind rather than its text.
        // Seen or acknowledged messages have no eligible info item, and wake:false deliveries never create one.
        var directMessages = state.Unread.Where(stored => InboxMessage.Decode(stored).TryGet(out var message, out _)
                && message.Wake && message.Kind is null && string.IsNullOrEmpty(message.TaskId) && !string.IsNullOrWhiteSpace(message.SenderSessionId))
            .Select(stored => Text(stored, InboxMessage.MessageIdKey)).ToHashSet(StringComparer.Ordinal);
        // Newest first: each ID wakes once, so a queued wake that never ran must not hold the candidate and block newer mail.
        var direct = eligible.Where(item => item.Kind == AttentionKinds.Info && directMessages.Contains(item.Subject))
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id, StringComparer.Ordinal).FirstOrDefault()?.Id;
        var wake = urgent.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id, StringComparer.Ordinal).FirstOrDefault()?.Id
            ?? direct
            ?? (eligible.Any(item => item.Kind != AttentionKinds.Tip && now - Waiting(item) >= hold) ? AttentionWeights.HoldWakePrefix + (boundary?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture) : null);
        if (wake is not null) status["wake"] = wake;
        return status;
    }

    /// <summary>The agent's answer: accept and dismiss close the item; later snoozes it for a number of boundaries or until a time.</summary>
    public static RelayReply Respond(AggregateContext context)
    {
        var queue = new SessionState(context.State).Attention;
        var args = context.Args;
        var now = context.Now;
        var items = queue.Items;
        RemoveExpired(items, now);
        var id = Required(args, "itemId");
        if (items[id] is not JsonObject stored) return Error(NotFound, new { itemId = id });
        var item = new QueuedItem(stored);
        var action = Text(args, "action");
        switch (action)
        {
            case AttentionActions.Accept or AttentionActions.Dismiss:
                queue.RememberClosed(AttentionItem.From(id, stored).Key, item.Revision);
                Tombstone(queue, AttentionItem.From(id, stored).Key, stored, now);
                items.Remove(id);
                return Ok(new { itemId = id, outcome = action == AttentionActions.Accept ? "accepted" : "dismissed", kind = item.Kind });
            case AttentionActions.Later:
                DateTimeOffset? until = null;
                if (args.ContainsKey("until"))
                {
                    if (!DateTimeOffset.TryParse(Text(args, "until"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) || time <= now) return Error(InvalidArguments, new { field = "until" });
                    until = time;
                }
                item.ShownAt = null;
                if (!Snooze(item, now, SnoozeBoundaries(Number(args, "snoozeBoundaries", RelayLimits.DefaultAttentionSnoozeBoundaries)), until))
                    return Error(SnoozeLimit, new { itemId = id, maxSnoozes = RelayLimits.MaxBlockingSnoozes, maxMinutes = RelayLimits.MaxBlockingSnoozeMinutes });
                var snoozed = AttentionItem.From(id, stored);
                var reply = new JsonObject { ["itemId"] = id, ["outcome"] = "later", ["kind"] = item.Kind };
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
    private static bool Snooze(QueuedItem item, DateTimeOffset now, int boundaries, DateTimeOffset? until)
    {
        DateTimeOffset? ceiling = null;
        if (item.Blocking)
        {
            var first = item.FirstSnoozedAt ?? now;
            ceiling = first.AddMinutes(RelayLimits.MaxBlockingSnoozeMinutes);
            if (item.Snoozes >= RelayLimits.MaxBlockingSnoozes || now >= ceiling) return false;
            item.FirstSnoozedAt = first;
        }
        item.Snoozes += 1;
        if (until is { } time)
        {
            item.SnoozeBoundaries = 0;
            item.SnoozeUntil = ceiling is { } cap && cap < time ? cap : time;
        }
        else
        {
            item.SnoozeBoundaries = boundaries;
            // For a blocking item the time cap still ends a boundary snooze early.
            item.SnoozeUntil = ceiling;
        }
        return true;
    }

    private static void EndSnooze(QueuedItem item) { item.SnoozeBoundaries = 0; item.SnoozeUntil = null; }

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

    private static string Fingerprint(JsonObject item) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(QueuedItem.Content(item).ToJsonString())), 0, 12);

    /// <summary>
    /// Remembers a closed item's content for <see cref="RelayLimits.AttentionTombstoneMinutes"/>, at most
    /// <see cref="RelayLimits.MaxAttentionTombstones"/> keys, so an outbox raise delivered twice cannot reopen it.
    /// </summary>
    private static void Tombstone(AttentionQueue queue, string key, JsonObject item, DateTimeOffset now)
    {
        var recent = queue.Recent(now);
        recent.Remove(key);
        recent[key] = RecentClose.New(now, Fingerprint(item));
        while (recent.Count > RelayLimits.MaxAttentionTombstones) recent.Remove(recent.First().Key);
    }

    private static bool RecentlyClosed(AttentionQueue queue, string key, string fingerprint, DateTimeOffset now) =>
        queue.Recent(now)[key] is JsonObject tombstone && new RecentClose(tombstone).Fingerprint == fingerprint;

    private static int SnoozeBoundaries(long boundaries) => (int)Math.Clamp(boundaries, 1, RelayLimits.MaxAttentionSnoozeBoundaries);

    private static IEnumerable<string> Refs(IReadOnlyList<string>? refs) => (refs ?? [])
        .Where(id => id.Length is > 0 and <= RelayLimits.MaxIdentifierLength).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxAttentionRefs);

    /// <summary>An optional identifier: empty when absent, refused when longer than the shared bound.</summary>
    private static string Optional(string? value, string key) =>
        (value ?? "") is { Length: <= RelayLimits.MaxIdentifierLength } text ? text : throw new ArgumentException(key + " is at most 256 characters.");

    private static void Set(JsonObject item, string key, string value) { if (value.Length > 0) item[key] = value; else item.Remove(key); }

    private static string Cap(string text)
    {
        if (text.Length <= RelayLimits.MaxAttentionTextLength) return text;
        var length = RelayLimits.MaxAttentionTextLength - 1;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// The hub singleton: session directory, worker presence, registered resources and their aliases, pause and cancellation requests. Its
/// handlers read and write the document through <see cref="ControlState"/>.
/// </summary>
internal static class ControlAggregate
{
    private const int MaxMachineSummaries = 50;
    private const int MaxNativeGenerations = 64;
    internal static readonly TimeSpan InitialFreshness = TimeSpan.FromMinutes(2);

    public static RelayReply Handle(AggregateContext context)
    {
        Required(context.Args, "repositoryId");
        return context.Operation switch
        {
            RelayTools.SessionOpen => OpenSession(context),
            Operations.Register => RegisterSession(context),
            Operations.SessionActive => RecordActivity(context),
            Operations.SessionOwns => TrackOwnership(context),
            Operations.SweepSessions => SweepSessions(context),
            Operations.SessionRestore => RestoreSession(context),
            Operations.SessionExpired => SettleClose(context),
            RelayTools.WorkerList => ListWorkers(context),
            RelayTools.GetStatus => Status(context),
            Operations.Resolve => ResolveResource(context),
            Operations.ResolveAll => ResolveAll(context),
            Operations.SessionOwners => OwnedSessions(context),
            Operations.SessionsOpen => OpenSessions(context),
            Operations.AttentionRoute => RouteAttention(context),
            RelayTools.ControlRequest => Control(context),
            _ => Error(UnknownTool)
        };
    }

    private static RelayReply OpenSession(AggregateContext context)
    {
        var args = context.Args;
        var participant = Text(args, NativeParticipant.Id);
        var state = new ControlState(context.State);
        if (participant.Length == 0) Required(args, "requestId");
        else if (ResumeNative(context, state, participant) is { } resume) return resume;
        if (state.SessionCount >= RelayLimits.MaxRecords) DropStaleSessions(context, state);
        // Every session was seen within StaleSessionHours, or still answers for an unresolved action: refused for now. The first clears as
        // sessions go stale; the second only once those actions are reported or recovered, since expiring their sessions would strand them.
        if (state.SessionCount >= RelayLimits.MaxRecords)
        {
            var cutoff = IdleCutoff(context.Now, TimeSpan.FromHours(RelayLimits.StaleSessionHours));
            var held = state.Entries().Count(entry => entry.Entry is { } record && record.OwnsWork(context.Now) && record.LastSeen <= cutoff);
            return Error(CapacityExceeded, new
            {
                staleAfterHours = RelayLimits.StaleSessionHours, heldByUnresolvedActions = held,
                hint = held > 0 ? "stale sessions own unresolved actions; report them, or recover them with action_report recover: true" : null
            });
        }
        var id = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // The role given at session_open routes manager-bound attention items; it grants nothing.
        var record = SessionRecord.Opened(id, token, context.Caller.PrincipalId, context.Repository, args).WithInitialFreshness(context.Now);
        state.Enter(record);
        if (record.NativeId is not null) state.TrackNative(record, Text(args, NativeParticipant.Request));
        context.Send(id, new SessionOpen(record));
        return OpenReply(id, token, args);
    }

    // A null answer permits exactly one new initialization in the same serialized directory operation.
    private static RelayReply? ResumeNative(AggregateContext context, ControlState state, string participant)
    {
        var args = context.Args;
        if (!context.Caller.IsInternal) return Error(InternalOnly);
        var generation = Required(args, NativeParticipant.Generation);
        var request = Required(args, NativeParticipant.Request);
        if (!state.CanTrackNative(participant)) return Error(CapacityExceeded);
        if (state.NativeJoin(participant) is { } join)
        {
            var currentId = Text(join, "sessionId");
            var currentGeneration = Text(join, "generation");
            var retired = join["retired"] as JsonObject;
            if (retired?.ContainsKey(generation) == true) return ExpiredNative();
            if (generation == currentGeneration && request != Text(join, "requestId")) return Error(RequestConflict);
            var duplicates = state.Entries().Where(row => row.Id != currentId && row.Entry is { } older && older.SessionId == row.Id
                && older.NativeId == participant && older.NativeGeneration.Length > 0
                && older.PrincipalId == context.Caller.PrincipalId && older.RepositoryId == context.Repository)
                .Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            if (duplicates.Count > 0)
            {
                // Only these exact, privately proven entries. Refusals restore their own session and clear its close fence.
                CloseSessions(context, state, (candidate, _) => duplicates.Contains(candidate), state.DuplicateCloseRoom, idle: true, force: false, nativeDuplicate: true);
                // The successful internal reply commits the close effects. The public boundary withholds a handle while they settle.
                return Ok(new { nativePending = true });
            }
            if (state.CurrentNativeSession(participant, context.Repository) is not null && state.Entry(currentId) is { } entry)
                return generation == currentGeneration && entry.PrincipalId == context.Caller.PrincipalId
                    ? OpenReply(currentId, entry.Token, args) : Error(RequestConflict);
            if (state.Entry(currentId) is not null) return Error(Unavailable);
            if (Text(args, NativeParticipant.ConfirmedExpiry) == currentId)
            {
                join["expired"] = true;
                state.Unfence(currentId);
            }
            if (!Flag(join, "expired")) return Error(Unavailable, new { nativeCheck = currentId });
            if (generation == currentGeneration) return ExpiredNative();
            if (state.NativeClosing(participant) || state.Entries().Any(row => row.Entry?.NativeId == participant)) return Error(Unavailable);
            if (retired?.Count >= MaxNativeGenerations - 1) return Error(CapacityExceeded);
            if (request == Text(join, "requestId") || retired?.Any(pair => pair.Value?.ToString() == request) == true) return Error(RequestConflict);
        }
        else if (state.NativeClosing(participant) || state.Entries().Any(row => row.Entry?.NativeId == participant))
            return Error(Unavailable);
        return null;
    }

    private static RelayReply OpenReply(string id, string token, JsonObject args) =>
        Ok(new { sessionId = id, sessionToken = token, bindingVersion = 0, binding = args["binding"] });

    private static RelayReply ExpiredNative() => Error(SessionUnknown, new { expired = true, reopen = RelayTools.SessionOpen });

    /// <summary>
    /// Makes room in a full directory: drops sessions not seen (fresh, active or observed) for <see cref="RelayLimits.StaleSessionHours"/>,
    /// least recently seen first, down to <see cref="RelayLimits.RetainedEntriesAfterPrune"/>, so it runs at most once per 512 opens.
    /// A session seen within that time is never dropped, nor one that answers for an unresolved action (its expiry would leave nobody able
    /// to report it). Each dropped session is sent <see cref="Operations.SessionExpire"/>, under its own repository, so its chat gets a
    /// clean <c>SessionUnknown</c> and opens again. This is the directory's capacity valve, so unread mail does not keep a session here.
    /// </summary>
    private static void DropStaleSessions(AggregateContext context, ControlState state)
    {
        var cutoff = IdleCutoff(context.Now, TimeSpan.FromHours(RelayLimits.StaleSessionHours));
        CloseSessions(context, state, (_, session) => session is null || session.LastSeen <= cutoff, state.SessionCount - RelayLimits.RetainedEntriesAfterPrune, idle: false);
    }

    /// <summary>
    /// The latest last-seen time a session may have and still count as idle for <paramref name="idle"/>. A working session reports its
    /// activity at most once per <see cref="RelayLimits.AttentionActivityReportMinutes"/>, so the directory may see it that much earlier
    /// than its last call; judging by that time alone, "idle for an hour" would close a chat that called 59 minutes after its last report.
    /// The report interval is added, so a session closed as idle made no call for at least <paramref name="idle"/> (once its last report
    /// has left its outbox); one may stay up to the interval longer.
    /// </summary>
    private static DateTimeOffset IdleCutoff(DateTimeOffset now, TimeSpan idle) => now - idle - TimeSpan.FromMinutes(RelayLimits.AttentionActivityReportMinutes);

    /// <summary>What one close did: the IDs it closed, least recently seen first, how many it kept for unresolved actions or for unread mail, and whether it left matching sessions for a later run.</summary>
    private readonly record struct Closing(List<string> Closed, int Kept, int Held, bool More);

    /// <summary>
    /// The one way the directory closes sessions (a full directory's cleanup, the hub's sweep, <c>close_sessions</c>): of the entries
    /// <paramref name="matches"/> accepts, closes up to <paramref name="limit"/>, least recently seen first.
    /// <list type="bullet">
    /// <item>One that answers for an unresolved action is never closed (its expiry would leave nobody able to report it) and counts as kept.
    /// The session checks again when its expiry arrives, since a report that it took an action may still be on its way here.</item>
    /// <item>An <paramref name="idle"/> close (the sweep, <c>close_sessions</c> with <c>idleMinutes</c>) skips a session held for unread mail
    /// (see <see cref="MailHold"/>) and counts it as held. Any other session it closes is asked to refuse if it has unread mail, unless its
    /// hold has lapsed; a label-only close or a full directory closes it, mail and all.</item>
    /// </list>
    /// Each closed session leaves the directory and is sent <see cref="Operations.SessionExpire"/>, under its own repository, so its chat
    /// gets a clean <c>SessionUnknown</c> and opens again. One that refuses sends <see cref="Operations.SessionRestore"/> and comes back.
    /// Each close is fenced (see <see cref="ControlState.Fence"/>), so only the session's answer to this close can bring it back.
    /// </summary>
    private static Closing CloseSessions(AggregateContext context, ControlState state, Func<string, DirectoryEntry?, bool> matches, int limit, bool idle, bool force = false, bool nativeDuplicate = false)
    {
        var now = context.Now;
        var matched = state.Entries().Where(entry => matches(entry.Id, entry.Entry))
            .Select(entry => (Key: entry.Id, Session: entry.Entry, Seen: entry.Entry?.LastSeen ?? DateTimeOffset.MinValue,
                Owns: entry.Entry is { } owner && owner.OwnsWork(now), Hold: entry.Entry is { } holder ? MailHold(holder, now) : Hold.None)).ToList();
        var closable = matched.Where(entry => force || !entry.Owns && !(idle && entry.Hold == Hold.Holding))
            // A forced close takes sessions without mail waiting first.
            .OrderBy(entry => entry.Hold == Hold.Holding).ThenBy(entry => entry.Seen).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToList();
        var closing = closable.Take(Math.Max(0, limit)).ToList();
        foreach (var (id, session, seen, _, hold) in closing)
        {
            state.Leave(id);
            var repository = session?.RepositoryId ?? "";
            var expire = new SessionExpire(repository, id, state.Fence(id, now, session?.NativeId, nativeDuplicate),
                // What the directory had heard when it decided; a session that has reported activity since was working, and refuses.
                SeenAt: !force && seen > DateTimeOffset.MinValue ? seen : null,
                // A session whose hold lapsed already had its grace, so this idle close takes it as it is.
                Idle: idle && hold == Hold.None, Force: force, RequestedBy: force ? context.Caller.PrincipalId : null);
            // Its own caller: the session belongs to its repository, which the caller that closed it may not reach (or may reach them all).
            context.Send(id, expire, new RelayCaller("relay-directory", false, [repository]));
        }
        return new(closing.Select(entry => entry.Key).ToList(), force ? 0 : matched.Count(entry => entry.Owns), idle && !force ? matched.Count(entry => !entry.Owns && entry.Hold == Hold.Holding) : 0,
            closable.Count > closing.Count);
    }

    /// <summary>
    /// <see cref="Operations.SweepSessions"/>: the hub's periodic sweep, across every repository, closes sessions not seen (fresh, active or
    /// observed) for <c>idleHours</c> (see <see cref="IdleCutoff"/>). Replies how many it closed, how many idle ones it kept because they
    /// answer for unresolved actions (<c>kept</c>) or hold unread mail (<c>held</c>), and <c>more</c> when it left some for the next run.
    /// </summary>
    private static RelayReply SweepSessions(AggregateContext context)
    {
        if (!SessionSweep.Decode(context.Args).TryGet(out var sweep, out var malformed)) return Malformed(malformed);
        var hours = sweep.IdleHours;
        if (hours <= 0) return Error(InvalidArguments, new { field = "idleHours" });
        var cutoff = IdleCutoff(context.Now, TimeSpan.FromHours(hours));
        var result = CloseSessions(context, new ControlState(context.State), (_, session) => session is null || session.LastSeen <= cutoff, RelayLimits.MaxSessionsClosedPerCall, idle: true);
        return Ok(new { closed = result.Closed.Count, kept = result.Kept, held = result.Held, more = result.More });
    }

    /// <summary>
    /// <c>control_request close_sessions</c>: closes this repository's sessions not seen for <c>idleMinutes</c> (at least
    /// <see cref="RelayLimits.MinCloseSessionsIdleMinutes"/>; see <see cref="IdleCutoff"/>) and/or whose label starts with <c>labelPrefix</c>
    /// (ignoring case); with both, a session must match both. The caller's own session is never closed. With <c>idleMinutes</c> it is an
    /// idle close, so sessions holding unread mail stay (<c>held</c>); a label-only close takes them. Replies the counts closed, kept and
    /// held, the first closed IDs, and <c>more</c> when matching sessions are left for another call.
    /// An exact <c>targetSessionId</c> replaces the bulk filters; only this form permits <c>force: true</c>, which also overrides unresolved work.
    /// </summary>
    private static RelayReply CloseSessionsByFilter(AggregateContext context)
    {
        var args = context.Args;
        var force = false;
        if (args["force"] is { } forceNode && (forceNode is not JsonValue flag || !flag.TryGetValue(out force)))
            return Error(InvalidArguments, new { field = "force" });
        string? target = null;
        if (args["targetSessionId"] is { } targetNode)
        {
            if (targetNode is not JsonValue text || !text.TryGetValue<string>(out var parsed) || string.IsNullOrWhiteSpace(parsed) || parsed.Length > RelayLimits.MaxIdentifierLength)
                return Error(InvalidArguments, new { field = "targetSessionId" });
            target = parsed;
            if (args["idleMinutes"] is not null || args["labelPrefix"] is not null)
                return Error(InvalidArguments, new { field = "targetSessionId", hint = "choose one session or a bulk filter" });
        }
        if (force && target is null) return Error(InvalidArguments, new { field = "force", hint = "force requires targetSessionId" });
        long? minutes = null;
        if (args["idleMinutes"] is { } idle)
        {
            // A whole JSON number only: a quoted or fractional value would read as some other idle time.
            if (idle is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !long.TryParse(value.ToJsonString(), out var parsed) || parsed < RelayLimits.MinCloseSessionsIdleMinutes)
                return Error(InvalidArguments, new { field = "idleMinutes", minimum = RelayLimits.MinCloseSessionsIdleMinutes });
            minutes = parsed;
        }
        string? prefix = null;
        if (args["labelPrefix"] is { } label)
        {
            if (label is not JsonValue text || !text.TryGetValue<string>(out var parsed) || string.IsNullOrWhiteSpace(parsed) || parsed.Length > RelayLimits.MaxIdentifierLength)
                return Error(InvalidArguments, new { field = "labelPrefix" });
            prefix = parsed;
        }
        if (minutes is null && prefix is null && target is null) return Error(InvalidArguments, new { field = "idleMinutes", hint = "give targetSessionId, idleMinutes, labelPrefix or both bulk filters" });
        // A close by label alone skips chats working right now (a call within LabelCloseActiveMinutes), counted as active; an idle filter or
        // one exact target says how quiet a session must be.
        var labelOnly = minutes is null && target is null;
        var cutoff = minutes is { } span ? IdleCutoff(context.Now, TimeSpan.FromMinutes(span))
            : labelOnly ? IdleCutoff(context.Now, TimeSpan.FromMinutes(RelayLimits.LabelCloseActiveMinutes)) : DateTimeOffset.MaxValue;
        var own = context.SessionId;
        var state = new ControlState(context.State);
        bool Matches(string id, DirectoryEntry? session) => session is not null && id != own && session.RepositoryId == context.Repository
            && (target is null || id == target) && (prefix is null || session.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var active = labelOnly ? state.Entries().Count(entry => Matches(entry.Id, entry.Entry) && entry.Entry!.LastSeen > cutoff) : 0;
        var result = CloseSessions(context, state, (id, session) => Matches(id, session) && session!.LastSeen <= cutoff, RelayLimits.MaxSessionsClosedPerCall,
            idle: minutes is not null, force: force);
        return Ok(new { closed = result.Closed.Count, kept = result.Kept, held = result.Held, active, more = result.More, ids = result.Closed.Take(RelayLimits.MaxClosedSessionIds).ToArray() });
    }

    private enum Hold { None, Holding, Lapsed }

    /// <summary>
    /// Whether the record is held open for unread mail. A session refuses an idle expiry while its inbox has unread messages (a chat that
    /// cannot be woken may idle for a day waiting on mail, which its expiry would delete), and the directory then remembers when. The hold
    /// lasts while the session is not seen again, for <see cref="RelayLimits.SessionMailHoldDays"/>; after that it has lapsed and the next
    /// idle close takes the session as it is. Once the session is seen again the hold no longer applies: its next idle close asks again.
    /// </summary>
    private static Hold MailHold(DirectoryEntry record, DateTimeOffset now)
    {
        if (record.MailHeldAt is not { } held || record.LastSeen > held) return Hold.None;
        return held > now.AddDays(-RelayLimits.SessionMailHoldDays) ? Hold.Holding : Hold.Lapsed;
    }

    /// <summary>
    /// <see cref="Operations.SessionRestore"/>: a session this directory closed refused its expiry, because it answers for an unresolved
    /// action (a report of it may not have arrived here before the close) or, for an idle close, because it has unread mail. Its record comes
    /// back from the session's copy (<see cref="SessionRestore.Record"/>), with ownership (ordered by the session's report number) or the
    /// mail hold, so later closes keep it. It returns even to a full directory: it never left the hub. A session still listed only gains the
    /// ownership or hold; one in another repository is ignored.
    /// </summary>
    private static RelayReply RestoreSession(AggregateContext context)
    {
        if (!SessionRestore.Decode(context.Args).TryGet(out var restore, out var malformed)) return Malformed(malformed);
        var id = restore.SessionId;
        var state = new ControlState(context.State);
        var record = state.Entry(id);
        // Only the answer to this directory's latest close of the session counts, once: a delayed or duplicated restore from an earlier
        // close would otherwise bring back a session that has since closed (a forced close, or a later one it accepted).
        if (!state.Fenced(id, restore.CloseSeq)) return Ok(new { restored = false });
        if (record is null) record = state.Enter(restore.Record with { SessionId = id, RepositoryId = context.Repository });
        else if (record.RepositoryId != context.Repository) return Ok(new { restored = false });
        // A redelivered restore never undoes a newer report that the work ended.
        if (restore.Owns && restore.Seq >= record.WorkSeq)
        {
            record.OwnsWorkAt = context.Now.ToUnixTimeSeconds();
            record.WorkSeq = restore.Seq;
        }
        if (restore.Mail) record.MailHeldAt = context.Now;
        state.Unfence(id);
        return Ok(new { restored = true });
    }

    /// <summary><see cref="Operations.SessionExpired"/>: a session closed as this directory asked, so that close is settled. Any other copy is ignored.</summary>
    private static RelayReply SettleClose(AggregateContext context)
    {
        if (!SessionExpired.Decode(context.Args).TryGet(out var expired, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        if (!state.Fenced(expired.SessionId, expired.CloseSeq)) return Ok(new { settled = false });
        state.ConfirmNativeExpiry(expired.SessionId);
        state.Unfence(expired.SessionId);
        return Ok(new { settled = true });
    }

    /// <summary>
    /// <see cref="Operations.SessionOwns"/>: a session's report that it now answers for unresolved work, or no longer does, sent only when
    /// that changes and as a daily refresh, so each action does not write this directory. A report numbered no later than the last one
    /// applied (<c>seq</c>) is stale and ignored, so reordered or repeated reports never flip it back. A session in another repository is
    /// ignored; one the directory does not list is closed (<see cref="CloseUnlisted"/>).
    /// </summary>
    private static RelayReply TrackOwnership(AggregateContext context)
    {
        if (!SessionOwnership.Decode(context.Args).TryGet(out var report, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        if (state.Entry(report.SessionId) is not { } session) return CloseUnlisted(context, state, report.SessionId, Ok(new { tracked = false, unlisted = true }));
        if (session.RepositoryId != context.Repository) return Ok(new { tracked = false });
        if (report.Seq <= session.WorkSeq) return Ok(new { tracked = false });
        session.WorkSeq = report.Seq;
        session.OwnsWorkAt = report.Owns ? WorkLedger.ReportedAt(report.At, context.Now) : null;
        return Ok(new { tracked = true });
    }

    /// <summary>
    /// <see cref="Operations.Register"/>: a session's presence after <c>session_bind</c>. Registrations can arrive reordered (a retried
    /// delivery), so one older than the binding the directory has (<c>bindingVersion</c>) is stale: it changes no binding or label, and
    /// either way times never move back. One from a session the directory does not list changes nothing there and closes the session
    /// (<see cref="CloseUnlisted"/>).
    /// </summary>
    private static RelayReply RegisterSession(AggregateContext context)
    {
        if (!SessionRegistration.Decode(context.Args).TryGet(out var registration, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        if (state.Entry(registration.SessionId) is not { } session) return CloseUnlisted(context, state, registration.SessionId, Ok(new { registered = false, unlisted = true }));
        if (session.RepositoryId != context.Repository) return Error(SessionDenied);
        var presence = registration.Presence;
        var stale = presence.BindingVersion < session.BindingVersion;
        session.Register(stale ? new SessionPresence { ObservedAt = presence.ObservedAt, FreshUntil = presence.FreshUntil } : presence);
        return Ok(new { registered = !stale });
    }

    /// <summary>
    /// <see cref="Operations.SessionActive"/>: a session's report that it was active, which keeps routed manager items reaching it and a full
    /// directory from dropping it. One no later than the activity the directory has is stale and changes nothing; one from a session the
    /// directory does not list closes the session (<see cref="CloseUnlisted"/>).
    /// </summary>
    private static RelayReply RecordActivity(AggregateContext context)
    {
        if (!SessionActivity.Decode(context.Args).TryGet(out var activity, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        if (state.Entry(activity.SessionId) is not { } session) return CloseUnlisted(context, state, activity.SessionId, Ok(new { recorded = false, unlisted = true }));
        if (session.RepositoryId != context.Repository) return Error(SessionDenied);
        if (activity.ActiveAt <= session.ActiveAt) return Ok(new { recorded = false });
        session.ActiveAt = activity.ActiveAt;
        return Ok(new { recorded = true });
    }

    /// <summary>
    /// A report (activity, a bind's presence, ownership) from a session this directory does not list. Either the session closed and the report
    /// was already on its way, or it is a ghost: open on its own grain, but invisible to <c>worker_list</c>, routing, closes and the
    /// dashboard, because a close's answer was lost. Nothing else would ever repair a ghost, so the directory closes it as it closes any
    /// session. One that still answers for work refuses and restores itself into the directory; any other closes, and its chat opens a new
    /// session. A session that had already closed acknowledges at once.
    /// <para>While a close of the session still awaits its answer (<see cref="ControlState.AwaitingAnswer"/>) nothing is sent: that answer
    /// may restore it, and this report may be the very activity that made the session refuse a close by label. A new close then would
    /// close a chat that is working. <paramref name="reply"/> is what the report itself is answered.</para>
    /// </summary>
    private static RelayReply CloseUnlisted(AggregateContext context, ControlState state, string sessionId, RelayReply reply)
    {
        if (state.AwaitingAnswer(sessionId, context.Now)) return reply;
        // Its own caller, as for any close: the session belongs to the report's repository.
        context.Send(sessionId, new SessionExpire(context.Repository, sessionId, state.Fence(sessionId, context.Now)), new RelayCaller("relay-directory", false, [context.Repository]));
        return reply;
    }

    /// <summary>Reported presence only. A record from another hub incarnation or past its freshness is shown as stale.</summary>
    private static RelayReply ListWorkers(AggregateContext context)
    {
        var hubIncarnation = Text(context.Args, "hubIncarnation");
        var workers = new ControlState(context.State).Entries().Select(entry => entry.Entry).OfType<DirectoryEntry>()
            .Where(entry => entry.RepositoryId == context.Repository).Select(entry => (JsonNode)entry.Worker(context.Now, hubIncarnation)).ToArray();
        var data = JsonNode.Parse(Page(workers, context.Args).DataJson)!.AsObject();
        var machines = workers.GroupBy(x => Text(x.AsObject(), "machineId")).Where(x => x.Key.Length > 0).Select(group => new
        {
            machineId = group.Key,
            sessionCount = group.Count(),
            availability = group.Any(x => Text(x.AsObject(), "availability") == "active") ? "active" : "unknown"
        }).ToArray();
        data["machines"] = JsonSerializer.SerializeToNode(machines.Take(MaxMachineSummaries)); data["machineSummariesTruncated"] = machines.Length > MaxMachineSummaries;
        // The page fit the reply budget on its own; with the machine summary it may not. Keep the largest prefix that fits with it, and move the
        // cursor to the first item left out, so the page is shortened rather than degraded and paging continues from the cut.
        var page = data["items"]!.AsArray();
        var offset = (int)Math.Clamp(Number(context.Args, "cursor"), 0, RelayLimits.MaxRecords);
        if (page.Count > 1 && !FitsWorkers(data, page, offset, page.Count))
        {
            var low = 1; var high = page.Count - 1;
            while (low < high) { var middle = (low + high + 1) / 2; if (FitsWorkers(data, page, offset, middle)) low = middle; else high = middle - 1; }
            data["items"] = new JsonArray(page.Take(low).Select(x => x!.DeepClone()).ToArray()); data["nextCursor"] = offset + low; data["truncated"] = true;
        }
        var machinePage = data["machines"]!.AsArray();
        while (!FitsWire(Ok(data), RelayLimits.AggregateReplyBytes) && machinePage.Count > 0) { machinePage.RemoveAt(machinePage.Count - 1); data["machineSummariesTruncated"] = true; }
        return Ok(data);
    }

    /// <summary>Whether the worker reply fits the aggregate budget with only the first <paramref name="count"/> of <paramref name="page"/>, as the wire measures it.</summary>
    private static bool FitsWorkers(JsonObject data, JsonArray page, int offset, int count)
    {
        var probe = data.DeepClone().AsObject();
        if (count < page.Count) { probe["items"] = new JsonArray(page.Take(count).Select(x => x!.DeepClone()).ToArray()); probe["nextCursor"] = offset + count; probe["truncated"] = true; }
        return FitsWire(Ok(probe), RelayLimits.AggregateReplyBytes);
    }

    private static RelayReply Status(AggregateContext context)
    {
        var sessionId = context.SessionId;
        var state = new ControlState(context.State);
        long? before = null;
        if (context.Args.ContainsKey("managerHistoryBefore"))
        {
            if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
            if (!ManagerSelection.WholeNumber(context.Args["managerHistoryBefore"], out var parsed)) return Error(InvalidArguments);
            before = parsed;
        }
        var historyOnly = false;
        if (context.Args.ContainsKey("managerHistoryOnly"))
        {
            if (context.Args["managerHistoryOnly"] is not JsonValue mode || !mode.TryGetValue(out historyOnly)) return Error(InvalidArguments);
            if (historyOnly && !context.Caller.IsAdministrator)
            {
                var summary = ManagerSelection.VisibleSummary(state, context.Caller, context.Now);
                return Ok(new { paused = state.Paused, cancellations = state.CancellationCount, requests = Array.Empty<object>(), requestsOmitted = true, manager = summary });
            }
        }
        var requests = !historyOnly && sessionId.Length > 0 ? state.CancellationsOf(sessionId).Take(RelayLimits.MaxPageSize).Select(x => new { target = x.Target, request = x.Request }).ToArray() : [];
        RelayReply Reply(CoCern.Contracts.ManagerSelectionStatus manager) => Ok(new { paused = state.Paused, cancellations = state.CancellationCount, requests, requestsOmitted = historyOnly, manager });
        var selection = ManagerSelection.Status(state, context.Caller, context.Now, before);
        var fitted = ManagerSelection.Fit(selection, candidate => FitsWire(Reply(candidate), RelayLimits.AggregateReplyBytes), before);
        var augmented = Reply(fitted);
        if (historyOnly) return fitted.HistoryReadRequired ? Error(PayloadTooLarge) : augmented;
        return FitsWire(augmented, RelayLimits.AggregateReplyBytes) ? augmented
            : Ok(new { paused = state.Paused, cancellations = state.CancellationCount, requests });
    }

    /// <summary>
    /// Passes one attention raise or close to every session in the repository whose role (from <c>session_open</c>, ignoring case) matches,
    /// except the session that caused it and sessions neither fresh nor active (a boundary reply, reported hourly as <c>activeAt</c>) for
    /// more than <see cref="RelayLimits.MaxAttentionRouteIdleHours"/> (closed chats); at most <see cref="RelayLimits.MaxAttentionRouteTargets"/>, most recently seen first.
    /// </summary>
    private static RelayReply RouteAttention(AggregateContext context)
    {
        if (!AttentionRoute.Decode(context.Args).TryGet(out var route, out var malformed)) return Malformed(malformed);
        var role = route.Role;
        // A raise goes to recently active managers only; a close reaches every manager, or an idle one would keep a stale item until it expires.
        var closing = route.Close is not null;
        var targets = ObjectAt(context.State, "sessions").Select(x => x.Value).OfType<JsonObject>()
            .Where(session => Text(session, "repositoryId") == context.Repository && string.Equals(Text(session, "role"), role, StringComparison.OrdinalIgnoreCase) && Text(session, "sessionId") != route.ExceptSessionId
                && (closing || Max(Date(session, "freshUntil"), Date(session, "activeAt")) > context.Now.AddHours(-RelayLimits.MaxAttentionRouteIdleHours)))
            .OrderByDescending(session => Date(session, "observedAt")).Take(closing ? RelayLimits.MaxRecords : RelayLimits.MaxAttentionRouteTargets).Select(session => Text(session, "sessionId")).ToArray();
        foreach (var target in targets)
        {
            if (route.Raise is { } raise) context.Send(target, raise with { RepositoryId = context.Repository });
            else context.Send(target, route.Close! with { RepositoryId = context.Repository });
        }
        return Ok(new { routed = targets.Length });
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    /// <summary>The given session IDs that the caller's principal opened in this repository. Read-only; tokens and other principals are never returned.</summary>
    private static RelayReply OwnedSessions(AggregateContext context)
    {
        if (!SessionOwnersQuery.Decode(context.Args).TryGet(out var query, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        var owned = query.SessionIds.Distinct(StringComparer.Ordinal)
            .Where(id => state.Entry(id) is { } session && session.PrincipalId == context.Caller.PrincipalId && session.RepositoryId == context.Repository).ToArray();
        return Ok(new { owned });
    }

    /// <summary>The given session IDs still open in this repository, whoever opened them. Read-only; nothing else about them is returned.</summary>
    private static RelayReply OpenSessions(AggregateContext context)
    {
        if (!SessionsOpenQuery.Decode(context.Args).TryGet(out var query, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        var open = query.SessionIds.Distinct(StringComparer.Ordinal).Where(id => state.Entry(id) is { } session && session.RepositoryId == context.Repository).ToArray();
        return Ok(new { open });
    }

    /// <summary>Maps a resource ID or a machine-scoped alias to its canonical registered resource.</summary>
    private static RelayReply ResolveResource(AggregateContext context)
    {
        if (!ResourceResolve.Decode(context.Args).TryGet(out var resolve, out var malformed)) return Malformed(malformed);
        var name = ResourceName(resolve.ResourceId);
        return new ControlState(context.State).Resources.Find(context.Repository, resolve.MachineId, name) is { } resolved ? Ok(resolved.ToReply()) : Error(ResourceUnknown);
    }

    /// <summary>A resource name a client gave: nonempty and within the identifier bound, or the call fails as invalid arguments.</summary>
    private static string ResourceName(string name) =>
        name is { Length: > 0 and <= RelayLimits.MaxIdentifierLength } ? name : throw new ArgumentException("resourceId is required and at most 256 characters.");

    /// <summary>Read-only: the canonical ID of each named resource this repository registered, in one call. Unknown names are left out.</summary>
    private static RelayReply ResolveAll(AggregateContext context)
    {
        if (!ResourceResolveAll.Decode(context.Args).TryGet(out var resolve, out var malformed)) return Malformed(malformed);
        var registry = new ControlState(context.State).Resources;
        var resolved = new JsonObject();
        foreach (var id in resolve.ResourceIds.Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxOfferResourceLookups))
            if (registry.Find(context.Repository, resolve.MachineId, id) is { } resource) resolved[id] = resource.ResourceId;
        return Ok(new { resolved });
    }

    private static RelayReply Control(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        var command = Required(context.Args, "command");
        var allSessions = false;
        if (context.Args.ContainsKey("allSessions"))
        {
            if (context.Args["allSessions"] is not JsonValue flag || !flag.TryGetValue(out allSessions) || command != "stop"
                || context.Args.ContainsKey("sessionId") || context.Args.ContainsKey("actionId") || context.Args.ContainsKey("targetSessionId"))
                return Error(InvalidArguments);
        }
        if (allSessions)
        {
            var global = new ControlState(context.State);
            global.Paused = true;
            var sessions = global.Entries().Where(entry => entry.Entry is not null).Select(entry => entry.Id).ToArray();
            foreach (var id in sessions) global.RequestCancellation(id, "", context.Caller.PrincipalId, context.Now);
            return Ok(new { paused = true, cancellationRequested = true, cancellationSessions = sessions.Length });
        }
        if (command is "appoint_manager" or "clear_manager") return ManagerSelection.Apply(context);
        if (command == "register_resource") return RegisterResource(context);
        if (command == "close_sessions") return CloseSessionsByFilter(context);
        var state = new ControlState(context.State);
        if (command is "pause" or "stop") state.Paused = true;
        else if (command == "resume") state.Paused = false;
        else if (command != "cancel") return Error(UnknownCommand);
        var cancellation = command is "stop" or "cancel";
        if (cancellation) state.RequestCancellation(context.SessionId, Text(context.Args, "actionId"), context.Caller.PrincipalId, context.Now);
        return Ok(new { paused = state.Paused, cancellationRequested = cancellation });
    }

    private static RelayReply RegisterResource(AggregateContext context)
    {
        var id = Required(context.Args, "resourceId");
        var machine = Required(context.Args, "machineId");
        var registry = new ControlState(context.State).Resources;
        var names = Strings(context.Args["aliases"]).Append(id).ToArray();
        var canonical = id;
        foreach (var name in names)
            if (registry.Alias(machine, name) is { } prior) canonical = prior;
        if (registry.Resource(canonical) is { } existing && existing.RepositoryId != context.Repository) return Error(RepositoryDenied);
        foreach (var name in names)
        {
            if (registry.Alias(machine, name) is { } other && other != canonical) return Error(AliasConflict);
            registry.SetAlias(machine, name, canonical);
        }
        registry.Register(canonical, context.Repository, machine, names);
        return Ok(new { resourceId = canonical });
    }
}

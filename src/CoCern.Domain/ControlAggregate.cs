using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>The hub singleton: session directory, worker presence, registered resources and their aliases, pause and cancellation requests.</summary>
internal static class ControlAggregate
{
    private const int MaxMachineSummaries = 50;
    internal static readonly TimeSpan InitialFreshness = TimeSpan.FromMinutes(2);

    public static RelayReply Handle(AggregateContext context)
    {
        Required(context.Args, "repositoryId");
        return context.Operation switch
        {
            RelayTools.SessionOpen => OpenSession(context),
            Operations.Register => RegisterSession(context),
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
        Required(args, "requestId");
        var sessions = ObjectAt(context.State, "sessions");
        if (sessions.Count >= RelayLimits.MaxRecords) DropStaleSessions(context, sessions);
        // Every session was seen within StaleSessionHours, or still answers for an unresolved action: refused for now. The first clears as
        // sessions go stale; the second only once those actions are reported or recovered, since expiring their sessions would strand them.
        if (sessions.Count >= RelayLimits.MaxRecords)
        {
            var cutoff = IdleCutoff(context.Now, TimeSpan.FromHours(RelayLimits.StaleSessionHours));
            var held = sessions.Count(entry => entry.Value is JsonObject record && OwnsWork(record, context.Now) && LastSeen(record) <= cutoff);
            return Error(CapacityExceeded, new
            {
                staleAfterHours = RelayLimits.StaleSessionHours, heldByUnresolvedActions = held,
                hint = held > 0 ? "stale sessions own unresolved actions; report them, or recover them with action_report recover: true" : null
            });
        }
        var id = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new JsonObject
        {
            ["sessionId"] = id, ["principalId"] = context.Caller.PrincipalId, ["repositoryId"] = context.Repository, ["sessionToken"] = token, ["bindingVersion"] = 0,
            ["machineId"] = Text(args, "machineId"), ["label"] = Text(args, "label"), ["binding"] = args["binding"]?.DeepClone(),
            ["observedAt"] = context.Now.ToString("O"), ["freshUntil"] = context.Now.Add(InitialFreshness).ToString("O"), ["availability"] = "unknown"
        };
        session["hubIncarnation"] = Text(args, "hubIncarnation");
        // The role given at session_open routes manager-bound attention items; it grants nothing.
        if (Text(args, "role") is { Length: > 0 and <= RelayLimits.MaxActivityRoleLength } role) session["role"] = role;
        sessions[id] = session;
        context.Emit(AggregateKinds.Session, id, Operations.Open, session);
        return Ok(new { sessionId = id, sessionToken = token, bindingVersion = 0, binding = args["binding"] });
    }

    /// <summary>
    /// Makes room in a full directory: drops sessions not seen (fresh, active or observed) for <see cref="RelayLimits.StaleSessionHours"/>,
    /// least recently seen first, down to <see cref="RelayLimits.RetainedEntriesAfterPrune"/>, so it runs at most once per 512 opens.
    /// A session seen within that time is never dropped, nor one that answers for an unresolved action (its expiry would leave nobody able
    /// to report it). Each dropped session is sent <see cref="Operations.SessionExpire"/>, under its own repository, so its chat gets a
    /// clean <c>SessionUnknown</c> and opens again. This is the directory's capacity valve, so unread mail does not keep a session here.
    /// </summary>
    private static void DropStaleSessions(AggregateContext context, JsonObject sessions)
    {
        var cutoff = IdleCutoff(context.Now, TimeSpan.FromHours(RelayLimits.StaleSessionHours));
        CloseSessions(context, sessions, (_, session) => session is null || LastSeen(session) <= cutoff, sessions.Count - RelayLimits.RetainedEntriesAfterPrune, idle: false);
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
    /// Each close is fenced (see <see cref="Fence"/>), so only the session's answer to this close can bring it back.
    /// </summary>
    private static Closing CloseSessions(AggregateContext context, JsonObject sessions, Func<string, JsonObject?, bool> matches, int limit, bool idle, bool force = false)
    {
        var now = context.Now;
        var matched = sessions.Select(entry => (entry.Key, Session: entry.Value as JsonObject)).Where(entry => matches(entry.Key, entry.Session))
            .Select(entry => (entry.Key, entry.Session, Seen: entry.Session is { } record ? LastSeen(record) : DateTimeOffset.MinValue,
                Owns: entry.Session is { } owner && OwnsWork(owner, now), Hold: entry.Session is { } holder ? MailHold(holder, now) : Hold.None)).ToList();
        var closable = matched.Where(entry => force || !entry.Owns && !(idle && entry.Hold == Hold.Holding))
            // A forced close takes sessions without mail waiting first.
            .OrderBy(entry => entry.Hold == Hold.Holding).ThenBy(entry => entry.Seen).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToList();
        var closing = closable.Take(Math.Max(0, limit)).ToList();
        foreach (var (id, session, seen, _, hold) in closing)
        {
            sessions.Remove(id);
            var repository = session is null ? "" : Text(session, "repositoryId");
            var expire = new JsonObject { ["repositoryId"] = repository, ["sessionId"] = id, ["closeSeq"] = Fence(context.State, id) };
            if (force) { expire["force"] = true; expire["requestedBy"] = context.Caller.PrincipalId; }
            // What the directory had heard when it decided; a session that has reported activity since was working, and refuses.
            else if (seen > DateTimeOffset.MinValue) expire["seenAt"] = seen.ToString("O");
            // A session whose hold lapsed already had its grace, so this idle close takes it as it is.
            if (idle && hold == Hold.None) expire["idle"] = true;
            // Its own caller: the session belongs to its repository, which the caller that closed it may not reach (or may reach them all).
            context.Emit(AggregateKinds.Session, id, Operations.SessionExpire, expire, new RelayCaller("relay-directory", false, [repository]));
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
        var hours = Number(context.Args, "idleHours");
        if (hours <= 0) return Error(InvalidArguments, new { field = "idleHours" });
        var cutoff = IdleCutoff(context.Now, TimeSpan.FromHours(hours));
        var result = CloseSessions(context, ObjectAt(context.State, "sessions"), (_, session) => session is null || LastSeen(session) <= cutoff, RelayLimits.MaxSessionsClosedPerCall, idle: true);
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
        var sessions = ObjectAt(context.State, "sessions");
        bool Matches(string id, JsonObject? session) => session is not null && id != own && Text(session, "repositoryId") == context.Repository
            && (target is null || id == target) && (prefix is null || Text(session, "label").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var active = labelOnly ? sessions.Count(entry => Matches(entry.Key, entry.Value as JsonObject) && LastSeen((JsonObject)entry.Value!) > cutoff) : 0;
        var result = CloseSessions(context, sessions, (id, session) => Matches(id, session) && LastSeen(session!) <= cutoff, RelayLimits.MaxSessionsClosedPerCall,
            idle: minutes is not null, force: force);
        return Ok(new { closed = result.Closed.Count, kept = result.Kept, held = result.Held, active, more = result.More, ids = result.Closed.Take(RelayLimits.MaxClosedSessionIds).ToArray() });
    }

    /// <summary>When the session refused an idle close because it had unread mail; see <see cref="MailHold"/>.</summary>
    private const string MailHeldKey = "mailHeldAt";

    private enum Hold { None, Holding, Lapsed }

    /// <summary>
    /// Whether the record is held open for unread mail. A session refuses an idle expiry while its inbox has unread messages (a chat that
    /// cannot be woken may idle for a day waiting on mail, which its expiry would delete), and the directory then remembers when. The hold
    /// lasts while the session is not seen again, for <see cref="RelayLimits.SessionMailHoldDays"/>; after that it has lapsed and the next
    /// idle close takes the session as it is. Once the session is seen again the hold no longer applies: its next idle close asks again.
    /// </summary>
    private static Hold MailHold(JsonObject record, DateTimeOffset now)
    {
        if (OptionalDate(record, MailHeldKey) is not { } held || LastSeen(record) > held) return Hold.None;
        return held > now.AddDays(-RelayLimits.SessionMailHoldDays) ? Hold.Holding : Hold.Lapsed;
    }

    /// <summary>The record fields a refused expiry brings back (<see cref="Operations.SessionRestore"/>): what the directory keeps of a session, as the session keeps it.</summary>
    private static IEnumerable<string> RestoredFields => RegistrationFields.Concat(["principalId", "sessionToken", "role"]);

    /// <summary>
    /// <see cref="Operations.SessionRestore"/>: a session this directory closed refused its expiry, because it answers for an unresolved
    /// action (a report of it may not have arrived here before the close) or, for an idle close, because it has unread mail. Its record comes
    /// back from the session's copy, with ownership (<c>owns</c>, ordered by the session's report <c>seq</c>) or the mail hold (<c>mail</c>),
    /// so later closes keep it. It returns even to a full directory: it never left the hub. A session still listed (a redelivery) only gains
    /// the ownership or hold; one in another repository, or a malformed copy, is ignored, since a refusal would be retried forever.
    /// </summary>
    private static RelayReply RestoreSession(AggregateContext context)
    {
        var args = context.Args;
        var id = Text(args, "sessionId");
        var sessions = ObjectAt(context.State, "sessions");
        // Only the answer to this directory's latest close of the session counts, once: a delayed or duplicated restore from an earlier
        // close would otherwise bring back a session that has since closed (a forced close, or a later one it accepted).
        if (id.Length == 0 || !Fenced(context.State, id, args)) return Ok(new { restored = false });
        if (sessions[id] is not JsonObject record)
        {
            if (args["record"] is not JsonObject copy || Text(copy, "principalId").Length == 0) return Ok(new { restored = false });
            record = new JsonObject();
            foreach (var field in RestoredFields) if (copy[field] is { } value) record[field] = value.DeepClone();
            record["sessionId"] = id; record["repositoryId"] = context.Repository;
            sessions[id] = record;
        }
        else if (Text(record, "repositoryId") != context.Repository) return Ok(new { restored = false });
        // A redelivered restore never undoes a newer report that the work ended.
        if (IsTrue(args, "owns") && Number(args, "seq") >= Number(record, WorkSeqKey))
        {
            record[OwnsWorkKey] = context.Now.ToUnixTimeSeconds();
            record[WorkSeqKey] = Number(args, "seq");
        }
        if (IsTrue(args, "mail")) record[MailHeldKey] = context.Now.ToString("O");
        Unfence(context.State, id);
        return Ok(new { restored = true });
    }

    private const string ClosingKey = "closing";
    private const string CloseSeqKey = "closeSeq";

    /// <summary>
    /// Fences one close of <paramref name="id"/>: returns its new <c>closeSeq</c>, which the session's one answer carries back, a restore
    /// (<see cref="RestoreSession"/>) or an acknowledgement (<see cref="SettleClose"/>). Either settles the fence. A fence lives only while
    /// its close is in flight, so the directory keeps none for sessions long closed; past <see cref="RelayLimits.MaxRecords"/> in flight the
    /// oldest is dropped, and its late answer is then ignored.
    /// </summary>
    private static long Fence(JsonObject state, string id)
    {
        var seq = Number(state, CloseSeqKey) + 1;
        state[CloseSeqKey] = seq;
        var closing = ObjectAt(state, ClosingKey);
        closing.Remove(id);
        closing[id] = seq;
        while (closing.Count > RelayLimits.MaxRecords) closing.Remove(closing.First().Key);
        return seq;
    }

    /// <summary>Whether <paramref name="args"/> answers the close of <paramref name="id"/> still in flight.</summary>
    private static bool Fenced(JsonObject state, string id, JsonObject args) =>
        state[ClosingKey] is JsonObject closing && closing[id] is JsonValue fence && fence.TryGetValue<long>(out var seq) && Number(args, "closeSeq") == seq;

    private static void Unfence(JsonObject state, string id)
    {
        if (state[ClosingKey] is not JsonObject closing) return;
        closing.Remove(id);
        if (closing.Count == 0) state.Remove(ClosingKey);
    }

    /// <summary><see cref="Operations.SessionExpired"/>: a session closed as this directory asked, so that close is settled. Any other copy is ignored.</summary>
    private static RelayReply SettleClose(AggregateContext context)
    {
        var id = Text(context.Args, "sessionId");
        if (id.Length == 0 || !Fenced(context.State, id, context.Args)) return Ok(new { settled = false });
        Unfence(context.State, id);
        return Ok(new { settled = true });
    }

    private const string OwnsWorkKey = "ownsWork";
    private const string WorkSeqKey = "ownsWorkSeq";

    /// <summary>Whether the session answers for an unresolved action: it reported so within <see cref="RelayLimits.SessionWorkRetentionDays"/>.</summary>
    private static bool OwnsWork(JsonObject session, DateTimeOffset now) => WorkLedger.Seconds(session[OwnsWorkKey]) > WorkLedger.Cutoff(now);

    /// <summary>
    /// <see cref="Operations.SessionOwns"/>: a session's report that it now answers for unresolved work, or no longer does, sent only when
    /// that changes and as a daily refresh, so each action does not write this directory. A report numbered no later than the last one
    /// applied (<c>seq</c>) is stale and ignored, so reordered or repeated reports never flip it back. A session missing from the
    /// directory, or in another repository, is ignored.
    /// </summary>
    private static RelayReply TrackOwnership(AggregateContext context)
    {
        if (!SessionOwnership.Decode(context.Args).TryGet(out var report, out var malformed)) return Malformed(malformed);
        if (ObjectAt(context.State, "sessions")[report.SessionId] is not JsonObject session || Text(session, "repositoryId") != context.Repository) return Ok(new { tracked = false });
        if (report.Seq <= Number(session, WorkSeqKey)) return Ok(new { tracked = false });
        session[WorkSeqKey] = report.Seq;
        if (report.Owns) session[OwnsWorkKey] = WorkLedger.ReportedAt(report.At, context.Now);
        else session.Remove(OwnsWorkKey);
        return Ok(new { tracked = true });
    }

    private static DateTimeOffset LastSeen(JsonObject session) =>
        new[] { Date(session, "freshUntil"), Date(session, "activeAt"), Date(session, "observedAt") }.Max();

    private static RelayReply RegisterSession(AggregateContext context)
    {
        var id = Required(context.Args, "sessionId");
        // A session dropped from the directory may still send a late registration; accept it unchanged so its outbox does not retry forever.
        if (ObjectAt(context.State, "sessions")[id] is not JsonObject session) return Ok(new { registered = false });
        if (Text(session, "repositoryId") != context.Repository) return Error(SessionDenied);
        // Registrations can arrive reordered (a retried delivery): an older binding never replaces a newer one, and times never move back.
        var stale = context.Args.ContainsKey("bindingVersion") && Number(context.Args, "bindingVersion") < Number(session, "bindingVersion");
        foreach (var property in RegistrationFields)
        {
            if (!context.Args.ContainsKey(property)) continue;
            if (property is "observedAt" or "freshUntil" or "activeAt")
            {
                if (Date(context.Args, property) > Date(session, property)) session[property] = context.Args[property]?.DeepClone();
            }
            else if (!stale) session[property] = context.Args[property]?.DeepClone();
        }
        return Ok(new { registered = !stale });
    }

    /// <summary>What a session reports to the directory about itself; nothing else of its state ever leaves it.</summary>
    internal static readonly string[] RegistrationFields = ["binding", "bindingVersion", "observedAt", "freshUntil", "availability", "machineId", "label", "hubIncarnation", "activeAt"];

    /// <summary>Reported presence only. A record from another hub incarnation or past its freshness is shown as stale.</summary>
    private static RelayReply ListWorkers(AggregateContext context)
    {
        var workers = ObjectAt(context.State, "sessions").Select(x => x.Value!.AsObject()).Where(x => Text(x, "repositoryId") == context.Repository).Select(x =>
        {
            var copy = x.DeepClone().AsObject(); copy.Remove("sessionToken"); copy.Remove("principalId"); copy.Remove(OwnsWorkKey); copy.Remove(WorkSeqKey); copy.Remove(MailHeldKey);
            var stale = Date(copy, "freshUntil") <= context.Now || Text(copy, "hubIncarnation") != Text(context.Args, "hubIncarnation");
            copy["stale"] = stale; copy.Remove("hubIncarnation");
            if (stale) copy["availability"] = "unknown";
            return (JsonNode)copy;
        }).ToArray();
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
        var cancellations = ObjectAt(context.State, "cancellations");
        return Ok(new
        {
            paused = Flag(context.State, "paused"),
            cancellations = cancellations.Count,
            requests = sessionId.Length > 0 ? cancellations.Where(x => x.Key.StartsWith(sessionId + ":", StringComparison.Ordinal)).Take(RelayLimits.MaxPageSize).Select(x => new { target = x.Key, request = x.Value }).ToArray() : []
        });
    }

    /// <summary>
    /// Passes one attention raise or close to every session in the repository whose role (from <c>session_open</c>, ignoring case) matches,
    /// except the session that caused it and sessions neither fresh nor active (a boundary reply, reported hourly as <c>activeAt</c>) for
    /// more than <see cref="RelayLimits.MaxAttentionRouteIdleHours"/> (closed chats); at most <see cref="RelayLimits.MaxAttentionRouteTargets"/>, most recently seen first.
    /// </summary>
    private static RelayReply RouteAttention(AggregateContext context)
    {
        var args = context.Args;
        var operation = Text(args, "operation");
        if (operation is not (Operations.AttentionRaise or Operations.AttentionClose) || args["item"] is not JsonObject item) return Error(InvalidArguments, new { field = "item" });
        var role = Required(args, "role");
        var except = Text(args, "exceptSessionId");
        // A raise goes to recently active managers only; a close reaches every manager, or an idle one would keep a stale item until it expires.
        var closing = operation == Operations.AttentionClose;
        var targets = ObjectAt(context.State, "sessions").Select(x => x.Value).OfType<JsonObject>()
            .Where(session => Text(session, "repositoryId") == context.Repository && string.Equals(Text(session, "role"), role, StringComparison.OrdinalIgnoreCase) && Text(session, "sessionId") != except
                && (closing || Max(Date(session, "freshUntil"), Date(session, "activeAt")) > context.Now.AddHours(-RelayLimits.MaxAttentionRouteIdleHours)))
            .OrderByDescending(session => Date(session, "observedAt")).Take(closing ? RelayLimits.MaxRecords : RelayLimits.MaxAttentionRouteTargets).Select(session => Text(session, "sessionId")).ToArray();
        foreach (var target in targets)
        {
            var copy = item.DeepClone().AsObject(); copy["repositoryId"] = context.Repository;
            context.Emit(AggregateKinds.Session, target, operation, copy);
        }
        return Ok(new { routed = targets.Length });
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    /// <summary>The given session IDs that the caller's principal opened in this repository. Read-only; tokens and other principals are never returned.</summary>
    private static RelayReply OwnedSessions(AggregateContext context)
    {
        var sessions = ObjectAt(context.State, "sessions");
        var owned = Strings(context.Args["sessionIds"]).Distinct(StringComparer.Ordinal)
            .Where(id => sessions[id] is JsonObject session && Text(session, "principalId") == context.Caller.PrincipalId && Text(session, "repositoryId") == context.Repository).ToArray();
        return Ok(new { owned });
    }

    /// <summary>The given session IDs still open in this repository, whoever opened them. Read-only; nothing else about them is returned.</summary>
    private static RelayReply OpenSessions(AggregateContext context)
    {
        var sessions = ObjectAt(context.State, "sessions");
        var open = Strings(context.Args["sessionIds"]).Distinct(StringComparer.Ordinal)
            .Where(id => sessions[id] is JsonObject session && Text(session, "repositoryId") == context.Repository).ToArray();
        return Ok(new { open });
    }

    /// <summary>Maps a resource ID or a machine-scoped alias to its canonical registered resource.</summary>
    private static RelayReply ResolveResource(AggregateContext context) =>
        Find(context, Required(context.Args, "resourceId")) is { } resolved ? Ok(resolved) : Error(ResourceUnknown);

    /// <summary>Read-only: the canonical ID of each named resource this repository registered, in one call. Unknown names are left out.</summary>
    private static RelayReply ResolveAll(AggregateContext context)
    {
        var resolved = new JsonObject();
        foreach (var id in Strings(context.Args["resourceIds"]).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxOfferResourceLookups))
            if (Find(context, id) is { } resource) resolved[id] = Text(resource, "resourceId");
        return Ok(new { resolved });
    }

    private static JsonObject? Find(AggregateContext context, string id)
    {
        var resources = ObjectAt(context.State, "resources");
        var resolved = resources[id] as JsonObject;
        if (resolved is null && ObjectAt(context.State, "aliases")[AliasKey(Text(context.Args, "machineId"), id)] is JsonValue alias) resolved = resources[alias.GetValue<string>()] as JsonObject;
        return resolved is null || Text(resolved, "repositoryId") != context.Repository ? null : resolved;
    }

    private static RelayReply Control(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        var command = Required(context.Args, "command");
        if (command == "register_resource") return RegisterResource(context);
        if (command == "close_sessions") return CloseSessionsByFilter(context);
        var state = context.State;
        if (command is "pause" or "stop") state["paused"] = true;
        else if (command == "resume") state["paused"] = false;
        else if (command != "cancel") return Error(UnknownCommand);
        var cancellation = command is "stop" or "cancel";
        if (cancellation)
            ObjectAt(state, "cancellations")[context.SessionId + ":" + Text(context.Args, "actionId")] = JsonSerializer.SerializeToNode(new { requestedBy = context.Caller.PrincipalId, at = context.Now, status = "requested_unknown" });
        return Ok(new { paused = Flag(state, "paused"), cancellationRequested = cancellation });
    }

    private static RelayReply RegisterResource(AggregateContext context)
    {
        var id = Required(context.Args, "resourceId");
        var machine = Required(context.Args, "machineId");
        var resources = ObjectAt(context.State, "resources");
        var aliases = ObjectAt(context.State, "aliases");
        var names = Strings(context.Args["aliases"]).Append(id).ToArray();
        var canonical = id;
        foreach (var name in names)
            if (aliases[AliasKey(machine, name)] is JsonValue prior) canonical = prior.GetValue<string>();
        if (resources[canonical] is JsonObject existing && Text(existing, "repositoryId") != context.Repository) return Error(RepositoryDenied);
        foreach (var name in names)
        {
            var aliasKey = AliasKey(machine, name);
            if (aliases[aliasKey] is JsonValue other && other.GetValue<string>() != canonical) return Error(AliasConflict);
            aliases[aliasKey] = canonical;
        }
        resources[canonical] = new JsonObject { ["resourceId"] = canonical, ["repositoryId"] = context.Repository, ["machineId"] = machine, ["aliases"] = JsonSerializer.SerializeToNode(names) };
        return Ok(new { resourceId = canonical });
    }

    private static string AliasKey(string machine, string name) => machine.ToUpperInvariant() + ":" + name.ToUpperInvariant();
}

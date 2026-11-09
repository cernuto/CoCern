using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// Records each repository's own choice of managers and answers who holds them there. A repository holds up to
/// <see cref="RelayLimits.MaxManagersPerRepository"/> managers; adding one never removes another, and nothing done in one repository changes another's
/// seats, generation or revocations. The caller's existing authority is the only authority used: being recorded widens no token, and losing the record
/// only refuses what a former manager presents as itself in that repository. Attribution and marking read this; nothing else does.
/// </summary>
internal static class ManagerSelection
{
    /// <summary>What a chat's own add records in place of Paul's words when it gave none.</summary>
    internal const string SelfAppointedWords = "self-appointed";
    /// <summary>What a chat's own removal records in place of Paul's words when it gave none.</summary>
    internal const string SelfRemovedWords = "self-removed";

    public static RelayReply Apply(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error("AdministratorRequired");
        var args = context.Args;
        var command = Text(args, "command");
        if (command is not ("appoint_manager" or "clear_manager")) return Error("UnknownCommand");
        var repository = context.Repository;
        if (!Identity(repository) || !Identity(context.Caller.PrincipalId) || !Identity(Text(args, "requestId")) ||
            !WholeNumber(args["expectedManagerGeneration"], out var expected) || expected == long.MaxValue) return Error("InvalidArguments");
        string? words = null;
        if (args.ContainsKey("humanWords"))
        {
            if (args["humanWords"] is not JsonValue wordsValue || !wordsValue.TryGetValue<string>(out var given) || string.IsNullOrWhiteSpace(given) ||
                given.Length > RelayLimits.MaxManagerHumanWordsLength || given.Any(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029')) return Error("InvalidArguments");
            words = given;
        }
        string? targetId = null;
        if (args.ContainsKey("targetSessionId"))
        {
            if (args["targetSessionId"] is not JsonValue targetValue || !targetValue.TryGetValue<string>(out targetId) || !Identity(targetId)) return Error("InvalidArguments");
        }
        else if (command == "appoint_manager") return Error("InvalidArguments");
        var state = new ControlState(context.State);
        var seat = state.Seat(repository);
        if (seat.Generation == long.MaxValue) return Error("InvalidArguments");
        if (expected != seat.Generation) return Error("VersionConflict", new { currentGeneration = seat.Generation });
        DirectoryEntry? target = null;
        ManagerAssignment? held = null;
        if (command == "appoint_manager")
        {
            target = state.Entry(targetId!);
            if (target is null || target.SessionId != targetId || state.AwaitingAnswer(targetId, context.Now) || target.LastSeen <= context.Now.AddHours(-RelayLimits.StaleSessionHours)) return Error("SessionUnknown");
            if (target.RepositoryId != repository) return Error("RepositoryDenied");
            if (!Identity(target.PrincipalId) || !Identity(target.RepositoryId)) return Error("InvalidArguments");
        }
        else if (targetId is not null)
            held = seat.Managers.FirstOrDefault(manager => manager.SessionId == targetId || Seated(state, manager, context.Now)?.SessionId == targetId);
        // Set only by the hub, from a session it verified in this repository. Only that proof says "this chat": a target that merely shares a label or
        // folder never does, and a chat that leaves its proof off is recorded as its token alone, so it acts on itself only with Paul's words.
        var acting = Text(args, ServerArguments.ActingSession);
        var actor = acting.Length > 0 && state.Entry(acting) is { } entry && entry.SessionId == acting && entry.RepositoryId == repository ? entry : null;
        var self = actor is not null && targetId is not null && (actor.SessionId == targetId || actor.NativeId.Length > 0 &&
            (target is not null && target.NativeId == actor.NativeId || held is not null && held.NativeId == actor.NativeId));
        // A chat may add or remove itself on its own word. Acting on any other chat, or on all of them, needs Paul's exact words.
        if (words is null && !self) return Error("InvalidArguments");
        var request = Text(args, "requestId");
        var recordedBy = acting.Length > 0 ? acting : null;
        if (target is not null)
        {
            var managers = seat.Managers;
            // Already a manager here: nothing changes, and the generation stays.
            if (managers.Any(manager => manager.SessionId == target.SessionId)) return Ok(new { manager = Status(state, context.Caller, repository, context.Now, null) });
            // The same chat under a new session replaces its own earlier seat, and is not a former manager for it.
            var earlier = target.NativeId.Length > 0 ? managers.FirstOrDefault(manager => manager.NativeId == target.NativeId) : null;
            if (earlier is null && managers.Count >= RelayLimits.MaxManagersPerRepository) return Error(RelayErrorCodes.CapacityExceeded, new { maxManagers = RelayLimits.MaxManagersPerRepository });
            var next = seat.Generation + 1;
            var change = state.SeatForChange(repository);
            if (earlier is not null) change.Remove(earlier.SessionId);
            change.Restore(target);
            var recorded = words ?? SelfAppointedWords;
            change.Add(ManagerAssignment.Assigned(target, next, context.Caller.PrincipalId, context.Now, request, recorded));
            change.Generation = next;
            Record(state, new ManagerSelectionHistory(next, command, earlier?.SessionId, target.SessionId, repository, context.Caller.PrincipalId, request, context.Now, recorded, recordedBy));
        }
        else
        {
            ManagerAssignment[] removed = targetId is null ? [.. seat.Managers] : held is null ? [] : [held];
            var change = removed.Length > 0 ? state.SeatForChange(repository) : null;
            var next = seat.Generation;
            foreach (var manager in removed)
            {
                next++;
                change!.Remove(manager.SessionId);
                // Removed by someone else, a chat may no longer present itself as this repository's manager. A chat that steps down itself is simply
                // no longer one: it holds what any chat with its token holds, and adding itself back is its own act again.
                if (!self) change.Revoke(manager, next);
                change.Generation = next;
                Record(state, new ManagerSelectionHistory(next, command, manager.SessionId, null, repository, context.Caller.PrincipalId, request, context.Now, words ?? SelfRemovedWords, recordedBy));
            }
        }
        return Ok(new { manager = Status(state, context.Caller, repository, context.Now, null) });
    }

    // Each repository keeps its own recent decisions, so another repository's churn never pushes them out; the document keeps a ceiling across all.
    private static void Record(ControlState state, ManagerSelectionHistory decision)
    {
        var history = state.ManagerHistory;
        history.Add(JsonSerializer.SerializeToNode(decision));
        bool Own(JsonNode? item) => item is JsonObject entry && Text(entry, "repositoryId") == decision.RepositoryId;
        while (history.Count(Own) > RelayLimits.ManagerSelectionHistoryEntries) history.Remove(history.First(Own));
        while (history.Count > RelayLimits.ManagerSelectionHistoryTotalEntries) history.RemoveAt(0);
    }

    // This projection reads only the repository's seats and their directory entries. It never opens the history collection.
    internal static ManagerSelectionStatus VisibleSummary(ControlState state, RelayCaller caller, string repository, DateTimeOffset now)
    {
        var seat = state.Seat(repository);
        var visible = caller.MayAccess(repository);
        ManagerSelectionSummary[] managers = visible
            ? seat.Managers.Select(manager => (Manager: manager, Seated: Seated(state, manager, now)))
                .Select(row => new ManagerSelectionSummary(row.Seated?.SessionId ?? row.Manager.SessionId, row.Manager.RepositoryId, row.Manager.Generation, row.Seated is not null))
                .OrderByDescending(summary => summary.Generation).ToArray()
            : [];
        var current = managers.FirstOrDefault(manager => manager.Available) ?? managers.FirstOrDefault();
        return new(visible || caller.IsAdministrator ? seat.Generation : 0, current, [], null, false, false, managers);
    }

    /// <summary>
    /// The live directory entry holding the seat, or null while the manager is closed, closing or stale. A native chat is followed to its current
    /// session under the same principal and repository; a matching label, folder or age never counts, so nothing else ever inherits the seat.
    /// </summary>
    private static DirectoryEntry? Seated(ControlState state, ManagerAssignment assignment, DateTimeOffset now)
    {
        var entry = state.Entry(assignment.SessionId);
        if (entry is null && assignment.NativeId.Length > 0 && state.CurrentNativeSession(assignment.NativeId, assignment.RepositoryId) is { } current) entry = state.Entry(current.SessionId);
        return entry is not null && entry.PrincipalId.Length > 0 && entry.PrincipalId == assignment.PrincipalId && entry.RepositoryId == assignment.RepositoryId &&
            !state.AwaitingAnswer(entry.SessionId, now) && entry.LastSeen > now.AddHours(-RelayLimits.StaleSessionHours) ? entry : null;
    }

    /// <summary>
    /// <see cref="Operations.ManagerStanding"/>, for the query's repository alone: that repository's generation, whether the session holds one of its
    /// seats, and whether it was removed from one there by someone else.
    /// </summary>
    public static RelayReply Standing(AggregateContext context)
    {
        if (!ManagerStandingQuery.Decode(context.Args).TryGet(out var query, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        var seat = state.Seat(query.RepositoryId);
        var manager = query.SessionId.Length > 0 && seat.Managers.Any(assignment => Seated(state, assignment, context.Now)?.SessionId == query.SessionId);
        var revoked = !manager && (seat.Revoked(query.SessionId) || state.Entry(query.SessionId) is { } entry && seat.Revoked(entry.NativeId));
        return Ok(new { generation = seat.Generation, manager, revoked });
    }

    public static ManagerSelectionStatus Status(ControlState state, RelayCaller caller, string repository, DateTimeOffset now, long? historyBefore)
    {
        var summary = VisibleSummary(state, caller, repository, now);
        if (!caller.IsAdministrator) return summary;
        var candidates = state.ManagerHistory.Select(x => x!.Deserialize<ManagerSelectionHistory>()!)
            .Where(x => x.RepositoryId == repository && (historyBefore is null || x.Generation < historyBefore)).OrderByDescending(x => x.Generation).ToArray();
        var page = candidates.Take(RelayLimits.ManagerSelectionHistoryPageEntries).ToArray();
        var more = candidates.Length > page.Length;
        var status = summary with { History = page, NextHistoryBefore = more ? page[^1].Generation : null, HistoryTruncated = more };
        return Fit(status, candidate => FitsWire(Ok(new { manager = candidate }), RelayLimits.AggregateReplyBytes), historyBefore);
    }

    // Fit the complete serialized RelayReply, including its escaped DataJson and any enclosing control fields.
    internal static ManagerSelectionStatus Fit(ManagerSelectionStatus status, Func<ManagerSelectionStatus, bool> fits, long? historyBefore)
    {
        while (status.History.Length > 0 && !fits(status))
        {
            var page = status.History[..^1];
            status = status with
            {
                History = page,
                NextHistoryBefore = page.Length > 0 ? page[^1].Generation : historyBefore,
                HistoryTruncated = true,
                HistoryReadRequired = page.Length == 0
            };
        }
        return status;
    }

    internal static bool WholeNumber(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue number && number.GetValueKind() == JsonValueKind.Number && long.TryParse(number.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= RelayLimits.MaxIdentifierLength && !value.Any(char.IsControl);
}

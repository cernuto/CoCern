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
/// Records a hub-wide choice and answers who holds it. The caller's existing authority is the only authority used: being recorded widens no token,
/// and losing the record only refuses what a former manager presents as itself. Attribution and marking read this; nothing else does.
/// </summary>
internal static class ManagerSelection
{
    public static RelayReply Apply(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error("AdministratorRequired");
        var args = context.Args;
        var command = Text(args, "command");
        if (command is not ("appoint_manager" or "clear_manager")) return Error("UnknownCommand");
        if (!Identity(context.Repository) || !Identity(context.Caller.PrincipalId) || !Identity(Text(args, "requestId")) ||
            !WholeNumber(args["expectedManagerGeneration"], out var expected) || expected == long.MaxValue ||
            args["humanWords"] is not JsonValue wordsValue || !wordsValue.TryGetValue<string>(out var words) ||
            string.IsNullOrWhiteSpace(words) || words.Length > RelayLimits.MaxManagerHumanWordsLength || words.Any(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029')) return Error("InvalidArguments");
        string? targetId = null;
        if (command == "clear_manager")
        {
            if (args.ContainsKey("targetSessionId")) return Error("InvalidArguments");
        }
        else
        {
            if (args["targetSessionId"] is not JsonValue targetValue || !targetValue.TryGetValue<string>(out targetId) || !Identity(targetId)) return Error("InvalidArguments");
        }
        var state = new ControlState(context.State);
        if (state.ManagerGeneration == long.MaxValue) return Error("InvalidArguments");
        if (expected != state.ManagerGeneration) return Error("VersionConflict", new { currentGeneration = state.ManagerGeneration });
        DirectoryEntry? target = null;
        if (targetId is not null)
        {
            target = state.Entry(targetId);
            if (target is null || target.SessionId != targetId || state.AwaitingAnswer(targetId, context.Now) || target.LastSeen <= context.Now.AddHours(-RelayLimits.StaleSessionHours)) return Error("SessionUnknown");
            if (target.RepositoryId != context.Repository) return Error("RepositoryDenied");
            if (!Identity(target.PrincipalId) || !Identity(target.RepositoryId)) return Error("InvalidArguments");
        }
        // Set only by the hub, from a session it verified. A chat that leaves its proof off is recorded as its token alone, so this refuses what the hub can see.
        var acting = Text(args, ServerArguments.ActingSession);
        if (target is not null && acting.Length > 0 && (acting == target.SessionId || target.NativeId.Length > 0 && state.Entry(acting)?.NativeId == target.NativeId))
            return Error(RelayErrorCodes.SelfAppointment);
        var next = state.ManagerGeneration + 1;
        var prior = state.Manager;
        var previous = prior?.SessionId;
        // The same chat under a new session keeps its seat. Anyone else's seat ends here, and with it what that chat may present as itself.
        if (prior is not null && !(target is not null && (prior.SessionId == target.SessionId || prior.NativeId.Length > 0 && prior.NativeId == target.NativeId))) state.RevokeManager(prior, next);
        if (target is not null) state.RestoreManager(target);
        state.Manager = target is null ? null : ManagerAssignment.Assigned(target, next, context.Caller.PrincipalId, context.Now, Text(args, "requestId"), words);
        state.ManagerGeneration = next;
        state.ManagerHistory.Add(JsonSerializer.SerializeToNode(new ManagerSelectionHistory(next, command, previous, targetId, context.Repository, context.Caller.PrincipalId, Text(args, "requestId"), context.Now, words, acting.Length > 0 ? acting : null)));
        while (state.ManagerHistory.Count > RelayLimits.ManagerSelectionHistoryEntries) state.ManagerHistory.RemoveAt(0);
        return Ok(new { manager = Status(state, context.Caller, context.Now, null) });
    }

    // This projection reads only the assignment and its directory entry. It never opens the history collection.
    internal static ManagerSelectionStatus VisibleSummary(ControlState state, RelayCaller caller, DateTimeOffset now)
    {
        var assignment = state.Manager;
        var visible = assignment is null || caller.MayAccess(assignment.RepositoryId);
        ManagerSelectionSummary? current = null;
        if (assignment is not null && visible)
        {
            var seated = Seated(state, assignment, now);
            current = new(seated?.SessionId ?? assignment.SessionId, assignment.RepositoryId, assignment.Generation, seated is not null);
        }
        return new(visible || caller.IsAdministrator ? state.ManagerGeneration : 0, current, [], null, false);
    }

    /// <summary>
    /// The live directory entry holding the seat, or null while the manager is closed, closing or stale. A native chat is followed to its current
    /// session under the same principal and repository; a matching label, folder or age never counts, so nothing else ever inherits the seat.
    /// </summary>
    private static DirectoryEntry? Seated(ControlState state, ManagerAssignment assignment, DateTimeOffset now)
    {
        var entry = state.Entry(assignment.SessionId);
        if (entry is null && assignment.NativeId.Length > 0 && state.CurrentNativeSession(assignment.NativeId, assignment.RepositoryId) is { } current) entry = state.Entry(current.SessionId);
        return entry is not null && entry.PrincipalId == assignment.PrincipalId && entry.RepositoryId == assignment.RepositoryId &&
            !state.AwaitingAnswer(entry.SessionId, now) && entry.LastSeen > now.AddHours(-RelayLimits.StaleSessionHours) ? entry : null;
    }

    /// <summary><see cref="Operations.ManagerStanding"/>: the current generation, and whether the session holds the seat or lost it.</summary>
    public static RelayReply Standing(AggregateContext context)
    {
        if (!ManagerStandingQuery.Decode(context.Args).TryGet(out var query, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        var manager = state.Manager is { } assignment && Seated(state, assignment, context.Now)?.SessionId == query.SessionId;
        var revoked = !manager && (state.ManagerRevoked(query.SessionId) || state.Entry(query.SessionId) is { } entry && state.ManagerRevoked(entry.NativeId));
        return Ok(new { generation = state.ManagerGeneration, manager, revoked });
    }

    public static ManagerSelectionStatus Status(ControlState state, RelayCaller caller, DateTimeOffset now, long? historyBefore)
    {
        var summary = VisibleSummary(state, caller, now);
        if (!caller.IsAdministrator) return summary;
        var candidates = state.ManagerHistory.Select(x => x!.Deserialize<ManagerSelectionHistory>()!).Where(x => historyBefore is null || x.Generation < historyBefore).OrderByDescending(x => x.Generation).ToArray();
        var page = candidates.Take(RelayLimits.ManagerSelectionHistoryPageEntries).ToArray();
        var more = candidates.Length > page.Length;
        var status = new ManagerSelectionStatus(state.ManagerGeneration, summary.Current, page, more ? page[^1].Generation : null, more);
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

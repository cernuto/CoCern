using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>Records an inert, hub-wide choice. The caller's existing authority is the only authority used.</summary>
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
        var next = state.ManagerGeneration + 1;
        var previous = state.Manager?.SessionId;
        state.Manager = target is null ? null : ManagerAssignment.Assigned(target, next, context.Caller.PrincipalId, context.Now, Text(args, "requestId"), words);
        state.ManagerGeneration = next;
        state.ManagerHistory.Add(JsonSerializer.SerializeToNode(new ManagerSelectionHistory(next, command, previous, targetId, context.Repository, context.Caller.PrincipalId, Text(args, "requestId"), context.Now, words)));
        while (state.ManagerHistory.Count > RelayLimits.ManagerSelectionHistoryEntries) state.ManagerHistory.RemoveAt(0);
        return Ok(new { manager = Status(state, context.Caller, context.Now, null) });
    }

    public static ManagerSelectionStatus Status(ControlState state, RelayCaller caller, DateTimeOffset now, long? historyBefore)
    {
        var assignment = state.Manager;
        var visible = assignment is null || caller.MayAccess(assignment.RepositoryId);
        ManagerSelectionSummary? current = null;
        if (assignment is not null && visible)
        {
            var entry = state.Entry(assignment.SessionId);
            var available = entry is not null && entry.SessionId == assignment.SessionId && entry.PrincipalId == assignment.PrincipalId && entry.RepositoryId == assignment.RepositoryId &&
                !state.AwaitingAnswer(assignment.SessionId, now) && entry.LastSeen > now.AddHours(-RelayLimits.StaleSessionHours);
            current = new(assignment.SessionId, assignment.RepositoryId, assignment.Generation, available);
        }
        if (!caller.IsAdministrator) return new(visible ? state.ManagerGeneration : 0, current, [], null, false);
        var candidates = state.ManagerHistory.Select(x => x!.Deserialize<ManagerSelectionHistory>()!).Where(x => historyBefore is null || x.Generation < historyBefore).OrderByDescending(x => x.Generation).ToArray();
        var page = candidates.Take(RelayLimits.ManagerSelectionHistoryPageEntries).ToArray();
        var more = candidates.Length > page.Length;
        var status = new ManagerSelectionStatus(state.ManagerGeneration, current, page, more ? page[^1].Generation : null, more);
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

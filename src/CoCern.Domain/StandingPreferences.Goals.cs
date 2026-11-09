using System.Globalization;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

internal static partial class StandingPreferences
{
    private static RelayReply ApproveGoal(AggregateContext context)
    {
        var args = context.Args;
        if (args.Any(field => field.Key is not ("repositoryId" or "boardId" or "sessionId" or "sessionToken" or "requestId" or "hubIncarnation" or "decision" or "taskId" or "expectedVersion" or "participants" or "expiresAt" or "humanWords"))) return Error(InvalidArguments);
        if (!Identifier(args["taskId"], out var taskId)) return Invalid("taskId");
        if (args["humanWords"] is not JsonValue quote || !quote.TryGetValue<string>(out var words) || string.IsNullOrWhiteSpace(words) || words.Length > RelayLimits.MaxDecisionHumanWordsLength) return Invalid("humanWords");
        var request = Text(args, "requestId");
        if (request.Length == 0) return Error(RequestIdRequired);
        var actor = context.ActingParticipant;
        if (actor is null || actor.SessionId != context.SessionId || actor.RepositoryId != context.Repository || actor.ParticipantId.Length == 0) return Error(SessionDenied);
        var all = args["participants"] is JsonValue value && value.TryGetValue<string>(out var mode) && mode == "repository";
        var members = context.GoalParticipants;
        if (!all && (args["participants"] is not JsonArray selectors || selectors.Count == 0 || members is null || members.Count != selectors.Count || members.Count > RelayLimits.MaxGrantTargets)) return Invalid("participants");
        if (!all && members is not null && (members.Any(member => member.RepositoryId != context.Repository || member.ParticipantId.Length == 0) || members.Select(member => member.ParticipantId).Distinct(StringComparer.Ordinal).Count() != members.Count || !members.Any(member => member.ParticipantId == actor.ParticipantId))) return Invalid("participants");
        if (all && members is not null) return Invalid("participants");
        var expiryText = Text(args, "expiresAt");
        if (!(expiryText.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || expiryText.EndsWith("+00:00", StringComparison.Ordinal)) || !DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) || expires.Offset != TimeSpan.Zero || expires <= context.Now) return Invalid("expiresAt");
        var board = new BoardState(context.State);
        if (board.Task(taskId) is not { } task) return Error(NotFound, new { taskId });
        if (!ExpectedVersionMatches(args, task.Version)) return VersionConflict(task.Version);
        if (task.Workflow is not { } workflow || workflow.State == "completed" || workflow.Packet is not { } packet || string.IsNullOrWhiteSpace(packet.Goal) || packet.AuthorityRef.StartsWith(GrantPrefix, StringComparison.Ordinal) || task.Json["goalAuthorityRef"] is not null) return Error(InvalidWorkflow);
        var bindings = board.Json["goalBindings"] as JsonObject;
        if ((bindings?.Count ?? 0) >= RelayLimits.MaxRecords) GoalAuthority.PruneEnded(board, context.Now);
        if ((bindings?.Count ?? 0) >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
        var current = board.Standing ?? new StandingState(new JsonObject());
        var next = new StandingState(current.Json.DeepClone().AsObject());
        if (next.Grants.Count >= RelayLimits.MaxStandingGrants) return Error(CapacityExceeded);
        string id;
        do { id = "g-" + Guid.NewGuid().ToString("N"); } while (next.HasGrant(id) || bindings?.ContainsKey(id) == true);
        var updatedWorkflow = workflow.Json.DeepClone().AsObject();
        updatedWorkflow[WorkflowRecord.PacketKey]![WorkflowPacket.AuthorityRefKey] = GrantPrefix + id;
        if (TaskWorkflow.Validate(updatedWorkflow, workflow, TaskActions.HasUnresolved(task)) is { } invalid) return Error(invalid);
        var auditRef = context.SessionId + ":" + request;
        var goal = new JsonObject { ["grantId"] = id, ["rootTaskId"] = taskId, ["purpose"] = packet.Goal, ["ownerParticipantId"] = actor.ParticipantId, ["repositoryWide"] = all, ["members"] = new JsonArray((members ?? []).Select(member => (JsonNode)member.ParticipantId).ToArray()), ["expiresAt"] = expires.ToUniversalTime().ToString("O"), ["auditRef"] = auditRef };
        var publicGoal = GoalAuthority.PublicGoal(goal);
        var effect = new JsonObject { ["goal"] = publicGoal.DeepClone(), ["scope"] = "repository", ["targets"] = new JsonArray(context.Repository), ["expiresAt"] = expires.ToUniversalTime().ToString("O") };
        var evidence = new JsonObject { [StandingDecision.AuditRefKey] = auditRef, [StandingDecision.PrincipalIdKey] = context.Caller.PrincipalId, [StandingDecision.SessionIdKey] = context.SessionId, [StandingDecision.AtKey] = context.Now.ToString("O"), [StandingDecision.HumanWordsKey] = words, [StandingDecision.OutcomeKey] = "approveGoal", [StandingDecision.EffectKey] = effect };
        var grant = StandingGrant.Create(id, StandingGrant.Requested(id, "repository", [context.Repository], expires), context.Caller.PrincipalId, context.Now, null);
        grant.Json["approvedGoal"] = publicGoal.DeepClone();
        grant.Json[StandingDecision.Key] = evidence.DeepClone();
        next.Grants[id] = grant.Json;
        next.Revision++;
        var entry = StandingChange.Create(next.Revision, context.Caller.PrincipalId, context.Now, ["+grant:" + id]);
        entry[StandingDecision.Key] = evidence.DeepClone();
        next.History.Add(entry);
        while (next.History.Count > RelayLimits.StandingHistoryEntries) next.History.RemoveAt(0);
        if (Oversized(next, context.Now) is { } tooLarge) return tooLarge;
        var visible = JsonNode.Parse(ViewOf(next, context.Now).DataJson)!.AsObject();
        if (visible["history"] is not JsonArray history || !history.OfType<JsonObject>().Any(change => Text(change[StandingDecision.Key] as JsonObject ?? new(), StandingDecision.AuditRefKey) == auditRef)) return Error(PayloadTooLarge);
        board.SetStanding(next);
        (board.Json["goalBindings"] as JsonObject ?? (JsonObject)(board.Json["goalBindings"] = new JsonObject()))[id] = goal.DeepClone();
        task.Json["goalAuthorityRef"] = GrantPrefix + id;
        task.SetWorkflow(updatedWorkflow);
        task.Version++;
        task.UpdatedAt = context.Now;
        task.UpdatedBy = context.Caller.PrincipalId;
        return Ok(new { taskId, grantId = id, outcome = "approveGoal", auditRef, revision = next.Revision, version = task.Version }, next.Revision);
    }
}

internal static class GoalAuthority
{
    public static JsonObject PublicGoal(JsonObject goal) => new() { ["rootTaskId"] = Text(goal, "rootTaskId"), ["purpose"] = Text(goal, "purpose"), ["repositoryWide"] = Flag(goal, "repositoryWide"), ["memberCount"] = (goal["members"] as JsonArray)?.Count ?? 0, ["expiresAt"] = Text(goal, "expiresAt") };

    public static string? Problem(BoardState board, WorkflowRecord workflow, VerifiedNativeParticipant? actor, string session, DateTimeOffset now)
    {
        var reference = workflow.Packet?.AuthorityRef ?? "";
        if (!reference.StartsWith(StandingPreferences.GrantPrefix, StringComparison.Ordinal)) return null;
        var id = reference[StandingPreferences.GrantPrefix.Length..];
        if ((board.Json["goalBindings"] as JsonObject)?[id] is not JsonObject goal)
            return board.Standing?.Grant(id)?.Json["approvedGoal"] is not null || board.Tasks.Any(task => Text(task.Json, "goalAuthorityRef") == reference)
                ? "binding_unavailable" : null;
        var grant = board.Standing?.Grant(id);
        if (grant is null || !JsonNode.DeepEquals(grant.Json["approvedGoal"], PublicGoal(goal)) || grant.ExpiresAtText != Text(goal, "expiresAt") || Text(grant.Json[StandingDecision.Key] as JsonObject ?? new(), StandingDecision.AuditRefKey) != Text(goal, "auditRef")) return "revoked";
        if (grant.ExpiresAt <= now) return "expired";
        if (grant.Scope != "repository" || !grant.TargetSet.SetEquals([board.RepositoryId])) return "scope";
        var root = board.Task(Text(goal, "rootTaskId"));
        if (root?.Workflow?.Packet is not { } rootPacket || root.Workflow.State == "completed" || rootPacket.Goal != Text(goal, "purpose") || rootPacket.AuthorityRef != reference || Text(root.Json, "goalAuthorityRef") != reference) return "root";
        if (actor is null || actor.SessionId != session || session.Length == 0 || actor.RepositoryId != board.RepositoryId || actor.ParticipantId.Length == 0) return "participant";
        if (!Flag(goal, "repositoryWide") && !Strings(goal["members"]).Contains(actor.ParticipantId, StringComparer.Ordinal)) return "participant";
        return null;
    }

    /// <summary>
    /// Drops the private bindings of goals that can never grant again (grant gone, expired or replaced, root missing or completed) once no
    /// unfinished task still names them. A task still pinned to a dropped binding stays refused (<c>binding_unavailable</c>), and grant IDs
    /// are never reused, so pruning frees capacity without reviving or widening any goal.
    /// </summary>
    public static void PruneEnded(BoardState board, DateTimeOffset now)
    {
        if (board.Json["goalBindings"] is not JsonObject bindings) return;
        foreach (var (id, node) in bindings.ToArray())
        {
            if (node is not JsonObject goal) continue;
            var grant = board.Standing?.Grant(id);
            var root = board.Task(Text(goal, "rootTaskId"));
            var ended = grant is null || grant.ExpiresAt <= now || !JsonNode.DeepEquals(grant.Json["approvedGoal"], PublicGoal(goal)) || root?.Workflow is not { } workflow || workflow.State == "completed";
            var reference = StandingPreferences.GrantPrefix + id;
            if (ended && !board.Tasks.Any(task => Text(task.Json, "goalAuthorityRef") == reference && task.Workflow?.State != "completed")) bindings.Remove(id);
        }
    }
}

/// <summary>Produces only the sanitized candidate; the private board binding remains in the input document.</summary>
public static class GoalPresentationCandidates
{
    public static StandingPresentation? ReadPending(JsonObject board, string boardId, string proposalId, string principal, string session, string request, VerifiedNativeParticipant? participant)
    {
        if (board["standing"] is not JsonObject standing) return null;
        var summary = StandingPreferences.PendingPresentation(standing, boardId, proposalId, principal, session, request, board, participant);
        return summary is null ? null : new(boardId, proposalId, summary.ToJsonString());
    }
}

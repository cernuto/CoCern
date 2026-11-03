using System.Text;
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
/// A task's optional structured workflow: its packet, acceptance criteria and blockers. A workflow only gates
/// execution when present; completion requires every criterion passed with evidence and no unresolved action.
/// </summary>
internal static class TaskWorkflow
{
    private const int MaxWorkflowBytes = 12_288;
    private const int MaxTextLength = 2048;
    private const int MaxItemIdLength = 128;
    private const int MaxCriteria = 32;
    private const int MaxBlockers = 16;
    private const int MaxPriority = 1000;
    private const int OfferBudgetBytes = 2048;
    private static readonly string[] PacketFields = [WorkflowPacket.GoalKey, WorkflowPacket.TargetKey, WorkflowPacket.AuthorityRefKey, WorkflowPacket.RestorationKey, WorkflowPacket.StopConditionKey];

    /// <summary>A task without a workflow is always executable; with one it must be ready, unblocked and targeted at this session if targeted at all.</summary>
    public static bool AllowsExecution(BoardTask task, string session) =>
        task.Workflow is not { } workflow
        || workflow.State == "ready" && workflow.BlockerList is { Count: 0 } && TargetsSession(workflow, session);

    public static RelayReply NotReady(BoardTask task) => Error(WorkflowNotReady, new { state = task.Workflow!.State }, task.Version);

    /// <summary>
    /// The gate <c>task_claim</c> (and its replay) and <c>action_begin</c> share, or null when this session may run the task now: the workflow
    /// allows it (<see cref="AllowsExecution"/>), every <c>dependsOn</c> task is completed, and a grant named in <c>authorityRef</c> is still
    /// valid. Dependencies and grants are read exactly as offers read them (<see cref="IsOfferable"/>), so the two cannot drift.
    /// </summary>
    public static RelayReply? ExecutionRefusal(BoardState board, BoardTask task, string session, DateTimeOffset now)
    {
        if (!AllowsExecution(task, session)) return NotReady(task);
        if (task.Workflow is not { } workflow) return null;
        if (UnfinishedDependencies(board, workflow) is { Length: > 0 } unfinished) return Error(DependenciesIncomplete, new { dependsOn = unfinished }, task.Version);
        return GrantRefusal(board, workflow, now) is { } refusal ? refusal with { Version = task.Version } : null;
    }

    /// <summary>
    /// <c>DeclaredResourcesNeeded</c> with the declared names when the hub asked for them (<see cref="Operations.ResolveDeclaredField"/>) and the task
    /// declares any, or null. The hub then resolves them and calls again with <c>declaredResources</c>.
    /// </summary>
    public static RelayReply? DeclaredResourcesNeeded(BoardTask task, JsonObject args) =>
        Flag(args, Operations.ResolveDeclaredField) && task.Workflow is { } workflow && workflow.Resources.Distinct(StringComparer.Ordinal).ToArray() is { Length: > 0 } declared
            ? Error(RelayErrorCodes.DeclaredResourcesNeeded, new { declared }, task.Version)
            : null;

    /// <summary>
    /// The <c>ResourcesRequired</c> refusal when <paramref name="held"/> (canonical resource IDs: a claim's <c>resourceIds</c>, or the
    /// task's claimed set at <c>action_begin</c>) does not cover every resource the packet declares, or null. The hub resolves each declared
    /// name (aliases too, by the session's machine) and passes the map as <c>declaredResources</c>, with <c>""</c> for a name no resource
    /// answers to. A name missing from the map (the packet changed since, or no hub) must be listed as itself. An unregistered name has no
    /// lock to take, so it is only refused on a board with <c>requireResources</c>.
    /// </summary>
    public static RelayReply? ResourceRefusal(BoardState board, BoardTask task, JsonObject args, IEnumerable<string> held)
    {
        if (task.Workflow is not { } workflow || workflow.Resources.ToArray() is not { Length: > 0 } declared) return null;
        var resolved = args["declaredResources"] as JsonObject;
        var have = held.ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        var unregistered = new List<string>();
        foreach (var name in declared)
        {
            var canonical = resolved?[name] is JsonValue value && value.TryGetValue<string>(out var id) ? id : name;
            if (canonical.Length == 0) { if (board.RequiresResources) unregistered.Add(name); }
            else if (!have.Contains(canonical)) missing.Add(name);
        }
        return missing.Count + unregistered.Count == 0 ? null : Error(ResourcesRequired, new { missing, unregistered, declared }, task.Version);
    }

    /// <summary>The <c>GrantInvalid</c> reply when the workflow's <c>authorityRef</c> names a grant that is unknown, expired or out of scope; readiness and execution both use it.</summary>
    public static RelayReply? GrantRefusal(BoardState board, WorkflowRecord workflow, DateTimeOffset now) =>
        StandingPreferences.GrantProblem(board, workflow, now) is { } reason ? Error(GrantInvalid, new { authorityRef = workflow.Packet!.AuthorityRef, reason }) : null;

    private static bool TargetsSession(WorkflowRecord workflow, string session) => workflow.TargetSessionId.Length == 0 || workflow.TargetSessionId == session;

    /// <summary>
    /// Returns the failure code, or null when <paramref name="workflow"/> (a writer's, to be stored as given) may replace <paramref name="prior"/>.
    /// Its fields are checked by the keys it will be stored under.
    /// </summary>
    public static string? Validate(JsonObject workflow, WorkflowRecord? prior, bool unresolved)
    {
        if (Encoding.UTF8.GetByteCount(workflow.ToJsonString()) > MaxWorkflowBytes) return InvalidWorkflow;
        var state = Text(workflow, WorkflowRecord.StateKey);
        if (!WorkflowStates.All.Contains(state) || workflow[WorkflowRecord.PacketKey] is not JsonObject packet || workflow[WorkflowRecord.CriteriaKey] is not JsonArray criteria
            || workflow[WorkflowRecord.BlockersKey] is not JsonArray blockers) return InvalidWorkflow;
        static bool Value(JsonObject obj, string key, int max) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= max;
        if (PacketFields.Any(key => !Value(packet, key, MaxTextLength))) return InvalidWorkflow;
        if (!Identifiers(workflow, WorkflowRecord.DependsOnKey, RelayLimits.MaxDependencies) || !Identifiers(packet, WorkflowPacket.ResourcesKey, RelayLimits.MaxClaimResources)) return InvalidWorkflow;
        if (criteria.Count is < 1 or > MaxCriteria || blockers.Count > MaxBlockers
            || workflow[WorkflowRecord.TargetSessionIdKey] is not null && !Value(workflow, WorkflowRecord.TargetSessionIdKey, 256)) return InvalidWorkflow;
        if (workflow[WorkflowRecord.PriorityKey] is not null && (workflow[WorkflowRecord.PriorityKey] is not JsonValue priority || priority.GetValueKind() != JsonValueKind.Number
            || !long.TryParse(priority.ToJsonString(), out var number) || number is < -MaxPriority or > MaxPriority)) return InvalidWorkflow;
        if (workflow[WorkflowRecord.RecheckEvidenceRefKey] is not null && !Value(workflow, WorkflowRecord.RecheckEvidenceRefKey, MaxTextLength)) return InvalidWorkflow;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in criteria)
        {
            if (item is not JsonObject criterion || !Value(criterion, WorkflowCriterion.IdKey, MaxItemIdLength) || !Value(criterion, WorkflowCriterion.DescriptionKey, MaxTextLength)
                || !ids.Add(Text(criterion, WorkflowCriterion.IdKey)) || !CriterionResults.All.Contains(Text(criterion, WorkflowCriterion.ResultKey))) return InvalidWorkflow;
            if (criterion[WorkflowCriterion.EvidenceRefKey] is not null && !Value(criterion, WorkflowCriterion.EvidenceRefKey, MaxTextLength)
                || Text(criterion, WorkflowCriterion.ResultKey) == "passed" && !Value(criterion, WorkflowCriterion.EvidenceRefKey, MaxTextLength)) return InvalidWorkflow;
        }
        ids.Clear();
        foreach (var item in blockers)
        {
            if (item is not JsonObject blocker || !Value(blocker, WorkflowBlocker.IdKey, MaxItemIdLength) || !ids.Add(Text(blocker, WorkflowBlocker.IdKey))
                || !Value(blocker, WorkflowBlocker.SummaryKey, MaxTextLength) || !Value(blocker, WorkflowBlocker.RecheckConditionKey, MaxTextLength)
                || !BlockerKinds.All.Contains(Text(blocker, WorkflowBlocker.KindKey))) return InvalidWorkflow;
        }
        if (state == "blocked" && blockers.Count == 0 || state != "blocked" && blockers.Count != 0) return InvalidWorkflow;
        if (state == "ready" && prior?.State == "blocked" && !Value(workflow, WorkflowRecord.RecheckEvidenceRefKey, MaxTextLength)) return WorkflowRecheckRequired;
        if (state == "completed" && (unresolved || criteria.Any(x => new WorkflowCriterion(x!.AsObject()).Result != "passed"))) return WorkflowIncomplete;
        return null;
    }

    /// <summary>An optional list of at most <paramref name="maximum"/> distinct, nonempty identifiers.</summary>
    private static bool Identifiers(JsonObject obj, string key, int maximum)
    {
        if (obj[key] is null) return true;
        if (obj[key] is not JsonArray items || items.Count > maximum) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.All(item => item is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) && id.Length <= RelayLimits.MaxIdentifierLength && seen.Add(id));
    }

    /// <summary>A workflow's dependencies, or none when <c>dependsOn</c> is not a list of strings (see <see cref="WorkflowRecord.DependsOn"/>).</summary>
    public static IEnumerable<string> Dependencies(JsonObject workflow) => new WorkflowRecord(workflow).DependsOn;

    /// <summary>The resources a workflow's packet declares, or none (see <see cref="WorkflowRecord.Resources"/>).</summary>
    public static IEnumerable<string> Resources(JsonObject workflow) => new WorkflowRecord(workflow).Resources;

    /// <summary>Returns the failure code, or null when every dependency names a task on this board and none leads back to <paramref name="taskId"/>.</summary>
    public static string? CheckDependencies(BoardState board, string taskId, WorkflowRecord workflow)
    {
        var direct = workflow.DependsOn.ToArray();
        // An archived task is a completed one that left the board; it is known and has no dependencies of its own.
        if (direct.Any(id => board.Task(id) is null && !board.IsArchived(id))) return DependencyUnknown;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(direct);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id == taskId) return DependencyCycle;
            if (!seen.Add(id) || board.Task(id)?.Workflow is not { } next) continue;
            foreach (var dependency in next.DependsOn) pending.Push(dependency);
        }
        return null;
    }

    private static bool DependenciesDone(BoardState board, WorkflowRecord workflow) => UnfinishedDependencies(board, workflow).Length == 0;

    /// <summary>
    /// The <c>dependsOn</c> tasks whose workflow is not <c>completed</c>, including IDs no longer on the board. An archived task was
    /// completed, so it counts as done. A legacy shape lists none.
    /// </summary>
    private static string[] UnfinishedDependencies(BoardState board, WorkflowRecord workflow) =>
        workflow.DependsOn.Where(id => (board.Task(id)?.Workflow is not { } dependency || dependency.State != "completed") && !board.IsArchived(id)).ToArray();

    /// <summary>Ready tasks that depend on <paramref name="completedTaskId"/> and now have every dependency completed.</summary>
    public static IReadOnlyList<string> Unblocked(BoardState board, string completedTaskId) =>
        board.Tasks
            .Where(task => task.Workflow is { } workflow && workflow.State == "ready" && workflow.DependsOn.Contains(completedTaskId, StringComparer.Ordinal) && DependenciesDone(board, workflow))
            .Select(task => task.TaskId).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A workflow's summary for a bounded read. Replies that are compacted again pass a summary back in, so a workflow whose blockers are
    /// not a list keeps the count it already has.
    /// </summary>
    public static JsonObject Summary(JsonObject workflow) => new()
    {
        ["state"] = workflow[WorkflowRecord.StateKey]?.DeepClone(), ["targetSessionId"] = workflow[WorkflowRecord.TargetSessionIdKey]?.DeepClone(),
        ["priority"] = new WorkflowRecord(workflow).Priority, ["blockerCount"] = new WorkflowRecord(workflow).BlockerList?.Count ?? Number(workflow, "blockerCount"), ["reported"] = true
    };

    /// <summary>
    /// The highest-priority ready task this session could claim next (<see cref="NextWorkRead"/>). An offer is advisory and acquires nothing.
    /// Within equal priority, a task listing none of the <c>busyResources</c> comes first, then task ID. A <c>limit</c> also returns up to
    /// that many candidates with their listed resources, so the caller can look up which are claimed.
    /// </summary>
    public static RelayReply NextWork(AggregateContext context)
    {
        if (!NextWorkRead.Decode(context.Args).TryGet(out var read, out var malformed)) return Malformed(malformed);
        var (board, now) = (new BoardState(context.State), context.Now);
        var session = read.SessionId;
        var current = read.TaskId ?? "";
        var busy = new HashSet<string>(read.BusyResources ?? [], StringComparer.Ordinal);
        var limit = (int)Math.Clamp(read.Limit, 0, RelayLimits.NextWorkCandidates);
        var ranked = board.TaskMap.Select(x => new BoardTask(x.Value!.AsObject()))
            .Where(task => task.TaskId != current && IsOfferable(board, task, session, now))
            .OrderByDescending(task => task.Workflow!.Priority)
            .ThenBy(task => busy.Count > 0 && task.Workflow!.Resources.Any(busy.Contains))
            .ThenBy(task => task.TaskId, StringComparer.Ordinal)
            .Take(Math.Max(limit, 1)).ToArray();
        RelayReply WithCandidates(JsonObject data)
        {
            if (limit > 0) data["candidates"] = new JsonArray(ranked.Select(task => (JsonNode)new JsonObject { ["taskId"] = task.TaskId, ["resources"] = new JsonArray(task.Workflow!.Resources.Select(id => (JsonNode)id).ToArray()) }).ToArray());
            return Ok(data);
        }
        if (ranked.Length == 0) return WithCandidates(new JsonObject { ["ok"] = true, ["code"] = "NoEligibleWork" });
        var eligible = ranked[0];
        var offer = new JsonObject { ["taskId"] = eligible.TaskId, ["expectedVersion"] = eligible.Version, ["snapshotRevision"] = board.TaskRevision, ["claimRequired"] = true, ["workflow"] = eligible.Workflow!.Json.DeepClone() };
        if (!FitsWire(Ok(new { ok = true, code = "Offered", offer }), OfferBudgetBytes)) { offer.Remove("workflow"); offer["detailFetchRequired"] = true; }
        return WithCandidates(new JsonObject { ["ok"] = true, ["code"] = "Offered", ["offer"] = offer });
    }

    /// <summary>
    /// Why a standing order may not offer this task to <paramref name="session"/>, or null: the next-work checks of <see cref="IsOfferable"/>
    /// except the target-session match, each with a reason for the order log.
    /// </summary>
    public static string? OfferProblem(BoardState board, BoardTask task, string session, DateTimeOffset now)
    {
        if (task.Workflow is not { } workflow) return "has no workflow";
        if (workflow.Blockers.Any(blocker => blocker.Kind == AttentionWeights.ProviderRejection)) return "has a provider_rejection blocker";
        if (workflow.State != "ready") return "is " + workflow.State;
        if (Validate(workflow.Json, null, TaskActions.HasUnresolved(task)) is not null) return "has an invalid workflow";
        if (TaskActions.HasUnresolved(task)) return "has an unresolved action";
        if (task.Owner is { Length: > 0 } owner && owner != session && task.LeaseUntil > now) return "is claimed by another session";
        if (!DependenciesDone(board, workflow)) return "has dependencies not completed";
        if (StandingPreferences.GrantProblem(board, workflow, now) is { } grant) return "names a grant that is " + (grant == "scope" ? "out of scope" : grant);
        return null;
    }

    private static bool IsOfferable(BoardState board, BoardTask task, string session, DateTimeOffset now) =>
        task.Workflow is { } workflow
        && workflow.State == "ready"
        && Validate(workflow.Json, null, TaskActions.HasUnresolved(task)) is null
        && !TaskActions.HasUnresolved(task)
        && TargetsSession(workflow, session)
        && (task.Owner.Length == 0 || task.Owner == session || task.LeaseUntil <= now)
        && DependenciesDone(board, workflow)
        && StandingPreferences.GrantProblem(board, workflow, now) is null;
}

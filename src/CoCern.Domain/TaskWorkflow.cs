using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
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
    private static readonly string[] PacketFields = ["goal", "target", "authorityRef", "restoration", "stopCondition"];
    private static readonly string[] BlockerKinds = ["coordinator_hold", "provider_rejection", "tool_unavailable", "resource_busy", "missing_fixture", "prerequisite", "other"];

    /// <summary>A task without a workflow is always executable; with one it must be ready, unblocked and targeted at this session if targeted at all.</summary>
    public static bool AllowsExecution(JsonObject task, string session) =>
        task["workflow"] is not JsonObject workflow
        || Text(workflow, "state") == "ready" && workflow["blockers"] is JsonArray { Count: 0 } && TargetsSession(workflow, session);

    public static RelayReply NotReady(JsonObject task) => Error(WorkflowNotReady, new { state = Text(task["workflow"]!.AsObject(), "state") }, Number(task, "version"));

    /// <summary>
    /// The gate <c>task_claim</c> (and its replay) and <c>action_begin</c> share, or null when this session may run the task now: the workflow
    /// allows it (<see cref="AllowsExecution"/>), every <c>dependsOn</c> task is completed, and a grant named in <c>authorityRef</c> is still
    /// valid. Dependencies and grants are read exactly as offers read them (<see cref="IsOfferable"/>), so the two cannot drift.
    /// </summary>
    public static RelayReply? ExecutionRefusal(JsonObject board, JsonObject task, string session, DateTimeOffset now)
    {
        if (!AllowsExecution(task, session)) return NotReady(task);
        if (task["workflow"] is not JsonObject workflow) return null;
        if (UnfinishedDependencies(ObjectAt(board, "tasks"), workflow) is { Length: > 0 } unfinished) return Error(DependenciesIncomplete, new { dependsOn = unfinished }, Number(task, "version"));
        return GrantRefusal(board, workflow, now) is { } refusal ? refusal with { Version = Number(task, "version") } : null;
    }

    /// <summary>The <c>GrantInvalid</c> reply when the workflow's <c>authorityRef</c> names a grant that is unknown, expired or out of scope; readiness and execution both use it.</summary>
    public static RelayReply? GrantRefusal(JsonObject board, JsonObject workflow, DateTimeOffset now) =>
        StandingPreferences.GrantProblem(board, workflow, now) is { } reason ? Error(GrantInvalid, new { authorityRef = Text(workflow["packet"]!.AsObject(), "authorityRef"), reason }) : null;

    private static bool TargetsSession(JsonObject workflow, string session) => Text(workflow, "targetSessionId").Length == 0 || Text(workflow, "targetSessionId") == session;

    /// <summary>Returns the failure code, or null when the workflow may replace <paramref name="prior"/>.</summary>
    public static string? Validate(JsonObject workflow, JsonObject? prior, bool unresolved)
    {
        if (Encoding.UTF8.GetByteCount(workflow.ToJsonString()) > MaxWorkflowBytes) return InvalidWorkflow;
        var state = Text(workflow, "state");
        if (state is not ("ready" or "blocked" or "completed") || workflow["packet"] is not JsonObject packet || workflow["criteria"] is not JsonArray criteria || workflow["blockers"] is not JsonArray blockers) return InvalidWorkflow;
        static bool Value(JsonObject obj, string key, int max) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= max;
        if (PacketFields.Any(key => !Value(packet, key, MaxTextLength))) return InvalidWorkflow;
        if (!Identifiers(workflow, "dependsOn", RelayLimits.MaxDependencies) || !Identifiers(packet, "resources", RelayLimits.MaxClaimResources)) return InvalidWorkflow;
        if (criteria.Count is < 1 or > MaxCriteria || blockers.Count > MaxBlockers || workflow["targetSessionId"] is not null && !Value(workflow, "targetSessionId", 256)) return InvalidWorkflow;
        if (workflow["priority"] is not null && (workflow["priority"] is not JsonValue priority || priority.GetValueKind() != JsonValueKind.Number || !long.TryParse(priority.ToJsonString(), out var number) || number is < -MaxPriority or > MaxPriority)) return InvalidWorkflow;
        if (workflow["recheckEvidenceRef"] is not null && !Value(workflow, "recheckEvidenceRef", MaxTextLength)) return InvalidWorkflow;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in criteria)
        {
            if (item is not JsonObject criterion || !Value(criterion, "id", MaxItemIdLength) || !Value(criterion, "description", MaxTextLength) || !ids.Add(Text(criterion, "id")) || Text(criterion, "result") is not ("not_run" or "passed" or "failed")) return InvalidWorkflow;
            if (criterion["evidenceRef"] is not null && !Value(criterion, "evidenceRef", MaxTextLength) || Text(criterion, "result") == "passed" && !Value(criterion, "evidenceRef", MaxTextLength)) return InvalidWorkflow;
        }
        ids.Clear();
        foreach (var item in blockers)
        {
            if (item is not JsonObject blocker || !Value(blocker, "id", MaxItemIdLength) || !ids.Add(Text(blocker, "id")) || !Value(blocker, "summary", MaxTextLength) || !Value(blocker, "recheckCondition", MaxTextLength) || !BlockerKinds.Contains(Text(blocker, "kind"))) return InvalidWorkflow;
        }
        if (state == "blocked" && blockers.Count == 0 || state != "blocked" && blockers.Count != 0) return InvalidWorkflow;
        if (state == "ready" && Text(prior ?? new(), "state") == "blocked" && !Value(workflow, "recheckEvidenceRef", MaxTextLength)) return WorkflowRecheckRequired;
        if (state == "completed" && (unresolved || criteria.Any(x => Text(x!.AsObject(), "result") != "passed"))) return WorkflowIncomplete;
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

    /// <summary>
    /// A workflow's dependencies, or none when <c>dependsOn</c> is not an array of strings. Boards always accepted unknown workflow fields,
    /// so a stored legacy value of another shape is skipped here rather than failing every later write; it still fails <see cref="Validate"/>.
    /// </summary>
    public static IEnumerable<string> Dependencies(JsonObject workflow) => StringList(workflow["dependsOn"]);
    public static IEnumerable<string> Resources(JsonObject workflow) => workflow["packet"] is JsonObject packet ? StringList(packet["resources"]) : [];

    private static IEnumerable<string> StringList(JsonNode? node) =>
        node is JsonArray items && items.All(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String) ? items.Select(item => item!.GetValue<string>()) : [];

    /// <summary>The board setting that refuses <c>ready</c> without <c>packet.resources</c>. Off unless set with <c>standing_put</c>.</summary>
    public static bool RequiresResources(JsonObject board) => board["standing"]?["settings"]?["requireResources"] is JsonValue value && value.TryGetValue<bool>(out var required) && required;

    /// <summary>Returns the failure code, or null when every dependency names a task on this board and none leads back to <paramref name="taskId"/>.</summary>
    public static string? CheckDependencies(JsonObject tasks, string taskId, JsonObject workflow)
    {
        var direct = Dependencies(workflow).ToArray();
        if (direct.Any(id => tasks[id] is not JsonObject)) return DependencyUnknown;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(direct);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id == taskId) return DependencyCycle;
            if (!seen.Add(id) || tasks[id]?["workflow"] is not JsonObject next) continue;
            foreach (var dependency in Dependencies(next)) pending.Push(dependency);
        }
        return null;
    }

    private static bool DependenciesDone(JsonObject tasks, JsonObject workflow) => UnfinishedDependencies(tasks, workflow).Length == 0;

    /// <summary>The <c>dependsOn</c> tasks whose workflow is not <c>completed</c>, including IDs no longer on the board. A legacy shape lists none.</summary>
    private static string[] UnfinishedDependencies(JsonObject tasks, JsonObject workflow) =>
        Dependencies(workflow).Where(id => tasks[id]?["workflow"] is not JsonObject dependency || Text(dependency, "state") != "completed").ToArray();

    /// <summary>Ready tasks that depend on <paramref name="completedTaskId"/> and now have every dependency completed.</summary>
    public static IReadOnlyList<string> Unblocked(JsonObject tasks, string completedTaskId) =>
        tasks.Select(x => x.Value!.AsObject())
            .Where(task => task["workflow"] is JsonObject workflow && Text(workflow, "state") == "ready" && Dependencies(workflow).Contains(completedTaskId, StringComparer.Ordinal) && DependenciesDone(tasks, workflow))
            .Select(task => Text(task, "taskId")).Order(StringComparer.Ordinal).ToArray();

    public static JsonObject Summary(JsonObject workflow) => new()
    {
        ["state"] = workflow["state"]?.DeepClone(), ["targetSessionId"] = workflow["targetSessionId"]?.DeepClone(), ["priority"] = Number(workflow, "priority"),
        ["blockerCount"] = (workflow["blockers"] as JsonArray)?.Count ?? Number(workflow, "blockerCount"), ["reported"] = true
    };

    /// <summary>
    /// The highest-priority ready task this session could claim next. An offer is advisory and acquires nothing. Within equal priority,
    /// a task listing none of the internal <c>busyResources</c> comes first, then task ID. The internal <c>limit</c> also returns up to
    /// that many candidates with their listed resources, so the caller can look up which are claimed.
    /// </summary>
    public static RelayReply NextWork(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var session = Text(args, "sessionId");
        var current = Text(args, "taskId");
        var tasks = ObjectAt(state, "tasks");
        var busy = new HashSet<string>(Strings(args["busyResources"]), StringComparer.Ordinal);
        var limit = (int)Math.Clamp(Number(args, "limit"), 0, RelayLimits.NextWorkCandidates);
        var ranked = tasks.Select(x => x.Value!.AsObject())
            .Where(task => Text(task, "taskId") != current && IsOfferable(state, tasks, task, session, now))
            .OrderByDescending(task => Number(task["workflow"]!.AsObject(), "priority"))
            .ThenBy(task => busy.Count > 0 && Resources(task["workflow"]!.AsObject()).Any(busy.Contains))
            .ThenBy(task => Text(task, "taskId"), StringComparer.Ordinal)
            .Take(Math.Max(limit, 1)).ToArray();
        RelayReply WithCandidates(JsonObject data)
        {
            if (limit > 0) data["candidates"] = new JsonArray(ranked.Select(task => (JsonNode)new JsonObject { ["taskId"] = Text(task, "taskId"), ["resources"] = new JsonArray(Resources(task["workflow"]!.AsObject()).Select(id => (JsonNode)id).ToArray()) }).ToArray());
            return Ok(data);
        }
        if (ranked.Length == 0) return WithCandidates(new JsonObject { ["ok"] = true, ["code"] = "NoEligibleWork" });
        var eligible = ranked[0];
        var offer = new JsonObject { ["taskId"] = Text(eligible, "taskId"), ["expectedVersion"] = Number(eligible, "version"), ["snapshotRevision"] = Number(state, "taskRevision"), ["claimRequired"] = true, ["workflow"] = eligible["workflow"]!.DeepClone() };
        if (!FitsWire(Ok(new { ok = true, code = "Offered", offer }), OfferBudgetBytes)) { offer.Remove("workflow"); offer["detailFetchRequired"] = true; }
        return WithCandidates(new JsonObject { ["ok"] = true, ["code"] = "Offered", ["offer"] = offer });
    }

    /// <summary>
    /// Why a standing order may not offer this task to <paramref name="session"/>, or null: the next-work checks of <see cref="IsOfferable"/>
    /// except the target-session match, each with a reason for the order log.
    /// </summary>
    public static string? OfferProblem(JsonObject board, JsonObject tasks, JsonObject task, string session, DateTimeOffset now)
    {
        if (task["workflow"] is not JsonObject workflow) return "has no workflow";
        if ((workflow["blockers"] as JsonArray ?? []).OfType<JsonObject>().Any(blocker => Text(blocker, "kind") == AttentionWeights.ProviderRejection)) return "has a provider_rejection blocker";
        if (Text(workflow, "state") != "ready") return "is " + Text(workflow, "state");
        if (Validate(workflow, null, TaskActions.HasUnresolved(task)) is not null) return "has an invalid workflow";
        if (TaskActions.HasUnresolved(task)) return "has an unresolved action";
        if (Text(task, "owner") is { Length: > 0 } owner && owner != session && Date(task, "leaseUntil") > now) return "is claimed by another session";
        if (!DependenciesDone(tasks, workflow)) return "has dependencies not completed";
        if (StandingPreferences.GrantProblem(board, workflow, now) is { } grant) return "names a grant that is " + (grant == "scope" ? "out of scope" : grant);
        return null;
    }

    private static bool IsOfferable(JsonObject board, JsonObject tasks, JsonObject task, string session, DateTimeOffset now) =>
        task["workflow"] is JsonObject workflow
        && Text(workflow, "state") == "ready"
        && Validate(workflow, null, TaskActions.HasUnresolved(task)) is null
        && !TaskActions.HasUnresolved(task)
        && TargetsSession(workflow, session)
        && (Text(task, "owner").Length == 0 || Text(task, "owner") == session || Date(task, "leaseUntil") <= now)
        && DependenciesDone(tasks, workflow)
        && StandingPreferences.GrantProblem(board, workflow, now) is null;
}

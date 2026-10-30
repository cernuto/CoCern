using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

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

    private static bool TargetsSession(JsonObject workflow, string session) => Text(workflow, "targetSessionId").Length == 0 || Text(workflow, "targetSessionId") == session;

    /// <summary>Returns the failure code, or null when the workflow may replace <paramref name="prior"/>.</summary>
    public static string? Validate(JsonObject workflow, JsonObject? prior, bool unresolved)
    {
        if (Encoding.UTF8.GetByteCount(workflow.ToJsonString()) > MaxWorkflowBytes) return InvalidWorkflow;
        var state = Text(workflow, "state");
        if (state is not ("ready" or "blocked" or "completed") || workflow["packet"] is not JsonObject packet || workflow["criteria"] is not JsonArray criteria || workflow["blockers"] is not JsonArray blockers) return InvalidWorkflow;
        static bool Value(JsonObject obj, string key, int max) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= max;
        if (PacketFields.Any(key => !Value(packet, key, MaxTextLength))) return InvalidWorkflow;
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

    public static JsonObject Summary(JsonObject workflow) => new()
    {
        ["state"] = workflow["state"]?.DeepClone(), ["targetSessionId"] = workflow["targetSessionId"]?.DeepClone(), ["priority"] = Number(workflow, "priority"),
        ["blockerCount"] = (workflow["blockers"] as JsonArray)?.Count ?? Number(workflow, "blockerCount"), ["reported"] = true
    };

    /// <summary>The highest-priority ready task this session could claim next. An offer is advisory and acquires nothing.</summary>
    public static RelayReply NextWork(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var session = Text(args, "sessionId");
        var current = Text(args, "taskId");
        var eligible = ObjectAt(state, "tasks").Select(x => x.Value!.AsObject())
            .Where(task => Text(task, "taskId") != current && IsOfferable(task, session, now))
            .OrderByDescending(task => Number(task["workflow"]!.AsObject(), "priority"))
            .ThenBy(task => Text(task, "taskId"), StringComparer.Ordinal)
            .FirstOrDefault();
        if (eligible is null) return Ok(new { ok = true, code = "NoEligibleWork" });
        var offer = new JsonObject { ["taskId"] = Text(eligible, "taskId"), ["expectedVersion"] = Number(eligible, "version"), ["snapshotRevision"] = Number(state, "taskRevision"), ["claimRequired"] = true, ["workflow"] = eligible["workflow"]!.DeepClone() };
        var reply = Ok(new { ok = true, code = "Offered", offer });
        if (!FitsWire(reply, OfferBudgetBytes)) { offer.Remove("workflow"); offer["detailFetchRequired"] = true; reply = Ok(new { ok = true, code = "Offered", offer }); }
        return reply;
    }

    private static bool IsOfferable(JsonObject task, string session, DateTimeOffset now) =>
        task["workflow"] is JsonObject workflow
        && Text(workflow, "state") == "ready"
        && Validate(workflow, null, TaskActions.HasUnresolved(task)) is null
        && !TaskActions.HasUnresolved(task)
        && TargetsSession(workflow, session)
        && (Text(task, "owner").Length == 0 || Text(task, "owner") == session || Date(task, "leaseUntil") <= now);
}

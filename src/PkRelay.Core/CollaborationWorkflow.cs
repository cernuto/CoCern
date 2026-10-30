using System.Text;
using System.Text.Json.Nodes;

namespace PkRelay.Core;

public static partial class CoordinationStateMachine
{
    private static bool WorkflowAllowsExecution(JsonObject task, string session) => task["workflow"] is not JsonObject workflow || Text(workflow, "state") == "ready" && workflow["blockers"] is JsonArray { Count: 0 } && (Text(workflow, "targetSessionId").Length == 0 || Text(workflow, "targetSessionId") == session);

    private static RelayReply Board(JsonObject state, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now, List<DurableEffect> effects)
    {
        var tasks = Object(state, "tasks");
        // Migrate only current markers. No copied history or changes to optimistic task versions.
        foreach (var task in tasks.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value!.AsObject()))
            if (!task.ContainsKey("changeRevision")) { state["taskRevision"] = Number(state, "taskRevision") + 1; task["changeRevision"] = Number(state, "taskRevision"); }
        var taskId = Text(args, "taskId");
        var before = IsMutation(operation) && taskId.Length > 0 ? tasks[taskId]?.ToJsonString() : null;
        var result = BoardCore(state, caller, operation, args, now, effects);
        if (!result.Ok) return result;
        if (IsMutation(operation) && taskId.Length > 0 && tasks[taskId] is JsonObject changedTask && before != changedTask.ToJsonString())
        {
            state["taskRevision"] = Number(state, "taskRevision") + 1;
            changedTask["changeRevision"] = Number(state, "taskRevision");
        }
        var data = JsonNode.Parse(result.DataJson)!.AsObject();
        if (Text(data, "taskId") is { Length: > 0 } id && tasks[id] is JsonObject changed) data["changeRevision"] = Number(changed, "changeRevision");
        return result with { DataJson = data.ToJsonString() };
    }

    private static RelayReply DeltaPage(JsonObject state, JsonObject args)
    {
        static bool Integer(JsonObject obj, string key) => obj[key] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number && long.TryParse(value.ToJsonString(), out _);
        if (!Integer(args, "sinceRevision") || args.ContainsKey("snapshotRevision") && !Integer(args, "snapshotRevision") || args.ContainsKey("cursor") && !Integer(args, "cursor")) return Error("InvalidArguments");
        var since = Number(args, "sinceRevision"); var current = Number(state, "taskRevision");
        var offset = Number(args, "cursor");
        if (since < 0 || since > current || offset < 0 || offset > MaxRecords || offset > 0 && !args.ContainsKey("snapshotRevision")) return Error("InvalidArguments");
        if (args.ContainsKey("snapshotRevision") && Number(args, "snapshotRevision") != current) return Error("DeltaRestartRequired", new { restartRequired = true, currentRevision = current });
        var all = Object(state, "tasks").Where(x => Number(x.Value!.AsObject(), "changeRevision") > since).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode)TaskSummary(x.Value!.AsObject())).ToArray();
        var limit = (int)Math.Clamp(Number(args, "limit", 20), 1, MaxPageSize);
        var items = all.Skip((int)offset).Take(limit).Select(x => x.DeepClone()).ToList();
        RelayReply Reply() => Ok(new { items, snapshotRevision = current, nextCursor = offset + items.Count < all.Length ? (long?)(offset + items.Count) : null, truncated = offset + items.Count < all.Length, total = all.Length });
        while (items.Count > 1 && !FitsWire(Reply(), MaxPayloadBytes - 8192)) items.RemoveAt(items.Count - 1);
        return Reply();
    }

    private static string? ValidateWorkflow(JsonObject workflow, JsonObject? prior, bool unresolved)
    {
        if (Encoding.UTF8.GetByteCount(workflow.ToJsonString()) > 12_288) return "InvalidWorkflow";
        var state = Text(workflow, "state");
        if (state is not ("ready" or "blocked" or "completed") || workflow["packet"] is not JsonObject packet || workflow["criteria"] is not JsonArray criteria || workflow["blockers"] is not JsonArray blockers) return "InvalidWorkflow";
        static bool Value(JsonObject obj, string key, int max) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= max;
        if (new[] { "goal", "target", "authorityRef", "restoration", "stopCondition" }.Any(key => !Value(packet, key, 2048))) return "InvalidWorkflow";
        if (criteria.Count is < 1 or > 32 || blockers.Count > 16 || workflow["targetSessionId"] is not null && !Value(workflow, "targetSessionId", 256)) return "InvalidWorkflow";
        if (workflow["priority"] is not null && (workflow["priority"] is not JsonValue priority || priority.GetValueKind() != System.Text.Json.JsonValueKind.Number || !long.TryParse(priority.ToJsonString(), out var number) || number is < -1000 or > 1000)) return "InvalidWorkflow";
        if (workflow["recheckEvidenceRef"] is not null && !Value(workflow, "recheckEvidenceRef", 2048)) return "InvalidWorkflow";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in criteria)
        {
            if (item is not JsonObject criterion || !Value(criterion, "id", 128) || !Value(criterion, "description", 2048) || !ids.Add(Text(criterion, "id")) || Text(criterion, "result") is not ("not_run" or "passed" or "failed")) return "InvalidWorkflow";
            if (criterion["evidenceRef"] is not null && !Value(criterion, "evidenceRef", 2048) || Text(criterion, "result") == "passed" && !Value(criterion, "evidenceRef", 2048)) return "InvalidWorkflow";
        }
        ids.Clear();
        foreach (var item in blockers)
        {
            if (item is not JsonObject blocker || !Value(blocker, "id", 128) || !ids.Add(Text(blocker, "id")) || !Value(blocker, "summary", 2048) || !Value(blocker, "recheckCondition", 2048) || Text(blocker, "kind") is not ("coordinator_hold" or "provider_rejection" or "tool_unavailable" or "resource_busy" or "missing_fixture" or "prerequisite" or "other")) return "InvalidWorkflow";
        }
        if (state == "blocked" && blockers.Count == 0 || state != "blocked" && blockers.Count != 0) return "InvalidWorkflow";
        if (state == "ready" && Text(prior ?? new(), "state") == "blocked" && !Value(workflow, "recheckEvidenceRef", 2048)) return "WorkflowRecheckRequired";
        if (state == "completed" && (unresolved || criteria.Any(x => Text(x!.AsObject(), "result") != "passed"))) return "WorkflowIncomplete";
        return null;
    }

    private static JsonObject WorkflowSummary(JsonObject workflow) => new()
    {
        ["state"] = workflow["state"]?.DeepClone(), ["targetSessionId"] = workflow["targetSessionId"]?.DeepClone(), ["priority"] = Number(workflow, "priority"),
        ["blockerCount"] = (workflow["blockers"] as JsonArray)?.Count ?? Number(workflow, "blockerCount"), ["reported"] = true
    };

    private static RelayReply NextWork(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var session = Text(args, "sessionId"); var current = Text(args, "taskId");
        var eligible = Object(state, "tasks").Select(x => x.Value!.AsObject()).Where(task => Text(task, "taskId") != current && task["workflow"] is JsonObject workflow && Text(workflow, "state") == "ready" && ValidateWorkflow(workflow, null, HasUnresolved(task)) is null && !HasUnresolved(task) && (Text(workflow, "targetSessionId").Length == 0 || Text(workflow, "targetSessionId") == session) && (Text(task, "owner").Length == 0 || Text(task, "owner") == session || Date(task, "leaseUntil") <= now)).OrderByDescending(task => Number(task["workflow"]!.AsObject(), "priority")).ThenBy(task => Text(task, "taskId"), StringComparer.Ordinal).FirstOrDefault();
        if (eligible is null) return Ok(new { ok = true, code = "NoEligibleWork" });
        var offer = new JsonObject { ["taskId"] = Text(eligible, "taskId"), ["expectedVersion"] = Number(eligible, "version"), ["snapshotRevision"] = Number(state, "taskRevision"), ["claimRequired"] = true, ["workflow"] = eligible["workflow"]!.DeepClone() };
        var reply = Ok(new { ok = true, code = "Offered", offer });
        if (!FitsWire(reply, 2048)) { offer.Remove("workflow"); offer["detailFetchRequired"] = true; reply = Ok(new { ok = true, code = "Offered", offer }); }
        return reply;
    }
}

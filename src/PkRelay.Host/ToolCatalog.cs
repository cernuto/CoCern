using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PkRelay.Core;

namespace PkRelay.Host;

public static class ToolCatalog
{
    public static readonly string[] Names = ["session_open", "session_bind", "worker_list", "get_status", "task_get", "task_update", "task_claim", "claim_acquire", "claim_renew", "claim_release", "action_begin", "action_report", "send_message", "check_inbox", "ack", "context_get", "context_put", "control_request"];
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static bool Contains(string name) => Names.Contains(name, StringComparer.Ordinal);
    public static IEnumerable<Tool> Tools(bool administrator) => Names.Where(n => administrator || n != "control_request").Select(n => new Tool
    {
        Name = n,
        Description = Description(n),
        InputSchema = Schema(n)
    });

    public static string Description(string name) => name switch
    {
        "session_open" => "Open a fresh private chat session; preserve the returned session handle privately.",
        "check_inbox" => "Read this session's durable inbox without acknowledging it.",
        "ack" => "Acknowledge explicitly handled message IDs in this session.",
        "control_request" => "Administrator coordination control; does not cancel or execute IDE operations.",
        _ => "Coordinate " + name.Replace('_', ' ') + ". Use the current session handle, stable mutation requestId, and applicable expected versions. Claims are advisory."
    };

    public static JsonElement Schema(string name)
    {
        var properties = new Dictionary<string, object>
        {
            ["repositoryId"] = new { type = "string", maxLength = 256 },
            ["boardId"] = new { type = "string", maxLength = 256 },
            ["sessionId"] = new { type = "string", maxLength = 256 },
            ["sessionToken"] = new { type = "string", maxLength = 256, description = "Private session secret; never put in shared notes." },
            ["requestId"] = new { type = "string", maxLength = 256 },
            ["expectedVersion"] = new { type = "integer", minimum = 0 },
            ["taskId"] = new { type = "string" },
            ["resourceId"] = new { type = "string" },
            ["resourceIds"] = new { type = "array", items = new { type = "string" }, maxItems = 16 },
            ["actionId"] = new { type = "string" },
            ["recipientSessionId"] = new { type = "string" },
            ["cursor"] = new { type = "string" },
            ["limit"] = new { type = "integer", minimum = 1, maximum = 50 }
        };
        void Text(string field, string description) => properties[field] = new { type = "string", description };
        void Integer(string field, string description) => properties[field] = new { type = "integer", minimum = 0, description };
        void Object(string field, string description) => properties[field] = new { type = "object", additionalProperties = true, description };
        void Array(string field, string description) => properties[field] = new { type = "array", items = new { type = "object", additionalProperties = true }, description };
        Text("machineId", "Reported machine or registered tool-host identity; not a permission grant.");
        switch (name)
        {
            case "session_open":
                Text("label", "Display label for this fresh chat session."); Object("binding", "Reported configured Roslyn/Visual Studio binding and source identity."); break;
            case "session_bind":
                Object("binding", "Reported configured Roslyn/Visual Studio binding; update under expectedVersion."); Text("availability", "Reported current worker availability."); Integer("freshnessSeconds", "Freshness duration, at most 600 seconds."); break;
            case "task_get":
                properties["section"] = new { type = "string", @enum = new[] { "facts", "messages", "reports", "actions" } }; Text("factKey", "Optional selected task fact key."); break;
            case "task_update":
                Object("patch", "Shared task facts; cannot overwrite coordination ownership/version internals.");
                properties["notes"] = new { description = "Bounded authored task notes, at most 16 KiB with task patch." };
                Text("message", "Task conversation message committed with this milestone."); Array("actionReports", "Reported action results committed atomically with this milestone."); break;
            case "task_claim":
            case "claim_acquire":
                Integer("leaseSeconds", "Advisory lease from 1 to 600 seconds; default 120."); break;
            case "claim_renew":
            case "claim_release":
                Integer("generation", "Required current resource grant generation when resourceId is supplied.");
                Integer("claimGeneration", "Required current task claim generation when taskId is supplied instead of resourceId.");
                Integer("leaseSeconds", "Renewal lease from 1 to 600 seconds; renew resource and task grants independently."); break;
            case "action_begin":
                Text("inputFingerprint", "Stable identity of this action's intended inputs."); Text("triggerId", "Optional delivery trigger consumed by this action."); Integer("attempt", "Explicit next attempt number after a resolved prior attempt.");
                properties["requiresRestoration"] = new { type = "boolean" }; properties["explicitRetry"] = new { type = "boolean" }; break;
            case "action_report":
                properties["outcome"] = new { type = "string", @enum = new[] { "started", "completed", "failed", "canceled", "incomplete", "unknown" } };
                properties["restoration"] = new { type = "string", @enum = new[] { "pending", "blocked", "verified", "accepted" } };
                properties["report"] = new { description = "Bounded reported evidence; not a claim that the relay performed or verified external execution." }; break;
            case "send_message": Text("text", "Message text, at most 8 KiB. Supply taskId for shared conversation, otherwise recipientSessionId for private delivery."); break;
            case "ack":
                Text("messageId", "One explicitly handled inbox message ID."); properties["messageIds"] = new { type = "array", items = new { type = "string" }, maxItems = 50 }; break;
            case "context_get": Text("contextId", "Selected shared context ID; omit for a bounded listing."); break;
            case "context_put":
                Text("contextId", "Stable authored shared context record ID."); properties["content"] = new { description = "Bounded explicitly shared context, at most 16 KiB." }; properties["provenance"] = new { description = "Source and authorship evidence for shared context." }; break;
            case "control_request":
                properties["command"] = new { type = "string", @enum = new[] { "pause", "resume", "stop", "cancel", "register_resource" } };
                properties["aliases"] = new { type = "array", items = new { type = "string" } }; Text("reason", "Coordination control explanation."); break;
        }
        var required = new List<string> { "repositoryId" };
        if (name is not ("worker_list" or "get_status" or "task_get" or "context_get" or "check_inbox")) required.Add("requestId");
        if (name is not ("session_open" or "worker_list" or "get_status" or "task_get" or "context_get" or "control_request")) required.AddRange(["sessionId", "sessionToken"]);
        if (name is "session_bind" or "task_update" or "task_claim" or "action_begin" or "action_report" or "context_put") required.Add("expectedVersion");
        if (name is "task_update" or "task_claim" or "action_begin" or "action_report") required.Add("taskId");
        if (name is "action_begin" or "action_report") required.Add("actionId");
        if (name == "session_bind") required.Add("binding");
        if (name == "task_claim") required.Add("resourceIds");
        if (name == "claim_acquire") required.Add("resourceId");
        if (name == "action_begin") required.Add("inputFingerprint");
        if (name == "action_report") required.Add("outcome");
        if (name == "send_message") required.Add("text");
        if (name == "context_put") required.AddRange(["contextId", "content"]);
        if (name == "control_request") required.Add("command");
        return JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = true });
    }

    public static int Status(RelayReply reply) => reply.Ok ? 200 : reply.Code switch
    {
        "RepositoryDenied" or "SessionDenied" or "Forbidden" or "AdministratorRequired" or "OwnerMismatch" => 403,
        "UnknownTool" or "NotFound" or "TaskNotFound" or "ContextNotFound" => 404,
        "PayloadTooLarge" => 413,
        "VersionConflict" or "RequestConflict" or "ClaimBusy" or "StaleClaim" or "Paused" or "Busy" or "ActionConflict" => 409,
        _ => 400
    };

    public static RelayReply Bound(RelayReply reply) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(reply, JsonOptions)) <= 32 * 1024 ? reply : new(false, "PayloadTooLarge", "{}", reply.Version);
}

// OpenAPI describes the shared argument envelope; each tool catalog entry supplies its required fields.
public sealed class CoordinationRequest
{
    public required string RepositoryId { get; init; }
    public string? SessionId { get; init; }
    public string? SessionToken { get; init; }
    public string? RequestId { get; init; }
    public long? ExpectedVersion { get; init; }
    public string? BoardId { get; init; }
    public string? TaskId { get; init; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? ToolArguments { get; init; }
}

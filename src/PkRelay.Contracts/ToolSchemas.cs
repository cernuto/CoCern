namespace PkRelay.Contracts;

/// <summary>Argument property schemas per tool. Serialized as JSON Schema by <see cref="RelayToolCatalog.InputSchema"/>.</summary>
internal static class ToolSchemas
{
    private static readonly string[] ForwardingFields = ["repositoryId", "sessionId", "sessionToken", "requestId"];

    /// <summary>
    /// The session envelope and required operation fields plus common chat coordination, delta paging and workflow arguments.
    /// Tool definitions sit in every agent's context on every turn, so a forwarding client advertises this subset.
    /// </summary>
    public static Dictionary<string, object> ForwardingProperties(RelayTool tool)
    {
        var all = Properties(tool.Name);
        string[] extra = tool.Name switch
        {
            RelayTools.SessionOpen => ["machineId", "label"],
            RelayTools.TaskGet => ["taskId", "section", "sinceRevision", "snapshotRevision", "cursor", "limit"],
            RelayTools.TaskUpdate => ["taskId", "expectedVersion", "workflow", "includeNextWork", "usage"],
            RelayTools.SendMessage => ["text", "recipientSessionId", "taskId", "replyTo", "ackMessageIds", "supersede", "wake"],
            RelayTools.CheckInbox => ["cursor", "limit"],
            RelayTools.Ack => ["messageId", "messageIds"],
            RelayTools.ClaimRenew => ["resourceId", "generation", "taskId", "claimGeneration", "leaseSeconds"],
            RelayTools.ClaimRelease => ["resourceId", "generation", "taskId", "claimGeneration", "expectedVersion"],
            _ => []
        };
        var properties = ForwardingFields.Concat(tool.RequiredFields).Concat(extra).Distinct(StringComparer.Ordinal).ToDictionary(field => field, field => all[field]);
        // Only the milestone call advertises a compact usage entry: adding it to action_report too would exceed the 20,000-character
        // forwarding budget (20,034). The hub still accepts usage on action_report, and InputSchema lists it there in full.
        if (properties.ContainsKey("usage")) properties["usage"] = new { type = "object", description = "Optional {inputTokens,outputTokens}." };
        // The same budget leaves room only for the names of workflow dependsOn and packet.resources; InputSchema describes them in full.
        if (properties.ContainsKey("workflow")) properties["workflow"] = WorkflowSchema(compact: true);
        return properties;
    }

    public static Dictionary<string, object> Properties(string tool)
    {
        var properties = new Dictionary<string, object>
        {
            ["repositoryId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = "Authorized repository identity." },
            ["boardId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength },
            ["sessionId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = "This chat's session ID." },
            ["sessionToken"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = "Private session secret; never put in shared notes." },
            ["requestId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = "Stable mutation request identity for reconciliation/retry." },
            ["expectedVersion"] = new { type = "integer", minimum = 0 },
            ["taskId"] = new { type = "string" },
            ["resourceId"] = new { type = "string" },
            ["resourceIds"] = new { type = "array", items = new { type = "string" }, maxItems = RelayLimits.MaxClaimResources },
            ["actionId"] = new { type = "string" },
            ["recipientSessionId"] = new { type = "string" },
            ["cursor"] = new { type = "integer", minimum = 0, description = "Numeric continuation returned by a bounded list." },
            ["limit"] = new { type = "integer", minimum = 1, maximum = RelayLimits.MaxPageSize }
        };
        void Text(string field, string description) => properties[field] = new { type = "string", description };
        void Integer(string field, string description) => properties[field] = new { type = "integer", minimum = 0, description };
        void Object(string field, string description) => properties[field] = new { type = "object", additionalProperties = true, description };
        void Array(string field, string description) => properties[field] = new { type = "array", items = new { type = "object", additionalProperties = true }, description };
        Text("machineId", "Reported machine or registered tool-host identity; not a permission grant.");
        switch (tool)
        {
            case RelayTools.SessionOpen:
                Text("label", "Display label for this fresh chat session."); Object("binding", "Reported configured Roslyn/Visual Studio binding and source identity."); break;
            case RelayTools.SessionBind:
                Object("binding", "Reported configured Roslyn/Visual Studio binding; update under expectedVersion."); Text("availability", "Reported current worker availability."); Integer("freshnessSeconds", "Freshness duration, at most 600 seconds."); break;
            case RelayTools.TaskGet:
                properties["taskId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = "Omit for a bounded board list or delta." };
                properties["section"] = new { type = "string", @enum = new[] { "facts", "messages", "reports", "actions", "workflow" } };
                Integer("sinceRevision", "Board-list delta checkpoint; omit taskId. Save the returned snapshotRevision only after all pages.");
                Integer("snapshotRevision", "Pinned revision returned by the first delta page. A concurrent task change returns DeltaRestartRequired.");
                properties["cursor"] = new { type = "integer", minimum = 0, description = "Numeric continuation returned by a bounded list; retain the same delta checkpoint and snapshot revision." };
                Text("factKey", "Optional selected task fact key."); break;
            case RelayTools.TaskUpdate:
                Object("patch", "Shared task facts; cannot overwrite coordination ownership/version internals.");
                properties["notes"] = new { description = "Bounded authored task notes, at most 16 KiB with task patch." };
                Text("message", "Task conversation message committed with this milestone."); Array("actionReports", "Reported action results committed atomically with this milestone.");
                properties["workflow"] = WorkflowSchema();
                properties["includeNextWork"] = new { type = "boolean", description = "After committing the milestone, include an independent advisory nextWork result. An offer acquires no claim and authorizes no execution." };
                properties["usage"] = UsageSchema();
                break;
            case RelayTools.TaskClaim:
            case RelayTools.ClaimAcquire:
                Integer("leaseSeconds", "Advisory lease from 1 to 1800 seconds; default 120."); break;
            case RelayTools.ClaimRenew:
            case RelayTools.ClaimRelease:
                Integer("generation", "Required current resource grant generation when resourceId is supplied.");
                Integer("claimGeneration", "Required current task claim generation when taskId is supplied instead of resourceId.");
                if (tool == RelayTools.ClaimRelease) Integer("expectedVersion", "Required current task version when releasing taskId; omit for a resource release.");
                Integer("leaseSeconds", "Renewal lease from 1 to 1800 seconds; renew resource and task grants independently."); break;
            case RelayTools.ActionBegin:
                Text("inputFingerprint", "Stable identity of this action's intended inputs."); Text("triggerId", "Optional delivery trigger consumed by this action."); Integer("attempt", "Explicit next attempt number after a resolved prior attempt.");
                properties["requiresRestoration"] = new { type = "boolean" }; properties["explicitRetry"] = new { type = "boolean" }; break;
            case RelayTools.ActionReport:
                properties["outcome"] = new { type = "string", @enum = new[] { "started", "completed", "failed", "canceled", "incomplete", "unknown" } };
                properties["restoration"] = new { type = "string", @enum = new[] { "pending", "blocked", "verified", "accepted" } };
                properties["report"] = new { description = "Bounded reported evidence; not a claim that the relay performed or verified external execution." };
                properties["usage"] = UsageSchema(); break;
            case RelayTools.SendMessage:
                Text("text", "Message text, at most 8 KiB. Supply taskId for shared conversation, otherwise recipientSessionId for private delivery.");
                Text("replyTo", "Optional ID of the message this replies to.");
                properties["ackMessageIds"] = new { type = "array", items = new { type = "string" }, maxItems = RelayLimits.MaxAcknowledgedMessages, description = "Inbox message IDs this reply handles; acknowledged in the same call." };
                properties["supersede"] = new { type = "boolean", description = "For repeated status/heartbeat messages: replaces your earlier unread superseding message to this recipient." };
                properties["wake"] = new { type = "boolean", description = "false for status/FYI needing no prompt action: stored, read at the recipient's next wake, never wakes it." }; break;
            case RelayTools.Ack:
                Text("messageId", "One explicitly handled inbox message ID."); properties["messageIds"] = new { type = "array", items = new { type = "string" }, maxItems = RelayLimits.MaxAcknowledgedMessages }; break;
            case RelayTools.ContextGet: Text("contextId", "Selected shared context ID; omit for a bounded listing."); break;
            case RelayTools.ContextPut:
                Text("contextId", "Stable authored shared context record ID."); properties["content"] = new { description = "Bounded explicitly shared context, at most 16 KiB." }; properties["provenance"] = new { description = "Source and authorship evidence for shared context." }; break;
            case RelayTools.ControlRequest:
                properties["command"] = new { type = "string", @enum = new[] { "pause", "resume", "stop", "cancel", "register_resource", "revoke_machine" } };
                properties["aliases"] = new { type = "array", items = new { type = "string" } }; Text("reason", "Coordination control explanation."); Text("machineId", "Machine to revoke; required by revoke_machine."); break;
        }
        return properties;
    }

    private static object UsageSchema()
    {
        static object Tokens() => new { type = "integer", minimum = 0, maximum = RelayLimits.MaxReportedTokens };
        return new { type = "object", properties = new { inputTokens = Tokens(), outputTokens = Tokens() }, additionalProperties = false, description = "Optional tokens you used since your last report; recorded for measurement only." };
    }

    private static object WorkflowSchema(bool compact = false)
    {
        static object Text(int maximum, string description) => new { type = "string", minLength = 1, maxLength = maximum, description };
        static object OptionalText(int maximum, string description) => new { type = new[] { "string", "null" }, minLength = 1, maxLength = maximum, description };
        object Identifiers(int maximum, string description) => compact ? new { } : (object)new { type = new[] { "array", "null" }, items = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength }, maxItems = maximum, uniqueItems = true, description };
        return new
        {
            type = "object",
            description = "Optional full structured workflow, at most 12288 UTF-8 serialized bytes. Reported evidence only; completion requires every criterion passed with evidence and no unresolved action/restoration. A blocked-to-ready update requires recheckEvidenceRef.",
            properties = new
            {
                state = new { type = "string", @enum = new[] { "ready", "blocked", "completed" } },
                packet = new
                {
                    type = "object",
                    properties = new { goal = Text(2048, "Useful completion goal."), target = Text(2048, "Source, workspace or fixture identity."), authorityRef = Text(2048, "Reference to existing human authority; grants no permission."), restoration = Text(2048, "Restoration expectation; actual action restoration remains in action reports."), stopCondition = Text(2048, "Condition requiring the participant to stop."), resources = Identifiers(RelayLimits.MaxClaimResources, "Resource IDs this task expects to claim.") },
                    required = new[] { "goal", "target", "authorityRef", "restoration", "stopCondition" }
                },
                criteria = new
                {
                    type = "array", minItems = 1, maxItems = 32,
                    items = new { type = "object", properties = new { id = Text(128, "Unique criterion ID."), description = Text(2048, "Acceptance criterion."), result = new { type = "string", @enum = new[] { "not_run", "passed", "failed" } }, evidenceRef = OptionalText(2048, "Nonempty evidence reference required for passed results.") }, required = new[] { "id", "description", "result" } }
                },
                blockers = new
                {
                    type = "array", maxItems = 16,
                    items = new { type = "object", properties = new { id = Text(128, "Unique blocker ID."), kind = new { type = "string", @enum = new[] { "coordinator_hold", "provider_rejection", "tool_unavailable", "resource_busy", "missing_fixture", "prerequisite", "other" } }, summary = Text(2048, "Current blocking condition."), recheckCondition = Text(2048, "Evidence needed before clearing the blocker.") }, required = new[] { "id", "kind", "summary", "recheckCondition" } }
                },
                targetSessionId = OptionalText(256, "Optional intended worker session ID; never its private token."),
                priority = new { type = new[] { "integer", "null" }, minimum = -1000, maximum = 1000, description = "Higher is offered first; ties prefer free resources, then task ID." },
                recheckEvidenceRef = OptionalText(2048, "Nonempty reference required when explicitly changing blocked to ready. Existing human authority may resolve a coordinator hold; provider approval remains separate."),
                dependsOn = Identifiers(RelayLimits.MaxDependencies, "Task IDs on this board that must be completed before this task is offered.")
            },
            required = new[] { "state", "packet", "criteria", "blockers" }
        };
    }
}

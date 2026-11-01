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
            RelayTools.TaskUpdate => ["taskId", "expectedVersion", "workflow", "includeNextWork", "usage", "attention"],
            RelayTools.TaskClaim or RelayTools.ActionReport => ["attention"],
            RelayTools.SendMessage => ["text", "recipientSessionId", "taskId", "replyTo", "ackMessageIds", "supersede", "wake"],
            RelayTools.CheckInbox => ["cursor", "limit"],
            RelayTools.Ack => ["messageId", "messageIds"],
            RelayTools.ClaimRenew => ["resourceId", "generation", "taskId", "claimGeneration", "leaseSeconds"],
            RelayTools.ClaimRelease => ["resourceId", "generation", "taskId", "claimGeneration", "expectedVersion"],
            RelayTools.StandingPropose => ["line", "grant"],
            RelayTools.StandingPut => ["lines", "grants", "remove", "approve", "settings"],
            RelayTools.Search => ["kinds"],
            RelayTools.AttentionRespond => ["until"],
            RelayTools.AnalyticsQuery => ["params", "sql"],
            RelayTools.OrderPut => ["expiresAt"],
            _ => []
        };
        var properties = ForwardingFields.Concat(tool.RequiredFields).Concat(extra).Distinct(StringComparer.Ordinal).ToDictionary(field => field, field => all[field]);
        // The envelope repeats on every tool, so it is advertised without its length bounds; the hub enforces them.
        foreach (var field in ForwardingFields) properties[field] = new { type = "string", description = EnvelopeDescription(field) };
        // Standing items are advertised by shape only; InputSchema gives their limits.
        if (tool.Name is RelayTools.StandingPropose or RelayTools.StandingPut)
            foreach (var field in properties.Keys.ToArray())
                if (CompactStanding(field) is { } compact) properties[field] = compact;
        // Only the milestone call advertises a compact usage entry: adding it to action_report too would exceed the 20,000-character
        // forwarding budget (20,034). The hub still accepts usage on action_report, and InputSchema lists it there in full.
        if (properties.ContainsKey("usage")) properties["usage"] = new { type = "object", description = "Optional {inputTokens,outputTokens}." };
        // Search is advertised by shape, without status or limit (default 3 hits); InputSchema gives them and the length and kind limits.
        if (tool.Name == RelayTools.Search)
        {
            // A read is never deduplicated, so its forwarding copy leaves out requestId.
            properties.Remove("requestId");
            properties["query"] = new { type = "string" };
            properties["kinds"] = new { type = "array", items = new { type = "string" } };
        }
        // Attention answers are advertised by type; InputSchema gives the ID bound and the until format.
        if (tool.Name == RelayTools.AttentionRespond)
        {
            properties["itemId"] = new { type = "string" };
            properties["action"] = new { type = "string", @enum = AttentionActions.All };
            properties["until"] = new { type = "string", description = "Optional ISO time for later." };
        }
        // Analytics is advertised by shape with the query names; InputSchema gives the limits. It is a read by the administrator token,
        // so its forwarding copy leaves out requestId and the session handle.
        if (tool.Name == RelayTools.AnalyticsQuery)
        {
            properties.Remove("requestId"); properties.Remove("sessionId"); properties.Remove("sessionToken");
            properties["name"] = new { type = "string", @enum = AnalyticsQueryNames.All.Append(AnalyticsQueryNames.AdHocSql).ToArray() };
            properties["params"] = new { type = "object", description = "Optional {days}." };
            properties["sql"] = new { type = "string", description = "SELECT, with name sql." };
        }
        // Orders are advertised by shape with the trigger and action names; InputSchema gives the limits. The administrator token is the
        // authority, so the forwarding copy leaves out the session handle.
        if (tool.Name is RelayTools.OrderPut or RelayTools.OrderRemove)
        {
            properties.Remove("sessionId"); properties.Remove("sessionToken");
            properties["orderId"] = new { type = "string" };
            if (tool.Name == RelayTools.OrderPut)
            {
                properties["trigger"] = new { type = "object", description = "{on:" + string.Join("|", OrderTriggers.All) + ",taskId|resourceId}" };
                properties["actions"] = new { type = "array", description = "[{do:" + string.Join("|", OrderActions.All) + ",taskId?,sessionId?,note?}]" };
                properties["expiresAt"] = new { type = "string" };
            }
        }
        // Each boundary tool advertises the attention opt-out with a short description; InputSchema gives the full one.
        if (properties.ContainsKey("attention")) properties["attention"] = new { type = "boolean", description = "false: no attention offer." };
        // The same budget leaves room only for typed names of workflow dependsOn and packet.resources; InputSchema describes them in full.
        if (properties.ContainsKey("workflow")) properties["workflow"] = WorkflowSchema(compact: true);
        return properties;
    }

    public static Dictionary<string, object> Properties(string tool)
    {
        var properties = new Dictionary<string, object>
        {
            ["repositoryId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = EnvelopeDescription("repositoryId") },
            ["boardId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength },
            ["sessionId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = EnvelopeDescription("sessionId") },
            ["sessionToken"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = EnvelopeDescription("sessionToken") },
            ["requestId"] = new { type = "string", maxLength = RelayLimits.MaxIdentifierLength, description = EnvelopeDescription("requestId") },
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
                properties["section"] = new { type = "string", @enum = new[] { "facts", "messages", "reports", "actions", "workflow", "standing" }, description = "standing needs no taskId." };
                Integer("sinceRevision", "Board delta checkpoint; omit taskId. Save snapshotRevision only after all pages.");
                Integer("snapshotRevision", "Pinned revision from the first delta page; a concurrent change returns DeltaRestartRequired.");
                properties["cursor"] = new { type = "integer", minimum = 0, description = "Continuation from a bounded list; keep the same sinceRevision and snapshotRevision." };
                Text("factKey", "Optional selected task fact key."); break;
            case RelayTools.TaskUpdate:
                Object("patch", "Shared task facts; cannot overwrite coordination ownership/version internals.");
                properties["notes"] = new { description = "Bounded authored task notes, at most 16 KiB with task patch." };
                Text("message", "Task conversation message committed with this milestone."); Array("actionReports", "Reported action results committed atomically with this milestone.");
                properties["workflow"] = WorkflowSchema();
                properties["includeNextWork"] = new { type = "boolean", description = "Also return an advisory nextWork offer; it acquires no claim and authorizes no execution." };
                properties["usage"] = UsageSchema();
                properties["attention"] = AttentionOptOut();
                break;
            case RelayTools.TaskClaim:
            case RelayTools.ClaimAcquire:
                Integer("leaseSeconds", "Advisory lease from 1 to 1800 seconds; default 120.");
                if (tool == RelayTools.TaskClaim) properties["attention"] = AttentionOptOut();
                break;
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
                properties["usage"] = UsageSchema(); properties["attention"] = AttentionOptOut(); break;
            case RelayTools.SendMessage:
                Text("text", "Message text, at most 8 KiB. Supply taskId for shared conversation, otherwise recipientSessionId for private delivery.");
                Text("replyTo", "Optional ID of the message this replies to.");
                properties["ackMessageIds"] = new { type = "array", items = new { type = "string" }, maxItems = RelayLimits.MaxAcknowledgedMessages, description = "Inbox message IDs this reply handles; acknowledged in the same call." };
                properties["supersede"] = new { type = "boolean", description = "For repeated status or heartbeats: replaces your earlier unread superseding message to this recipient." };
                properties["wake"] = new { type = "boolean", description = "false for status/FYI needing no action now: stored and read at the next wake, never wakes the recipient." }; break;
            case RelayTools.Ack:
                Text("messageId", "One explicitly handled inbox message ID."); properties["messageIds"] = new { type = "array", items = new { type = "string" }, maxItems = RelayLimits.MaxAcknowledgedMessages }; break;
            case RelayTools.ContextGet: Text("contextId", "Selected shared context ID; omit for a bounded listing."); break;
            case RelayTools.ContextPut:
                Text("contextId", "Stable authored shared context record ID."); properties["content"] = new { description = "Bounded explicitly shared context, at most 16 KiB." }; properties["provenance"] = new { description = "Source and authorship evidence for shared context." }; break;
            case RelayTools.StandingPropose:
                properties["line"] = LineSchema(idRequired: false);
                properties["grant"] = GrantSchema(idRequired: false); break;
            case RelayTools.StandingPut:
                properties["expectedRevision"] = new { type = "integer", minimum = 0, description = "Current standing revision; 0 before the first write." };
                properties["lines"] = new { type = "array", items = LineSchema(idRequired: true), maxItems = RelayLimits.MaxStandingLines, description = "Lines to add, or to replace by ID." };
                properties["grants"] = new { type = "array", items = GrantSchema(idRequired: true), maxItems = RelayLimits.MaxStandingGrants, description = "Grants to add, or to replace by ID." };
                properties["remove"] = StandingIds("Line, grant or proposal IDs to remove; removing a proposal rejects it.");
                properties["approve"] = StandingIds("Pending proposal IDs to approve.");
                properties["settings"] = new { type = "object", properties = new { requireResources = new { type = "boolean", description = "Refuse ready without packet.resources." } }, additionalProperties = false, description = "Board settings." }; break;
            case RelayTools.Search:
                properties["query"] = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxSearchQueryLength, description = "Words to find; each space-separated chunk is one phrase. Search syntax is read as plain words." };
                properties["kinds"] = new { type = "array", items = new { type = "string", @enum = SearchKinds.All }, maxItems = SearchKinds.All.Count, uniqueItems = true, description = "Kinds to search; default all." };
                properties["status"] = new { type = "string", maxLength = RelayLimits.MaxSearchStatusLength, description = "Only hits with this status, such as ready, blocked or completed." };
                properties["limit"] = new { type = "integer", minimum = 1, maximum = RelayLimits.MaxSearchResults, description = $"Most hits; default {RelayLimits.DefaultSearchResults}." }; break;
            case RelayTools.AttentionRespond:
                properties["itemId"] = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength, description = "Attention item ID from an offer." };
                properties["action"] = new { type = "string", @enum = AttentionActions.All, description = "later hides the item for a few replies, or until a time; accept and dismiss close it." };
                properties["until"] = new { type = "string", format = "date-time", description = "Optional future time when a later item returns." }; break;
            case RelayTools.AnalyticsQuery:
                properties["name"] = new { type = "string", @enum = AnalyticsQueryNames.All.Append(AnalyticsQueryNames.AdHocSql).ToArray(), description = "A named query, or sql to run the sql field." };
                properties["params"] = new { type = "object", properties = new { days = new { type = "integer", minimum = 1, maximum = RelayLimits.MaxAnalyticsPeriodDays, description = $"Days per compared period; default {RelayLimits.DefaultAnalyticsPeriodDays}." } }, additionalProperties = false, description = "Named query parameters." };
                properties["sql"] = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxAnalyticsSqlLength, description = "One SELECT over the analytics views; only with name sql, and only when the hub allows ad hoc SQL." }; break;
            case RelayTools.OrderPut:
                properties["orderId"] = OrderId();
                properties["trigger"] = new
                {
                    type = "object",
                    properties = new
                    {
                        on = new { type = "string", @enum = OrderTriggers.All, description = "workflow_completed and blocker_cleared name a taskId; claim_released names a resourceId." },
                        taskId = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength, description = "A task on this board." },
                        resourceId = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength, description = "A registered resource of this repository." }
                    },
                    required = new[] { "on" }, additionalProperties = false, description = "When the order runs; one order per trigger."
                };
                properties["actions"] = new
                {
                    type = "array", minItems = 1, maxItems = RelayLimits.MaxOrderActions,
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            @do = new { type = "string", @enum = OrderActions.All, description = "release_claims releases a task's claims; offer offers taskId to sessionId as next work; notify sends sessionId an attention item." },
                            taskId = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength, description = "A task on this board; release_claims defaults to the trigger's task." },
                            sessionId = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength, description = "The session to offer to or notify." },
                            note = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxOrderNoteLength, description = "Optional text for notify." }
                        },
                        required = new[] { "do" }, additionalProperties = false
                    },
                    description = "Coordination actions; never acceptance, grants or blocker changes."
                };
                properties["expiresAt"] = new { type = "string", format = "date-time", description = $"Optional future expiry, at most {RelayLimits.MaxOrderExpiryDays} days ahead; the hub's default otherwise." }; break;
            case RelayTools.OrderRemove:
                properties["orderId"] = OrderId(); break;
            case RelayTools.ControlRequest:
                properties["command"] = new { type = "string", @enum = new[] { "pause", "resume", "stop", "cancel", "register_resource", "revoke_machine" } };
                properties["aliases"] = new { type = "array", items = new { type = "string" } }; Text("reason", "Coordination control explanation."); Text("machineId", "Machine to revoke; required by revoke_machine."); break;
        }
        return properties;
    }

    private static string EnvelopeDescription(string field) => field switch
    {
        "repositoryId" => "Authorized repository ID.",
        "sessionId" => "Your session ID.",
        "sessionToken" => "Private session secret; never share it.",
        _ => "Stable request ID; reuse on retry."
    };

    private static object? CompactStanding(string field) => field switch
    {
        "line" => new { type = "object", description = "{id?,text}" },
        "grant" => new { type = "object", description = "{id?,scope,targets,expiresAt}" },
        "lines" => new { type = "array", description = "[{id,text}]" },
        "grants" => new { type = "array", description = "[{id,scope,targets,expiresAt}]" },
        "remove" or "approve" => new { type = "array", items = new { type = "string" } },
        "settings" => new { type = "object", description = "{requireResources}" },
        "expectedRevision" => new { type = "integer" },
        _ => null
    };

    private static object StandingIds(string description) =>
        new { type = "array", items = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxStandingIdLength }, maxItems = RelayLimits.MaxStandingLines + RelayLimits.MaxStandingGrants + RelayLimits.MaxStandingProposals, description };

    private static object LineSchema(bool idRequired) => new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxStandingIdLength, description = idRequired ? "Line ID." : "Optional line ID; an existing ID proposes an edit." },
            text = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxStandingLineLength, description = "A settled convention, such as a rule every chat on this board follows." }
        },
        required = idRequired ? new[] { "id", "text" } : ["text"],
        description = "A standing line: a convention, never a permission."
    };

    private static object GrantSchema(bool idRequired) => new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxStandingIdLength, description = idRequired ? "Grant ID; name it in authorityRef as grant:<id>." : "Optional grant ID." },
            scope = new { type = "string", @enum = new[] { "repository", "resource", "action" } },
            targets = new { type = "array", items = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength }, minItems = 1, maxItems = RelayLimits.MaxGrantTargets, description = "Repository, resource IDs or action classes covered; a repository grant defaults to this repository." },
            expiresAt = new { type = "string", format = "date-time", description = "Required future expiry." }
        },
        required = new[] { "scope", "expiresAt" }.Concat(idRequired ? ["id"] : Array.Empty<string>()).ToArray(),
        description = "Typed authority with a scope and an expiry."
    };

    private static object OrderId() => new { type = "string", minLength = 1, maxLength = RelayLimits.MaxStandingIdLength, description = "Standing order ID on this board." };

    private static object AttentionOptOut() => new { type = "boolean", description = "false skips the attention offer on this reply, for work that must not be interrupted." };

    private static object UsageSchema()
    {
        static object Tokens() => new { type = "integer", minimum = 0, maximum = RelayLimits.MaxReportedTokens };
        return new { type = "object", properties = new { inputTokens = Tokens(), outputTokens = Tokens() }, additionalProperties = false, description = "Optional tokens you used since your last report; recorded for measurement only." };
    }

    private static object WorkflowSchema(bool compact = false)
    {
        static object Text(int maximum, string description) => new { type = "string", minLength = 1, maxLength = maximum, description };
        static object OptionalText(int maximum, string description) => new { type = new[] { "string", "null" }, minLength = 1, maxLength = maximum, description };
        object Identifiers(int maximum, string description) => compact ? new { type = "array", items = new { type = "string" } } : (object)new { type = new[] { "array", "null" }, items = new { type = "string", minLength = 1, maxLength = RelayLimits.MaxIdentifierLength }, maxItems = maximum, uniqueItems = true, description };
        return new
        {
            type = "object",
            description = "Optional structured workflow, at most 12288 UTF-8 bytes. Reported evidence only; completed needs every criterion passed with evidence and no unresolved action or restoration. Blocked to ready needs recheckEvidenceRef.",
            properties = new
            {
                state = new { type = "string", @enum = new[] { "ready", "blocked", "completed" } },
                packet = new
                {
                    type = "object",
                    properties = new { goal = Text(2048, "Useful completion goal."), target = Text(2048, "Source, workspace or fixture identity."), authorityRef = Text(2048, "Reference to existing human authority; grants no permission."), restoration = Text(2048, "Expected restoration; actual restoration stays in action reports."), stopCondition = Text(2048, "Condition requiring the participant to stop."), resources = Identifiers(RelayLimits.MaxClaimResources, "Resource IDs this task expects to claim.") },
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
                priority = new { type = new[] { "integer", "null" }, minimum = -1000, maximum = 1000, description = "Higher is offered first." },
                recheckEvidenceRef = OptionalText(2048, "Nonempty reference required when explicitly changing blocked to ready."),
                dependsOn = Identifiers(RelayLimits.MaxDependencies, "Task IDs on this board that must be completed before this task is offered.")
            },
            required = new[] { "state", "packet", "criteria", "blockers" }
        };
    }
}

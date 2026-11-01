using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>
/// One repository board: its tasks (facts, conversation, reports, actions, workflow), the task claim, and shared
/// context notes. Every committed task change advances the board's taskRevision so readers can fetch deltas.
/// </summary>
internal static class TaskBoardAggregate
{
    private const int MaxConversation = 256;
    private const int MaxSharedBytes = 16_384;
    private const int MaxContextEntryBytes = 24_576;
    private static readonly string[] ListSections = ["messages", "reports", "attemptHistory"];

    public static RelayReply Handle(AggregateContext context)
    {
        var state = context.State;
        var tasks = ObjectAt(state, "tasks");
        // Migrate only current markers. No copied history or changes to optimistic task versions.
        foreach (var task in tasks.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value!.AsObject()))
            if (!task.ContainsKey("changeRevision")) MarkChanged(state, task);
        var taskId = Text(context.Args, "taskId");
        var mutation = CoordinationStateMachine.IsMutation(context.Operation) && taskId.Length > 0;
        var before = mutation ? tasks[taskId]?.ToJsonString() : null;
        var result = Dispatch(context, tasks, taskId);
        if (!result.Ok) return result;
        if (mutation && tasks[taskId] is JsonObject changedTask && before != changedTask.ToJsonString()) MarkChanged(state, changedTask);
        var data = JsonNode.Parse(result.DataJson)!.AsObject();
        if (Text(data, "taskId") is { Length: > 0 } id && tasks[id] is JsonObject changed) data["changeRevision"] = Number(changed, "changeRevision");
        return result with { DataJson = data.ToJsonString() };
    }

    private static void MarkChanged(JsonObject state, JsonObject task)
    {
        state["taskRevision"] = Number(state, "taskRevision") + 1;
        task["changeRevision"] = Number(state, "taskRevision");
    }

    private static RelayReply Dispatch(AggregateContext context, JsonObject tasks, string taskId)
    {
        switch (context.Operation)
        {
            case Operations.NextWork: return TaskWorkflow.NextWork(context.State, context.Args, context.Now);
            case Operations.Standing: return StandingPreferences.Read(context.State, context.Now);
            case RelayTools.StandingPut: return StandingPreferences.Put(context);
            case RelayTools.StandingPropose: return StandingPreferences.Propose(context);
            case RelayTools.TaskGet when taskId.Length == 0 && Text(context.Args, "section") == "standing": return StandingPreferences.View(context.State, context.Now);
            case RelayTools.TaskGet when taskId.Length == 0: return context.Args.ContainsKey("sinceRevision") ? DeltaPage(context.State, context.Args) : ListTasks(context.State, context.Args);
            case RelayTools.ContextGet: return GetContext(context);
            case RelayTools.ContextPut: return PutContext(context);
        }
        if (taskId.Length == 0) return Error(InvalidArguments, new { required = "taskId" });
        if (tasks[taskId] is not JsonObject task)
        {
            // Only an explicit create (task_update at expected version 0) brings a task into existence.
            if (context.Operation != RelayTools.TaskUpdate || !ExpectedVersionMatches(context.Args, 0)) return Error(NotFound);
            if (tasks.Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
            tasks[taskId] = task = new() { ["taskId"] = taskId, ["version"] = 0, ["actions"] = new JsonObject(), ["messages"] = new JsonArray(), ["reports"] = new JsonArray(), ["facts"] = new JsonObject() };
        }
        return context.Operation switch
        {
            RelayTools.TaskGet => ReadTask(context.Args, task),
            RelayTools.ClaimRenew or RelayTools.ClaimRelease => RenewOrRelease(context, task),
            Operations.TaskUnclaim => Unclaim(context, task),
            RelayTools.TaskClaim => Claim(context, task),
            RelayTools.ActionBegin => TaskActions.Begin(context, task),
            RelayTools.ActionReport or RelayTools.TaskUpdate => Update(context, task),
            RelayTools.SendMessage => SendTaskMessage(context, task),
            _ => Error(UnknownTool)
        };
    }

    private static RelayReply ListTasks(JsonObject state, JsonObject args)
    {
        var page = Page(ObjectAt(state, "tasks").OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode)Summary(x.Value!.AsObject())), args);
        var data = JsonNode.Parse(page.DataJson)!.AsObject();
        data["boardRevision"] = Number(state, "taskRevision");
        return Ok(data);
    }

    /// <summary>Tasks changed since a checkpoint, paged against a pinned snapshot revision. A concurrent change restarts the read.</summary>
    private static RelayReply DeltaPage(JsonObject state, JsonObject args)
    {
        static bool Integer(JsonObject obj, string key) => obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), out _);
        if (!Integer(args, "sinceRevision") || args.ContainsKey("snapshotRevision") && !Integer(args, "snapshotRevision") || args.ContainsKey("cursor") && !Integer(args, "cursor")) return Error(InvalidArguments);
        var since = Number(args, "sinceRevision");
        var current = Number(state, "taskRevision");
        var offset = Number(args, "cursor");
        if (since < 0 || since > current || offset < 0 || offset > RelayLimits.MaxRecords || offset > 0 && !args.ContainsKey("snapshotRevision")) return Error(InvalidArguments);
        if (args.ContainsKey("snapshotRevision") && Number(args, "snapshotRevision") != current) return Error(DeltaRestartRequired, new { restartRequired = true, currentRevision = current });
        var all = ObjectAt(state, "tasks").Where(x => Number(x.Value!.AsObject(), "changeRevision") > since).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode)Summary(x.Value!.AsObject())).ToArray();
        var limit = (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultPageSize), 1, RelayLimits.MaxPageSize);
        var items = all.Skip((int)offset).Take(limit).Select(x => x.DeepClone()).ToList();
        RelayReply Reply() => Ok(new { items, snapshotRevision = current, nextCursor = offset + items.Count < all.Length ? (long?)(offset + items.Count) : null, truncated = offset + items.Count < all.Length, total = all.Length });
        while (items.Count > 1 && !FitsWire(Reply(), RelayLimits.AggregateReplyBytes)) items.RemoveAt(items.Count - 1);
        return Reply();
    }

    private static RelayReply GetContext(AggregateContext context)
    {
        var notes = ObjectAt(context.State, "context");
        var id = Text(context.Args, "contextId");
        if (id.Length == 0) return Page(notes.Select(x => x.Value!), context.Args);
        return notes[id] is JsonNode note ? Ok(note, Number(note.AsObject(), "version")) : Error(NotFound);
    }

    private static RelayReply PutContext(AggregateContext context)
    {
        var args = context.Args;
        var notes = ObjectAt(context.State, "context");
        var id = Required(args, "contextId");
        var version = notes[id] is JsonObject previous ? Number(previous, "version") : 0;
        if (ByteSize(args["content"]) > MaxSharedBytes) return Error(PayloadTooLarge);
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        if (notes.Count >= RelayLimits.MaxRecords && !notes.ContainsKey(id)) return Error(CapacityExceeded);
        var entry = new JsonObject
        {
            ["contextId"] = id, ["version"] = version + 1, ["content"] = args["content"]?.DeepClone(), ["provenance"] = args["provenance"]?.DeepClone(),
            ["author"] = context.Caller.PrincipalId, ["updatedAt"] = context.Now.ToString("O")
        };
        if (ByteSize(entry) > MaxContextEntryBytes) return Error(PayloadTooLarge);
        notes[id] = entry;
        return Ok(entry, version + 1);
    }

    private static RelayReply ReadTask(JsonObject args, JsonObject task)
    {
        var version = Number(task, "version");
        var section = Text(args, "section");
        if (section == "workflow") return task["workflow"] is JsonObject workflow ? Ok(workflow, version) : Error(NotFound);
        if (ListSections.Contains(section)) return Page(ArrayAt(task, section).Select(x => x!), args) with { Version = version };
        if (section == "actions") return Page(ObjectAt(task, "actions").Select(x => x.Value!), args) with { Version = version };
        if (section == "facts")
        {
            var facts = ObjectAt(task, "facts");
            var factKey = Text(args, "factKey");
            if (factKey.Length > 0) return facts.ContainsKey(factKey) ? Ok(new { key = factKey, value = facts[factKey] }, version) : Error(NotFound);
            return Page(facts.Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["value"] = x.Value?.DeepClone() }), args) with { Version = version };
        }
        var recent = Recent(task);
        if (ByteSize(recent) > RelayLimits.MaxPayloadBytes) return Ok(Summary(task), version);
        return Ok(recent, version);
    }

    /// <summary>
    /// The task with only its latest list entries. A chat re-reading a task otherwise takes in its whole history every time, and that read
    /// grows with every message; each trimmed list gets a <c>&lt;section&gt;Total</c>, and its older entries stay in the paged section.
    /// </summary>
    private static JsonObject Recent(JsonObject task)
    {
        if (ListSections.All(section => ArrayAt(task, section).Count <= RelayLimits.RecentTaskEntries)) return task;
        var copy = task.DeepClone().AsObject();
        foreach (var section in ListSections)
        {
            var entries = ArrayAt(task, section);
            if (entries.Count <= RelayLimits.RecentTaskEntries) continue;
            copy[section] = new JsonArray(entries.Skip(entries.Count - RelayLimits.RecentTaskEntries).Select(entry => entry!.DeepClone()).ToArray());
            copy[section + "Total"] = entries.Count;
        }
        return copy;
    }

    private static RelayReply RenewOrRelease(AggregateContext context, JsonObject task)
    {
        var version = Number(task, "version");
        var generation = Number(task, "claimGeneration");
        if (Text(task, "owner") != context.SessionId || generation != Number(context.Args, "claimGeneration") || Date(task, "leaseUntil") <= context.Now) return Error(StaleClaim, new { claimGeneration = generation });
        var release = context.Operation == RelayTools.ClaimRelease;
        if (!release) task["leaseUntil"] = context.Now.AddSeconds(Leases.Seconds(context.Args)).ToString("O");
        else
        {
            if (TaskActions.HasUnresolved(task)) return Error(UnresolvedAction);
            if (!ExpectedVersionMatches(context.Args, version)) return VersionConflict(version);
            task.Remove("owner"); task["leaseUntil"] = context.Now.ToString("O"); task["version"] = ++version;
        }
        return Ok(new { claimGeneration = generation, leaseUntil = Text(task, "leaseUntil"), released = release }, version);
    }

    /// <summary>Rolls back a task claim this request made when its resource claims could not all be acquired.</summary>
    private static RelayReply Unclaim(AggregateContext context, JsonObject task)
    {
        var args = context.Args;
        if (Text(task, "owner") == context.SessionId && Number(task, "claimGeneration") == Number(args, "claimGeneration") && Text(task, "claimRequestId") == Text(args, "claimRequestId"))
        {
            task.Remove("owner"); task["leaseUntil"] = context.Now.ToString("O"); task["version"] = Number(task, "version") + 1;
        }
        return Ok(new { released = true }, Number(task, "version"));
    }

    private static RelayReply Claim(AggregateContext context, JsonObject task)
    {
        var args = context.Args;
        var session = context.SessionId;
        var version = Number(task, "version");
        if (!TaskWorkflow.AllowsExecution(task, session)) return TaskWorkflow.NotReady(task);
        var owned = Text(task, "owner") == session && Date(task, "leaseUntil") > context.Now;
        if (owned && !SameResources(task["resourceIds"], args["resourceIds"])) return Error(ResourceSetConflict, new { resourceIds = task["resourceIds"] }, version);
        var unresolved = TaskActions.HasUnresolved(task);
        if (!owned && unresolved) return Error(Busy, new { holder = Text(task, "owner"), leaseUntil = Text(task, "leaseUntil"), unresolved = true }, version);
        if (Text(task, "owner").Length > 0 && Text(task, "owner") != session && (Date(task, "leaseUntil") > context.Now || unresolved)) return Error(Busy, new { holder = Text(task, "owner"), leaseUntil = Text(task, "leaseUntil"), unresolved }, version);
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        if (!owned)
        {
            task["owner"] = Required(args, "sessionId"); task["claimGeneration"] = Number(task, "claimGeneration") + 1; task["claimRequestId"] = Text(args, "requestId");
            task["leaseUntil"] = context.Now.AddSeconds(Leases.Seconds(args)).ToString("O"); task["version"] = ++version;
            task["resourceIds"] = args["resourceIds"]?.DeepClone() ?? new JsonArray();
        }
        return Ok(new { taskId = Text(args, "taskId"), newlyAcquired = !owned, claimGeneration = Number(task, "claimGeneration"), leaseUntil = Text(task, "leaseUntil"), resourceIds = task["resourceIds"] }, version);
    }

    private static bool SameResources(JsonNode? held, JsonNode? requested) =>
        Strings(held).Order(StringComparer.Ordinal).SequenceEqual(Strings(requested).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>A milestone commit: action reports, shared facts/notes, a conversation message and the workflow, all under one expected version.</summary>
    private static RelayReply Update(AggregateContext context, JsonObject task)
    {
        var args = context.Args;
        var version = Number(task, "version");
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        var reports = context.Operation == RelayTools.ActionReport ? new JsonArray(args.DeepClone()) : args["actionReports"] as JsonArray ?? new JsonArray();
        if (context.Operation == RelayTools.TaskUpdate && Text(task, "owner").Length > 0 && Text(task, "owner") != context.SessionId)
        {
            // An administrator may repair an abandoned packet with its own authenticated session.
            // Action writers and the next worker's ordinary claim remain separately fenced.
            var packetRecovery = context.Caller.IsAdministrator && task["workflow"] is JsonObject && args["workflow"] is JsonObject && !args.ContainsKey("actionReports") && !TaskActions.CarriesMilestone(args);
            if (!packetRecovery || Date(task, "leaseUntil") > context.Now) return Error(OwnerConflict);
            if (TaskActions.HasUnresolved(task)) return Error(UnresolvedAction);
        }
        foreach (var report in reports)
            if (TaskActions.ApplyReport(context, task, report!.AsObject()) is { } failure) return failure;
        if (args["patch"] is JsonObject patch)
        {
            if (ByteSize(patch) > MaxSharedBytes) return Error(PayloadTooLarge);
            foreach (var fact in patch) ObjectAt(task, "facts")[fact.Key] = fact.Value?.DeepClone();
        }
        if (args["notes"] is JsonNode notes)
        {
            if (ByteSize(notes) > MaxSharedBytes) return Error(PayloadTooLarge);
            ObjectAt(task, "facts")["notes"] = notes.DeepClone();
        }
        if (args["message"] is JsonObject message) AddMessage(context, task, message);
        IReadOnlyList<string> unblocked = [];
        if (args.ContainsKey("workflow"))
        {
            if (context.Operation != RelayTools.TaskUpdate || args["workflow"] is not JsonObject workflow) return Error(InvalidWorkflow);
            if (TaskWorkflow.Validate(workflow, task["workflow"] as JsonObject, TaskActions.HasUnresolved(task)) is { } invalid) return Error(invalid);
            if (Text(workflow, "state") == "ready" && TaskWorkflow.RequiresResources(context.State) && workflow["packet"]?["resources"] is not JsonArray) return Error(InvalidWorkflow, new { required = "packet.resources" });
            // A packet whose authorityRef names a standing grant is ready only while that grant exists, covers it and has not expired.
            if (Text(workflow, "state") == "ready" && StandingPreferences.GrantProblem(context.State, workflow, context.Now) is { } grant) return Error(GrantInvalid, new { authorityRef = Text(workflow["packet"]!.AsObject(), "authorityRef"), reason = grant });
            var tasks = ObjectAt(context.State, "tasks");
            if (TaskWorkflow.CheckDependencies(tasks, Text(task, "taskId"), workflow) is { } dependency) return Error(dependency);
            var completing = Text(workflow, "state") == "completed" && (task["workflow"] is not JsonObject prior || Text(prior, "state") != "completed");
            task["workflow"] = workflow.DeepClone();
            // Seam for attention (build steps 7-8): raise a next_work item to each unblocked task's targetSessionId here.
            // Always send a revision (the task's changeRevision) so a late or repeated raise can never reopen a closed item.
            if (completing) unblocked = TaskWorkflow.Unblocked(tasks, Text(task, "taskId"));
        }
        task["updatedAt"] = context.Now.ToString("O"); task["updatedBy"] = context.Caller.PrincipalId; task["version"] = ++version;
        // An acknowledgment, not the task: echoing the whole task (up to its 256-message conversation) put it into the updater's context again
        // on every milestone, and the stored replay of each request kept a copy too. task_get still returns the task. The actions this
        // call reported are included, since the reporter needs their resulting status and restoration.
        var acknowledgment = new JsonObject { ["taskId"] = Text(task, "taskId"), ["version"] = version, ["updatedAt"] = Text(task, "updatedAt") };
        var reported = new JsonObject();
        foreach (var id in reports.Select(report => Text(report!.AsObject(), "actionId")).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
            if (ObjectAt(task, "actions")[id] is JsonNode action) reported[id] = action.DeepClone();
        if (reported.Count > 0) acknowledgment["actions"] = reported;
        if (unblocked.Count > 0) acknowledgment["unblocked"] = new JsonArray(unblocked.Take(RelayLimits.MaxPageSize).Select(id => (JsonNode)id).ToArray());
        if (unblocked.Count > RelayLimits.MaxPageSize) acknowledgment["unblockedTotal"] = unblocked.Count;
        return Ok(acknowledgment, version);
    }

    private static RelayReply SendTaskMessage(AggregateContext context, JsonObject task)
    {
        AddMessage(context, task, context.Args);
        var version = Number(task, "version") + 1;
        task["version"] = version;
        return Ok(new { messageId = ArrayAt(task, "messages").Last()!["messageId"]!.GetValue<string>() }, version);
    }

    /// <summary>Appends to the shared task conversation and notifies the addressed session, or the current owner.</summary>
    private static void AddMessage(AggregateContext context, JsonObject task, JsonObject message)
    {
        var messages = ArrayAt(task, "messages");
        if (messages.Count >= MaxConversation) throw new ArgumentException("Task conversation limit reached.");
        var nextVersion = Number(task, "version") + 1;
        var entry = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString("N"), ["taskId"] = Text(task, "taskId"), ["text"] = SessionAggregate.MessageText(message), ["kind"] = Text(message, "kind", "message"),
            ["replyTo"] = message["replyTo"]?.DeepClone(), ["author"] = context.Caller.PrincipalId, ["sessionId"] = context.SessionId, ["at"] = context.Now.ToString("O"), ["taskVersion"] = nextVersion
        };
        messages.Add(entry);
        var recipient = Text(message, "recipientSessionId", Text(task, "owner"));
        if (recipient.Length > 0)
            context.Emit(AggregateKinds.Session, recipient, Operations.Deliver, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["messageId"] = Text(entry, "messageId"), ["taskId"] = Text(task, "taskId"), ["boardId"] = Text(context.Args, "boardId", "default"),
                ["kind"] = "task.changed", ["taskVersion"] = nextVersion
            });
    }

    /// <summary>A bounded projection of a task: ownership, claim, workflow state and unresolved actions, without its detail sections.</summary>
    public static JsonObject Summary(JsonObject task)
    {
        var summary = new JsonObject
        {
            ["truncated"] = true, ["taskId"] = Text(task, "taskId"), ["version"] = Number(task, "version"), ["owner"] = task["owner"]?.DeepClone(),
            ["claimGeneration"] = Number(task, "claimGeneration"), ["leaseUntil"] = task["leaseUntil"]?.DeepClone(), ["resourceIds"] = task["resourceIds"]?.DeepClone()
        };
        summary["changeRevision"] = Number(task, "changeRevision");
        if (task["workflow"] is JsonObject workflow) { summary["workflow"] = TaskWorkflow.Summary(workflow); summary["workflowDetailFetchRequired"] = true; }
        var unresolved = new JsonArray();
        foreach (var action in (task["actions"] as JsonObject ?? new()).Where(x => TaskActions.IsUnresolved(x.Value!.AsObject())))
            unresolved.Add(new JsonObject { ["actionId"] = action.Key, ["attempt"] = action.Value!["attempt"]?.DeepClone(), ["status"] = action.Value["status"]?.DeepClone(), ["restoration"] = action.Value["restoration"]?.DeepClone(), ["resourceIds"] = action.Value["resourceIds"]?.DeepClone() });
        summary["unresolvedActions"] = unresolved; summary["sections"] = new JsonArray("facts", "messages", "reports", "actions", "attemptHistory");
        return summary;
    }

    public static bool ExceedsTaskCapacity(JsonObject state) =>
        state["tasks"] is JsonObject tasks && tasks.Any(task => ByteSize(task.Value) > RelayLimits.MaxTaskBytes);

    /// <summary>A whole-task read that cannot fit the aggregate reply budget falls back to the task summary.</summary>
    public static RelayReply FitTaskRead(JsonObject state, string operation, JsonObject args, RelayReply result) =>
        !FitsWire(result, RelayLimits.AggregateReplyBytes) && operation == RelayTools.TaskGet && ObjectAt(state, "tasks")[Text(args, "taskId")] is JsonObject task && Text(args, "section").Length == 0
            ? Ok(Summary(task), result.Version)
            : result;
}

using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

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
        // A write to a nearly full board first moves its oldest finished tasks to the archive.
        if (CoordinationStateMachine.IsMutation(context.Operation) && context.Operation != Operations.TaskArchive) TaskArchiving.Run(context, sweep: false);
        var mutation = CoordinationStateMachine.IsMutation(context.Operation) && taskId.Length > 0;
        var before = mutation ? tasks[taskId]?.ToJsonString() : null;
        var result = Dispatch(context, tasks, taskId);
        if (!result.Ok) return result;
        if (mutation && tasks[taskId] is JsonObject changedTask && before != changedTask.ToJsonString()) MarkChanged(state, changedTask);
        // Standing orders run once the triggering change is marked, so their trigger revision is the task's changeRevision.
        foreach (var (on, target) in context.OrderTriggers) StandingOrders.Run(context, on, target, "task:" + Number(tasks[target]!.AsObject(), "changeRevision"));
        var data = JsonNode.Parse(result.DataJson)!.AsObject();
        if (Text(data, "taskId") is { Length: > 0 } id && tasks[id] is JsonObject changed) data["changeRevision"] = Number(changed, "changeRevision");
        return result with { DataJson = data.ToJsonString() };
    }

    internal static void MarkChanged(JsonObject state, JsonObject task)
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
            case Operations.StandingProposeHub: return StandingPreferences.ProposeFromHub(context);
            case RelayTools.TaskGet when taskId.Length == 0 && Text(context.Args, "section") == "standing": return StandingPreferences.View(context.State, context.Now);
            case RelayTools.TaskGet when taskId.Length == 0: return context.Args.ContainsKey("sinceRevision") ? DeltaPage(context.State, context.Args) : ListTasks(context.State, context.Args);
            case RelayTools.ContextGet: return GetContext(context);
            case RelayTools.ContextPut: return PutContext(context);
            case RelayTools.OrderPut: return StandingOrders.Put(context);
            case RelayTools.OrderRemove: return StandingOrders.Remove(context);
            case Operations.Orders: return StandingOrders.Read(context.State, context.Now);
            case Operations.OrderFire: return StandingOrders.OnFire(context);
            case Operations.OrderFailed: return StandingOrders.OnFailed(context);
            case Operations.TaskArchive: TaskArchiving.Run(context, sweep: true); return Ok(new { archived = context.Archived.Count });
        }
        if (taskId.Length == 0) return Error(InvalidArguments, new { required = "taskId" });
        if (tasks[taskId] is not JsonObject task)
        {
            // An archived task is read from storage by the hub; every write to it, a new task under its ID included, is refused.
            if (TaskArchiving.IsArchived(context.State, taskId)) return Error(TaskArchived, new { taskId, archived = true });
            // Only an explicit create (task_update at expected version 0) brings a task into existence.
            if (context.Operation != RelayTools.TaskUpdate || !ExpectedVersionMatches(context.Args, 0)) return Error(NotFound);
            // Full even after archiving: every task is open, claimed or waiting on an action.
            if (tasks.Count >= RelayLimits.MaxRecords)
                return Error(CapacityExceeded, new
                {
                    maxTasks = RelayLimits.MaxRecords, openTasks = tasks.Count(entry => entry.Value is JsonObject item && !TaskArchiving.IsFinished(item)),
                    // Finished tasks wait MinArchiveAgeHours, a claim's lease or an open action before they can leave.
                    finishedTasks = tasks.Count(entry => entry.Value is JsonObject item && TaskArchiving.IsFinished(item)), archivable = 0
                });
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
        // Live tasks plus, for a checkpoint, remembered removals: at most MaxRecords of each.
        if (since < 0 || since > current || offset < 0 || offset > 2 * RelayLimits.MaxRecords || offset > 0 && !args.ContainsKey("snapshotRevision")) return Error(InvalidArguments);
        if (args.ContainsKey("snapshotRevision") && Number(args, "snapshotRevision") != current) return Error(DeltaRestartRequired, new { restartRequired = true, currentRevision = current });
        // The board forgot archived IDs up to archiveFloor; a checkpoint older than that cannot learn every removal, so it starts over from 0.
        var floor = Number(state, TaskArchiving.FloorKey);
        if (since > 0 && since < floor) return Error(DeltaRestartRequired, new { restartRequired = true, currentRevision = current, archiveFloor = floor, sinceRevision = 0 });
        // Tasks moved to the archive since the checkpoint come back as removals: { taskId, archived: true, changeRevision }. A reader from
        // 0 has seen nothing yet, so it gets only live tasks.
        var removed = since == 0 ? Enumerable.Empty<(string Key, JsonNode Item)>() : (state[TaskArchiving.ArchivedKey] as JsonObject ?? new JsonObject())
            .Select(x => (x.Key, Revision: TaskArchiving.Revision(x.Value))).Where(x => x.Revision > since && x.Revision > floor)
            .Select(x => (x.Key, Item: (JsonNode)new JsonObject { ["taskId"] = x.Key, ["archived"] = true, ["changeRevision"] = x.Revision }));
        var all = ObjectAt(state, "tasks").Where(x => Number(x.Value!.AsObject(), "changeRevision") > since).Select(x => (x.Key, Item: (JsonNode)Summary(x.Value!.AsObject())))
            .Concat(removed).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Item).ToArray();
        var limit = (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultPageSize), 1, RelayLimits.MaxPageSize);
        var items = all.Skip((int)offset).Take(args.ContainsKey("partOffset") ? 1 : limit).Select(x => x.DeepClone()).ToList();
        RelayReply Reply() => Ok(new { items, snapshotRevision = current, nextCursor = offset + items.Count < all.Length ? (long?)(offset + items.Count) : null, truncated = offset + items.Count < all.Length, total = all.Length });
        while (items.Count > 1 && !FitsWire(Reply(), RelayLimits.AggregateReplyBytes)) items.RemoveAt(items.Count - 1);
        // A summary too large for any page comes back in pieces (partOffset), and the cursor still moves past it.
        if (items.Count == 1 && (args.ContainsKey("partOffset") || !FitsWire(Reply(), RelayLimits.AggregateReplyBytes)))
        {
            var whole = items[0];
            items[0] = Piece(whole, Number(args, "partOffset"), piece => { items[0] = piece; return FitsWire(Reply(), RelayLimits.AggregateReplyBytes); });
        }
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
        // Escaping for the wire can double a note's size; one context_get could not return it whole.
        if (CheckReadable(entry) is { } unreadable) return unreadable;
        notes[id] = entry;
        return Ok(entry, version + 1);
    }

    /// <summary>An archived task's read, from the JSON storage kept: the same sections and fallbacks as a live task.</summary>
    internal static RelayReply ReadArchived(JsonObject args, JsonObject task)
    {
        var reply = ReadTask(args, task);
        return reply.Ok && Text(args, "section").Length == 0 && !FitsWire(reply, RelayLimits.AggregateReplyBytes) ? Ok(Summary(task), reply.Version) : reply;
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
            // A recovered action no longer holds the task; its pending restoration still blocks new actions and completion.
            if (TaskActions.HoldsTask(task)) return Error(UnresolvedAction);
            if (!ExpectedVersionMatches(context.Args, version)) return VersionConflict(version);
            ReleaseClaim(task, context.Now); task["version"] = ++version;
        }
        return Ok(new { claimGeneration = generation, leaseUntil = Text(task, "leaseUntil"), released = release }, version);
    }

    /// <summary>Rolls back a task claim this request made when its resource claims could not all be acquired.</summary>
    private static RelayReply Unclaim(AggregateContext context, JsonObject task)
    {
        var args = context.Args;
        if (Text(task, "owner") == context.SessionId && Number(task, "claimGeneration") == Number(args, "claimGeneration") && Text(task, "claimRequestId") == Text(args, "claimRequestId"))
        {
            ReleaseClaim(task, context.Now); task["version"] = Number(task, "version") + 1;
            // The claim is undone, so its retry must run again rather than replay it.
            CoordinationStateMachine.ForgetRequest(context.State, context.Caller.PrincipalId, context.SessionId, RelayTools.TaskClaim, Text(args, "claimRequestId"));
            AttentionSources.OnUnclaim(context, task);
        }
        return Ok(new { released = true }, Number(task, "version"));
    }

    /// <summary>
    /// What a retry gets instead of its stored reply: a begun action returns as it stands now, and a claim replays only while this session
    /// still holds the same claim on a task it may still run.
    /// </summary>
    internal static RelayReply? ReplayRefusal(JsonObject board, string operation, JsonObject args, JsonObject stored, DateTimeOffset now)
    {
        var (session, task) = (Text(args, "sessionId"), ObjectAt(board, "tasks")[Text(args, "taskId")] as JsonObject);
        if (operation == RelayTools.ActionBegin && task is not null && ObjectAt(task, "actions")[Text(args, "actionId")] is JsonObject action)
            return Ok(new { action, replayAllowed = false, existing = true }, Number(task, "version"));
        if (operation is not (RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.TaskClaim) || task is null) return null;
        if (Text(task, "owner") != session || Number(task, "claimGeneration") != Number(stored, "claimGeneration")) return Error(StaleClaim);
        return operation == RelayTools.TaskClaim ? TaskWorkflow.ExecutionRefusal(board, task, session, now) : null;
    }

    /// <summary>Ends a task's claim; the caller bumps its version.</summary>
    internal static void ReleaseClaim(JsonObject task, DateTimeOffset now)
    {
        task.Remove("owner");
        task["leaseUntil"] = now.ToString("O");
    }

    private static RelayReply Claim(AggregateContext context, JsonObject task)
    {
        var args = context.Args;
        var session = context.SessionId;
        var version = Number(task, "version");
        if (TaskWorkflow.ExecutionRefusal(context.State, task, session, context.Now) is { } refusal) return refusal;
        // The claim must take every resource the packet declares, so no work runs while another session holds one.
        if (TaskWorkflow.DeclaredResourcesNeeded(task, args) is { } needed) return needed;
        if (TaskWorkflow.ResourceRefusal(context.State, task, args, Strings(args["resourceIds"])) is { } resources) return resources;
        var owned = Text(task, "owner") == session && Date(task, "leaseUntil") > context.Now;
        if (owned && !SameResources(task["resourceIds"], args["resourceIds"])) return Error(ResourceSetConflict, new { resourceIds = task["resourceIds"] }, version);
        // A recovered action (its writer gone, see TaskActions.ApplyReport) no longer holds the task once its writer's lease has lapsed.
        var unresolved = TaskActions.HoldsTask(task);
        if (!owned && unresolved) return Error(Busy, new { holder = Text(task, "owner"), leaseUntil = Text(task, "leaseUntil"), unresolved = true }, version);
        if (Text(task, "owner").Length > 0 && Text(task, "owner") != session && (Date(task, "leaseUntil") > context.Now || unresolved)) return Error(Busy, new { holder = Text(task, "owner"), leaseUntil = Text(task, "leaseUntil"), unresolved }, version);
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        if (!owned)
        {
            task["owner"] = Required(args, "sessionId"); task["claimGeneration"] = Number(task, "claimGeneration") + 1; task["claimRequestId"] = Text(args, "requestId");
            task["leaseUntil"] = context.Now.AddSeconds(Leases.Seconds(args)).ToString("O"); task["version"] = ++version;
            task["resourceIds"] = args["resourceIds"]?.DeepClone() ?? new JsonArray();
            AttentionSources.OnClaim(context, task);
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
        var priorStatus = ObjectAt(task, "actions").ToDictionary(x => x.Key, x => x.Value is JsonObject action ? Text(action, "status") : "", StringComparer.Ordinal);
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
        if (args["message"] is JsonObject message && AddMessage(context, task, message) is { } unreadable) return unreadable;
        IReadOnlyList<string> unblocked = [];
        var priorWorkflow = task["workflow"]?.DeepClone() as JsonObject;
        if (args.ContainsKey("workflow"))
        {
            if (context.Operation != RelayTools.TaskUpdate || args["workflow"] is not JsonObject workflow) return Error(InvalidWorkflow);
            if (TaskWorkflow.Validate(workflow, task["workflow"] as JsonObject, TaskActions.HasUnresolved(task)) is { } invalid) return Error(invalid);
            if (Text(workflow, "state") == "ready" && TaskWorkflow.RequiresResources(context.State) && workflow["packet"]?["resources"] is not JsonArray) return Error(InvalidWorkflow, new { required = "packet.resources" });
            // A packet whose authorityRef names a standing grant is ready only while that grant exists, covers it and has not expired.
            if (Text(workflow, "state") == "ready" && TaskWorkflow.GrantRefusal(context.State, workflow, context.Now) is { } grant) return grant;
            var tasks = ObjectAt(context.State, "tasks");
            // The hub passes the dependencies it found in the archive table (archivedDependsOn); they count as completed from now on.
            TaskArchiving.Remember(context.State, Strings(args["archivedDependsOn"]).Intersect(TaskWorkflow.Dependencies(workflow), StringComparer.Ordinal), context.Now);
            if (TaskWorkflow.CheckDependencies(tasks, Text(task, "taskId"), workflow) is { } dependency) return Error(dependency);
            var completing = Text(workflow, "state") == "completed" && (task["workflow"] is not JsonObject prior || Text(prior, "state") != "completed");
            task["workflow"] = workflow.DeepClone();
            // Archiving counts from completion; reopening clears it.
            if (completing) task["completedAt"] = context.Now.ToString("O");
            else if (Text(workflow, "state") != "completed") task.Remove("completedAt");
            if (completing) unblocked = TaskWorkflow.Unblocked(tasks, Text(task, "taskId"));
        }
        // Attention items for this change commit with it: blocked, failed and review_ready to managers, next_work to each unblocked target.
        string[] NewlyIn(string status) => ObjectAt(task, "actions").Where(x => x.Value is JsonObject action && Text(action, "status") == status && priorStatus.GetValueOrDefault(x.Key) != status)
            .Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        // An abandoned action's external effects are unknown, so the managers hear of it like a failure.
        // A writer's real outcome that replaced abandoned (not a failure: that one is NewlyIn("failed")).
        var replaced = ObjectAt(task, "actions").Where(x => x.Value is JsonObject action && priorStatus.GetValueOrDefault(x.Key) == TaskActions.Abandoned && Text(action, "status") is not (TaskActions.Abandoned or "failed"))
            .Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        AttentionSources.OnTaskUpdate(context, task, priorWorkflow, unblocked, NewlyIn("failed"), NewlyIn(TaskActions.Abandoned), replaced);
        StandingOrders.Detect(context, task, priorWorkflow);
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
        if (AddMessage(context, task, context.Args) is { } unreadable) return unreadable;
        var version = Number(task, "version") + 1;
        task["version"] = version;
        return Ok(new { messageId = ArrayAt(task, "messages").Last()!["messageId"]!.GetValue<string>() }, version);
    }

    /// <summary>
    /// Appends to the shared task conversation and notifies the addressed session, or the current owner. Returns <c>PayloadTooLarge</c>,
    /// and changes nothing, when the entry escaped for the wire would not fit a <c>task_get</c> messages page.
    /// </summary>
    private static RelayReply? AddMessage(AggregateContext context, JsonObject task, JsonObject message)
    {
        var messages = ArrayAt(task, "messages");
        if (messages.Count >= MaxConversation) throw new ArgumentException("Task conversation limit reached.");
        var nextVersion = Number(task, "version") + 1;
        var entry = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString("N"), ["taskId"] = Text(task, "taskId"), ["text"] = SessionAggregate.MessageText(message), ["kind"] = Text(message, "kind", "message"),
            ["replyTo"] = message["replyTo"]?.DeepClone(), ["author"] = context.Caller.PrincipalId, ["sessionId"] = context.SessionId, ["at"] = context.Now.ToString("O"), ["taskVersion"] = nextVersion
        };
        if (CheckReadable(entry) is { } unreadable) return unreadable;
        messages.Add(entry);
        var recipient = Text(message, "recipientSessionId", Text(task, "owner"));
        if (recipient.Length > 0)
            context.Emit(AggregateKinds.Session, recipient, Operations.Deliver, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["messageId"] = Text(entry, "messageId"), ["taskId"] = Text(task, "taskId"), ["boardId"] = Text(context.Args, "boardId", "default"),
                ["kind"] = "task.changed", ["taskVersion"] = nextVersion
            });
        return null;
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

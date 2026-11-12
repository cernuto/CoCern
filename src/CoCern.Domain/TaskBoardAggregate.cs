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
/// One repository board: its tasks (facts, conversation, reports, actions, workflow), the task claim, and shared
/// context notes. Every committed task change advances the board's taskRevision so readers can fetch deltas.
/// </summary>
internal static class TaskBoardAggregate
{
    private const int MaxConversation = 256;
    private const int MaxSharedBytes = 16_384;
    private const int MaxContextEntryBytes = 24_576;
    /// <summary>The paged list sections <c>task_get</c> reads by name; each is the task's own key.</summary>
    private static readonly string[] ListSections = [BoardTask.MessagesKey, BoardTask.ReportsKey, BoardTask.AttemptHistoryKey];

    public static RelayReply Handle(AggregateContext context)
    {
        var board = new BoardState(context.State);
        var tasks = board.TaskMap;
        // Migrate only current markers. No copied history or changes to optimistic task versions.
        foreach (var task in tasks.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new BoardTask(x.Value!.AsObject())))
            if (!task.HasChangeRevision) board.MarkChanged(task);
        if (context.Operation is EventWaitSources.SourceWatchOperation or EventWaitSources.SourceUnwatchOperation)
            return EventWaitSources.HandleSource(context, AggregateKinds.Board);
        var taskId = Text(context.Args, "taskId");
        // A write to a nearly full board first moves its oldest finished tasks to the archive.
        if (CoordinationStateMachine.IsMutation(context.Operation) && context.Operation != Operations.TaskArchive) TaskArchiving.Run(context, sweep: false);
        var mutation = CoordinationStateMachine.IsMutation(context.Operation) && taskId.Length > 0;
        var before = mutation ? tasks[taskId]?.ToJsonString() : null;
        var wasCompleted = mutation && board.Task(taskId)?.Workflow?.State == "completed";
        var result = Dispatch(context, board, taskId);
        if (!result.Ok) return result;
        if (mutation && board.Task(taskId) is { } changedTask && before != changedTask.Json.ToJsonString()) board.MarkChanged(changedTask);
        if (mutation && !wasCompleted && board.Task(taskId) is { } completed && completed.Workflow?.State == "completed")
            EventWaitSources.TaskCompleted(context, completed);
        // Standing orders run once the triggering change is marked, so their trigger revision is the task's changeRevision.
        foreach (var (on, target) in context.OrderTriggers) StandingOrders.Run(context, on, target, "task:" + board.Task(target)!.ChangeRevision);
        var data = JsonNode.Parse(result.DataJson)!.AsObject();
        if (Text(data, "taskId") is { Length: > 0 } id && board.Task(id) is { } changed) data["changeRevision"] = changed.ChangeRevision;
        return result with { DataJson = data.ToJsonString() };
    }

    private static RelayReply Dispatch(AggregateContext context, BoardState board, string taskId)
    {
        switch (context.Operation)
        {
            case Operations.NextWork: return TaskWorkflow.NextWork(context);
            case Operations.Standing: return StandingPreferences.Read(context);
            case GoalOrderDelivery.BoardOperation: return GoalOrderDelivery.CheckBoard(context);
            case Operations.PresentStanding: return StandingPreferences.Present(context);
            case RelayTools.StandingPut: return StandingPreferences.Put(context);
            case RelayTools.StandingPropose: return StandingPreferences.Propose(context);
            case RelayTools.RecordDecision: return StandingPreferences.RecordDecision(context);
            case Operations.StandingProposeHub: return StandingPreferences.ProposeFromHub(context);
            case RelayTools.TaskGet when taskId.Length == 0 && Text(context.Args, "section") == "standing": return StandingPreferences.View(context.State, context.Now);
            case RelayTools.TaskGet when taskId.Length == 0 && Text(context.Args, "section") == "brief": return Error(InvalidArguments, new { required = "taskId" });
            case RelayTools.TaskGet when taskId.Length == 0: return context.Args.ContainsKey("sinceRevision") ? DeltaPage(board, context.Args) : ListTasks(board, context);
            case RelayTools.ContextGet: return GetContext(context);
            case RelayTools.ContextPut: return PutContext(context);
            case RelayTools.OrderPut: return StandingOrders.Put(context);
            case RelayTools.OrderRemove: return StandingOrders.Remove(context);
            case Operations.Orders: return StandingOrders.Read(context);
            case Operations.OrderFire: return StandingOrders.OnFire(context);
            case Operations.OrderFailed: return StandingOrders.OnFailed(context);
            case Operations.OrderWatchFailed: return StandingOrders.OnWatchFailed(context);
            case Operations.TaskArchive: return Sweep(context);
            case Operations.TaskUnclaim: return Unclaim(context, board);
            case Operations.SessionClosed: return Untarget(context, board);
        }
        if (taskId.Length == 0) return Error(InvalidArguments, new { required = "taskId" });
        if (board.Task(taskId) is not { } task)
        {
            // Only an explicit create (task_update at expected version 0) brings a task into existence.
            if (board.IsArchived(taskId) || context.Operation != RelayTools.TaskUpdate || !ExpectedVersionMatches(context.Args, 0)) return Absent(board, taskId);
            // Full even after archiving: every task is open, claimed or waiting on an action.
            if (board.TaskMap.Count >= RelayLimits.MaxRecords)
                return Error(CapacityExceeded, new
                {
                    maxTasks = RelayLimits.MaxRecords, openTasks = board.Tasks.Count(item => !TaskArchiving.IsFinished(item)),
                    // Finished tasks wait MinArchiveAgeHours, a claim's lease or an open action before they can leave.
                    finishedTasks = board.Tasks.Count(TaskArchiving.IsFinished), archivable = 0
                });
            task = board.AddTask(taskId);
        }
        return context.Operation switch
        {
            RelayTools.TaskGet => ReadTask(context.Args, task),
            RelayTools.ClaimRenew or RelayTools.ClaimRelease => RenewOrRelease(context, task),
            RelayTools.TaskClaim => Claim(context, task),
            RelayTools.ActionBegin => TaskActions.Begin(context, task),
            RelayTools.ActionReport or RelayTools.TaskUpdate => Update(context, task),
            RelayTools.SendMessage => SendTaskMessage(context, task),
            _ => Error(UnknownTool)
        };
    }

    private static RelayReply ListTasks(BoardState board, AggregateContext context)
    {
        var args = context.Args;
        var full = args["includeDetails"] is JsonValue details && details.TryGetValue<bool>(out var include) && include;
        var openOnly = args["openOnly"] is JsonValue open && open.TryGetValue<bool>(out var filter) && filter;
        var tasks = board.TaskMap.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new BoardTask(x.Value!.AsObject()))
            .Where(task => !openOnly || !TaskArchiving.Archivable(task, context.Now)).ToArray();
        var tag = openOnly ? ListSnapshot.Tag(new { kind = "openTasks", context.Repository, context.Key, context.Caller.PrincipalId, context.Caller.IsAdministrator }, tasks.Select(task => task.TaskId)) : null;
        if (tag is not null && !ListSnapshot.Matches(args, tag)) return Error("ListRestartRequired");
        var rows = tasks.Select(task =>
        {
            if (!full && TaskArchiving.Archivable(task, context.Now)) return (JsonNode)new JsonObject { ["taskId"] = task.TaskId, ["state"] = "completed" };
            var summary = Summary(task);
            if (!full) summary.Remove("sections");
            return (JsonNode)summary;
        }).ToArray();
        var page = Page(rows, args);
        var data = JsonNode.Parse(page.DataJson)!.AsObject();
        data["boardRevision"] = board.TaskRevision;
        if (tag is not null) data["snapshotTag"] = tag;
        return full && tag is null ? Ok(data) : ListSnapshot.Fit(data, rows, args);
    }

    /// <summary>Tasks changed since a checkpoint, paged against a pinned snapshot revision. A concurrent change restarts the read.</summary>
    private static RelayReply DeltaPage(BoardState board, JsonObject args)
    {
        static bool Integer(JsonObject obj, string key) => obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), out _);
        if (!Integer(args, "sinceRevision") || args.ContainsKey("snapshotRevision") && !Integer(args, "snapshotRevision") || args.ContainsKey("cursor") && !Integer(args, "cursor")) return Error(InvalidArguments);
        var since = Number(args, "sinceRevision");
        var current = board.TaskRevision;
        var offset = Number(args, "cursor");
        // Live tasks plus, for a checkpoint, remembered removals: at most MaxRecords of each.
        if (since < 0 || since > current || offset < 0 || offset > 2 * RelayLimits.MaxRecords || offset > 0 && !args.ContainsKey("snapshotRevision")) return Error(InvalidArguments);
        if (args.ContainsKey("snapshotRevision") && Number(args, "snapshotRevision") != current) return Error(DeltaRestartRequired, new { restartRequired = true, currentRevision = current });
        // The board forgot archived IDs up to archiveFloor; a checkpoint older than that cannot learn every removal, so it starts over from 0.
        var floor = board.ArchiveFloor;
        if (since > 0 && since < floor) return Error(DeltaRestartRequired, new { restartRequired = true, currentRevision = current, archiveFloor = floor, sinceRevision = 0 });
        // Tasks moved to the archive since the checkpoint come back as removals: { taskId, archived: true, changeRevision }. A reader from
        // 0 has seen nothing yet, so it gets only live tasks.
        var removed = since == 0 ? Enumerable.Empty<(string Key, JsonNode Item)>() : (board.ArchivedTasksIfAny ?? new JsonObject())
            .Select(x => (x.Key, Revision: TaskArchiving.Revision(x.Value))).Where(x => x.Revision > since && x.Revision > floor)
            .Select(x => (x.Key, Item: (JsonNode)new JsonObject { ["taskId"] = x.Key, ["archived"] = true, ["changeRevision"] = x.Revision }));
        var all = board.TaskMap.Select(x => (x.Key, Task: new BoardTask(x.Value!.AsObject()))).Where(x => x.Task.ChangeRevision > since).Select(x => (x.Key, Item: (JsonNode)Summary(x.Task)))
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
        var notes = new BoardState(context.State).Notes;
        var id = Text(context.Args, "contextId");
        if (id.Length == 0) return Page(notes.Select(x => x.Value!), context.Args);
        return notes[id] is JsonNode note ? Ok(note, new ContextNote(note.AsObject()).Version) : Error(NotFound);
    }

    private static RelayReply PutContext(AggregateContext context)
    {
        var args = context.Args;
        var notes = new BoardState(context.State).Notes;
        var id = Required(args, "contextId");
        var version = notes[id] is JsonObject previous ? new ContextNote(previous).Version : 0;
        if (ByteSize(args["content"]) > MaxSharedBytes) return Error(PayloadTooLarge);
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        if (notes.Count >= RelayLimits.MaxRecords && !notes.ContainsKey(id)) return Error(CapacityExceeded);
        var entry = ContextNote.Create(id, version + 1, args["content"]?.DeepClone(), args["provenance"]?.DeepClone(), context.Caller.PrincipalId, context.Now).Json;
        if (ByteSize(entry) > MaxContextEntryBytes) return Error(PayloadTooLarge);
        // Escaping for the wire can double a note's size; one context_get could not return it whole.
        if (CheckReadable(entry) is { } unreadable) return unreadable;
        notes[id] = entry;
        return Ok(entry, version + 1);
    }

    /// <summary>An archived task's read, from the JSON storage kept: the same sections and fallbacks as a live task.</summary>
    internal static RelayReply ReadArchived(JsonObject args, JsonObject archived)
    {
        var task = new BoardTask(archived);
        var reply = ReadTask(args, task);
        return reply.Ok && Text(args, "section").Length == 0 && !FitsWire(reply, RelayLimits.AggregateReplyBytes) ? Ok(Summary(task), reply.Version) : reply;
    }

    private static RelayReply ReadTask(JsonObject args, BoardTask task)
    {
        var version = task.Version;
        var section = Text(args, "section");
        if (section == "brief") return Brief(args, task);
        if (section == BoardTask.WorkflowKey) return task.Workflow is { } workflow ? Ok(workflow.Json, version) : Error(NotFound);
        if (ListSections.Contains(section)) return Page(task.Section(section).Select(x => x!), args) with { Version = version };
        if (section == BoardTask.ActionsKey) return Page(task.Actions.Select(x => x.Value!), args) with { Version = version };
        if (section == BoardTask.FactsKey)
        {
            var facts = task.Facts;
            var factKey = Text(args, "factKey");
            if (factKey.Length > 0) return facts.ContainsKey(factKey) ? Ok(new { key = factKey, value = facts[factKey] }, version) : Error(NotFound);
            return Page(facts.Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["value"] = x.Value?.DeepClone() }), args) with { Version = version };
        }
        var recent = Recent(task);
        if (ByteSize(recent) > RelayLimits.MaxPayloadBytes) return Ok(Summary(task), version);
        return Ok(recent, version);
    }

    /// <summary>
    /// Current task context without copied history. Required groups fit whole or name the exact section still needed before acting;
    /// no packet, criterion, action fence or code string is shortened to make a brief appear complete.
    /// </summary>
    internal static RelayReply Brief(JsonObject args, BoardTask task)
    {
        const int maximumBytes = 4_096;
        var sections = new[] { BoardTask.WorkflowKey, BoardTask.FactsKey, BoardTask.ActionsKey, BoardTask.MessagesKey, BoardTask.ReportsKey, BoardTask.AttemptHistoryKey };
        // One common reference: add the exact name from sections or detailFetchRequired instead of repeating the identity six times.
        var refs = new JsonObject
        {
            ["tool"] = RelayTools.TaskGet, ["repositoryId"] = Text(args, "repositoryId"), ["boardId"] = Text(args, "boardId", "default"), ["taskId"] = task.TaskId
        };
        var needed = new JsonArray();
        var brief = new JsonObject
        {
            ["brief"] = true, ["complete"] = true, ["taskId"] = task.TaskId, ["version"] = task.Version, ["changeRevision"] = task.ChangeRevision,
            ["owner"] = task.Json[BoardTask.OwnerKey]?.DeepClone(), ["claimGeneration"] = task.ClaimGeneration,
            ["leaseUntil"] = task.Json[BoardTask.LeaseUntilKey]?.DeepClone(), ["resourceIds"] = task.ResourceIds?.DeepClone(),
            ["sections"] = new JsonArray(sections.Select(section => (JsonNode?)JsonValue.Create(section)).ToArray()),
            ["refs"] = refs, ["detailFetchRequired"] = needed
        };
        foreach (var key in new[] { BoardTask.UpdatedAtKey, BoardTask.UpdatedByKey, BoardTask.ArchivedAtKey })
            if (task.Json[key] is { } value) brief[key] = value.DeepClone();
        bool Fits() => FitsWire(Ok(brief, task.Version), maximumBytes);
        bool Include(string key, JsonNode value)
        {
            brief[key] = value;
            if (Fits()) return true;
            brief.Remove(key);
            return false;
        }
        void Need(string section)
        {
            if (!needed.Any(value => value?.GetValue<string>() == section)) needed.Add(section);
            brief["complete"] = false;
        }

        if (task.Workflow is { } workflow && !Include(BoardTask.WorkflowKey, workflow.Json.DeepClone()))
        {
            brief["workflowState"] = workflow.State;
            Need(BoardTask.WorkflowKey);
        }
        var unresolved = new JsonArray();
        foreach (var entry in task.Actions)
        {
            if (entry.Value is not JsonObject record || !TaskActions.IsUnresolved(new TaskAction(record))) continue;
            var copy = record.DeepClone().AsObject();
            copy["actionId"] = entry.Key;
            unresolved.Add(copy);
        }
        brief["unresolvedActionCount"] = unresolved.Count;
        if (!Include("unresolvedActions", unresolved)) Need(BoardTask.ActionsKey);

        var facts = task.FactsIfAny;
        var titleIncluded = facts?[BoardTask.TitleFact] is { } title && Include("facts", new JsonObject { [BoardTask.TitleFact] = title.DeepClone() });
        if (facts is { Count: > 0 } && (!titleIncluded || facts.Count > 1)) Need(BoardTask.FactsKey);
        brief["factCount"] = facts?.Count ?? 0;
        brief["actionCount"] = task.Actions.Count;
        foreach (var section in ListSections) brief[section + "Total"] = task.Section(section).Count;
        // Count/refusal markers can tip an otherwise exact-fit group over the cap. Remove groups atomically, never their text.
        if (!Fits() && brief.Remove("facts")) Need(BoardTask.FactsKey);
        if (!Fits() && brief.Remove("unresolvedActions") && brief["unresolvedActionCount"]!.GetValue<int>() > 0) Need(BoardTask.ActionsKey);
        if (!Fits() && brief.Remove(BoardTask.WorkflowKey))
        {
            brief["workflowState"] = task.Workflow!.State;
            Need(BoardTask.WorkflowKey);
        }
        // Extremely long required IDs/resources can outweigh the brief budget themselves. Refuse this projection rather than clip them.
        if (!Fits()) return Error(PayloadTooLarge, new { taskId = task.TaskId, version = task.Version, readFullTask = true }, task.Version);
        var result = Ok(brief, task.Version);
        var recentData = Recent(task);
        var recent = Ok(recentData, task.Version);
        // A task already smaller than its navigation metadata needs no projection at all.
        var selected = FitsWire(recent, maximumBytes) && ByteSize(recentData) <= ByteSize(brief) ? recent : result;
        return selected with
        {
            ProjectionSourceBytes = System.Text.Encoding.UTF8.GetByteCount(recent.DataJson),
            ProjectionReplyBytes = System.Text.Encoding.UTF8.GetByteCount(selected.DataJson)
        };
    }

    /// <summary>
    /// The task with only its latest list entries. A chat re-reading a task otherwise takes in its whole history every time, and that read
    /// grows with every message; each trimmed list gets a <c>&lt;section&gt;Total</c>, and its older entries stay in the paged section.
    /// </summary>
    private static JsonObject Recent(BoardTask task)
    {
        if (ListSections.All(section => task.Section(section).Count <= RelayLimits.RecentTaskEntries)) return task.Json;
        var copy = task.Json.DeepClone().AsObject();
        foreach (var section in ListSections)
        {
            var entries = task.Section(section);
            if (entries.Count <= RelayLimits.RecentTaskEntries) continue;
            copy[section] = new JsonArray(entries.Skip(entries.Count - RelayLimits.RecentTaskEntries).Select(entry => entry!.DeepClone()).ToArray());
            copy[section + "Total"] = entries.Count;
        }
        return copy;
    }

    private static RelayReply RenewOrRelease(AggregateContext context, BoardTask task)
    {
        var version = task.Version;
        var generation = task.ClaimGeneration;
        if (task.Owner != context.SessionId || generation != Number(context.Args, "claimGeneration") || task.LeaseUntil <= context.Now) return Error(StaleClaim, new { claimGeneration = generation });
        var release = context.Operation == RelayTools.ClaimRelease;
        if (!release) task.LeaseUntil = context.Now.AddSeconds(Leases.Seconds(context.Args));
        else
        {
            // A recovered action no longer holds the task; its pending restoration still blocks new actions and completion.
            if (TaskActions.HoldsTask(task)) return Error(UnresolvedAction);
            if (!ExpectedVersionMatches(context.Args, version)) return VersionConflict(version);
            task.ReleaseClaim(context.Now); task.Version = ++version;
        }
        return Ok(new { claimGeneration = generation, leaseUntil = task.LeaseUntilText, released = release }, version);
    }

    /// <summary>
    /// Rolls back a task claim this request made when its resource claims could not all be acquired (<see cref="TaskUnclaim"/>). A claim the
    /// session no longer holds, or one another request made, is left alone.
    /// </summary>
    private static RelayReply Unclaim(AggregateContext context, BoardState board)
    {
        if (!TaskUnclaim.Decode(context.Args).TryGet(out var unclaim, out var malformed)) return Malformed(malformed);
        if (board.Task(unclaim.TaskId) is not { } task) return Absent(board, unclaim.TaskId);
        if (task.Owner == unclaim.SessionId && task.ClaimGeneration == unclaim.ClaimGeneration && task.ClaimRequestId == unclaim.ClaimRequestId)
        {
            task.ReleaseClaim(context.Now); task.Version += 1;
            // The claim is undone, so its retry must run again rather than replay it.
            CoordinationStateMachine.ForgetRequest(context.State, context.Caller.PrincipalId, unclaim.SessionId, RelayTools.TaskClaim, unclaim.ClaimRequestId);
            AttentionSources.OnUnclaim(context, task.Json);
        }
        return Ok(new { released = true }, task.Version);
    }

    /// <summary>The due archive sweep (<see cref="TaskArchiveSweep"/>): moves aged finished tasks to the archive and forgets old archived IDs.</summary>
    private static RelayReply Sweep(AggregateContext context)
    {
        if (!TaskArchiveSweep.Decode(context.Args).TryGet(out _, out var malformed)) return Malformed(malformed);
        TaskArchiving.Run(context, sweep: true);
        return Ok(new { archived = context.Archived.Count });
    }

    /// <summary>A task this board does not hold: an archived one is read from storage by the hub, and every write to it is refused.</summary>
    private static RelayReply Absent(BoardState board, string taskId) => board.IsArchived(taskId) ? Error(TaskArchived, new { taskId, archived = true }) : Error(NotFound);

    /// <summary>
    /// <see cref="Operations.SessionClosed"/>: a session this board's tasks targeted, or one that answered for work here, has closed. Each
    /// task that still targets it loses the target, as a lapsed claim frees a task: a target only the closed session could claim would leave
    /// the task unclaimable by anyone. Its unresolved actions here are stranded (see <see cref="TaskActions.Strand"/>): marked and made
    /// recoverable at once. Each changed task gets a new version and change revision, so readers and offers see it; a repeated notice
    /// changes nothing.
    /// </summary>
    private static RelayReply Untarget(AggregateContext context, BoardState board)
    {
        if (!SessionClosed.Decode(context.Args).TryGet(out var closed, out var malformed)) return Malformed(malformed);
        var session = closed.SessionId;
        var (cleared, stranded) = (0, 0);
        foreach (var task in board.Tasks)
        {
            var changed = false;
            if (task.Workflow is { } workflow && workflow.TargetSessionId == session)
            {
                workflow.ClearTarget();
                changed = true; cleared++;
            }
            var (actionsChanged, newlyStranded) = TaskActions.Strand(task, session, context.Now);
            if (actionsChanged) { changed = true; stranded++; }
            if (!changed) continue;
            task.Version += 1;
            board.MarkChanged(task);
            // Each action newly stranded is raised to the managers once, at the revision of this change.
            foreach (var action in newlyStranded) AttentionSources.OnStranded(context, task, action, session, board.TaskRevision);
        }
        return Ok(new { cleared, stranded });
    }

    /// <summary>
    /// What a retry gets instead of its stored reply: a begun action returns as it stands now, and a claim replays only while this session
    /// still holds the same claim on a task it may still run.
    /// </summary>
    internal static RelayReply? ReplayRefusal(JsonObject state, string operation, JsonObject args, JsonObject stored, DateTimeOffset now, VerifiedNativeParticipant? participant)
    {
        var board = new BoardState(state);
        var (session, task) = (Text(args, "sessionId"), board.Task(Text(args, "taskId")));
        if (operation == RelayTools.ActionBegin && task?.Action(Text(args, "actionId")) is { } action)
        {
            if (task.Workflow is { } workflow && GoalAuthority.Problem(board, workflow, participant, session, now) is { } goal) return Error(GrantInvalid, new { reason = goal }, task.Version);
            return Ok(new { action = action.Json, replayAllowed = false, existing = true }, task.Version);
        }
        if (operation is not (RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.TaskClaim) || task is null) return null;
        if (task.Owner != session || task.ClaimGeneration != Number(stored, "claimGeneration")) return Error(StaleClaim);
        return operation == RelayTools.TaskClaim ? TaskWorkflow.ExecutionRefusal(board, task, session, now, participant) : null;
    }

    private static RelayReply Claim(AggregateContext context, BoardTask task)
    {
        var args = context.Args;
        var board = new BoardState(context.State);
        var session = context.SessionId;
        var version = task.Version;
        if (TaskWorkflow.ExecutionRefusal(board, task, session, context.Now, context.ActingParticipant) is { } refusal) return refusal;
        // The claim must take every resource the packet declares, so no work runs while another session holds one.
        if (TaskWorkflow.DeclaredResourcesNeeded(task, args) is { } needed) return needed;
        if (TaskWorkflow.ResourceRefusal(board, task, args, Strings(args["resourceIds"])) is { } resources) return resources;
        var owned = task.Owner == session && task.LeaseUntil > context.Now;
        if (owned && !SameResources(task.ResourceIds, args["resourceIds"])) return Error(ResourceSetConflict, new { resourceIds = task.ResourceIds }, version);
        // A recovered action (its writer gone, see TaskActions.ApplyReport) no longer holds the task once its writer's lease has lapsed.
        var unresolved = TaskActions.HoldsTask(task);
        if (!owned && unresolved) return Error(Busy, new { holder = task.Owner, leaseUntil = task.LeaseUntilText, unresolved = true }, version);
        if (task.Owner.Length > 0 && task.Owner != session && (task.LeaseUntil > context.Now || unresolved)) return Error(Busy, new { holder = task.Owner, leaseUntil = task.LeaseUntilText, unresolved }, version);
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        if (!owned)
        {
            task.Owner = Required(args, "sessionId"); task.ClaimGeneration += 1; task.ClaimRequestId = Text(args, "requestId");
            task.LeaseUntil = context.Now.AddSeconds(Leases.Seconds(args)); task.Version = ++version;
            task.ResourceIds = args["resourceIds"]?.DeepClone() ?? new JsonArray();
            AttentionSources.OnClaim(context, task.Json);
        }
        return Ok(new { taskId = Text(args, "taskId"), newlyAcquired = !owned, claimGeneration = task.ClaimGeneration, leaseUntil = task.LeaseUntilText, resourceIds = task.ResourceIds }, version);
    }

    private static bool SameResources(JsonNode? held, JsonNode? requested) =>
        Strings(held).Order(StringComparer.Ordinal).SequenceEqual(Strings(requested).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>A milestone commit: action reports, shared facts/notes, a conversation message and the workflow, all under one expected version.</summary>
    private static RelayReply Update(AggregateContext context, BoardTask task)
    {
        var args = context.Args;
        var board = new BoardState(context.State);
        var version = task.Version;
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        var reports = context.Operation == RelayTools.ActionReport ? new JsonArray(args.DeepClone()) : args["actionReports"] as JsonArray ?? new JsonArray();
        if (context.Operation == RelayTools.TaskUpdate && task.Owner.Length > 0 && task.Owner != context.SessionId)
        {
            // An administrator may repair an abandoned packet with its own authenticated session.
            // Action writers and the next worker's ordinary claim remain separately fenced.
            var packetRecovery = context.Caller.IsAdministrator && task.Workflow is not null && args["workflow"] is JsonObject && !args.ContainsKey("actionReports") && !TaskActions.CarriesMilestone(args);
            if (!packetRecovery || task.LeaseUntil > context.Now) return Error(OwnerConflict);
            if (TaskActions.HasUnresolved(task)) return Error(UnresolvedAction);
        }
        var priorStatus = task.Actions.ToDictionary(x => x.Key, x => x.Value is JsonObject action ? new TaskAction(action).Status : "", StringComparer.Ordinal);
        var priorStranded = task.ActionRecords.Where(TaskActions.IsStranded).Select(action => action.ActionId).ToList();
        foreach (var report in reports)
            if (TaskActions.ApplyReport(context, task, report as JsonObject ?? throw new ArgumentException("Each action report must be an object.")) is { } failure) return failure;
        if (args["patch"] is JsonObject patch)
        {
            if (ByteSize(patch) > MaxSharedBytes) return Error(PayloadTooLarge);
            foreach (var fact in patch) task.Facts[fact.Key] = fact.Value?.DeepClone();
        }
        if (args["notes"] is JsonNode notes)
        {
            if (ByteSize(notes) > MaxSharedBytes) return Error(PayloadTooLarge);
            task.Facts[BoardTask.NotesFact] = notes.DeepClone();
        }
        if (args["message"] is JsonObject message && AddMessage(context, task, message) is { } unreadable) return unreadable;
        IReadOnlyList<string> unblocked = [];
        var priorWorkflow = task.Workflow?.Json.DeepClone().AsObject();
        if (args.ContainsKey("workflow"))
        {
            if (context.Operation != RelayTools.TaskUpdate || args["workflow"] is not JsonObject workflow) return Error(InvalidWorkflow);
            if (TaskWorkflow.Validate(workflow, task.Workflow, TaskActions.HasUnresolved(task)) is { } invalid) return Error(invalid);
            var incoming = new WorkflowRecord(workflow);
            var incomingRef = incoming.Packet?.AuthorityRef ?? "";
            var pinnedRef = Text(task.Json, "goalAuthorityRef");
            if (pinnedRef.Length > 0 && incomingRef != pinnedRef) return Error(GrantInvalid, new { reason = "immutable goal binding" });
            var goalId = pinnedRef.StartsWith(StandingPreferences.GrantPrefix, StringComparison.Ordinal) ? pinnedRef[StandingPreferences.GrantPrefix.Length..] : "";
            var goal = (board.Json["goalBindings"] as JsonObject)?[goalId] as JsonObject;
            var isRoot = goal is not null && Text(goal, "rootTaskId") == task.TaskId;
            if (isRoot && incoming.Packet?.Goal != Text(goal!, "purpose")) return Error(GrantInvalid, new { reason = "immutable goal" });
            var goalProblem = GoalAuthority.Problem(board, incoming, context.ActingParticipant, context.SessionId, context.Now);
            // The goal no longer grants anything (expired, revoked, root completed, binding gone): only the caller's membership is not the problem.
            var goalEnded = goalProblem is not null and not "participant";
            if (isRoot && incoming.State == "completed" && task.Workflow?.State != "completed" && !goalEnded && !context.Caller.IsAdministrator
                && (context.ActingParticipant is not { } owner || owner.SessionId != context.SessionId || owner.RepositoryId != context.Repository || owner.ParticipantId != Text(goal!, "ownerParticipantId"))) return Error(SessionDenied);
            // Winding a goal-bound task down (blocked or completed under its unchanged binding) uses none of the goal's authority, so it stays
            // possible once the goal ended, for an administrator's recovery, and for the session holding the claim. Reopening a completed root
            // is not winding down: it would make an ended goal live again. Ready (work under the goal) always needs the goal's authority.
            var windingDown = pinnedRef.Length > 0 && incoming.State is "blocked" or "completed"
                && !(isRoot && task.Workflow?.State == "completed" && incoming.State != "completed")
                && (goalEnded || context.Caller.IsAdministrator || task.Owner.Length > 0 && task.Owner == context.SessionId);
            if (goalProblem is not null && !windingDown) return Error(GrantInvalid, new { reason = goalProblem });
            if (incoming.State == "ready" && board.RequiresResources && !incoming.DeclaresResources) return Error(InvalidWorkflow, new { required = "packet.resources" });
            // A packet whose authorityRef names a standing grant is ready only while that grant exists, covers it and has not expired.
            if (incoming.State == "ready" && TaskWorkflow.GrantRefusal(board, incoming, context.Now) is { } grant) return grant;
            // The hub passes the dependencies it found in the archive table (archivedDependsOn); they count as completed from now on.
            TaskArchiving.Remember(board, Strings(args["archivedDependsOn"]).Intersect(incoming.DependsOn, StringComparer.Ordinal), context.Now);
            if (TaskWorkflow.CheckDependencies(board, task.TaskId, incoming) is { } dependency) return Error(dependency);
            if (incomingRef.StartsWith(StandingPreferences.GrantPrefix, StringComparison.Ordinal) && (board.Json["goalBindings"] as JsonObject)?[incomingRef[StandingPreferences.GrantPrefix.Length..]] is not null) task.Json["goalAuthorityRef"] = incomingRef;
            var completing = incoming.State == "completed" && (task.Workflow is not { } prior || prior.State != "completed");
            task.SetWorkflow(workflow);
            task.StampWaiting(incoming, context.Now);
            // A newly named target is asked to tell this board when it closes, so the task never waits on a session that is gone.
            if (incoming.TargetSessionId is { Length: > 0 } target && (priorWorkflow is null || new WorkflowRecord(priorWorkflow).TargetSessionId != target) && context.Key.Length > 0)
                context.Send(target, new SessionTargetWatch(context.Repository, target, context.Key, Text(args, "boardId", "default")));
            // Archiving counts from completion; reopening clears it.
            if (completing) task.CompletedAt = context.Now;
            else if (incoming.State != "completed") task.ClearCompletedAt();
            if (completing) unblocked = TaskWorkflow.Unblocked(board, task.TaskId);
        }
        // Attention items for this change commit with it: blocked, failed and review_ready to managers, next_work to each unblocked target.
        string[] NewlyIn(string status) => task.Actions.Where(x => x.Value is JsonObject action && new TaskAction(action).Status == status && priorStatus.GetValueOrDefault(x.Key) != status)
            .Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        // An abandoned action's external effects are unknown, so the managers hear of it like a failure.
        // A writer's real outcome that replaced abandoned (not a failure: that one is NewlyIn("failed")).
        var replaced = task.Actions.Where(x => x.Value is JsonObject action && priorStatus.GetValueOrDefault(x.Key) == TaskActions.Abandoned && new TaskAction(action).Status is not (TaskActions.Abandoned or "failed"))
            .Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        AttentionSources.OnTaskUpdate(context, task.Json, priorWorkflow, unblocked, NewlyIn("failed"), NewlyIn(TaskActions.Abandoned), replaced);
        // A stranded action this change reported or recovered no longer needs the managers.
        var settledStranded = priorStranded.Where(id => task.Action(id) is not { } action || !TaskActions.IsStranded(action)).ToList();
        if (settledStranded.Count > 0) AttentionSources.OnStrandedSettled(context, task.TaskId, settledStranded, board.TaskRevision + 1);
        StandingOrders.Detect(context, task, priorWorkflow is null ? null : new WorkflowRecord(priorWorkflow));
        task.UpdatedAt = context.Now; task.UpdatedBy = context.Caller.PrincipalId; task.Version = ++version;
        // An acknowledgment, not the task: echoing the whole task (up to its 256-message conversation) put it into the updater's context again
        // on every milestone, and the stored replay of each request kept a copy too. task_get still returns the task. The actions this
        // call reported are included, since the reporter needs their resulting status and restoration.
        var acknowledgment = new JsonObject { ["taskId"] = task.TaskId, ["version"] = version, ["updatedAt"] = task.UpdatedAtText };
        var reported = new JsonObject();
        foreach (var id in reports.OfType<JsonObject>().Select(report => Text(report, "actionId")).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
            if (task.Actions[id] is JsonNode action) reported[id] = action.DeepClone();
        if (reported.Count > 0) acknowledgment["actions"] = reported;
        if (unblocked.Count > 0) acknowledgment["unblocked"] = new JsonArray(unblocked.Take(RelayLimits.MaxPageSize).Select(id => (JsonNode)id).ToArray());
        if (unblocked.Count > RelayLimits.MaxPageSize) acknowledgment["unblockedTotal"] = unblocked.Count;
        return Ok(acknowledgment, version);
    }

    private static RelayReply SendTaskMessage(AggregateContext context, BoardTask task)
    {
        if (AddMessage(context, task, context.Args) is { } unreadable) return unreadable;
        var version = task.Version + 1;
        task.Version = version;
        return Ok(new { messageId = new TaskMessage(task.Messages.Last()!.AsObject()).MessageId }, version);
    }

    /// <summary>
    /// Appends to the shared task conversation and notifies the addressed session, or the current owner. Returns <c>PayloadTooLarge</c>,
    /// and changes nothing, when the entry escaped for the wire would not fit a <c>task_get</c> messages page.
    /// </summary>
    private static RelayReply? AddMessage(AggregateContext context, BoardTask task, JsonObject message)
    {
        var messages = task.Messages;
        if (messages.Count >= MaxConversation) throw new ArgumentException("Task conversation limit reached.");
        var nextVersion = task.Version + 1;
        var entry = TaskMessage.Create(Guid.NewGuid().ToString("N"), task.TaskId, SessionAggregate.MessageText(message), Text(message, "kind", "message"), message["replyTo"]?.DeepClone(),
            context.Caller.PrincipalId, context.SessionId, context.Now, nextVersion);
        // send_message alone: its copy of this field is the hub's, and a task_update's arguments are still the client's.
        if (context.Operation == RelayTools.SendMessage && context.Args[ServerArguments.ManagerOrigin] is not null)
            entry.Json[TaskMessage.ManagerGenerationKey] = Number(context.Args, ServerArguments.ManagerOrigin);
        if (CheckReadable(entry.Json) is { } unreadable) return unreadable;
        messages.Add(entry.Json);
        var recipient = Text(message, "recipientSessionId", task.Owner);
        if (recipient.Length > 0)
            context.Send(recipient, new MessageDelivery(InboxMessage.TaskChanged(context.Repository, entry.MessageId, Text(context.Args, "boardId", "default"), task.TaskId, nextVersion)));
        return null;
    }

    /// <summary>A bounded projection of a task: ownership, claim, workflow state and unresolved actions, without its detail sections.</summary>
    private static JsonObject Summary(BoardTask task)
    {
        var summary = new JsonObject
        {
            ["truncated"] = true, ["taskId"] = task.TaskId, ["version"] = task.Version, ["owner"] = task.Json[BoardTask.OwnerKey]?.DeepClone(),
            ["claimGeneration"] = task.ClaimGeneration, ["leaseUntil"] = task.Json[BoardTask.LeaseUntilKey]?.DeepClone(), ["resourceIds"] = task.ResourceIds?.DeepClone()
        };
        summary["changeRevision"] = task.ChangeRevision;
        if (task.Workflow is { } workflow) { summary["workflow"] = TaskWorkflow.Summary(workflow.Json); summary["workflowDetailFetchRequired"] = true; }
        var unresolved = new JsonArray();
        foreach (var (id, action) in (task.Json[BoardTask.ActionsKey] as JsonObject ?? new()).Select(x => (x.Key, Record: x.Value!.AsObject())).Where(x => TaskActions.IsUnresolved(new TaskAction(x.Record))))
            unresolved.Add(new JsonObject
            {
                ["actionId"] = id, ["attempt"] = action[TaskAction.AttemptKey]?.DeepClone(), ["status"] = action[TaskAction.StatusKey]?.DeepClone(),
                ["restoration"] = action[TaskAction.RestorationKey]?.DeepClone(), ["resourceIds"] = action[TaskAction.ResourceIdsKey]?.DeepClone()
            });
        summary["unresolvedActions"] = unresolved;
        summary["sections"] = new JsonArray(BoardTask.FactsKey, BoardTask.MessagesKey, BoardTask.ReportsKey, BoardTask.ActionsKey, BoardTask.AttemptHistoryKey);
        return summary;
    }

    public static bool ExceedsTaskCapacity(JsonObject state) => new BoardState(state).Tasks.Any(task => ByteSize(task.Json) > RelayLimits.MaxTaskBytes);

    /// <summary>A whole-task read that cannot fit the aggregate reply budget falls back to the task summary.</summary>
    public static RelayReply FitTaskRead(JsonObject state, string operation, JsonObject args, RelayReply result) =>
        !FitsWire(result, RelayLimits.AggregateReplyBytes) && operation == RelayTools.TaskGet && new BoardState(state).Task(Text(args, "taskId")) is { } task && Text(args, "section").Length == 0
            ? Ok(Summary(task), result.Version)
            : result;
}

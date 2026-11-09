using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

/// <summary>
/// The aggregates' attention item sources. Each raise and close is emitted through the source's own outbox, so it commits with the
/// change that caused it. Kinds come from structure only (a blocker kind, a failed action, a closure, a claim), never from prose.
/// Every effect carries a <c>revision</c> from one counter per key space, so a late or repeated effect can never reopen a closed item:
/// task items use the board's task revision, a lock's items its own counter, and proposals their raise (1) and close (2).
/// </summary>
internal static class AttentionSources
{
    /// <summary>The role, given at <c>session_open</c> and compared ignoring case, of the sessions that receive manager-bound items.</summary>
    public const string ManagerRole = SessionRoles.Manager;

    /// <summary>
    /// Board items after a <c>task_update</c> or <c>action_report</c>: <c>blocked</c> and <c>failed</c> (also for an action a recovery
    /// abandoned) to managers, <c>review_ready</c>. When a writer replaces abandoned with its real outcome (<paramref name="replacedActions"/>),
    /// the <c>failed</c> item is closed, or restated for the actions still failed or abandoned, so it never keeps saying effects are unknown.
    /// to managers on completion, and <c>next_work</c> to each unblocked task's target. Leaving a state closes the items it raised.
    /// </summary>
    public static void OnTaskUpdate(AggregateContext context, JsonObject task, JsonObject? prior, IReadOnlyList<string> unblocked, IReadOnlyList<string> failedActions,
        IReadOnlyList<string> abandonedActions, IReadOnlyList<string> replacedActions)
    {
        var tasks = ObjectAt(context.State, "tasks");
        var taskId = Text(task, "taskId");
        var revision = Number(context.State, "taskRevision") + 1;
        var workflow = task["workflow"] as JsonObject;
        var (was, now) = (prior is null ? "" : Text(prior, "state"), workflow is null ? "" : Text(workflow, "state"));
        var priority = AttentionWeights.PriorityLevel(workflow is null ? 0 : Number(workflow, "priority"));
        // Read only when an item needs them: the tasks that depend on this one, and whether one of them is claimed by another session,
        // which from a manager's view means this task blocks someone.
        var dependents = new Lazy<JsonObject[]>(() => tasks.Select(x => x.Value).OfType<JsonObject>().Where(other => other["workflow"] is JsonObject flow && TaskWorkflow.Dependencies(flow).Contains(taskId, StringComparer.Ordinal)).ToArray());
        bool Blocking() => dependents.Value.Any(other => Text(other, "owner") is { Length: > 0 } owner && owner != Text(task, "owner") && Date(other, "leaseUntil") > context.Now);
        AttentionContent Item(string kind) => new() { Kind = kind, TaskId = taskId, Priority = priority, Revision = revision };

        if (now == "blocked" && (was != "blocked" || BlockerIds(prior) != BlockerIds(workflow)))
        {
            var first = (workflow!["blockers"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(blocker => BlockerRank(Text(blocker, "kind"))).FirstOrDefault();
            ToGoalOwnerOrManagers(context, Item(AttentionKinds.Blocked) with
            {
                Blocker = first is null ? null : Text(first, "kind"), Note = first is null ? null : Text(first, "summary"), Blocking = Blocking(),
                Refs = dependents.Value.Select(other => Text(other, "taskId")).ToArray()
            });
        }
        else if (was == "blocked" && now != "blocked") ToGoalOwnerOrManagers(context, Kinds(Item(AttentionKinds.Blocked), AttentionKinds.Blocked));

        if (failedActions.Count > 0 || abandonedActions.Count > 0 || replacedActions.Count > 0)
        {
            // A raise replaces the item's note, so it states every action still failed or abandoned, not just this change's; none left closes it.
            var actions = ObjectAt(task, "actions");
            string[] Still(string status) => actions.Where(x => x.Value is JsonObject action && Text(action, "status") == status).Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
            (failedActions, abandonedActions) = (Still("failed"), Still(TaskActions.Abandoned));
            if (failedActions.Count == 0 && abandonedActions.Count == 0) ToGoalOwnerOrManagers(context, Kinds(Item(AttentionKinds.Failed), AttentionKinds.Failed));
        }
        if (failedActions.Count > 0 || abandonedActions.Count > 0)
        {
            var parts = new List<string>();
            if (failedActions.Count > 0) parts.Add("action " + string.Join(", ", failedActions) + " failed");
            if (abandonedActions.Count > 0) parts.Add("action " + string.Join(", ", abandonedActions) + " abandoned by recovery; its external effects are unknown");
            ToGoalOwnerOrManagers(context, Item(AttentionKinds.Failed) with { Note = string.Join("; ", parts), Blocking = Blocking() });
        }

        if (now == "completed" && was != "completed")
        {
            ToGoalOwnerOrManagers(context, Kinds(Item(AttentionKinds.Blocked), AttentionKinds.Blocked, AttentionKinds.Failed));
            ToGoalOwnerOrManagers(context, Item(AttentionKinds.ReviewReady) with { Note = "completed with " + ((workflow!["criteria"] as JsonArray)?.Count ?? 0) + " criteria passed; review the evidence" });
            // Related work and duplicate warnings were raised to the claimer; the task is closed now.
            if (Text(task, "owner") is { Length: > 0 } owner)
                Emit(context, owner, new AttentionSelection { Subject = taskId, Kinds = [AttentionKinds.RelatedWork, AttentionKinds.DuplicateWarning], Revision = revision });
            foreach (var id in unblocked)
            {
                if (tasks[id]?["workflow"] is not JsonObject next || Text(next, "targetSessionId") is not { Length: > 0 } target || target == context.SessionId) continue;
                Emit(context, target, new AttentionContent
                {
                    Kind = AttentionKinds.NextWork, TaskId = id, Priority = AttentionWeights.PriorityLevel(Number(next, "priority")), Note = "ready: " + taskId + " completed",
                    Refs = [taskId], Revision = revision
                });
            }
        }
        else if (was == "completed" && now != "completed") ToGoalOwnerOrManagers(context, Kinds(Item(AttentionKinds.ReviewReady), AttentionKinds.ReviewReady));
    }

    /// <summary>
    /// A new claim closes the task's <c>next_work</c> item at its target, whoever claimed it. (The hub closes the claimer's own item, from a
    /// next-work offer, after the claim.)
    /// </summary>
    public static void OnClaim(AggregateContext context, JsonObject task)
    {
        if (task["workflow"] is not JsonObject workflow || Text(workflow, "targetSessionId") is not { Length: > 0 } target) return;
        Emit(context, target, new AttentionSelection { TaskId = Text(task, "taskId"), Kinds = [AttentionKinds.NextWork], Revision = Number(context.State, "taskRevision") + 1 });
    }

    /// <summary>A claim rolled back (a resource was busy) offers the task to its target again, since the claim closed that offer.</summary>
    public static void OnUnclaim(AggregateContext context, JsonObject task)
    {
        if (task["workflow"] is not JsonObject workflow || Text(workflow, "state") != "ready" || Text(workflow, "targetSessionId") is not { Length: > 0 } target) return;
        Emit(context, target, new AttentionContent
        {
            Kind = AttentionKinds.NextWork, TaskId = Text(task, "taskId"), Priority = AttentionWeights.PriorityLevel(Number(workflow, "priority")), Note = "ready again: a claim was rolled back",
            Revision = Number(context.State, "taskRevision") + 1
        });
    }

    /// <summary>Proposals wait for an explicitly requested human-active boundary; they never wake manager sessions.</summary>
    public static void OnProposal(AggregateContext context, string proposalId, string note) { }

    /// <summary>An approved or rejected proposal's item is closed at the managers.</summary>
    public static void OnProposalDecided(AggregateContext context, string proposalId) =>
        ToManagers(context, new AttentionSelection { Subject = proposalId, Kinds = [AttentionKinds.StandingProposal], Revision = 2 });

    /// <summary>
    /// A <c>stranded</c> item to the managers for <paramref name="action"/>, which <paramref name="session"/> left unresolved when it closed.
    /// The action keeps its records and resource blocks, and the task's claim has lapsed, so the principal's other session (or an
    /// administrator) can recover it now. The item is about the action (its subject), so each stranded action has its own; it closes when
    /// the action is reported or recovered (<see cref="OnStrandedSettled"/>).
    /// </summary>
    public static void OnStranded(AggregateContext context, BoardTask task, TaskAction action, string session, long revision) =>
        ToGoalOwnerOrManagers(context, new AttentionContent
        {
            Kind = AttentionKinds.Stranded, TaskId = task.TaskId, Subject = action.ActionId, Priority = AttentionWeights.PriorityLevel(task.Workflow?.Priority ?? 0),
            Note = "action " + action.ActionId + " (attempt " + action.Attempt + ") was left unresolved when session " + session + " closed; recover it with action_report and recover: true",
            Revision = revision
        });

    /// <summary>Closes the <c>stranded</c> items of the actions a report or recovery settled.</summary>
    public static void OnStrandedSettled(AggregateContext context, string taskId, IEnumerable<string> actionIds, long revision)
    {
        foreach (var actionId in actionIds)
            ToGoalOwnerOrManagers(context, new AttentionSelection { TaskId = taskId, Subject = actionId, Kinds = [AttentionKinds.Stranded], Revision = revision });
    }

    /// <summary>
    /// A lock's due check, run twice per lease while its holder has an action in flight. Shortly before the lease ends the holder gets one
    /// <c>claim_expiring</c> item. When the lease has ended with the action still unresolved, that item closes and an <c>interrupted</c>
    /// item goes to the holder and to the managers. <paramref name="resourceId"/> is the lock's key.
    /// </summary>
    public static void OnLeaseCheck(AggregateContext context, string resourceId)
    {
        var state = context.State;
        var holder = Text(state, LockState.HolderKey);
        var lease = Text(state, LockState.LeaseUntilKey);
        if (holder.Length == 0 || lease.Length == 0) return;
        var action = (state[LockState.BlockersKey] as JsonObject ?? new JsonObject()).FirstOrDefault(x => x.Value is JsonObject blocker && Text(blocker, Blocker.SessionIdKey) == holder);
        if (action.Key is null) return;
        var (boardId, taskId, actionId) = CoordinationStateMachine.ParseActionKey(action.Key);
        // Remembered so the closes name the same task: a close guards only the key it names, and the raises carry the task.
        state[LockState.WarnedTaskKey] = new JsonObject { [LockState.WarnedBoardIdKey] = boardId, [LockState.WarnedTaskIdKey] = taskId };
        AttentionContent Item(string kind, string note) => new()
        {
            Kind = kind, TaskId = taskId, BoardId = boardId, Subject = resourceId, Priority = AttentionWeights.High, Note = note, Revision = NextLockRevision(state)
        };
        if (Date(state, LockState.LeaseUntilKey) > context.Now)
        {
            if (Text(state, LockState.WarnedLeaseKey) == lease) return;
            state[LockState.WarnedLeaseKey] = lease;
            Emit(context, holder, Item(AttentionKinds.ClaimExpiring, "claim on " + resourceId + " ends " + lease + " with an action in flight; renew it"));
            return;
        }
        if (Text(state, LockState.InterruptedLeaseKey) == lease) return;
        state[LockState.InterruptedLeaseKey] = lease;
        if (state.ContainsKey(LockState.WarnedLeaseKey))
            Emit(context, holder, WarnedClose(state, resourceId, AttentionKinds.ClaimExpiring));
        state[LockState.WarnedLeaseKey] = lease;
        var note = "claim on " + resourceId + " ended with action " + actionId + " unresolved; report its outcome";
        Emit(context, holder, Item(AttentionKinds.Interrupted, note));
        ToManagers(context, Item(AttentionKinds.Interrupted, note));
    }

    /// <summary>
    /// A renewed or released lease, or a resolved action, closes the holder's <c>claim_expiring</c> item, if one was raised; a resolved
    /// action also closes the <c>interrupted</c> items at the holder and the managers.
    /// </summary>
    public static void OnLeaseSettled(AggregateContext context, string holder, string resourceId)
    {
        var state = context.State;
        var interrupted = state.Remove(LockState.InterruptedLeaseKey);
        if (!state.Remove(LockState.WarnedLeaseKey) || holder.Length == 0) { state.Remove(LockState.WarnedTaskKey); return; }
        Emit(context, holder, WarnedClose(state, resourceId, AttentionKinds.ClaimExpiring, AttentionKinds.Interrupted));
        if (interrupted) ToManagers(context, WarnedClose(state, resourceId, AttentionKinds.Interrupted));
        state.Remove(LockState.WarnedTaskKey);
    }

    /// <summary>A close for the lock's warned items, naming the task they were raised for.</summary>
    private static AttentionSelection WarnedClose(JsonObject state, string resourceId, params string[] kinds)
    {
        var close = new AttentionSelection { Subject = resourceId, Kinds = kinds, Revision = NextLockRevision(state) };
        return state[LockState.WarnedTaskKey] is JsonObject task ? close with { TaskId = Text(task, LockState.WarnedTaskIdKey), BoardId = Text(task, LockState.WarnedBoardIdKey) } : close;
    }

    /// <summary>
    /// When a lock with an action in flight should run its next lease check: shortly before the lease ends, then when it ends; null when
    /// both are done for this lease or nothing is in flight.
    /// </summary>
    public static DateTimeOffset? LeaseWarningAt(JsonObject lockState)
    {
        if (Text(lockState, "holder").Length == 0 || lockState["blockers"] is not JsonObject { Count: > 0 } || !DateTimeOffset.TryParse(Text(lockState, "leaseUntil"), out var lease)) return null;
        var current = Text(lockState, "leaseUntil");
        return Text(lockState, "warnedLease") != current ? lease.AddSeconds(-RelayLimits.ClaimExpiringLeadSeconds)
            : Text(lockState, "interruptedLease") != current ? lease
            : null;
    }

    private static long NextLockRevision(JsonObject state)
    {
        var revision = Number(state, LockState.AttentionRevisionKey) + 1;
        state[LockState.AttentionRevisionKey] = revision;
        return revision;
    }

    /// <summary>A provider rejection ranks first, and a coordinator hold or a human decision last, so the item takes the most urgent blocker's kind.</summary>
    private static int BlockerRank(string kind) => kind == AttentionWeights.ProviderRejection ? 0 : (kind is AttentionWeights.CoordinatorHold or AttentionWeights.HumanDecision) ? 2 : 1;

    private static string BlockerIds(JsonObject? workflow) =>
        string.Join("\n", (workflow?["blockers"] as JsonArray ?? []).OfType<JsonObject>().Select(blocker => Text(blocker, "id")).Order(StringComparer.Ordinal));

    /// <summary>The close of <paramref name="kinds"/> for the task an item was raised for, at its revision.</summary>
    private static AttentionSelection Kinds(AttentionContent item, params string[] kinds) => new() { TaskId = item.TaskId, Revision = item.Revision, Kinds = kinds };

    /// <summary>
    /// Raises <paramref name="item"/> at one session through this aggregate's outbox; a standing order's raise names its order and run. The
    /// item is on the call's board unless it names its own.
    /// </summary>
    internal static void Emit(AggregateContext context, string sessionId, AttentionContent item, string? orderId = null, long? orderRun = null) =>
        context.Send(sessionId, new AttentionRaise(context.Repository, item.BoardId is null && CallBoard(context) is { } board ? item with { BoardId = board } : item, orderId, orderRun));

    /// <summary>Closes the items <paramref name="close"/> names at one session; they are on the call's board unless it names its own.</summary>
    internal static void Emit(AggregateContext context, string sessionId, AttentionSelection close) =>
        context.Send(sessionId, new AttentionClose(context.Repository, close.BoardId is null && CallBoard(context) is { } board ? close with { BoardId = board } : close));

    /// <summary>
    /// Raises <paramref name="item"/> at every manager session in the repository but the one whose call caused it, on that call's board
    /// when it names one (<see cref="AttentionRoute"/>).
    /// </summary>
    internal static void ToManagers(AggregateContext context, AttentionContent item) =>
        context.Send(AggregateKinds.ControlKey, AttentionRoute.Raising(ManagerRole, new AttentionRaise(context.Repository, CallBoard(context) is { } board ? item with { BoardId = board } : item), context.SessionId));

    /// <summary>The route role prefix naming a goal owner's private native participant ID, resolved at the control directory.</summary>
    internal const string GoalOwnerRole = "goal-owner:";

    private static string? GoalOwner(AggregateContext context, string? taskId)
    {
        if (taskId is null || new BoardState(context.State).Task(taskId) is not { } task) return null;
        var reference = Text(task.Json, "goalAuthorityRef");
        if (!reference.StartsWith(StandingPreferences.GrantPrefix, StringComparison.Ordinal)) return null;
        return (context.State["goalBindings"] as JsonObject)?[reference[StandingPreferences.GrantPrefix.Length..]] is JsonObject goal ? Text(goal, "ownerParticipantId") : null;
    }

    private static void ToGoalOwnerOrManagers(AggregateContext context, AttentionContent item)
    {
        if (GoalOwner(context, item.TaskId) is not { Length: > 0 } owner) { ToManagers(context, item); return; }
        // The control directory falls back to the managers when the owner has no current native session; the caller is excluded only then.
        context.Send(AggregateKinds.ControlKey, AttentionRoute.Raising(GoalOwnerRole + owner, new AttentionRaise(context.Repository, CallBoard(context) is { } board ? item with { BoardId = board } : item), context.SessionId));
    }

    private static void ToGoalOwnerOrManagers(AggregateContext context, AttentionSelection close)
    {
        if (GoalOwner(context, close.TaskId) is not { Length: > 0 } owner) { ToManagers(context, close); return; }
        // The control directory closes at the owner's current session and at every manager, whichever the raise reached.
        context.Send(AggregateKinds.ControlKey, AttentionRoute.Closing(GoalOwnerRole + owner, new AttentionClose(context.Repository, CallBoard(context) is { } board ? close with { BoardId = board } : close)));
    }

    /// <summary>Closes the items <paramref name="close"/> names at every manager session in the repository, on the call's board when it names one.</summary>
    internal static void ToManagers(AggregateContext context, AttentionSelection close) =>
        context.Send(AggregateKinds.ControlKey, AttentionRoute.Closing(ManagerRole, new AttentionClose(context.Repository, CallBoard(context) is { } board ? close with { BoardId = board } : close)));

    /// <summary>The board the call that caused an item names, if it names one.</summary>
    private static string? CallBoard(AggregateContext context) => Text(context.Args, "boardId") is { Length: > 0 } board ? board : null;
}

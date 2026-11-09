using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// External action records. An action is recorded before the agent invokes it and stays unresolved until a
/// report settles its outcome and any restoration obligation. Unresolved actions block claims and resources.
/// </summary>
internal static class TaskActions
{
    private const int MaxActionIdLength = 128;
    private const int MaxActions = 128;
    private const int MaxHistory = 256;
    private static readonly IReadOnlyList<string> Outcomes = ActionOutcomes.All;

    /// <summary>
    /// The status a recovery gives an action whose writer is gone without reporting it: terminal, and not an outcome. Only a recovery sets
    /// it; a report never can.
    /// </summary>
    public const string Abandoned = "abandoned";

    public static bool IsUnresolved(TaskAction action) =>
        action.Status is "invoking" or "started" or "unknown" or "incomplete" || action.Restoration is "pending" or "blocked";

    public static bool HasUnresolved(BoardTask task) => task.ActionRecords.Any(IsUnresolved);

    /// <summary>
    /// Whether an unresolved action still holds its task against another session's claim or a release. A recovered one does not: its
    /// writer is gone, and its pending restoration still blocks new actions, completion and its resources until it is resolved. One its
    /// writer took back (an override from the writer's own session) holds the task again, like any other action of its writer.
    /// </summary>
    public static bool HoldsTask(BoardTask task) => task.ActionRecords.Any(action => IsUnresolved(action) && action.Recovery is null);

    /// <summary>
    /// A closed session's unresolved actions on <paramref name="task"/> (those it answers for: it wrote them, or recovered them): each is
    /// marked <c>ownerClosed</c> once, and the task's claim, while that session holds it, lapses now. The principal's other session may then
    /// recover them at once (<see cref="RecoveryRefusal"/>) rather than waiting out the lease, which the closed session can no longer renew.
    /// The owner, the action records and their resource blocks stay, and nothing about the outcome is assumed. Returns whether anything
    /// changed, so a repeated notice changes nothing.
    /// </summary>
    internal static (bool Changed, IReadOnlyList<TaskAction> Stranded) Strand(BoardTask task, string session, DateTimeOffset now)
    {
        var answered = task.ActionRecords.Where(action => IsUnresolved(action) && OwnerSession(action) == session).ToList();
        if (answered.Count == 0) return (false, []);
        var stranded = answered.Where(action => !action.OwnerClosed).ToList();
        foreach (var action in stranded) action.MarkOwnerClosed(session, now);
        var changed = stranded.Count > 0;
        if (task.Owner == session && task.LeaseUntil > now)
        {
            task.LeaseUntil = now;
            changed = true;
        }
        return (changed, stranded);
    }

    /// <summary>
    /// Whether <paramref name="action"/> is stranded now: unresolved, and still answered for by the session seen to close. A report that
    /// settles it, or a recovery by another session, ends that.
    /// </summary>
    internal static bool IsStranded(TaskAction action) => IsUnresolved(action) && action.OwnerClosedSessionId is { Length: > 0 } closed && OwnerSession(action) == closed;

    /// <summary>
    /// The bulky parts of a report or an action record: its report text and its tool binding. Dropping one keeps the record itself. A
    /// report keeps its body under the same key an action does.
    /// </summary>
    private static readonly string[] BodyFields = [TaskAction.ReportKey, TaskAction.BindingKey];

    /// <summary>
    /// Brings a task back under <see cref="RelayLimits.MaxTaskBytes"/> for a report that settles an action, which must always land (an
    /// unsettled action holds its task and locks for good). Every history a task keeps is trimmed, bodies before entries and oldest first:
    /// the bodies of older reports, of superseded attempts (<c>attemptHistory</c>, whole action copies) and of other settled actions; then
    /// the oldest reports, superseded attempts and messages. The newest entry of each list keeps its body, and an unresolved action is never
    /// touched. Counts stay bounded by their own limits (<see cref="MaxHistory"/>, <see cref="MaxActions"/>); this bounds their bytes.
    /// </summary>
    private static void MakeRoom(BoardTask task, JsonArray history, string settledId)
    {
        var size = ByteSize(task.Json);
        bool Over() => size > RelayLimits.MaxTaskBytes;
        void DropBodies(IEnumerable<JsonObject> entries)
        {
            foreach (var entry in entries)
            {
                if (!Over()) return;
                foreach (var field in BodyFields)
                    if (entry[field] is { } body) { size -= ByteSize(body) - 4; entry[field] = null; }
            }
            size = ByteSize(task.Json);
        }
        void DropOldest(JsonArray? entries)
        {
            while (entries is { Count: > 1 } && Over()) { size -= ByteSize(entries[0]) + 1; entries.RemoveAt(0); }
            size = ByteSize(task.Json);
        }
        static IEnumerable<JsonObject> AllButNewest(JsonArray? entries) => entries is null ? [] : entries.Take(entries.Count - 1).OfType<JsonObject>();
        var attempts = task.AttemptHistoryIfAny;
        DropBodies(AllButNewest(history));
        DropBodies(AllButNewest(attempts));
        DropBodies(task.Actions.Where(entry => entry.Key != settledId && entry.Value is JsonObject action && !IsUnresolved(new TaskAction(action)))
            .Select(entry => entry.Value!.AsObject()).OrderBy(action => new TaskAction(action).StartedAt).ToArray());
        DropOldest(history);
        DropOldest(attempts);
        DropOldest(task.MessagesIfAny);
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "canceled" or Abandoned;

    /// <summary>The outcomes a recovery may give: it settles the action, so it can never leave it in flight under a session that did not start it.</summary>
    private static bool IsFinalOutcome(string outcome) => outcome is "completed" or "failed" or "canceled";

    /// <summary>The session that answers for an action: the one that recovered it, else its writer.</summary>
    private static string OwnerSession(TaskAction action) => action.Recovery is { } recovery ? recovery.SessionId : action.SessionId;

    /// <summary>
    /// Tells <paramref name="sessionId"/>'s session that it now answers for attempt <paramref name="attempt"/> of action
    /// <paramref name="actionId"/> (<paramref name="open"/>), or no longer does; the session tells the directory when that changes, so a
    /// full directory never expires a session that owns work.
    /// </summary>
    private static void Track(AggregateContext context, string sessionId, string actionId, long attempt, bool open)
    {
        if (sessionId.Length == 0) return;
        context.Send(sessionId, new ActionWorkReport(context.Repository, sessionId, Text(context.Args, "boardId", AggregateKinds.DefaultBoard), Text(context.Args, "taskId"),
            actionId, attempt, open, context.Now));
    }

    public static RelayReply Begin(AggregateContext context, BoardTask task)
    {
        var args = context.Args;
        var board = new BoardState(context.State);
        var session = context.SessionId;
        var version = task.Version;
        var id = Required(args, "actionId");
        var fingerprint = Required(args, "inputFingerprint");
        var actions = task.Actions;
        TaskAction? previousAttempt = null;
        if (id.Length > MaxActionIdLength) return Error(InvalidArguments);
        if (task.Workflow is { } goalWorkflow && GoalAuthority.Problem(board, goalWorkflow, context.ActingParticipant, session, context.Now) is { } goalProblem) return Error(GrantInvalid, new { reason = goalProblem }, version);
        if (task.Action(id) is { } prior)
        {
            if (prior.InputFingerprint != fingerprint) return Error(ActionConflict);
            if (!Flag(args, "explicitRetry")) return Ok(new { action = prior.Json, replayAllowed = false, existing = true }, version);
            if (prior.Restoration is "pending" or "blocked") return Error(RestorationRequired);
            if (!IsTerminal(prior.Status)) return Error(ActionUncertain);
            if (Number(args, "attempt") != prior.Attempt + 1) return Error(AttemptConflict);
            previousAttempt = prior;
        }
        if (TaskWorkflow.ExecutionRefusal(board, task, session, context.Now, context.ActingParticipant) is { } refusal) return refusal;
        if (previousAttempt is not null) task.AttemptHistory.Add(previousAttempt.Json.DeepClone());
        if (task.Owner != session || task.LeaseUntil <= context.Now) return Error(ClaimRequired);
        // The task's claim must cover the packet's declared resources (it may have changed since the claim); the hub checks they are held.
        if (TaskWorkflow.DeclaredResourcesNeeded(task, args) is { } needed) return needed;
        if (TaskWorkflow.ResourceRefusal(board, task, args, task.ClaimedResources) is { } resources) return resources;
        // The hub found a declared resource's lock not held by this session: nothing is recorded, so no action starts unguarded.
        if (args[Operations.LockRefusalField] is JsonObject locks)
            return Error(Text(locks, "code") == Busy ? Busy : ClaimRequired, new { resourceId = Text(locks, "resourceId"), replayAllowed = false }, version);
        if (actions.Any(x => x.Key != id && IsUnresolved(new TaskAction(x.Value!.AsObject())))) return Error(ActionUncertain);
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        var trigger = Text(args, "triggerId");
        if (trigger.Length > 0 && actions.Any(x => x.Key != id && new TaskAction(x.Value!.AsObject()).TriggerId == trigger)) return Error(TriggerConsumed);
        if (actions.Count >= MaxActions || task.AttemptHistory.Count >= MaxHistory) return Error(CapacityExceeded);
        var requiresRestoration = Flag(args, "requiresRestoration");
        var action = TaskAction.Begun(id, fingerprint, trigger, Number(args, "attempt", 1), "invoking", session, context.Caller.PrincipalId, Number(args, "bindingVersion"),
            args["binding"]?.DeepClone(), context.Now, requiresRestoration ? "pending" : "not_required", task.ResourceIds?.DeepClone(), requiresRestoration);
        actions[id] = action.Json; task.Version = ++version;
        // Retrying a failed or abandoned action replaces it: the managers' failed item is restated for what is still failed, or closed.
        if (previousAttempt is not null && previousAttempt.Status is "failed" or Abandoned)
            AttentionSources.OnTaskUpdate(context, task.Json, task.Workflow?.Json, [], [], [], [id]);
        foreach (var resource in action.Resources)
            context.Send(resource, new ActionBlock(context.Repository, id, CoordinationStateMachine.ActionKey(args, id, action.Attempt), session));
        Track(context, session, id, action.Attempt, open: true);
        return Ok(new { action = action.Json, replayAllowed = true, existing = false }, version);
    }

    /// <summary>
    /// Applies one action report to the task. Returns a failure, or null when the report was recorded.
    /// <list type="bullet">
    /// <item>The action's own writer (principal and session), or the session that recovered it, reports its outcome and restoration.</item>
    /// <item>An administrator's cleanup (<c>restoration: accepted</c>, no outcome) accepts its restoration and keeps its status.</item>
    /// <item>A recovery (<c>recover: true</c>, <c>action_report</c> only) is for an action whose writer is gone: an administrator, or the
    /// action's own principal on another session once the writer's claim has lapsed, takes it over. Without an outcome (an administrator
    /// can never give one) an action still in flight becomes <see cref="Abandoned"/>. The recovery is recorded with who and when; a required
    /// restoration stays pending, and the recovering session reports it. A recovered action no longer holds its task against claims.</item>
    /// <item>An abandoned action's own principal (its writer, still there after all, or a new session of it) may replace
    /// <see cref="Abandoned"/> with the real terminal outcome. The action keeps <c>overridden</c> (the recovery it replaced, who and when),
    /// and answers to its writer's session again.</item>
    /// </list>
    /// </summary>
    public static RelayReply? ApplyReport(AggregateContext context, BoardTask task, JsonObject update)
    {
        var args = context.Args;
        var caller = context.Caller;
        var session = context.SessionId;
        var id = Required(update, "actionId");
        if (ByteSize(update) > RelayLimits.MaxReportBytes) return Error(PayloadTooLarge);
        if (task.Action(id) is not { } currentAction) return Error(ActionUnknown);
        if (!update.ContainsKey("attempt") || Number(update, "attempt") < 1) return Error(AttemptRequired);
        var attempt = Number(update, "attempt");
        var historical = attempt != currentAction.Attempt;
        var action = historical
            ? task.AttemptHistory.OfType<JsonObject>().Select(copy => new TaskAction(copy)).SingleOrDefault(copy => copy.ActionId == id && copy.Attempt == attempt)
            : currentAction;
        if (action is null) return Error(AttemptConflict);
        var originalWriter = action.PrincipalId == caller.PrincipalId && action.SessionId == session;
        var recoverer = action.Recovery is { } recovered && recovered.PrincipalId == caller.PrincipalId && recovered.SessionId == session;
        // Administrative acceptance is only a cleanup decision. It cannot supply an outcome.
        var adminAcceptance = caller.IsAdministrator && context.Operation == RelayTools.ActionReport && Text(update, "restoration") == "accepted" && !update.ContainsKey("outcome") && !CarriesMilestone(args);
        var recovery = context.Operation == RelayTools.ActionReport && !historical && Flag(update, "recover");
        var status = action.Status;
        // The writer was there after all (an administrator abandoned it during a live lease, say): its real outcome replaces abandoned.
        var overriding = !recovery && !historical && status == Abandoned && action.PrincipalId == caller.PrincipalId && update.ContainsKey("outcome");
        if (recovery) { if (RecoveryRefusal(context, task, action, update) is { } refused) return refused; }
        else if (!originalWriter && !recoverer && !adminAcceptance && !overriding) return Error(ActionDenied);
        // A cleanup without an outcome keeps the action's status, whatever it is (an action still invoking included); a recovery without
        // one abandons an action still in flight.
        var outcome = recovery && !update.ContainsKey("outcome") ? (IsTerminal(status) ? status : Abandoned) : adminAcceptance ? status : Required(update, "outcome");
        if (recovery && update.ContainsKey("outcome") && !IsFinalOutcome(outcome) && outcome != Abandoned) return Error(InvalidOutcome, new { recovery = true, allowed = new[] { "completed", "failed", "canceled" } });
        var kept = (adminAcceptance || recovery) && outcome == status;
        if (!kept && !Outcomes.Contains(outcome) && !(recovery && outcome == Abandoned)) return Error(InvalidOutcome);
        var restoration = update.ContainsKey("restoration") ? Text(update, "restoration") : action.Restoration;
        if (!Restorations.All.Contains(restoration)) return Error(InvalidRestoration);
        if (Text(update, "restoration") == "accepted" && action.Restoration != "accepted" && !caller.IsAdministrator) return Error(AdministratorRequired);
        var history = task.Reports;
        // A report on an unresolved action must always land, or the action holds its task and locks for good: it evicts the oldest report.
        var settling = IsUnresolved(action);
        if (history.Count >= MaxHistory && !settling) return Error(CapacityExceeded);
        while (history.Count >= MaxHistory) history.RemoveAt(0);
        var entry = TaskReport.Create(id, attempt, outcome, restoration, update["report"]?.DeepClone(), context.Now, caller.PrincipalId, session);
        if (recovery) entry.Recovered = true;
        if (overriding && IsTerminal(outcome)) entry.Overrode = Abandoned;
        if (historical)
        {
            // A late report on a superseded attempt is kept for the record; it changes no current state.
            entry.Historical = true;
            history.Add(entry.Json);
            return null;
        }
        // Only a terminal outcome replaces abandoned: the action was already released, so it never becomes in flight again.
        if (IsTerminal(status) && status != outcome && !(overriding && IsTerminal(outcome))) return Error(ActionConflict);
        var priorRestoration = action.Restoration;
        var requiresRestoration = action.RequiresRestoration || priorRestoration is "pending" or "blocked";
        if (requiresRestoration && restoration == "not_required") return Error(RestorationRequired);
        if (priorRestoration is "verified" or "accepted" && restoration != priorRestoration) return Error(RestorationResolved);
        if (!IsUnresolved(action) && restoration is "pending" or "blocked") return Error(RestorationResolved);
        var (priorOwner, wasUnresolved) = (OwnerSession(action), IsUnresolved(action));
        action.Status = outcome;
        action.Restoration = restoration;
        action.Reported(update["report"]?.DeepClone(), context.Now);
        if (overriding && status != outcome)
        {
            action.Overridden = ActionOverride.Create(Abandoned, action.RecoveryCopy, caller.PrincipalId, session, context.Now);
            action.Recovery = null;
            // Taken back from another session of the principal: that session answers for it now (the writer's is gone), so it can still
            // report an owed restoration, and the directory keeps that session while it does.
            if (session != action.SessionId) action.Recovery = ActionRecovery.Create(caller.PrincipalId, session, caller.IsAdministrator, context.Now, status, takenBack: true);
        }
        if (recovery) action.Recovery = ActionRecovery.Create(caller.PrincipalId, session, caller.IsAdministrator, context.Now, status);
        history.Add(entry.Json);
        if (settling) MakeRoom(task, history, id);
        var actionKey = CoordinationStateMachine.ActionKey(args, id, action.Attempt);
        var (owner, unresolved) = (OwnerSession(action), IsUnresolved(action));
        if (wasUnresolved && (!unresolved || owner != priorOwner)) Track(context, priorOwner, id, action.Attempt, open: false);
        // A new owner is told at once. The same owner's record is refreshed at most every SessionWorkRefreshHours, so a long-running action
        // stays tracked without a directory write on every progress report.
        if (unresolved && (owner != priorOwner || !wasUnresolved || action.TrackedAt <= context.Now.AddHours(-RelayLimits.SessionWorkRefreshHours)))
        {
            Track(context, owner, id, action.Attempt, open: true);
            action.TrackedAt = context.Now;
        }
        // An administrator unblocks another principal's action only through cleanup: acceptance, a recovery, or a report on one it recovered.
        var administrativeAcceptance = caller.IsAdministrator && (adminAcceptance || recovery || recoverer);
        if (!unresolved)
            foreach (var resource in action.Resources)
                context.Send(resource, new ActionUnblock(context.Repository, id, actionKey, action.SessionId, administrativeAcceptance));
        return null;
    }

    /// <summary>
    /// Why this caller may not recover the action, or null. Another principal must be an administrator, and gives no outcome; the action's
    /// own principal on another session must wait until the claim of the session that answers for it (its writer, or the session that
    /// recovered it) has lapsed, since until then that session is still there to report. A recovery changes no shared task content, and
    /// an action already resolved has nothing to recover.
    /// </summary>
    private static RelayReply? RecoveryRefusal(AggregateContext context, BoardTask task, TaskAction action, JsonObject update)
    {
        var caller = context.Caller;
        var samePrincipal = action.PrincipalId == caller.PrincipalId;
        if (!samePrincipal && (!caller.IsAdministrator || update.ContainsKey("outcome")) || CarriesMilestone(context.Args)) return Error(ActionDenied);
        if (!IsUnresolved(action)) return Error(ActionConflict, new { status = action.Status, restoration = action.Restoration });
        var (writer, answering, owner) = (action.SessionId, OwnerSession(action), task.Owner);
        if (!caller.IsAdministrator && context.SessionId != answering && owner.Length > 0 && (owner == writer || owner == answering) && task.LeaseUntil > context.Now)
            return Error(OwnerConflict, new { holder = owner, leaseUntil = task.LeaseUntilText });
        return null;
    }

    /// <summary>Whether a request also changes shared task content, which administrative cleanup paths may not do.</summary>
    public static bool CarriesMilestone(JsonObject args) => args.ContainsKey("patch") || args.ContainsKey("notes") || args.ContainsKey("message");
}

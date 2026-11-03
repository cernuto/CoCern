using System.Text.Json.Nodes;
using CoCern.Contracts;
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

    /// <summary>When the board last told the control directory that the action's session answers for it.</summary>
    private const string TrackedAtField = "trackedAt";

    public static bool IsUnresolved(JsonObject action) =>
        Text(action, "status") is "invoking" or "started" or "unknown" or "incomplete" || Text(action, "restoration") is "pending" or "blocked";

    public static bool HasUnresolved(JsonObject task) => (task["actions"] as JsonObject ?? new()).Any(x => IsUnresolved(x.Value!.AsObject()));

    /// <summary>
    /// Whether an unresolved action still holds its task against another session's claim or a release. A recovered one does not: its
    /// writer is gone, and its pending restoration still blocks new actions, completion and its resources until it is resolved. One its
    /// writer took back (an override from the writer's own session) holds the task again, like any other action of its writer.
    /// </summary>
    public static bool HoldsTask(JsonObject task) =>
        (task["actions"] as JsonObject ?? new()).Any(x => x.Value is JsonObject action && IsUnresolved(action) && action["recovery"] is not JsonObject);

    /// <summary>
    /// A closed session's unresolved actions on <paramref name="task"/> (those it answers for: it wrote them, or recovered them): each is
    /// marked <c>ownerClosed</c> once, and the task's claim, while that session holds it, lapses now. The principal's other session may then
    /// recover them at once (<see cref="RecoveryRefusal"/>) rather than waiting out the lease, which the closed session can no longer renew.
    /// The owner, the action records and their resource blocks stay, and nothing about the outcome is assumed. Returns whether anything
    /// changed, so a repeated notice changes nothing.
    /// </summary>
    internal static bool Strand(JsonObject task, string session, DateTimeOffset now)
    {
        var answered = (task["actions"] as JsonObject ?? new()).Select(entry => entry.Value).OfType<JsonObject>()
            .Where(action => IsUnresolved(action) && OwnerSession(action) == session).ToList();
        if (answered.Count == 0) return false;
        var changed = false;
        foreach (var action in answered.Where(action => action["ownerClosed"] is null))
        {
            action["ownerClosed"] = new JsonObject { ["sessionId"] = session, ["at"] = now.ToString("O") };
            changed = true;
        }
        if (Text(task, "owner") == session && Date(task, "leaseUntil") > now)
        {
            task["leaseUntil"] = now.ToString("O");
            changed = true;
        }
        return changed;
    }

    /// <summary>The bulky parts of a report or an action record: its report text and its tool binding. Dropping one keeps the record itself.</summary>
    private static readonly string[] BodyFields = ["report", "binding"];

    /// <summary>
    /// Brings a task back under <see cref="RelayLimits.MaxTaskBytes"/> for a report that settles an action, which must always land (an
    /// unsettled action holds its task and locks for good). Every history a task keeps is trimmed, bodies before entries and oldest first:
    /// the bodies of older reports, of superseded attempts (<c>attemptHistory</c>, whole action copies) and of other settled actions; then
    /// the oldest reports, superseded attempts and messages. The newest entry of each list keeps its body, and an unresolved action is never
    /// touched. Counts stay bounded by their own limits (<see cref="MaxHistory"/>, <see cref="MaxActions"/>); this bounds their bytes.
    /// </summary>
    private static void MakeRoom(JsonObject task, JsonArray history, string settledId)
    {
        var size = ByteSize(task);
        bool Over() => size > RelayLimits.MaxTaskBytes;
        void DropBodies(IEnumerable<JsonObject> entries)
        {
            foreach (var entry in entries)
            {
                if (!Over()) return;
                foreach (var field in BodyFields)
                    if (entry[field] is { } body) { size -= ByteSize(body) - 4; entry[field] = null; }
            }
            size = ByteSize(task);
        }
        void DropOldest(JsonArray? entries)
        {
            while (entries is { Count: > 1 } && Over()) { size -= ByteSize(entries[0]) + 1; entries.RemoveAt(0); }
            size = ByteSize(task);
        }
        static IEnumerable<JsonObject> AllButNewest(JsonArray? entries) => entries is null ? [] : entries.Take(entries.Count - 1).OfType<JsonObject>();
        var attempts = task["attemptHistory"] as JsonArray;
        DropBodies(AllButNewest(history));
        DropBodies(AllButNewest(attempts));
        DropBodies(ObjectAt(task, "actions").Where(entry => entry.Key != settledId && entry.Value is JsonObject action && !IsUnresolved(action))
            .Select(entry => entry.Value!.AsObject()).OrderBy(action => Date(action, "startedAt")).ToArray());
        DropOldest(history);
        DropOldest(attempts);
        DropOldest(task["messages"] as JsonArray);
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "canceled" or Abandoned;

    /// <summary>The outcomes a recovery may give: it settles the action, so it can never leave it in flight under a session that did not start it.</summary>
    private static bool IsFinalOutcome(string outcome) => outcome is "completed" or "failed" or "canceled";

    /// <summary>The session that answers for an action: the one that recovered it, else its writer.</summary>
    private static string OwnerSession(JsonObject action) => action["recovery"] is JsonObject recovery ? Text(recovery, "sessionId") : Text(action, "sessionId");

    /// <summary>
    /// Tells <paramref name="sessionId"/>'s session that it now answers for an unresolved action (<paramref name="open"/>), or no longer
    /// does; the session tells the directory when that changes, so a full directory never expires a session that owns work.
    /// </summary>
    private static void Track(AggregateContext context, string sessionId, string actionKey, bool open)
    {
        if (sessionId.Length == 0) return;
        context.Emit(AggregateKinds.Session, sessionId, Operations.SessionWork, new JsonObject
        {
            ["repositoryId"] = context.Repository, ["sessionId"] = sessionId, ["actionKey"] = actionKey, ["open"] = open, ["at"] = context.Now.ToString("O")
        });
    }

    public static RelayReply Begin(AggregateContext context, JsonObject task)
    {
        var args = context.Args;
        var session = context.SessionId;
        var version = Number(task, "version");
        var id = Required(args, "actionId");
        var fingerprint = Required(args, "inputFingerprint");
        var actions = ObjectAt(task, "actions");
        JsonObject? previousAttempt = null;
        if (id.Length > MaxActionIdLength) return Error(InvalidArguments);
        if (actions[id] is JsonObject prior)
        {
            if (Text(prior, "inputFingerprint") != fingerprint) return Error(ActionConflict);
            if (!Flag(args, "explicitRetry")) return Ok(new { action = prior, replayAllowed = false, existing = true }, version);
            if (Text(prior, "restoration") is "pending" or "blocked") return Error(RestorationRequired);
            if (!IsTerminal(Text(prior, "status"))) return Error(ActionUncertain);
            if (Number(args, "attempt") != Number(prior, "attempt") + 1) return Error(AttemptConflict);
            previousAttempt = prior;
        }
        if (TaskWorkflow.ExecutionRefusal(context.State, task, session, context.Now) is { } refusal) return refusal;
        if (previousAttempt is not null) ArrayAt(task, "attemptHistory").Add(previousAttempt.DeepClone());
        if (Text(task, "owner") != session || Date(task, "leaseUntil") <= context.Now) return Error(ClaimRequired);
        // The task's claim must cover the packet's declared resources (it may have changed since the claim); the hub checks they are held.
        if (TaskWorkflow.DeclaredResourcesNeeded(task, args) is { } needed) return needed;
        if (TaskWorkflow.ResourceRefusal(context.State, task, args, Strings(task["resourceIds"])) is { } resources) return resources;
        // The hub found a declared resource's lock not held by this session: nothing is recorded, so no action starts unguarded.
        if (args[Operations.LockRefusalField] is JsonObject locks)
            return Error(Text(locks, "code") == Busy ? Busy : ClaimRequired, new { resourceId = Text(locks, "resourceId"), replayAllowed = false }, version);
        if (actions.Any(x => x.Key != id && IsUnresolved(x.Value!.AsObject()))) return Error(ActionUncertain);
        if (!ExpectedVersionMatches(args, version)) return VersionConflict(version);
        var trigger = Text(args, "triggerId");
        if (trigger.Length > 0 && actions.Any(x => x.Key != id && Text(x.Value!.AsObject(), "triggerId") == trigger)) return Error(TriggerConsumed);
        if (actions.Count >= MaxActions || ArrayAt(task, "attemptHistory").Count >= MaxHistory) return Error(CapacityExceeded);
        var action = new JsonObject
        {
            ["actionId"] = id, ["inputFingerprint"] = fingerprint, ["triggerId"] = trigger, ["attempt"] = Number(args, "attempt", 1), ["status"] = "invoking",
            ["sessionId"] = session, ["principalId"] = context.Caller.PrincipalId, ["bindingVersion"] = Number(args, "bindingVersion"), ["binding"] = args["binding"]?.DeepClone(),
            ["startedAt"] = context.Now.ToString("O"), ["restoration"] = Flag(args, "requiresRestoration") ? "pending" : "not_required", ["resourceIds"] = task["resourceIds"]?.DeepClone()
        };
        action["requiresRestoration"] = Flag(args, "requiresRestoration");
        action[TrackedAtField] = context.Now.ToString("O");
        actions[id] = action; task["version"] = ++version;
        // Retrying a failed or abandoned action replaces it: the managers' failed item is restated for what is still failed, or closed.
        if (previousAttempt is not null && Text(previousAttempt, "status") is "failed" or Abandoned)
            AttentionSources.OnTaskUpdate(context, task, task["workflow"] as JsonObject, [], [], [], [id]);
        foreach (var resource in Strings(action["resourceIds"]))
            context.Emit(AggregateKinds.Lock, resource, Operations.Block, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["actionId"] = id, ["actionKey"] = CoordinationStateMachine.ActionKey(args, id, Number(action, "attempt")), ["sessionId"] = session
            });
        Track(context, session, CoordinationStateMachine.ActionKey(args, id, Number(action, "attempt")), open: true);
        return Ok(new { action, replayAllowed = true, existing = false }, version);
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
    public static RelayReply? ApplyReport(AggregateContext context, JsonObject task, JsonObject update)
    {
        var args = context.Args;
        var caller = context.Caller;
        var session = context.SessionId;
        var id = Required(update, "actionId");
        if (ByteSize(update) > RelayLimits.MaxReportBytes) return Error(PayloadTooLarge);
        if (ObjectAt(task, "actions")[id] is not JsonObject currentAction) return Error(ActionUnknown);
        if (!update.ContainsKey("attempt") || Number(update, "attempt") < 1) return Error(AttemptRequired);
        var attempt = Number(update, "attempt");
        var historical = attempt != Number(currentAction, "attempt");
        var action = historical ? ArrayAt(task, "attemptHistory").OfType<JsonObject>().SingleOrDefault(x => Text(x, "actionId") == id && Number(x, "attempt") == attempt) : currentAction;
        if (action is null) return Error(AttemptConflict);
        var originalWriter = Text(action, "principalId") == caller.PrincipalId && Text(action, "sessionId") == session;
        var recoverer = action["recovery"] is JsonObject recovered && Text(recovered, "principalId") == caller.PrincipalId && Text(recovered, "sessionId") == session;
        // Administrative acceptance is only a cleanup decision. It cannot supply an outcome.
        var adminAcceptance = caller.IsAdministrator && context.Operation == RelayTools.ActionReport && Text(update, "restoration") == "accepted" && !update.ContainsKey("outcome") && !CarriesMilestone(args);
        var recovery = context.Operation == RelayTools.ActionReport && !historical && Flag(update, "recover");
        var status = Text(action, "status");
        // The writer was there after all (an administrator abandoned it during a live lease, say): its real outcome replaces abandoned.
        var overriding = !recovery && !historical && status == Abandoned && Text(action, "principalId") == caller.PrincipalId && update.ContainsKey("outcome");
        if (recovery) { if (RecoveryRefusal(context, task, action, update) is { } refused) return refused; }
        else if (!originalWriter && !recoverer && !adminAcceptance && !overriding) return Error(ActionDenied);
        // A cleanup without an outcome keeps the action's status, whatever it is (an action still invoking included); a recovery without
        // one abandons an action still in flight.
        var outcome = recovery && !update.ContainsKey("outcome") ? (IsTerminal(status) ? status : Abandoned) : adminAcceptance ? status : Required(update, "outcome");
        if (recovery && update.ContainsKey("outcome") && !IsFinalOutcome(outcome) && outcome != Abandoned) return Error(InvalidOutcome, new { recovery = true, allowed = new[] { "completed", "failed", "canceled" } });
        var kept = (adminAcceptance || recovery) && outcome == status;
        if (!kept && !Outcomes.Contains(outcome) && !(recovery && outcome == Abandoned)) return Error(InvalidOutcome);
        var restoration = update.ContainsKey("restoration") ? Text(update, "restoration") : Text(action, "restoration");
        if (!Restorations.All.Contains(restoration)) return Error(InvalidRestoration);
        if (Text(update, "restoration") == "accepted" && Text(action, "restoration") != "accepted" && !caller.IsAdministrator) return Error(AdministratorRequired);
        var history = ArrayAt(task, "reports");
        // A report on an unresolved action must always land, or the action holds its task and locks for good: it evicts the oldest report.
        var settling = IsUnresolved(action);
        if (history.Count >= MaxHistory && !settling) return Error(CapacityExceeded);
        while (history.Count >= MaxHistory) history.RemoveAt(0);
        var entry = new JsonObject
        {
            ["actionId"] = id, ["attempt"] = attempt, ["outcome"] = outcome, ["restoration"] = restoration, ["report"] = update["report"]?.DeepClone(),
            ["at"] = context.Now.ToString("O"), ["author"] = caller.PrincipalId, ["sessionId"] = session
        };
        if (recovery) entry["recovered"] = true;
        if (overriding && IsTerminal(outcome)) entry["overrode"] = Abandoned;
        if (historical)
        {
            // A late report on a superseded attempt is kept for the record; it changes no current state.
            entry["historical"] = true;
            history.Add(entry);
            return null;
        }
        // Only a terminal outcome replaces abandoned: the action was already released, so it never becomes in flight again.
        if (IsTerminal(status) && status != outcome && !(overriding && IsTerminal(outcome))) return Error(ActionConflict);
        var priorRestoration = Text(action, "restoration");
        var requiresRestoration = Flag(action, "requiresRestoration") || priorRestoration is "pending" or "blocked";
        if (requiresRestoration && restoration == "not_required") return Error(RestorationRequired);
        if (priorRestoration is "verified" or "accepted" && restoration != priorRestoration) return Error(RestorationResolved);
        if (!IsUnresolved(action) && restoration is "pending" or "blocked") return Error(RestorationResolved);
        var (priorOwner, wasUnresolved) = (OwnerSession(action), IsUnresolved(action));
        action["status"] = outcome;
        action["restoration"] = restoration;
        action["report"] = update["report"]?.DeepClone(); action["reportedAt"] = context.Now.ToString("O");
        if (overriding && status != outcome)
        {
            action["overridden"] = new JsonObject
            {
                ["status"] = Abandoned, ["recovery"] = action["recovery"]?.DeepClone(), ["principalId"] = caller.PrincipalId, ["sessionId"] = session, ["at"] = context.Now.ToString("O")
            };
            action.Remove("recovery");
            // Taken back from another session of the principal: that session answers for it now (the writer's is gone), so it can still
            // report an owed restoration, and the directory keeps that session while it does.
            if (session != Text(action, "sessionId"))
                action["recovery"] = new JsonObject
                {
                    ["principalId"] = caller.PrincipalId, ["sessionId"] = session, ["administrator"] = caller.IsAdministrator, ["at"] = context.Now.ToString("O"), ["priorStatus"] = status,
                    ["override"] = true
                };
        }
        if (recovery)
            action["recovery"] = new JsonObject
            {
                ["principalId"] = caller.PrincipalId, ["sessionId"] = session, ["administrator"] = caller.IsAdministrator, ["at"] = context.Now.ToString("O"), ["priorStatus"] = status
            };
        history.Add(entry);
        if (settling) MakeRoom(task, history, id);
        var actionKey = CoordinationStateMachine.ActionKey(args, id, Number(action, "attempt"));
        var (owner, unresolved) = (OwnerSession(action), IsUnresolved(action));
        if (wasUnresolved && (!unresolved || owner != priorOwner)) Track(context, priorOwner, actionKey, open: false);
        // A new owner is told at once. The same owner's record is refreshed at most every SessionWorkRefreshHours, so a long-running action
        // stays tracked without a directory write on every progress report.
        if (unresolved && (owner != priorOwner || !wasUnresolved || Date(action, TrackedAtField) <= context.Now.AddHours(-RelayLimits.SessionWorkRefreshHours)))
        {
            Track(context, owner, actionKey, open: true);
            action[TrackedAtField] = context.Now.ToString("O");
        }
        // An administrator unblocks another principal's action only through cleanup: acceptance, a recovery, or a report on one it recovered.
        var administrativeAcceptance = caller.IsAdministrator && (adminAcceptance || recovery || recoverer);
        if (!unresolved)
            foreach (var resource in Strings(action["resourceIds"]))
                context.Emit(AggregateKinds.Lock, resource, Operations.Unblock, new JsonObject
                {
                    ["repositoryId"] = context.Repository, ["actionId"] = id, ["actionKey"] = actionKey,
                    ["sessionId"] = Text(action, "sessionId"), ["administrativeAcceptance"] = administrativeAcceptance
                });
        return null;
    }

    /// <summary>
    /// Why this caller may not recover the action, or null. Another principal must be an administrator, and gives no outcome; the action's
    /// own principal on another session must wait until the claim of the session that answers for it (its writer, or the session that
    /// recovered it) has lapsed, since until then that session is still there to report. A recovery changes no shared task content, and
    /// an action already resolved has nothing to recover.
    /// </summary>
    private static RelayReply? RecoveryRefusal(AggregateContext context, JsonObject task, JsonObject action, JsonObject update)
    {
        var caller = context.Caller;
        var samePrincipal = Text(action, "principalId") == caller.PrincipalId;
        if (!samePrincipal && (!caller.IsAdministrator || update.ContainsKey("outcome")) || CarriesMilestone(context.Args)) return Error(ActionDenied);
        if (!IsUnresolved(action)) return Error(ActionConflict, new { status = Text(action, "status"), restoration = Text(action, "restoration") });
        var (writer, answering, owner) = (Text(action, "sessionId"), OwnerSession(action), Text(task, "owner"));
        if (!caller.IsAdministrator && context.SessionId != answering && owner.Length > 0 && (owner == writer || owner == answering) && Date(task, "leaseUntil") > context.Now)
            return Error(OwnerConflict, new { holder = owner, leaseUntil = Text(task, "leaseUntil") });
        return null;
    }

    /// <summary>Whether a request also changes shared task content, which administrative cleanup paths may not do.</summary>
    public static bool CarriesMilestone(JsonObject args) => args.ContainsKey("patch") || args.ContainsKey("notes") || args.ContainsKey("message");
}

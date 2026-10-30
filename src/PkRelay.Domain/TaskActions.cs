using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>
/// External action records. An action is recorded before the agent invokes it and stays unresolved until a
/// report settles its outcome and any restoration obligation. Unresolved actions block claims and resources.
/// </summary>
internal static class TaskActions
{
    private const int MaxActionIdLength = 128;
    private const int MaxActions = 128;
    private const int MaxHistory = 256;
    private static readonly string[] Outcomes = ["started", "completed", "failed", "canceled", "incomplete", "unknown"];
    private static readonly string[] Restorations = ["pending", "blocked", "verified", "accepted", "not_required"];

    public static bool IsUnresolved(JsonObject action) =>
        Text(action, "status") is "invoking" or "started" or "unknown" or "incomplete" || Text(action, "restoration") is "pending" or "blocked";

    public static bool HasUnresolved(JsonObject task) => (task["actions"] as JsonObject ?? new()).Any(x => IsUnresolved(x.Value!.AsObject()));

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "canceled";

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
        if (!TaskWorkflow.AllowsExecution(task, session)) return TaskWorkflow.NotReady(task);
        if (previousAttempt is not null) ArrayAt(task, "attemptHistory").Add(previousAttempt.DeepClone());
        if (Text(task, "owner") != session || Date(task, "leaseUntil") <= context.Now) return Error(ClaimRequired);
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
        actions[id] = action; task["version"] = ++version;
        foreach (var resource in Strings(action["resourceIds"]))
            context.Emit(AggregateKinds.Lock, resource, Operations.Block, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["actionId"] = id, ["actionKey"] = CoordinationStateMachine.ActionKey(args, id, Number(action, "attempt")), ["sessionId"] = session
            });
        return Ok(new { action, replayAllowed = true, existing = false }, version);
    }

    /// <summary>Applies one action report to the task. Returns a failure, or null when the report was recorded.</summary>
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
        // Administrative acceptance is only a cleanup decision. It cannot supply an outcome.
        var adminAcceptance = caller.IsAdministrator && context.Operation == RelayTools.ActionReport && Text(update, "restoration") == "accepted" && !update.ContainsKey("outcome") && !CarriesMilestone(args);
        if (!originalWriter && !adminAcceptance) return Error(ActionDenied);
        var outcome = adminAcceptance ? Text(action, "status") : Required(update, "outcome");
        if (!Outcomes.Contains(outcome)) return Error(InvalidOutcome);
        var restoration = update.ContainsKey("restoration") ? Text(update, "restoration") : Text(action, "restoration");
        if (!Restorations.Contains(restoration)) return Error(InvalidRestoration);
        if (Text(update, "restoration") == "accepted" && Text(action, "restoration") != "accepted" && !caller.IsAdministrator) return Error(AdministratorRequired);
        var history = ArrayAt(task, "reports");
        if (history.Count >= MaxHistory) return Error(CapacityExceeded);
        var entry = new JsonObject
        {
            ["actionId"] = id, ["attempt"] = attempt, ["outcome"] = outcome, ["restoration"] = restoration, ["report"] = update["report"]?.DeepClone(),
            ["at"] = context.Now.ToString("O"), ["author"] = caller.PrincipalId, ["sessionId"] = session
        };
        if (historical)
        {
            // A late report on a superseded attempt is kept for the record; it changes no current state.
            entry["historical"] = true;
            history.Add(entry);
            return null;
        }
        if (IsTerminal(Text(action, "status")) && Text(action, "status") != outcome) return Error(ActionConflict);
        var priorRestoration = Text(action, "restoration");
        var requiresRestoration = Flag(action, "requiresRestoration") || priorRestoration is "pending" or "blocked";
        if (requiresRestoration && restoration == "not_required") return Error(RestorationRequired);
        if (priorRestoration is "verified" or "accepted" && restoration != priorRestoration) return Error(RestorationResolved);
        if (!IsUnresolved(action) && restoration is "pending" or "blocked") return Error(RestorationResolved);
        action["status"] = outcome;
        action["restoration"] = restoration;
        action["report"] = update["report"]?.DeepClone(); action["reportedAt"] = context.Now.ToString("O");
        history.Add(entry);
        if (!IsUnresolved(action))
            foreach (var resource in Strings(action["resourceIds"]))
                context.Emit(AggregateKinds.Lock, resource, Operations.Unblock, new JsonObject
                {
                    ["repositoryId"] = context.Repository, ["actionId"] = id, ["actionKey"] = CoordinationStateMachine.ActionKey(args, id, Number(action, "attempt")),
                    ["sessionId"] = Text(action, "sessionId"), ["administrativeAcceptance"] = adminAcceptance
                });
        return null;
    }

    /// <summary>Whether a request also changes shared task content, which administrative cleanup paths may not do.</summary>
    public static bool CarriesMilestone(JsonObject args) => args.ContainsKey("patch") || args.ContainsKey("notes") || args.ContainsKey("message");
}

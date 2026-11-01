using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Domain;

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
    /// Board items after a <c>task_update</c> or <c>action_report</c>: <c>blocked</c> and <c>failed</c> to managers, <c>review_ready</c>
    /// to managers on completion, and <c>next_work</c> to each unblocked task's target. Leaving a state closes the items it raised.
    /// </summary>
    public static void OnTaskUpdate(AggregateContext context, JsonObject task, JsonObject? prior, IReadOnlyList<string> unblocked, IReadOnlyList<string> failedActions)
    {
        var tasks = ObjectAt(context.State, "tasks");
        var taskId = Text(task, "taskId");
        var revision = Number(context.State, "taskRevision") + 1;
        var workflow = task["workflow"] as JsonObject;
        var (was, now) = (prior is null ? "" : Text(prior, "state"), workflow is null ? "" : Text(workflow, "state"));
        var priority = workflow is null ? 0 : Number(workflow, "priority");
        // Read only when an item needs them: the tasks that depend on this one, and whether one of them is claimed by another session,
        // which from a manager's view means this task blocks someone.
        var dependents = new Lazy<JsonObject[]>(() => tasks.Select(x => x.Value).OfType<JsonObject>().Where(other => other["workflow"] is JsonObject flow && TaskWorkflow.Dependencies(flow).Contains(taskId, StringComparer.Ordinal)).ToArray());
        bool Blocking() => dependents.Value.Any(other => Text(other, "owner") is { Length: > 0 } owner && owner != Text(task, "owner") && Date(other, "leaseUntil") > context.Now);
        JsonObject Item(string kind) => new() { ["kind"] = kind, ["taskId"] = taskId, ["priority"] = priority, ["revision"] = revision };

        if (now == "blocked" && (was != "blocked" || BlockerIds(prior) != BlockerIds(workflow)))
        {
            var blockers = (workflow!["blockers"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(blocker => BlockerRank(Text(blocker, "kind"))).ToArray();
            var item = Item(AttentionKinds.Blocked);
            if (blockers.FirstOrDefault() is { } first) { item["blocker"] = Text(first, "kind"); item["note"] = Text(first, "summary"); }
            if (Blocking()) item["blocking"] = true;
            item["refs"] = new JsonArray(dependents.Value.Select(other => (JsonNode)Text(other, "taskId")).ToArray());
            ToManagers(context, Operations.AttentionRaise, item);
        }
        else if (was == "blocked" && now != "blocked") ToManagers(context, Operations.AttentionClose, Kinds(Item(AttentionKinds.Blocked), AttentionKinds.Blocked));

        if (failedActions.Count > 0)
        {
            var item = Item(AttentionKinds.Failed);
            item["note"] = "action " + string.Join(", ", failedActions) + " failed";
            if (Blocking()) item["blocking"] = true;
            ToManagers(context, Operations.AttentionRaise, item);
        }

        if (now == "completed" && was != "completed")
        {
            ToManagers(context, Operations.AttentionClose, Kinds(Item(AttentionKinds.Blocked), AttentionKinds.Blocked, AttentionKinds.Failed));
            var review = Item(AttentionKinds.ReviewReady);
            review["note"] = "completed with " + ((workflow!["criteria"] as JsonArray)?.Count ?? 0) + " criteria passed; review the evidence";
            ToManagers(context, Operations.AttentionRaise, review);
            // Related work and duplicate warnings were raised to the claimer; the task is closed now.
            if (Text(task, "owner") is { Length: > 0 } owner)
                Emit(context, owner, Operations.AttentionClose, new JsonObject { ["subject"] = taskId, ["kinds"] = new JsonArray(AttentionKinds.RelatedWork, AttentionKinds.DuplicateWarning), ["revision"] = revision });
            foreach (var id in unblocked)
            {
                if (tasks[id]?["workflow"] is not JsonObject next || Text(next, "targetSessionId") is not { Length: > 0 } target || target == context.SessionId) continue;
                Emit(context, target, Operations.AttentionRaise, new JsonObject
                {
                    ["kind"] = AttentionKinds.NextWork, ["taskId"] = id, ["priority"] = Number(next, "priority"), ["note"] = "ready: " + taskId + " completed",
                    ["refs"] = new JsonArray(taskId), ["revision"] = revision
                });
            }
        }
        else if (was == "completed" && now != "completed") ToManagers(context, Operations.AttentionClose, Kinds(Item(AttentionKinds.ReviewReady), AttentionKinds.ReviewReady));
    }

    /// <summary>
    /// A new claim closes the task's <c>next_work</c> item at its target, whoever claimed it. (The hub closes the claimer's own item, from a
    /// next-work offer, after the claim.)
    /// </summary>
    public static void OnClaim(AggregateContext context, JsonObject task)
    {
        if (task["workflow"] is not JsonObject workflow || Text(workflow, "targetSessionId") is not { Length: > 0 } target) return;
        Emit(context, target, Operations.AttentionClose, new JsonObject
        {
            ["taskId"] = Text(task, "taskId"), ["kinds"] = new JsonArray(AttentionKinds.NextWork), ["revision"] = Number(context.State, "taskRevision") + 1
        });
    }

    /// <summary>A new proposal is raised to the managers; each proposal ID is raised once (revision 1) and closed once (revision 2).</summary>
    public static void OnProposal(AggregateContext context, string proposalId, string note) =>
        ToManagers(context, Operations.AttentionRaise, new JsonObject { ["kind"] = AttentionKinds.StandingProposal, ["subject"] = proposalId, ["note"] = note, ["revision"] = 1 });

    /// <summary>An approved or rejected proposal's item is closed at the managers.</summary>
    public static void OnProposalDecided(AggregateContext context, string proposalId) =>
        ToManagers(context, Operations.AttentionClose, new JsonObject { ["subject"] = proposalId, ["kinds"] = new JsonArray(AttentionKinds.StandingProposal), ["revision"] = 2 });

    /// <summary>
    /// A lock's due check, run twice per lease while its holder has an action in flight. Shortly before the lease ends the holder gets one
    /// <c>claim_expiring</c> item. When the lease has ended with the action still unresolved, that item closes and an <c>interrupted</c>
    /// item goes to the holder and to the managers. <paramref name="resourceId"/> is the lock's key.
    /// </summary>
    public static void OnLeaseCheck(AggregateContext context, string resourceId)
    {
        var state = context.State;
        var holder = Text(state, "holder");
        var lease = Text(state, "leaseUntil");
        if (holder.Length == 0 || lease.Length == 0) return;
        var action = (state["blockers"] as JsonObject ?? new JsonObject()).FirstOrDefault(x => x.Value is JsonObject blocker && Text(blocker, "sessionId") == holder);
        if (action.Key is null) return;
        var (boardId, taskId, actionId) = ActionTask(action.Key);
        JsonObject Item(string kind, string note) => new()
        {
            ["kind"] = kind, ["taskId"] = taskId, ["boardId"] = boardId, ["subject"] = resourceId, ["priority"] = AttentionWeights.High, ["note"] = note, ["revision"] = NextLockRevision(state)
        };
        if (Date(state, "leaseUntil") > context.Now)
        {
            if (Text(state, "warnedLease") == lease) return;
            state["warnedLease"] = lease;
            Emit(context, holder, Operations.AttentionRaise, Item(AttentionKinds.ClaimExpiring, "claim on " + resourceId + " ends " + lease + " with an action in flight; renew it"));
            return;
        }
        if (Text(state, "interruptedLease") == lease) return;
        state["interruptedLease"] = lease;
        if (state.ContainsKey("warnedLease"))
            Emit(context, holder, Operations.AttentionClose, new JsonObject { ["subject"] = resourceId, ["kinds"] = new JsonArray(AttentionKinds.ClaimExpiring), ["revision"] = NextLockRevision(state) });
        state["warnedLease"] = lease;
        var note = "claim on " + resourceId + " ended with action " + actionId + " unresolved; report its outcome";
        Emit(context, holder, Operations.AttentionRaise, Item(AttentionKinds.Interrupted, note));
        ToManagers(context, Operations.AttentionRaise, Item(AttentionKinds.Interrupted, note));
    }

    /// <summary>
    /// A renewed or released lease, or a resolved action, closes the holder's <c>claim_expiring</c> item, if one was raised; a resolved
    /// action also closes the <c>interrupted</c> items at the holder and the managers.
    /// </summary>
    public static void OnLeaseSettled(AggregateContext context, string holder, string resourceId)
    {
        var state = context.State;
        var interrupted = state.Remove("interruptedLease");
        if (!state.Remove("warnedLease") || holder.Length == 0) return;
        Emit(context, holder, Operations.AttentionClose, new JsonObject { ["subject"] = resourceId, ["kinds"] = new JsonArray(AttentionKinds.ClaimExpiring, AttentionKinds.Interrupted), ["revision"] = NextLockRevision(state) });
        if (interrupted)
            ToManagers(context, Operations.AttentionClose, new JsonObject { ["subject"] = resourceId, ["kinds"] = new JsonArray(AttentionKinds.Interrupted), ["revision"] = NextLockRevision(state) });
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
        var revision = Number(state, "attentionRevision") + 1;
        state["attentionRevision"] = revision;
        return revision;
    }

    /// <summary>The board and task of an action key (<see cref="CoordinationStateMachine.ActionKey"/>): repository/board/task/action/attempt, each escaped.</summary>
    private static (string BoardId, string TaskId, string ActionId) ActionTask(string actionKey)
    {
        var parts = actionKey.Split('/');
        return parts.Length >= 4 ? (Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]), Uri.UnescapeDataString(parts[3]))
            : parts.Length >= 3 ? (Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]), "") : ("default", "", actionKey);
    }

    /// <summary>A provider rejection ranks first and a coordinator hold last, so the item takes the most urgent blocker's kind.</summary>
    private static int BlockerRank(string kind) => kind == AttentionWeights.ProviderRejection ? 0 : kind == AttentionWeights.CoordinatorHold ? 2 : 1;

    private static string BlockerIds(JsonObject? workflow) =>
        string.Join("\n", (workflow?["blockers"] as JsonArray ?? []).OfType<JsonObject>().Select(blocker => Text(blocker, "id")).Order(StringComparer.Ordinal));

    private static JsonObject Kinds(JsonObject item, params string[] kinds)
    {
        var close = new JsonObject { ["taskId"] = item["taskId"]!.DeepClone(), ["revision"] = item["revision"]!.DeepClone() };
        close["kinds"] = new JsonArray(kinds.Select(kind => (JsonNode)kind).ToArray());
        return close;
    }

    private static void Emit(AggregateContext context, string sessionId, string operation, JsonObject item)
    {
        item["repositoryId"] = context.Repository;
        if (Text(context.Args, "boardId") is { Length: > 0 } board && !item.ContainsKey("boardId")) item["boardId"] = board;
        context.Emit(AggregateKinds.Session, sessionId, operation, item);
    }

    /// <summary>Asks the control aggregate to pass the effect to every manager session in the repository except the one that caused it.</summary>
    internal static void ToManagers(AggregateContext context, string operation, JsonObject item)
    {
        item["repositoryId"] = context.Repository;
        if (Text(context.Args, "boardId") is { Length: > 0 } board) item["boardId"] = board;
        context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.AttentionRoute, new JsonObject
        {
            ["repositoryId"] = context.Repository, ["role"] = ManagerRole, ["operation"] = operation, ["exceptSessionId"] = context.SessionId, ["item"] = item
        });
    }
}

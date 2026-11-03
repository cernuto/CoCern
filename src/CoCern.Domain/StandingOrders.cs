using System.Globalization;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// A board's standing orders (state key <c>orders</c>): administrator-approved "when this happens, do that" rules the hub runs itself.
/// Triggers are a fixed set and actions are coordination only (release claims, offer next work, notify), so an order can never accept
/// work, grant authority, change a blocker or touch another board. Its actions are emitted as outbox effects with the triggering change.
/// Every order effect carries the board's order <c>revision</c>, so a redelivered effect is applied once. A failed run is logged and
/// raised to the managers; after <see cref="RelayLimits.MaxOrderFailures"/> failed runs the order is disabled.
/// </summary>
internal static class StandingOrders
{
    public static RelayReply Put(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        if (!context.OrdersEnabled) return Error(OrdersUnavailable);
        var args = context.Args;
        if (!Identifier(args["orderId"], RelayLimits.MaxStandingIdLength, out var id)) return Invalid("orderId");
        var tasks = ObjectAt(context.State, "tasks");
        if (ReadTrigger(args["trigger"], tasks, out var trigger) is { } triggerField) return Invalid("trigger" + triggerField);
        var (on, target) = (Text(trigger, "on"), Target(trigger));
        if (args["actions"] is not JsonArray list || list.Count is 0 or > RelayLimits.MaxOrderActions) return Invalid("actions");
        var actions = new JsonArray();
        for (var index = 0; index < list.Count; index++)
        {
            if (ReadAction(list[index], on, tasks, out var action) is { } field) return Invalid($"actions[{index}]{field}");
            actions.Add(action);
        }
        DateTimeOffset expires;
        if (args["expiresAt"] is null) expires = context.Now.AddDays(Math.Clamp(Number(args, "defaultExpiryDays", RelayLimits.DefaultOrderExpiryDays), 1, RelayLimits.MaxOrderExpiryDays));
        else if (!DateTimeOffset.TryParse(Text(args, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out expires) || expires <= context.Now || expires > context.Now.AddDays(RelayLimits.MaxOrderExpiryDays)) return Invalid("expiresAt");

        var orders = context.State[BoardReadModels.OrdersKey] as JsonObject ?? new JsonObject();
        if (WriteConflict(args, orders) is { } conflict) return conflict;
        var items = ObjectAt(orders, "items");
        var watchedBefore = Watched(items);
        // Expired orders never run again; a write drops them so they do not hold places under the cap.
        foreach (var expired in Items(items).Where(order => Date(order, "expiresAt") <= context.Now && Text(order, "id") != id).ToArray())
        {
            items.Remove(Text(expired, "id"));
            Log(orders, context, Text(expired, "id"), "expired");
        }
        if (Items(items).FirstOrDefault(order => Text(order, "id") != id && SameTrigger(order, on, target)) is { } other) return Error(InvalidArguments, new { field = "trigger", conflictsWith = Text(other, "id") });
        if (!items.ContainsKey(id) && items.Count >= RelayLimits.MaxOrders) return Error(CapacityExceeded, new { maxOrders = RelayLimits.MaxOrders });
        var replaced = items[id] is JsonObject;
        var revision = NextWrite(orders);
        NextRevision(orders);
        items[id] = new JsonObject
        {
            ["id"] = id, ["trigger"] = trigger, ["actions"] = actions, ["approvedBy"] = context.Caller.PrincipalId, ["approvedAt"] = context.Now.ToString("O"),
            ["expiresAt"] = expires.ToUniversalTime().ToString("O"), ["runs"] = 0, ["failures"] = 0
        };
        Log(orders, context, id, (replaced ? "replaced by " : "put by ") + context.Caller.PrincipalId);
        context.State[BoardReadModels.OrdersKey] = orders;
        // A replaced order starts clean: its open failure item at the managers closes.
        if (replaced) CloseFailure(context, id, NextRevision(orders));
        UpdateWatches(context, watchedBefore, Watched(items), on == OrderTriggers.ClaimReleased ? target : null, id);
        return Ok(new { orderId = id, revision, expiresAt = Text(items[id]!.AsObject(), "expiresAt"), replaced }, revision);
    }

    public static RelayReply Remove(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        if (!context.OrdersEnabled) return Error(OrdersUnavailable);
        if (!Identifier(context.Args["orderId"], RelayLimits.MaxStandingIdLength, out var id)) return Invalid("orderId");
        if (context.State[BoardReadModels.OrdersKey] is not JsonObject orders || orders["items"] is not JsonObject items || !items.ContainsKey(id)) return Error(NotFound, new { orderId = id });
        if (WriteConflict(context.Args, orders) is { } conflict) return conflict;
        var watchedBefore = Watched(items);
        items.Remove(id);
        var revision = NextWrite(orders);
        Log(orders, context, id, "removed by " + context.Caller.PrincipalId);
        CloseFailure(context, id, NextRevision(orders));
        UpdateWatches(context, watchedBefore, Watched(items), null, id);
        return Ok(new { orderId = id, removed = true, revision }, revision);
    }

    /// <summary>The compact list <c>get_status</c> shows: trigger, target, expiry, runs and failures, and whether each is off or expired.</summary>
    public static RelayReply Read(JsonObject board, DateTimeOffset now)
    {
        var orders = board[BoardReadModels.OrdersKey] as JsonObject ?? new JsonObject();
        var list = new JsonArray(Items(orders["items"] as JsonObject ?? new JsonObject()).OrderBy(order => Text(order, "id"), StringComparer.Ordinal).Select(order =>
        {
            var item = new JsonObject
            {
                ["id"] = Text(order, "id"), ["on"] = Text(order["trigger"] as JsonObject ?? new JsonObject(), "on"), ["t"] = Target(order["trigger"] as JsonObject ?? new JsonObject()),
                ["until"] = Text(order, "expiresAt"), ["runs"] = Number(order, "runs"), ["fails"] = Number(order, "failures")
            };
            if (IsTrue(order, "disabled")) item["off"] = true;
            if (Date(order, "expiresAt") <= now) item["expired"] = true;
            if (Text(order, "lastError") is { Length: > 0 } error) item["err"] = Cap(error, 120);
            return (JsonNode)item;
        }).ToArray());
        return Ok(new { revision = Number(orders, "writeRevision"), orders = list }, Number(orders, "writeRevision"));
    }

    /// <summary>Records the order triggers a task change caused: completion, and leaving the blocked state. They run once the change is marked.</summary>
    public static void Detect(AggregateContext context, JsonObject task, JsonObject? prior)
    {
        if (!context.OrdersEnabled || context.State[BoardReadModels.OrdersKey] is not JsonObject) return;
        var was = prior is null ? "" : Text(prior, "state");
        var now = task["workflow"] is JsonObject workflow ? Text(workflow, "state") : "";
        var taskId = Text(task, "taskId");
        if (now == "completed" && was != "completed") context.OrderTriggers.Add((OrderTriggers.WorkflowCompleted, taskId));
        if (was == "blocked" && now != "blocked") context.OrderTriggers.Add((OrderTriggers.BlockerCleared, taskId));
    }

    /// <summary>
    /// Runs the one active order for this trigger, at most once per <paramref name="triggerRevision"/> (the task's changeRevision, or
    /// the lock's generation). Disabled and expired orders never run.
    /// </summary>
    public static void Run(AggregateContext context, string on, string target, string triggerRevision)
    {
        if (!context.OrdersEnabled || context.State[BoardReadModels.OrdersKey] is not JsonObject orders) return;
        var order = Items(ObjectAt(orders, "items")).FirstOrDefault(item => SameTrigger(item, on, target));
        if (order is null || IsDisabled(order) || Date(order, "expiresAt") <= context.Now || Text(order, "lastRun") == triggerRevision) return;
        var revision = NextRevision(orders);
        order["lastRun"] = triggerRevision; order["runRevision"] = revision; order["runs"] = Number(order, "runs") + 1; order["lastRunAt"] = context.Now.ToString("O");
        var problems = (order["actions"] as JsonArray ?? []).OfType<JsonObject>().Select(action => Execute(context, order, action, on, target, revision) is { } problem ? Text(action, "do") + ": " + problem : null).OfType<string>().ToArray();
        if (problems.Length == 0) Log(orders, context, Text(order, "id"), "ran on " + on + " " + target);
        else Fail(context, orders, order, string.Join("; ", problems));
    }

    /// <summary>A watched lock's claim was released: runs this board's <c>claim_released</c> order, or stops watching when none names the lock.</summary>
    public static RelayReply OnFire(AggregateContext context)
    {
        if (!context.OrdersEnabled) return Ok(new { ran = false });
        var resource = Text(context.Args, "resourceId");
        if (context.State[BoardReadModels.OrdersKey] is not JsonObject orders || !Items(ObjectAt(orders, "items")).Any(order => SameTrigger(order, OrderTriggers.ClaimReleased, resource)))
        {
            Watch(context, resource, false, "");
            return Ok(new { ran = false });
        }
        Run(context, OrderTriggers.ClaimReleased, resource, "lock:" + Number(context.Args, "generation"));
        return Ok(new { ran = true });
    }

    /// <summary>A lock could not carry out an order's release, or could not watch for it: one failed run, counted once.</summary>
    public static RelayReply OnFailed(AggregateContext context)
    {
        var args = context.Args;
        if (!context.OrdersEnabled || context.State[BoardReadModels.OrdersKey] is not JsonObject orders || ObjectAt(orders, "items")[Text(args, "orderId")] is not JsonObject order) return Ok(new { counted = false });
        var watch = IsTrue(args, "watch");
        var run = Number(args, "run");
        // A failure from an earlier run of a replaced order, or a second failure in the same run, is not counted again.
        if (!watch && (Number(order, "runRevision") != run || Number(order, "failedRun") == run))
        {
            Log(orders, context, Text(order, "id"), "also failed: " + Text(args, "reason"));
            return Ok(new { counted = false });
        }
        Fail(context, orders, order, Text(args, "reason"));
        return Ok(new { counted = true });
    }

    /// <summary>Lock: starts or stops telling one board when this lock's claim is released.</summary>
    public static RelayReply Watch(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        var board = Text(args, "boardKey");
        if (board.Length == 0) return Error(InvalidArguments, new { required = "boardKey" });
        var boards = ObjectAt(state, "orderBoards");
        var watch = IsTrue(args, "watch");
        if (watch && !boards.ContainsKey(board) && boards.Count >= RelayLimits.MaxOrderWatchBoards)
        {
            context.Emit(AggregateKinds.Board, board, Operations.OrderFailed, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["boardId"] = Text(args, "boardId", "default"), ["orderId"] = Text(args, "orderId"), ["watch"] = true,
                ["reason"] = "resource " + context.Key + " is already watched by " + RelayLimits.MaxOrderWatchBoards + " boards"
            });
            watch = false;
        }
        else if (watch) boards[board] = new JsonObject { ["boardId"] = Text(args, "boardId", "default") };
        else boards.Remove(board);
        if (boards.Count == 0) state.Remove("orderBoards");
        return Ok(new { watching = watch });
    }

    /// <summary>
    /// Lock: an order's release of the claim its task's owner holds. Applied once per order run; a claim the owner no longer holds is
    /// left alone, and a claim with an unresolved action is reported back to the board as a failed run.
    /// </summary>
    public static RelayReply Release(AggregateContext context)
    {
        var state = context.State;
        var args = context.Args;
        var mark = Text(args, "boardKey") + "|" + Text(args, "orderId") + "|" + Number(args, "run");
        var done = ArrayAt(state, "orderReleases");
        if (done.Any(entry => entry is JsonValue value && value.TryGetValue<string>(out var seen) && seen == mark)) return Ok(new { released = false, repeated = true });
        done.Add(mark);
        while (done.Count > RelayLimits.OrderReleasesRemembered) done.RemoveAt(0);
        var holder = Text(args, "holderSessionId");
        if (holder.Length == 0 || Text(state, "holder") != holder || Date(state, "leaseUntil") <= context.Now) return Ok(new { released = false });
        // Only the grant taken for the order's task claim: a session that already claimed the resource again (for its next task, or
        // directly) keeps it.
        if (Text(args, "claimFor") is not { Length: > 0 } claim || Text(state, "claimFor") != claim) return Ok(new { released = false, claimedAgain = true });
        if (state["blockers"] is JsonObject { Count: > 0 })
        {
            context.Emit(AggregateKinds.Board, Text(args, "boardKey"), Operations.OrderFailed, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["boardId"] = Text(args, "boardId", "default"), ["orderId"] = Text(args, "orderId"), ["run"] = Number(args, "run"),
                ["reason"] = "claim on " + context.Key + " has an unresolved action"
            });
            return Ok(new { released = false, failed = true });
        }
        LockAggregate.Release(context, holder);
        return Ok(new { released = true });
    }

    /// <summary>Lock: a released claim tells each watching board, with the lock's generation as the trigger revision.</summary>
    public static void OnLockReleased(AggregateContext context)
    {
        if (context.State["orderBoards"] is not JsonObject { Count: > 0 } boards) return;
        foreach (var board in boards.ToArray())
            context.Emit(AggregateKinds.Board, board.Key, Operations.OrderFire, new JsonObject
            {
                ["repositoryId"] = context.Repository, ["boardId"] = Text(board.Value as JsonObject ?? new JsonObject(), "boardId", "default"),
                ["resourceId"] = context.Key, ["generation"] = Number(context.State, "generation")
            });
    }

    /// <summary>Runs one action; returns why it could not run, or null.</summary>
    private static string? Execute(AggregateContext context, JsonObject order, JsonObject action, string on, string target, long revision)
    {
        var tasks = ObjectAt(context.State, "tasks");
        var id = Text(order, "id");
        var triggerTask = on == OrderTriggers.ClaimReleased ? "" : target;
        switch (Text(action, "do"))
        {
            case OrderActions.ReleaseClaims:
            {
                var taskId = Text(action, "taskId", triggerTask);
                if (tasks[taskId] is not JsonObject task) return "unknown task " + taskId;
                var owner = Text(task, "owner");
                if (owner.Length == 0) return null;
                if (TaskActions.HasUnresolved(task)) return "task " + taskId + " has an unresolved action";
                TaskBoardAggregate.ReleaseClaim(task, context.Now);
                // The triggering task's own change is already marked and versioned by its update; any other task is changed here.
                if (!(context.Operation is RelayTools.TaskUpdate or RelayTools.ActionReport && Text(context.Args, "taskId") == taskId))
                {
                    task["version"] = Number(task, "version") + 1;
                    TaskBoardAggregate.MarkChanged(context.State, task);
                }
                foreach (var resource in Strings(task["resourceIds"]).Distinct(StringComparer.Ordinal))
                    context.Emit(AggregateKinds.Lock, resource, Operations.OrderRelease, new JsonObject
                    {
                        ["repositoryId"] = context.Repository, ["boardId"] = Text(context.Args, "boardId", "default"), ["boardKey"] = context.Key,
                        ["holderSessionId"] = owner, ["claimFor"] = ClaimKey(Text(context.Args, "boardId", "default"), taskId, Number(task, "claimGeneration")), ["orderId"] = id, ["run"] = revision
                    });
                return null;
            }
            case OrderActions.Offer:
            {
                var taskId = Text(action, "taskId");
                if (tasks[taskId] is not JsonObject task) return "unknown task " + taskId;
                // The next-work checks apply, except the target-session match: an order never works around a provider rejection, and
                // offers only work the session could claim now.
                if (TaskWorkflow.OfferProblem(context.State, tasks, task, Text(action, "sessionId"), context.Now) is { } problem) return "task " + taskId + " " + problem;
                var workflow = task["workflow"]!.AsObject();
                var item = new AttentionContent
                {
                    Kind = AttentionKinds.NextWork, TaskId = taskId, Subject = id, Priority = AttentionWeights.PriorityLevel(Number(workflow, "priority")),
                    Note = "order " + id + ": " + on + " " + target + "; claim it with task_claim", Revision = revision, Refs = triggerTask.Length > 0 ? [triggerTask] : null
                };
                // Each order raise names its order and run; if the session refuses it, the dispatcher reports a failed run.
                AttentionSources.Emit(context, Text(action, "sessionId"), item, id, revision);
                return null;
            }
            case OrderActions.Notify:
            {
                var item = new AttentionContent
                {
                    Kind = AttentionKinds.Info, Subject = id, Note = Text(action, "note", "order " + id + ": " + on + " " + target), Revision = revision,
                    TaskId = triggerTask.Length > 0 ? triggerTask : null
                };
                AttentionSources.Emit(context, Text(action, "sessionId"), item, id, revision);
                return null;
            }
            default: return "unknown action";
        }
    }

    /// <summary>Counts one failed run, logs it, raises <c>order_failed</c> to the managers, and disables the order at the failure limit.</summary>
    private static void Fail(AggregateContext context, JsonObject orders, JsonObject order, string reason)
    {
        var id = Text(order, "id");
        var failures = Number(order, "failures") + 1;
        order["failures"] = failures;
        order["failedRun"] = Number(order, "runRevision");
        order["lastError"] = Cap(reason, RelayLimits.MaxOrderNoteLength);
        order["lastErrorAt"] = context.Now.ToString("O");
        var disabled = failures >= RelayLimits.MaxOrderFailures;
        if (disabled) order["disabled"] = true;
        Log(orders, context, id, (disabled ? "failed and disabled: " : "failed: ") + reason);
        var item = new AttentionContent
        {
            Kind = AttentionKinds.OrderFailed, Subject = id,
            Note = "order " + id + (disabled ? " disabled after " + failures + " failures: " : " failed: ") + reason, Revision = NextRevision(orders)
        };
        if (order["trigger"] is JsonObject trigger && Text(trigger, "taskId") is { Length: > 0 } taskId) item = item with { TaskId = taskId };
        AttentionSources.ToManagers(context, item);
    }

    private static void CloseFailure(AggregateContext context, string id, long revision) =>
        AttentionSources.ToManagers(context, new AttentionSelection { Subject = id, Kinds = [AttentionKinds.OrderFailed], Revision = revision });

    /// <summary>Asks each lock a <c>claim_released</c> order newly names (or the written order names) to tell this board, and the ones no order names any more to stop.</summary>
    private static void UpdateWatches(AggregateContext context, HashSet<string> before, HashSet<string> after, string? written, string orderId)
    {
        foreach (var resource in after.Where(resource => !before.Contains(resource) || resource == written)) Watch(context, resource, true, orderId);
        foreach (var resource in before.Where(resource => !after.Contains(resource))) Watch(context, resource, false, orderId);
    }

    private static void Watch(AggregateContext context, string resource, bool watch, string orderId) =>
        context.Emit(AggregateKinds.Lock, resource, Operations.OrderWatch, new JsonObject
        {
            ["repositoryId"] = context.Repository, ["boardId"] = Text(context.Args, "boardId", "default"), ["boardKey"] = context.Key, ["orderId"] = orderId, ["watch"] = watch
        });

    private static HashSet<string> Watched(JsonObject items) =>
        Items(items).Select(order => order["trigger"] as JsonObject).OfType<JsonObject>().Where(trigger => Text(trigger, "on") == OrderTriggers.ClaimReleased)
            .Select(trigger => Text(trigger, "resourceId")).ToHashSet(StringComparer.Ordinal);

    /// <summary>Returns the invalid field (prefixed with a dot), or null with the normalized trigger. A task trigger must name a task on this board.</summary>
    private static string? ReadTrigger(JsonNode? node, JsonObject tasks, out JsonObject trigger)
    {
        trigger = new JsonObject();
        if (node is not JsonObject source) return "";
        var on = Text(source, "on");
        if (!OrderTriggers.All.Contains(on)) return ".on";
        var field = on == OrderTriggers.ClaimReleased ? "resourceId" : "taskId";
        if (source.FirstOrDefault(property => property.Key != "on" && property.Key != field) is { Key: { } extra }) return "." + extra;
        if (!Identifier(source[field], RelayLimits.MaxIdentifierLength, out var target) || field == "taskId" && tasks[target] is not JsonObject) return "." + field;
        trigger["on"] = on;
        trigger[field] = target;
        return null;
    }

    /// <summary>Returns the invalid field (prefixed with a dot), or null with the normalized action. Tasks must be on this board.</summary>
    private static string? ReadAction(JsonNode? node, string on, JsonObject tasks, out JsonObject action)
    {
        action = new JsonObject();
        if (node is not JsonObject source) return "";
        var kind = Text(source, "do");
        if (!OrderActions.All.Contains(kind)) return ".do";
        string[] allowed = kind switch
        {
            OrderActions.ReleaseClaims => ["taskId"],
            OrderActions.Offer => ["taskId", "sessionId"],
            _ => ["sessionId", "note"]
        };
        if (source.FirstOrDefault(property => property.Key != "do" && !allowed.Contains(property.Key)) is { Key: { } extra }) return "." + extra;
        action["do"] = kind;
        // release_claims on a task trigger defaults to that task; a claim_released order must name the task whose claims it releases.
        var needsTask = kind == OrderActions.Offer || kind == OrderActions.ReleaseClaims && (on == OrderTriggers.ClaimReleased || source.ContainsKey("taskId"));
        if (needsTask)
        {
            if (!Identifier(source["taskId"], RelayLimits.MaxIdentifierLength, out var taskId) || tasks[taskId] is not JsonObject) return ".taskId";
            action["taskId"] = taskId;
        }
        if (kind != OrderActions.ReleaseClaims)
        {
            if (!Identifier(source["sessionId"], RelayLimits.MaxIdentifierLength, out var sessionId)) return ".sessionId";
            action["sessionId"] = sessionId;
        }
        if (source.ContainsKey("note"))
        {
            if (!Identifier(source["note"], RelayLimits.MaxOrderNoteLength, out var note)) return ".note";
            action["note"] = note;
        }
        return null;
    }

    /// <summary>The identity of one task claim, as a lock records it for the grant it serves: board, task and claim generation.</summary>
    public static string ClaimKey(string boardId, string taskId, long claimGeneration) => boardId + "/" + taskId + "#" + claimGeneration;

    private static IEnumerable<JsonObject> Items(JsonObject items) => items.Select(x => x.Value).OfType<JsonObject>();

    private static bool SameTrigger(JsonObject order, string on, string target) =>
        order["trigger"] is JsonObject trigger && Text(trigger, "on") == on && Target(trigger) == target;

    private static string Target(JsonObject trigger) => Text(trigger, "on") == OrderTriggers.ClaimReleased ? Text(trigger, "resourceId") : Text(trigger, "taskId");

    private static bool IsDisabled(JsonObject order) => IsTrue(order, "disabled");

    /// <summary>
    /// The write revision moves only when an administrator puts or removes an order. An optional <c>expectedRevision</c> must match it,
    /// as with <c>standing_put</c>, so two administrators cannot overwrite each other unseen.
    /// </summary>
    private static RelayReply? WriteConflict(JsonObject args, JsonObject orders)
    {
        var current = Number(orders, "writeRevision");
        if (args["expectedRevision"] is null) return null;
        return args["expectedRevision"] is JsonValue expected && long.TryParse(expected.ToJsonString(), out var revision) && revision == current
            ? null : Error(RelayErrorCodes.VersionConflict, new { currentRevision = current }, current);
    }

    private static long NextWrite(JsonObject orders)
    {
        var revision = Number(orders, "writeRevision") + 1;
        orders["writeRevision"] = revision;
        return revision;
    }

    /// <summary>One counter for every order effect and change on this board, so raises, closes and releases are applied once and in order.</summary>
    private static long NextRevision(JsonObject orders)
    {
        var revision = Number(orders, "revision") + 1;
        orders["revision"] = revision;
        return revision;
    }

    private static void Log(JsonObject orders, AggregateContext context, string orderId, string what)
    {
        var log = ArrayAt(orders, "log");
        log.Add(new JsonObject { ["at"] = context.Now.ToString("O"), ["orderId"] = orderId, ["what"] = Cap(what, RelayLimits.MaxOrderNoteLength) });
        while (log.Count > RelayLimits.OrderLogEntries) log.RemoveAt(0);
    }

    private static string Cap(string text, int maximum) => text.Length <= maximum ? text : text[..(maximum - 1)] + "…";

    private static bool Identifier(JsonNode? node, int maximum, out string value)
    {
        value = node is JsonValue text && text.TryGetValue<string>(out var read) ? read : "";
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    }

    private static RelayReply Invalid(string field, string? id = null) => Error(InvalidArguments, id is null ? new { field } : (object)new { field, id });
}

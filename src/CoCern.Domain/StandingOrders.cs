using System.Globalization;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// A board's standing orders (state key <c>orders</c>): administrator-approved "when this happens, do that" rules the hub runs itself.
/// Triggers are a fixed set and actions are coordination only (release claims, offer next work, notify), so an order can never accept
/// work, grant authority, change a blocker or touch another board. Its actions are emitted as outbox effects with the triggering change.
/// Every order effect carries the board's order <c>revision</c>, so a redelivered effect is applied once. A failed run is logged and
/// raised to the managers; after <see cref="RelayLimits.MaxOrderFailures"/> failed runs the order is disabled. A lock keeps the boards
/// watching it, and the order releases it applied, in its own document (<see cref="LockOrderState"/>).
/// </summary>
internal static class StandingOrders
{
    public static RelayReply Put(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        if (!context.OrdersEnabled) return Error(OrdersUnavailable);
        var args = context.Args;
        var board = new BoardState(context.State);
        if (!Identifier(args["orderId"], RelayLimits.MaxStandingIdLength, out var id)) return Invalid("orderId");
        if (ReadTrigger(args["trigger"], board, out var trigger) is { } triggerField) return Invalid("trigger" + triggerField);
        var (on, target) = (trigger.On, trigger.Target);
        if (args["actions"] is not JsonArray list || list.Count is 0 or > RelayLimits.MaxOrderActions) return Invalid("actions");
        var actions = new JsonArray();
        for (var index = 0; index < list.Count; index++)
        {
            if (ReadAction(list[index], on, board, out var action) is { } field) return Invalid($"actions[{index}]{field}");
            actions.Add(action);
        }
        DateTimeOffset expires;
        if (args["expiresAt"] is null) expires = context.Now.AddDays(Math.Clamp(Number(args, "defaultExpiryDays", RelayLimits.DefaultOrderExpiryDays), 1, RelayLimits.MaxOrderExpiryDays));
        else if (!DateTimeOffset.TryParse(Text(args, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out expires) || expires <= context.Now || expires > context.Now.AddDays(RelayLimits.MaxOrderExpiryDays)) return Invalid("expiresAt");

        var orders = board.Orders ?? new OrdersState(new JsonObject());
        if (WriteConflict(args, orders) is { } conflict) return conflict;
        var items = orders.Items;
        var watchedBefore = Watched(orders);
        // Expired orders never run again; a write drops them so they do not hold places under the cap.
        foreach (var expired in orders.Orders().Where(order => order.ExpiresAt <= context.Now && order.Id != id).ToArray())
        {
            items.Remove(expired.Id);
            Log(orders, context, expired.Id, "expired");
        }
        if (orders.Orders().FirstOrDefault(order => order.Id != id && SameTrigger(order, on, target)) is { } other) return Error(InvalidArguments, new { field = "trigger", conflictsWith = other.Id });
        if (!items.ContainsKey(id) && items.Count >= RelayLimits.MaxOrders) return Error(CapacityExceeded, new { maxOrders = RelayLimits.MaxOrders });
        var replaced = items[id] is JsonObject;
        var revision = orders.NextWrite();
        orders.NextRevision();
        items[id] = OrderRecord.Create(id, trigger, actions, context.Caller.PrincipalId, context.Now, expires).Json;
        Log(orders, context, id, (replaced ? "replaced by " : "put by ") + context.Caller.PrincipalId);
        board.SetOrders(orders);
        // A replaced order starts clean: its open failure item at the managers closes.
        if (replaced) CloseFailure(context, id, orders.NextRevision());
        UpdateWatches(context, watchedBefore, Watched(orders), on == OrderTriggers.ClaimReleased ? target : null, id);
        return Ok(new { orderId = id, revision, expiresAt = orders.Order(id)!.ExpiresAtText, replaced }, revision);
    }

    public static RelayReply Remove(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        if (!context.OrdersEnabled) return Error(OrdersUnavailable);
        if (!Identifier(context.Args["orderId"], RelayLimits.MaxStandingIdLength, out var id)) return Invalid("orderId");
        if (new BoardState(context.State).Orders is not { } orders || !orders.HasOrder(id)) return Error(NotFound, new { orderId = id });
        if (WriteConflict(context.Args, orders) is { } conflict) return conflict;
        var watchedBefore = Watched(orders);
        orders.Items.Remove(id);
        var revision = orders.NextWrite();
        Log(orders, context, id, "removed by " + context.Caller.PrincipalId);
        CloseFailure(context, id, orders.NextRevision());
        UpdateWatches(context, watchedBefore, Watched(orders), null, id);
        return Ok(new { orderId = id, removed = true, revision }, revision);
    }

    /// <summary>
    /// The compact list <c>get_status</c> shows (<see cref="OrdersRead"/>): trigger, target, expiry, runs and failures, and whether each is
    /// off or expired.
    /// </summary>
    public static RelayReply Read(AggregateContext context)
    {
        if (!OrdersRead.Decode(context.Args).TryGet(out _, out var malformed)) return Malformed(malformed);
        var orders = new BoardState(context.State).Orders ?? new OrdersState(new JsonObject());
        var list = new JsonArray(orders.Orders().OrderBy(order => order.Id, StringComparer.Ordinal).Select(order =>
        {
            var trigger = order.Trigger ?? new OrderTrigger(new JsonObject());
            var item = new JsonObject
            {
                ["id"] = order.Id, ["on"] = trigger.On, ["t"] = trigger.Target, ["until"] = order.ExpiresAtText, ["runs"] = order.Runs, ["fails"] = order.Failures
            };
            if (order.Disabled) item["off"] = true;
            if (order.ExpiresAt <= context.Now) item["expired"] = true;
            if (order.LastError is { Length: > 0 } error) item["err"] = Cap(error, 120);
            return (JsonNode)item;
        }).ToArray());
        return Ok(new { revision = orders.WriteRevision, orders = list }, orders.WriteRevision);
    }

    /// <summary>Records the order triggers a task change caused: completion, and leaving the blocked state. They run once the change is marked.</summary>
    public static void Detect(AggregateContext context, BoardTask task, WorkflowRecord? prior)
    {
        if (!context.OrdersEnabled || new BoardState(context.State).Orders is null) return;
        var was = prior?.State ?? "";
        var now = task.Workflow?.State ?? "";
        var taskId = task.TaskId;
        if (now == "completed" && was != "completed") context.OrderTriggers.Add((OrderTriggers.WorkflowCompleted, taskId));
        if (was == "blocked" && now != "blocked") context.OrderTriggers.Add((OrderTriggers.BlockerCleared, taskId));
    }

    /// <summary>
    /// Runs the one active order for this trigger, at most once per <paramref name="triggerRevision"/> (the task's changeRevision, or
    /// the lock's generation). Disabled and expired orders never run.
    /// </summary>
    public static void Run(AggregateContext context, string on, string target, string triggerRevision)
    {
        if (!context.OrdersEnabled || new BoardState(context.State).Orders is not { } orders) return;
        var order = orders.Orders(create: true).FirstOrDefault(item => SameTrigger(item, on, target));
        if (order is null || order.Disabled || order.ExpiresAt <= context.Now || order.LastRun == triggerRevision) return;
        var revision = orders.NextRevision();
        order.LastRun = triggerRevision; order.RunRevision = revision; order.Runs += 1; order.LastRunAt = context.Now;
        var problems = order.Steps.Select(step => Execute(context, order, step, on, target, revision) is { } problem ? step.Do + ": " + problem : null).OfType<string>().ToArray();
        if (problems.Length == 0) Log(orders, context, order.Id, "ran on " + on + " " + target);
        else Fail(context, orders, order, string.Join("; ", problems));
    }

    /// <summary>A watched lock's claim was released (<see cref="OrderFire"/>): runs this board's <c>claim_released</c> order, or stops watching when none names the lock.</summary>
    public static RelayReply OnFire(AggregateContext context)
    {
        if (!OrderFire.Decode(context.Args).TryGet(out var fire, out var malformed)) return Malformed(malformed);
        if (!context.OrdersEnabled) return Ok(new { ran = false });
        var resource = fire.ResourceId;
        if (new BoardState(context.State).Orders is not { } orders || !orders.Orders(create: true).Any(order => SameTrigger(order, OrderTriggers.ClaimReleased, resource)))
        {
            SendWatch(context, resource, false, "");
            return Ok(new { ran = false });
        }
        Run(context, OrderTriggers.ClaimReleased, resource, "lock:" + fire.Generation);
        return Ok(new { ran = true });
    }

    /// <summary>A run of an order failed (<see cref="OrderFailed"/>): one failed run, counted once.</summary>
    public static RelayReply OnFailed(AggregateContext context)
    {
        if (!OrderFailed.Decode(context.Args).TryGet(out var failed, out var malformed)) return Malformed(malformed);
        if (!context.OrdersEnabled || new BoardState(context.State).Orders is not { } orders || orders.Order(failed.OrderId) is not { } order) return Ok(new { counted = false });
        // A failure from an earlier run of a replaced order, or a second failure in the same run, is not counted again.
        if (order.RunRevision != failed.Run || order.FailedRun == failed.Run)
        {
            Log(orders, context, order.Id, "also failed: " + failed.Reason);
            return Ok(new { counted = false });
        }
        Fail(context, orders, order, failed.Reason);
        return Ok(new { counted = true });
    }

    /// <summary>A lock refused to watch for an order (<see cref="OrderWatchFailed"/>): the order can never fire, so each refusal counts as a failed run.</summary>
    public static RelayReply OnWatchFailed(AggregateContext context)
    {
        if (!OrderWatchFailed.Decode(context.Args).TryGet(out var failed, out var malformed)) return Malformed(malformed);
        if (!context.OrdersEnabled || new BoardState(context.State).Orders is not { } orders || orders.Order(failed.OrderId) is not { } order) return Ok(new { counted = false });
        Fail(context, orders, order, failed.Reason);
        return Ok(new { counted = true });
    }

    /// <summary>Lock: starts or stops telling one board when this lock's claim is released (<see cref="OrderWatch"/>).</summary>
    public static RelayReply Watch(AggregateContext context)
    {
        if (!OrderWatch.Decode(context.Args).TryGet(out var request, out var malformed)) return Malformed(malformed);
        var watches = new LockOrderState(context.State);
        var watch = request.Watch;
        if (watch && !watches.IsWatchedBy(request.BoardKey) && watches.WatchCount >= RelayLimits.MaxOrderWatchBoards)
        {
            context.Send(request.BoardKey, new OrderWatchFailed(context.Repository, request.BoardId, request.OrderId,
                "resource " + context.Key + " is already watched by " + RelayLimits.MaxOrderWatchBoards + " boards"));
            watch = false;
        }
        else if (watch) watches.Watch(request.BoardKey, request.BoardId);
        else watches.Unwatch(request.BoardKey);
        return Ok(new { watching = watch });
    }

    /// <summary>
    /// Lock: an order's release of the claim its task's owner holds (<see cref="OrderRelease"/>). Applied once per order run; a claim the
    /// owner no longer holds is left alone, and a claim with an unresolved action is reported back to the board as a failed run.
    /// </summary>
    public static RelayReply Release(AggregateContext context)
    {
        if (!OrderRelease.Decode(context.Args).TryGet(out var release, out var malformed)) return Malformed(malformed);
        var state = context.State;
        if (!new LockOrderState(state).Remember(release.BoardKey + "|" + release.OrderId + "|" + release.Run)) return Ok(new { released = false, repeated = true });
        var holder = release.HolderSessionId;
        if (Text(state, LockState.HolderKey) != holder || Date(state, LockState.LeaseUntilKey) <= context.Now) return Ok(new { released = false });
        // Only the grant taken for the order's task claim: a session that already claimed the resource again (for its next task, or
        // directly) keeps it.
        if (Text(state, LockState.ClaimForKey) != release.ClaimFor) return Ok(new { released = false, claimedAgain = true });
        if (state[LockState.BlockersKey] is JsonObject { Count: > 0 })
        {
            context.Send(release.BoardKey, new OrderFailed(context.Repository, release.BoardId, release.OrderId, release.Run, "claim on " + context.Key + " has an unresolved action"));
            return Ok(new { released = false, failed = true });
        }
        LockAggregate.Release(context, holder);
        return Ok(new { released = true });
    }

    /// <summary>Lock: a released claim tells each watching board, with the lock's generation as the trigger revision (<see cref="OrderFire"/>).</summary>
    public static void OnLockReleased(AggregateContext context)
    {
        foreach (var (boardKey, boardId) in new LockOrderState(context.State).WatchingBoards)
            context.Send(boardKey, new OrderFire(context.Repository, boardId, context.Key, Number(context.State, LockState.GenerationKey)));
    }

    /// <summary>Runs one action; returns why it could not run, or null.</summary>
    private static string? Execute(AggregateContext context, OrderRecord order, OrderStep step, string on, string target, long revision)
    {
        var board = new BoardState(context.State);
        var id = order.Id;
        var triggerTask = on == OrderTriggers.ClaimReleased ? "" : target;
        switch (step.Do)
        {
            case OrderActions.ReleaseClaims:
            {
                var taskId = step.TaskId ?? triggerTask;
                if (board.Task(taskId) is not { } task) return "unknown task " + taskId;
                var owner = task.Owner;
                if (owner.Length == 0) return null;
                if (TaskActions.HasUnresolved(task)) return "task " + taskId + " has an unresolved action";
                task.ReleaseClaim(context.Now);
                // The triggering task's own change is already marked and versioned by its update; any other task is changed here.
                if (!(context.Operation is RelayTools.TaskUpdate or RelayTools.ActionReport && Text(context.Args, "taskId") == taskId))
                {
                    task.Version += 1;
                    board.MarkChanged(task);
                }
                var boardId = Text(context.Args, "boardId", "default");
                foreach (var resource in task.ClaimedResources.Distinct(StringComparer.Ordinal))
                    context.Send(resource, new OrderRelease(context.Repository, boardId, context.Key, owner, ClaimKey(boardId, taskId, task.ClaimGeneration), id, revision));
                return null;
            }
            case OrderActions.Offer:
            {
                var taskId = step.TaskId ?? "";
                if (board.Task(taskId) is not { } task) return "unknown task " + taskId;
                // The next-work checks apply, except the target-session match: an order never works around a provider rejection, and
                // offers only work the session could claim now.
                if (TaskWorkflow.OfferProblem(board, task, step.SessionId, context.Now) is { } problem) return "task " + taskId + " " + problem;
                var item = new JsonObject
                {
                    ["kind"] = AttentionKinds.NextWork, ["taskId"] = taskId, ["subject"] = id, ["priority"] = task.Workflow!.Priority,
                    ["note"] = "order " + id + ": " + on + " " + target + "; claim it with task_claim", ["revision"] = revision
                };
                if (triggerTask.Length > 0) item["refs"] = new JsonArray(triggerTask);
                AttentionSources.Emit(context, step.SessionId, Operations.AttentionRaise, Mark(item, id, revision));
                return null;
            }
            case OrderActions.Notify:
            {
                var item = new JsonObject
                {
                    ["kind"] = AttentionKinds.Info, ["subject"] = id, ["note"] = step.Note ?? "order " + id + ": " + on + " " + target, ["revision"] = revision
                };
                if (triggerTask.Length > 0) item["taskId"] = triggerTask;
                AttentionSources.Emit(context, step.SessionId, Operations.AttentionRaise, Mark(item, id, revision));
                return null;
            }
            default: return "unknown action";
        }
    }

    /// <summary>Names the order and run an order's raise belongs to; if the session refuses it, the dispatcher reports a failed run.</summary>
    private static JsonObject Mark(JsonObject item, string orderId, long run)
    {
        item[OrderIdField] = orderId;
        item[OrderRunField] = run;
        return item;
    }

    private const string OrderIdField = Operations.OrderIdField;
    private const string OrderRunField = Operations.OrderRunField;

    /// <summary>Counts one failed run, logs it, raises <c>order_failed</c> to the managers, and disables the order at the failure limit.</summary>
    private static void Fail(AggregateContext context, OrdersState orders, OrderRecord order, string reason)
    {
        var id = order.Id;
        var failures = order.Failures + 1;
        order.Failures = failures;
        order.FailedRun = order.RunRevision;
        order.LastError = Cap(reason, RelayLimits.MaxOrderNoteLength);
        order.LastErrorAt = context.Now;
        var disabled = failures >= RelayLimits.MaxOrderFailures;
        if (disabled) order.Disabled = true;
        Log(orders, context, id, (disabled ? "failed and disabled: " : "failed: ") + reason);
        var item = new JsonObject
        {
            ["kind"] = AttentionKinds.OrderFailed, ["subject"] = id,
            ["note"] = "order " + id + (disabled ? " disabled after " + failures + " failures: " : " failed: ") + reason, ["revision"] = orders.NextRevision()
        };
        if (order.Trigger is { } trigger && trigger.TaskId is { Length: > 0 } taskId) item["taskId"] = taskId;
        AttentionSources.ToManagers(context, Operations.AttentionRaise, item);
    }

    private static void CloseFailure(AggregateContext context, string id, long revision) =>
        AttentionSources.ToManagers(context, Operations.AttentionClose, new JsonObject { ["subject"] = id, ["kinds"] = new JsonArray(AttentionKinds.OrderFailed), ["revision"] = revision });

    /// <summary>Asks each lock a <c>claim_released</c> order newly names (or the written order names) to tell this board, and the ones no order names any more to stop.</summary>
    private static void UpdateWatches(AggregateContext context, HashSet<string> before, HashSet<string> after, string? written, string orderId)
    {
        foreach (var resource in after.Where(resource => !before.Contains(resource) || resource == written)) SendWatch(context, resource, true, orderId);
        foreach (var resource in before.Where(resource => !after.Contains(resource))) SendWatch(context, resource, false, orderId);
    }

    private static void SendWatch(AggregateContext context, string resource, bool watch, string orderId) =>
        context.Send(resource, new OrderWatch(context.Repository, Text(context.Args, "boardId", "default"), context.Key, orderId, watch));

    private static HashSet<string> Watched(OrdersState orders) =>
        orders.Orders().Select(order => order.Trigger).OfType<OrderTrigger>().Where(trigger => trigger.On == OrderTriggers.ClaimReleased)
            .Select(trigger => trigger.ResourceId).ToHashSet(StringComparer.Ordinal);

    /// <summary>Returns the invalid field (prefixed with a dot), or null with the normalized trigger. A task trigger must name a task on this board.</summary>
    private static string? ReadTrigger(JsonNode? node, BoardState board, out OrderTrigger trigger)
    {
        trigger = new OrderTrigger(new JsonObject());
        if (node is not JsonObject source) return "";
        var on = Text(source, OrderTrigger.OnKey);
        if (!OrderTriggers.All.Contains(on)) return ".on";
        var field = OrderTrigger.TargetKey(on);
        if (source.FirstOrDefault(property => property.Key != OrderTrigger.OnKey && property.Key != field) is { Key: { } extra }) return "." + extra;
        if (!Identifier(source[field], RelayLimits.MaxIdentifierLength, out var target) || field == OrderTrigger.TaskIdKey && board.Task(target) is null) return "." + field;
        trigger = OrderTrigger.Create(on, target);
        return null;
    }

    /// <summary>Returns the invalid field (prefixed with a dot), or null with the normalized action. Tasks must be on this board.</summary>
    private static string? ReadAction(JsonNode? node, string on, BoardState board, out JsonObject action)
    {
        action = new JsonObject();
        if (node is not JsonObject source) return "";
        var kind = Text(source, OrderStep.DoKey);
        if (!OrderActions.All.Contains(kind)) return ".do";
        string[] allowed = kind switch
        {
            OrderActions.ReleaseClaims => [OrderStep.TaskIdKey],
            OrderActions.Offer => [OrderStep.TaskIdKey, OrderStep.SessionIdKey],
            _ => [OrderStep.SessionIdKey, OrderStep.NoteKey]
        };
        if (source.FirstOrDefault(property => property.Key != OrderStep.DoKey && !allowed.Contains(property.Key)) is { Key: { } extra }) return "." + extra;
        action[OrderStep.DoKey] = kind;
        // release_claims on a task trigger defaults to that task; a claim_released order must name the task whose claims it releases.
        var needsTask = kind == OrderActions.Offer || kind == OrderActions.ReleaseClaims && (on == OrderTriggers.ClaimReleased || source.ContainsKey(OrderStep.TaskIdKey));
        if (needsTask)
        {
            if (!Identifier(source[OrderStep.TaskIdKey], RelayLimits.MaxIdentifierLength, out var taskId) || board.Task(taskId) is null) return "." + OrderStep.TaskIdKey;
            action[OrderStep.TaskIdKey] = taskId;
        }
        if (kind != OrderActions.ReleaseClaims)
        {
            if (!Identifier(source[OrderStep.SessionIdKey], RelayLimits.MaxIdentifierLength, out var sessionId)) return "." + OrderStep.SessionIdKey;
            action[OrderStep.SessionIdKey] = sessionId;
        }
        if (source.ContainsKey(OrderStep.NoteKey))
        {
            if (!Identifier(source[OrderStep.NoteKey], RelayLimits.MaxOrderNoteLength, out var note)) return "." + OrderStep.NoteKey;
            action[OrderStep.NoteKey] = note;
        }
        return null;
    }

    /// <summary>The identity of one task claim, as a lock records it for the grant it serves: board, task and claim generation.</summary>
    public static string ClaimKey(string boardId, string taskId, long claimGeneration) => boardId + "/" + taskId + "#" + claimGeneration;

    private static bool SameTrigger(OrderRecord order, string on, string target) => order.Trigger is { } trigger && trigger.On == on && trigger.Target == target;

    /// <summary>
    /// The write revision moves only when an administrator puts or removes an order. An optional <c>expectedRevision</c> must match it,
    /// as with <c>standing_put</c>, so two administrators cannot overwrite each other unseen.
    /// </summary>
    private static RelayReply? WriteConflict(JsonObject args, OrdersState orders)
    {
        var current = orders.WriteRevision;
        if (args["expectedRevision"] is null) return null;
        return args["expectedRevision"] is JsonValue expected && long.TryParse(expected.ToJsonString(), out var revision) && revision == current
            ? null : Error(RelayErrorCodes.VersionConflict, new { currentRevision = current }, current);
    }

    private static void Log(OrdersState orders, AggregateContext context, string orderId, string what) => orders.Log(context.Now, orderId, Cap(what, RelayLimits.MaxOrderNoteLength));

    private static string Cap(string text, int maximum) => text.Length <= maximum ? text : text[..(maximum - 1)] + "…";

    private static bool Identifier(JsonNode? node, int maximum, out string value)
    {
        value = node is JsonValue text && text.TryGetValue<string>(out var read) ? read : "";
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    }

    private static RelayReply Invalid(string field, string? id = null) => Error(InvalidArguments, id is null ? new { field } : (object)new { field, id });
}

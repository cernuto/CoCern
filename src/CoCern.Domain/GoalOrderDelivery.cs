using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>Resolve an order target at control, recheck the board, then recheck the target before raising the advisory offer.</summary>
public static class GoalOrderDelivery
{
    public const string VerifyOperation = "__goal_order_verify";
    public const string BoardOperation = "__goal_order_board";
    public const string DeliverOperation = "__goal_order_deliver";

    internal static void Queue(AggregateContext context, string session, AttentionContent item, string orderId, long run)
    {
        var boardId = Text(context.Args, "boardId", "default");
        var raise = new AttentionRaise(context.Repository, item with { BoardId = boardId }, orderId, run);
        var payload = new JsonObject { ["repositoryId"] = context.Repository, ["boardId"] = boardId, ["boardKey"] = context.Key, ["targetSession"] = session, ["orderId"] = orderId, ["run"] = run, ["taskId"] = item.TaskId, ["raise"] = raise.ToJson() };
        var identity = new JsonArray(context.Key, orderId, run, session, item.TaskId).ToJsonString();
        payload["requestId"] = "goal-order-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, VerifyOperation, payload);
    }

    private static void Failed(AggregateContext context, string reason)
    {
        var args = context.Args;
        context.Send(Text(args, "boardKey"), new OrderFailed(context.Repository, Text(args, "boardId", "default"), Text(args, "orderId"), Number(args, "run"), reason));
    }

    private static RelayReply FailOnBoard(AggregateContext context, string reason)
    {
        var args = context.Args;
        var failed = new OrderFailed(context.Repository, Text(args, "boardId", "default"), Text(args, "orderId"), Number(args, "run"), reason);
        var local = new AggregateContext(context.State, context.Caller, OrderFailed.Operation, failed.ToJson(), context.Now)
        {
            Key = context.Key, OrdersEnabled = context.OrdersEnabled
        };
        var result = StandingOrders.OnFailed(local);
        if (result.Ok) context.Effects.AddRange(local.Effects);
        return result;
    }

    private static VerifiedNativeParticipant? Current(AggregateContext context)
    {
        var control = new ControlState(context.State);
        var session = Text(context.Args, "targetSession");
        var entry = control.Entry(session);
        if (entry is null || entry.RepositoryId != context.Repository || entry.NativeId.Length == 0) return null;
        var current = control.CurrentNativeSession(entry.NativeId, context.Repository);
        return current?.SessionId == session ? current : null;
    }

    internal static RelayReply Verify(AggregateContext context)
    {
        if (!context.OrdersEnabled) return Ok(new { queued = false });
        if (Current(context) is not { } actor) { Failed(context, "goal offer target is not a current verified native session"); return Ok(new { queued = false }); }
        var payload = context.Args.DeepClone().AsObject();
        payload["verifiedParticipant"] = actor.ParticipantId;
        context.Emit(AggregateKinds.Board, Text(payload, "boardKey"), BoardOperation, payload);
        return Ok(new { queued = true });
    }

    internal static RelayReply CheckBoard(AggregateContext context)
    {
        var args = context.Args;
        var board = new BoardState(context.State);
        var session = Text(args, "targetSession");
        var id = Text(args, "orderId");
        var run = Number(args, "run");
        if (!context.OrdersEnabled || board.Orders?.Order(id) is not { } order || order.Disabled || order.ExpiresAt <= context.Now || order.RunRevision != run) return Ok(new { queued = false });
        var taskId = Text(args, "taskId");
        if (!order.Steps.Any(step => step.Do == OrderActions.Offer && step.TaskId == taskId && step.SessionId == session) || board.Task(taskId) is not { } task) return Ok(new { queued = false });
        var participant = Text(args, "verifiedParticipant");
        var actor = participant.Length == 0 ? null : new VerifiedNativeParticipant(participant, session, context.Repository);
        if (TaskWorkflow.OfferProblem(board, task, session, context.Now, actor) is { } problem)
        {
            return FailOnBoard(context, "goal offer refused: " + problem);
        }
        context.Emit(AggregateKinds.Control, AggregateKinds.ControlKey, DeliverOperation, args.DeepClone().AsObject());
        return Ok(new { queued = true });
    }

    internal static RelayReply Deliver(AggregateContext context)
    {
        if (!context.OrdersEnabled) return Ok(new { delivered = false });
        var actor = Current(context);
        if (actor is null || actor.ParticipantId != Text(context.Args, "verifiedParticipant")) { Failed(context, "goal offer target is no longer current"); return Ok(new { delivered = false }); }
        if (context.Args["raise"] is not JsonObject payload || !AttentionRaise.Decode(payload).TryGet(out var raise, out _)) { Failed(context, "goal offer is malformed"); return Ok(new { delivered = false }); }
        var delivery = raise.ToJson();
        delivery["__goalOrderBoardKey"] = Text(context.Args, "boardKey");
        context.Emit(AggregateKinds.Session, actor.SessionId, AttentionRaise.Operation, delivery);
        return Ok(new { delivered = true });
    }
}

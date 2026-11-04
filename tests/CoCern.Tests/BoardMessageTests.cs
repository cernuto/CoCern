using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The board's typed operations: the hub's direct requests to a board, and the order effects between boards and locks. Each reads back
/// exactly what it writes and names a missing or mistyped field; a board or lock given a malformed one answers
/// <see cref="RelayErrorCodes.MalformedEffect"/> and changes nothing; and every order effect is advice.
/// </summary>
public sealed class BoardMessageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);

    private static string Request<T>(T request) where T : class, IInternalRequest<T>
    {
        Assert.True(T.Decode(request.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
        Assert.Equal(request.ToJson().ToJsonString(), decoded.ToJson().ToJsonString());
        return decoded.ToJson().ToJsonString();
    }

    private static T Effect<T>(T message) where T : class, IEffectMessage<T>
    {
        Assert.True(T.Decode(message.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
        return decoded;
    }

    private static IReadOnlyList<string> RequestMissing<T>(JsonObject payload) where T : class, IInternalRequest<T>
    {
        Assert.False(T.Decode(payload).TryGet(out _, out var fields));
        return fields;
    }

    private static IReadOnlyList<string> EffectMissing<T>(JsonObject payload) where T : class, IEffectMessage<T>
    {
        Assert.False(T.Decode(payload).TryGet(out _, out var fields));
        return fields;
    }

    private static JsonObject Without(JsonObject payload, params string[] fields)
    {
        foreach (var field in fields) payload.Remove(field);
        return payload;
    }

    [Fact]
    public void Board_requests_read_back_name_what_they_are_missing_and_name_their_board_only_when_given()
    {
        Request(new TaskUnclaim("repo", "ops", "T-1", "s1", 2, "claim-1"));
        // A call that named no board reaches the default board, and the board labels what it emits as that call did.
        Assert.False(JsonNode.Parse(Request(new TaskUnclaim("repo", null, "T-1", "s1", 2, "claim-1")))!.AsObject().ContainsKey("boardId"));
        Request(new NextWorkRead("repo", "ops", "s1", "T-1", 5, ["rig-1", "rig-2"]));
        Assert.Equal("""{"repositoryId":"repo","sessionId":"s1"}""", Request(new NextWorkRead("repo", null, "s1")));
        Request(new StandingRead("repo", "default"));
        Request(new StandingPresentationRequest("repo", "default", "s1", "p1", "task_update:r1", """{"k":"standing_proposal","id":"p1","b":"default","line":{"text":"Confirm first"}}"""));
        Request(new OrdersRead("repo", "default"));
        Request(new TaskArchiveSweep("repo", "ops"));
        Request(new HubStandingProposal("repo", "default", "tip-board-reread", "tip-x", "Read the board as changes."));

        var unclaim = new TaskUnclaim("repo", null, "T-1", "s1", 2, "claim-1").ToJson();
        unclaim["claimGeneration"] = 0;
        Assert.Equal(["claimGeneration", "claimRequestId"], RequestMissing<TaskUnclaim>(Without(unclaim, "claimRequestId")));
        var next = new NextWorkRead("repo", null, "s1").ToJson();
        next["busyResources"] = new JsonArray("rig-1", 2);
        Assert.Equal(["sessionId", "busyResources"], RequestMissing<NextWorkRead>(Without(next, "sessionId")));
        var proposal = new HubStandingProposal("repo", null, "rule", "tip-x", "text").ToJson();
        proposal["line"]!.AsObject().Remove("text");
        Assert.Equal(["line.text"], RequestMissing<HubStandingProposal>(proposal));
        Assert.Equal(["repositoryId"], RequestMissing<StandingRead>(new JsonObject { ["boardId"] = "default" }));
    }

    [Theory]
    [InlineData("repositoryId")]
    [InlineData("sessionId")]
    [InlineData("proposalId")]
    [InlineData("presentationRequest")]
    [InlineData("expectedEffect")]
    public void A_standing_presentation_request_requires_its_identity_correlation_and_exact_effect(string missing)
    {
        var request = new StandingPresentationRequest("repo", null, "s1", "p1", "check_inbox:r1", """{"k":"standing_proposal","id":"p1","b":"default","line":{"text":"Confirm first"}}""");
        Assert.False(request.ToJson().ContainsKey("boardId"));
        Assert.Equal([missing], RequestMissing<StandingPresentationRequest>(Without(request.ToJson(), missing)));
        var wrongShape = request.ToJson();
        wrongShape["expectedEffect"] = new JsonArray("not an effect");
        Assert.Equal(["expectedEffect"], RequestMissing<StandingPresentationRequest>(wrongShape));
    }

    [Fact]
    public void A_malformed_standing_presentation_request_changes_nothing_and_stays_internal()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var args = new StandingPresentationRequest("repo", null, "s1", "p1", "check_inbox:r1", "{}").ToJson();
        args.Remove("expectedEffect");
        var before = board.Value.Json;
        var refused = CoordinationStateMachine.Apply(board, "board", "key", Hub, Operations.PresentStanding, args, Now);
        Assert.False(refused.Ok);
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
        Assert.Contains("expectedEffect", refused.DataJson);
        Assert.Equal(before, board.Value.Json);
        Assert.Empty(board.Effects);
        var external = CoordinationStateMachine.Apply(board, "board", "key", Hub with { IsInternal = false }, Operations.PresentStanding, args, Now);
        Assert.False(external.Ok);
        Assert.Equal(RelayErrorCodes.InternalOnly, external.Code);
        Assert.Equal(before, board.Value.Json);
    }

    [Fact]
    public void Order_messages_read_back_and_name_what_they_are_missing()
    {
        Assert.Equal(new OrderWatch("repo", "default", "repo/default", "o-1", true), Effect(new OrderWatch("repo", "default", "repo/default", "o-1", true)));
        // A board with no order on the lock any more stops watching without naming one; a watch always names its order.
        Assert.Equal(new OrderWatch("repo", "default", "repo/default", "", false), Effect(new OrderWatch("repo", "default", "repo/default", "", false)));
        Assert.Equal(["orderId"], EffectMissing<OrderWatch>(new OrderWatch("repo", "default", "repo/default", "", true).ToJson()));
        var release = new OrderRelease("repo", "default", "repo/default", "s1", "default/T-1#1", "o-1", 3);
        Assert.Equal(release, Effect(release));
        Assert.Equal(["claimFor", "run"], EffectMissing<OrderRelease>(Without(release.ToJson(), "claimFor", "run")));
        Assert.Equal(new OrderFire("repo", "default", "rig-1", 4), Effect(new OrderFire("repo", "default", "rig-1", 4)));
        var fire = new OrderFire("repo", "default", "rig-1", 4).ToJson();
        fire["generation"] = "4";
        Assert.Equal(["boardId", "generation"], EffectMissing<OrderFire>(Without(fire, "boardId")));
        Assert.Equal(new OrderFailed("repo", "default", "o-1", 3, "late"), Effect(new OrderFailed("repo", "default", "o-1", 3, "late")));
        Assert.Equal(new OrderWatchFailed("repo", "default", "o-1", "full"), Effect(new OrderWatchFailed("repo", "default", "o-1", "full")));
        // A refused watch belongs to no run: it is its own message, and the run failure always names its run.
        Assert.Equal(["run"], EffectMissing<OrderFailed>(new JsonObject { ["repositoryId"] = "repo", ["boardId"] = "default", ["orderId"] = "o-1", ["watch"] = true, ["reason"] = "full" }));
        Assert.Equal(["reason"], EffectMissing<OrderWatchFailed>(Without(new OrderWatchFailed("repo", "default", "o-1", "full").ToJson(), "reason")));
    }

    [Fact]
    public void A_board_and_a_lock_answer_a_malformed_request_or_order_effect_by_name_and_change_nothing()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        RelayReply Board(string operation, JsonObject args)
        {
            args["repositoryId"] = "repo";
            return CoordinationStateMachine.Apply(board, AggregateKinds.Board, "repo/default", Hub, operation, args, Now, ordersEnabled: true);
        }
        Assert.True(Board(RelayTools.TaskUpdate, new() { ["taskId"] = "T-1", ["expectedVersion"] = 0, ["requestId"] = "create" }).Ok);
        Assert.True(Board(RelayTools.TaskClaim, new() { ["taskId"] = "T-1", ["sessionId"] = "s1", ["expectedVersion"] = 1, ["requestId"] = "claim" }).Ok);
        var before = board.Value.Json;
        void Refused(string operation, JsonObject args, params string[] fields)
        {
            var reply = Board(operation, args);
            Assert.Equal(RelayErrorCodes.MalformedEffect, reply.Code);
            Assert.Equal(fields, JsonNode.Parse(reply.DataJson)!["fields"]!.AsArray().Select(field => field!.GetValue<string>()));
        }
        Refused(TaskUnclaim.Operation, Without(new TaskUnclaim("repo", null, "T-1", "s1", 1, "claim").ToJson(), "claimRequestId"), "claimRequestId");
        Refused(NextWorkRead.Operation, new JsonObject(), "sessionId");
        Refused(OrderFire.Operation, Without(new OrderFire("repo", "default", "rig-1", 1).ToJson(), "boardId"), "boardId");
        Refused(OrderFailed.Operation, new JsonObject { ["boardId"] = "default", ["orderId"] = "o-1", ["watch"] = true, ["reason"] = "full" }, "run");
        Refused(OrderWatchFailed.Operation, new JsonObject { ["boardId"] = "default", ["reason"] = "full" }, "orderId");
        Refused(HubStandingProposal.Operation, new JsonObject { ["rule"] = "r" }, "line");
        Assert.Equal(before, board.Value.Json);
        // The well-formed rollback releases the claim it names.
        Assert.True(Board(TaskUnclaim.Operation, new TaskUnclaim("repo", null, "T-1", "s1", 1, "claim").ToJson()).Ok);
        Assert.Null(JsonNode.Parse(board.Value.Json)!["tasks"]!["T-1"]!["owner"]);

        var printer = new StateEnvelope<CoordinationDocument>();
        RelayReply Lock(string operation, JsonObject args)
        {
            args["repositoryId"] = "repo";
            return CoordinationStateMachine.Apply(printer, AggregateKinds.Lock, "rig-1", Hub, operation, args, Now, ordersEnabled: true);
        }
        Assert.Equal(RelayErrorCodes.MalformedEffect, Lock(OrderWatch.Operation, Without(new OrderWatch("repo", "default", "repo/default", "o-1", true).ToJson(), "boardKey")).Code);
        Assert.Equal(RelayErrorCodes.MalformedEffect, Lock(OrderRelease.Operation, Without(new OrderRelease("repo", "default", "repo/default", "s1", "default/T-1#1", "o-1", 1).ToJson(), "holderSessionId")).Code);
        Assert.Equal("{}", printer.Value.Json);
    }

    [Fact]
    public void Order_effects_are_advice_dropped_when_refused_and_when_orders_are_off_and_board_requests_go_to_the_board()
    {
        foreach (var operation in new[] { OrderWatch.Operation, OrderRelease.Operation, OrderFire.Operation, OrderFailed.Operation, OrderWatchFailed.Operation })
        {
            Assert.Equal(DeliveryClass.Advisory, EffectMessages.DeliveryOf(operation));
            Assert.True(Operations.IsOrder(operation), operation);
            var effect = new DurableEffect("e", EffectMessages.ByOperation[operation].TargetKind, "k", operation, "{}", Now);
            Assert.Equal(EffectDelivery.Failure.Drop, EffectDelivery.OnFailure(effect, new JsonObject(), RelayErrorCodes.MalformedEffect, AggregateKinds.Lock, ordersOn: true));
            Assert.Equal(EffectDelivery.Failure.Retry, EffectDelivery.OnFailure(effect, new JsonObject(), RelayErrorCodes.StorageFailure, AggregateKinds.Lock, ordersOn: true));
            Assert.True(EffectDelivery.Dropped(effect, attention: null, ordersOn: false));
        }
        Assert.Equal([AggregateKinds.Lock, AggregateKinds.Lock, AggregateKinds.Board, AggregateKinds.Board, AggregateKinds.Board],
            new[] { OrderWatch.TargetKind, OrderRelease.TargetKind, OrderFire.TargetKind, OrderFailed.TargetKind, OrderWatchFailed.TargetKind });
        foreach (var operation in new[] { Operations.TaskUnclaim, Operations.NextWork, Operations.Standing, Operations.Orders, Operations.TaskArchive, Operations.StandingProposeHub })
            Assert.Equal(AggregateKinds.Board, InternalRequests.ByOperation[operation].TargetKind);
    }
}

using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The typed messages between aggregates: each reads back exactly what it writes, a missing or mistyped field is named (never its value),
/// and a handler given one answers <see cref="RelayErrorCodes.MalformedEffect"/> rather than a silent Ok.
/// </summary>
public sealed class EffectMessageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);

    private static T RoundTrip<T>(T message) where T : class, IEffectMessage<T>
    {
        Assert.True(T.Decode(message.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
        return decoded;
    }

    private static IReadOnlyList<string> Malformed<T>(JsonObject payload) where T : class, IEffectMessage<T>
    {
        Assert.False(T.Decode(payload).TryGet(out _, out var fields));
        return fields;
    }

    private static JsonObject Without(JsonObject payload, params string[] fields)
    {
        var copy = payload.DeepClone().AsObject();
        foreach (var field in fields) copy.Remove(field);
        return copy;
    }

    [Fact]
    public void A_board_work_report_reads_back_and_names_its_missing_fields()
    {
        var report = new ActionWorkReport("repo", "s1", "ops board", "t/1", "deploy", 2, true, Now);
        Assert.Equal(report, RoundTrip(report));
        // The action key is built from the fields, escaped as the board and its locks build it.
        Assert.Equal(CoordinationStateMachine.ActionKey(new JsonObject { ["repositoryId"] = "repo", ["boardId"] = "ops board", ["taskId"] = "t/1" }, "deploy", 2), report.ActionKey);
        Assert.Equal(["boardId", "at"], Malformed<ActionWorkReport>(Without(report.ToJson(), "boardId", "at")));
        var mistyped = report.ToJson(); mistyped["attempt"] = 0; mistyped["open"] = "yes";
        Assert.Equal(["attempt", "open"], Malformed<ActionWorkReport>(mistyped));
        Assert.Equal((Operations.SessionWork, AggregateKinds.Session, DeliveryClass.Retry), (ActionWorkReport.Operation, ActionWorkReport.TargetKind, ActionWorkReport.Delivery));
    }

    [Fact]
    public void An_ownership_report_requires_its_time_and_sequence()
    {
        var report = new SessionOwnership("repo", "s1", true, Now, 3);
        Assert.Equal(report, RoundTrip(report));
        Assert.Equal(["at", "seq"], Malformed<SessionOwnership>(Without(report.ToJson(), "at", "seq")));
        Assert.Equal((Operations.SessionOwns, AggregateKinds.Control, DeliveryClass.DropAndCount), (SessionOwnership.Operation, SessionOwnership.TargetKind, SessionOwnership.Delivery));
    }

    [Fact]
    public void Handlers_answer_a_malformed_report_with_its_field_names_and_the_directory_takes_no_board_report()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(CoordinationStateMachine.Apply(session, AggregateKinds.Session, "s1", Hub, Operations.Open, new() { ["repositoryId"] = "repo", ["sessionId"] = "s1", ["principalId"] = "alice", ["sessionToken"] = "secret" }, Now).Ok);
        var work = Without(new ActionWorkReport("repo", "s1", "default", "t", "a", 1, true, Now).ToJson(), "taskId");
        var refused = CoordinationStateMachine.Apply(session, AggregateKinds.Session, "s1", Hub, Operations.SessionWork, work, Now);
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
        Assert.Equal(["taskId"], JsonNode.Parse(refused.DataJson)!["fields"]!.AsArray().Select(field => field!.GetValue<string>()));
        Assert.Empty(session.Effects);

        var control = new StateEnvelope<CoordinationDocument>();
        var unnumbered = Without(new SessionOwnership("repo", "s1", true, Now, 1).ToJson(), "seq");
        Assert.Equal(RelayErrorCodes.MalformedEffect, CoordinationStateMachine.Apply(control, AggregateKinds.Control, AggregateKinds.ControlKey, Hub, Operations.SessionOwns, unnumbered, Now).Code);
        // The board's per-action report is for the session; the directory no longer keeps a ledger of its own.
        Assert.Equal(RelayErrorCodes.UnknownTool, CoordinationStateMachine.Apply(control, AggregateKinds.Control, AggregateKinds.ControlKey, Hub, Operations.SessionWork,
            new ActionWorkReport("repo", "s1", "default", "t", "a", 1, true, Now).ToJson(), Now).Code);
    }

    private static string Json<T>(T message) where T : class, IEffectMessage<T> => message.ToJson().ToJsonString();

    private static SessionRecord Record(JsonObject json)
    {
        Assert.True(SessionRecord.Decode(json).TryGet(out var record, out var fields), string.Join(", ", fields));
        return record;
    }

    private static readonly SessionRecord Opened = SessionRecord.Opened("s1", "T0KEN", "alice", "repo", new JsonObject
    {
        ["label"] = "chat", ["machineId"] = "pc-1", ["hubIncarnation"] = "hub-1", ["role"] = "manager", ["binding"] = new JsonObject { ["roslyn"] = "codelens" },
        // session_open's other arguments are never part of the record, so a client cannot forge freshness or activity through them.
        ["freshUntil"] = "9999-12-31T00:00:00Z", ["activeAt"] = "9999-12-31T00:00:00Z", ["bindingVersion"] = 41, ["requestId"] = "open-1"
    });

    [Fact]
    public void Every_session_lifecycle_message_reads_back_exactly_what_it_writes()
    {
        var fresh = Opened.WithInitialFreshness(Now);
        Assert.Equal(Json(new SessionOpen(fresh)), Json(RoundTrip(new SessionOpen(fresh))));
        Assert.Equal(Json(new SessionOpen(Opened)), Json(RoundTrip(new SessionOpen(Opened))));
        Assert.Equal(new SessionExpire("repo", "s1", 7, Now, Idle: true), RoundTrip(new SessionExpire("repo", "s1", 7, Now, Idle: true)));
        Assert.Equal(new SessionExpire("repo", "s1", 8, Force: true, RequestedBy: "admin"), RoundTrip(new SessionExpire("repo", "s1", 8, Force: true, RequestedBy: "admin")));
        var restore = new SessionRestore("repo", "s1", 7, fresh with { ActiveAt = Now }, Owns: true, Seq: 4, Mail: true);
        Assert.Equal(Json(restore), Json(RoundTrip(restore)));
        Assert.Equal(new SessionExpired("repo", "s1", 7), RoundTrip(new SessionExpired("repo", "s1", 7)));
        Assert.Equal(new SessionClosed("repo", "ops", "s1"), RoundTrip(new SessionClosed("repo", "ops", "s1")));
        var registration = new SessionRegistration("repo", "s1", fresh.Presence with { BindingVersion = 3 });
        Assert.Equal(Json(registration), Json(RoundTrip(registration)));
        Assert.Equal(new SessionActivity("repo", "s1", Now), RoundTrip(new SessionActivity("repo", "s1", Now)));

        // A record takes only the client's own fields, and a role only within its bound.
        Assert.Equal(("chat", "pc-1", "manager", null, null, null), (Opened.Presence.Label, Opened.Presence.MachineId, Opened.Role, Opened.Presence.FreshUntil, Opened.Presence.BindingVersion, Opened.ActiveAt));
        Assert.Null(SessionRecord.Opened("s1", "t", "alice", "repo", new JsonObject { ["role"] = new string('m', RelayLimits.MaxActivityRoleLength + 1) }).Role);
    }

    [Fact]
    public void Each_lifecycle_message_names_the_fields_it_is_missing()
    {
        Assert.Equal(["principalId", "sessionToken"], Malformed<SessionOpen>(Without(Opened.ToJson(), "principalId", "sessionToken")));
        var expire = new SessionExpire("repo", "s1", 7).ToJson();
        Assert.Equal(["closeSeq"], Malformed<SessionExpire>(Without(expire, "closeSeq")));
        var forced = new SessionExpire("repo", "s1", 7, Force: true, RequestedBy: "admin").ToJson();
        Assert.Equal(["requestedBy"], Malformed<SessionExpire>(Without(forced, "requestedBy")));
        var restore = new SessionRestore("repo", "s1", 7, Opened, Owns: true, Seq: 2).ToJson();
        restore["record"]!.AsObject().Remove("principalId");
        Assert.Equal(["record.principalId", "seq"], Malformed<SessionRestore>(Without(restore, "seq")));
        Assert.Equal(["record"], Malformed<SessionRestore>(Without(restore, "record")));
        Assert.Equal(["closeSeq"], Malformed<SessionExpired>(Without(new SessionExpired("repo", "s1", 7).ToJson(), "closeSeq")));
        Assert.Equal(["boardId"], Malformed<SessionClosed>(Without(new SessionClosed("repo", "ops", "s1").ToJson(), "boardId")));
        Assert.Equal(["observedAt", "freshUntil"], Malformed<SessionRegistration>(Without(new SessionRegistration("repo", "s1", Opened.WithInitialFreshness(Now).Presence).ToJson(), "observedAt", "freshUntil")));
        Assert.Equal(["activeAt"], Malformed<SessionActivity>(Without(new SessionActivity("repo", "s1", Now).ToJson(), "activeAt")));
    }

    [Fact]
    public void A_bind_registers_presence_and_a_verified_call_reports_activity_each_as_its_own_message()
    {
        var hub = new AggregateHub();
        var chat = hub.Open("chat", Now);
        hub.DeliverAll(Now);
        Assert.True(hub.Session(chat, RelayTools.SessionBind, new() { ["expectedVersion"] = 0, ["binding"] = new JsonObject { ["v"] = 1 } }, Now).Ok);
        var bound = Assert.Single(hub.Queued);
        Assert.Equal((Operations.Register, DeliveryClass.Retry), (bound.Operation, SessionRegistration.Delivery));
        Assert.DoesNotContain("activeAt", bound.PayloadJson);
        Assert.DoesNotContain(chat.Token, bound.PayloadJson);
        hub.DeliverAll(Now);
        Assert.True(hub.Verify(chat, Now.AddMinutes(1)).Ok);
        var active = Assert.Single(hub.Queued);
        Assert.Equal((Operations.SessionActive, DeliveryClass.DropAndCount), (active.Operation, SessionActivity.Delivery));
        Assert.Equal(["activeAt", "repositoryId", "sessionId"], JsonNode.Parse(active.PayloadJson)!.AsObject().Select(entry => entry.Key).Where(key => !key.StartsWith("__", StringComparison.Ordinal)).Order());
        hub.DeliverAll(Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(1), DateTimeOffset.Parse(hub.Directory()[chat.Id]!["activeAt"]!.GetValue<string>()));
    }

    [Fact]
    public void A_session_and_its_directory_entry_hold_one_record_whichever_copy_of_its_open_lands_first()
    {
        foreach (var role in new[] { "manager", new string('m', RelayLimits.MaxActivityRoleLength + 1) })
        {
            var hub = new AggregateHub { HoldDirectCalls = true };
            var chat = hub.Open("chat", Now, machineId: "pc-1", role: role);
            var drained = hub.Explore(Now, state =>
            {
                var session = Record(state.State(AggregateKinds.Session, chat.Id));
                Assert.Equal(Record(state.Directory()[chat.Id]!.AsObject()).ToJson().ToJsonString(), session.ToJson().ToJsonString());
                Assert.Equal(role.Length <= RelayLimits.MaxActivityRoleLength ? role : null, session.Role);
            });
            Assert.True(drained > 1);
        }
    }

    [Fact]
    public void Inbox_messages_read_back_and_a_direct_message_always_says_what_it_replies_to()
    {
        var direct = new InboxMessage { MessageId = "m1", RepositoryId = "repo", Text = "hi", Author = "alice", SenderSessionId = "s1", SentAt = Now, Supersede = true, Wake = false };
        var delivery = new MessageDelivery(direct, AttentionExpiryDays: 7);
        Assert.Equal(Json(delivery), Json(RoundTrip(delivery)));
        // The keys send_message always stored, in its order, with the reply link even when the message answers none.
        Assert.Equal(["messageId", "repositoryId", "text", "author", "senderSessionId", "sentAt", "replyTo", "supersede", "wake"], direct.ToJson().Select(entry => entry.Key));
        var changed = new MessageDelivery(InboxMessage.TaskChanged("repo", "n1", "ops", "T-1", 3));
        Assert.Equal(Json(changed), Json(RoundTrip(changed)));
        Assert.DoesNotContain("replyTo", Json(changed));
        Assert.Equal(["messageId"], Malformed<MessageDelivery>(Without(delivery.ToJson(), "messageId")));
        var mistyped = delivery.ToJson(); mistyped["wake"] = "no"; mistyped["taskVersion"] = "3";
        Assert.Equal(["taskVersion", "wake"], Malformed<MessageDelivery>(mistyped));
        Assert.Equal((Operations.Deliver, AggregateKinds.Session, DeliveryClass.Retry), (MessageDelivery.Operation, MessageDelivery.TargetKind, MessageDelivery.Delivery));
        Assert.Equal((Operations.SessionTargetWatch, DeliveryClass.Retry), (SessionTargetWatch.Operation, SessionTargetWatch.Delivery));
        Assert.Equal(new SessionTargetWatch("repo", "s1", "repo/ops", "ops"), RoundTrip(new SessionTargetWatch("repo", "s1", "repo/ops", "ops")));
    }

    [Fact]
    public void Attention_raises_closes_and_routes_read_back_and_name_their_missing_fields()
    {
        var item = new AttentionContent
        {
            Kind = AttentionKinds.Blocked, TaskId = "T-1", BoardId = "ops", Blocker = "provider_rejection", Blocking = true, Priority = AttentionWeights.High, Refs = ["T-2"],
            Note = "waiting", Revision = 4, ExpiryDays = 2
        };
        var raise = new AttentionRaise("repo", item, OrderId: "o-1", OrderRun: 3);
        Assert.Equal(Json(raise), Json(RoundTrip(raise)));
        var close = new AttentionClose("repo", new AttentionSelection { TaskIds = ["T-1", "T-2"], BoardId = "ops", Kinds = [AttentionKinds.Blocked], Revision = 5 });
        Assert.Equal(Json(close), Json(RoundTrip(close)));
        foreach (var routed in new[] { AttentionRoute.Raising("manager", raise, "s1"), AttentionRoute.Closing("manager", close) })
            Assert.Equal(Json(routed), Json(RoundTrip(routed)));
        Assert.All(new[] { AttentionRaise.Delivery, AttentionClose.Delivery, AttentionRoute.Delivery }, delivery => Assert.Equal(DeliveryClass.Advisory, delivery));

        Assert.Equal(["kind"], Malformed<AttentionRaise>(Without(raise.ToJson(), "kind")));
        // An order's raise names its run, which a refusal counts as failed.
        Assert.Equal(["orderRun"], Malformed<AttentionRaise>(Without(raise.ToJson(), "orderRun")));
        var mistyped = raise.ToJson(); mistyped["refs"] = new JsonArray("T-2", 7); mistyped["priority"] = true;
        Assert.Equal(["priority", "refs"], Malformed<AttentionRaise>(mistyped));
        // A task's numeric priority, as sources sent it before they gave its level, reads as that level.
        var numeric = raise.ToJson(); numeric["priority"] = 3;
        Assert.Equal(AttentionWeights.High, RoundTrip(AttentionRaise.Decode(numeric).Message!).Item.Priority);
        // A route names its item's fields under item, and carries only a raise or a close.
        var route = AttentionRoute.Raising("manager", raise, "").ToJson(); route["item"]!.AsObject().Remove("kind");
        Assert.Equal(["item.kind"], Malformed<AttentionRoute>(route));
        var unknown = AttentionRoute.Closing("manager", close).ToJson(); unknown["operation"] = Operations.Deliver;
        Assert.Equal(["operation"], Malformed<AttentionRoute>(unknown));
    }

    [Fact]
    public void The_hubs_direct_session_requests_read_back_and_name_their_missing_fields()
    {
        static T Back<T>(T request) where T : class, IInternalRequest<T>
        {
            Assert.True(T.Decode(request.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
            Assert.Equal(request.ToJson().ToJsonString(), decoded.ToJson().ToJsonString());
            return decoded;
        }
        static IReadOnlyList<string> Missing<T>(JsonObject payload) where T : class, IInternalRequest<T>
        {
            Assert.False(T.Decode(payload).TryGet(out _, out var fields));
            return fields;
        }
        var verify = Back(new SessionVerify("repo", "s1", "T0KEN", "hub-1"));
        var standing = Back(new StandingSeen("repo", "s1", "T0KEN", "ops", 4));
        var offer = Back(new AttentionOffer("repo", "s1", "T0KEN") { FocusTaskId = "T-1", FocusBoardId = "ops", SnoozeBoundaries = 2, PageMessageIds = ["m1"] });
        var seen = Back(new AttentionSeen("repo", "s1", "T0KEN", ["m1", "m2"]));
        Back(new AttentionSuggestion("repo", "s1", new AttentionContent { Kind = AttentionKinds.Tip, Subject = "rule", Priority = AttentionWeights.Low, Note = "n", Revision = 1, ExpiryDays = 1 }));
        var claimed = Back(new AttentionClaimed("repo", "s1", "ops", "T-1", 9));
        Assert.Equal(["sessionToken"], Missing<SessionVerify>(Without(verify.ToJson(), "sessionToken")));
        Assert.Equal(["standingRevision"], Missing<StandingSeen>(Without(standing.ToJson(), "standingRevision")));
        Assert.Equal(["sessionId"], Missing<AttentionOffer>(Without(offer.ToJson(), "sessionId")));
        Assert.Equal(["messageIds"], Missing<AttentionSeen>(Without(seen.ToJson(), "messageIds")));
        Assert.Equal(["revision"], Missing<AttentionClaimed>(Without(claimed.ToJson(), "revision")));
        Assert.Equal(["kind"], Missing<AttentionSuggestion>(new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = "s1" }));
    }

    [Fact]
    public void The_session_answers_a_malformed_delivery_attention_item_or_request_with_its_field_names()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        RelayReply Apply(string operation, JsonObject args) => CoordinationStateMachine.Apply(session, AggregateKinds.Session, "s1", Hub, operation, args, Now);
        static IEnumerable<string> Fields(RelayReply reply)
        {
            Assert.Equal(RelayErrorCodes.MalformedEffect, reply.Code);
            return JsonNode.Parse(reply.DataJson)!["fields"]!.AsArray().Select(field => field!.GetValue<string>());
        }
        Assert.True(Apply(Operations.Open, new SessionOpen(Opened).ToJson()).Ok);
        Assert.Equal(["messageId"], Fields(Apply(Operations.Deliver, new JsonObject { ["repositoryId"] = "repo", ["text"] = "hi" })));
        Assert.Equal(["kind"], Fields(Apply(Operations.AttentionRaise, new JsonObject { ["repositoryId"] = "repo", ["taskId"] = "T-1" })));
        Assert.Equal(["kinds"], Fields(Apply(Operations.AttentionClose, new JsonObject { ["repositoryId"] = "repo", ["taskId"] = "T-1", ["kinds"] = "blocked" })));
        Assert.Equal(["boardKey"], Fields(Apply(Operations.SessionTargetWatch, Without(new SessionTargetWatch("repo", "s1", "repo/ops", "ops").ToJson(), "boardKey"))));
        // A request that presents the session's handle is authenticated first, then read.
        Assert.Equal(["standingRevision"], Fields(Apply(Operations.StandingSeen, Without(new StandingSeen("repo", "s1", "T0KEN", "ops", 1).ToJson(), "standingRevision"))));
        Assert.Equal(RelayErrorCodes.SessionDenied, Apply(Operations.StandingSeen, new StandingSeen("repo", "s1", "wrong", "ops", 1).ToJson()).Code);
        Assert.Empty(session.Effects);

        // The hub's own raise is applied as a raise, and its close for a claimed task closes the claimer's next_work item for it.
        Assert.True(Apply(Operations.AttentionSuggest, new AttentionSuggestion("repo", "s1", new AttentionContent { Kind = AttentionKinds.NextWork, TaskId = "T-1", Revision = 2 }).ToJson()).Ok);
        var closed = Apply(Operations.AttentionClaimed, new AttentionClaimed("repo", "s1", AggregateKinds.DefaultBoard, "T-1", 3).ToJson());
        Assert.Equal(1, JsonNode.Parse(closed.DataJson)!["closed"]!.GetValue<int>());
    }

    /// <summary>
    /// Internal operations without a typed message yet. Typing one means taking it off this list (the test fails while a typed operation
    /// is still listed) and lowering <see cref="PinnedUntyped"/>; a new internal operation must be typed, since listing it here would
    /// need the pin raised. So the list only shrinks.
    /// </summary>
    private static readonly string[] Untyped =
    [
        Operations.Resolve, Operations.ResolveAll, Operations.Dispatch,
        Operations.TaskUnclaim, Operations.NextWork, Operations.ClaimState, Operations.Standing, Operations.StandingProposeHub,
        Operations.SweepSessions, Operations.TaskArchive, Operations.SessionOwners, Operations.SessionsOpen,
        Operations.LeaseCheck, Operations.Orders, Operations.OrderFire, Operations.OrderWatch,
        Operations.OrderRelease, Operations.OrderFailed
    ];

    /// <summary>The number of untyped internal operations. It may only go down.</summary>
    private const int PinnedUntyped = 18;

    [Fact]
    public void Lock_messages_read_back_name_their_missing_fields_and_the_lock_answers_a_malformed_one()
    {
        var block = new ActionBlock("repo", "deploy", "repo/default/T-1/deploy/1", "s1");
        Assert.Equal(block, RoundTrip(block));
        Assert.Equal(new ActionUnblock("repo", "deploy", block.ActionKey, "s1", AdministrativeAcceptance: true), RoundTrip(new ActionUnblock("repo", "deploy", block.ActionKey, "s1", AdministrativeAcceptance: true)));
        Assert.False(new ActionUnblock("repo", "deploy", block.ActionKey, "s1").ToJson().ContainsKey("administrativeAcceptance"));
        Assert.Equal(["actionKey"], Malformed<ActionBlock>(Without(block.ToJson(), "actionKey")));
        Assert.Equal(["sessionId"], Malformed<ActionUnblock>(Without(new ActionUnblock("repo", "deploy", block.ActionKey, "s1").ToJson(), "sessionId")));

        // A block that names only its action (the lock once fell back to the action's ID) is malformed, not a different lock entry.
        var lockState = new StateEnvelope<CoordinationDocument>();
        Assert.True(CoordinationStateMachine.Apply(lockState, "lock", "printer", Hub, RelayTools.ClaimAcquire, new() { ["repositoryId"] = "repo", ["sessionId"] = "s1", ["requestId"] = "c" }, Now).Ok);
        var refused = CoordinationStateMachine.Apply(lockState, "lock", "printer", Hub, Operations.Block, Without(block.ToJson(), "actionKey"), Now);
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
        Assert.True(CoordinationStateMachine.Apply(lockState, "lock", "printer", Hub, Operations.Block, block.ToJson(), Now).Ok);
        Assert.Equal([block.ActionKey], JsonNode.Parse(lockState.Value.Json)!["blockers"]!.AsObject().Select(entry => entry.Key));
    }

    [Fact]
    public void A_session_closed_before_it_opened_tells_a_board_whose_task_targets_it()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        var directory = new RelayCaller("relay-directory", false, ["repo"], true);
        Assert.True(CoordinationStateMachine.Apply(session, "session", "s1", directory, Operations.SessionExpire, new SessionExpire("repo", "s1", 1).ToJson(), Now).Ok);
        var watch = CoordinationStateMachine.Apply(session, "session", "s1", Hub, Operations.SessionTargetWatch, new SessionTargetWatch("repo", "s1", "repo/ops", "ops").ToJson(), Now);
        Assert.True(JsonNode.Parse(watch.DataJson)!["closed"]!.GetValue<bool>());
        var told = Assert.Single(session.Effects, effect => effect.Operation == SessionClosed.Operation);
        Assert.Equal(("repo/ops", new SessionClosed("repo", "ops", "s1")), (told.TargetKey, RoundTrip(SessionClosed.Decode(JsonNode.Parse(told.PayloadJson)!.AsObject()).Message!)));

        // A session never opened or closed here is still left alone.
        var unknown = new StateEnvelope<CoordinationDocument>();
        var ignored = CoordinationStateMachine.Apply(unknown, "session", "s2", Hub, Operations.SessionTargetWatch, new SessionTargetWatch("repo", "s2", "repo/ops", "ops").ToJson(), Now);
        Assert.False(JsonNode.Parse(ignored.DataJson)!["watching"]!.GetValue<bool>());
        Assert.Empty(unknown.Effects);
    }

    [Fact]
    public void Every_internal_operation_has_a_typed_message_or_is_on_an_untyped_list_that_only_shrinks()
    {
        var internalOperations = typeof(Operations).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!).Where(Operations.IsInternal).ToList();
        var typed = EffectMessages.ByOperation.Keys.Concat(InternalRequests.ByOperation.Keys).ToList();
        // An operation is an outbox message or a direct request, never both.
        Assert.Empty(EffectMessages.ByOperation.Keys.Intersect(InternalRequests.ByOperation.Keys));
        Assert.Empty(typed.Except(internalOperations));
        Assert.Empty(Untyped.Intersect(typed));
        Assert.Equal(Untyped.Length, Untyped.Distinct().Count());
        Assert.Equal(internalOperations.Order(StringComparer.Ordinal), typed.Concat(Untyped).Order(StringComparer.Ordinal));
        Assert.True(Untyped.Length == PinnedUntyped, "The untyped list holds " + Untyped.Length + " operations, pinned at " + PinnedUntyped + ": type a new operation instead of listing it, and lower the pin when you type one.");
        // Each typed message names the operation it is listed under.
        Assert.All(EffectMessages.ByOperation, entry => Assert.Equal(entry.Key, entry.Value.Operation));
        Assert.All(InternalRequests.ByOperation, entry => Assert.Equal(entry.Key, entry.Value.Operation));
    }
}

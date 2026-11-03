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

    /// <summary>
    /// Internal operations without a typed message yet. Typing one means taking it off this list (the test fails while a typed operation
    /// is still listed) and lowering <see cref="PinnedUntyped"/>; a new internal operation must be typed, since listing it here would
    /// need the pin raised. So the list only shrinks.
    /// </summary>
    private static readonly string[] Untyped =
    [
        Operations.Verify, Operations.Deliver, Operations.Resolve, Operations.ResolveAll, Operations.Dispatch,
        Operations.ClaimState, Operations.StandingSeen,
        Operations.SessionTargetWatch, Operations.SweepSessions, Operations.SessionOwners, Operations.SessionsOpen,
        Operations.AttentionRaise, Operations.AttentionClose, Operations.AttentionOffer, Operations.AttentionSeen, Operations.AttentionRoute,
        Operations.LeaseCheck
    ];

    /// <summary>The number of untyped internal operations. It may only go down.</summary>
    private const int PinnedUntyped = 17;

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
        var watch = CoordinationStateMachine.Apply(session, "session", "s1", Hub, Operations.SessionTargetWatch, new() { ["repositoryId"] = "repo", ["boardKey"] = "repo/ops", ["boardId"] = "ops" }, Now);
        Assert.True(JsonNode.Parse(watch.DataJson)!["closed"]!.GetValue<bool>());
        var told = Assert.Single(session.Effects, effect => effect.Operation == SessionClosed.Operation);
        Assert.Equal(("repo/ops", new SessionClosed("repo", "ops", "s1")), (told.TargetKey, RoundTrip(SessionClosed.Decode(JsonNode.Parse(told.PayloadJson)!.AsObject()).Message!)));

        // A session never opened or closed here is still left alone.
        var unknown = new StateEnvelope<CoordinationDocument>();
        var ignored = CoordinationStateMachine.Apply(unknown, "session", "s2", Hub, Operations.SessionTargetWatch, new() { ["repositoryId"] = "repo", ["boardKey"] = "repo/ops", ["boardId"] = "ops" }, Now);
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

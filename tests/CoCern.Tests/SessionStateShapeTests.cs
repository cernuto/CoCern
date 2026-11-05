using System.Text.Json.Nodes;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;
using static CoCern.Tests.ExhaustiveStateFixture;

namespace CoCern.Tests;

/// <summary>
/// The session's state class declares every key its document stores. These tests build session documents through the aggregate hub, as
/// the hub does (open, bind, mail, acknowledgments, attention, work reports, a close), and check that each holds no key
/// <see cref="SessionState.Shape"/> leaves out, and that the shape is not vacuous: what the sessions wrote covers what it declares.
/// </summary>
public sealed class SessionStateShapeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private const string Repository = AggregateHub.Repository;

    private static RelayReply Effect<T>(AggregateHub hub, Chat chat, T message, DateTimeOffset at) where T : class, IEffectMessage<T>
    {
        var reply = hub.Apply(T.TargetKind, chat.Id, T.Operation, message.ToJson(), AggregateHub.BoardCaller, at);
        Assert.True(reply.Ok, T.Operation + ": " + reply.Code + " " + reply.DataJson);
        return reply;
    }

    private static RelayReply Request<T>(AggregateHub hub, Chat chat, T request, DateTimeOffset at) where T : class, IInternalRequest<T>
    {
        var reply = hub.Session(chat, T.Operation, request.ToJson(), at);
        Assert.True(reply.Ok, T.Operation + ": " + reply.Code + " " + reply.DataJson);
        return reply;
    }

    [Fact]
    public void A_session_stores_only_the_keys_its_state_declares()
    {
        var hub = new AggregateHub();
        var chat = hub.Open("chat", Now, machineId: "pc-1", role: SessionRoles.Manager);
        var peer = hub.Open("peer", Now);
        var gone = hub.Open("gone", Now);
        hub.DeliverAll(Now);

        // Its binding, activity, standing and the board watching it.
        Assert.True(hub.Session(chat, RelayTools.SessionBind, new() { ["expectedVersion"] = 0, ["binding"] = new JsonObject { ["roslyn"] = "codelens" }, ["availability"] = "available" }, Now).Ok);
        Assert.True(hub.Verify(chat, Now).Ok);
        Request(hub, chat, new StandingSeen(Repository, chat.Id, chat.Token, AggregateKinds.DefaultBoard, 3), Now);
        Effect(hub, chat, new SessionTargetWatch(Repository, chat.Id, AggregateHub.BoardKey, AggregateKinds.DefaultBoard), Now);

        // Mail: a superseding status request, a quiet message, a board's notice that becomes an info item, an acknowledgment, and the
        // notice of a message to a session that never opened.
        Assert.True(hub.Send(peer, chat.Id, "status?", Now, supersede: true).Ok);
        Assert.True(hub.Session(peer, RelayTools.SendMessage, new() { ["recipientSessionId"] = chat.Id, ["text"] = "fyi", ["wake"] = false, ["replyTo"] = "m-0" }, Now).Ok);
        Assert.True(hub.Send(peer, chat.Id, "read me", Now).Ok);
        Assert.True(hub.Send(chat, "nobody", "hello?", Now).Ok);
        hub.DeliverAll(Now);
        Effect(hub, chat, new MessageDelivery(InboxMessage.TaskChanged(Repository, "n-1", "ops", "t-9", 2), AttentionExpiryDays: 7), Now);
        var read = hub.Inbox(chat, Now).Single(message => message["text"]?.GetValue<string>() == "read me")["messageId"]!.GetValue<string>();
        Assert.True(hub.Session(chat, RelayTools.Ack, new() { ["messageIds"] = new JsonArray(read) }, Now).Ok);

        // Its own report of waiting on Paul, in a session that stays open.
        Assert.True(hub.Session(peer, RelayTools.Ack, new() { ["waitingFor"] = "Which rig?", [RelayLimits.HookWaitingField] = true, [RelayLimits.HookReportIdField] = "report-1" }, Now).Ok);

        // Attention: a blocking item on another board, an item closed without a revision and one with, an archived task, the hub's own
        // offered work and its close after a claim, an offer, a snooze, and an info item closed as seen.
        Effect(hub, chat, new AttentionRaise(Repository, new AttentionContent
        {
            Kind = AttentionKinds.Blocked, TaskId = "t-1", BoardId = "ops", Subject = "printer", Blocker = AttentionWeights.ProviderRejection, Blocking = true,
            Priority = AttentionWeights.High, Refs = ["t-2"], Note = "waiting on the provider", Revision = 2, ExpiryDays = 3
        }), Now);
        Effect(hub, chat, new AttentionRaise(Repository, new AttentionContent { Kind = AttentionKinds.ReviewReady, TaskId = "t-3" }), Now);
        Effect(hub, chat, new AttentionClose(Repository, new AttentionSelection { TaskId = "t-3" }), Now);
        Effect(hub, chat, new AttentionClose(Repository, new AttentionSelection { TaskId = "t-6", Kinds = [AttentionKinds.Failed], Revision = 4 }), Now);
        Effect(hub, chat, new AttentionClose(Repository, new AttentionSelection { TaskIds = ["t-4"], Revision = 9 }), Now);
        Request(hub, chat, new AttentionSuggestion(Repository, chat.Id, new AttentionContent { Kind = AttentionKinds.NextWork, TaskId = "t-5", Revision = 1, ExpiryDays = 1 }), Now);
        Request(hub, chat, new AttentionClaimed(Repository, chat.Id, AggregateKinds.DefaultBoard, "t-5", 2), Now);
        var offered = AggregateHub.Data(Request(hub, chat, new AttentionOffer(Repository, chat.Id, chat.Token) { SnoozeBoundaries = 2 }, Now))["attention"]!["id"]!.GetValue<string>();
        Assert.True(hub.Session(chat, RelayTools.AttentionRespond, new() { ["itemId"] = offered, ["action"] = AttentionActions.Later }, Now).Ok);
        Request(hub, chat, new AttentionOffer(Repository, chat.Id, chat.Token), Now.AddMinutes(1));
        Request(hub, chat, new AttentionSeen(Repository, chat.Id, chat.Token, ["n-1"]), Now.AddMinutes(1));

        // Work it answers for, one action of which ended, and a forced close of another session.
        Effect(hub, chat, new ActionWorkReport(Repository, chat.Id, AggregateKinds.DefaultBoard, "t-7", "deploy", 1, true, Now), Now);
        Effect(hub, chat, new ActionWorkReport(Repository, chat.Id, AggregateKinds.DefaultBoard, "t-8", "deploy", 1, true, Now), Now);
        Effect(hub, chat, new ActionWorkReport(Repository, chat.Id, AggregateKinds.DefaultBoard, "t-8", "deploy", 1, false, Now.AddMinutes(1)), Now.AddMinutes(1));
        Assert.True(hub.Close(new() { ["targetSessionId"] = gone.Id, ["force"] = true }, Now).Ok);
        hub.DeliverAll(Now.AddMinutes(1));

        // A genuine wait owned by the still-open peer watches the already
        // closed session. The primary actor stores the wait; delivery records
        // the closed-source retirement fence and settles the owner once.
        var waitArgs = new JsonObject { ["repositoryId"] = Repository, ["sessionId"] = peer.Id, ["sessionToken"] = peer.Token };
        waitArgs["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest("shape-wait", "claim_released", "shape-resource", "1", Now.AddMinutes(10)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(hub.Apply(AggregateKinds.Session, peer.Id, EventWaitAggregate.RegisterOperation, waitArgs,
            AggregateHub.Alice with { IsInternal = true }, Now.AddMinutes(1)).Ok);
        var ownerWatch = new EventWaitOwnerWatch(Repository, peer.Id, "shape-wait", "shape-watch", gone.Id, Now.AddMinutes(10));
        Assert.True(EventWaitSources.Apply(hub.Of(AggregateKinds.Session, peer.Id), AggregateHub.Alice, peer.Id, "bind", waitArgs,
            Now.AddMinutes(1), ownerWatch).Ok);
        hub.DeliverAll(Now.AddMinutes(1));
        Assert.Equal("target_gone", hub.State(AggregateKinds.Session, peer.Id)[SessionState.EventWaitsKey]!["shape-wait"]!["terminal"]!["outcome"]!.GetValue<string>());
        Assert.NotEmpty(hub.State(AggregateKinds.Session, gone.Id)["eventWaitRetired"]!.AsObject());

        // Authenticated direct send and real source-arm delivery exercise the
        // private sender sequence and immutable arming snapshot, not raw keys.
        var sent = hub.Send(chat, peer.Id, "peer-shape-original", Now.AddMinutes(1));
        Assert.True(sent.Ok);
        var peerRequest = new EventWaitRequest("peer-shape-wait", "peer_reply", AggregateHub.Data(sent)["messageId"]!.GetValue<string>(), "",
            Now.AddMinutes(10), PeerSessionId: peer.Id, ReplyTo: AggregateHub.Data(sent)["messageId"]!.GetValue<string>());
        var peerArgs = new JsonObject { ["repositoryId"] = Repository, ["sessionId"] = chat.Id, ["sessionToken"] = chat.Token,
            ["request"] = JsonSerializer.SerializeToNode(peerRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        Assert.True(hub.Apply(AggregateKinds.Session, chat.Id, EventWaitAggregate.RegisterOperation, peerArgs,
            AggregateHub.Alice with { IsInternal = true }, Now.AddMinutes(1)).Ok);
        hub.DeliverAll(Now.AddMinutes(1));

        // Include a real admitted native open so the schema's optional native fields are exercised.
        var native = new JsonObject
        {
            ["repositoryId"] = Repository, ["machineId"] = "pc-native", ["requestId"] = Guid.NewGuid().ToString("N"),
            ["nativeJoin"] = new JsonObject
            {
                ["provider"] = "codex", ["nativeId"] = Guid.NewGuid().ToString("D"), ["workspace"] = "/fixture/native-state-shape",
                ["bindingSecret"] = Convert.ToBase64String(new byte[32]), ["generation"] = Guid.NewGuid().ToString("N")
            }
        };
        Assert.True(NativeParticipant.Admit(AggregateHub.Alice, native));
        var opened = hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.SessionOpen, native, AggregateHub.Alice with { IsInternal = true }, Now);
        Assert.True(opened.Ok);
        var nativeSession = AggregateHub.Data(opened)["sessionId"]!.GetValue<string>();
        hub.DeliverAll(Now.AddMinutes(1));
        var nativeDocument = hub.State(AggregateKinds.Session, nativeSession);
        Assert.Equal(native[NativeParticipant.Id]!.GetValue<string>(), nativeDocument[NativeParticipant.Id]!.GetValue<string>());
        Assert.Equal(native[NativeParticipant.Generation]!.GetValue<string>(), nativeDocument[NativeParticipant.Generation]!.GetValue<string>());
        Assert.Equal(native[NativeParticipant.Request]!.GetValue<string>(), nativeDocument[NativeParticipant.Request]!.GetValue<string>());
        var documents = new[] { chat, peer, gone }.Select(session => hub.State(AggregateKinds.Session, session.Id)).Append(nativeDocument).ToArray();
        ExhaustiveStateFixture.Verify(SessionState.Shape, documents);

        // The shape is not vacuous: between them the sessions wrote every key it declares, but the request tombstones (written only once a
        // full replay cache evicts a reply early) and the work ledger's overflow mark (only past its open-action limit).
        var state = documents[0];
        Assert.Equal(InboxMessage.Keys.Order(StringComparer.Ordinal), EntryKeys(state, SessionState.InboxKey).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(QueuedItem.Shape.Fields.Keys.Order(StringComparer.Ordinal), EntryKeys(state, AttentionQueue.ItemsKey).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal([WorkLedger.ClosedKey, WorkLedger.OpenKey], Keys(state[SessionState.WorkKey]).Order(StringComparer.Ordinal));
        Assert.Equal(WorkReport.Shape.Fields.Keys.Order(StringComparer.Ordinal), Keys(state[SessionState.WorkReportedKey]).Order(StringComparer.Ordinal));
        Assert.Equal(RecentClose.Shape.Fields.Keys.Order(StringComparer.Ordinal), EntryKeys(state, AttentionQueue.RecentKey).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal([BoardWatches.BoardIdKey], EntryKeys(state, SessionState.TargetBoardsKey).Concat(EntryKeys(state, SessionState.WorkBoardsKey)).Distinct());

        Assert.Equal(WaitingMark.Shape.Fields.Keys.Order(StringComparer.Ordinal), Keys(documents[1][SessionState.WaitingForKey]).Order(StringComparer.Ordinal));

        // The typed members read what the handlers wrote.
        Assert.Equal(("Which rig?", Now, Now.AddHours(RelayLimits.WaitingForHours)), (new SessionState(documents[1]).WaitingFor!.Text, new SessionState(documents[1]).WaitingFor!.Since, new SessionState(documents[1]).WaitingFor!.ExpiresAt));
        var session = new SessionState(state);
        Assert.Equal((chat.Id, SessionRoles.Manager, "pc-1", 1L, "available"), (session.SessionId, session.Role, session.MachineId, session.BindingVersion, session.Availability));
        Assert.Equal(3, session.StandingSeen.Of(AggregateKinds.DefaultBoard));
        Assert.True(session.Acked.Holds(read));
        Assert.Equal((true, 1L), (session.WorkReported!.Owns, session.WorkReported.Seq));
        Assert.Equal(Now.AddMinutes(1), session.Attention.BoundaryAt);
        // The forced close landed when it was delivered.
        var closed = new SessionState(documents[2]);
        Assert.Equal((true, Now.AddMinutes(1), "admin"), (closed.Closed, closed.ClosedAt, closed.ForceClosedBy));
    }

}

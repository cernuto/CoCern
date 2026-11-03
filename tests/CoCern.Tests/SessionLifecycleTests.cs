using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A session's life from open to close, played through <see cref="CoordinationStateMachine.Apply"/> with explicit times and with the
/// outbox effects between aggregates delivered by hand, in the order a test chooses: idle closes judge by the last call (activity is
/// reported only hourly), unread mail and unreported work keep a session open, a closed session's mail goes back to its sender, and a task
/// targeting a closed session becomes claimable by anyone.
/// </summary>
public sealed class SessionLifecycleTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
    private static readonly RelayCaller Alice = AggregateHub.Alice;
    private static readonly RelayCaller BoardCaller = AggregateHub.BoardCaller;
    private static readonly string BoardKey = AggregateHub.BoardKey;

    [Fact]
    public void An_idle_close_counts_from_the_last_call_even_though_activity_reaches_the_directory_only_hourly()
    {
        var hub = new AggregateHub();
        var working = hub.Open("working", T0);
        var idle = hub.Open("idle", T0);
        hub.Verify(working, T0); hub.Verify(idle, T0);
        hub.DeliverAll(T0);
        // The working chat calls again 59 minutes later: within the report interval, so the directory hears nothing.
        hub.Verify(working, T0.AddMinutes(59));
        Assert.Empty(hub.Of(AggregateKinds.Session, working.Id).Effects);

        // At T0+63 the directory still last saw both at T0 (their open's two-minute freshness has passed), but the working chat called four
        // minutes ago, so "idle for an hour" must not close it. The other has been idle 63 minutes; it may wait up to an hour more.
        var early = Data(hub.Close(new() { ["idleMinutes"] = 60 }, T0.AddMinutes(63)));
        Assert.Equal((0, 0), (early["closed"]!.GetValue<int>(), early["kept"]!.GetValue<int>()));
        Assert.False(early["more"]!.GetValue<bool>());
        Assert.Equal(2, hub.Directory().Count);

        // The idle chat calls at T0+100 (reported); at T0+123 the working chat has made no call for 64 minutes and is closed.
        hub.Verify(idle, T0.AddMinutes(100));
        hub.DeliverAll(T0.AddMinutes(100));
        var later = Data(hub.Close(new() { ["idleMinutes"] = 60 }, T0.AddMinutes(123)));
        Assert.Equal([working.Id], later["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Equal([idle.Id], hub.Directory().Select(entry => entry.Key));

        // The sweep judges the same way: an hour more than its idle hours.
        var swept = new AggregateHub();
        var chat = swept.Open("chat", T0);
        swept.Verify(chat, T0);
        swept.DeliverAll(T0);
        Assert.Equal(0, Data(swept.Sweep(24, T0.AddHours(24).AddMinutes(30)))["closed"]!.GetValue<int>());
        Assert.Equal(1, Data(swept.Sweep(24, T0.AddHours(25).AddMinutes(5)))["closed"]!.GetValue<int>());
    }

    [Fact]
    public void An_idle_session_with_unread_mail_refuses_the_sweep_and_stays_until_it_is_seen_again_or_the_hold_lapses()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("worker", T0);
        var sender = hub.Open("sender", T0);
        hub.Verify(worker, T0); hub.Verify(sender, T0);
        hub.DeliverAll(T0);
        Assert.True(hub.Send(sender, worker.Id, "please review", T0.AddMinutes(5)).Ok);
        hub.DeliverAll(T0.AddMinutes(5));
        Assert.Single(hub.Inbox(worker, T0.AddMinutes(6)));

        // The sweep closes the idle worker as the directory sees it, and asks it to refuse if it has mail. It does, and comes back.
        hub.Verify(sender, T0.AddHours(25));
        hub.DeliverAll(T0.AddHours(25));
        var first = Data(hub.Sweep(24, T0.AddHours(26)));
        Assert.Equal((1, 0), (first["closed"]!.GetValue<int>(), first["held"]!.GetValue<int>()));
        var expire = Assert.Single(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects, effect => effect.Operation == Operations.SessionExpire);
        Assert.True(JsonNode.Parse(expire.PayloadJson)!["idle"]!.GetValue<bool>());
        var refused = Data(hub.Deliver(expire, T0.AddHours(26)));
        Assert.Equal((false, "mail"), (refused["expired"]!.GetValue<bool>(), refused["keptFor"]!.GetValue<string>()));
        hub.DeliverAll(T0.AddHours(26));
        // Its chat never saw the close: its handle still works and its mail is still there.
        Assert.True(hub.Directory().ContainsKey(worker.Id));
        Assert.Single(hub.Inbox(worker, T0.AddHours(26)));

        // Later sweeps keep it without asking again.
        var held = Data(hub.Sweep(24, T0.AddHours(30)));
        Assert.Equal((0, 1), (held["closed"]!.GetValue<int>(), held["held"]!.GetValue<int>()));
        Assert.DoesNotContain(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects, effect => effect.Operation == Operations.SessionExpire);
        // An idle close_sessions keeps it too (and closes the sender, idle five hours with an empty inbox); a label-only close is
        // explicit and would take it.
        var closed = Data(hub.Close(new() { ["idleMinutes"] = 60 }, T0.AddHours(30)));
        Assert.Equal((1, 1, false), (closed["closed"]!.GetValue<int>(), closed["held"]!.GetValue<int>(), closed["more"]!.GetValue<bool>()));
        Assert.Equal([sender.Id], closed["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Single(hub.Discard(Operations.SessionExpire));

        // Past the hold, an idle close takes it as it is: the expiry no longer asks.
        var lapsed = Data(hub.Sweep(24, T0.AddHours(26).AddDays(RelayLimits.SessionMailHoldDays).AddMinutes(1)));
        Assert.Equal((1, 0), (lapsed["closed"]!.GetValue<int>(), lapsed["held"]!.GetValue<int>()));
        var final = Assert.Single(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects, effect => effect.Operation == Operations.SessionExpire);
        Assert.Null(JsonNode.Parse(final.PayloadJson)!["idle"]);
        Assert.True(Data(hub.Deliver(final, T0.AddDays(9)))["expired"]!.GetValue<bool>());
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(worker, T0.AddDays(9)).Code);
    }

    [Fact]
    public void A_session_seen_again_after_its_mail_hold_is_asked_again_and_an_empty_inbox_lets_it_close()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("worker", T0);
        var sender = hub.Open("sender", T0);
        hub.Verify(worker, T0); hub.Verify(sender, T0);
        hub.DeliverAll(T0);
        hub.Send(sender, worker.Id, "status?", T0.AddMinutes(1));
        hub.DeliverAll(T0.AddMinutes(1));
        hub.Sweep(24, T0.AddHours(26));
        hub.DeliverAll(T0.AddHours(26));
        Assert.True(hub.Directory().ContainsKey(worker.Id));

        // The chat comes back (each call is verified, which reports it), reads and acknowledges its mail, then goes quiet: the hold no
        // longer applies, and the next idle close takes it.
        hub.Verify(worker, T0.AddHours(27));
        var messages = hub.Inbox(worker, T0.AddHours(27));
        Assert.True(hub.Session(worker, RelayTools.Ack, new() { ["messageIds"] = new JsonArray(messages.Select(m => (JsonNode)m["messageId"]!.GetValue<string>()).ToArray()) }, T0.AddHours(27)).Ok);
        hub.DeliverAll(T0.AddHours(27));
        var swept = Data(hub.Sweep(24, T0.AddHours(53)));
        Assert.Equal((1, 0), (swept["closed"]!.GetValue<int>(), swept["held"]!.GetValue<int>()));
        hub.DeliverAll(T0.AddHours(53));
        Assert.False(hub.Directory().ContainsKey(worker.Id));
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(worker, T0.AddHours(54)).Code);
    }

    [Fact]
    public void A_close_that_is_not_idle_takes_a_session_with_mail_and_later_messages_to_it_come_back_to_their_sender()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("smoke worker", T0);
        var sender = hub.Open("sender", T0);
        hub.Verify(worker, T0); hub.Verify(sender, T0);
        hub.DeliverAll(T0);
        hub.Send(sender, worker.Id, "first", T0.AddMinutes(1));
        hub.DeliverAll(T0.AddMinutes(1));
        Assert.Equal(1, Data(hub.Close(new() { ["targetSessionId"] = worker.Id }, T0.AddMinutes(2)))["closed"]!.GetValue<int>());
        hub.DeliverAll(T0.AddMinutes(2));
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(worker, T0.AddMinutes(3)).Code);

        // The message it never read goes back to its sender rather than vanishing with the inbox.
        var unread = Assert.Single(hub.Inbox(sender, T0.AddMinutes(3)));
        Assert.Equal(("delivery.failed", "unread", worker.Id), (unread["kind"]!.GetValue<string>(), unread["reason"]!.GetValue<string>(), unread["recipientSessionId"]!.GetValue<string>()));
        Assert.Contains("closed first", unread["text"]!.GetValue<string>());
        Assert.True(hub.Session(sender, RelayTools.Ack, new() { ["messageIds"] = new JsonArray(unread["messageId"]!.GetValue<string>()) }, T0.AddMinutes(3)).Ok);

        // A message to the closed session is not dropped silently: the sender gets a notice naming it.
        var sent = Data(hub.Send(sender, worker.Id, "are you there?", T0.AddMinutes(4)));
        hub.DeliverAll(T0.AddMinutes(4));
        var notice = Assert.Single(hub.Inbox(sender, T0.AddMinutes(5)));
        Assert.Equal(("delivery.failed", "closed", worker.Id, sent["messageId"]!.GetValue<string>()),
            (notice["kind"]!.GetValue<string>(), notice["reason"]!.GetValue<string>(), notice["recipientSessionId"]!.GetValue<string>(), notice["undeliveredMessageId"]!.GetValue<string>()));
        Assert.Contains("not delivered", notice["text"]!.GetValue<string>());

        // So is one to a session never opened; a superseding status message to it keeps one notice per recipient.
        for (var i = 0; i < 3; i++)
        {
            hub.Send(sender, "never-opened", "status " + i, T0.AddMinutes(6 + i), supersede: true);
            hub.DeliverAll(T0.AddMinutes(6 + i));
        }
        var notices = hub.Inbox(sender, T0.AddMinutes(10));
        var unknown = Assert.Single(notices, message => message["recipientSessionId"]!.GetValue<string>() == "never-opened");
        Assert.Equal("unknown", unknown["reason"]!.GetValue<string>());
        // A notice is never itself returned, so a closed sender cannot make notices loop.
        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public void A_close_that_races_the_report_of_a_new_action_is_refused_by_the_session_which_comes_back_owning_it()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("smoke 1", T0);
        hub.Verify(worker, T0);
        hub.DeliverAll(T0);
        // A board records the session's new action and tells the session, which tells the directory; that last report is still queued
        // when an administrator closes the session by label.
        Assert.True(hub.Apply(AggregateKinds.Session, worker.Id, Operations.SessionWork, Work(worker.Id, true, T0.AddMinutes(1)), BoardCaller, T0.AddMinutes(1)).Ok);
        var report = Assert.Single(hub.Discard(Operations.SessionOwns));
        Assert.Equal(AggregateKinds.Control, report.TargetKind);

        Assert.Equal(1, Data(hub.Close(new() { ["targetSessionId"] = worker.Id }, T0.AddMinutes(2)))["closed"]!.GetValue<int>());
        Assert.False(hub.Directory().ContainsKey(worker.Id));
        var refused = Data(hub.Deliver(Assert.Single(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects), T0.AddMinutes(2)));
        Assert.Equal("work", refused["keptFor"]!.GetValue<string>());
        hub.DeliverAll(T0.AddMinutes(2));
        // The late report finds the restored record already owning the work.
        Assert.True(hub.Deliver(report, T0.AddMinutes(3)).Ok);

        Assert.True(hub.Directory().ContainsKey(worker.Id));
        Assert.True(hub.Verify(worker, T0.AddMinutes(3)).Ok);
        var again = Data(hub.Close(new() { ["targetSessionId"] = worker.Id }, T0.AddMinutes(4)));
        Assert.Equal((0, 1), (again["closed"]!.GetValue<int>(), again["kept"]!.GetValue<int>()));
        // The restored record lists as before, without the directory's internal fields.
        var listed = Assert.Single(Data(hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.WorkerList, new() { ["hubIncarnation"] = "hub-1" }, Alice, T0.AddMinutes(4)))["items"]!.AsArray())!;
        Assert.Equal("smoke 1", listed["label"]!.GetValue<string>());
        Assert.Null(listed["ownsWork"]);
        Assert.Null(listed["sessionToken"]);
    }

    [Fact]
    public void Force_closing_an_exact_session_expires_it_despite_unread_mail_and_unresolved_work()
    {
        var hub = new AggregateHub();
        var target = hub.Open("Same label", T0);
        var peer = hub.Open("Same label", T0);
        hub.Verify(target, T0); hub.Verify(peer, T0);
        hub.DeliverAll(T0);
        Assert.True(hub.Send(peer, target.Id, "unread", T0).Ok);
        Assert.True(hub.Apply(AggregateKinds.Session, target.Id, Operations.SessionWork, Work(target.Id, true, T0), BoardCaller, T0).Ok);
        hub.DeliverAll(T0);

        var kept = hub.Close(new() { ["targetSessionId"] = target.Id }, T0.AddMinutes(1));
        Assert.True(kept.Ok, kept.DataJson);
        Assert.Equal(1, Data(kept)["kept"]!.GetValue<int>());
        Assert.Single(hub.Inbox(target, T0.AddMinutes(1)));
        var forced = hub.Close(new() { ["targetSessionId"] = target.Id, ["force"] = true }, T0.AddMinutes(2));
        Assert.True(forced.Ok, forced.DataJson);
        Assert.Equal([target.Id], Data(forced)["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        hub.DeliverAll(T0.AddMinutes(2));
        Assert.False(hub.Directory().ContainsKey(target.Id));
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(target, T0.AddMinutes(3)).Code);
        Assert.True(hub.Verify(peer, T0.AddMinutes(3)).Ok);
        Assert.Null(hub.State(AggregateKinds.Session, target.Id)["inbox"]);
        // A late work report cannot revive the closed session.
        Assert.True(hub.Apply(AggregateKinds.Session, target.Id, Operations.SessionWork, Work(target.Id, true, T0.AddMinutes(4)), BoardCaller, T0.AddMinutes(4)).Ok);
        hub.DeliverAll(T0.AddMinutes(4));
        Assert.False(hub.Directory().ContainsKey(target.Id));
    }

    [Fact]
    public void A_late_restore_cannot_bring_back_a_force_closed_session()
    {
        var hub = new AggregateHub();
        var target = hub.Open("target", T0);
        hub.Verify(target, T0); hub.DeliverAll(T0);
        Assert.True(hub.Apply(AggregateKinds.Session, target.Id, Operations.SessionWork, Work(target.Id, true, T0), BoardCaller, T0).Ok);
        // The directory has not yet heard the work report, so a normal close is refused by the session.
        Assert.Single(hub.Discard(Operations.SessionOwns));
        Assert.True(hub.Close(new() { ["targetSessionId"] = target.Id }, T0.AddMinutes(1)).Ok);
        var expiry = Assert.Single(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects);
        Assert.True(hub.Deliver(expiry, T0.AddMinutes(1)).Ok);
        var restore = Assert.Single(hub.Of(AggregateKinds.Session, target.Id).Effects, effect => effect.Operation == Operations.SessionRestore);
        Assert.True(hub.Deliver(restore, T0.AddMinutes(1)).Ok);
        Assert.True(hub.Close(new() { ["targetSessionId"] = target.Id, ["force"] = true }, T0.AddMinutes(2)).Ok);
        hub.DeliverAll(T0.AddMinutes(2));
        Assert.True(hub.Deliver(restore, T0.AddMinutes(3)).Ok);
        Assert.False(hub.Directory().ContainsKey(target.Id));
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(target, T0.AddMinutes(3)).Code);
    }

    /// <summary>
    /// A board task <c>t</c> claimed by <paramref name="owner"/> for an hour, at version 3, with one unresolved action <c>a</c> that session
    /// began, or (without <paramref name="begun"/>) none yet.
    /// </summary>
    private static void SeedBoard(AggregateHub hub, string owner, bool begun = true)
    {
        var action = new JsonObject
        {
            ["actionId"] = "a", ["attempt"] = 1, ["status"] = "invoking", ["sessionId"] = owner, ["principalId"] = "alice", ["restoration"] = "not_required",
            ["requiresRestoration"] = false, ["resourceIds"] = new JsonArray(), ["startedAt"] = T0.ToString("O"), ["inputFingerprint"] = "f"
        };
        var task = new JsonObject
        {
            ["taskId"] = "t", ["version"] = 3, ["owner"] = owner, ["leaseUntil"] = T0.AddHours(1).ToString("O"), ["claimGeneration"] = 1,
            ["actions"] = begun ? new JsonObject { ["a"] = action } : new JsonObject(), ["messages"] = new JsonArray(), ["reports"] = new JsonArray(), ["facts"] = new JsonObject()
        };
        hub.Of(AggregateKinds.Board, BoardKey).Value.Json = new JsonObject { ["repositoryId"] = "repo", ["tasks"] = new JsonObject { ["t"] = task } }.ToJsonString();
    }

    private static JsonObject BoardTask(AggregateHub hub) => hub.State(AggregateKinds.Board, BoardKey)["tasks"]!["t"]!.AsObject();

    private static RelayReply ReportWork(AggregateHub hub, Chat chat, bool open, DateTimeOffset at) =>
        hub.Apply(AggregateKinds.Session, chat.Id, Operations.SessionWork, Work(chat.Id, open, at), BoardCaller, at);

    /// <summary>The board's report that <paramref name="session"/> starts or stops answering for attempt 1 of action a on task t.</summary>
    private static JsonObject Work(string session, bool open, DateTimeOffset at) => new ActionWorkReport("repo", session, "default", "t", "a", 1, open, at).ToJson();

    [Fact]
    public void A_forced_close_tells_the_board_which_makes_the_sessions_actions_recoverable_at_once()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("worker", T0);
        hub.Verify(worker, T0); hub.DeliverAll(T0);
        SeedBoard(hub, worker.Id);
        Assert.True(ReportWork(hub, worker, true, T0.AddMinutes(1)).Ok);
        hub.DeliverAll(T0.AddMinutes(1));

        Assert.Equal(1, Data(hub.Close(new() { ["targetSessionId"] = worker.Id, ["force"] = true }, T0.AddMinutes(2)))["closed"]!.GetValue<int>());
        hub.DeliverAll(T0.AddMinutes(2));

        // The task keeps its owner and the action its record, but the claim has lapsed and the action says whose session closed.
        var task = BoardTask(hub);
        Assert.Equal((worker.Id, 4L), (task["owner"]!.GetValue<string>(), task["version"]!.GetValue<long>()));
        Assert.Equal(T0.AddMinutes(2), DateTimeOffset.Parse(task["leaseUntil"]!.GetValue<string>()));
        Assert.Equal(("invoking", worker.Id), (task["actions"]!["a"]!["status"]!.GetValue<string>(), task["actions"]!["a"]!["ownerClosed"]!["sessionId"]!.GetValue<string>()));
        // A repeated notice changes nothing.
        Assert.True(hub.Apply(AggregateKinds.Board, BoardKey, Operations.SessionClosed, new() { ["boardId"] = "default", ["sessionId"] = worker.Id }, BoardCaller, T0.AddMinutes(3)).Ok);
        Assert.Equal(4L, BoardTask(hub)["version"]!.GetValue<long>());
        // The session answered the close, so the directory keeps no fence for it.
        Assert.Null(hub.State(AggregateKinds.Control, AggregateKinds.ControlKey)["closing"]);
    }

    [Fact]
    public void A_report_of_new_work_that_lands_after_a_close_makes_the_action_recoverable()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("smoke worker", T0);
        hub.Verify(worker, T0); hub.DeliverAll(T0);
        SeedBoard(hub, worker.Id);
        // The close lands before the board's report of the action the session just began.
        Assert.Equal(1, Data(hub.Close(new() { ["targetSessionId"] = worker.Id }, T0.AddMinutes(1)))["closed"]!.GetValue<int>());
        hub.DeliverAll(T0.AddMinutes(1));
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(worker, T0.AddMinutes(1)).Code);

        Assert.True(Data(ReportWork(hub, worker, true, T0.AddMinutes(2)))["toldBoard"]!.GetValue<bool>());
        hub.DeliverAll(T0.AddMinutes(2));
        var task = BoardTask(hub);
        Assert.Equal(T0.AddMinutes(2), DateTimeOffset.Parse(task["leaseUntil"]!.GetValue<string>()));
        Assert.Equal(worker.Id, task["actions"]!["a"]!["ownerClosed"]!["sessionId"]!.GetValue<string>());
        // A late report that work ended asks nothing of the board.
        Assert.Null(Data(ReportWork(hub, worker, false, T0.AddMinutes(3)))["toldBoard"]);
    }

    private static RelayReply Begin(AggregateHub hub, Chat chat, DateTimeOffset at) =>
        hub.Board(RelayTools.ActionBegin, new() { ["taskId"] = "t", ["sessionId"] = chat.Id, ["actionId"] = "a", ["inputFingerprint"] = "f", ["expectedVersion"] = 3 }, at);

    private static RelayReply Settle(AggregateHub hub, Chat chat, DateTimeOffset at) =>
        hub.Board(RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = chat.Id, ["actionId"] = "a", ["attempt"] = 1, ["outcome"] = "completed", ["expectedVersion"] = 4 }, at);

    [Fact]
    public void Work_reports_delivered_in_any_order_and_twice_leave_the_directory_knowing_whether_the_session_owns_work()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("worker", T0);
        hub.Verify(worker, T0); hub.DeliverAll(T0);
        SeedBoard(hub, worker.Id, begun: false);

        // Begun and not yet reported: however the board's report and the session's reach their targets, the directory knows.
        Assert.True(Begin(hub, worker, T0.AddMinutes(1)).Ok);
        var owning = hub.Explore(T0.AddMinutes(2), drained =>
        {
            drained.AssertInvariants(T0.AddMinutes(2));
            Assert.NotNull(drained.Directory()[worker.Id]!["ownsWork"]);
        });
        Assert.True(owning > 1);

        // Begun and reported: in every order, with any report delivered twice, the session ends up owning nothing, and so does its entry.
        Assert.True(Settle(hub, worker, T0.AddMinutes(2)).Ok);
        var settled = hub.Explore(T0.AddMinutes(3), drained =>
        {
            drained.AssertInvariants(T0.AddMinutes(3));
            Assert.Null(drained.State(AggregateKinds.Session, worker.Id)["work"]!["openActions"]);
            Assert.Null(drained.Directory()[worker.Id]!["ownsWork"]);
        });
        Assert.True(settled > 1);
    }

    [Fact]
    public void A_close_racing_the_report_of_a_new_action_never_strands_it_in_any_delivery_order()
    {
        foreach (var force in new[] { false, true })
        {
            var hub = new AggregateHub();
            var worker = hub.Open("smoke worker", T0);
            hub.Verify(worker, T0); hub.DeliverAll(T0);
            SeedBoard(hub, worker.Id, begun: false);
            // The session begins an action, and an administrator closes it before anything is delivered.
            Assert.True(Begin(hub, worker, T0.AddMinutes(1)).Ok);
            var close = new JsonObject { ["targetSessionId"] = worker.Id };
            if (force) close["force"] = true;
            Assert.Equal(1, Data(hub.Close(close, T0.AddMinutes(2)))["closed"]!.GetValue<int>());
            // Either the session hears of the action first and refuses (comes back owning it), or it closes first and the late report makes
            // the action recoverable; a forced close takes it either way, and the board hears.
            var closed = new HashSet<bool>();
            hub.Explore(T0.AddMinutes(2), drained =>
            {
                drained.AssertInvariants(T0.AddMinutes(2));
                closed.Add(drained.State(AggregateKinds.Session, worker.Id)["expiredAt"] is not null);
            });
            Assert.Equal(force ? [true] : [false, true], closed.Order());
        }
    }

    [Fact]
    public void A_repeated_restore_from_an_earlier_close_cannot_bring_back_a_session_that_closed_since()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("smoke worker", T0);
        hub.Verify(worker, T0); hub.DeliverAll(T0);
        // The session begins work the directory has not heard of, so the first close is refused and the session restores itself.
        Assert.True(ReportWork(hub, worker, true, T0.AddMinutes(1)).Ok);
        Assert.Single(hub.Discard(Operations.SessionOwns));
        Assert.True(hub.Close(new() { ["targetSessionId"] = worker.Id }, T0.AddMinutes(2)).Ok);
        Assert.True(hub.Deliver(Assert.Single(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects), T0.AddMinutes(2)).Ok);
        var restore = Assert.Single(hub.Of(AggregateKinds.Session, worker.Id).Effects, effect => effect.Operation == Operations.SessionRestore);
        Assert.True(Data(hub.Deliver(restore, T0.AddMinutes(2)))["restored"]!.GetValue<bool>());
        Assert.True(hub.Directory().ContainsKey(worker.Id));

        // The work ends and a second close is accepted.
        Assert.True(ReportWork(hub, worker, false, T0.AddMinutes(3)).Ok);
        hub.DeliverAll(T0.AddMinutes(3));
        Assert.Equal(1, Data(hub.Close(new() { ["targetSessionId"] = worker.Id }, T0.AddMinutes(4)))["closed"]!.GetValue<int>());
        hub.DeliverAll(T0.AddMinutes(4));

        // A copy of the first restore, delivered late (its outbox removal failed after the directory committed), is ignored.
        Assert.False(Data(hub.Deliver(restore, T0.AddMinutes(5)))["restored"]!.GetValue<bool>());
        Assert.False(hub.Directory().ContainsKey(worker.Id));
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(worker, T0.AddMinutes(5)).Code);
        Assert.Null(hub.State(AggregateKinds.Control, AggregateKinds.ControlKey)["closing"]);
    }

    [Fact]
    public void A_close_by_label_skips_a_chat_working_right_now_and_takes_it_once_it_is_quiet()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("smoke worker", T0);
        hub.Verify(worker, T0); hub.DeliverAll(T0);

        // A call a few minutes ago: the close by label keeps it, counted as active, and the chat carries on.
        var skipped = Data(hub.Close(new() { ["labelPrefix"] = "smoke" }, T0.AddMinutes(5)));
        Assert.Equal((0, 0, 1), (skipped["closed"]!.GetValue<int>(), skipped["kept"]!.GetValue<int>(), skipped["active"]!.GetValue<int>()));
        Assert.Empty(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects);
        Assert.True(hub.Verify(worker, T0.AddMinutes(6)).Ok);

        // Quiet for longer than the window (plus the hour activity may take to reach the directory), it is closed like any other.
        var closed = Data(hub.Close(new() { ["labelPrefix"] = "smoke" }, T0.AddHours(2)));
        Assert.Equal((1, 0), (closed["closed"]!.GetValue<int>(), closed["active"]!.GetValue<int>()));
        hub.DeliverAll(T0.AddHours(2));
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(worker, T0.AddHours(2)).Code);
    }

    [Fact]
    public void A_session_whose_new_activity_has_not_reached_the_directory_refuses_the_close_and_comes_back()
    {
        var hub = new AggregateHub();
        var worker = hub.Open("worker", T0);
        hub.Verify(worker, T0); hub.DeliverAll(T0);
        // After hours of quiet the chat calls again; its activity report is still on its way when an idle close decides from older news.
        Assert.True(hub.Verify(worker, T0.AddHours(5)).Ok);
        var report = Assert.Single(hub.Of(AggregateKinds.Session, worker.Id).Effects);
        Assert.Equal(Operations.Register, report.Operation);
        Assert.Equal(1, Data(hub.Close(new() { ["idleMinutes"] = 60 }, T0.AddHours(5)))["closed"]!.GetValue<int>());

        var refused = Data(hub.Deliver(Assert.Single(hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Effects), T0.AddHours(5)));
        Assert.Equal("active", refused["keptFor"]!.GetValue<string>());
        hub.DeliverAll(T0.AddHours(5));
        Assert.True(hub.Directory().ContainsKey(worker.Id));
        Assert.True(hub.Verify(worker, T0.AddHours(5).AddMinutes(1)).Ok);
        Assert.Null(hub.State(AggregateKinds.Control, AggregateKinds.ControlKey)["closing"]);
    }

    [Fact]
    public void Close_sessions_advertises_the_exact_target_and_force_to_agents()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.ControlRequest, out var tool));
        var properties = RelayToolCatalog.InputSchema(tool).GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("targetSessionId").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("force").GetProperty("type").GetString());
    }

    [Fact]
    public void Bind_keeps_the_first_machine_and_leaves_binding_and_availability_alone_when_they_are_null_or_left_out()
    {
        var hub = new AggregateHub();
        var session = hub.Open("chat", T0, machineId: "PC-1");
        Assert.True(hub.Session(session, RelayTools.SessionBind, new() { ["expectedVersion"] = 0, ["binding"] = new JsonObject { ["roslyn"] = "codelens" }, ["availability"] = "active", ["machineId"] = "PC-2" }, T0).Ok);
        Assert.True(hub.Session(session, RelayTools.SessionBind, new() { ["expectedVersion"] = 1, ["binding"] = null, ["availability"] = null, ["label"] = "renamed" }, T0.AddMinutes(1)).Ok);
        var state = hub.State(AggregateKinds.Session, session.Id);
        Assert.Equal(("PC-1", "codelens", "active", "renamed"), (state["machineId"]!.GetValue<string>(), state["binding"]!["roslyn"]!.GetValue<string>(), state["availability"]!.GetValue<string>(), state["label"]!.GetValue<string>()));
        // The directory hears the same.
        hub.DeliverAll(T0.AddMinutes(1));
        var record = hub.Directory()[session.Id]!;
        Assert.Equal(("PC-1", "codelens", "active"), (record["machineId"]!.GetValue<string>(), record["binding"]!["roslyn"]!.GetValue<string>(), record["availability"]!.GetValue<string>()));

        // A session opened without a machine records the first one it reports; an empty binding object still replaces the binding.
        var older = hub.Open("older", T0);
        Assert.True(hub.Session(older, RelayTools.SessionBind, new() { ["expectedVersion"] = 0, ["machineId"] = "PC-3", ["binding"] = new JsonObject { ["v"] = 1 } }, T0).Ok);
        Assert.True(hub.Session(older, RelayTools.SessionBind, new() { ["expectedVersion"] = 1, ["machineId"] = "PC-4", ["binding"] = new JsonObject() }, T0).Ok);
        var olderState = hub.State(AggregateKinds.Session, older.Id);
        Assert.Equal("PC-3", olderState["machineId"]!.GetValue<string>());
        Assert.Empty(olderState["binding"]!.AsObject());
        Assert.Equal("unknown", olderState["availability"]!.GetValue<string>());
    }

    [Fact]
    public void A_task_targeting_a_session_that_closes_loses_the_target_and_anyone_may_claim_it()
    {
        var hub = new AggregateHub();
        var target = hub.Open("target", T0);
        Assert.True(hub.Board(RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["expectedVersion"] = 0, ["workflow"] = ReadyWorkflow(target.Id) }, T0).Ok);
        var watch = Assert.Single(hub.Of(AggregateKinds.Board, BoardKey).Effects, effect => effect.Operation == Operations.SessionTargetWatch);
        Assert.Equal(target.Id, watch.TargetKey);
        hub.DeliverAll(T0);
        Assert.Equal(RelayErrorCodes.WorkflowNotReady, hub.Board(RelayTools.TaskClaim, new() { ["taskId"] = "t", ["expectedVersion"] = 1, ["sessionId"] = "other" }, T0).Code);

        // The target is closed; its expiry reaches the board, which drops the target.
        Assert.True(hub.Session(target, RelayTools.SessionBind, new() { ["expectedVersion"] = 0, ["label"] = "smoke target" }, T0).Ok);
        hub.DeliverAll(T0);
        hub.Close(new() { ["targetSessionId"] = target.Id }, T0.AddMinutes(1));
        hub.DeliverAll(T0.AddMinutes(1));
        var task = hub.State(AggregateKinds.Board, BoardKey)["tasks"]!["t"]!.AsObject();
        Assert.Null(task["workflow"]!["targetSessionId"]);
        Assert.Equal(2, task["version"]!.GetValue<long>());
        Assert.True(hub.Board(RelayTools.TaskClaim, new() { ["taskId"] = "t", ["expectedVersion"] = 2, ["sessionId"] = "other" }, T0.AddMinutes(2)).Ok);

        // A target named after its session closed hears so at once.
        Assert.True(hub.Board(RelayTools.TaskUpdate, new() { ["taskId"] = "late", ["expectedVersion"] = 0, ["workflow"] = ReadyWorkflow(target.Id) }, T0.AddMinutes(3)).Ok);
        hub.DeliverAll(T0.AddMinutes(3));
        Assert.Null(hub.State(AggregateKinds.Board, BoardKey)["tasks"]!["late"]!["workflow"]!["targetSessionId"]);
        // A task targeting another, live session keeps its target.
        var live = hub.Open("live", T0);
        Assert.True(hub.Board(RelayTools.TaskUpdate, new() { ["taskId"] = "kept", ["expectedVersion"] = 0, ["workflow"] = ReadyWorkflow(live.Id) }, T0.AddMinutes(4)).Ok);
        hub.DeliverAll(T0.AddMinutes(4));
        Assert.Equal(live.Id, hub.State(AggregateKinds.Board, BoardKey)["tasks"]!["kept"]!["workflow"]!["targetSessionId"]!.GetValue<string>());
    }

    [Fact]
    public void A_settling_report_makes_room_in_superseded_attempts_and_other_actions_reports()
    {
        // A task close to its byte limit, its bulk in superseded attempts and settled actions' report bodies and bindings, and one action in flight.
        var body = new string('x', 6_000);
        var attempts = new JsonArray();
        var actions = new JsonObject
        {
            ["live"] = new JsonObject
            {
                ["actionId"] = "live", ["attempt"] = 1, ["status"] = "invoking", ["sessionId"] = "s", ["principalId"] = "alice", ["restoration"] = "not_required",
                ["requiresRestoration"] = false, ["resourceIds"] = new JsonArray(), ["startedAt"] = T0.ToString("O"), ["inputFingerprint"] = "f"
            }
        };
        for (var i = 0; i < 20; i++)
            actions["done-" + i] = new JsonObject { ["actionId"] = "done-" + i, ["attempt"] = 1, ["status"] = "completed", ["restoration"] = "not_required", ["report"] = body, ["startedAt"] = T0.AddMinutes(-i).ToString("O") };
        var task = new JsonObject
        {
            ["taskId"] = "t", ["version"] = 5, ["owner"] = "s", ["leaseUntil"] = T0.AddHours(1).ToString("O"), ["claimGeneration"] = 1, ["actions"] = actions,
            ["attemptHistory"] = attempts, ["messages"] = new JsonArray(), ["reports"] = new JsonArray(), ["facts"] = new JsonObject()
        };
        while (JsonFields.ByteSize(task) + 12_500 < RelayLimits.MaxTaskBytes)
            attempts.Add(new JsonObject { ["actionId"] = "old", ["attempt"] = attempts.Count + 1, ["status"] = "failed", ["restoration"] = "not_required", ["report"] = body, ["binding"] = new JsonObject { ["b"] = body } });
        var superseded = attempts.Count;
        // The report below adds about 14 KB (its entry and the action's own copy), more than the task has left.
        Assert.InRange(JsonFields.ByteSize(task), RelayLimits.MaxTaskBytes - 12_500, RelayLimits.MaxTaskBytes - 1);
        var board = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["tasks"] = new JsonObject { ["t"] = task } }.ToJsonString() } };

        var report = new JsonObject
        {
            ["repositoryId"] = "repo", ["requestId"] = "report", ["sessionId"] = "s", ["taskId"] = "t", ["actionId"] = "live", ["attempt"] = 1, ["outcome"] = "completed",
            ["expectedVersion"] = 5, ["report"] = new string('r', 7_000)
        };
        var settled = CoordinationStateMachine.Apply(board, AggregateKinds.Board, BoardKey, Alice, RelayTools.ActionReport, report, T0);
        Assert.True(settled.Ok, settled.Code + " " + settled.DataJson);
        var after = JsonNode.Parse(board.Value.Json)!["tasks"]!["t"]!.AsObject();
        Assert.True(JsonFields.ByteSize(after) <= RelayLimits.MaxTaskBytes);
        Assert.Equal("completed", after["actions"]!["live"]!["status"]!.GetValue<string>());
        // The settled action and its report keep their bodies, and every superseded attempt is still listed.
        Assert.Equal(7_000, after["actions"]!["live"]!["report"]!.GetValue<string>().Length);
        Assert.Equal(superseded, after["attemptHistory"]!.AsArray().Count);
        Assert.NotNull(after["attemptHistory"]![superseded - 1]!["report"]);
        Assert.Null(after["attemptHistory"]![0]!["report"]);
    }

    [Fact]
    public async Task With_attention_off_a_working_session_still_reports_its_activity_to_the_directory()
    {
        var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "session-lifecycle-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddSingleton(new AttentionOptions { Enabled = false }).AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            async Task<JsonObject> Call(string tool, object args) => Data(await application.ExecuteAsync(Alice, tool, JsonSerializer.SerializeToElement(args)));
            var opened = await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-1", label = "Chat" });
            var (id, token) = (opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>());
            await Call(RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = id, sessionToken = token });
            Assert.True((await factory.GetGrain<ISessionGrain>(id).ExecuteAsync(Alice with { IsInternal = true }, Operations.Dispatch, "{}")).Ok);
            var worker = (await Call(RelayTools.WorkerList, new { repositoryId = "repo" }))["items"]!.AsArray().Single(item => item!["sessionId"]!.GetValue<string>() == id)!;
            Assert.NotNull(worker["activeAt"]);
        }
        finally { await host.StopAsync(); host.Dispose(); }
    }

    private static JsonObject ReadyWorkflow(string target) => new()
    {
        ["state"] = "ready", ["targetSessionId"] = target,
        ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human", ["restoration"] = "r", ["stopCondition"] = "s" },
        ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }),
        ["blockers"] = new JsonArray()
    };

    private static JsonObject Data(RelayReply reply)
    {
        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
        return JsonNode.Parse(reply.DataJson)!.AsObject();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

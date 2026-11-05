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
    public async Task A_retried_session_open_whose_session_has_since_closed_says_so_and_the_chat_opens_another()
    {
        var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "session-replay-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            Task<RelayReply> Call(RelayCaller caller, string tool, object args) => application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            var opened = Data(await Call(Alice, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-1", label = "smoke" }));
            var id = opened["sessionId"]!.GetValue<string>();
            Assert.True((await Call(new RelayCaller("admin", true, ["repo"]), RelayTools.ControlRequest, new { repositoryId = "repo", requestId = "close-1", command = "close_sessions", targetSessionId = id })).Ok);
            // The directory's outbox delivers the queued open (already applied) and the close.
            Assert.True((await factory.GetGrain<IControlGrain>(AggregateKinds.ControlKey).ExecuteAsync(Alice with { IsInternal = true }, Operations.Dispatch, "{}")).Ok);

            // The chat's reply to its session_open was lost, so it retries with the same requestId: the directory replays the same handle,
            // and the hub's direct open finds that session closed.
            var replayed = await Call(Alice, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-1", label = "smoke" });
            Assert.Equal(RelayErrorCodes.SessionUnknown, replayed.Code);
            Assert.True(JsonNode.Parse(replayed.DataJson)!["expired"]!.GetValue<bool>());
            // As SessionUnknown asks, the chat opens again with a new requestId, and gets a new session.
            var fresh = Data(await Call(Alice, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-2", label = "smoke" }));
            Assert.NotEqual(id, fresh["sessionId"]!.GetValue<string>());
        }
        finally { await host.StopAsync(); host.Dispose(); }
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

    private static JsonObject Control(AggregateHub hub) => hub.State(AggregateKinds.Control, AggregateKinds.ControlKey);

    /// <summary>The kinds of the open attention items at <paramref name="chat"/>'s session, with what each is about.</summary>
    private static List<(string Kind, string? Subject)> Items(AggregateHub hub, Chat chat) =>
        (hub.State(AggregateKinds.Session, chat.Id)["attention"] as JsonObject ?? []).Select(entry => entry.Value!.AsObject())
            .Select(item => (item["kind"]!.GetValue<string>(), item["subject"]?.GetValue<string>())).ToList();

}

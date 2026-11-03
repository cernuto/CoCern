using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;
using static CoCern.Tests.AggregateHub;

namespace CoCern.Tests;

/// <summary>
/// The control directory stores only the keys <see cref="ControlState"/> declares. The document is built as the hub builds it, through
/// <see cref="CoordinationStateMachine.Apply"/> and the sessions' own answers: a bound manager that reports activity, a session held
/// for unread mail, one that answers for work, a close still in flight, a registered resource with an alias, a pause and a cancellation.
/// </summary>
public sealed class ControlStateShapeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller SessionCaller = Alice with { IsInternal = true };

    private static RelayReply Admin(AggregateHub hub, JsonObject args, DateTimeOffset now) =>
        hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, args, AggregateHub.Admin, now);

    [Fact]
    public void The_control_directory_stores_only_the_keys_its_state_declares()
    {
        var hub = new AggregateHub();
        var manager = hub.Open("manager", T0, machineId: "pc-1", role: SessionRoles.Manager);
        var worker = hub.Open("worker", T0, machineId: "pc-1");
        var owner = hub.Open("owner", T0);
        var pending = hub.Open("pending", T0);
        hub.DeliverAll(T0);
        Assert.True(hub.Session(manager, RelayTools.SessionBind, new() { ["expectedVersion"] = 0, ["binding"] = new JsonObject { ["roslyn"] = "codelens" } }, T0.AddMinutes(1)).Ok);
        Assert.True(hub.Verify(manager, T0.AddMinutes(2)).Ok);
        Assert.True(hub.Send(manager, worker.Id, "please review", T0.AddMinutes(3)).Ok);
        hub.DeliverAll(T0.AddMinutes(3));
        // The owner's session answers for an action (as a board's report would tell it), and says so to the directory.
        Assert.True(hub.Apply(SessionOwnership.TargetKind, AggregateKinds.ControlKey, SessionOwnership.Operation, new SessionOwnership(Repository, owner.Id, true, T0.AddMinutes(4), 1).ToJson(), SessionCaller, T0.AddMinutes(4)).Ok);
        Assert.True(Admin(hub, new() { ["command"] = "register_resource", ["resourceId"] = "printer", ["machineId"] = "pc-1", ["aliases"] = new JsonArray("lp"), ["requestId"] = "register" }, T0.AddMinutes(5)).Ok);
        Assert.True(Admin(hub, new() { ["command"] = "stop", ["sessionId"] = manager.Id, ["actionId"] = "deploy", ["requestId"] = "stop" }, T0.AddMinutes(6)).Ok);

        // A day later the manager is still working; an idle close takes the worker, which refuses for its unread mail and comes back
        // held, and the pending session, whose answer has not arrived. The owner is kept for its work.
        var later = T0.AddDays(1);
        Assert.True(hub.Verify(manager, later).Ok);
        hub.DeliverAll(later);
        var closed = AggregateHub.Data(hub.Close(new() { ["idleMinutes"] = RelayLimits.MinCloseSessionsIdleMinutes }, later));
        Assert.Equal((2, 1), (closed["closed"]!.GetValue<int>(), closed["kept"]!.GetValue<int>()));
        var inFlight = hub.Discard(Operations.SessionExpire);
        foreach (var expire in inFlight.Where(effect => effect.TargetKey != pending.Id)) Accept(expire, hub.Deliver(expire, later));
        hub.DeliverAll(later);

        var json = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey);
        Assert.Empty(ControlState.Shape.Undeclared(json));

        // The shape is not vacuous: the document holds every key it declares, but the ones only another aggregate or a full replay cache writes.
        Assert.Empty(ControlState.Shape.Fields.Keys.Except([CommonState.RepositoryIdKey, ReadModels.RequestTombstonesKey]).Except(json.Select(entry => entry.Key)));
        var entryKeys = json[ControlState.SessionsKey]!.AsObject().SelectMany(entry => entry.Value!.AsObject().Select(field => field.Key)).ToHashSet();
        Assert.Empty(DirectoryEntry.Shape.Fields.Keys.Except(entryKeys));

        // Its typed members read what the handlers wrote.
        var state = new ControlState(json);
        Assert.Equal(SessionRoles.Manager, state.Entry(manager.Id)!.Role);
        Assert.Equal(later, state.Entry(manager.Id)!.ActiveAt);
        Assert.Equal(later, state.Entry(worker.Id)!.MailHeldAt);
        Assert.True(state.Entry(owner.Id)!.OwnsWork(later));
        Assert.Equal(1, state.Entry(owner.Id)!.WorkSeq);
        Assert.Null(state.Entry(pending.Id));
        var fence = JsonNode.Parse(inFlight.Single(effect => effect.TargetKey == pending.Id).PayloadJson)!["closeSeq"]!.GetValue<long>();
        Assert.True(state.Fenced(pending.Id, fence));
        Assert.Equal("printer", state.Resources.Find(Repository, "PC-1", "LP")!.ResourceId);
        Assert.Null(state.Resources.Find("other", "pc-1", "lp"));
        Assert.Equal(["lp", "printer"], state.Resources.Resource("printer")!.Aliases);
        Assert.True(state.Paused);
        var (target, request) = Assert.Single(state.CancellationsOf(manager.Id));
        Assert.Equal(manager.Id + ":deploy", target);
        var cancellation = new Cancellation(request!.AsObject());
        Assert.Equal(("admin", T0.AddMinutes(6), Cancellation.RequestedUnknown), (cancellation.RequestedBy, cancellation.At, cancellation.Status));
        // A request is stored exactly as it always was, its time as the serializer writes one.
        Assert.Equal(JsonSerializer.SerializeToNode(new { requestedBy = "admin", at = T0.AddMinutes(6), status = "requested_unknown" })!.ToJsonString(), request.ToJsonString());
    }

    [Fact]
    public void A_worker_listing_leaves_out_the_entrys_secret_principal_and_the_directorys_own_marks()
    {
        var entry = new DirectoryEntry(new SessionRecord { SessionId = "s1", PrincipalId = "alice", RepositoryId = "repo", SessionToken = "secret" }
            .WithInitialFreshness(T0).ToJson());
        entry.OwnsWorkAt = T0.ToUnixTimeSeconds(); entry.WorkSeq = 2; entry.MailHeldAt = T0;
        var worker = entry.Worker(T0.AddMinutes(1), "hub-1");
        Assert.Equal(["availability", "bindingVersion", "freshUntil", "observedAt", "repositoryId", "sessionId", "stale"], worker.Select(field => field.Key).Order());
        // From another hub incarnation, so stale: its availability is not to be trusted.
        Assert.Equal((true, "unknown"), (worker["stale"]!.GetValue<bool>(), worker["availability"]!.GetValue<string>()));
    }
}

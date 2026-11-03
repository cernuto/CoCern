using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The hub's direct requests to the control directory and to locks, and the outbox dispatch every grain takes: each reads back exactly
/// what it writes, names the fields it is missing (never their values), and its handler answers a malformed one with
/// <see cref="RelayErrorCodes.MalformedEffect"/> while still refusing a client's bad name as invalid arguments.
/// </summary>
public sealed class DirectoryAndLockRequestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);
    private static readonly RelayCaller Admin = new("admin", true, ["repo"], true);

    private static string Json<T>(T request) where T : class, IInternalRequest<T> => request.ToJson().ToJsonString();

    private static T RoundTrip<T>(T request) where T : class, IInternalRequest<T>
    {
        Assert.True(T.Decode(request.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
        Assert.Equal(Json(request), Json(decoded));
        return decoded;
    }

    private static IReadOnlyList<string> Malformed<T>(JsonObject payload) where T : class, IInternalRequest<T>
    {
        Assert.False(T.Decode(payload).TryGet(out _, out var fields));
        return fields;
    }

    private static JsonObject Without(JsonObject payload, params string[] fields)
    {
        foreach (var field in fields) payload.Remove(field);
        return payload;
    }

    private static RelayReply Apply<T>(StateEnvelope<CoordinationDocument> envelope, string key, JsonObject args, RelayCaller? caller = null) where T : class, IInternalRequest<T> =>
        CoordinationStateMachine.Apply(envelope, T.TargetKind, key, caller ?? Hub, T.Operation, args, Now);

    private static JsonObject Data(RelayReply reply)
    {
        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
        return JsonNode.Parse(reply.DataJson)!.AsObject();
    }

    [Fact]
    public void Directory_requests_read_back_and_name_their_missing_fields()
    {
        Assert.Equal("vs", RoundTrip(new ResourceResolve("repo", "vs", "pc-a")).ResourceId);
        // A client's name is taken as given, even empty: the directory refuses it as invalid arguments, not as a malformed request.
        Assert.Equal("", RoundTrip(new ResourceResolve("repo", "")).ResourceId);
        Assert.Equal(["resourceId"], Malformed<ResourceResolve>(Without(new ResourceResolve("repo", "vs").ToJson(), "resourceId")));
        Assert.Equal(["vs", "printer"], RoundTrip(new ResourceResolveAll("repo", ["vs", "printer"], "pc-a")).ResourceIds);
        var mistyped = new ResourceResolveAll("repo", ["vs"]).ToJson(); mistyped["resourceIds"] = new JsonArray("vs", 3);
        Assert.Equal(["resourceIds"], Malformed<ResourceResolveAll>(mistyped));
        Assert.Equal((24L, SessionSweep.EveryRepository), (RoundTrip(new SessionSweep(24)).IdleHours, RoundTrip(new SessionSweep(24)).RepositoryId));
        Assert.Equal(["idleHours"], Malformed<SessionSweep>(Without(new SessionSweep(24).ToJson(), "idleHours")));
        Assert.Equal(["s1", "s2"], RoundTrip(new SessionOwnersQuery("repo", ["s1", "s2"])).SessionIds);
        Assert.Equal(["sessionIds"], Malformed<SessionOwnersQuery>(Without(new SessionOwnersQuery("repo", ["s1"]).ToJson(), "sessionIds")));
        Assert.Equal(["s1"], RoundTrip(new SessionsOpenQuery("repo", ["s1"])).SessionIds);
        Assert.Equal(["repositoryId", "sessionIds"], Malformed<SessionsOpenQuery>(new JsonObject()));
        Assert.All(new[] { ResourceResolve.TargetKind, ResourceResolveAll.TargetKind, SessionSweep.TargetKind, SessionOwnersQuery.TargetKind, SessionsOpenQuery.TargetKind },
            kind => Assert.Equal(AggregateKinds.Control, kind));
    }

    [Fact]
    public void Lock_and_dispatch_requests_read_back_and_name_their_missing_fields()
    {
        Assert.Equal("s1", RoundTrip(new ClaimStateQuery("repo", "s1")).SessionId);
        Assert.Equal(["sessionId"], Malformed<ClaimStateQuery>(Without(new ClaimStateQuery("repo", "s1").ToJson(), "sessionId")));
        RoundTrip(new LockLeaseCheck("repo"));
        Assert.Equal(["repositoryId"], Malformed<LockLeaseCheck>(new JsonObject()));
        Assert.Equal((AggregateKinds.Lock, AggregateKinds.Lock), (ClaimStateQuery.TargetKind, LockLeaseCheck.TargetKind));

        // The pump's dispatch names its due item; an empty one delivers everything due, as it always has.
        Assert.Equal("""{"workId":"w-1"}""", Json(RoundTrip(new OutboxDispatch("w-1"))));
        Assert.Equal("{}", Json(RoundTrip(new OutboxDispatch())));
        RoundTrip(new OutboxDispatch(EffectOperation: Operations.Block, ActionKey: "repo/default/t/a/1"));
        Assert.Equal(["workId"], Malformed<OutboxDispatch>(new JsonObject { ["workId"] = 7 }));
        Assert.Equal(InternalRequests.AnyAggregate, OutboxDispatch.TargetKind);
    }

    [Fact]
    public void The_directory_answers_a_malformed_request_by_name_and_still_refuses_a_bad_resource_name()
    {
        var control = new StateEnvelope<CoordinationDocument>();
        Assert.True(CoordinationStateMachine.Apply(control, AggregateKinds.Control, AggregateKinds.ControlKey, Admin, RelayTools.ControlRequest,
            new() { ["repositoryId"] = "repo", ["command"] = "register_resource", ["resourceId"] = "pc-a", ["machineId"] = "a", ["aliases"] = new JsonArray("vs"), ["requestId"] = "r" }, Now).Ok);

        Assert.Equal("pc-a", Data(Apply<ResourceResolve>(control, AggregateKinds.ControlKey, new ResourceResolve("repo", "vs", "a").ToJson()))["resourceId"]!.GetValue<string>());
        Assert.Equal(RelayErrorCodes.ResourceUnknown, Apply<ResourceResolve>(control, AggregateKinds.ControlKey, new ResourceResolve("repo", "vs", "b").ToJson()).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Apply<ResourceResolve>(control, AggregateKinds.ControlKey, new ResourceResolve("repo", "").ToJson()).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Apply<ResourceResolve>(control, AggregateKinds.ControlKey, new ResourceResolve("repo", new string('r', RelayLimits.MaxIdentifierLength + 1)).ToJson()).Code);
        var resolved = Data(Apply<ResourceResolveAll>(control, AggregateKinds.ControlKey, new ResourceResolveAll("repo", ["vs", "pc-a", "unknown"], "a").ToJson()))["resolved"]!.AsObject();
        Assert.Equal("""{"vs":"pc-a","pc-a":"pc-a"}""", resolved.ToJsonString());

        foreach (var (operation, payload, field) in new[]
        {
            (Operations.Resolve, Without(new ResourceResolve("repo", "vs").ToJson(), "resourceId"), "resourceId"),
            (Operations.ResolveAll, Without(new ResourceResolveAll("repo", ["vs"]).ToJson(), "resourceIds"), "resourceIds"),
            (Operations.SweepSessions, Without(new SessionSweep(24, "repo").ToJson(), "idleHours"), "idleHours"),
            (Operations.SessionOwners, Without(new SessionOwnersQuery("repo", ["s1"]).ToJson(), "sessionIds"), "sessionIds"),
            (Operations.SessionsOpen, Without(new SessionsOpenQuery("repo", ["s1"]).ToJson(), "sessionIds"), "sessionIds")
        })
        {
            var refused = CoordinationStateMachine.Apply(control, AggregateKinds.Control, AggregateKinds.ControlKey, Admin, operation, payload, Now);
            Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
            Assert.Equal([field], JsonNode.Parse(refused.DataJson)!["fields"]!.AsArray().Select(name => name!.GetValue<string>()));
        }
        // A sweep with no idle time to judge by is still refused as before, by the field.
        Assert.Equal(RelayErrorCodes.InvalidArguments, CoordinationStateMachine.Apply(control, AggregateKinds.Control, AggregateKinds.ControlKey, Admin, SessionSweep.Operation, new SessionSweep(0, "repo").ToJson(), Now).Code);
    }

    [Fact]
    public void A_lock_answers_its_typed_requests_and_names_what_a_malformed_one_lacks()
    {
        var lockState = new StateEnvelope<CoordinationDocument>();
        Assert.True(CoordinationStateMachine.Apply(lockState, AggregateKinds.Lock, "printer", Hub, RelayTools.ClaimAcquire, new() { ["repositoryId"] = "repo", ["sessionId"] = "s1", ["requestId"] = "c" }, Now).Ok);
        var held = Data(Apply<ClaimStateQuery>(lockState, "printer", new ClaimStateQuery("repo", "s1").ToJson()));
        Assert.Equal((false, true), (held["busy"]!.GetValue<bool>(), held["held"]!.GetValue<bool>()));
        var other = Data(Apply<ClaimStateQuery>(lockState, "printer", new ClaimStateQuery("repo", "s2").ToJson()));
        Assert.Equal((true, false), (other["busy"]!.GetValue<bool>(), other["held"]!.GetValue<bool>()));
        Assert.True(Apply<LockLeaseCheck>(lockState, "printer", new LockLeaseCheck("repo").ToJson()).Ok);

        var refused = Apply<ClaimStateQuery>(lockState, "printer", Without(new ClaimStateQuery("repo", "s1").ToJson(), "sessionId"));
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
        Assert.Equal(["sessionId"], JsonNode.Parse(refused.DataJson)!["fields"]!.AsArray().Select(name => name!.GetValue<string>()));
    }
}

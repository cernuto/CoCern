using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

public sealed class NativeFileScopeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
    private static readonly RelayCaller Machine = new("enrolled-principal", false, ["repo", "other"], true, "authenticated-machine");

    [Fact]
    public void Protected_scope_requires_the_exact_current_private_session_and_authenticated_machine()
    {
        var (state, query) = Open(Machine, @"C:\physical\repo");
        var granted = Read(state, query, Machine);
        Assert.True(granted.Ok, granted.Code);
        var scope = JsonNode.Parse(granted.DataJson)!;
        Assert.Equal("authenticated-machine", scope["machineScope"]!.GetValue<string>());
        Assert.Equal(@"C:\physical\repo", scope["canonicalWorkingCopyRoot"]!.GetValue<string>());
        Assert.DoesNotContain(query.SessionToken, granted.DataJson);

        Assert.False(Read(state, query with { SessionToken = "wrong" }, Machine).Ok);
        Assert.False(Read(state, query with { RepositoryId = "other" }, Machine).Ok);
        Assert.False(Read(state, query, Machine with { PrincipalId = "another-principal" }).Ok);
        Assert.False(Read(state, query, Machine with { AuthenticatedMachineId = "reported-label" }).Ok);
        Assert.Equal(RelayErrorCodes.InternalOnly, Read(state, query, Machine with { IsInternal = false }).Code);

        var closed = CoordinationStateMachine.Apply(state, AggregateKinds.Control, AggregateKinds.ControlKey, Machine with { IsAdministrator = true },
            RelayTools.ControlRequest, new JsonObject { ["repositoryId"] = "repo", ["requestId"] = Guid.NewGuid().ToString("N"), ["command"] = "close_sessions", ["targetSessionId"] = query.SessionId, ["force"] = true }, Now);
        Assert.True(closed.Ok, closed.Code);
        Assert.False(Read(state, query, Machine).Ok);
    }

    [Fact]
    public void Legacy_joins_and_callers_without_machine_provenance_never_gain_file_scope()
    {
        var configured = RelayAuthentication.Caller(RelayAuthentication.Principal("configured", false, ["repo"], "Relay"));
        Assert.Null(configured.AuthenticatedMachineId);
        var authenticated = RelayAuthentication.Caller(RelayAuthentication.Principal("enrolled-principal", false, ["repo"], "Relay", "authenticated-machine"));
        var converter = new RelayCallerSurrogateConverter();
        var serialized = converter.ConvertToSurrogate(authenticated);
        Assert.Equal("authenticated-machine", converter.ConvertFromSurrogate(serialized).AuthenticatedMachineId);
        serialized.AuthenticatedMachineId = null;
        Assert.Null(converter.ConvertFromSurrogate(serialized).AuthenticatedMachineId);
        var effects = new List<DurableEffect> { new("effect", AggregateKinds.Session, "session", Operations.Deliver, "{}", Now, false) };
        CoordinationStateMachine.StampEffects(effects, 0, authenticated);
        var payload = JsonNode.Parse(effects[0].PayloadJson)!.AsObject();
        Assert.Equal("authenticated-machine", CoordinationStateMachine.TakeEffectCaller(payload).AuthenticatedMachineId);
        Assert.Null(CoordinationStateMachine.TakeEffectCaller(payload).AuthenticatedMachineId);

        var (legacy, legacyQuery) = Open(Machine, null);
        Assert.False(Read(legacy, legacyQuery, Machine).Ok);
        var local = Machine with { AuthenticatedMachineId = null };
        var (unproven, unprovenQuery) = Open(local, @"C:\physical\repo");
        Assert.False(Read(unproven, unprovenQuery, local).Ok);
    }

    private static (StateEnvelope<CoordinationDocument> State, NativeFileScopeQuery Query) Open(RelayCaller caller, string? protectedRoot)
    {
        var join = new JsonObject
        {
            ["provider"] = "codex", ["nativeId"] = Guid.NewGuid().ToString("D"), ["workspace"] = @"C:\physical\repo",
            ["bindingSecret"] = Convert.ToBase64String(new byte[32]), ["generation"] = Guid.NewGuid().ToString("N")
        };
        if (protectedRoot is not null) join["protectedRoot"] = protectedRoot;
        var arguments = new JsonObject { ["repositoryId"] = "repo", ["machineId"] = "untrusted-label", ["requestId"] = Guid.NewGuid().ToString("N"), ["nativeJoin"] = join };
        Assert.True(NativeParticipant.Admit(caller, arguments));
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        var reply = CoordinationStateMachine.Apply(state, AggregateKinds.Control, AggregateKinds.ControlKey, caller, RelayTools.SessionOpen, arguments, Now);
        Assert.True(reply.Ok, reply.Code);
        Assert.DoesNotContain(@"C:\physical\repo", reply.DataJson);
        var opened = JsonNode.Parse(reply.DataJson)!;
        return (state, new NativeFileScopeQuery("repo", opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>(), arguments[NativeParticipant.Id]!.GetValue<string>()));
    }

    private static RelayReply Read(StateEnvelope<CoordinationDocument> state, NativeFileScopeQuery query, RelayCaller caller) =>
        CoordinationStateMachine.Apply(state, AggregateKinds.Control, AggregateKinds.ControlKey, caller, NativeFileScopeQuery.Operation, query.ToJson(), Now);
}

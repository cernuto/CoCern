using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public sealed class DiscoveryDomainTests
{
    private static readonly RelayCaller Admin = new("hub", true, [], true);
    private static readonly RelayCaller Plain = new("machine:m1", false, ["cocern"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
    private const string Hash = "ABCDEF0123456789";

    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string op, JsonObject args, RelayCaller? caller = null, int seconds = 0) =>
        DiscoveryStateMachine.Apply(state, caller ?? Admin, op, args, Now.AddSeconds(seconds));
    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
    private static RelayReply Enroll(StateEnvelope<CoordinationDocument> state, string id = "m1", string hash = Hash) =>
        Call(state, DiscoveryOperations.Enroll, new() { ["machineId"] = id, ["machineName"] = "PC-B", ["credentialHash"] = hash });
    private static JsonObject Chat(string thread, string repository = "cocern", bool wakeable = true, bool waiter = false, string provider = "codex") => new()
    {
        ["provider"] = provider, ["threadId"] = thread, ["relaySessionId"] = "rs-" + thread, ["repositoryId"] = repository, ["label"] = "chat " + thread, ["wakeable"] = wakeable, ["waiterRunning"] = waiter
    };
    private static RelayReply Announce(StateEnvelope<CoordinationDocument> state, int seconds = 0, params JsonObject[] chats) =>
        Call(state, DiscoveryOperations.Announce, new() { ["machineId"] = "m1", ["machineName"] = "PC-B", ["clients"] = new JsonArray("codex", "claude"), ["chats"] = new JsonArray(chats.Select(x => (JsonNode)x).ToArray()) }, seconds: seconds);
    private static JsonArray Items(RelayReply reply) => Data(reply)["items"]!.AsArray();
    private static RelayReply Directory(StateEnvelope<CoordinationDocument> state, int seconds = 0, params string[] repositories) =>
        Call(state, DiscoveryOperations.Directory, new() { ["repositories"] = new JsonArray(repositories.Select(x => (JsonNode)x).ToArray()) }, seconds: seconds);

    [Fact]
    public void Reenrolling_the_same_id_with_another_hash_is_refused()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Enroll(state).Ok);
        Assert.Equal("AliasConflict", Enroll(state, hash: "OTHER").Code);
    }

    [Fact]
    public void Revoked_machine_id_cannot_enroll_again()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Call(state, DiscoveryOperations.Revoke, new() { ["machineId"] = "m1" }).Ok);
        Assert.Equal("MachineRevoked", Enroll(state).Code);
        Assert.Equal(RelayErrorKind.Denied, RelayErrorCodes.Classify("MachineRevoked"));
        Assert.Equal("NotFound", Call(state, DiscoveryOperations.Validate, new() { ["credentialHash"] = Hash }).Code);
    }

    [Fact]
    public void Directory_filters_repositories_and_hides_thread_ids()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Announce(state, 0, Chat("t-in"), Chat("t-out", repository: "other")).Ok);
        var reply = Directory(state, 0, "cocern");
        Assert.True(reply.Ok);
        var items = Items(reply);
        Assert.Single(items);
        Assert.Equal("rs-t-in", items[0]!["relaySessionId"]!.ToString());
        Assert.Equal("PC-B", items[0]!["machineName"]!.ToString());
        Assert.True(items[0]!["reachable"]!.GetValue<bool>());
        Assert.DoesNotContain("threadId", reply.DataJson);
        Assert.DoesNotContain("t-out", reply.DataJson);
        Assert.DoesNotContain(Hash, reply.DataJson);
        Assert.DoesNotContain("credential", reply.DataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Revoke_requires_administrator()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.Equal("AdministratorRequired", Call(state, DiscoveryOperations.Revoke, new() { ["machineId"] = "m1" }, Plain).Code);
        Assert.True(Call(state, DiscoveryOperations.Validate, new() { ["credentialHash"] = Hash }).Ok);
    }

    [Fact]
    public void Reachability_follows_the_provider_and_only_internal_callers_may_apply()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Announce(state, 0, Chat("cx", wakeable: true, waiter: false), Chat("cl", provider: "claude", wakeable: false, waiter: true), Chat("cl-idle", provider: "claude", wakeable: true, waiter: false)).Ok);
        var reachable = Items(Directory(state, 0, "cocern")).ToDictionary(x => x!["relaySessionId"]!.ToString(), x => x!["reachable"]!.GetValue<bool>());
        Assert.True(reachable["rs-cx"]); Assert.True(reachable["rs-cl"]); Assert.False(reachable["rs-cl-idle"]);
        Assert.Equal("InternalOnly", Call(state, DiscoveryOperations.Directory, new() { ["repositories"] = new JsonArray("cocern") }, new RelayCaller("machine:m1", false, ["cocern"])).Code);
    }

}

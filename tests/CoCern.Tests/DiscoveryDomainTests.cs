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
    public void Enroll_then_validate_resolves_the_machine_principal()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var enrolled = Enroll(state);
        Assert.True(enrolled.Ok);
        Assert.Equal("machine:m1", Data(enrolled)["principalId"]!.ToString());
        var validated = Call(state, DiscoveryOperations.Validate, new() { ["credentialHash"] = Hash });
        Assert.True(validated.Ok);
        Assert.Equal("machine:m1", Data(validated)["principalId"]!.ToString());
        Assert.Equal("m1", Data(validated)["machineId"]!.ToString());
        Assert.Equal("NotFound", Call(state, DiscoveryOperations.Validate, new() { ["credentialHash"] = "unknown" }).Code);
    }

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
    public void Announce_keeps_only_the_newest_chat_per_provider_thread()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        JsonObject Session(string thread, string session, int minutesAgo, string provider = "codex")
        {
            var chat = Chat(thread, provider: provider);
            chat["relaySessionId"] = session; chat["openedAt"] = Now.AddMinutes(-minutesAgo).ToString("O"); chat["wakeable"] = provider != "unknown";
            return chat;
        }
        // A stale node lists the superseded session first; the hub still keeps the newest one per thread, comparing thread IDs without case.
        var reply = Announce(state, 0, Session("t-a", "rs-old", 20), Session("T-A", "rs-new", 1), Session("t-a", "rs-claude", 5, "claude"), Session("t-b", "rs-b", 30),
            Session("unmapped", "rs-u1", 2, "unknown"), Session("unmapped", "rs-u2", 3, "unknown"));
        Assert.True(reply.Ok);
        Assert.Equal(["rs-b", "rs-claude", "rs-new"], Data(reply)["routes"]!.AsArray().Select(x => x!["relaySessionId"]!.ToString()).Order());
        // Unmapped chats share a placeholder thread, so they are never collapsed.
        Assert.Equal(["rs-b", "rs-claude", "rs-new", "rs-u1", "rs-u2"], Items(Directory(state, 0, "cocern")).Select(x => x!["relaySessionId"]!.ToString()).Order());
    }

    [Fact]
    public void Announce_returns_routes_only_for_wakeable_chats_of_that_machine()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        var reply = Announce(state, 0, Chat("t-wake"), Chat("t-quiet", wakeable: false));
        Assert.True(reply.Ok);
        var routes = Data(reply)["routes"]!.AsArray();
        Assert.Single(routes);
        Assert.Equal("t-wake", routes[0]!["threadId"]!.ToString());
        Assert.Equal("rs-t-wake", routes[0]!["relaySessionId"]!.ToString());
        Assert.Equal("cocern", routes[0]!["repositoryId"]!.ToString());
        Assert.Equal("codex", routes[0]!["provider"]!.ToString());
        Assert.Empty(Data(Announce(state, 1))["routes"]!.AsArray());
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
    public void Machine_silent_past_liveness_timeout_is_not_reachable()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Announce(state, 0, Chat("t1")).Ok);
        Assert.True(Items(Directory(state, 90, "cocern"))[0]!["reachable"]!.GetValue<bool>());
        var stale = Items(Directory(state, 91, "cocern"));
        Assert.Single(stale);
        Assert.False(stale[0]!["reachable"]!.GetValue<bool>());
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

    [Fact]
    public void Read_only_operations_leave_the_state_unchanged_and_announcements_are_bounded()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Announce(state, 0, Chat("t1")).Ok);
        var before = state.Value.Json;
        Assert.True(Call(state, DiscoveryOperations.Validate, new() { ["credentialHash"] = Hash }, seconds: 5).Ok);
        Assert.True(Directory(state, 5, "cocern").Ok);
        Assert.Equal(before, state.Value.Json);
        var tooMany = Enumerable.Range(0, DiscoveryLimits.MaxChatsPerMachine + 1).Select(i => Chat("t" + i)).ToArray();
        Assert.Equal("CapacityExceeded", Announce(state, 6, tooMany).Code);
        Assert.Equal("NotFound", Call(state, DiscoveryOperations.Announce, new() { ["machineId"] = "ghost", ["machineName"] = "x", ["clients"] = new JsonArray(), ["chats"] = new JsonArray() }).Code);
        Assert.Equal(before, state.Value.Json);
    }

    [Fact]
    public void A_full_directory_is_truncated_to_the_reply_budget_without_repeated_serialization()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        for (var machine = 0; machine < DiscoveryLimits.MaxMachines; machine++)
        {
            var id = "m" + machine;
            Assert.True(Call(state, DiscoveryOperations.Enroll, new() { ["machineId"] = id, ["machineName"] = "PC-" + machine, ["credentialHash"] = "H" + machine }).Ok);
            var chats = Enumerable.Range(0, DiscoveryLimits.MaxChatsPerMachine).Select(i => (JsonNode)Chat(machine + "-" + i)).ToArray();
            Assert.True(Call(state, DiscoveryOperations.Announce, new() { ["machineId"] = id, ["machineName"] = "PC-" + machine, ["clients"] = new JsonArray(), ["chats"] = new JsonArray(chats) }).Ok);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var reply = Directory(state, 0, "cocern");
        clock.Stop();
        Assert.True(reply.Ok);
        Assert.True(Data(reply)["truncated"]!.GetValue<bool>());
        Assert.True(Replies.FitsWire(reply, RelayLimits.AggregateReplyBytes));
        Assert.InRange(Items(reply).Count, 1, DiscoveryLimits.MaxMachines * DiscoveryLimits.MaxChatsPerMachine - 1);
        // A loose bound, so a busy machine can't fail it: serializing every prefix of these thousands of items, as the binary search avoids,
        // takes minutes, while the search takes well under a second.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Directory took " + clock.Elapsed);
    }

    [Fact]
    public void A_directory_that_fits_is_not_truncated()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Announce(state, 0, Chat("t1"), Chat("t2")).Ok);
        var reply = Directory(state, 0, "cocern");
        Assert.Equal(2, Items(reply).Count);
        Assert.False(Data(reply)["truncated"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("chats", "null-element")]
    [InlineData("chats", "string-element")]
    [InlineData("chats", "not-an-array")]
    [InlineData("clients", "null-element")]
    [InlineData("clients", "number-element")]
    [InlineData("clients", "not-an-array")]
    public void Announce_rejects_malformed_arrays_as_invalid_arguments(string field, string shape)
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        Assert.True(Announce(state, 0, Chat("t1")).Ok);
        var before = state.Value.Json;
        var args = new JsonObject { ["machineId"] = "m1", ["machineName"] = "PC-B", ["clients"] = new JsonArray("codex"), ["chats"] = new JsonArray(Chat("t2")) };
        args[field] = shape switch
        {
            "null-element" => new JsonArray(new JsonNode?[] { null }),
            "string-element" => new JsonArray("not-an-object"),
            "number-element" => new JsonArray(7),
            _ => JsonValue.Create("not-an-array")
        };
        Assert.Equal("InvalidArguments", Call(state, DiscoveryOperations.Announce, args).Code);
        Assert.Equal(before, state.Value.Json);
    }

    [Theory]
    [InlineData("null-element")]
    [InlineData("number-element")]
    [InlineData("not-an-array")]
    public void Directory_rejects_malformed_repositories_as_invalid_arguments(string shape)
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Enroll(state).Ok);
        var before = state.Value.Json;
        JsonNode? repositories = shape switch { "null-element" => new JsonArray(new JsonNode?[] { null }), "number-element" => new JsonArray(7), _ => JsonValue.Create("cocern") };
        Assert.Equal("InvalidArguments", Call(state, DiscoveryOperations.Directory, new() { ["repositories"] = repositories }).Code);
        Assert.Equal(before, state.Value.Json);
    }
}

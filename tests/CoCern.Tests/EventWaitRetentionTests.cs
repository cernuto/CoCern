using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public sealed class EventWaitRetentionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private static RelayCaller Caller => new("principal", false, ["repo"], true);

    [Fact]
    public void A_full_retirement_pool_refuses_atomically_instead_of_erasing_an_unexpired_fence()
    {
        var owner = SaturatedRetirement(); var before = owner.Value.Json;
        Assert.Equal("CapacityExceeded", Register(owner, "one-more").Code);
        Assert.Equal(before, owner.Value.Json);
        Assert.Equal(RelayLimits.MaxRequestTombstones, JsonNode.Parse(owner.Value.Json)!["eventWaitRetired"]!.AsObject().Count);
        Assert.NotNull(JsonNode.Parse(owner.Value.Json)!["eventWaits"]!["existing-0"]);
        Assert.Empty(owner.Effects);
    }

    private static StateEnvelope<CoordinationDocument> SaturatedRetirement()
    {
        var owner = Open("owner");
        for (var index = 0; index < 256; index++)
        {
            Assert.True(Register(owner, "existing-" + index).Ok);
            var cancel = Args("owner"); cancel["waitId"] = "existing-" + index;
            Assert.True(Apply(owner, "owner", "__event_wait_cancel", cancel).Ok);
            owner.Effects.Clear();
        }
        // Valid private replay bookkeeping, not injected grants or authority.
        var state = JsonNode.Parse(owner.Value.Json)!.AsObject(); var fences = new JsonObject();
        for (var index = 0; index < RelayLimits.MaxRequestTombstones; index++)
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fixture-fence-" + index)));
            fences[key] = Epoch.AddDays(30).ToUnixTimeSeconds();
        }
        state["eventWaitRetired"] = fences;
        owner.Value = new() { Json = state.ToJsonString() };
        return owner;
    }

    private static RelayReply Register(StateEnvelope<CoordinationDocument> owner, string id, DateTimeOffset? at = null)
    {
        var now = at ?? Epoch;
        var args = Args("owner");
        args["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest(id, "claim_released", "resource", "1", now.AddMinutes(10)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Apply(owner, "owner", "__event_wait_register", args, now);
    }
    private static StateEnvelope<CoordinationDocument> Open(string id)
    {
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        var args = Args(id); args["principalId"] = "principal";
        Assert.True(Apply(state, id, "__open", args).Ok); state.Effects.Clear(); return state;
    }
    private static JsonObject Args(string id) => new() { ["repositoryId"] = "repo", ["sessionId"] = id, ["sessionToken"] = "fixture-token" };
    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string id, string operation, JsonObject args, DateTimeOffset? now = null) =>
        CoordinationStateMachine.Apply(state, "session", id, Caller, operation, args, now ?? Epoch);
}

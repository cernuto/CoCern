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
    public void More_than_256_finished_waits_do_not_exhaust_a_session_and_recent_evicted_ids_refuse_replay()
    {
        var owner = Open("owner");
        for (var index = 0; index < 300; index++)
        {
            Assert.True(Register(owner, "wait-" + index).Ok);
            var cancel = Args("owner"); cancel["waitId"] = "wait-" + index;
            Assert.True(Apply(owner, "owner", "__event_wait_cancel", cancel).Ok);
            owner.Effects.Clear();
        }
        Assert.InRange(JsonNode.Parse(owner.Value.Json)!["eventWaits"]!.AsObject().Count, 1, 256);
        var before = owner.Value.Json;
        Assert.Equal("RequestExpired", Register(owner, "wait-0").Code);
        Assert.Equal(before, owner.Value.Json);
        Assert.Empty(owner.Effects);
    }

    [Fact]
    public void Terminal_compaction_never_evicts_an_active_wait()
    {
        var owner = Open("owner"); Assert.True(Register(owner, "pending").Ok);
        for (var index = 0; index < 300; index++)
        {
            Assert.True(Register(owner, "finished-" + index).Ok);
            var cancel = Args("owner"); cancel["waitId"] = "finished-" + index;
            Assert.True(Apply(owner, "owner", "__event_wait_cancel", cancel).Ok);
            owner.Effects.Clear();
        }
        var active = JsonNode.Parse(owner.Value.Json)!["eventWaits"]!["pending"];
        Assert.NotNull(active);
        Assert.Null(active["terminal"]);
    }

    [Fact]
    public void Canceled_source_slots_are_reused_without_rearming_a_recent_retired_watch()
    {
        var source = Open("peer");
        EventWaitOwnerWatch Watch(int index) => new("repo", "owner-" + index, "wait-" + index, "watch-" + index, "peer", Epoch.AddMinutes(10));
        for (var index = 0; index < 150; index++)
        {
            Assert.True(EventWaitSources.Apply(source, Caller, "peer", "watch", Args("peer"), Epoch, Watch(index)).Ok);
            Assert.True(EventWaitSources.Apply(source, Caller, "peer", "unwatch", Args("peer"), Epoch, Watch(index)).Ok);
        }
        var replay = EventWaitSources.Apply(source, Caller, "peer", "watch", Args("peer"), Epoch, Watch(0));
        Assert.True(replay.Ok);
        Assert.False(JsonNode.Parse(replay.DataJson)!["watching"]!.GetValue<bool>());
        Assert.Empty(source.Effects);
    }

    [Fact]
    public void A_closed_source_answers_a_new_watch_even_when_its_old_watch_slots_are_full()
    {
        var source = Open("peer");
        for (var index = 0; index < 128; index++)
            Assert.True(EventWaitSources.Apply(source, Caller, "peer", "watch", Args("peer"), Epoch,
                new("repo", "owner-" + index, "wait-" + index, "watch-" + index, "peer", Epoch.AddMinutes(10))).Ok);
        var close = Args("peer"); close["closeSeq"] = 1; close["force"] = true; close["requestedBy"] = "fixture";
        Assert.True(Apply(source, "peer", "__session_expire", close).Ok); source.Effects.Clear();
        Assert.True(EventWaitSources.Apply(source, Caller, "peer", "watch", Args("peer"), Epoch,
            new("repo", "late-owner", "late-wait", "late-watch", "peer", Epoch.AddMinutes(10))).Ok);
        var notice = Assert.Single(source.Effects, effect => effect.Operation == EventWaitSources.LifecycleOperation);
        Assert.Equal("late-owner", notice.TargetKey);
    }

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

    [Fact]
    public void A_late_lifecycle_after_terminal_compaction_is_consumed_without_a_second_receipt()
    {
        var owner = Open("owner"); var source = Open("peer");
        Assert.True(Register(owner, "original").Ok);
        var watch = new EventWaitOwnerWatch("repo", "owner", "original", "original-watch", "peer", Epoch.AddMinutes(10));
        Assert.True(EventWaitSources.Apply(owner, Caller with { IsInternal = false }, "owner", "bind", Args("owner"), Epoch, watch).Ok);
        var install = Assert.Single(owner.Effects, effect => effect.Operation == EventWaitSources.WatchOperation);
        Assert.True(CoordinationStateMachine.Apply(source, "session", "peer", Caller, install.Operation, JsonNode.Parse(install.PayloadJson)!.AsObject(), Epoch).Ok);
        var close = Args("peer"); close["closeSeq"] = 1; close["force"] = true; close["requestedBy"] = "fixture";
        Assert.True(Apply(source, "peer", "__session_expire", close).Ok);
        var lifecycle = Assert.Single(source.Effects, effect => effect.Operation == EventWaitSources.LifecycleOperation);
        var cancel = Args("owner"); cancel["waitId"] = "original";
        Assert.True(Apply(owner, "owner", "__event_wait_cancel", cancel).Ok); owner.Effects.Clear();
        for (var index = 0; index < 300; index++)
        {
            Assert.True(Register(owner, "finished-" + index).Ok);
            cancel["waitId"] = "finished-" + index;
            Assert.True(Apply(owner, "owner", "__event_wait_cancel", cancel).Ok); owner.Effects.Clear();
        }
        Assert.Null(JsonNode.Parse(owner.Value.Json)!["eventWaits"]!["original"]);
        Assert.True(CoordinationStateMachine.Apply(owner, "session", "owner", Caller, lifecycle.Operation,
            JsonNode.Parse(lifecycle.PayloadJson)!.AsObject(), Epoch.AddSeconds(1)).Ok);
        Assert.Empty(owner.Effects);
        Assert.Null(JsonNode.Parse(owner.Value.Json)!["eventWaits"]!["original"]);
    }

    [Fact]
    public void Expired_retirement_fences_make_room_without_a_lifetime_registration_limit()
    {
        var owner = SaturatedRetirement();
        Assert.True(Register(owner, "after-window", Epoch.AddDays(31)).Ok);
        Assert.InRange(JsonNode.Parse(owner.Value.Json)!["eventWaitRetired"]!.AsObject().Count, 1, RelayLimits.MaxRequestTombstones);
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

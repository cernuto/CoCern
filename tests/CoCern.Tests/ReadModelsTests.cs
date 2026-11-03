using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The Domain's rules for readers of stored state (<see cref="ReadModels"/>), and the two readers that used to keep their own copies of
/// the reachability rule: the discovery directory and the dashboard's observation.
/// </summary>
public sealed class ReadModelsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");

    [Theory]
    [InlineData("""{"sessionId":"s"}""", false)]
    [InlineData("""{"sessionId":"s","expiredAt":null}""", false)]
    [InlineData("""{"sessionId":"s","expiredAt":"2026-11-03T00:00:00.0000000+00:00"}""", true)]
    [InlineData("""{"binding":{"expiredAt":"2026-11-03T00:00:00Z","tools":["a"]},"sessionId":"s","label":"\"expiredAt\""}""", false)]
    [InlineData("""{"binding":{"expiredAt":"x"},"inbox":{"m":{"text":"expiredAt"}},"expiredAt":"2026-11-03T00:00:00Z"}""", true)]
    public void Only_the_top_level_expiredAt_closes_a_session(string json, bool closed)
    {
        Assert.Equal(closed, ReadModels.IsClosed(json));
        Assert.Equal(closed, ReadModels.IsClosed(JsonNode.Parse(json)!.AsObject()));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-90, true)]
    [InlineData(-91, false)]
    // A stamp a little ahead of the reader's clock: an announcement landed after the reader took its time.
    [InlineData(10, true)]
    [InlineData(90, true)]
    // Far ahead: a clock since set back; that is no evidence of recent contact.
    [InlineData(91, false)]
    [InlineData(3600, false)]
    public void A_machine_is_live_within_the_timeout_of_now_on_either_side(int seenOffsetSeconds, bool live)
    {
        Assert.Equal(live, ReadModels.IsLive(Now.AddSeconds(seenOffsetSeconds), Now));
        Assert.False(ReadModels.IsLive(null, Now));
        Assert.False(ReadModels.IsLive(DateTimeOffset.MinValue, Now));
    }

    [Theory]
    [InlineData(ChatProviders.Codex, true, false, true)]
    [InlineData(ChatProviders.Codex, false, true, false)]
    [InlineData("Codex", true, false, true)]
    [InlineData(ChatProviders.Claude, false, true, true)]
    [InlineData(ChatProviders.Claude, true, false, false)]
    [InlineData("CLAUDE", false, true, true)]
    [InlineData(ChatProviders.Unknown, true, true, false)]
    [InlineData("", true, true, false)]
    public void A_chat_is_reachable_through_its_providers_own_wake_path(string provider, bool wakeable, bool waiterRunning, bool reachable)
    {
        Assert.Equal(reachable, ReadModels.IsReachable(provider, wakeable, waiterRunning, Now, Now));
        Assert.False(ReadModels.IsReachable(provider, wakeable, waiterRunning, Now.AddSeconds(-91), Now));
    }

    /// <summary>
    /// The directory and the dashboard read the same announcement and must agree about every chat, including at the edges where their
    /// copies of the rule used to differ (a stamp ahead of the reader's clock).
    /// </summary>
    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(91)]
    [InlineData(-120)]
    public async Task The_directory_and_the_dashboard_agree_on_reachability(int readOffsetSeconds)
    {
        var discovery = new StateEnvelope<CoordinationDocument>();
        var hub = new RelayCaller("hub", true, [], true);
        Assert.True(DiscoveryStateMachine.Apply(discovery, hub, DiscoveryOperations.Enroll, new() { ["machineId"] = "m1", ["machineName"] = "PC A", ["credentialHash"] = "ABCDEF0123456789" }, Now).Ok);
        JsonObject Chat(string thread, string provider, bool wakeable, bool waiter) => new()
        {
            ["provider"] = provider, ["threadId"] = thread, ["relaySessionId"] = "rs-" + thread, ["repositoryId"] = "repo", ["label"] = "chat " + thread, ["wakeable"] = wakeable, ["waiterRunning"] = waiter
        };
        var chats = new JsonArray(Chat("cx", "codex", true, false), Chat("cx-idle", "codex", false, true), Chat("cl", "Claude", false, true), Chat("cl-idle", "claude", true, false), Chat("u", "unknown", true, true));
        Assert.True(DiscoveryStateMachine.Apply(discovery, hub, DiscoveryOperations.Announce, new() { ["machineId"] = "m1", ["machineName"] = "PC A", ["chats"] = chats }, Now).Ok);
        var at = Now.AddSeconds(readOffsetSeconds);

        var directory = DiscoveryStateMachine.Apply(discovery, hub, DiscoveryOperations.Directory, new() { ["repositories"] = new JsonArray("repo") }, at);
        var listed = JsonNode.Parse(directory.DataJson)!["items"]!.AsArray().ToDictionary(item => item!["relaySessionId"]!.GetValue<string>(), item => item!["reachable"]!.GetValue<bool>());

        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-read-models-" + Guid.NewGuid().ToString("N"), "relay.db"));
        await new SqliteGrainStorage(store).WriteStateAsync("discovery", GrainId.Create("cocern.discovery", "machines"), new GrainState<StateEnvelope<CoordinationDocument>>(discovery));
        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(at, "incarnation"));
        var shown = observation.Sessions.ToDictionary(session => session.SessionId, session => session.Reachable);

        Assert.Equal(listed.OrderBy(x => x.Key), shown.OrderBy(x => x.Key));
        var live = readOffsetSeconds is >= -90 and <= 90;
        Assert.Equal(live, Assert.Single(observation.Machines).Reachable);
        Assert.Equal(new Dictionary<string, bool> { ["rs-cx"] = live, ["rs-cx-idle"] = false, ["rs-cl"] = live, ["rs-cl-idle"] = false, ["rs-u"] = false }.OrderBy(x => x.Key), listed.OrderBy(x => x.Key));
    }
}

using System.Text.Json.Nodes;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A closed session (idle sweep, close_sessions or a full directory) keeps its stored record, marked expiredAt, so its chat is told it
/// expired. The dashboard must not count or list it, or "Close idle sessions" would appear to do nothing.
/// </summary>
public sealed class DashboardExpiredSessionTests
{
    [Fact]
    public async Task Expired_sessions_are_neither_counted_nor_listed()
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-expired-sessions-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var now = DateTimeOffset.UtcNow;
        await WriteSession(store, "live", now, expiredAt: null);
        await WriteSession(store, "closed", now.AddHours(-30), expiredAt: now);
        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation"));
        Assert.Equal("live", Assert.Single(observation.Sessions).SessionId);
        Assert.Equal(1, observation.SessionCount);
        // A closed session is not a sign that the observation was cut short.
        Assert.False(observation.SessionsTruncated);
    }

    /// <summary>
    /// A joined PC's node keeps the chats it last announced until it announces again, and a PC that is off never does. A chat whose session
    /// was closed must leave the list all the same, while an announced chat with no stored record yet keeps its place.
    /// </summary>
    [Fact]
    public async Task An_announced_chat_whose_session_was_closed_is_not_listed()
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-expired-sessions-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var now = DateTimeOffset.UtcNow;
        await WriteSession(store, "live", now, expiredAt: null);
        await WriteSession(store, "closed", now.AddHours(-30), expiredAt: now);
        await WriteDiscovery(store, now, Chat("live", "Live chat"), Chat("closed", "Closed chat"), Chat("unstored", "Announced only"));

        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation"));

        Assert.Equal(["live", "unstored"], observation.Sessions.Select(session => session.SessionId).Order(StringComparer.Ordinal));
        Assert.False(observation.SessionsTruncated);
        Assert.Equal("PC A", Assert.Single(observation.Sessions, session => session.SessionId == "live").MachineName);
    }

    /// <summary>Closed records are kept, so a long-running hub has many; they must never use up the bounded read before a session still open.</summary>
    [Fact]
    public async Task Many_closed_sessions_never_crowd_an_open_one_out_of_the_list()
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-expired-sessions-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 205; index++) await WriteSession(store, "a-closed-" + index.ToString("D3"), now.AddHours(-30), expiredAt: now);
        await WriteSession(store, "z-open", now, expiredAt: null);

        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation"));

        Assert.Equal("z-open", Assert.Single(observation.Sessions).SessionId);
        Assert.Equal(1, observation.SessionCount);
        Assert.False(observation.SessionsTruncated);
    }

    /// <summary>
    /// A session's binding is the client's own and may use any key, expiredAt included. Only the record's own top-level expiredAt closes a
    /// session, so a binding that names it must neither hide an open session from the list nor drop it from the count, whether its chat is
    /// announced or not; and the count and the list always agree.
    /// </summary>
    [Fact]
    public async Task A_binding_that_names_expiredAt_does_not_hide_an_open_session()
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-expired-sessions-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var now = DateTimeOffset.UtcNow;
        var binding = new JsonObject { ["expiredAt"] = now.AddDays(-1).ToString("O"), ["tools"] = new JsonArray("check_inbox") };
        await WriteSession(store, "bound-stored", now, expiredAt: null, binding);
        await WriteSession(store, "bound-announced", now, expiredAt: null, binding);
        await WriteSession(store, "closed", now.AddHours(-30), expiredAt: now);
        await WriteSession(store, "closed-announced", now.AddHours(-30), expiredAt: now);
        await WriteDiscovery(store, now, Chat("bound-announced", "Announced chat"), Chat("closed-announced", "Closed chat"));

        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation"));

        Assert.Equal(["bound-announced", "bound-stored"], observation.Sessions.Select(session => session.SessionId).Order(StringComparer.Ordinal));
        Assert.Equal(observation.Sessions.Length, observation.SessionCount);
        Assert.False(observation.SessionsTruncated);
    }

    /// <summary>A rename through session_bind changes the stored label at once; the node's announced copy can lag behind it, so the stored one is shown.</summary>
    [Fact]
    public async Task The_stored_label_wins_over_a_lagging_announced_one()
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-expired-sessions-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var now = DateTimeOffset.UtcNow;
        await WriteSession(store, "renamed", now, expiredAt: null);
        await WriteDiscovery(store, now, Chat("renamed", "Old name"));

        var session = Assert.Single(Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation")).Sessions);

        Assert.Equal("chat renamed", session.Label);
        Assert.Equal("PC A", session.MachineName);
    }

    private static JsonObject Chat(string id, string label) => new()
    {
        ["relaySessionId"] = id, ["repositoryId"] = "repo", ["provider"] = "claude", ["threadId"] = Guid.NewGuid().ToString(), ["label"] = label, ["waiterRunning"] = true
    };

    private static Task WriteDiscovery(SqliteStore store, DateTimeOffset seen, params JsonObject[] chats)
    {
        var document = new JsonObject
        {
            ["machines"] = new JsonObject { ["pc-a"] = new JsonObject { ["machineName"] = "PC A", ["lastSeen"] = seen.ToString("O"), ["chats"] = new JsonArray(chats.Select(chat => (JsonNode)chat).ToArray()) } }
        };
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new() { Value = new() { Json = document.ToJsonString() } });
        return new SqliteGrainStorage(store).WriteStateAsync("discovery", GrainId.Create("cocern.discovery", "machines"), state);
    }

    private static Task WriteSession(SqliteStore store, string id, DateTimeOffset reported, DateTimeOffset? expiredAt, JsonObject? binding = null)
    {
        var document = new JsonObject
        {
            ["sessionId"] = id, ["repositoryId"] = "repo", ["label"] = "chat " + id,
            ["observedAt"] = reported.ToString("O"), ["freshUntil"] = reported.AddMinutes(1).ToString("O"), ["binding"] = binding?.DeepClone()
        };
        if (expiredAt is { } expired) document["expiredAt"] = expired.ToString("O");
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new() { Value = new() { Json = document.ToJsonString() } });
        return new SqliteGrainStorage(store).WriteStateAsync("session", GrainId.Create("cocern.session", id), state);
    }
}

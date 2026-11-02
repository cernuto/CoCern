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
    }

    private static Task WriteSession(SqliteStore store, string id, DateTimeOffset reported, DateTimeOffset? expiredAt)
    {
        var document = new JsonObject
        {
            ["sessionId"] = id, ["repositoryId"] = "repo", ["label"] = "chat " + id,
            ["observedAt"] = reported.ToString("O"), ["freshUntil"] = reported.AddMinutes(1).ToString("O")
        };
        if (expiredAt is { } expired) document["expiredAt"] = expired.ToString("O");
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new() { Value = new() { Json = document.ToJsonString() } });
        return new SqliteGrainStorage(store).WriteStateAsync("session", GrainId.Create("cocern.session", id), state);
    }
}

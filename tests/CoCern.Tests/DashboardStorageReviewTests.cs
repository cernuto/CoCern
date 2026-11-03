using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans;
using Orleans.Runtime;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardStorageReviewTests
{
    [Fact]
    public async Task Announced_sessions_are_retained_when_old_persisted_sessions_exceed_the_limit()
    {
        using var fixture = new ObservationFixture();
        for (var index = 0; index < 205; index++)
            await fixture.Session("a-old-" + index.ToString("D3"), "repo-old");
        await fixture.Session("z-live", "repo-live", unread: 1);
        await fixture.Discovery(Machine("pc-live", fixture.Now, Chat("z-live", "repo-live")));

        var observation = await fixture.Observe();
        var session = Assert.Single(observation.Sessions, item => item.SessionId == "z-live");
        Assert.True(session.IsMapped);
        Assert.True(session.Reachable);
        Assert.Equal(1, session.UnreadMessages);
        Assert.InRange(observation.Sessions.Length, 1, 200);
        Assert.Equal(206, observation.SessionCount);
        Assert.True(observation.SessionsTruncated);
    }

    [Fact]
    public async Task Malformed_discovery_keeps_the_truncation_warning()
    {
        using var fixture = new ObservationFixture();
        await fixture.DiscoveryJson("{");
        var observation = await fixture.Observe();
        Assert.True(observation.SessionsTruncated);
        Assert.Contains("Some discovery metadata could not be observed.", observation.Warnings);
    }

    [Fact]
    public async Task Oversized_discovery_keeps_the_truncation_warning()
    {
        using var fixture = new ObservationFixture();
        await fixture.DiscoveryJson(new JsonObject { ["padding"] = new string('x', 1024 * 1024) }.ToJsonString());
        var observation = await fixture.Observe();
        Assert.True(observation.SessionsTruncated);
        Assert.Contains("Discovery metadata exceeded the bounded observation size.", observation.Warnings);
    }

    [Fact]
    public async Task Projection_removes_format_characters_from_labels_and_identifiers()
    {
        using var fixture = new ObservationFixture();
        await fixture.Session("session\u202E-a", "repo\u200E-a", label: "A\u202E\u0001\U000E0001B");
        var observation = await fixture.Observe();
        var session = Assert.Single(observation.Sessions);
        Assert.Equal("session-a", session.SessionId);
        Assert.Equal("repo-a", session.RepositoryId);
        Assert.Equal("AB", session.Label);
        Assert.DoesNotContain("\u202E", JsonSerializer.Serialize(observation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_announcements_across_repositories_choose_live_then_newest_deterministically()
    {
        using var fixture = new ObservationFixture();
        await fixture.Discovery(
            Machine("pc-old", fixture.Now.AddMinutes(-10), Chat("shared", "repo-old")),
            Machine("pc-live-old", fixture.Now.AddSeconds(-10), Chat("shared", "repo-a")),
            Machine("pc-live-new", fixture.Now, Chat("shared", "repo-b")));
        var first = await fixture.Observe();
        var session = Assert.Single(first.Sessions);
        Assert.Equal("repo-b", session.RepositoryId);
        Assert.Equal("pc-live-new", session.MachineId);
        Assert.True(session.Reachable);

        await fixture.Discovery(
            Machine("pc-live-new", fixture.Now, Chat("shared", "repo-b")),
            Machine("pc-live-old", fixture.Now.AddSeconds(-10), Chat("shared", "repo-a")),
            Machine("pc-old", fixture.Now.AddMinutes(-10), Chat("shared", "repo-old")));
        var second = await fixture.Observe();
        Assert.Equal(session, Assert.Single(second.Sessions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Previous_or_missing_hub_incarnation_is_stale_without_changing_reported_dates(bool missing)
    {
        using var fixture = new ObservationFixture();
        var reported = fixture.Now.AddSeconds(-1);
        var freshUntil = fixture.Now.AddMinutes(1);
        await fixture.Session("previous", "repo", incarnation: missing ? null : "previous-incarnation", reported: reported, freshUntil: freshUntil);
        var session = Assert.Single((await fixture.Observe()).Sessions);
        Assert.True(session.IncarnationMismatch);
        Assert.Equal(reported, session.ReportedAt);
        Assert.Equal(freshUntil, session.FreshUntil);
    }

    [Fact]
    public async Task Application_written_session_is_fresh_for_its_current_incarnation()
    {
        await using var host = new RelayHostFixture(disableDispatcher: true);
        var application = host.Services.GetRequiredService<RelayApplication>();
        var store = host.Services.GetRequiredService<SqliteStore>();
        var caller = new RelayCaller("dashboard-review", true, ["repo"]);
        var opened = await application.ExecuteAsync(caller, "session_open", JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = Guid.NewGuid().ToString("N") }));
        Assert.True(opened.Ok, opened.Code);
        using var openedDocument = JsonDocument.Parse(opened.DataJson);
        var bound = await application.ExecuteAsync(caller, "session_bind", JsonSerializer.SerializeToElement(new
        {
            repositoryId = "repo", requestId = Guid.NewGuid().ToString("N"),
            sessionId = openedDocument.RootElement.GetProperty("sessionId").GetString(),
            sessionToken = openedDocument.RootElement.GetProperty("sessionToken").GetString(),
            expectedVersion = 0, binding = new { }, availability = "active", freshnessSeconds = 300
        }));
        Assert.True(bound.Ok, bound.Code);

        var now = DateTimeOffset.UtcNow;
        var session = Assert.Single((await store.ObserveAsync(now, application.IncarnationId)).Sessions);
        Assert.True(session.FreshUntil > now);
        Assert.False(session.IncarnationMismatch);
        Assert.NotNull(session.ReportedAt);
        Assert.NotNull(session.FreshUntil);

        var restartedApplication = new RelayApplication(host.Services.GetRequiredService<IGrainFactory>());
        Assert.NotEqual(application.IncarnationId, restartedApplication.IncarnationId);
        var restartedSession = Assert.Single((await store.ObserveAsync(now, restartedApplication.IncarnationId)).Sessions);
        Assert.Equal(session.SessionId, restartedSession.SessionId);
        Assert.True(restartedSession.IncarnationMismatch);
        Assert.True(restartedSession.FreshUntil > now);
        Assert.Equal(session.ReportedAt, restartedSession.ReportedAt);
        Assert.Equal(session.FreshUntil, restartedSession.FreshUntil);
    }

    [Fact]
    public async Task Persisted_and_announced_duplicates_have_one_public_session_identity()
    {
        using var fixture = new ObservationFixture();
        await fixture.Session("shared", "repo-current");
        await fixture.Session("legacy-key", "repo-old", publicId: "shared");
        await fixture.Discovery(
            Machine("pc-a", fixture.Now, Chat("shared", "repo-current")),
            Machine("pc-b", fixture.Now.AddSeconds(-1), Chat("shared", "repo-other")));
        var observation = await fixture.Observe();
        Assert.Equal("repo-current", Assert.Single(observation.Sessions).RepositoryId);
        Assert.Equal(observation.Sessions.Length, observation.Sessions.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count());
    }

    private static JsonObject Machine(string id, DateTimeOffset seen, params JsonObject[] chats) => new()
    {
        ["id"] = id,
        ["machineName"] = id,
        ["lastSeen"] = seen.ToString("O"),
        ["chats"] = new JsonArray(chats.Select(chat => (JsonNode)chat).ToArray())
    };

    private static JsonObject Chat(string id, string repository) => new()
    {
        ["relaySessionId"] = id, ["repositoryId"] = repository, ["provider"] = "codex", ["wakeable"] = true
    };

    private sealed class ObservationFixture : IDisposable
    {
        public SqliteStore Store { get; } = new(Path.Combine(Path.GetTempPath(), "cocern-dashboard-review-" + Guid.NewGuid().ToString("N"), "relay.db"));
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        private readonly Dictionary<string, GrainState<StateEnvelope<CoordinationDocument>>> _states = new(StringComparer.Ordinal);

        public Task Session(string grainKey, string repository, string? label = null, int unread = 0, string? incarnation = null, DateTimeOffset? reported = null, DateTimeOffset? freshUntil = null, string? publicId = null)
        {
            var inbox = new JsonObject();
            for (var index = 0; index < unread; index++) inbox[index.ToString()] = new JsonObject();
            return Write("session", "cocern.session", grainKey, new JsonObject
            {
                ["sessionId"] = publicId ?? grainKey, ["repositoryId"] = repository, ["label"] = label ?? grainKey,
                ["observedAt"] = (reported ?? Now).ToString("O"), ["freshUntil"] = (freshUntil ?? Now.AddMinutes(1)).ToString("O"),
                ["hubIncarnation"] = incarnation, ["inbox"] = inbox
            }.ToJsonString());
        }

        public Task Discovery(params JsonObject[] machines)
        {
            var inventory = new JsonObject();
            foreach (var machine in machines)
            {
                var id = machine["id"]!.GetValue<string>();
                machine.Remove("id");
                inventory[id] = machine;
            }
            return DiscoveryJson(new JsonObject { ["machines"] = inventory }.ToJsonString());
        }

        public Task DiscoveryJson(string json) => Write("discovery", "cocern.discovery", "machines", json);

        private async Task Write(string name, string type, string key, string json)
        {
            var stateKey = name + "/" + key;
            if (!_states.TryGetValue(stateKey, out var state))
            {
                state = new GrainState<StateEnvelope<CoordinationDocument>>(new());
                _states.Add(stateKey, state);
            }
            state.State = new() { Value = new() { Json = json } };
            await new SqliteGrainStorage(Store).WriteStateAsync(name, GrainId.Create(type, key), state);
        }

        // A busy or expired sample is explicitly unavailable; retry that result, not a failed content assertion.
        public Task<RelayObservation> Observe() => Store.ObserveAsync(Now, "observing-application", complete: false);
        public void Dispose() => Store.Dispose();
    }
}




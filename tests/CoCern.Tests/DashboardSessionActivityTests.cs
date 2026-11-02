using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardSessionActivityTests
{
    [Fact]
    public void New_session_open_time_is_hub_owned_and_survives_later_binding_and_activity()
    {
        var opened = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
        var state = new StateEnvelope<CoordinationDocument>();
        var caller = new RelayCaller("dashboard-open-time", true, ["repo"]) with { IsInternal = true };
        var initialize = new JsonObject
        {
            ["repositoryId"] = "repo", ["sessionId"] = "session", ["sessionToken"] = "secret", ["principalId"] = caller.PrincipalId,
            ["openedAt"] = opened.AddDays(-1).ToString("O")
        };
        Assert.True(CoordinationStateMachine.Apply(state, AggregateKinds.Session, "session", caller, Operations.Open, initialize, opened).Ok);
        AssertOpened();
        Assert.True(CoordinationStateMachine.Apply(state, AggregateKinds.Session, "session", caller, "session_bind", new JsonObject
        {
            ["repositoryId"] = "repo", ["sessionToken"] = "secret", ["requestId"] = "bind-later", ["expectedVersion"] = 0,
            ["binding"] = new JsonObject(), ["openedAt"] = opened.AddDays(1).ToString("O")
        }, opened.AddHours(1)).Ok);
        Assert.True(CoordinationStateMachine.Apply(state, AggregateKinds.Session, "session", caller, Operations.Verify, new JsonObject
        {
            ["repositoryId"] = "repo", ["sessionToken"] = "secret"
        }, opened.AddHours(2)).Ok);
        Assert.True(CoordinationStateMachine.Apply(state, AggregateKinds.Session, "session", caller, Operations.Open, initialize, opened.AddHours(3)).Ok);
        AssertOpened();

        void AssertOpened()
        {
            var document = JsonSerializer.Deserialize<JsonElement>(state.Value.Json);
            Assert.True(document.TryGetProperty("openedAt", out var stamp));
            Assert.Equal(opened, stamp.GetDateTimeOffset());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_open_time_projects_recorded_dates_and_leaves_legacy_unknown(bool known)
    {
        using var store = new SqliteStore(DatabasePath());
        var now = DateTimeOffset.UtcNow;
        var opened = known ? now.AddHours(-1) : (DateTimeOffset?)null;
        await WriteSession(store, now, openedAt: opened);
        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation"));
        var session = JsonSerializer.SerializeToElement(Assert.Single(observation.Sessions));
        Assert.True(session.TryGetProperty("OpenedAt", out var stamp));
        if (opened is { } expected) Assert.Equal(expected, stamp.GetDateTimeOffset());
        else Assert.Equal(JsonValueKind.Null, stamp.ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(3L)]
    public async Task Session_binding_version_distinguishes_unbound_bound_and_unknown(long? bindingVersion)
    {
        using var store = new SqliteStore(DatabasePath());
        var now = DateTimeOffset.UtcNow;
        await WriteSession(store, now, bindingVersion);
        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation"));
        var session = JsonSerializer.SerializeToElement(Assert.Single(observation.Sessions));
        Assert.True(session.TryGetProperty("BindingVersion", out var version));
        if (bindingVersion is { } expected) Assert.Equal(expected, version.GetInt64());
        else Assert.Equal(JsonValueKind.Null, version.ValueKind);
    }

    [Fact]
    public async Task Last_activity_uses_successful_session_calls_without_refreshing_a_stale_binding()
    {
        var path = DatabasePath();
        var now = DateTimeOffset.UtcNow;
        var reported = now.AddHours(-2);
        var active = now.AddSeconds(-10);
        using (var store = new SqliteStore(path))
        {
            await WriteSession(store, reported);
            store.Record(Call(active) with { Tool = "get_status" });
            store.Record(Call(active.AddSeconds(-1)));
            store.Record(Call(now.AddSeconds(-1)) with { Ok = false });
            store.Record(Call(now.AddSeconds(-1)) with { Kind = ActivityEvent.Wake });
            store.Record(Call(now.AddSeconds(-1)) with { RepositoryId = "other-repo" });
            store.Record(Call(now.AddSeconds(-1)) with { SessionId = "other-session" });
            store.Record(Call(now.AddMinutes(1)));
            await store.FlushActivityAsync();

            await AssertActivity(store);
        }
        // Raw call history remains useful after a hub restart, without treating an old binding as current.
        using (var reopened = new SqliteStore(path)) await AssertActivity(reopened);

        async Task AssertActivity(SqliteStore store)
        {
            var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "current-incarnation"));
            var session = Assert.Single(observation.Sessions);
            Assert.Equal(active, session.LastActivityAt);
            Assert.Equal(reported, session.ReportedAt);
            Assert.Equal(reported.AddMinutes(1), session.FreshUntil);
            Assert.True(session.IncarnationMismatch);
        }
    }

    [Fact]
    public async Task Binding_reports_and_unsuccessful_calls_leave_unknown_activity_unreported()
    {
        using var store = new SqliteStore(DatabasePath());
        var now = DateTimeOffset.UtcNow;
        await WriteSession(store, now);
        store.Record(Call(now.AddSeconds(-1)) with { Ok = false });
        store.Record(Call(now.AddSeconds(-1)) with { Kind = ActivityEvent.Wake });
        await store.FlushActivityAsync();

        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, "previous-incarnation"));
        Assert.Null(Assert.Single(observation.Sessions).LastActivityAt);
    }

    [Fact]
    public async Task Successful_application_reads_report_recent_activity_without_an_ide_binding()
    {
        await using var host = new RelayHostFixture(disableDispatcher: true);
        var application = host.Services.GetRequiredService<RelayApplication>();
        var store = host.Services.GetRequiredService<SqliteStore>();
        var caller = new RelayCaller("dashboard-activity", true, ["repo"]);
        var opened = await application.ExecuteAsync(caller, "session_open", JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "activity-open" }));
        Assert.True(opened.Ok, opened.Code);
        using var document = JsonDocument.Parse(opened.DataJson);
        var initial = Assert.Single(Assert.IsType<RelayObservation>(await store.ReadObservationAsync(DateTimeOffset.UtcNow, application.IncarnationId)).Sessions);
        var beforeRead = DateTimeOffset.UtcNow;
        var status = await application.ExecuteAsync(caller, "check_inbox", JsonSerializer.SerializeToElement(new
        {
            repositoryId = "repo",
            sessionId = document.RootElement.GetProperty("sessionId").GetString(),
            sessionToken = document.RootElement.GetProperty("sessionToken").GetString(),
            attention = false
        }));
        Assert.True(status.Ok, status.Code);
        await store.FlushActivityAsync();
        var now = DateTimeOffset.UtcNow;
        var observation = Assert.IsType<RelayObservation>(await store.ReadObservationAsync(now, application.IncarnationId));
        var session = Assert.Single(observation.Sessions);
        Assert.NotNull(session.LastActivityAt);
        Assert.InRange(session.LastActivityAt.Value, beforeRead, now);
        Assert.Equal(initial.ReportedAt, session.ReportedAt);
        Assert.Equal(initial.FreshUntil, session.FreshUntil);
        Assert.Equal(initial.IncarnationMismatch, session.IncarnationMismatch);
    }

    private static string DatabasePath() => Path.Combine(Path.GetTempPath(), "cocern-session-activity-" + Guid.NewGuid().ToString("N"), "relay.db");

    private static ActivityEvent Call(DateTimeOffset at) => new()
    {
        Timestamp = at, Kind = ActivityEvent.Call, RepositoryId = "repo", SessionId = "session", Tool = "check_inbox", Ok = true, Code = "Ok"
    };

    private static Task WriteSession(SqliteStore store, DateTimeOffset reported, long? bindingVersion = null, DateTimeOffset? openedAt = null)
    {
        var document = new JsonObject
        {
            ["sessionId"] = "session", ["repositoryId"] = "repo", ["hubIncarnation"] = "previous-incarnation",
            ["observedAt"] = reported.ToString("O"), ["freshUntil"] = reported.AddMinutes(1).ToString("O")
        };
        if (bindingVersion is { } version) document["bindingVersion"] = version;
        if (openedAt is { } opened) document["openedAt"] = opened.ToString("O");
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new()
        {
            Value = new() { Json = document.ToJsonString() }
        });
        return new SqliteGrainStorage(store).WriteStateAsync("session", GrainId.Create("cocern.session", "session"), state);
    }
}

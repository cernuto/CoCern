using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Hosting;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using CoCern.Storage;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Sessions are closed before the directory fills: a periodic sweep closes those idle longer than <c>Relay:Sessions:IdleHours</c>, and an
/// administrator can close many at once with <c>control_request close_sessions</c>. Closing is the full-directory cleanup's expiry: the
/// session leaves the directory, its chat gets <c>SessionUnknown</c> with <c>expired</c>, and one that answers for an unresolved action stays.
/// </summary>
public sealed class SessionCleanupTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
    private static readonly RelayCaller Admin = new("admin", true, ["repo"]);
    private static readonly RelayCaller Worker = new("alice", false, ["repo"]);
    private static readonly RelayCaller Hub = new("relay-sweep", true, [], true);

    [Fact]
    public void The_sweep_closes_sessions_idle_past_the_setting_in_every_repository_and_keeps_recent_and_busy_ones()
    {
        // A directory far from full: two long idle sessions (one in another repository), one seen recently, one reopened in its chat an
        // hour ago, and one idle as long that still answers for an unresolved action.
        var active = Record("active", TimeSpan.FromHours(30)); active["activeAt"] = Now.AddHours(-1).ToString("O");
        var control = Directory(Record("idle", TimeSpan.FromHours(30)), Record("idle-other", TimeSpan.FromHours(30), repository: "other"),
            Record("recent", TimeSpan.FromHours(2)), active, Record("busy", TimeSpan.FromHours(30)));
        Owns(control, "busy");
        control.Effects.Clear();

        var swept = Data(Sweep(control, 24));
        Assert.Equal((2, 1), (swept["closed"]!.GetValue<int>(), swept["kept"]!.GetValue<int>()));
        Assert.Equal(["active", "busy", "recent"], Sessions(control).Select(entry => entry.Key).Order(StringComparer.Ordinal));
        // Each closed session is told, under its own repository, exactly as the full-directory cleanup tells it.
        var expiries = Expiries(control);
        Assert.Equal(["idle", "idle-other"], expiries.Select(effect => effect.TargetKey).Order(StringComparer.Ordinal));
        Assert.All(expiries, effect =>
        {
            Assert.Equal(AggregateKinds.Session, effect.TargetKind);
            var payload = Payload(effect);
            var repository = effect.TargetKey == "idle-other" ? "other" : "repo";
            Assert.Equal((repository, effect.TargetKey), (payload["repositoryId"]!.GetValue<string>(), payload["sessionId"]!.GetValue<string>()));
            var caller = CoordinationStateMachine.TakeEffectCaller(payload);
            Assert.Equal([repository], caller.Repositories);
            Assert.False(caller.IsAdministrator);
        });

        // A second sweep finds nothing new and still keeps the busy session.
        control.Effects.Clear();
        var again = Data(Sweep(control, 24));
        Assert.Equal((0, 1), (again["closed"]!.GetValue<int>(), again["kept"]!.GetValue<int>()));
        Assert.Empty(Expiries(control));
        Assert.Equal(3, Sessions(control).Count);
    }

    [Fact]
    public void The_sweep_judges_idleness_by_the_hours_it_is_given()
    {
        var control = Directory(Record("three", TimeSpan.FromHours(3)), Record("thirty", TimeSpan.FromHours(30)), Record("fifty", TimeSpan.FromHours(50)));
        Assert.Equal(1, Data(Sweep(control, 48))["closed"]!.GetValue<int>());
        Assert.Equal(["thirty", "three"], Sessions(control).Select(entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.Equal(2, Data(Sweep(control, 2))["closed"]!.GetValue<int>());
        Assert.Empty(Sessions(control));
        Assert.Equal(["fifty", "thirty", "three"], Expiries(control).Select(effect => effect.TargetKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_the_hub_itself_may_sweep()
    {
        var control = Directory(Record("idle", TimeSpan.FromHours(30)));
        Assert.Equal(RelayErrorCodes.InternalOnly, Call(control, Operations.SweepSessions, new() { ["idleHours"] = 24 }, Admin).Code);
        Assert.Equal(RelayErrorCodes.InternalOnly, Call(control, Operations.SweepSessions, new() { ["idleHours"] = 24 }, Worker).Code);
        Assert.Single(Sessions(control));
        Assert.True(Sweep(control, 24).Ok);
        Assert.Empty(Sessions(control));
    }

    [Fact]
    public void Close_sessions_is_for_administrators_and_needs_a_valid_filter()
    {
        var control = Directory(Record("s", TimeSpan.FromHours(5), "smoke"));
        Assert.Equal(RelayErrorCodes.AdministratorRequired, Close(control, new() { ["labelPrefix"] = "smoke" }, Worker).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Close(control, new()).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Close(control, new() { ["idleMinutes"] = 59 }).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Close(control, new() { ["idleMinutes"] = 30, ["labelPrefix"] = "smoke" }).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Close(control, new() { ["labelPrefix"] = "" }).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Close(control, new() { ["idleMinutes"] = "an hour" }).Code);
        Assert.Single(Sessions(control));
        Assert.Empty(control.Effects);
        Assert.Equal(1, Data(Close(control, new() { ["idleMinutes"] = 60 }))["closed"]!.GetValue<int>());
    }

    [Fact]
    public void An_exact_session_target_closes_only_that_session_even_with_duplicate_labels()
    {
        var control = Directory(Record("one", TimeSpan.Zero, "Same label"), Record("two", TimeSpan.Zero, "Same label"), Record("foreign", TimeSpan.Zero, "Same label", "other"));
        var reply = Close(control, new() { ["targetSessionId"] = "one" });
        Assert.True(reply.Ok, reply.DataJson);
        Assert.Equal(["one"], Data(reply)["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Equal(["foreign", "two"], Sessions(control).Select(entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.Equal("one", Assert.Single(Expiries(control)).TargetKey);
        Assert.Equal(0, Data(Close(control, new() { ["targetSessionId"] = "foreign", ["force"] = true }))["closed"]!.GetValue<int>());
        Assert.Equal(0, Data(Close(control, new() { ["targetSessionId"] = "two", ["sessionId"] = "two", ["force"] = true }))["closed"]!.GetValue<int>());
        Assert.Equal(RelayErrorCodes.AdministratorRequired, Close(control, new() { ["targetSessionId"] = "two", ["force"] = true }, Worker).Code);
        Assert.True(Sessions(control).ContainsKey("two"));
    }

    [Fact]
    public void Only_an_explicit_force_close_of_one_session_overrides_its_unresolved_work()
    {
        var control = Directory(Record("busy", TimeSpan.Zero), Record("peer", TimeSpan.Zero));
        Owns(control, "busy");
        control.Effects.Clear();
        var kept = Close(control, new() { ["targetSessionId"] = "busy" });
        Assert.True(kept.Ok, kept.DataJson);
        Assert.Equal((0, 1), (Data(kept)["closed"]!.GetValue<int>(), Data(kept)["kept"]!.GetValue<int>()));
        Assert.Empty(Expiries(control));
        var forced = Close(control, new() { ["targetSessionId"] = "busy", ["force"] = true });
        Assert.True(forced.Ok, forced.DataJson);
        Assert.Equal((1, 0), (Data(forced)["closed"]!.GetValue<int>(), Data(forced)["kept"]!.GetValue<int>()));
        var expiry = JsonNode.Parse(Assert.Single(Expiries(control)).PayloadJson)!;
        Assert.True(expiry["force"]!.GetValue<bool>());
        Assert.Null(expiry["idle"]);
        Assert.Equal(["peer"], Sessions(control).Select(entry => entry.Key));
    }

    [Theory]
    [InlineData("{\"force\":true}")]
    [InlineData("{\"labelPrefix\":\"smoke\",\"force\":true}")]
    [InlineData("{\"targetSessionId\":\"\"}")]
    [InlineData("{\"targetSessionId\":12}")]
    [InlineData("{\"targetSessionId\":\"s\",\"force\":\"true\"}")]
    [InlineData("{\"targetSessionId\":\"s\",\"idleMinutes\":60}")]
    [InlineData("{\"targetSessionId\":\"s\",\"labelPrefix\":\"smoke\"}")]
    public void A_force_close_requires_one_unambiguous_session_target(string json)
    {
        var control = Directory(Record("s", TimeSpan.Zero, "smoke"));
        Assert.Equal(RelayErrorCodes.InvalidArguments, Close(control, JsonNode.Parse(json)!.AsObject()).Code);
        Assert.Single(Sessions(control));
        Assert.Empty(control.Effects);
    }

    [Fact]
    public void Close_sessions_by_label_prefix_matches_the_start_of_the_label_ignoring_case()
    {
        var control = Directory(Record("a", TimeSpan.FromHours(3), "Smoke test 1"), Record("b", TimeSpan.FromHours(3), "SMOKE-2"), Record("c", TimeSpan.FromHours(3), "Run a smoke test"),
            Record("d", TimeSpan.FromHours(3)), Record("e", TimeSpan.FromHours(3), "smoke elsewhere", "other"), Record("busy", TimeSpan.FromHours(3), "smoke busy"),
            Record("now", TimeSpan.FromMinutes(1), "smoke now"));
        Owns(control, "busy");
        control.Effects.Clear();

        var closed = Data(Close(control, new() { ["labelPrefix"] = "smoke" }));
        Assert.Equal((2, 1, 1), (closed["closed"]!.GetValue<int>(), closed["kept"]!.GetValue<int>(), closed["active"]!.GetValue<int>()));
        Assert.Equal(["a", "b"], closed["ids"]!.AsArray().Select(id => id!.GetValue<string>()).Order(StringComparer.Ordinal));
        // A label that only contains the prefix, an unlabeled session, another repository's, a busy one and one working right now stay.
        Assert.Equal(["busy", "c", "d", "e", "now"], Sessions(control).Select(entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["a", "b"], Expiries(control).Select(effect => effect.TargetKey).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("sessionToken", closed.ToJsonString());
    }

    [Fact]
    public void Close_sessions_by_idle_minutes_closes_sessions_not_seen_for_that_long_and_both_filters_must_match()
    {
        var control = Directory(Record("old", TimeSpan.FromHours(3)), Record("recent", TimeSpan.FromMinutes(30)), Record("busy", TimeSpan.FromHours(5)),
            Record("other-repo", TimeSpan.FromHours(5), repository: "other"));
        Owns(control, "busy");
        control.Effects.Clear();
        var closed = Data(Close(control, new() { ["idleMinutes"] = 60 }));
        Assert.Equal((1, 1), (closed["closed"]!.GetValue<int>(), closed["kept"]!.GetValue<int>()));
        Assert.Equal(["old"], closed["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Equal(["busy", "other-repo", "recent"], Sessions(control).Select(entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.Equal("old", Assert.Single(Expiries(control)).TargetKey);

        var both = Directory(Record("p1", TimeSpan.FromHours(3), "smoke 1"), Record("p2", TimeSpan.FromMinutes(10), "smoke 2"), Record("p3", TimeSpan.FromHours(3), "chat"));
        var matched = Data(Close(both, new() { ["idleMinutes"] = 60, ["labelPrefix"] = "Smoke" }));
        Assert.Equal(["p1"], matched["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Equal(["p2", "p3"], Sessions(both).Select(entry => entry.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Close_sessions_never_closes_the_callers_own_session()
    {
        var control = Directory(Record("own", TimeSpan.FromHours(5), "smoke admin"), Record("peer", TimeSpan.FromHours(5), "smoke peer"));
        var closed = Data(Close(control, new() { ["labelPrefix"] = "smoke", ["idleMinutes"] = 60, ["sessionId"] = "own" }));
        Assert.Equal(1, closed["closed"]!.GetValue<int>());
        Assert.Equal(["peer"], closed["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.True(Sessions(control).ContainsKey("own"));
        Assert.DoesNotContain(control.Effects, effect => effect.TargetKey == "own");
    }

    [Fact]
    public void Close_sessions_reports_every_closed_session_but_lists_at_most_fifty_ids()
    {
        var records = Enumerable.Range(0, 60).Select(i => Record("bulk-" + i, TimeSpan.FromHours(2), "bulk " + i)).Append(Record("keep", TimeSpan.FromHours(2), "keep")).ToArray();
        var control = Directory(records);
        var closed = Data(Close(control, new() { ["labelPrefix"] = "bulk" }));
        Assert.Equal((60, 0), (closed["closed"]!.GetValue<int>(), closed["kept"]!.GetValue<int>()));
        var ids = closed["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToArray();
        Assert.Equal(50, ids.Length);
        Assert.Equal(50, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("bulk-", id, StringComparison.Ordinal));
        Assert.Equal(["keep"], Sessions(control).Select(entry => entry.Key));
        Assert.Equal(60, Expiries(control).Select(effect => effect.TargetKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Control_request_advertises_close_sessions()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.ControlRequest, out var control));
        var commands = RelayToolCatalog.InputSchema(control).GetProperty("properties").GetProperty("command").GetProperty("enum").EnumerateArray().Select(value => value.GetString());
        Assert.Contains("close_sessions", commands);
    }

    [Fact]
    public async Task A_closed_session_gets_a_clean_expired_error_and_leaves_the_worker_list()
    {
        using var host = await StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            async Task<RelayReply> Call(RelayCaller caller, string tool, object args) => await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            async Task<(string Id, string Token)> Open(RelayCaller caller, string label)
            {
                var opened = Data(await Call(caller, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-" + label, label }));
                return (opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>());
            }
            var smoke = await Open(Worker, "Smoke test 1");
            var kept = await Open(Worker, "Real work");
            var own = await Open(Admin, "smoke admin");

            var closed = Data(await Call(Admin, RelayTools.ControlRequest, new { repositoryId = "repo", requestId = "close-1", command = "close_sessions", targetSessionId = smoke.Id, sessionId = own.Id, sessionToken = own.Token }));
            Assert.Equal(1, closed["closed"]!.GetValue<int>());
            Assert.Equal([smoke.Id], closed["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
            Assert.True((await factory.GetGrain<IControlGrain>(AggregateKinds.ControlKey).ExecuteAsync(Admin with { IsInternal = true }, Operations.Dispatch, "{}")).Ok);

            var inbox = await Call(Worker, RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = smoke.Id, sessionToken = smoke.Token });
            Assert.Equal(RelayErrorCodes.SessionUnknown, inbox.Code);
            Assert.True(JsonNode.Parse(inbox.DataJson)!["expired"]!.GetValue<bool>());
            Assert.True((await Call(Worker, RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = kept.Id, sessionToken = kept.Token })).Ok);
            Assert.True((await Call(Admin, RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = own.Id, sessionToken = own.Token })).Ok);
            var workers = Data(await Call(Admin, RelayTools.WorkerList, new { repositoryId = "repo" }))["items"]!.AsArray().Select(worker => worker!["sessionId"]!.GetValue<string>()).ToArray();
            Assert.DoesNotContain(smoke.Id, workers);
            Assert.Contains(kept.Id, workers);
            Assert.Contains(own.Id, workers);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task The_sweep_service_closes_a_stored_idle_session_by_its_idle_hours_and_keeps_fresh_sessions()
    {
        // A directory as the hub stored it: one session last seen 30 hours ago. With the hour the directory allows for activity reports,
        // 24 idle hours close it and 48 do not.
        var database = Path.Combine(Path.GetTempPath(), "cocern-sweep-" + Guid.NewGuid().ToString("N"), "relay.db");
        using (var store = new SqliteStore(database))
        {
            // The silo runs on the real clock.
            var directory = new GrainState<StateEnvelope<CoordinationDocument>>(Directory(Record("idle", TimeSpan.FromHours(30), "Old chat", now: DateTimeOffset.UtcNow)));
            await new SqliteGrainStorage(store).WriteStateAsync("control", GrainId.Create("cocern.control", AggregateKinds.ControlKey), directory);
        }
        using var host = await StartAsync(database);
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var opened = Data(await application.ExecuteAsync(Worker, RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1", label = "Chat" })));
            async Task<string[]> Workers() => Data(await application.ExecuteAsync(Admin, RelayTools.WorkerList, JsonSerializer.SerializeToElement(new { repositoryId = "repo" })))["items"]!.AsArray()
                .Select(worker => worker!["sessionId"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
            SessionSweepService Sweeper(int hours) => new(host.Services.GetRequiredService<IGrainFactory>(), new SessionCleanupOptions { IdleHours = hours }, NullLogger<SessionSweepService>.Instance);
            Assert.Equal(new[] { "idle", opened["sessionId"]!.GetValue<string>() }.Order(StringComparer.Ordinal), await Workers());

            var kept = Data(await Sweeper(48).SweepAsync());
            Assert.Equal((0, 0, false), (kept["closed"]!.GetValue<int>(), kept["kept"]!.GetValue<int>(), kept["more"]!.GetValue<bool>()));
            Assert.Contains("idle", await Workers());

            var swept = Data(await Sweeper(24).SweepAsync());
            Assert.Equal((1, 0), (swept["closed"]!.GetValue<int>(), swept["kept"]!.GetValue<int>()));
            Assert.Equal([opened["sessionId"]!.GetValue<string>()], await Workers());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public void A_close_capped_at_its_batch_says_more_remain_and_the_next_one_takes_the_rest()
    {
        var extra = 88;
        var control = Directory(Enumerable.Range(0, RelayLimits.MaxSessionsClosedPerCall + extra).Select(i => Record("idle-" + i, TimeSpan.FromHours(30))).ToArray());
        var first = Data(Close(control, new() { ["idleMinutes"] = 60 }));
        Assert.Equal((RelayLimits.MaxSessionsClosedPerCall, true), (first["closed"]!.GetValue<int>(), first["more"]!.GetValue<bool>()));
        var second = Data(Close(control, new() { ["idleMinutes"] = 60 }));
        Assert.Equal((extra, false), (second["closed"]!.GetValue<int>(), second["more"]!.GetValue<bool>()));
        Assert.Empty(Sessions(control));

        var swept = Directory(Enumerable.Range(0, RelayLimits.MaxSessionsClosedPerCall + 1).Select(i => Record("s-" + i, TimeSpan.FromHours(30))).ToArray());
        Assert.True(Data(Sweep(swept, 24))["more"]!.GetValue<bool>());
        Assert.False(Data(Sweep(swept, 24))["more"]!.GetValue<bool>());
    }

    [Fact]
    public void Idle_hours_default_to_a_day_and_must_be_from_two_to_seven_hundred_twenty()
    {
        Assert.Equal(24, new SessionCleanupOptions().IdleHours);
        Assert.Equal(2, new SessionCleanupOptions { IdleHours = 2 }.Validated().IdleHours);
        Assert.Equal(720, new SessionCleanupOptions { IdleHours = 720 }.Validated().IdleHours);
        foreach (var hours in new[] { 1, 721, 0, -5 })
            Assert.Contains("IdleHours", Assert.Throws<InvalidOperationException>(() => new SessionCleanupOptions { IdleHours = hours }.Validated()).Message);
    }

    [Fact]
    public async Task The_hub_reads_idle_hours_from_its_settings_and_runs_the_sweep()
    {
        await using (var host = new RelayHostFixture())
        {
            Assert.Equal(24, host.Services.GetRequiredService<SessionCleanupOptions>().IdleHours);
            Assert.Single(host.Services.GetServices<IHostedService>().OfType<SessionSweepService>());
        }
        await using (var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Sessions:IdleHours"] = "48" }))
            Assert.Equal(48, host.Services.GetRequiredService<SessionCleanupOptions>().IdleHours);
        foreach (var value in new[] { "1", "721" })
        {
            await using var bad = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Sessions:IdleHours"] = value });
            Assert.Contains("IdleHours", Assert.ThrowsAny<Exception>(() => bad.Services.GetRequiredService<SessionCleanupOptions>()).ToString());
        }
    }

    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string operation, JsonObject args, RelayCaller caller, string repository = "repo")
    {
        args["repositoryId"] = repository;
        return CoordinationStateMachine.Apply(state, AggregateKinds.Control, AggregateKinds.ControlKey, caller, operation, args, Now);
    }

    private static RelayReply Sweep(StateEnvelope<CoordinationDocument> state, int idleHours) => Call(state, Operations.SweepSessions, new() { ["idleHours"] = idleHours }, Hub);

    private static RelayReply Close(StateEnvelope<CoordinationDocument> state, JsonObject args, RelayCaller? caller = null)
    {
        args["command"] = "close_sessions";
        args["requestId"] = Guid.NewGuid().ToString("N");
        return Call(state, RelayTools.ControlRequest, args, caller ?? Admin);
    }

    /// <summary>The session's own report that it answers for an unresolved action, as the session aggregate sends it.</summary>
    private static void Owns(StateEnvelope<CoordinationDocument> state, string id, string repository = "repo") =>
        Assert.True(Call(state, Operations.SessionOwns, new() { ["sessionId"] = id, ["owns"] = true, ["at"] = Now.ToString("O"), ["seq"] = 1 }, Hub, repository).Ok);

    /// <summary>A directory record last seen <paramref name="idle"/> before <paramref name="now"/> (the tests' fixed time unless given).</summary>
    private static JsonObject Record(string id, TimeSpan idle, string label = "", string repository = "repo", DateTimeOffset? now = null)
    {
        var seen = (now ?? Now) - idle;
        return new()
        {
            ["sessionId"] = id, ["principalId"] = "alice", ["repositoryId"] = repository, ["sessionToken"] = "t-" + id, ["bindingVersion"] = 0, ["label"] = label,
            ["observedAt"] = seen.ToString("O"), ["freshUntil"] = seen.ToString("O"), ["availability"] = "unknown"
        };
    }

    private static StateEnvelope<CoordinationDocument> Directory(params JsonObject[] records) => new()
    {
        Value = new() { Json = new JsonObject { ["sessions"] = new JsonObject(records.Select(record => KeyValuePair.Create(record["sessionId"]!.GetValue<string>(), (JsonNode?)record))) }.ToJsonString() }
    };

    private static JsonObject Sessions(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!["sessions"]!.AsObject();
    private static List<DurableEffect> Expiries(StateEnvelope<CoordinationDocument> state) => state.Effects.Where(effect => effect.Operation == Operations.SessionExpire).ToList();
    private static JsonObject Payload(DurableEffect effect) => JsonNode.Parse(effect.PayloadJson)!.AsObject();

    private static JsonObject Data(RelayReply reply)
    {
        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
        return JsonNode.Parse(reply.DataJson)!.AsObject();
    }

    /// <summary>A silo with in-memory storage, or with the hub's SQLite store at <paramref name="database"/>.</summary>
    private static async Task<IHost> StartAsync(string? database = null)
    {
        var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo =>
            {
                silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "session-cleanup-tests", clusterId: Guid.NewGuid().ToString("N"));
                if (database is null) silo.AddMemoryGrainStorage("relay"); else silo.AddCoCernSqlite(database);
            })
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        return host;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

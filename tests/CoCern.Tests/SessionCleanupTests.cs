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
    public void Close_sessions_never_closes_the_callers_own_session()
    {
        var control = Directory(Record("own", TimeSpan.FromHours(5), "smoke admin"), Record("peer", TimeSpan.FromHours(5), "smoke peer"));
        var closed = Data(Close(control, new() { ["labelPrefix"] = "smoke", ["idleMinutes"] = 60, ["sessionId"] = "own" }));
        Assert.Equal(1, closed["closed"]!.GetValue<int>());
        Assert.Equal(["peer"], closed["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.True(Sessions(control).ContainsKey("own"));
        Assert.DoesNotContain(control.Effects, effect => effect.TargetKey == "own");
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

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

// Supplemental proof only: these fixtures change no production code.
public sealed class EventWaitPeerProofTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Peer_arm_capacity_refuses_without_evicting_a_live_snapshot_and_expiry_makes_room()
    {
        var at = DateTimeOffset.UtcNow;
        var hub = new AggregateHub(); var peer = hub.Open("peer", at);
        var owners = new[] { hub.Open("first", at), hub.Open("second", at), hub.Open("third", at) };
        hub.DeliverAll(at);
        var originals = owners.Select(owner => AggregateHub.Data(hub.Send(owner, peer.Id, "original", at))["messageId"]!.GetValue<string>()).ToArray();
        hub.DeliverAll(at);

        RelayReply Register(int ownerIndex, string id, DateTimeOffset now)
        {
            var owner = owners[ownerIndex];
            var request = new EventWaitRequest(id, "peer_reply", originals[ownerIndex], "", now.AddMinutes(10),
                PeerSessionId: peer.Id, ReplyTo: originals[ownerIndex]);
            return hub.Apply("session", owner.Id, EventWaitAggregate.RegisterOperation, new JsonObject
            {
                ["repositoryId"] = "repo", ["sessionId"] = owner.Id, ["sessionToken"] = owner.Token,
                ["requestId"] = "register-" + id, ["request"] = JsonSerializer.SerializeToNode(request, Json)
            }, AggregateHub.Alice with { IsInternal = true }, now);
        }
        for (var owner = 0; owner < 2; owner++)
        {
            for (var index = 0; index < 64; index++) Assert.True(Register(owner, "wait-" + index, at).Ok);
            hub.DeliverAll(at);
        }
        var before = hub.State("session", peer.Id)["eventWaitPeerArms"]!.ToJsonString();
        Assert.Equal(128, hub.State("session", peer.Id)["eventWaitPeerArms"]!.AsObject().Count);
        Assert.True(Register(2, "overflow", at).Ok);
        var overflow = Assert.Single(hub.Queued, effect => effect.Operation == PeerReplyWaits.ArmOperation);
        var denied = hub.Deliver(overflow, at);
        Assert.False(denied.Ok); Assert.Equal("CapacityExceeded", denied.Code);
        Assert.Equal(before, hub.State("session", peer.Id)["eventWaitPeerArms"]!.ToJsonString());

        // Every old arm has actually expired; no live snapshot is sacrificed.
        var later = at.AddMinutes(11);
        Assert.True(Register(2, "after-expiry", later).Ok);
        var next = Assert.Single(hub.Queued, effect => effect.Operation == PeerReplyWaits.ArmOperation);
        Assert.True(hub.Deliver(next, later).Ok);
        Assert.Single(hub.State("session", peer.Id)["eventWaitPeerArms"]!.AsObject());
    }

    [Fact]
    public async Task A_real_Silo_private_read_commits_lazy_expiry_and_its_one_receipt_to_storage()
    {
        var database = Path.Combine(Path.GetTempPath(), "cocern-read-expiry-" + Guid.NewGuid().ToString("N"), "relay.db");
        var registeredAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var hub = new AggregateHub(); var owner = hub.Open("owner", registeredAt); hub.DeliverAll(registeredAt);
        var args = new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = owner.Id, ["sessionToken"] = owner.Token,
            ["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest("read-expiry", "claim_released", "fixture-resource", "1", registeredAt.AddMinutes(1)), Json) };
        Assert.True(hub.Apply("session", owner.Id, EventWaitAggregate.RegisterOperation, args,
            AggregateHub.Alice with { IsInternal = true }, registeredAt).Ok);
        var id = GrainId.Create("cocern.session", owner.Id);
        using (var seed = new SqliteStore(database))
            await new SqliteGrainStorage(seed).WriteStateAsync("session", id,
                new GrainState<StateEnvelope<CoordinationDocument>>(hub.Of("session", owner.Id)));

        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders())
            .UseOrleans(silo => silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(),
                serviceId: "event-wait-read-expiry", clusterId: Guid.NewGuid().ToString("N")).AddCoCernSqlite(database))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var grain = CoordinationGrain.Aggregate(host.Services.GetRequiredService<IGrainFactory>(), "session", owner.Id);
            var read = new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = owner.Id, ["sessionToken"] = owner.Token, ["waitId"] = "read-expiry" };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var reply = await grain.ExecuteAsync(AggregateHub.Alice with { IsInternal = true }, EventWaitAggregate.ReadOperation, read.ToJsonString()).WaitAsync(deadline.Token);
                Assert.True(reply.Ok, reply.Code);
                Assert.Equal("expired", AggregateHub.Data(reply)["terminal"]!["outcome"]!.GetValue<string>());
            }
            // No due pump or inbox read ran: only the private read can have
            // committed this transition despite its IsRead classification.
            var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
            await new SqliteGrainStorage(host.Services.GetRequiredService<SqliteStore>()).ReadStateAsync("session", id, stored);
            Assert.True(stored.RecordExists);
            var persisted = Assert.IsType<StateEnvelope<CoordinationDocument>>(stored.State);
            var terminal = JsonNode.Parse(persisted.Value.Json)!["eventWaits"]!["read-expiry"]!["terminal"]!;
            Assert.Equal("expired", terminal["outcome"]!.GetValue<string>());
            Assert.Single(persisted.Effects, effect => effect.Operation == "__deliver"
                && JsonNode.Parse(effect.PayloadJson)?["kind"]?.GetValue<string>() == "event_wait_terminal");
        }
        finally { await host.StopAsync(); }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

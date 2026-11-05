using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

public sealed class EventWaitSiloTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task The_real_due_pump_expires_a_persisted_wait_without_reads_and_restart_cannot_refire()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cocern-event-wait-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(directory, "relay.db");
        var caller = new RelayCaller("principal", false, ["repo"], true);
        // A valid historical registration makes its deadline already due at
        // startup. No test clock, sleep-based expiry, or live hub is involved.
        var registeredAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var expiresAt = registeredAt.AddMinutes(1);
        var control = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        var opened = CoordinationStateMachine.Apply(control, "control", AggregateKinds.ControlKey, caller, "session_open",
            new JsonObject { ["repositoryId"] = "repo", ["requestId"] = "fixture-open", ["label"] = "Deadline fixture" }, registeredAt);
        Assert.True(opened.Ok);
        var handle = JsonNode.Parse(opened.DataJson)!;
        var sessionId = handle["sessionId"]!.GetValue<string>();
        var sessionToken = handle["sessionToken"]!.GetValue<string>();
        var envelope = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        JsonObject Args() => new() { ["repositoryId"] = "repo", ["sessionId"] = sessionId, ["sessionToken"] = sessionToken };
        var opening = Assert.Single(control.Effects, effect => effect.Operation == "__open");
        Assert.True(CoordinationStateMachine.Apply(envelope, "session", sessionId, caller, "__open",
            JsonNode.Parse(opening.PayloadJson)!.AsObject(), registeredAt).Ok);
        control.Effects.Clear(); // The genuine directory open was applied above.
        envelope.Effects.Clear();
        var registering = Args();
        registering["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest("wait", "claim_released", "resource", "12", expiresAt), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(CoordinationStateMachine.Apply(envelope, "session", sessionId, caller, "__event_wait_register", registering, registeredAt).Ok);
        var deadline = Assert.Single(envelope.Effects, effect => effect.Operation == "__event_wait_due");
        // A future effect is deliberately first: due dispatch must not stop there.
        envelope.Effects.Insert(0, new("future-sentinel", "session", sessionId, "__event_wait_due",
            new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = sessionId }.ToJsonString(), DateTimeOffset.UtcNow.AddHours(1)));
        using (var seed = new SqliteStore(database))
        {
            await new SqliteGrainStorage(seed).WriteStateAsync("control", GrainId.Create("cocern.control", AggregateKinds.ControlKey),
                new GrainState<StateEnvelope<CoordinationDocument>>(control));
            await new SqliteGrainStorage(seed).WriteStateAsync("session", GrainId.Create("cocern.session", sessionId),
                new GrainState<StateEnvelope<CoordinationDocument>>(envelope));
        }

        async Task<(string Receipt, string MessageId)> RunAsync()
        {
            using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders())
                .UseOrleans(silo => silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(),
                    serviceId: "event-wait-restart", clusterId: Guid.NewGuid().ToString("N")).AddCoCernSqlite(database))
                .ConfigureServices(services => services.AddCoCernCore()).Build();
            await host.StartAsync();
            try
            {
                var store = host.Services.GetRequiredService<SqliteStore>();
                var grains = host.Services.GetRequiredService<IGrainFactory>();
                var failures = 0;
                // This is the production pump used by DueWorkDispatcher, with
                // real SQLite due references and actual Orleans grain dispatch.
                var pump = new DueWorkPump(store, (work, ct) => CoordinationGrain.DispatchDueAsync(grains, work, ct), () => failures++);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                for (var batch = 0; batch < 6; batch++)
                {
                    var dispatched = await pump.DispatchNextBatchAsync(DateTimeOffset.UtcNow, timeout.Token).WaitAsync(timeout.Token);
                    output.WriteLine($"Pump batch {batch}: {dispatched} references; failures {failures}.");
                    if (dispatched == 0) break;
                }
                Assert.Equal(0, failures);
                // Read storage only after pumping; no status, inbox, or verify
                // call can have caused this transition or delivered the notice.
                var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
                await new SqliteGrainStorage(store).ReadStateAsync("session", GrainId.Create("cocern.session", sessionId), stored);
                Assert.True(stored.RecordExists);
                var persisted = Assert.IsType<StateEnvelope<CoordinationDocument>>(stored.State);
                var state = JsonNode.Parse(persisted.Value.Json)!.AsObject();
                output.WriteLine("Persisted keys: " + string.Join(",", state.Select(pair => pair.Key)));
                output.WriteLine("Remaining effects: " + string.Join(",", persisted.Effects.Select(effect => effect.Operation)));
                var waits = Assert.IsType<JsonObject>(state["eventWaits"]);
                var wait = Assert.IsType<JsonObject>(waits["wait"]);
                var terminal = Assert.IsType<JsonObject>(wait["terminal"]);
                Assert.Equal("expired", terminal["outcome"]!.GetValue<string>());
                var message = Assert.Single(state["inbox"]!.AsObject());
                Assert.Equal("event_wait_terminal", message.Value!["kind"]!.GetValue<string>());
                Assert.Equal(terminal["messageId"]!.GetValue<string>(), message.Key);
                Assert.DoesNotContain(persisted.Effects, effect => effect.Id == deadline.Id);
                Assert.Single(persisted.Effects, effect => effect.Id == "future-sentinel");
                Assert.DoesNotContain(await store.ReadDueAsync(DateTimeOffset.UtcNow, 64), work => work.WorkId == deadline.Id);
                return (terminal.ToJsonString(), message.Key);
            }
            finally { await host.StopAsync(); }
        }

        var first = await RunAsync();
        Assert.Equal(first, await RunAsync());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}

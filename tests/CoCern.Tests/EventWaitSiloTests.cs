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
    public async Task Ordinary_claim_expiry_reaches_the_waiter_through_real_due_work_without_reads_or_grant_transfer()
    {
        var database = Path.Combine(Path.GetTempPath(), "cocern-event-claim-" + Guid.NewGuid().ToString("N"), "relay.db");
        var caller = new RelayCaller("principal", false, ["repo"], true);
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);
        var control = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        var opened = CoordinationStateMachine.Apply(control, "control", AggregateKinds.ControlKey, caller, "session_open",
            new JsonObject { ["repositoryId"] = "repo", ["requestId"] = "fixture-open" }, at);
        Assert.True(opened.Ok);
        var handle = JsonNode.Parse(opened.DataJson)!;
        var id = handle["sessionId"]!.GetValue<string>(); var token = handle["sessionToken"]!.GetValue<string>();
        var owner = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        var opening = Assert.Single(control.Effects, effect => effect.Operation == "__open");
        Assert.True(CoordinationStateMachine.Apply(owner, "session", id, caller, "__open", JsonNode.Parse(opening.PayloadJson)!.AsObject(), at).Ok);
        control.Effects.Clear(); owner.Effects.Clear();
        JsonObject Args() => new() { ["repositoryId"] = "repo", ["sessionId"] = id, ["sessionToken"] = token };
        var registration = Args();
        registration["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest("wait", "claim_released", "resource", "1", at.AddMinutes(10)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(CoordinationStateMachine.Apply(owner, "session", id, caller, "__event_wait_register", registration, at).Ok);
        var binding = JsonSerializer.SerializeToNode(new EventWaitSourceWatch("repo", id, "wait", "watch", "claim_released", "lock", "resource", null,
            at.AddMinutes(10), Revision: "1"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        binding["sessionId"] = id; binding["sessionToken"] = token;
        Assert.True(CoordinationStateMachine.Apply(owner, "session", id, caller, EventWaitSources.SourceBindOperation, binding, at.AddSeconds(1)).Ok);
        var resource = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        Assert.True(CoordinationStateMachine.Apply(resource, "lock", "resource", caller, "claim_acquire", new JsonObject
        { ["repositoryId"] = "repo", ["sessionId"] = "holder", ["leaseSeconds"] = 30 }, at).Ok);
        var install = Assert.Single(owner.Effects, effect => effect.Operation == EventWaitSources.SourceWatchOperation);
        Assert.False(install.PayloadJson.Contains(token, StringComparison.Ordinal));
        Assert.True(CoordinationStateMachine.Apply(resource, "lock", "resource", caller, install.Operation,
            JsonNode.Parse(install.PayloadJson)!.AsObject(), at.AddSeconds(1)).Ok);
        // Keep the queued install replay: at startup it can arrive after the lease
        // ended, before its indexed timer. It must not erase the expiry event.
        using (var seed = new SqliteStore(database))
        {
            var writer = new SqliteGrainStorage(seed);
            await writer.WriteStateAsync("control", GrainId.Create("cocern.control", AggregateKinds.ControlKey), new GrainState<StateEnvelope<CoordinationDocument>>(control));
            await writer.WriteStateAsync("session", GrainId.Create("cocern.session", id), new GrainState<StateEnvelope<CoordinationDocument>>(owner));
            await writer.WriteStateAsync("lock", GrainId.Create("cocern.lock", "resource"), new GrainState<StateEnvelope<CoordinationDocument>>(resource));
        }
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders())
            .UseOrleans(silo => silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(),
                serviceId: "event-claim-expiry", clusterId: Guid.NewGuid().ToString("N")).AddCoCernSqlite(database))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var store = host.Services.GetRequiredService<SqliteStore>(); var grains = host.Services.GetRequiredService<IGrainFactory>();
            var failures = 0;
            var pump = new DueWorkPump(store, (work, ct) => CoordinationGrain.DispatchDueAsync(grains, work, ct), () => failures++);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (var batch = 0; batch < 10; batch++)
                if (await pump.DispatchNextBatchAsync(DateTimeOffset.UtcNow, timeout.Token).WaitAsync(timeout.Token) == 0) break;
            Assert.Equal(0, failures);
            var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
            await new SqliteGrainStorage(store).ReadStateAsync("session", GrainId.Create("cocern.session", id), stored);
            var state = JsonNode.Parse(Assert.IsType<StateEnvelope<CoordinationDocument>>(stored.State).Value.Json)!;
            Assert.Equal("satisfied", state["eventWaits"]!["wait"]!["terminal"]!["outcome"]!.GetValue<string>());
            Assert.Equal("1", state["eventWaits"]!["wait"]!["terminal"]!["revision"]!.GetValue<string>());
            Assert.Single(state["inbox"]!.AsObject());
            await new SqliteGrainStorage(store).ReadStateAsync("lock", GrainId.Create("cocern.lock", "resource"), stored);
            var grant = JsonNode.Parse(Assert.IsType<StateEnvelope<CoordinationDocument>>(stored.State).Value.Json)!;
            Assert.Equal(1, grant["generation"]!.GetValue<long>());
            Assert.Equal("holder", grant["holder"]!.GetValue<string>());
            Assert.Equal(at.AddSeconds(30), grant["leaseUntil"]!.GetValue<DateTimeOffset>());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Cross_repository_wait_callbacks_are_consumed_but_generic_transfers_still_retry()
    {
        var database = Path.Combine(Path.GetTempPath(), "cocern-event-denial-" + Guid.NewGuid().ToString("N"), "relay.db");
        var sender = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{\"repositoryId\":\"repo\"}" } };
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var callbacks = new[] { EventWaitSources.WatchOperation, EventWaitSources.UnwatchOperation, EventWaitSources.LifecycleOperation,
            EventWaitSources.SourceWatchOperation, EventWaitSources.SourceUnwatchOperation, EventWaitSources.SourceAckOperation, EventWaitSources.SourceSignalOperation };
        foreach (var operation in callbacks)
            sender.Effects.Add(new("denied-" + operation,
                operation is EventWaitSources.SourceWatchOperation or EventWaitSources.SourceUnwatchOperation ? "board" : "session",
                "foreign", operation, "{\"repositoryId\":\"repo\"}", now));
        sender.Effects.Add(new("generic-transfer", "session", "foreign", "__unclassified_transfer", "{\"repositoryId\":\"repo\"}", now));
        CoordinationStateMachine.StampEffects(sender.Effects, 0, new RelayCaller("principal", false, ["repo"], true));
        using (var seed = new SqliteStore(database))
        {
            var writer = new SqliteGrainStorage(seed);
            await writer.WriteStateAsync("session", GrainId.Create("cocern.session", "sender"), new GrainState<StateEnvelope<CoordinationDocument>>(sender));
            await writer.WriteStateAsync("session", GrainId.Create("cocern.session", "foreign"), new GrainState<StateEnvelope<CoordinationDocument>>(
                new() { Value = new() { Json = "{\"repositoryId\":\"other\"}" } }));
            await writer.WriteStateAsync("board", GrainId.Create("cocern.board", "foreign"), new GrainState<StateEnvelope<CoordinationDocument>>(
                new() { Value = new() { Json = "{\"repositoryId\":\"other\"}" } }));
        }
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders())
            .UseOrleans(silo => silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(),
                serviceId: "event-wait-denial", clusterId: Guid.NewGuid().ToString("N")).AddCoCernSqlite(database))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var store = host.Services.GetRequiredService<SqliteStore>();
            var grains = host.Services.GetRequiredService<IGrainFactory>();
            var failures = 0;
            var pump = new DueWorkPump(store, (work, ct) => CoordinationGrain.DispatchDueAsync(grains, work, ct), () => failures++);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await pump.DispatchNextBatchAsync(DateTimeOffset.UtcNow, timeout.Token).WaitAsync(timeout.Token);
            Assert.Equal(1, failures); // Only the unrelated transfer stays retryable.
            var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
            await new SqliteGrainStorage(store).ReadStateAsync("session", GrainId.Create("cocern.session", "sender"), stored);
            var state = Assert.IsType<StateEnvelope<CoordinationDocument>>(stored.State);
            Assert.Equal("generic-transfer", Assert.Single(state.Effects).Id);
            await new SqliteGrainStorage(store).ReadStateAsync("session", GrainId.Create("cocern.session", "foreign"), stored);
            var foreign = JsonNode.Parse(Assert.IsType<StateEnvelope<CoordinationDocument>>(stored.State).Value.Json)!.AsObject();
            Assert.Equal("other", foreign["repositoryId"]!.GetValue<string>());
            Assert.False(foreign.ContainsKey("eventWaitOwnerWatches"));
            Assert.False(foreign.ContainsKey("eventWaits"));
        }
        finally { await host.StopAsync(); }
    }

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

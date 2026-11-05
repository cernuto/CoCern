using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public sealed class CoreSiloTests
{
    [Fact]
    public async Task Real_nonreentrant_grains_deliver_durable_messages_and_preserve_cleanup_holds()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", true, ["repo"]);
            async Task<RelayReply> Call(string tool, JsonObject args)
            {
                args["repositoryId"] = "repo"; if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
                return await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            }
            JsonObject Data(RelayReply reply) { Assert.True(reply.Ok, reply.Code + " " + reply.DataJson); return JsonNode.Parse(reply.DataJson)!.AsObject(); }
            var first = Data(await Call("session_open", new())); var second = Data(await Call("session_open", new()));
            JsonObject For(JsonObject session) => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone() };
            var bind = For(first); bind["expectedVersion"] = 0; bind["binding"] = new JsonObject(); bind["availability"] = "active";
            Data(await Call("session_bind", bind));
            var send = For(first); send["recipientSessionId"] = second["sessionId"]!.DeepClone(); send["text"] = "durable message";
            Data(await Call("send_message", send));
            var inbox = Data(await Call("check_inbox", For(second))); Assert.Empty(inbox["items"]!.AsArray());
            var senderGrain = factory.GetGrain<ISessionGrain>(first["sessionId"]!.GetValue<string>());
            Assert.True((await senderGrain.ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
            var workers = Data(await Call("worker_list", new()));
            Assert.Contains(workers["items"]!.AsArray(), worker => worker!["availability"]!.ToString() == "active");
            var restartedApplication = new RelayApplication(factory);
            var afterRestart = Data(await restartedApplication.ExecuteAsync(caller, "worker_list", JsonSerializer.SerializeToElement(new { repositoryId = "repo" })));
            Assert.All(afterRestart["items"]!.AsArray(), worker => Assert.Equal("unknown", worker!["availability"]!.ToString()));
            Assert.True((await senderGrain.ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
            inbox = Data(await Call("check_inbox", For(second))); Assert.Single(inbox["items"]!.AsArray());
            var ack = For(second); ack["messageId"] = inbox["items"]![0]!["messageId"]!.DeepClone(); Data(await Call("ack", ack));
            Assert.Empty(Data(await Call("check_inbox", For(second)))["items"]!.AsArray());

            Data(await Call("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "pc", ["machineId"] = "pc" }));
            var create = For(first); create["taskId"] = "t"; create["expectedVersion"] = 0; Data(await Call("task_update", create));
            var claim = For(first); claim["taskId"] = "t"; claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray("pc");
            var grant = Data(await Call("task_claim", claim)); var generation = grant["grants"]![0]!["generation"]!.DeepClone();
            var begin = For(first); begin["taskId"] = "t"; begin["actionId"] = "a"; begin["inputFingerprint"] = "f"; begin["expectedVersion"] = 2; begin["requiresRestoration"] = true;
            Assert.True(Data(await Call("action_begin", begin))["replayAllowed"]!.GetValue<bool>());
            Data(await Call("control_request", new() { ["command"] = "pause" }));
            var report = For(first); report["taskId"] = "t"; report["actionId"] = "a"; report["expectedVersion"] = 3; report["attempt"] = 1; report["outcome"] = "completed"; report["restoration"] = "blocked";
            Data(await Call("action_report", report));
            var release = For(first); release["resourceId"] = "pc"; release["generation"] = generation.DeepClone();
            Assert.Equal("UnresolvedAction", (await Call("claim_release", release)).Code);
            report["requestId"] = Guid.NewGuid().ToString("N"); report["expectedVersion"] = 4; report["restoration"] = "verified"; Data(await Call("action_report", report));
            var board = factory.GetGrain<ITaskBoardGrain>("repo/default");
            Assert.True((await board.ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
            release["requestId"] = Guid.NewGuid().ToString("N"); Data(await Call("claim_release", release));
            claim["requestId"] = Guid.NewGuid().ToString("N"); claim["expectedVersion"] = 5;
            Assert.Equal("Paused", (await Call("task_claim", claim)).Code);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_client_cannot_mark_its_own_dependencies_archived_even_without_an_archive_store()
    {
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "core-archive-inject-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var caller = new RelayCaller("alice", true, ["repo"]);
            Task<RelayReply> Call(string tool, JsonObject args) { args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N"); return application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args)); }
            var session = JsonNode.Parse((await Call("session_open", new())).DataJson)!.AsObject();
            var workflow = new JsonObject
            {
                ["state"] = "ready", ["dependsOn"] = new JsonArray("never-archived"),
                ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human", ["restoration"] = "r", ["stopCondition"] = "s" },
                ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }), ["blockers"] = new JsonArray()
            };
            var injected = await Call("task_update", new()
            {
                ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["taskId"] = "t", ["expectedVersion"] = 0,
                ["workflow"] = workflow, ["archivedDependsOn"] = new JsonArray("never-archived")
            });
            Assert.Equal(RelayErrorCodes.DependencyUnknown, injected.Code);
        }
        finally { await host.StopAsync(); }
    }

    /// <summary>A clock the test moves by hand.</summary>

    /// <summary>Named query results by name, with the snapshot taken at the clock's time; the queries it ran, in order.</summary>

    /// <summary>Grain storage that outlives a silo, with the due-work index the hub's pump reads, as the SQLite store keeps them.</summary>
    private sealed class DurableStorage : Orleans.Storage.IGrainStorage, IDueWorkSource
    {
        private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string State, GrainId Grain), string> rows = new();

        public StateEnvelope<CoordinationDocument>? Envelope(string stateName, GrainId grainId) =>
            rows.TryGetValue((stateName, grainId), out var json) ? JsonSerializer.Deserialize<StateEnvelope<CoordinationDocument>>(json, Json) : null;

        public void Remove(string stateName, GrainId grainId) => rows.TryRemove((stateName, grainId), out _);

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var found = rows.TryGetValue((stateName, grainId), out var json);
            grainState.State = found ? JsonSerializer.Deserialize<T>(json!, Json)! : Activator.CreateInstance<T>();
            grainState.ETag = found ? "1" : null;
            grainState.RecordExists = found;
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            rows[(stateName, grainId)] = JsonSerializer.Serialize(grainState.State, Json);
            grainState.ETag = "1"; grainState.RecordExists = true;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            rows.TryRemove((stateName, grainId), out _);
            grainState.ETag = null; grainState.RecordExists = false;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DueWorkReference>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default, int offset = 0) =>
            Task.FromResult<IReadOnlyList<DueWorkReference>>(rows.Keys.SelectMany(key => (Envelope(key.State, key.Grain)?.GetDueWork() ?? []).Where(work => work.DueAt <= now)
                    .Select(work => new DueWorkReference(key.State, key.Grain.Type.ToString()!, key.Grain.Key.ToString()!, work.Id, work.Kind, work.DueAt)))
                .OrderBy(work => work.DueAt).Skip(offset).Take(limit).ToList());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}

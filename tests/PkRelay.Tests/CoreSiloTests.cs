using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using PkRelay.Core;
using Xunit;

namespace PkRelay.Tests;

public sealed class CoreSiloTests
{
    [Fact]
    public async Task Real_nonreentrant_grains_deliver_durable_messages_and_preserve_cleanup_holds()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddPkRelayCore()).Build();
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using PkRelay.Contracts;
using PkRelay.Core;
using PkRelay.Domain;
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

    [Fact]
    public async Task Search_leaves_out_unknown_dates_passes_cancellation_and_reports_a_failing_index_as_unavailable()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        var search = new FakeSearch();
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-search-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddPkRelayCore().AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), search: search))).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var caller = new RelayCaller("alice", true, ["repo"]);
            Task<RelayReply> Call(string tool, JsonObject args, CancellationToken cancellationToken = default)
            {
                args["repositoryId"] = "repo"; if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
                return application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args), cancellationToken);
            }
            var session = JsonNode.Parse((await Call("session_open", new())).DataJson)!.AsObject();
            JsonObject Search() => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["query"] = "anything" };

            using var cancellation = new CancellationTokenSource();
            search.Result = [new SearchHit("dated", "task", "ready", DateTimeOffset.Parse("2026-10-29T12:00:00Z"), "[anything]"), new SearchHit("undated", "task", "open", DateTimeOffset.MinValue, "[anything]")];
            var reply = await Call("search", Search(), cancellation.Token);
            Assert.True(reply.Ok, reply.Code);
            var hits = JsonNode.Parse(reply.DataJson)!["hits"]!.AsArray();
            Assert.Equal("2026-10-29", hits[0]!["at"]!.GetValue<string>());
            Assert.False(hits[1]!.AsObject().ContainsKey("at"));
            Assert.Equal(cancellation.Token, search.Token);

            search.Failure = new FakeDbException();
            Assert.Equal(RelayErrorCodes.Unavailable, (await Call("search", Search())).Code);
            search.Failure = new ObjectDisposedException("SqliteStore");
            Assert.Equal(RelayErrorCodes.Unavailable, (await Call("search", Search())).Code);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Attention_effects_reach_their_session_take_the_hub_settings_and_refused_ones_are_dropped()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-attention-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddPkRelayCore().AddSingleton(new AttentionOptions { SnoozeBoundaries = 2, ItemExpiryDays = 2 })
                // The test seeds outboxes straight into memory storage, which then reads them back through its JSON type allow-list.
                .Configure<Orleans.Serialization.Configuration.TypeManifestOptions>(options => options.AllowAllTypes = true)).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var storage = host.Services.GetRequiredKeyedService<Orleans.Storage.IGrainStorage>("relay");
            var caller = new RelayCaller("alice", false, ["repo"]);
            var hub = caller with { IsInternal = true };
            async Task<JsonObject> Call(string tool, JsonObject args)
            {
                args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
                var reply = await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
                Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
                return JsonNode.Parse(reply.DataJson)!.AsObject();
            }
            var session = await Call("session_open", new());
            var sessionId = session["sessionId"]!.GetValue<string>();
            DurableEffect Raise(string target, JsonObject item)
            {
                item["repositoryId"] = "repo"; item["__principalId"] = "alice"; item["__administrator"] = false; item["__repositories"] = new JsonArray("repo");
                return new(Guid.NewGuid().ToString("N"), "session", target, Operations.AttentionRaise, item.ToJsonString(), DateTimeOffset.UtcNow.AddSeconds(-1));
            }

            // A board outbox holding raises for an open session, an unknown session and a malformed kind. Seed it before the board activates.
            var board = new StateEnvelope<CoordinationDocument> { Value = new() { Json = """{"repositoryId":"repo"}""" } };
            board.Effects.Add(Raise(sessionId, new() { ["kind"] = "blocked", ["taskId"] = "T-1", ["note"] = "waiting on review" }));
            board.Effects.Add(Raise("no-such-session", new() { ["kind"] = "blocked", ["taskId"] = "T-1" }));
            board.Effects.Add(Raise(sessionId, new() { ["kind"] = "not-a-kind", ["taskId"] = "T-2" }));
            board.Effects.Add(Raise(sessionId, new() { ["kind"] = "next_work", ["taskId"] = "T-3" }));
            await storage.WriteStateAsync("board", GrainId.Create("pkrelay.board", "repo/seeded"), new GrainState<StateEnvelope<CoordinationDocument>>(board));
            var dispatched = await factory.GetGrain<ITaskBoardGrain>("repo/seeded").ExecuteAsync(hub, Operations.Dispatch, "{}");
            // Refused items are dropped, not left to retry forever: nothing is pending.
            Assert.True(dispatched.Ok, dispatched.Code + " " + dispatched.DataJson);

            var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
            await storage.ReadStateAsync("session", GrainId.Create("pkrelay.session", sessionId), stored);
            var items = JsonNode.Parse(stored.State!.Value.Json)!["attention"]!.AsObject();
            Assert.Equal(new[] { "T-1", "T-3" }, items.Select(item => item.Value!["taskId"]!.GetValue<string>()));
            var lifetime = DateTimeOffset.Parse(items.First().Value!["expiresAt"]!.GetValue<string>()) - DateTimeOffset.Parse(items.First().Value!["createdAt"]!.GetValue<string>());
            Assert.Equal(TimeSpan.FromDays(2), lifetime);

            var sessionGrain = factory.GetGrain<ISessionGrain>(sessionId);
            var handle = new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = sessionId, ["sessionToken"] = session["sessionToken"]!.DeepClone() };
            var offered = JsonNode.Parse((await sessionGrain.ExecuteAsync(hub, Operations.AttentionOffer, handle.ToJsonString())).DataJson)!.AsObject();
            var offer = offered["attention"]!.AsObject();
            Assert.Equal(("blocked", "T-1", "T-1 blocked: waiting on review", 1), (offer["k"]!.GetValue<string>(), offer["t"]!.GetValue<string>(), offer["x"]!.GetValue<string>(), offer["more"]!.GetValue<int>()));

            // The answer goes through the public tool, with the hub's two-boundary snooze.
            var answer = handle.DeepClone().AsObject(); answer["itemId"] = offer["id"]!.DeepClone(); answer["action"] = "later"; answer["snoozeBoundaries"] = 9;
            Assert.Equal(2, (await Call("attention_respond", answer))["snoozeBoundaries"]!.GetValue<int>());
            answer["itemId"] = "a-999"; answer["action"] = "dismiss"; answer["requestId"] = Guid.NewGuid().ToString("N");
            Assert.Equal(RelayErrorCodes.NotFound, (await application.ExecuteAsync(caller, "attention_respond", JsonSerializer.SerializeToElement(answer))).Code);

            // A session delivering an item to itself applies it in place (a non-reentrant grain cannot call itself), with the hub's lifetime.
            var self = new StateEnvelope<CoordinationDocument> { Value = new() { Json = """{"repositoryId":"repo","principalId":"alice","sessionToken":"tok","sessionId":"self"}""" } };
            self.Effects.Add(Raise("self", new() { ["kind"] = "info", ["taskId"] = "T-9" }));
            await storage.WriteStateAsync("session", GrainId.Create("pkrelay.session", "self"), new GrainState<StateEnvelope<CoordinationDocument>>(self));
            var selfGrain = factory.GetGrain<ISessionGrain>("self");
            Assert.True((await selfGrain.ExecuteAsync(hub, Operations.Dispatch, "{}")).Ok);
            var selfOffer = JsonNode.Parse((await selfGrain.ExecuteAsync(hub, Operations.AttentionOffer, """{"repositoryId":"repo","sessionId":"self","sessionToken":"tok"}""")).DataJson)!["attention"]!;
            Assert.Equal("T-9", selfOffer["t"]!.GetValue<string>());
        }
        finally { await host.StopAsync(); }
    }

    private sealed class FakeSearch : IFullTextSearch
    {
        public IReadOnlyList<SearchHit> Result { get; set; } = [];
        public Exception? Failure { get; set; }
        public CancellationToken Token { get; private set; }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string repositoryId, string sessionId, string query, IReadOnlyList<string> kinds, int limit, string? status = null, CancellationToken ct = default)
        {
            Token = ct;
            return Failure is { } failure ? Task.FromException<IReadOnlyList<SearchHit>>(failure) : Task.FromResult(Result);
        }
    }

    private sealed class FakeDbException : System.Data.Common.DbException { }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}

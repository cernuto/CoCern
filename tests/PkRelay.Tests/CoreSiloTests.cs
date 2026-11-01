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

    [Fact]
    public async Task Attention_sources_reach_claimers_holders_and_managers_through_real_grains()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        var search = new FakeSearch();
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-sources-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddPkRelayCore().AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), search: search))).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", true, ["repo"]);
            var hub = caller with { IsInternal = true };
            async Task<JsonObject> Call(string tool, JsonObject args)
            {
                args["repositoryId"] = "repo"; if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
                var reply = await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
                Assert.True(reply.Ok, tool + ": " + reply.Code + " " + reply.DataJson);
                return JsonNode.Parse(reply.DataJson)!.AsObject();
            }
            async Task Dispatch(IRelayAggregateGrain grain, string workId = "") =>
                Assert.True((await grain.ExecuteAsync(hub, Operations.Dispatch, new JsonObject { ["workId"] = workId }.ToJsonString())).Ok);
            var manager = await Call("session_open", new() { ["role"] = "manager" });
            var worker = await Call("session_open", new() { ["role"] = "worker" });
            JsonObject As(JsonObject session, JsonObject args) { args["sessionId"] = session["sessionId"]!.DeepClone(); args["sessionToken"] = session["sessionToken"]!.DeepClone(); return args; }
            JsonObject Workflow(string state)
            {
                var workflow = new JsonObject
                {
                    ["state"] = state, ["packet"] = new JsonObject { ["goal"] = "print the label", ["target"] = "t", ["authorityRef"] = "human-grant:1", ["restoration"] = "r", ["stopCondition"] = "s" },
                    ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }), ["blockers"] = new JsonArray()
                };
                if (state == "blocked") workflow["blockers"] = new JsonArray(new JsonObject { ["id"] = "b", ["kind"] = "tool_unavailable", ["summary"] = "printer offline", ["recheckCondition"] = "r" });
                return workflow;
            }
            await Call("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "pc", ["machineId"] = "pc" });
            await Call("task_update", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready") }));

            // After the claim reply, an all-words search finds look-alikes: an open one is a possible duplicate, a completed one related
            // work; IDs only, never the task itself, and never a weak match.
            search.AllWords = [new SearchHit("T-1", "task", "ready", DateTimeOffset.UtcNow, "[print]", Score: 9), new SearchHit("T-7", "task", "completed", DateTimeOffset.UtcNow, "[print]", Score: 5),
                new SearchHit("T-8", "task", "ready", DateTimeOffset.UtcNow, "[print]", Score: 4), new SearchHit("T-9", "task", "ready", DateTimeOffset.UtcNow, "[print]", Score: RelayLimits.RelatedWorkMinScore / 2)];
            var claim = await Call("task_claim", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray("pc"), ["leaseSeconds"] = 30 }));
            Assert.False(claim.ContainsKey("attention"));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((await Call("get_status", As(worker, new())))["attention"]!["actionable"]!.GetValue<int>() < 2 && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Equal("print the label", search.Query);
            var duplicate = (await Call("check_inbox", As(worker, new())))["attention"]!.AsObject();
            Assert.Equal(("duplicate_warning", 1), (duplicate["k"]!.GetValue<string>(), duplicate["more"]!.GetValue<int>()));
            Assert.Equal("duplicate_warning: may duplicate T-1 refs T-8", duplicate["x"]!.GetValue<string>());

            // An action in flight on a lease about to end: the lock's due check raises claim_expiring to the holder.
            await Call("action_begin", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 2, ["actionId"] = "print", ["inputFingerprint"] = "f" }));
            var printer = factory.GetGrain<ILockGrain>("pc");
            await Dispatch(printer, "lease_warning");
            await Dispatch(printer);
            var status = (await Call("get_status", As(worker, new() { ["attentionDetail"] = true })))["attention"]!.AsObject();
            Assert.Equal(3, status["actionable"]!.GetValue<int>());
            var worked = await Call("action_report", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 3, ["actionId"] = "print", ["attempt"] = 1, ["outcome"] = "started", ["attention"] = false }));
            Assert.False(worked.ContainsKey("attention"));

            // The worker blocks the task: the board routes a blocked item through the control aggregate to the manager.
            await Call("task_update", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 4, ["workflow"] = Workflow("blocked"), ["attention"] = false }));
            await Dispatch(factory.GetGrain<ITaskBoardGrain>("repo/default"));
            await Dispatch(factory.GetGrain<IControlGrain>(AggregateKinds.ControlKey));
            var managed = await Call("task_update", As(manager, new() { ["taskId"] = "M-1", ["expectedVersion"] = 0 }));
            Assert.Equal(("blocked", "T-1", "T-1 blocked: printer offline"), (managed["attention"]!["k"]!.GetValue<string>(), managed["attention"]!["t"]!.GetValue<string>(), managed["attention"]!["x"]!.GetValue<string>()));
            // A background inbox read takes no offer, and the worker (not a manager) got no copy.
            Assert.False((await Call("check_inbox", As(manager, new() { ["attention"] = false }))).ContainsKey("attention"));
            Assert.Equal(3, (await Call("get_status", As(worker, new())))["attention"]!["actionable"]!.GetValue<int>());

            // A next-work offer on a milestone becomes a next_work item at the caller once the reply's own offer was chosen, and the
            // caller's own claim closes it.
            await Call("task_update", As(manager, new() { ["taskId"] = "N-1", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready"), ["attention"] = false }));
            var offered = await Call("task_update", As(manager, new() { ["taskId"] = "M-1", ["expectedVersion"] = 1, ["includeNextWork"] = true, ["attention"] = false }));
            Assert.Equal("N-1", offered["nextWork"]!["offer"]!["taskId"]!.GetValue<string>());
            var managerStatus = (await Call("get_status", As(manager, new() { ["attentionDetail"] = true })))["attention"]!.AsObject();
            Assert.Equal(2, managerStatus["actionable"]!.GetValue<int>());
            await Call("attention_respond", As(manager, new() { ["itemId"] = managed["attention"]!["id"]!.DeepClone(), ["action"] = "dismiss" }));
            var next = (await Call("check_inbox", As(manager, new())))["attention"]!.AsObject();
            Assert.Equal(("next_work", "N-1"), (next["k"]!.GetValue<string>(), next["t"]!.GetValue<string>()));
            await Call("task_claim", As(manager, new() { ["taskId"] = "N-1", ["expectedVersion"] = 1, ["attention"] = false }));
            Assert.Equal(0, (await Call("get_status", As(manager, new())))["attention"]!["actionable"]!.GetValue<int>());

            // Renewing the lease closes claim_expiring.
            var grant = claim["grants"]![0]!;
            await Call("claim_renew", As(worker, new() { ["resourceId"] = "pc", ["generation"] = grant["generation"]!.DeepClone(), ["leaseSeconds"] = 600 }));
            await Dispatch(printer);
            Assert.Equal(2, (await Call("get_status", As(worker, new())))["attention"]!["actionable"]!.GetValue<int>());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Anomaly_monitor_check_survives_a_restart_ignores_small_samples_cools_down_and_closes_back_in_range()
    {
        var storage = new DurableStorage();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var analytics = new FakeAnalytics(clock);
        var options = new AnomalyOptions { Time = clock };
        var caller = new RelayCaller("alice", false, ["repo"]);
        IHost Silo() => new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo =>
            {
                silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "core-anomaly-tests", clusterId: Guid.NewGuid().ToString("N"));
                silo.Services.AddKeyedSingleton<Orleans.Storage.IGrainStorage>("relay", (_, _) => storage);
            })
            .ConfigureServices(services => services.AddPkRelayCore().AddSingleton(options).AddSingleton<IAnalyticsQueries>(analytics)
                .AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), anomaly: options))).Build();
        async Task<JsonObject> Call(IHost host, string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            var reply = await host.Services.GetRequiredService<RelayApplication>().ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            Assert.True(reply.Ok, tool + ": " + reply.Code + " " + reply.DataJson);
            return JsonNode.Parse(reply.DataJson)!.AsObject();
        }
        // Like the hub's pump: dispatch every due item until none is left, judging the monitor by its own clock.
        async Task Pump(IHost host)
        {
            for (var round = 0; round < 5; round++)
            {
                var due = await storage.ReadDueAsync(clock.GetUtcNow() > DateTimeOffset.UtcNow ? clock.GetUtcNow() : DateTimeOffset.UtcNow, 64);
                if (due.Count == 0) return;
                foreach (var item in due) await host.Services.GetRequiredService<RelayApplication>().DispatchDueAsync(item);
            }
        }
        StateEnvelope<CoordinationDocument> Monitor() => storage.Envelope("anomaly", GrainId.Create("pkrelay.anomaly", "repo"))!;
        long Revision() => JsonNode.Parse(Monitor().Value.Json)!["attentionRevision"]?.GetValue<long>() ?? 0;

        JsonObject manager;
        using (var first = Silo())
        {
            await first.StartAsync();
            manager = await Call(first, "session_open", new() { ["role"] = "Manager" });
            // The manager session starts the monitor after its reply; the first check is due at once.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (storage.Envelope("anomaly", GrainId.Create("pkrelay.anomaly", "repo")) is null && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Equal(clock.GetUtcNow(), Assert.Single(Monitor().DueWork, work => work.Kind == DueWorkKinds.AnomalyCheck).DueAt);

            // A value far above its baseline, but from too few rows, raises nothing.
            analytics.Rows[AnalyticsQueryNames.TokensPerTask] = [["all", 3000.0, 100.0, 2L, 2L]];
            await Pump(first);
            Assert.Equal(AnomalyOptions.CheckedQueries, analytics.Queries);
            Assert.Empty(Monitor().Effects);
            Assert.Equal(0, Revision());
            Assert.Equal(clock.GetUtcNow().AddHours(1), Assert.Single(Monitor().DueWork, work => work.Kind == DueWorkKinds.AnomalyCheck).DueAt);
            await first.StopAsync();
        }

        using var second = Silo();
        await second.StartAsync();
        JsonObject As(JsonObject args) { args["sessionId"] = manager["sessionId"]!.DeepClone(); args["sessionToken"] = manager["sessionToken"]!.DeepClone(); return args; }
        async Task<int> Actionable() => (await Call(second, "get_status", As(new())))["attention"]!["actionable"]!.GetValue<int>();
        async Task Check(object?[] tokens)
        {
            clock.Advance(TimeSpan.FromHours(1));
            analytics.Rows[AnalyticsQueryNames.TokensPerTask] = [tokens];
            await Pump(second);
        }

        // After the restart the scheduled check is still in due work, and it runs once its time comes.
        analytics.Queries.Clear();
        await Check(["all", 300.0, 100.0, 10L, 10L]);
        Assert.Equal(AnomalyOptions.CheckedQueries, analytics.Queries);
        Assert.Equal(1, await Actionable());
        var offer = (await Call(second, "check_inbox", As(new())))["attention"]!.AsObject();
        Assert.Equal(("anomaly", "anomaly: tokens_per_task all: 300 vs 100 baseline (n=10)"), (offer["k"]!.GetValue<string>(), offer["x"]!.GetValue<string>()));

        // The same anomaly in the same period is not raised again.
        await Check(["all", 400.0, 100.0, 10L, 10L]);
        Assert.Equal(1, Revision());
        // Back in range: the item closes.
        await Check(["all", 110.0, 100.0, 10L, 10L]);
        Assert.Equal(2, Revision());
        Assert.Equal(0, await Actionable());
        // Out of range again within the cooldown: suppressed.
        await Check(["all", 300.0, 100.0, 10L, 10L]);
        Assert.Equal(2, Revision());
        Assert.Equal(0, await Actionable());
        // Once the cooldown has passed, it is raised again.
        clock.Advance(TimeSpan.FromHours(RelayLimits.AnomalyCooldownHours));
        await Check(["all", 300.0, 100.0, 10L, 10L]);
        Assert.Equal(3, Revision());
        Assert.Equal(1, await Actionable());
        Assert.Empty(Monitor().Effects);
        await second.StopAsync();
    }

    [Fact]
    public async Task Anomaly_monitor_does_nothing_when_analytics_or_attention_is_off()
    {
        var storage = new DurableStorage();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var analytics = new FakeAnalytics(clock);
        analytics.Rows[AnalyticsQueryNames.TokensPerTask] = [["all", 300.0, 100.0, 10L, 10L]];
        var options = new AnomalyOptions { Time = clock };
        var caller = new RelayCaller("alice", false, ["repo"]);
        var monitorId = GrainId.Create("pkrelay.anomaly", "repo");
        foreach (var (attentionOn, analyticsOn) in new[] { (false, true), (true, false) })
        {
            using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo =>
                {
                    silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "core-anomaly-off-tests", clusterId: Guid.NewGuid().ToString("N"));
                    silo.Services.AddKeyedSingleton<Orleans.Storage.IGrainStorage>("relay", (_, _) => storage);
                })
                .ConfigureServices(services =>
                {
                    var attention = new AttentionOptions { Enabled = attentionOn };
                    services.AddPkRelayCore().AddSingleton(options).AddSingleton(attention);
                    if (analyticsOn) services.AddSingleton<IAnalyticsQueries>(analytics);
                    // The host passes the monitor's options only with analytics on.
                    services.AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), attention: attention, anomaly: analyticsOn ? options : null));
                }).Build();
            await host.StartAsync();
            var application = host.Services.GetRequiredService<RelayApplication>();
            var opened = await application.ExecuteAsync(caller, "session_open", JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = Guid.NewGuid().ToString("N"), role = "manager" }));
            Assert.True(opened.Ok, opened.Code);
            await Task.Delay(200);
            Assert.Null(storage.Envelope("anomaly", monitorId));

            // A check scheduled earlier (here started by hand) runs no query and raises nothing, and the monitor stops.
            var monitor = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IAnomalyMonitorGrain>("repo");
            Assert.True((await monitor.ExecuteAsync(caller with { IsInternal = true }, Operations.AnomalyStart, """{"repositoryId":"repo"}""")).Ok);
            var due = Assert.Single(await storage.ReadDueAsync(clock.GetUtcNow(), 64), work => work.Kind == DueWorkKinds.AnomalyCheck);
            await application.DispatchDueAsync(due);
            Assert.Empty(analytics.Queries);
            Assert.Empty(storage.Envelope("anomaly", monitorId)!.Effects);
            Assert.Empty(storage.Envelope("anomaly", monitorId)!.DueWork);
            await host.StopAsync();
            storage.Remove("anomaly", monitorId);
        }
    }

    [Fact]
    public void An_anomaly_needs_enough_samples_on_both_sides_and_a_value_past_the_ratio()
    {
        var at = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        JsonObject Find(string query, params object?[][] rows) => AnomalyMonitorGrain.Finding(query, new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"], rows, false, at), new AnomalyThreshold(2, 5));
        Assert.Equal("2026-11-01", Find(AnalyticsQueryNames.TokensPerTask)["period"]!.GetValue<string>());
        Assert.Null(Find(AnalyticsQueryNames.TokensPerTask, ["all", 300.0, 100.0, 4L, 10L])["anomaly"]);
        Assert.Null(Find(AnalyticsQueryNames.TokensPerTask, ["all", 300.0, 100.0, 10L, 4L])["anomaly"]);
        Assert.Null(Find(AnalyticsQueryNames.TokensPerTask, ["all", 199.0, 100.0, 10L, 10L])["anomaly"]);
        Assert.Null(Find(AnalyticsQueryNames.TokensPerTask, ["all", 300.0, null, 10L, 10L])["anomaly"]);
        Assert.Null(Find(AnalyticsQueryNames.TokensPerTask, ["all", 300.0, 0.0, 10L, 10L])["anomaly"]);
        // Closures are better when higher, so they are never an anomaly.
        Assert.Null(Find(AnalyticsQueryNames.QualityGuardrails, ["closures", 30L, 10L, 30L, 10L])["anomaly"]);
        // The worst row (by ratio) is the one reported.
        var worst = Find(AnalyticsQueryNames.NoisiestTools, ["task_get", 900L, 300L, 9L, 9L], ["search", 500L, 100L, 6L, 5L])["anomaly"]!;
        Assert.Equal(("search", 500.0, 100.0, 6L), (worst["key"]!.GetValue<string>(), worst["value"]!.GetValue<double>(), worst["baseline"]!.GetValue<double>(), worst["samples"]!.GetValue<long>()));
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>Named query results by name, with the snapshot taken at the clock's time; the queries it ran, in order.</summary>
    private sealed class FakeAnalytics(TimeProvider clock) : IAnalyticsQueries
    {
        public Dictionary<string, IReadOnlyList<IReadOnlyList<object?>>> Rows { get; } = [];
        public List<string> Queries { get; } = [];
        public IReadOnlyList<string> QueryNames => AnalyticsQueryNames.All;
        public Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default)
        {
            lock (Queries) Queries.Add(name);
            return Task.FromResult(new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"], Rows.TryGetValue(name, out var rows) ? rows : [], false, clock.GetUtcNow()));
        }
        public Task<AnalyticsResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

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

    private sealed class FakeSearch : IFullTextSearch
    {
        public IReadOnlyList<SearchHit> Result { get; set; } = [];
        public Exception? Failure { get; set; }
        public CancellationToken Token { get; private set; }
        public string? Query { get; private set; }
        public IReadOnlyList<SearchHit> AllWords { get; set; } = [];

        public Task<IReadOnlyList<SearchHit>> SearchAllWordsAsync(string repositoryId, string sessionId, string query, IReadOnlyList<string> kinds, int limit, CancellationToken ct = default)
        {
            Query = query;
            return Task.FromResult(AllWords);
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string repositoryId, string sessionId, string query, IReadOnlyList<string> kinds, int limit, string? status = null, CancellationToken ct = default)
        {
            Token = ct; Query = query;
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

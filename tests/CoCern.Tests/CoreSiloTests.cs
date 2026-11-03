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
    public async Task Search_leaves_out_unknown_dates_passes_cancellation_and_reports_a_failing_index_as_unavailable()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        var search = new FakeSearch();
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-search-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), search: search))).Build();
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
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(new AttentionOptions { SnoozeBoundaries = 2, ItemExpiryDays = 2 })
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
            await storage.WriteStateAsync("board", GrainId.Create("cocern.board", "repo/seeded"), new GrainState<StateEnvelope<CoordinationDocument>>(board));
            var dispatched = await factory.GetGrain<ITaskBoardGrain>("repo/seeded").ExecuteAsync(hub, Operations.Dispatch, "{}");
            // Refused items are dropped, not left to retry forever: nothing is pending.
            Assert.True(dispatched.Ok, dispatched.Code + " " + dispatched.DataJson);

            var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
            await storage.ReadStateAsync("session", GrainId.Create("cocern.session", sessionId), stored);
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
            await storage.WriteStateAsync("session", GrainId.Create("cocern.session", "self"), new GrainState<StateEnvelope<CoordinationDocument>>(self));
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
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), search: search))).Build();
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
            // The claim's background related-work search would otherwise raise T-1 as a duplicate to the manager at a random moment.
            search.AllWords = [];
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
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(options).AddSingleton<IAnalyticsQueries>(analytics)
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
                foreach (var item in due) await CoordinationGrain.DispatchDueAsync(host.Services.GetRequiredService<IGrainFactory>(), item);
            }
        }
        StateEnvelope<CoordinationDocument> Monitor() => storage.Envelope("anomaly", GrainId.Create("cocern.anomaly", "repo"))!;
        long Revision() => JsonNode.Parse(Monitor().Value.Json)!["attentionRevision"]?.GetValue<long>() ?? 0;
        // A due check schedules the next one and runs its queries detached; the findings come back to the monitor, then their routes are pumped.
        async Task RunCheck(IHost host)
        {
            var at = clock.GetUtcNow();
            await Pump(host);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (JsonFields.Date(JsonNode.Parse(Monitor().Value.Json)!.AsObject(), "findingsAt") < at && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.Equal(at, JsonFields.Date(JsonNode.Parse(Monitor().Value.Json)!.AsObject(), "findingsAt"));
            await Pump(host);
        }

        JsonObject manager;
        using (var first = Silo())
        {
            await first.StartAsync();
            manager = await Call(first, "session_open", new() { ["role"] = "Manager" });
            // The manager session starts the monitor after its reply; the first check is due at once.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (storage.Envelope("anomaly", GrainId.Create("cocern.anomaly", "repo")) is null && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Equal(clock.GetUtcNow(), Assert.Single(Monitor().DueWork, work => work.Kind == DueWorkKinds.AnomalyCheck).DueAt);

            // A value far above its baseline, but from too few rows, raises nothing.
            analytics.Rows[AnalyticsQueryNames.TokensPerTask] = [["all", 3000.0, 100.0, 2L, 2L]];
            await RunCheck(first);
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
            await RunCheck(second);
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
        // In range but from too few rows is not back in range: the item stays open.
        await Check(["all", 110.0, 100.0, 2L, 10L]);
        Assert.Equal(1, Revision());
        Assert.Equal(1, await Actionable());
        // Back in range with enough samples: the item closes.
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
        var monitorId = GrainId.Create("cocern.anomaly", "repo");
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
                    services.AddCoCernCore().AddSingleton(options).AddSingleton(attention);
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
            await CoordinationGrain.DispatchDueAsync(host.Services.GetRequiredService<IGrainFactory>(), due);
            Assert.Empty(analytics.Queries);
            Assert.Empty(storage.Envelope("anomaly", monitorId)!.Effects);
            Assert.Empty(storage.Envelope("anomaly", monitorId)!.DueWork);
            await host.StopAsync();
            storage.Remove("anomaly", monitorId);
        }
    }

    [Fact]
    public async Task A_slow_anomaly_check_does_not_hold_up_other_due_work()
    {
        var storage = new DurableStorage();
        // The clock is a minute behind, so the monitor's first check is due before the message below.
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(-1));
        var analytics = new FakeAnalytics(clock) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        analytics.Rows[AnalyticsQueryNames.TokensPerTask] = [["all", 300.0, 100.0, 10L, 10L]];
        var options = new AnomalyOptions { Time = clock };
        var caller = new RelayCaller("alice", false, ["repo"]);
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo =>
            {
                silo.UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "core-anomaly-slow-tests", clusterId: Guid.NewGuid().ToString("N"));
                silo.Services.AddKeyedSingleton<Orleans.Storage.IGrainStorage>("relay", (_, _) => storage);
            })
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(options).AddSingleton<IAnalyticsQueries>(analytics)
                .AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), anomaly: options))).Build();
        await host.StartAsync();
        var application = host.Services.GetRequiredService<RelayApplication>();
        async Task<JsonObject> Call(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            var reply = await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            Assert.True(reply.Ok, tool + ": " + reply.Code + " " + reply.DataJson);
            return JsonNode.Parse(reply.DataJson)!.AsObject();
        }
        var monitorId = GrainId.Create("cocern.anomaly", "repo");
        var manager = await Call("session_open", new() { ["role"] = "manager" });
        var worker = await Call("session_open", new() { ["role"] = "worker" });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (storage.Envelope("anomaly", monitorId) is null && DateTime.UtcNow < deadline) await Task.Delay(25);
        // Deliver the session openings first, so only the check and the message are due.
        foreach (var item in await storage.ReadDueAsync(DateTimeOffset.UtcNow, 64)) if (item.Kind != DueWorkKinds.AnomalyCheck) await CoordinationGrain.DispatchDueAsync(host.Services.GetRequiredService<IGrainFactory>(), item);
        await Call("send_message", new() { ["sessionId"] = worker["sessionId"]!.DeepClone(), ["sessionToken"] = worker["sessionToken"]!.DeepClone(), ["recipientSessionId"] = manager["sessionId"]!.DeepClone(), ["text"] = "same time" });

        // Like the hub's pump, one due item at a time: the check comes first, and its query never returns while the gate is shut.
        var due = await storage.ReadDueAsync(DateTimeOffset.UtcNow, 64);
        Assert.Equal(DueWorkKinds.AnomalyCheck, due[0].Kind);
        var pumped = Task.Run(async () => { foreach (var item in due) await CoordinationGrain.DispatchDueAsync(host.Services.GetRequiredService<IGrainFactory>(), item); });
        Assert.Same(pumped, await Task.WhenAny(pumped, Task.Delay(TimeSpan.FromSeconds(10))));
        await pumped;
        var inbox = await Call("check_inbox", new() { ["sessionId"] = manager["sessionId"]!.DeepClone(), ["sessionToken"] = manager["sessionToken"]!.DeepClone(), ["attention"] = false });
        Assert.Equal("same time", Assert.Single(inbox["items"]!.AsArray())!["text"]!.GetValue<string>());
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (analytics.Queries.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Single(analytics.Queries);
        // The next check was scheduled before any query ran.
        Assert.Equal(clock.GetUtcNow().AddHours(1), Assert.Single(storage.Envelope("anomaly", monitorId)!.DueWork, work => work.Kind == DueWorkKinds.AnomalyCheck).DueAt);

        // Once the query returns, the findings reach the monitor and the item is raised.
        analytics.Gate.SetResult();
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (JsonNode.Parse(storage.Envelope("anomaly", monitorId)!.Value.Json)!["attentionRevision"] is null && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(1, JsonNode.Parse(storage.Envelope("anomaly", monitorId)!.Value.Json)!["attentionRevision"]!.GetValue<long>());
        await host.StopAsync();
    }

    [Fact]
    public async Task Standing_orders_run_through_real_grains_releasing_claims_offering_next_work_and_following_released_locks()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        var orders = new OrderOptions { Enabled = true };
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-orders-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(orders).AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), orders: orders))).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", true, ["repo"]);
            var hub = caller with { IsInternal = true };
            async Task<RelayReply> Raw(string tool, JsonObject args, RelayCaller? who = null)
            {
                args["repositoryId"] = "repo"; if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
                return await application.ExecuteAsync(who ?? caller, tool, JsonSerializer.SerializeToElement(args));
            }
            async Task<JsonObject> Call(string tool, JsonObject args)
            {
                var reply = await Raw(tool, args);
                Assert.True(reply.Ok, tool + ": " + reply.Code + " " + reply.DataJson);
                return JsonNode.Parse(reply.DataJson)!.AsObject();
            }
            var board = factory.GetGrain<ITaskBoardGrain>("repo/default");
            var printer = factory.GetGrain<ILockGrain>("pc");
            async Task Dispatch(IRelayAggregateGrain grain) => Assert.True((await grain.ExecuteAsync(hub, Operations.Dispatch, "{}")).Ok);
            async Task Pump() { foreach (var grain in new IRelayAggregateGrain[] { board, printer, board, factory.GetGrain<IControlGrain>(AggregateKinds.ControlKey) }) await Dispatch(grain); }
            var manager = await Call("session_open", new() { ["role"] = "manager" });
            var worker = await Call("session_open", new() { ["role"] = "worker" });
            var next = await Call("session_open", new() { ["role"] = "worker" });
            JsonObject As(JsonObject session, JsonObject args) { args["sessionId"] = session["sessionId"]!.DeepClone(); args["sessionToken"] = session["sessionToken"]!.DeepClone(); return args; }
            JsonObject Workflow(string state) => new()
            {
                ["state"] = state, ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human-grant:1", ["restoration"] = "r", ["stopCondition"] = "s" },
                ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = state == "completed" ? "passed" : "not_run", ["evidenceRef"] = state == "completed" ? "artifact:1" : null }),
                ["blockers"] = new JsonArray()
            };
            await Call("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "pc", ["machineId"] = "pc" });
            await Call("task_update", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready"), ["attention"] = false }));
            await Call("task_update", As(worker, new() { ["taskId"] = "T-2", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready"), ["attention"] = false }));

            // A non-administrator cannot write orders, whatever role its session claims.
            Assert.Equal(RelayErrorCodes.AdministratorRequired, (await Raw("order_put", new() { ["orderId"] = "o-0", ["trigger"] = new JsonObject { ["on"] = "workflow_completed", ["taskId"] = "T-1" }, ["actions"] = new JsonArray(new JsonObject { ["do"] = "release_claims" }) }, new RelayCaller("bob", false, ["repo"]))).Code);
            // When T-1 completes: release its claims, offer T-2 to the next worker, tell the manager. When pc is released: tell the manager.
            await Call("order_put", new()
            {
                ["orderId"] = "o-1", ["trigger"] = new JsonObject { ["on"] = "workflow_completed", ["taskId"] = "T-1" },
                ["actions"] = new JsonArray(new JsonObject { ["do"] = "release_claims" }, new JsonObject { ["do"] = "offer", ["taskId"] = "T-2", ["sessionId"] = next["sessionId"]!.DeepClone() }, new JsonObject { ["do"] = "notify", ["sessionId"] = manager["sessionId"]!.DeepClone(), ["note"] = "T-1 done" })
            });
            Assert.Equal(RelayErrorCodes.ResourceUnknown, (await Raw("order_put", new() { ["orderId"] = "o-2", ["trigger"] = new JsonObject { ["on"] = "claim_released", ["resourceId"] = "nowhere" }, ["actions"] = new JsonArray(new JsonObject { ["do"] = "notify", ["sessionId"] = "x" }) })).Code);
            await Call("order_put", new()
            {
                ["orderId"] = "o-2", ["trigger"] = new JsonObject { ["on"] = "claim_released", ["resourceId"] = "pc" },
                ["actions"] = new JsonArray(new JsonObject { ["do"] = "notify", ["sessionId"] = manager["sessionId"]!.DeepClone(), ["note"] = "pc is free" })
            });
            await Pump();

            var claim = await Call("task_claim", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray("pc"), ["leaseSeconds"] = 600, ["attention"] = false }));
            Assert.Equal(RelayErrorCodes.Busy, (await Raw("claim_acquire", As(next, new() { ["resourceId"] = "pc" }))).Code);
            await Call("task_update", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 2, ["workflow"] = Workflow("completed"), ["attention"] = false }));
            // The board's release reaches the lock, whose release runs the board's claim_released order in turn.
            await Pump();
            await Pump();

            Assert.False((await Call("task_get", new() { ["taskId"] = "T-1" })).ContainsKey("owner"));
            Assert.True((await Call("claim_acquire", As(next, new() { ["resourceId"] = "pc" })))["newlyAcquired"]!.GetValue<bool>());
            var offer = (await Call("check_inbox", As(next, new())))["attention"]!.AsObject();
            Assert.Equal(("next_work", "T-2"), (offer["k"]!.GetValue<string>(), offer["t"]!.GetValue<string>()));
            Assert.Contains("claim it with task_claim", offer["x"]!.GetValue<string>());
            // The offer acquired nothing: T-2 is still unclaimed until the worker claims it.
            Assert.False((await Call("task_get", new() { ["taskId"] = "T-2" })).ContainsKey("owner"));
            var notes = new List<string>();
            for (var turn = 0; turn < 6; turn++)
            {
                if ((await Call("check_inbox", As(manager, new())))["attention"] is not JsonObject item) break;
                notes.Add(item["x"]!.GetValue<string>());
                await Call("attention_respond", As(manager, new() { ["itemId"] = item["id"]!.DeepClone(), ["action"] = "dismiss" }));
            }
            Assert.Contains(notes, note => note.Contains("T-1 done"));
            Assert.Contains(notes, note => note.Contains("pc is free"));
            Assert.Contains(notes, note => note.Contains("review_ready"));

            // get_status lists the orders; a background detail read does not.
            var listed = (await Call("get_status", As(manager, new())))["orders"]!.AsArray();
            Assert.Equal([("o-1", 1L), ("o-2", 1L)], listed.Select(order => (order!["id"]!.GetValue<string>(), order["runs"]!.GetValue<long>())));
            Assert.False((await Call("get_status", As(manager, new() { ["attentionDetail"] = true }))).ContainsKey("orders"));
            Assert.Equal(claim["grants"]![0]!["generation"]!.GetValue<long>() + 1, (await Call("claim_acquire", As(next, new() { ["resourceId"] = "pc" })))["generation"]!.GetValue<long>());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_late_order_release_keeps_a_claim_taken_again_and_a_refused_notice_is_a_failed_run()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        var orders = new OrderOptions { Enabled = true };
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "core-orders-race-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(orders).AddSingleton(provider => new RelayApplication(provider.GetRequiredService<IGrainFactory>(), orders: orders))).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", true, ["repo"]);
            var hub = caller with { IsInternal = true };
            async Task<RelayReply> Raw(string tool, JsonObject args)
            {
                args["repositoryId"] = "repo"; if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
                return await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            }
            async Task<JsonObject> Call(string tool, JsonObject args)
            {
                var reply = await Raw(tool, args);
                Assert.True(reply.Ok, tool + ": " + reply.Code + " " + reply.DataJson);
                return JsonNode.Parse(reply.DataJson)!.AsObject();
            }
            // A refused item makes the board raise order_failed while it dispatches, so one pass may leave that new effect pending.
            async Task Dispatch(IRelayAggregateGrain grain)
            {
                for (var attempt = 0; attempt < 5; attempt++)
                    if ((await grain.ExecuteAsync(hub, Operations.Dispatch, "{}")).Ok) return;
                Assert.Fail("dispatch stayed pending");
            }
            var board = factory.GetGrain<ITaskBoardGrain>("repo/default");
            async Task Pump() { foreach (var grain in new IRelayAggregateGrain[] { board, factory.GetGrain<ILockGrain>("pc"), board, factory.GetGrain<IControlGrain>(AggregateKinds.ControlKey) }) await Dispatch(grain); }
            var manager = await Call("session_open", new() { ["role"] = "manager" });
            var worker = await Call("session_open", new() { ["role"] = "worker" });
            var other = await Call("session_open", new() { ["role"] = "worker" });
            JsonObject As(JsonObject session, JsonObject args) { args["sessionId"] = session["sessionId"]!.DeepClone(); args["sessionToken"] = session["sessionToken"]!.DeepClone(); return args; }
            JsonObject Workflow(string state) => new()
            {
                ["state"] = state, ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human-grant:1", ["restoration"] = "r", ["stopCondition"] = "s" },
                ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = state == "completed" ? "passed" : "not_run", ["evidenceRef"] = state == "completed" ? "artifact:1" : null }),
                ["blockers"] = new JsonArray()
            };
            await Call("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "pc", ["machineId"] = "pc" });
            await Call("task_update", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready"), ["attention"] = false }));
            await Call("task_update", As(worker, new() { ["taskId"] = "T-2", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready"), ["attention"] = false }));
            await Call("order_put", new()
            {
                ["orderId"] = "o-1", ["trigger"] = new JsonObject { ["on"] = "workflow_completed", ["taskId"] = "T-1" },
                ["actions"] = new JsonArray(new JsonObject { ["do"] = "release_claims" }, new JsonObject { ["do"] = "notify", ["sessionId"] = "no-such-session", ["note"] = "T-1 done" })
            });

            // The worker completes T-1 and at once claims T-2 with the same resource, before the pump delivers the order's release.
            await Call("task_claim", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray("pc"), ["leaseSeconds"] = 600, ["attention"] = false }));
            await Call("task_update", As(worker, new() { ["taskId"] = "T-1", ["expectedVersion"] = 2, ["workflow"] = Workflow("completed"), ["attention"] = false }));
            await Call("task_claim", As(worker, new() { ["taskId"] = "T-2", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray("pc"), ["leaseSeconds"] = 600, ["attention"] = false }));
            await Pump();
            await Pump();
            // The new claim survives: another session still cannot take pc, and T-2 is still the worker's.
            Assert.Equal(RelayErrorCodes.Busy, (await Raw("claim_acquire", As(other, new() { ["resourceId"] = "pc" }))).Code);
            Assert.Equal(worker["sessionId"]!.GetValue<string>(), (await Call("task_get", new() { ["taskId"] = "T-2" }))["owner"]!.GetValue<string>());
            Assert.False((await Call("task_get", new() { ["taskId"] = "T-1" })).ContainsKey("owner"));

            // The notice to an unknown session was refused: a failed run, logged and raised to the manager.
            var listed = Assert.Single((await Call("get_status", As(manager, new())))["orders"]!.AsArray())!.AsObject();
            Assert.Equal((1L, 1L), (listed["runs"]!.GetValue<long>(), listed["fails"]!.GetValue<long>()));
            Assert.Contains("refused", listed["err"]!.GetValue<string>());
            var item = (await Call("check_inbox", As(manager, new())))["attention"]!.AsObject();
            Assert.Equal("order_failed", item["k"]!.GetValue<string>());
            Assert.Contains("no-such-session", item["x"]!.GetValue<string>());
        }
        finally { await host.StopAsync(); }
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

        // An open item's row is back in range only when it was measured with enough samples.
        JsonObject Open(params object?[][] rows) => AnomalyMonitorGrain.Finding(AnalyticsQueryNames.TokensPerTask, new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"], rows, false, at), new AnomalyThreshold(2, 5), "all");
        Assert.True(Open(["all", 110.0, 100.0, 10L, 10L])["inRange"]!.GetValue<bool>());
        Assert.Null(Open(["all", 110.0, 100.0, 4L, 10L])["inRange"]);
        Assert.Null(Open(["all", 110.0, 100.0, 10L, 4L])["inRange"]);
        Assert.Null(Open()["inRange"]);
        Assert.Null(Open(["all", 300.0, 100.0, 10L, 10L])["inRange"]);

        // Closures that drop to half their baseline or less are an anomaly; the samples are the baseline's, since few closures is the signal.
        var dropped = Find(AnalyticsQueryNames.QualityGuardrails, ["closures", 4L, 10L, 4L, 10L], ["reopened", 1L, 1L, 1L, 1L])["anomaly"]!;
        Assert.Equal(("closures", 4.0, 10.0, 10L), (dropped["key"]!.GetValue<string>(), dropped["value"]!.GetValue<double>(), dropped["baseline"]!.GetValue<double>(), dropped["samples"]!.GetValue<long>()));
        Assert.Equal("closures", Find(AnalyticsQueryNames.QualityGuardrails, ["closures", 0L, 10L, 0L, 10L])["anomaly"]!["key"]!.GetValue<string>());
        Assert.Null(Find(AnalyticsQueryNames.QualityGuardrails, ["closures", 6L, 10L, 6L, 10L])["anomaly"]);
        Assert.Null(Find(AnalyticsQueryNames.QualityGuardrails, ["closures", 0L, 4L, 0L, 4L])["anomaly"]);
        Assert.Null(AnomalyMonitorGrain.Finding(AnalyticsQueryNames.QualityGuardrails, new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"], [["closures", 0L, 10L, 0L, 10L]], false, at), new AnomalyThreshold(2, 5, 0))["anomaly"]);
        // With only the drop check on, quality_guardrails is still checked.
        Assert.Contains(new AnomalyOptions { Thresholds = new Dictionary<string, AnomalyThreshold> { [AnalyticsQueryNames.QualityGuardrails] = new(0, 5) } }.Checks(), check => check.Query == AnalyticsQueryNames.QualityGuardrails);
    }

    [Fact]
    public async Task A_board_archive_sweep_that_fails_waits_before_running_again()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-29T00:00:00Z"));
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "core-archive-retry-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(new BoardOptions { Time = clock })
                .Configure<Orleans.Serialization.Configuration.TypeManifestOptions>(options => options.AllowAllTypes = true)).Build();
        await host.StartAsync();
        try
        {
            var storage = host.Services.GetRequiredKeyedService<Orleans.Storage.IGrainStorage>("relay");
            var id = GrainId.Create("cocern.board", "repo/default");
            // A board whose every write fails (one task is past the task byte limit) with an archive sweep due now.
            var board = new StateEnvelope<CoordinationDocument>
            {
                Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["tasks"] = new JsonObject { ["huge"] = new JsonObject { ["taskId"] = "huge", ["facts"] = new JsonObject { ["x"] = new string('x', RelayLimits.MaxTaskBytes) } } } }.ToJsonString() }
            };
            board.DueWork.Add(new(DueWorkKinds.TaskArchive, DueWorkKinds.TaskArchive, clock.GetUtcNow().AddMinutes(-1)));
            await storage.WriteStateAsync("board", id, new GrainState<StateEnvelope<CoordinationDocument>>(board));
            var dispatched = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<ITaskBoardGrain>("repo/default").ExecuteAsync(new RelayCaller("relay-dispatcher", false, [], true), Operations.Dispatch, "{}");
            Assert.True(dispatched.Ok, dispatched.Code);
            var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
            await storage.ReadStateAsync("board", id, stored);
            var due = Assert.Single(stored.State!.DueWork, work => work.Kind == DueWorkKinds.TaskArchive);
            Assert.Equal(clock.GetUtcNow().AddMinutes(RelayLimits.ArchiveRetryMinutes), due.DueAt);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_claim_must_take_the_resources_its_packet_declares_and_nothing_starts_while_another_session_holds_one()
    {
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "core-declared-resources-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var caller = new RelayCaller("alice", true, ["repo"]);
            async Task<RelayReply> Call(string tool, JsonObject args)
            {
                args["repositoryId"] = "repo"; if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
                return await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            }
            JsonObject Data(RelayReply reply) { Assert.True(reply.Ok, reply.Code + " " + reply.DataJson); return JsonNode.Parse(reply.DataJson)!.AsObject(); }
            var a = Data(await Call("session_open", new() { ["machineId"] = "pc" })); var b = Data(await Call("session_open", new() { ["machineId"] = "pc" }));
            JsonObject For(JsonObject session, string? taskId = null)
            {
                var args = new JsonObject { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone() };
                if (taskId is not null) args["taskId"] = taskId;
                return args;
            }
            Data(await Call("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "rig-1", ["aliases"] = new JsonArray("bench"), ["machineId"] = "pc" }));
            Data(await Call("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "rig-2", ["machineId"] = "pc" }));
            Data(await Call("standing_put", new() { ["expectedRevision"] = 0, ["settings"] = new JsonObject { ["requireResources"] = true } }));
            JsonObject Ready(params string[] resources) => new()
            {
                ["state"] = "ready", ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }), ["blockers"] = new JsonArray(),
                ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human", ["restoration"] = "r", ["stopCondition"] = "s", ["resources"] = new JsonArray(resources.Select(id => (JsonNode)id).ToArray()) }
            };
            foreach (var (id, resource) in new[] { ("t", "rig-1"), ("alias", "bench"), ("u", "rig-2") })
            {
                var create = For(a, id); create["expectedVersion"] = 0; create["workflow"] = Ready(resource);
                Data(await Call("task_update", create));
            }
            JsonObject Claim(JsonObject session, string taskId, params string[] resources)
            {
                var claim = For(session, taskId); claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray(resources.Select(id => (JsonNode)id).ToArray());
                return claim;
            }

            // The reviewer's sequence: a claim with resourceIds [] on a task declaring rig-1 is refused, so no action can begin unguarded.
            var bare = await Call("task_claim", Claim(a, "t"));
            Assert.Equal(RelayErrorCodes.ResourcesRequired, bare.Code);
            Assert.Equal(["rig-1"], JsonNode.Parse(bare.DataJson)!["missing"]!.AsArray().Select(name => name!.GetValue<string>()));
            var unclaimed = For(a, "t"); unclaimed["expectedVersion"] = 1; unclaimed["actionId"] = "x"; unclaimed["inputFingerprint"] = "f";
            Assert.Equal(RelayErrorCodes.ClaimRequired, (await Call("action_begin", unclaimed)).Code);
            // While another session holds rig-1, the claim that names it is refused and rolled back.
            var held = For(b); held["resourceId"] = "rig-1";
            var heldGrant = Data(await Call("claim_acquire", held));
            Assert.Equal(RelayErrorCodes.Busy, (await Call("task_claim", Claim(a, "t", "rig-1"))).Code);
            var release = For(b); release["resourceId"] = "rig-1"; release["generation"] = heldGrant["generation"]!.DeepClone();
            Data(await Call("claim_release", release));
            // With the declared resource named, the claim takes it and the action begins guarded by it. (The rolled-back claim moved the
            // task to version 3.)
            var named = Claim(a, "t", "rig-1"); named["expectedVersion"] = 3;
            Data(await Call("task_claim", named));
            var begin = For(a, "t"); begin["expectedVersion"] = 4; begin["actionId"] = "x"; begin["inputFingerprint"] = "f";
            Assert.True(Data(await Call("action_begin", begin))["replayAllowed"]!.GetValue<bool>());
            // A declared alias resolves by the session's machine, as offers resolve it: "bench" on pc is rig-1, which A now holds.
            Assert.Equal(RelayErrorCodes.ResourcesRequired, (await Call("task_claim", Claim(b, "alias"))).Code);
            Assert.Equal(RelayErrorCodes.Busy, (await Call("task_claim", Claim(b, "alias", "rig-1"))).Code);

            // action_begin checks the declared resources are held by this session: here A gave rig-2 up and B took it.
            var claimed = Data(await Call("task_claim", Claim(a, "u", "rig-2")));
            var giveUp = For(a); giveUp["resourceId"] = "rig-2"; giveUp["generation"] = claimed["grants"]![0]!["generation"]!.DeepClone();
            Data(await Call("claim_release", giveUp));
            var taken = For(b); taken["resourceId"] = "rig-2";
            var takenGrant = Data(await Call("claim_acquire", taken));
            var start = For(a, "u"); start["expectedVersion"] = 2; start["actionId"] = "y"; start["inputFingerprint"] = "f";
            Assert.Equal(RelayErrorCodes.Busy, (await Call("action_begin", start)).Code);
            var back = For(b); back["resourceId"] = "rig-2"; back["generation"] = takenGrant["generation"]!.DeepClone();
            Data(await Call("claim_release", back));
            start["requestId"] = Guid.NewGuid().ToString("N");
            Assert.Equal(RelayErrorCodes.ClaimRequired, (await Call("action_begin", start)).Code);
            var read = For(a, "u"); read["section"] = "actions";
            Assert.Empty(Data(await Call("task_get", read))["items"]!.AsArray());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task An_archive_sweep_on_another_board_closes_the_archived_tasks_attention_at_its_target_session()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "core-archive-close-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore().AddSingleton(new BoardOptions { Time = clock })
                .Configure<Orleans.Serialization.Configuration.TypeManifestOptions>(options => options.AllowAllTypes = true)).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var storage = host.Services.GetRequiredKeyedService<Orleans.Storage.IGrainStorage>("relay");
            var caller = new RelayCaller("alice", true, ["repo"]);
            var opened = await application.ExecuteAsync(caller, "session_open", JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open" }));
            var sessionId = JsonNode.Parse(opened.DataJson)!["sessionId"]!.GetValue<string>();
            // The target session holds an item about task t on board "side".
            var raise = new JsonObject { ["repositoryId"] = "repo", ["kind"] = AttentionKinds.ReviewReady, ["taskId"] = "t", ["boardId"] = "side", ["revision"] = 1, ["note"] = "review t", ["expiryDays"] = 30 };
            Assert.True((await factory.GetGrain<ISessionGrain>(sessionId).ExecuteAsync(caller with { IsInternal = true }, Operations.AttentionRaise, raise.ToJsonString())).Ok);
            async Task<JsonObject> Attention()
            {
                var stored = new GrainState<StateEnvelope<CoordinationDocument>>(new());
                await storage.ReadStateAsync("session", GrainId.Create("cocern.session", sessionId), stored);
                return JsonNode.Parse(stored.State!.Value.Json)!["attention"] as JsonObject ?? new JsonObject();
            }
            Assert.Contains((await Attention()).Select(entry => entry.Value), item => item!["taskId"]!.GetValue<string>() == "t");
            // On board "side", t finished long ago with that session as its target; its sweep is due.
            var workflow = new JsonObject
            {
                ["state"] = "completed", ["targetSessionId"] = sessionId,
                ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human", ["restoration"] = "r", ["stopCondition"] = "s" },
                ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "passed", ["evidenceRef"] = "artifact:1" }), ["blockers"] = new JsonArray()
            };
            var board = new StateEnvelope<CoordinationDocument>
            {
                Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["taskRevision"] = 1, ["tasks"] = new JsonObject { ["t"] = new JsonObject
                {
                    ["taskId"] = "t", ["version"] = 2, ["changeRevision"] = 1, ["actions"] = new JsonObject(), ["workflow"] = workflow,
                    ["completedAt"] = clock.GetUtcNow().AddDays(-RelayLimits.DefaultArchiveAfterDays - 1).ToString("O")
                } } }.ToJsonString() }
            };
            board.DueWork.Add(new(DueWorkKinds.TaskArchive, DueWorkKinds.TaskArchive, clock.GetUtcNow().AddMinutes(-1)));
            await storage.WriteStateAsync("board", GrainId.Create("cocern.board", "repo/side"), new GrainState<StateEnvelope<CoordinationDocument>>(board));
            var dispatched = await factory.GetGrain<ITaskBoardGrain>("repo/side").ExecuteAsync(new RelayCaller("relay-dispatcher", false, [], true), Operations.Dispatch, "{}");
            Assert.True(dispatched.Ok, dispatched.Code);
            // The sweep archived t, and its close, stamped with the sweep's authority and naming board "side", reached the session.
            var swept = new GrainState<StateEnvelope<CoordinationDocument>>(new());
            await storage.ReadStateAsync("board", GrainId.Create("cocern.board", "repo/side"), swept);
            Assert.False(JsonNode.Parse(swept.State!.Value.Json)!["tasks"]!.AsObject().ContainsKey("t"));
            Assert.Empty(swept.State.Effects);
            Assert.DoesNotContain((await Attention()).Select(entry => entry.Value), item => item!["taskId"]!.GetValue<string>() == "t");
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
        /// <summary>When set, every query waits for it, like a slow DuckDB query.</summary>
        public TaskCompletionSource? Gate { get; init; }
        public IReadOnlyList<string> QueryNames => AnalyticsQueryNames.All;
        public async Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default)
        {
            lock (Queries) Queries.Add(name);
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"], Rows.TryGetValue(name, out var rows) ? rows : [], false, clock.GetUtcNow());
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

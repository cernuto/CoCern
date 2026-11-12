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

/// <summary>
/// A session's label should match its chat's real title, which can change after the session opened. The owner renames it with
/// <c>session_bind</c>'s optional <c>label</c>; the session token proves ownership, and the directory, worker_list and dashboard follow.
/// </summary>
public sealed class SessionRenameTests
{
    [Fact]
    public async Task The_label_tool_only_renames_its_authenticated_session_and_old_replays_cannot_undo_it()
    {
        using var host = await StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", false, ["repo"]);
            async Task<RelayReply> Call(string tool, object args) => await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            async Task<(string Id, string Token)> Open(string requestId, string label)
            {
                var data = JsonNode.Parse((await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId, label })).DataJson)!;
                return (data["sessionId"]!.GetValue<string>(), data["sessionToken"]!.GetValue<string>());
            }
            var own = await Open("label-own", "Original");
            var peer = await Open("label-peer", "Peer");
            async Task<string> Listed((string Id, string Token) session)
            {
                Assert.True((await factory.GetGrain<ISessionGrain>(session.Id).ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
                var workers = JsonNode.Parse((await Call(RelayTools.WorkerList, new { repositoryId = "repo" })).DataJson)!["items"]!.AsArray();
                return workers.Single(worker => worker!["sessionId"]!.GetValue<string>() == session.Id)!["label"]!.GetValue<string>();
            }
            var first = new { repositoryId = "repo", requestId = "label-first", sessionId = own.Id, sessionToken = own.Token, label = "First title" };
            var renamed = await Call(RelayTools.SessionLabelSet, first);
            Assert.True(renamed.Ok, renamed.Code);
            Assert.Equal("First title", await Listed(own));
            Assert.Equal(RelayErrorCodes.SessionDenied, (await Call(RelayTools.SessionLabelSet, new { repositoryId = "repo", requestId = "label-wrong-token", sessionId = own.Id, sessionToken = peer.Token, label = "Hijacked" })).Code);
            Assert.Equal(RelayErrorCodes.SessionDenied, (await Call(RelayTools.SessionLabelSet, new { repositoryId = "repo", requestId = "label-wrong-actor", sessionId = peer.Id, sessionToken = peer.Token, actingSessionId = own.Id, actingSessionToken = own.Token, label = "Hijacked" })).Code);
            Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(RelayTools.SessionLabelSet, new { repositoryId = "repo", requestId = "label-target", sessionId = own.Id, sessionToken = own.Token, targetSessionId = peer.Id, label = "Hijacked" })).Code);
            Assert.Equal(RelayErrorCodes.SessionDenied, (await application.ExecuteAsync(caller with { PrincipalId = "bob" }, RelayTools.SessionLabelSet, JsonSerializer.SerializeToElement(first))).Code);
            Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(RelayTools.SessionLabelSet, new { repositoryId = "repo", requestId = "label-invalid", sessionId = own.Id, sessionToken = own.Token, label = "bad\nlabel" })).Code);
            Assert.True((await Call(RelayTools.SessionLabelSet, new { repositoryId = "repo", requestId = "label-second", sessionId = own.Id, sessionToken = own.Token, label = "Current title" })).Ok);
            Assert.Equal(renamed.DataJson, (await Call(RelayTools.SessionLabelSet, first)).DataJson);
            Assert.Equal("Current title", await Listed(own));
            Assert.Equal("Peer", await Listed(peer));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task The_owner_renames_its_session_with_session_bind_and_the_directory_follows()
    {
        using var host = await StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", false, ["repo"]);
            async Task<RelayReply> Call(string tool, object args) => await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            var opened = JsonNode.Parse((await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-1", label = "CoCern smoke test" })).DataJson)!;
            var (id, token) = (opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>());
            async Task<string?> Listed()
            {
                Assert.True((await factory.GetGrain<ISessionGrain>(id).ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
                var workers = JsonNode.Parse((await Call(RelayTools.WorkerList, new { repositoryId = "repo" })).DataJson)!["items"]!.AsArray();
                return workers.Single(worker => worker!["sessionId"]!.GetValue<string>() == id)!["label"]?.GetValue<string>();
            }

            var renamed = await Call(RelayTools.SessionBind, new { repositoryId = "repo", requestId = "bind-1", sessionId = id, sessionToken = token, expectedVersion = 0, binding = new { }, label = "Run a smoke test" });
            Assert.True(renamed.Ok, renamed.Code + " " + renamed.DataJson);
            Assert.Equal("Run a smoke test", await Listed());

            // A bind that leaves label out keeps the name.
            Assert.True((await Call(RelayTools.SessionBind, new { repositoryId = "repo", requestId = "bind-2", sessionId = id, sessionToken = token, expectedVersion = 1, binding = new { } })).Ok);
            Assert.Equal("Run a smoke test", await Listed());

            // Only the session's own token renames it, and a label must be a valid identifier.
            Assert.False((await Call(RelayTools.SessionBind, new { repositoryId = "repo", requestId = "bind-3", sessionId = id, sessionToken = "not-the-token", expectedVersion = 2, binding = new { }, label = "Hijacked" })).Ok);
            Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(RelayTools.SessionBind, new { repositoryId = "repo", requestId = "bind-4", sessionId = id, sessionToken = token, expectedVersion = 2, binding = new { }, label = "bad\nlabel" })).Code);
            Assert.Equal("Run a smoke test", await Listed());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_metadata_only_bind_keeps_the_binding_and_the_owner_can_report_its_machine()
    {
        using var host = await StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", false, ["repo"]);
            async Task<RelayReply> Call(string tool, object args) => await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            // A session opened without a machine, as older clients did.
            var opened = JsonNode.Parse((await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-1", label = "Chat" })).DataJson)!;
            var (id, token) = (opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>());
            async Task<JsonNode> Listed()
            {
                Assert.True((await factory.GetGrain<ISessionGrain>(id).ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
                var workers = JsonNode.Parse((await Call(RelayTools.WorkerList, new { repositoryId = "repo", includeStale = true })).DataJson)!["items"]!.AsArray();
                return workers.Single(worker => worker!["sessionId"]!.GetValue<string>() == id)!;
            }

            Assert.True((await Call(RelayTools.SessionBind, new { repositoryId = "repo", requestId = "bind-1", sessionId = id, sessionToken = token, expectedVersion = 0, binding = new { roslyn = "codelens" } })).Ok);
            var metadata = new { repositoryId = "repo", requestId = "bind-2", sessionId = id, sessionToken = token, expectedVersion = 1, machineId = "16XAURORA", label = "Renamed chat" };
            var updated = await Call(RelayTools.SessionBind, metadata);
            Assert.True(updated.Ok, updated.Code + " " + updated.DataJson);
            var listed = await Listed();
            Assert.Equal("16XAURORA", listed["machineId"]!.GetValue<string>());
            Assert.Equal("Renamed chat", listed["label"]!.GetValue<string>());
            Assert.Equal("codelens", Assert.IsType<JsonObject>(listed["binding"])["roslyn"]!.GetValue<string>());

            // A retry of the same request is the same answer, and another token cannot move the session to another machine.
            Assert.Equal(updated.DataJson, (await Call(RelayTools.SessionBind, metadata)).DataJson);
            Assert.False((await Call(RelayTools.SessionBind, new { repositoryId = "repo", requestId = "bind-3", sessionId = id, sessionToken = "not-the-token", expectedVersion = 2, machineId = "ELSEWHERE" })).Ok);
            Assert.Equal("16XAURORA", (await Listed())["machineId"]!.GetValue<string>());
        }
        finally { await host.StopAsync(); }
    }

    private static async Task<IHost> StartAsync()
    {
        var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "session-rename-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
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

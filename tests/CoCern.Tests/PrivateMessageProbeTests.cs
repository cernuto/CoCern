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
/// A private send checks the caller's access with a worker_list probe. With a large directory a 50-entry probe outgrew the reply bound
/// and every private message failed PayloadTooLarge (seen live at 108 sessions); the probe now reads one entry.
/// </summary>
public sealed class PrivateMessageProbeTests
{
    [Fact]
    public async Task Private_messages_are_delivered_however_large_the_worker_directory_grows()
    {
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "private-probe-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var caller = new RelayCaller("alice", false, ["repo"]);
            async Task<RelayReply> Call(string tool, object args) => await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            JsonNode Open(RelayReply reply) { Assert.True(reply.Ok, reply.Code + " " + reply.DataJson); return JsonNode.Parse(reply.DataJson)!; }

            // Long labels and machine names make 50 directory entries far larger than one reply may be; whether worker_list itself trims
            // such a page or refuses it, the send's access probe must not depend on it.
            var padding = new string('x', 200);
            for (var i = 0; i < 70; i++)
                Open(await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-" + i, label = $"chat {i} {padding}", machineId = $"pc-{i}-{padding}" }));

            var sender = Open(await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-sender", label = "sender" }));
            var recipient = Open(await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-recipient", label = "recipient" }));
            var sent = await Call(RelayTools.SendMessage, new { repositoryId = "repo", requestId = "send-1", sessionId = sender["sessionId"]!.GetValue<string>(), sessionToken = sender["sessionToken"]!.GetValue<string>(), recipientSessionId = recipient["sessionId"]!.GetValue<string>(), text = "hello" });
            Assert.True(sent.Ok, sent.Code + " " + sent.DataJson);

            Assert.True((await factory.GetGrain<ISessionGrain>(sender["sessionId"]!.GetValue<string>()).ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
            var inbox = Open(await Call(RelayTools.CheckInbox, new { repositoryId = "repo", requestId = "read-1", sessionId = recipient["sessionId"]!.GetValue<string>(), sessionToken = recipient["sessionToken"]!.GetValue<string>() }));
            Assert.Equal("hello", inbox["items"]!.AsArray().Single()!["text"]!.GetValue<string>());

            // The probe still checks access: a caller without the repository is refused before anything is stored.
            var outsider = new RelayCaller("mallory", false, ["other"]);
            Assert.False((await application.ExecuteAsync(outsider, RelayTools.SendMessage, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "send-2", sessionId = sender["sessionId"]!.GetValue<string>(), sessionToken = sender["sessionToken"]!.GetValue<string>(), recipientSessionId = recipient["sessionId"]!.GetValue<string>(), text = "intrusion" }))).Ok);
        }
        finally { await host.StopAsync(); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

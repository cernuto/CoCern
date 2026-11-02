using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A chat keeps one session: the guidance every agent reads says to reuse it and to reopen with the same requestId, which the hub
/// answers with the same session. Telling agents to open a fresh one left several identical sessions per chat on the dashboard.
/// </summary>
public sealed class SessionGuidanceTests
{
    [Fact]
    public void Session_open_guidance_asks_for_one_session_per_chat_reopened_with_the_same_requestId()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.SessionOpen, out var tool));
        var properties = RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties");
        var requestId = properties.GetProperty("requestId").GetProperty("description").GetString()!;
        var label = properties.GetProperty("label").GetProperty("description").GetString()!;
        foreach (var text in new[] { tool.Description, requestId, label, AdapterInstructions.Server })
            Assert.DoesNotContain("fresh", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same requestId", tool.Description, StringComparison.Ordinal);
        Assert.Contains("same session", requestId, StringComparison.Ordinal);
        Assert.Contains("one session per chat", AdapterInstructions.Server, StringComparison.Ordinal);
        Assert.Contains("same requestId", AdapterInstructions.Server, StringComparison.Ordinal);
        // Other tools keep the shared envelope text.
        Assert.True(RelayToolCatalog.TryGet(RelayTools.SendMessage, out var send));
        Assert.Equal("Stable request ID; reuse on retry.", RelayToolCatalog.ForwardingSchema(send).GetProperty("properties").GetProperty("requestId").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Retrying_session_open_with_the_same_requestId_and_arguments_returns_the_same_session()
    {
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "session-guidance-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var caller = new RelayCaller("alice", false, ["repo"]);
            Task<RelayReply> Open(string requestId, string label) =>
                application.ExecuteAsync(caller, RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId, label, role = "worker" }));
            var first = JsonNode.Parse((await Open("open-chat-1", "Chat one")).DataJson)!;
            var again = await Open("open-chat-1", "Chat one");
            Assert.True(again.Ok, again.Code);
            Assert.Equal(first["sessionId"]!.GetValue<string>(), JsonNode.Parse(again.DataJson)!["sessionId"]!.GetValue<string>());
            Assert.Equal(first["sessionToken"]!.GetValue<string>(), JsonNode.Parse(again.DataJson)!["sessionToken"]!.GetValue<string>());
            // Changed arguments under the same requestId are refused rather than silently opening a second session.
            Assert.Equal(RelayErrorCodes.RequestConflict, (await Open("open-chat-1", "Renamed")).Code);
            var other = JsonNode.Parse((await Open("open-chat-2", "Chat two")).DataJson)!;
            Assert.NotEqual(first["sessionId"]!.GetValue<string>(), other["sessionId"]!.GetValue<string>());
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

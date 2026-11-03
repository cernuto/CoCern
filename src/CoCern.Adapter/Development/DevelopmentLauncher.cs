using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;

namespace CoCern.Adapter.Development;

/// <summary>A stable development endpoint whose private adapter MCP connection is replaced at debug startup.</summary>
internal static class DevelopmentLauncher
{
    internal static ListToolsResult AdvertiseGuidance(ListToolsResult result, string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions) || result.Tools.Count == 0) return result;
        // Initialization instructions cannot change on an existing upstream connection. Deliver current guidance with refreshed tool metadata instead.
        var copy = JsonSerializer.Deserialize<ListToolsResult>(JsonSerializer.Serialize(result))!;
        var entry = copy.Tools.FirstOrDefault(tool => tool.Name == "session_open") ?? copy.Tools[0];
        entry.Description = $"{entry.Description}\n\nCurrent CoCern server guidance:\n{instructions}";
        return copy;
    }
    public static async Task<int> RunAsync(string root, string dotnet, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        await using var connections = new DevelopmentConnections(root, async (manifest, token) =>
        {
            var transport = new StdioClientTransport(new()
            {
                Command = dotnet, Arguments = [AdapterDeployment.AdapterPath(root, manifest)], Name = "CoCern development backend",
                InheritEnvironmentVariables = true, ShutdownTimeout = TimeSpan.FromSeconds(2),
                EnvironmentVariables = new Dictionary<string, string?> { ["COCERN_DEVELOPMENT_GENERATION"] = manifest.Generation },
                StandardErrorLines = _ => { } // Child diagnostics can contain handles; only fixed launcher messages reach the client.
            });
            return new Backend(await McpClient.CreateAsync(transport, cancellationToken: token));
        }, Console.Error.WriteLine);
        await connections.RefreshAsync(cancellationToken);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        McpServer? upstream = null;
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "CoCern development launcher", Version = "1.0" };
            options.ServerInstructions = "CoCern development endpoint. Follow the current server guidance included in the session_open tool description; tool metadata refreshes when the development adapter changes.";
            options.Capabilities = new() { Tools = new() { ListChanged = true } };
        }).WithStdioServerTransport()
            .WithListToolsHandler((request, token) =>
            {
                upstream = request.Server;
                return connections.UseAsync(async (backend, ct) => AdvertiseGuidance(await backend.ListToolsAsync(request.Params ?? new(), ct), backend.Instructions), token);
            })
            .WithCallToolHandler((request, token) =>
            {
                upstream = request.Server;
                return connections.UseAsync((backend, ct) => backend.CallToolAsync(request.Params!, ct), token);
            });
        using var host = builder.Build();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        var watching = WatchAsync(connections, () => upstream, stopping.Token);
        try { await host.RunAsync(cancellationToken); }
        finally
        {
            await stopping.CancelAsync();
            await watching;
        }
        return 0;
    }

    private static async Task WatchAsync(DevelopmentConnections connections, Func<McpServer?> upstream, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                if (await connections.RefreshAsync(cancellationToken) && upstream() is { } server)
                {
                    try { await server.SendNotificationAsync("notifications/tools/list_changed", cancellationToken); }
                    catch (Exception error) when (error is InvalidOperationException or IOException) { }
                }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private sealed class Backend(McpClient client) : IDevelopmentBackend
    {
        public bool IsClosed => client.Completion.IsCompleted;
        public string? Instructions => client.ServerInstructions;
        public ValueTask<ListToolsResult> ListToolsAsync(ListToolsRequestParams request, CancellationToken cancellationToken) => client.ListToolsAsync(request, cancellationToken);
        public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken) => client.CallToolAsync(request, cancellationToken);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

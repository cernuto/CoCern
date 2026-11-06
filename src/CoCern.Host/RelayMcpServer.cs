using System.Text.Json;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Adapter;

namespace CoCern.Host;

/// <summary>Remote MCP surface: the same catalog and application entry point as REST, shaped as MCP tool results.</summary>
internal static class RelayMcpServer
{
    public static IServiceCollection AddRelayMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddHostedService<Dashboard.McpProtocolDiagnosticsService>();
        services.AddMcpServer(options => options.ServerInfo = new() { Name = "CoCern", Version = "1.0.0" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithCoCernWorkflow()
            .WithListToolsHandler((request, _) =>
            {
                var context = HttpContextOf(request.Server.Services!);
                return ValueTask.FromResult(new ListToolsResult { Tools = ToolResults.Tools(context).ToList() });
            })
            .WithCallToolHandler(async (request, cancellationToken) =>
            {
                var context = HttpContextOf(request.Server.Services!);
                McpProtocolDiagnostics.Bind(request.Params?.Meta);
                var reply = await CallAsync(context, request.Params, cancellationToken);
                return McpReadResults.FromHost(request.Params?.Name ?? "", reply.Ok, reply.Code, reply.DataJson, reply.Version);
            });
        return services;
    }

    private static async Task<RelayReply> CallAsync(HttpContext context, CallToolRequestParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null || !RelayToolCatalog.TryGet(parameters.Name, out _)) return ToolResults.Failure(RelayErrorCodes.UnknownTool);
        var application = context.RequestServices.GetRequiredService<IRelayApplication>();
        var arguments = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>());
        try { return ToolResults.Bound(await application.ExecuteAsync(RelayAuthentication.Caller(context.User), parameters.Name, arguments, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return ToolResults.Failure(RelayErrorCodes.InternalError); }
    }

    private static HttpContext HttpContextOf(IServiceProvider services) => services.GetRequiredService<IHttpContextAccessor>().HttpContext!;
}

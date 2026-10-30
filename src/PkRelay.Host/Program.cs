using System.Text.Json;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Orleans.Hosting;
using PkRelay.Core;
using PkRelay.Host;
using PkRelay.Storage;
using Scalar.AspNetCore;
using Serilog;
using System.Net;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5271");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32768);
        builder.Services.AddSerilog(configuration => configuration.MinimumLevel.Information().MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning).MinimumLevel.Override("Orleans", Serilog.Events.LogEventLevel.Warning).MinimumLevel.Override("ModelContextProtocol", Serilog.Events.LogEventLevel.Warning).WriteTo.Console());
        builder.Host.ConfigureServices((context, services) => services.AddRelayAuthentication(context.Configuration));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSignalR(options => { options.EnableDetailedErrors = false; options.MaximumReceiveMessageSize = 32768; });
        builder.Services.AddSingleton<ICoordinationNotifier, CoordinationNotifier>();
        builder.Services.AddPkRelayCore();
        builder.UseOrleans(silo => silo.UseLocalhostClustering(siloPort: 21111, gatewayPort: 31111, serviceId: "PkRelay", clusterId: "PkRelay")
            .AddPkRelaySqlite(Path.Combine(builder.Environment.ContentRootPath, "data", "relay.db")));
        // Resolve final configuration through DI so application-factory and hosting overrides apply before any store/listener opens.
        builder.Services.Replace(ServiceDescriptor.Singleton<SqliteStore>(services => new SqliteStore(services.GetRequiredService<IConfiguration>()["Relay:DatabasePath"] ?? Path.Combine(services.GetRequiredService<IHostEnvironment>().ContentRootPath, "data", "relay.db"))));
        builder.Services.AddOptions<EndpointOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.SiloPort = configuration.GetValue("Relay:SiloPort", 21111);
            options.GatewayPort = configuration.GetValue("Relay:GatewayPort", 31111);
            options.AdvertisedIPAddress = IPAddress.Loopback;
            options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Loopback, options.SiloPort);
            options.GatewayListeningEndpoint = options.GatewayPort == 0 ? null : new IPEndPoint(IPAddress.Loopback, options.GatewayPort);
        });
        builder.Services.AddOptions<DevelopmentClusterMembershipOptions>().Configure<IConfiguration>((options, configuration) => options.PrimarySiloEndpoint = new IPEndPoint(IPAddress.Loopback, configuration.GetValue("Relay:SiloPort", 21111)));
        builder.Services.AddOptions<ClusterOptions>().Configure<IConfiguration>((options, configuration) => { options.ServiceId = "PkRelay"; options.ClusterId = configuration["Relay:ClusterId"] ?? "PkRelay"; });
        builder.Services.AddHostedService<DueWorkDispatcher>();
        builder.Services.AddOpenApi();
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "PkRelay", Version = "1.0.0" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((request, _) =>
            {
                var context = request.Server.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                return ValueTask.FromResult(new ListToolsResult { Tools = ToolCatalog.Tools(RelayAuthentication.Caller(context.User).IsAdministrator).ToList() });
            })
            .WithCallToolHandler(async (request, cancellationToken) =>
            {
                var context = request.Server.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                var application = context.RequestServices.GetRequiredService<IRelayApplication>();
                var parameters = request.Params;
                RelayReply reply;
                if (parameters is null || !ToolCatalog.Contains(parameters.Name)) reply = new(false, "UnknownTool", "{}", 0);
                else
                {
                    var arguments = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>());
                    try { reply = ToolCatalog.Bound(await application.ExecuteAsync(RelayAuthentication.Caller(context.User), parameters.Name, arguments, cancellationToken)); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { reply = new(false, "InternalError", "{}", 0); }
                }
                return new CallToolResult { IsError = !reply.Ok, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(reply, ToolCatalog.JsonOptions) }] };
            });

        var app = builder.Build();
        app.UseSerilogRequestLogging(options => options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms");
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (BadHttpRequestException exception) { if (!context.Response.HasStarted) { context.Response.StatusCode = exception.StatusCode; await context.Response.WriteAsJsonAsync(new { ok = false, code = exception.StatusCode == 413 ? "PayloadTooLarge" : "InvalidArguments" }); } }
            catch (Exception) { if (!context.Response.HasStarted) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { ok = false, code = "InternalError" }); } }
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp") || context.Request.Path.StartsWithSegments("/hubs")) context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method == "POST" && (context.Request.Path.StartsWithSegments("/api/tools") || context.Request.Path.StartsWithSegments("/mcp")))
            {
                if (context.Request.ContentLength > 32768) { context.Response.StatusCode = 413; await context.Response.WriteAsJsonAsync(new { ok = false, code = "PayloadTooLarge" }); return; }
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                while (true)
                {
                    var read = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
                    if (read == 0) break;
                    if (buffer.Length + read > 32768) { context.Response.StatusCode = 413; await context.Response.WriteAsJsonAsync(new { ok = false, code = "PayloadTooLarge" }); return; }
                    buffer.Write(bytes, 0, read);
                }
                buffer.Position = 0;
                context.Request.Body = buffer;
                await next(context);
                return;
            }
            await next(context);
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/tools", (HttpContext context) => Results.Ok(ToolCatalog.Tools(RelayAuthentication.Caller(context.User).IsAdministrator))).RequireAuthorization();
        app.MapPost("/api/tools/{tool}", ExecuteTool).RequireAuthorization()
            .Accepts<CoordinationRequest>("application/json").Produces<RelayReply>().Produces<RelayReply>(400).Produces(401).Produces<RelayReply>(403).Produces<RelayReply>(404).Produces<RelayReply>(409).Produces<RelayReply>(413)
            .WithDescription("Authenticated repository-scoped coordination. Arguments are the raw tool JSON object. Inspect GET /api/tools for the fixed catalog and required fields; actor identity always comes from authentication. dataJson is a JSON string containing structured domain data.");
        app.MapHub<CoordinationHub>("/hubs/coordination").RequireAuthorization();
        app.MapMcp("/mcp").RequireAuthorization();
        app.MapOpenApi();
        app.MapScalarApiReference("/scalar", options => options.WithTitle("PkRelay coordination API"));
        var authority = builder.Configuration["Relay:OAuth:Authority"];
        var resource = builder.Configuration["Relay:OAuth:Resource"];
        if (authority is not null && Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) && resourceUri.Scheme == "https")
            app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new { resource = resourceUri.AbsoluteUri, authorization_servers = new[] { authority }, scopes_supported = builder.Configuration.GetSection("Relay:OAuth:RequiredScopes").Get<string[]>() ?? ["relay"], bearer_methods_supported = new[] { "header" }, resource_name = "PkRelay coordination" }));
        await app.RunAsync();
    }

    private static async Task<IResult> ExecuteTool(string tool, HttpContext context, IRelayApplication application)
    {
        if (!ToolCatalog.Contains(tool)) return Results.Json(new RelayReply(false, "UnknownTool", "{}", 0), statusCode: 404);
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 32 }, context.RequestAborted);
            var reply = ToolCatalog.Bound(await application.ExecuteAsync(RelayAuthentication.Caller(context.User), tool, document.RootElement, context.RequestAborted));
            return Results.Json(reply, statusCode: ToolCatalog.Status(reply));
        }
        catch (JsonException) { return Results.Json(new RelayReply(false, "InvalidArguments", "{}", 0), statusCode: 400); }
    }
}

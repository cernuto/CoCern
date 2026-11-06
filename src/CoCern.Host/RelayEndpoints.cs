using System.Net;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Scalar.AspNetCore;
using Serilog;

namespace CoCern.Host;

using Dashboard;

public static class RelayEndpoints
{
    public static WebApplication UseRelayPipeline(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            // Hosts keep the static logger unchanged; request events must use this host's sink.
            options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
        });
        app.UseRelayErrorEnvelope();
        app.UseDashboardBoundary();
        app.UseLocalApiReference();
        app.UseBlazorFrameworkFiles("/dashboard");
        // RCL modules resolve relative to the dashboard's base URI. Their manifest paths
        // remain under _content, so expose that public asset subtree at the same base.
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/dashboard/_content"), branch =>
            branch.UseStaticFiles(new StaticFileOptions { RequestPath = "/dashboard" }));
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<ConnectionTimingMiddleware>();
        app.UseBoundedToolBodies();
        return app;
    }

    public static WebApplication MapRelayEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/tools", (HttpContext context) => Results.Ok(ToolResults.Tools(context))).RequireAuthorization();
        app.MapPost("/api/tools/{tool}", ExecuteToolAsync).RequireAuthorization()
            .Accepts<CoordinationRequest>("application/json").Produces<RelayReply>().Produces<RelayReply>(400).Produces(401).Produces<RelayReply>(403).Produces<RelayReply>(404).Produces<RelayReply>(409).Produces<RelayReply>(413)
            .WithDescription("Authenticated repository-scoped coordination. Arguments are the raw tool JSON object. Inspect GET /api/tools for the fixed catalog and required fields; actor identity always comes from authentication. dataJson is a JSON string containing structured domain data.");
        app.MapDiscoveryEndpoints();
        app.MapJoinEndpoints();
        app.MapRelayDashboard();
        app.MapConnectionDiagnostics();
        app.MapFallbackToFile("/dashboard/{*path:nonfile}", "dashboard/index.html");
        app.MapHub<CoordinationHub>("/" + HubEvents.Path).RequireAuthorization();
        app.MapMcp("/mcp").RequireAuthorization();
        app.MapOpenApi();
        app.MapScalarApiReference("/scalar", options => options.WithTitle("CoCern coordination API"));
        app.MapProtectedResourceMetadata();
        return app;
    }

    /// <summary>The API reference describes every route, so only this PC may read it; a connection without an IP (in-process, pipes) is local.</summary>
    private static void UseLocalApiReference(this WebApplication app) => app.Use((context, next) =>
    {
        if ((context.Request.Path.StartsWithSegments("/openapi") || context.Request.Path.StartsWithSegments("/scalar"))
            && context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return next(context);
    });

    private static async Task<IResult> ExecuteToolAsync(string tool, HttpContext context, IRelayApplication application)
    {
        if (!RelayToolCatalog.TryGet(tool, out _)) return Results.Json(ToolResults.Failure(RelayErrorCodes.UnknownTool), statusCode: StatusCodes.Status404NotFound);
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 32 }, context.RequestAborted);
            var reply = ToolResults.Bound(await application.ExecuteAsync(RelayAuthentication.Caller(context.User), tool, document.RootElement, context.RequestAborted));
            return Results.Json(reply, statusCode: ToolResults.Status(reply));
        }
        catch (JsonException) { return Results.Json(ToolResults.Failure(RelayErrorCodes.InvalidArguments), statusCode: StatusCodes.Status400BadRequest); }
    }

    /// <summary>RFC 9728 metadata, published only when an HTTPS resource and external issuer are configured.</summary>
    private static void MapProtectedResourceMetadata(this WebApplication app)
    {
        var configuration = app.Configuration;
        var authority = configuration["Relay:OAuth:Authority"];
        if (authority is null || !Uri.TryCreate(configuration["Relay:OAuth:Resource"], UriKind.Absolute, out var resource) || resource.Scheme != "https") return;
        var scopes = configuration.GetSection("Relay:OAuth:RequiredScopes").Get<string[]>() ?? ["relay"];
        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new
        {
            resource = resource.AbsoluteUri,
            authorization_servers = new[] { authority },
            scopes_supported = scopes,
            bearer_methods_supported = new[] { "header" },
            resource_name = "CoCern coordination"
        }));
    }
}

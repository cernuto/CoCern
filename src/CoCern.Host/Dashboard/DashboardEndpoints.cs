using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoCern.Host.Dashboard;

public static class DashboardEndpoints
{
    private const string LoginPolicy = "dashboard-login";
    public static IServiceCollection AddRelayDashboard(this IServiceCollection services)
    {
        services.AddSingleton<DashboardAuthenticationSettings>();
        services.AddSingleton<DashboardSecurity>();
        services.AddSingleton<DashboardRetainedContext>();
        services.AddSingleton<ReadEfficiencyMeasurements>();
        services.AddSingleton<DashboardEfficiency>();
        services.AddSingleton<DashboardSignInLink>();
        services.AddSingleton<DashboardConnect>();
        services.AddSingleton<DashboardConnectionDiagnostics>();
        services.AddSingleton<HubListener>();
        services.AddSingleton<DashboardAddPc>();
        services.AddSingleton<DashboardLogBuffer>();
        services.AddSingleton<DashboardTrafficBuffer>();
        services.Replace(ServiceDescriptor.Singleton<IRelayApplication, DashboardRelayApplication>());
        services.AddSingleton<DashboardCollector>();
        services.AddHostedService(provider => provider.GetRequiredService<DashboardCollector>());
        services.AddSingleton<DashboardConnections>();
        services.AddSingleton(provider => new DashboardAnalytics(
            provider.GetService<IAnalyticsTool>() ?? (provider.GetService<IAnalyticsQueries>() is { } queries
                ? new AnalyticsTool(queries, allowAdHocSql: false)
                : AnalyticsTool.Off), provider));
        services.AddRateLimiter(options => options.AddPolicy(LoginPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(DiscoveryEndpoints.Address(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            })));
        services.AddAuthentication().AddCookie(DashboardSecurity.Scheme, options =>
        {
            options.Cookie.Name = "CoCern.Dashboard";
            // A browser sends a host's cookies to every port on it, so the cookie is scoped to the dashboard's own paths: every route that
            // needs it (sign-in and sign-out under /dashboard/api, the API, the /dashboard/hub stream) is under /dashboard, and another
            // local service's other paths never receive it. HttpOnly keeps it from scripts, Strict from other sites' requests.
            options.Cookie.Path = "/dashboard";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Secure whenever the dashboard is reached over HTTPS (the network listener). Over plain loopback HTTP it is not, since not every
            // browser keeps a Secure cookie set over http://127.0.0.1; the path and a short remembered lifetime bound it there instead.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = DashboardSecurity.ValidateCookie;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        services.AddOptions<CookieAuthenticationOptions>(DashboardSecurity.Scheme)
            .Configure<DashboardAuthenticationSettings>((options, settings) => options.ExpireTimeSpan = settings.Lifetime(false));
        services.AddAuthorization(options => options.AddPolicy(DashboardSecurity.Policy, policy => policy
            .AddAuthenticationSchemes(DashboardSecurity.Scheme).RequireAuthenticatedUser().RequireClaim(DashboardSecurity.Policy, "true")));
        return services;
    }

    public static WebApplication MapRelayDashboard(this WebApplication app)
    {
        // Validate once at startup, including when no dashboard viewer has been enrolled yet.
        var authentication = app.Services.GetRequiredService<DashboardAuthenticationSettings>();
        app.MapGet("/", () => Results.Redirect("/dashboard/"));
        // Checked once started, when the configuration is final: no viewer configured, no link.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (app.Services.GetRequiredService<DashboardSecurity>().Enabled)
                app.Services.GetRequiredService<DashboardSignInLink>().IssueAtStart(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(),
                    open: app.Configuration.GetValue("Relay:Dashboard:OpenOnStart", false));
        });
        var api = app.MapGroup("/dashboard/api");
        // Analytics reads bypass the tool pipeline. Normalize their query at the same boundary as every other dashboard API.
        api.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var supplied = context.Request.Query["repositoryId"];
            if (supplied.Count == 1 && supplied[0] is { Length: > 0 } value && value.Length <= RelayLimits.MaxIdentifierLength && !value.Any(char.IsControl))
            {
                var canonical = context.RequestServices.GetRequiredService<RepositoryCatalog>().Find(value);
                if (canonical is null) return Results.Json(new { ok = false, code = RelayErrorCodes.RepositoryDenied }, statusCode: 403);
                var query = context.Request.Query.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                query["repositoryId"] = canonical;
                context.Request.Query = new QueryCollection(query);
            }
            return await next(invocation);
        });
        api.MapGet("/login-options", (HttpContext context, DashboardSecurity security) => Results.Ok(new DashboardLoginOptions
        {
            SessionHours = authentication.SessionHours, RememberDays = authentication.RememberDays,
            LocalSignInAvailable = DashboardSecurity.LocalBrowser(context) && security.Enabled
        }));
        api.MapGet("/auth", (HttpContext context) => Results.Ok(new DashboardAuthStatus { Authenticated = true, PrincipalId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) }))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/login", LoginAsync).RequireRateLimiting(LoginPolicy);
        api.MapPost("/login-local", LoginLocalAsync).RequireRateLimiting(LoginPolicy);
        api.MapPost("/logout", async (HttpContext context) =>
        {
            if (!DashboardSecurity.SecureTransport(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
            await context.SignOutAsync(DashboardSecurity.Scheme);
            return Results.Ok(new DashboardAuthStatus());
        }).RequireAuthorization(DashboardSecurity.Policy);
        // Adding CoCern to this PC's apps edits this user's app settings, so only a viewer on this PC may do it.
        api.MapGet("/connect", async (HttpContext context, DashboardConnect connect) =>
            DashboardConnect.OnThisPc(context) ? Results.Ok(await connect.StatusAsync(DashboardConnect.AdminChoice(context), context.RequestAborted)) : Results.Ok(new DashboardConnectStatus { Unavailable = "Open the dashboard on the hub's PC to connect its apps." }))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/connect/repair", async (HttpContext context, DashboardConnect connect) =>
            Results.Ok(DashboardConnect.OnThisPc(context) ? await connect.RepairAsync(true, context.RequestAborted) : new DashboardRepairStatus { Unavailable = "Open the dashboard on the hub's PC to check its installation." }))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/connect/repair", async (HttpContext context, DashboardConnect connect) =>
        {
            if (!DashboardConnect.OnThisPc(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
            return Results.Ok(await connect.RepairAsync(false, context.RequestAborted));
        }).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/connect/diagnostics", (HttpContext context, DashboardConnectionDiagnostics diagnostics) =>
        {
            if (!DashboardConnect.OnThisPc(context)) return Results.StatusCode(403);
            var snapshot = diagnostics.Read();
            return context.Request.Query["download"].ToString() == "true"
                ? Results.File(JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json", "cocern-connection-diagnostics.json")
                : Results.Ok(snapshot);
        }).RequireAuthorization(DashboardSecurity.Policy);
        // It shows the join code, so only a viewer on this PC sees it.
        api.MapGet("/add-pc", (HttpContext context, DashboardAddPc addPc) =>
            Results.Ok(DashboardConnect.OnThisPc(context) ? addPc.Status() : new DashboardAddPcStatus { Unavailable = "Open the dashboard on the hub's PC to see how another PC joins." }))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/connect/{client}", async (HttpContext context, string client, DashboardConnect connect) =>
        {
            if (!DashboardConnect.OnThisPc(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
            return ConnectClients.All.Contains(client) ? Results.Ok(await connect.ConnectAsync(client, DashboardConnect.AdminChoice(context), context.RequestAborted)) : Results.NotFound();
        }).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/snapshot", (DashboardCollector collector) => Results.Ok(collector.Current)).RequireAuthorization(DashboardSecurity.Policy);
        api.MapDashboardRetainedContext();
        api.MapGet("/efficiency", (HttpContext context, DashboardEfficiency efficiency) => efficiency.GetAsync(context)).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/database/reset", DashboardDatabaseManagement.ReadAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/database/reset", DashboardDatabaseManagement.ScheduleAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/analytics", (HttpContext context, DashboardAnalytics analytics) => analytics.GetAsync(context)).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/analytics/test-effort", (HttpContext context, DashboardAnalytics analytics) => analytics.GetTestEffortAsync(context)).RequireAuthorization(DashboardSecurity.Policy);
        // The Sessions page's "Close idle sessions": the cookie alone would let another site's form post this, so it also needs this origin.
        api.MapPost("/sessions/close", CloseSessionsAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/sessions/tool-settings", DashboardSessionToolSettings.ReadAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/sessions/tool-settings", DashboardSessionToolSettings.SaveAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/standing/decide", DashboardStandingManagement.DecideAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/manager", DashboardManagerManagement.ReadAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/manager/decide", DashboardManagerManagement.DecideAsync).RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/dispatch", DashboardManagerManagement.DispatchAsync).RequireAuthorization(DashboardSecurity.Policy);
        app.MapHub<DashboardHub>("/dashboard/hub", options =>
        {
            options.ApplicationMaxBufferSize = 256 * 1024;
            options.TransportMaxBufferSize = 256 * 1024;
            options.CloseOnAuthenticationExpiration = true;
        }).RequireAuthorization(DashboardSecurity.Policy);
        return app;
    }

    public static WebApplication UseDashboardBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/dashboard/api") || context.Request.Path.StartsWithSegments("/dashboard/hub"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!DashboardSecurity.SecureTransport(context)) { context.Response.StatusCode = 403; return; }
                if (context.Request.Path.StartsWithSegments("/dashboard/hub"))
                {
                    // WebSockets require an explicit Origin; same-origin fetch polling may omit it.
                    var originRequired = context.WebSockets.IsWebSocketRequest || HttpMethods.IsConnect(context.Request.Method)
                        || string.Equals(context.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase) || context.Request.Headers.ContainsKey("Origin");
                    if (originRequired && !DashboardSecurity.SameOrigin(context)
                        || string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
                }
            }
            await next(context);
        });
        return app;
    }

    /// <summary>
    /// Runs <c>control_request close_sessions</c> for one repository as the signed-in viewer (an administrator, or it could not have signed in).
    /// Only the repository, filters, explicit target and override are forwarded, never caller session credentials. The reply contains counts and closed IDs.
    /// </summary>
    private static async Task<IResult> CloseSessionsAsync(HttpContext context, IRelayApplication application)
    {
        if (!DashboardSecurity.SecureTransport(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } principalId) return Results.StatusCode(403);
        if (await ReadBodyAsync(context) is not { } body) return Results.StatusCode(413);
        var arguments = new JsonObject { ["requestId"] = "dashboard-close-" + Guid.NewGuid().ToString("N"), ["command"] = "close_sessions" };
        string repository;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return CloseRefused(RelayErrorCodes.InvalidArguments, null);
            if (!json.RootElement.TryGetProperty("repositoryId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                return CloseRefused(RelayErrorCodes.InvalidArguments, "repositoryId");
            repository = id.GetString()!;
            arguments["repositoryId"] = repository;
            // The filter goes to the hub as sent, so the hub alone decides what a valid one is.
            foreach (var name in new[] { "idleMinutes", "labelPrefix", "targetSessionId", "force" })
                if (json.RootElement.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null) arguments[name] = JsonNode.Parse(value.GetRawText());
        }
        catch (JsonException) { return CloseRefused(RelayErrorCodes.InvalidArguments, null); }
        var reply = await application.ExecuteAsync(new RelayCaller(principalId, true, [repository]), RelayTools.ControlRequest,
            JsonSerializer.SerializeToElement(arguments), context.RequestAborted);
        JsonNode? data;
        try { data = JsonNode.Parse(reply.DataJson); }
        catch (JsonException) { data = null; }
        if (!reply.Ok) return CloseRefused(reply.Code, (data as JsonObject)?["field"] is JsonValue field && field.TryGetValue<string>(out var name) ? name : null, ToolResults.Status(reply));
        return Results.Ok(new DashboardCloseSessionsResult
        {
            Closed = Count(data?["closed"]),
            Kept = Count(data?["kept"]),
            Held = Count(data?["held"]),
            Active = Count(data?["active"]),
            More = data?["more"] is JsonValue more && more.TryGetValue<bool>(out var remaining) && remaining,
            Ids = (data?["ids"] as JsonArray)?.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToArray() ?? []
        });

        static int Count(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;
    }

    private static IResult CloseRefused(string code, string? field, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new DashboardCloseSessionsResult { Code = code, Field = field }, statusCode: status);

    /// <summary>The request body up to 16 KiB, or null when it is larger.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        const int maximumBytes = 16 * 1024;
        if (context.Request.ContentLength > maximumBytes) return null;
        using var body = new MemoryStream();
        var buffer = new byte[maximumBytes + 1];
        while (body.Length <= maximumBytes)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(0, maximumBytes + 1 - (int)body.Length), context.RequestAborted);
            if (count == 0) break;
            body.Write(buffer, 0, count);
        }
        return body.Length > maximumBytes ? null : body.ToArray();
    }

    private static async Task<IResult> LoginLocalAsync(HttpContext context, DashboardSecurity security, DashboardAuthenticationSettings settings)
    {
        if (!DashboardSecurity.LocalBrowser(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(settings.Lifetime(false));
        var principal = security.LocalViewer(now, expires);
        if (principal is null) return Results.Unauthorized();
        await context.SignInAsync(DashboardSecurity.Scheme, principal, new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, IssuedUtc = now, ExpiresUtc = expires });
        return Results.Ok(new DashboardAuthStatus { Authenticated = true, PrincipalId = principal.FindFirstValue(ClaimTypes.NameIdentifier) });
    }

    private static async Task<IResult> LoginAsync(HttpContext context, DashboardSecurity security, DashboardSignInLink link, DashboardAuthenticationSettings settings)
    {
        if (!DashboardSecurity.SecureTransport(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        if (await ReadBodyAsync(context) is not { } body) return Results.StatusCode(413);
        ClaimsPrincipal? principal;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset expires;
        var rememberBrowser = false;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest();
            if (json.RootElement.TryGetProperty("rememberBrowser", out var remember))
            {
                if (remember.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Results.BadRequest();
                rememberBrowser = remember.GetBoolean();
            }
            expires = now.Add(settings.Lifetime(rememberBrowser));
            if (json.RootElement.TryGetProperty("link", out var code) && code.ValueKind == JsonValueKind.String)
            {
                // A logged link signs in only from this PC, so a copy of the log elsewhere is no use.
                if (!HubLocalRequest.IsLocal(context)) return Results.StatusCode(403);
                if (!link.Redeem(code.GetString(), now)) return Results.Unauthorized();
                principal = security.LocalViewer(now, expires);
                link.Issue(now);
            }
            else if (json.RootElement.TryGetProperty("token", out var value) && value.ValueKind == JsonValueKind.String) principal = security.Exchange(value.GetString()!, now, expires);
            else return Results.BadRequest();
        }
        catch (JsonException) { return Results.BadRequest(); }
        if (principal is null) return Results.Unauthorized();
        await context.SignInAsync(DashboardSecurity.Scheme, principal, new AuthenticationProperties { IsPersistent = rememberBrowser, AllowRefresh = false, IssuedUtc = now, ExpiresUtc = expires });
        return Results.Ok(new DashboardAuthStatus { Authenticated = true, PrincipalId = principal.FindFirstValue(ClaimTypes.NameIdentifier) });
    }
}


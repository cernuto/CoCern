using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoCern.Host.Dashboard;

public static class DashboardEndpoints
{
    private const string LoginPolicy = "dashboard-login";
    public static IServiceCollection AddRelayDashboard(this IServiceCollection services)
    {
        services.AddSingleton<DashboardSecurity>();
        services.AddSingleton<IRelayObservationSource>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<DashboardLogBuffer>();
        services.AddSingleton<DashboardTrafficBuffer>();
        services.Replace(ServiceDescriptor.Singleton<IRelayApplication, DashboardRelayApplication>());
        services.AddSingleton<DashboardCollector>();
        services.AddHostedService(provider => provider.GetRequiredService<DashboardCollector>());
        services.AddSingleton<DashboardConnections>();
        services.AddRateLimiter(options => options.AddPolicy(LoginPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(DiscoveryEndpoints.Address(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            })));
        services.AddAuthentication().AddCookie(DashboardSecurity.Scheme, options =>
        {
            options.Cookie.Name = "CoCern.Dashboard";
            options.Cookie.Path = "/dashboard";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = DashboardSecurity.ValidateCookie;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        services.AddAuthorization(options => options.AddPolicy(DashboardSecurity.Policy, policy => policy
            .AddAuthenticationSchemes(DashboardSecurity.Scheme).RequireAuthenticatedUser().RequireClaim(DashboardSecurity.Policy, "true")));
        return services;
    }

    public static WebApplication MapRelayDashboard(this WebApplication app)
    {
        var api = app.MapGroup("/dashboard/api");
        api.MapGet("/auth", (HttpContext context) => Results.Ok(new DashboardAuthStatus { Authenticated = true, PrincipalId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) }))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapPost("/login", LoginAsync).RequireRateLimiting(LoginPolicy);
        api.MapPost("/logout", async (HttpContext context) =>
        {
            if (!DashboardSecurity.SecureTransport(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
            await context.SignOutAsync(DashboardSecurity.Scheme);
            return Results.Ok(new DashboardAuthStatus());
        }).RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("/snapshot", (DashboardCollector collector) => Results.Ok(collector.Current)).RequireAuthorization(DashboardSecurity.Policy);
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

    private static async Task<IResult> LoginAsync(HttpContext context, DashboardSecurity security)
    {
        if (!DashboardSecurity.SecureTransport(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        const int maximumBytes = 16 * 1024;
        if (context.Request.ContentLength > maximumBytes) return Results.StatusCode(413);
        using var body = new MemoryStream();
        var buffer = new byte[maximumBytes + 1];
        while (body.Length <= maximumBytes)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(0, maximumBytes + 1 - (int)body.Length), context.RequestAborted);
            if (count == 0) break;
            body.Write(buffer, 0, count);
        }
        if (body.Length > maximumBytes) return Results.StatusCode(413);
        ClaimsPrincipal? principal;
        try
        {
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("token", out var value) || value.ValueKind != JsonValueKind.String) return Results.BadRequest();
            principal = security.Exchange(value.GetString()!, DateTimeOffset.UtcNow);
        }
        catch (JsonException) { return Results.BadRequest(); }
        if (principal is null) return Results.Unauthorized();
        await context.SignInAsync(DashboardSecurity.Scheme, principal, new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) });
        return Results.Ok(new DashboardAuthStatus { Authenticated = true, PrincipalId = principal.FindFirstValue(ClaimTypes.NameIdentifier) });
    }
}


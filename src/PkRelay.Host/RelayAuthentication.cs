using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PkRelay.Core;
using PkRelay.Domain;

namespace PkRelay.Host;

public sealed class RelayClient
{
    public string Token { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public bool IsAdministrator { get; set; }
    public string[] Repositories { get; set; } = [];
}

public sealed class OAuthPrincipal
{
    public string Subject { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public bool IsAdministrator { get; set; }
    public string[] Repositories { get; set; } = [];
}

public static class RelayAuthentication
{
    public static void AddRelayAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(provider => provider.GetRequiredService<IConfiguration>().GetSection("Relay:Clients").Get<RelayClient[]>() ?? []);
        services.AddHostedService<RelayConfigurationValidator>();
        var auth = services.AddAuthentication("Relay").AddScheme<AuthenticationSchemeOptions, RelayAuthenticationHandler>("Relay", _ => { });
        auth.AddJwtBearer("ExternalJwt", _ => { });
        services.AddOptions<JwtBearerOptions>("ExternalJwt").Configure<IConfiguration>((options, config) => ConfigureJwt(options, config));
        services.AddAuthorization();
    }

    internal static void Validate(IConfiguration configuration)
    {
        var clients = configuration.GetSection("Relay:Clients").Get<RelayClient[]>() ?? [];
        if (clients.Any(c => string.IsNullOrWhiteSpace(c.Token) || string.IsNullOrWhiteSpace(c.PrincipalId) || c.Token.Length > 4096))
            throw new InvalidOperationException("Relay client configuration is invalid.");
        var authority = configuration["Relay:OAuth:Authority"];
        var audience = configuration["Relay:OAuth:Audience"];
        var oauth = !string.IsNullOrWhiteSpace(authority);
        if (!oauth && clients.Length == 0) throw new InvalidOperationException("Configure relay clients or an external OAuth issuer before starting.");
        if (oauth && (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) || issuer.Scheme != "https" || issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 || string.IsNullOrWhiteSpace(audience)))
            throw new InvalidOperationException("OAuth requires an HTTPS authority and an audience.");
        var resource = configuration["Relay:OAuth:Resource"];
        if (resource is not null && (!Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) || resourceUri.Scheme != "https" || resourceUri.UserInfo.Length != 0 || resourceUri.Query.Length != 0 || resourceUri.Fragment.Length != 0))
            throw new InvalidOperationException("OAuth resource must be a public HTTPS URL without credentials, query or fragment.");
        var scopes = configuration.GetSection("Relay:OAuth:RequiredScopes").Get<string[]>() ?? ["relay"];
        if (oauth && (scopes.Length == 0 || scopes.Any(string.IsNullOrWhiteSpace))) throw new InvalidOperationException("OAuth requires nonempty resource scopes.");
    }

    private static void ConfigureJwt(JwtBearerOptions options, IConfiguration configuration)
    {
        var authority = configuration["Relay:OAuth:Authority"];
        var audience = configuration["Relay:OAuth:Audience"];
        var mappings = configuration.GetSection("Relay:OAuth:Principals").Get<OAuthPrincipal[]>() ?? [];
        var requiredScopes = configuration.GetSection("Relay:OAuth:RequiredScopes").Get<string[]>() ?? ["relay"];
        options.Authority = authority;
        options.Audience = audience;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30) };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var grantedScopes = context.Principal?.FindAll("scope").Concat(context.Principal.FindAll("scp")).SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal) ?? [];
                if (!requiredScopes.All(grantedScopes.Contains)) { context.Fail("Required resource scope is missing."); return Task.CompletedTask; }
                var subject = context.Principal?.FindFirstValue("sub");
                var mapping = mappings.SingleOrDefault(m => m.Subject == subject);
                if (mapping is null || string.IsNullOrWhiteSpace(mapping.PrincipalId)) context.Fail("Subject is not enrolled.");
                else context.Principal = Principal(mapping.PrincipalId, mapping.IsAdministrator, mapping.Repositories, "ExternalJwt");
                return Task.CompletedTask;
            }
        };
    }

    internal static ClaimsPrincipal Principal(string id, bool admin, IEnumerable<string> repositories, string scheme) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim("relay_admin", admin ? "true" : "false") }.Concat(repositories.Select(r => new Claim("relay_repository", r))), scheme));

    public static RelayCaller Caller(ClaimsPrincipal principal) => new(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException(), principal.HasClaim("relay_admin", "true"), principal.FindAll("relay_repository").Select(c => c.Value).ToArray());
}

internal sealed class RelayConfigurationValidator(IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { RelayAuthentication.Validate(configuration); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RelayAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, RelayClient[] clients, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!Request.IsHttps && (Context.Connection.RemoteIpAddress is not { } remoteAddress || !IPAddress.IsLoopback(remoteAddress)))
            return AuthenticateResult.Fail("HTTPS is required.");
        var token = header[7..];
        if (token.Length > 8192) return AuthenticateResult.Fail("Invalid credentials.");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        RelayClient? match = null;
        foreach (var client in clients)
            if (CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Encoding.UTF8.GetBytes(client.Token)))) match = client;
        if (match is not null)
        {
            return AuthenticateResult.Success(new AuthenticationTicket(RelayAuthentication.Principal(match.PrincipalId, match.IsAdministrator, match.Repositories, Scheme.Name), Scheme.Name));
        }
        if (!string.IsNullOrWhiteSpace(configuration["Relay:OAuth:Authority"])) return await Context.AuthenticateAsync("ExternalJwt");
        return AuthenticateResult.Fail("Invalid credentials.");
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        var resource = configuration["Relay:OAuth:Resource"];
        Response.Headers.WWWAuthenticate = Uri.TryCreate(resource, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            ? "Bearer resource_metadata=\"" + uri.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource/mcp\""
            : "Bearer";
        return Task.CompletedTask;
    }
}

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CoCern.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CoCern.Host.Dashboard;

public sealed class DashboardSecurity(IConfiguration configuration, IHostEnvironment environment)
{
    public const string Scheme = "Dashboard";
    public const string Policy = "dashboard-read-instance";
    private const string EnrollmentClaim = "dashboard_enrollment";
    private const string ExpiryClaim = "dashboard_expires";
    private readonly object enrollmentGate = new();
    private Enrollment[] enrollments = [];
    private KeyValuePair<string, string?>[] enrollmentConfiguration = [];
    private long refreshEnrollmentAt;
    private readonly Func<long> monotonicMilliseconds = static () => Environment.TickCount64;

    internal DashboardSecurity(IConfiguration configuration, IHostEnvironment environment, Func<long> monotonicMilliseconds)
        : this(configuration, environment) => this.monotonicMilliseconds = monotonicMilliseconds;

    public bool Enabled => configuration.GetSection("Relay:Dashboard:ViewerPrincipalIds").Get<string[]>() is { } ids
        && ids.Any(id => !string.IsNullOrWhiteSpace(id));

    public ClaimsPrincipal? Exchange(string token, DateTimeOffset now, DateTimeOffset? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) return null;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        Enrollment? match = null;
        foreach (var enrollment in CurrentClients())
            if (CryptographicOperations.FixedTimeEquals(digest, enrollment.Digest)) match = enrollment;
        return match is null || !Allowed(match.Client) ? null : Principal(match, expiresAt ?? now.Add(new DashboardAuthenticationSettings(configuration).Lifetime(false)));
    }

    /// <summary>The first configured viewer, for a sign-in link: the hub's own administrator on this PC.</summary>
    public ClaimsPrincipal? LocalViewer(DateTimeOffset now, DateTimeOffset? expiresAt = null) => CurrentClients().FirstOrDefault(enrollment => Allowed(enrollment.Client)) is { } viewer ? Principal(viewer, expiresAt ?? now.Add(new DashboardAuthenticationSettings(configuration).Lifetime(false))) : null;

    private static ClaimsPrincipal Principal(Enrollment match, DateTimeOffset expiresAt) =>
        new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, match.Client.PrincipalId),
            new Claim(Policy, "true"),
            new Claim(EnrollmentClaim, match.Fingerprint),
            new Claim(ExpiryClaim, expiresAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ], Scheme));

    public bool IsValid(ClaimsPrincipal? principal, DateTimeOffset now) => Caller(principal, now) is not null;

    /// <summary>Rechecks the exact viewer enrollment before a repository-scoped dashboard read.</summary>
    public RelayCaller? Caller(ClaimsPrincipal? principal, DateTimeOffset now)
    {
        if (principal?.Identity is not { IsAuthenticated: true, AuthenticationType: Scheme }
            || !principal.HasClaim(Policy, "true")
            || !long.TryParse(principal.FindFirstValue(ExpiryClaim), out var expires) || expires <= now.UtcTicks) return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var fingerprint = principal.FindFirstValue(EnrollmentClaim);
        var match = CurrentClients().FirstOrDefault(enrollment => enrollment.Client.PrincipalId == id && Allowed(enrollment.Client)
            && enrollment.Fingerprint == fingerprint);
        return match is null ? null : new RelayCaller(match.Client.PrincipalId, match.Client.IsAdministrator, match.Client.Repositories);
    }

    private Enrollment[] CurrentClients()
    {
        // Inspect configuration cheaply so direct enrollment/admin edits revoke immediately.
        // Token files and token digests are shared across viewers for at most one second.
        var configured = configuration.GetSection("Relay:Clients").AsEnumerable()
            .OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        lock (enrollmentGate)
        {
            var now = monotonicMilliseconds();
            if (now < refreshEnrollmentAt && configured.SequenceEqual(enrollmentConfiguration)) return enrollments;
            try
            {
                enrollments = RelayAuthentication.LoadClients(configuration, environment).Select(client =>
                {
                    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(client.Token));
                    return new Enrollment(client, digest, Convert.ToHexString(digest));
                }).ToArray();
            }
            catch (Exception) { enrollments = []; }
            enrollmentConfiguration = configured;
            refreshEnrollmentAt = now + 1000;
            return enrollments;
        }
    }

    private bool Allowed(RelayClient client) => client.IsAdministrator && !string.IsNullOrEmpty(client.Token)
        && configuration.GetSection("Relay:Dashboard:ViewerPrincipalIds").Get<string[]>() is { } ids
        && ids.Contains(client.PrincipalId, StringComparer.Ordinal);

    public static bool LocalBrowser(HttpContext context) => DashboardConnect.OnThisPc(context)
        && (string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(context.Request.Host.Host.Trim('[', ']'), out var host) && IPAddress.IsLoopback(host))
        && !context.Request.Headers.ContainsKey("Forwarded")
        && !context.Request.Headers.Keys.Any(key => key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase));

    public static bool SecureTransport(HttpContext context) => context.Request.IsHttps
        || context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    public static bool SameOrigin(HttpContext context)
    {
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)
            || !string.IsNullOrEmpty(origin.UserInfo)) return false;
        return string.Equals(origin.GetLeftPart(UriPartial.Authority), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    public static Task ValidateCookie(CookieValidatePrincipalContext context)
    {
        if (!SecureTransport(context.HttpContext) || !context.HttpContext.RequestServices.GetRequiredService<DashboardSecurity>().IsValid(context.Principal, DateTimeOffset.UtcNow))
            context.RejectPrincipal();
        return Task.CompletedTask;
    }

    private sealed record Enrollment(RelayClient Client, byte[] Digest, string Fingerprint);
}

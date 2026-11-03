using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using CoCern.Contracts;

namespace CoCern.Host.Dashboard;

/// <summary>
/// A one-time dashboard sign-in link, so the first look needs no token pasted. The hub logs it; it works once, only from this PC
/// (loopback), for <see cref="Lifetime"/>. Each use logs the next one. The token file still signs in as before.
/// </summary>
public sealed class DashboardSignInLink(ILogger<DashboardSignInLink> log)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ0123456789";
    private readonly object gate = new();
    private byte[]? digest;
    private DateTimeOffset expires;
    private string? baseUrl;

    /// <summary>Spends the current code if it matches and is still valid; constant-time.</summary>
    public bool Redeem(string? code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return false;
        var candidate = Digest(code);
        lock (gate)
        {
            if (digest is null || now >= expires || !CryptographicOperations.FixedTimeEquals(candidate, digest)) return false;
            digest = null;
            return true;
        }
    }

    /// <summary>Makes a new code, replacing any unused one, and logs its link; returns the link.</summary>
    public string Issue(DateTimeOffset now)
    {
        var code = new string(RandomNumberGenerator.GetItems<char>(Alphabet, 24));
        lock (gate) { digest = Digest(code); expires = now + Lifetime; }
        var link = (baseUrl ?? HubAddresses.LocalUrl + "/dashboard/") + "#link=" + code;
        log.LogWarning("Dashboard sign-in link (works once, from this PC, for 24 hours): {SignInLink}", link);
        return link;
    }

    /// <summary>
    /// Logs the first link once the hub is listening, at a loopback address it serves. With <paramref name="open"/>
    /// (<c>Relay:Dashboard:OpenOnStart</c>), also opens it in the default browser, so the dashboard comes up already signed in.
    /// </summary>
    public void IssueAtStart(IServer server, bool open = false)
    {
        baseUrl = LoopbackUrl(server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []);
        var link = Issue(DateTimeOffset.UtcNow);
        if (!open) return;
        // Best effort: a PC without a browser (a server, a container) just keeps the logged link.
        try { using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            log.LogInformation("Could not open a browser; open the sign-in link above instead.");
        }
    }

    internal static string? LoopbackUrl(IEnumerable<string> addresses)
    {
        foreach (var address in addresses.OrderBy(address => address.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            if (!Uri.TryCreate(address.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://0.0.0.0", "://127.0.0.1").Replace("://[::]", "://[::1]"), UriKind.Absolute, out var uri)) continue;
            if (uri.IsLoopback) return $"{uri.Scheme}://{uri.Authority}/dashboard/";
        }
        return null;
    }

    private static byte[] Digest(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()));
}

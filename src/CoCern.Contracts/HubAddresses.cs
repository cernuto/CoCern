using System.Net;

namespace CoCern.Contracts;

/// <summary>Where a hub listens unless configured otherwise. The hub, its adapters and Connect all start from these.</summary>
public static class HubAddresses
{
    /// <summary>Plain HTTP on this PC's loopback: the dashboard and a local adapter with no address of its own.</summary>
    public const string LocalUrl = "http://127.0.0.1:5271";
    /// <summary>The pinned loopback HTTPS available to local apps (<c>Relay:Local:HttpsPort</c>).</summary>
    public const int LocalHttpsPort = 5272;
    /// <summary>The HTTPS listener other PCs join (<c>Relay:Network:HttpsPort</c>).</summary>
    public const int NetworkHttpsPort = 5273;

    /// <summary>The first configured HTTP origin bound to loopback. Never resolve a name or substitute for a wildcard.</summary>
    public static string? SelectLoopbackHttp(IEnumerable<string> addresses)
    {
        foreach (var address in addresses)
        {
            if (!TryParseLoopbackOrigin(address, out var origin) || origin.Scheme != Uri.UriSchemeHttp) continue;
            return origin.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? new UriBuilder(origin) { Host = IPAddress.Loopback.ToString() }.Uri.AbsoluteUri
                : origin.AbsoluteUri;
        }
        return null;
    }

    /// <summary>Accepts only a bare HTTP(S) origin on localhost or a numeric loopback address.</summary>
    public static bool TryParseLoopbackOrigin(string address, out Uri origin)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out origin!) ||
            (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps) || origin.Port <= 0 ||
            !origin.IsWellFormedOriginalString()) return false;
        // Check the original spelling: Uri canonicalizes dot segments and otherwise hides some invalid inputs.
        var separator = address.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) return false;
        var authority = address.AsSpan(separator + 3);
        if (authority.EndsWith("/")) authority = authority[..^1];
        if (authority.IsEmpty || authority.IndexOfAny("/\\@?#%") >= 0) return false;
        return origin.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(origin.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}

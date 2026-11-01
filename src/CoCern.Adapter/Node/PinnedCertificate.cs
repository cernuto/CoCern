using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CoCern.Adapter.Node;

/// <summary>Trust in the hub's self-signed certificate by its SHA-256 fingerprint (uppercase hex of the raw certificate, compared case-insensitively). The exact pin replaces chain and name checks.</summary>
public static class PinnedCertificate
{
    public static bool IsFingerprint(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public static bool Matches(X509Certificate? certificate, string fingerprint) => certificate is not null && string.Equals(Fingerprint(certificate), fingerprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>Accepts the server certificate only when its fingerprint matches; no redirects, no cookies (as <see cref="RelayForwarder.CreateHandler()"/>).</summary>
    public static HttpClientHandler CreateHandler(string fingerprint) => CreateHandler(fingerprint, null);

    /// <summary>As <see cref="CreateHandler(string)"/>, and reports the fingerprint of any certificate it refuses.</summary>
    internal static HttpClientHandler CreateHandler(string fingerprint, Action<string>? mismatch)
    {
        if (!IsFingerprint(fingerprint)) throw new AdapterConfigurationException("A pinned certificate fingerprint must be 64 hexadecimal characters.");
        var handler = RelayForwarder.CreateHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (Matches(certificate, fingerprint)) return true;
            mismatch?.Invoke(certificate is null ? "none" : Fingerprint(certificate));
            return false;
        };
        return handler;
    }

    /// <summary>Trust on first use: connects without credentials, accepts whatever certificate the server presents, and returns its fingerprint. Used only before anything is pinned and before any secret exists.</summary>
    internal static async Task<string> ProbeAsync(Uri hub, CancellationToken cancellationToken)
    {
        string? presented = null;
        using var handler = RelayForwarder.CreateHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            presented = certificate is null ? null : Fingerprint(certificate);
            return presented is not null;
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(new Uri(hub, "health"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return presented ?? throw new HttpRequestException("The hub presented no certificate.");
    }
}

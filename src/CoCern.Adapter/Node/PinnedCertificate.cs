using System.Security.Authentication;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

internal sealed class HubCertificateMismatchException() : AuthenticationException("The hub certificate no longer matches the saved fingerprint.");

/// <summary>Trust in the hub's self-signed certificate by its SHA-256 fingerprint (uppercase hex of the raw certificate, compared case-insensitively). The exact pin replaces chain and name checks.</summary>
public static class PinnedCertificate
{
    /// <summary>Accepts the server certificate only when its fingerprint matches; no redirects, no cookies (as <see cref="RelayForwarder.CreateHandler()"/>).</summary>
    public static SocketsHttpHandler CreateHandler(string fingerprint) => CreateHandler(fingerprint, null);

    /// <summary>As <see cref="CreateHandler(string)"/>, and reports the fingerprint of any certificate it refuses.</summary>
    internal static SocketsHttpHandler CreateHandler(string fingerprint, Action<string>? mismatch)
    {
        if (!CertificateFingerprint.IsValid(fingerprint)) throw new AdapterConfigurationException("A pinned certificate fingerprint must be 64 hexadecimal characters.");
        var handler = RelayForwarder.CreateHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (CertificateFingerprint.Matches(certificate, fingerprint)) return true;
            mismatch?.Invoke(certificate is null ? "none" : CertificateFingerprint.Of(certificate));
            if (certificate is null) return false;
            throw new HubCertificateMismatchException();
        };
        return handler;
    }

    /// <summary>Trust on first use: connects without credentials, accepts whatever certificate the server presents, and returns its fingerprint. Used only before anything is pinned and before any secret exists.</summary>
    internal static async Task<string> ProbeAsync(Uri hub, CancellationToken cancellationToken)
    {
        string? presented = null;
        using var handler = RelayForwarder.CreateHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            presented = certificate is null ? null : CertificateFingerprint.Of(certificate);
            return presented is not null;
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(new Uri(hub, "health"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return presented ?? throw new HttpRequestException("The hub presented no certificate.");
    }
}

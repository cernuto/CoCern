using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CoCern.Contracts;

/// <summary>
/// How a hub's self-signed certificate is identified everywhere it is pinned: the uppercase hex SHA-256 of the raw certificate, compared
/// case-insensitively. The hub logs it and writes it to <see cref="LocalHubRecord"/>; adapters, nodes and setup pin it.
/// </summary>
public static class CertificateFingerprint
{
    public static string Of(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>A fingerprint as people paste it (any case, with or without colons, padded), in the form <see cref="Of"/> gives; null stays null.</summary>
    public static string? Normalize(string? value) => value?.Replace(":", "").Trim().ToUpperInvariant();

    public static bool IsValid(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public static bool Matches(X509Certificate? certificate, string fingerprint) =>
        certificate is not null && string.Equals(Of(certificate), fingerprint, StringComparison.OrdinalIgnoreCase);
}

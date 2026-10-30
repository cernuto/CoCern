using PkRelay.Adapter.Node;

namespace PkRelay.Adapter;

public sealed class AdapterConfigurationException(string message) : Exception(message);

public sealed class AdapterOptions
{
    public const string DefaultUrl = "http://127.0.0.1:5271";

    public AdapterOptions(string url, string token, bool admin = false, bool signalR = false)
    {
        if (!TryParseOrigin(url, out var uri))
            throw new AdapterConfigurationException("PKRELAY_URL must be a root HTTPS origin, or HTTP loopback origin, without credentials, query, or fragment.");

        var normalized = token.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 8192 || normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '~' and not '+' and not '/' and not '='))
            throw new AdapterConfigurationException("A valid PKRELAY_TOKEN_FILE or PKRELAY_TOKEN is required.");

        BaseUri = uri;
        Token = normalized;
        Admin = admin;
        SignalR = signalR;
    }

    public Uri BaseUri { get; }
    public string Token { get; }
    public bool Admin { get; }
    public bool SignalR { get; }

    /// <summary>When set, only a hub certificate with this SHA-256 fingerprint is accepted (stored uppercase).</summary>
    public string? PinnedFingerprint
    {
        get;
        init => field = value is null ? null : PinnedCertificate.IsFingerprint(value) ? value.ToUpperInvariant() : throw new AdapterConfigurationException("A pinned certificate fingerprint must be 64 hexadecimal characters.");
    }

    /// <summary>True for a root HTTPS origin, or HTTP to a loopback host, without credentials, query or fragment.</summary>
    public static bool TryParseOrigin(string url, out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!) ||
            (uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/") return false;
        return true;
    }

    public static AdapterOptions FromEnvironment(Func<string, string?>? environment = null, Func<string, string>? readFile = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        readFile ??= File.ReadAllText;
        string? token;
        var tokenFile = environment("PKRELAY_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            try { token = readFile(tokenFile); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                throw new AdapterConfigurationException("Unable to read PKRELAY_TOKEN_FILE.");
            }
        }
        else token = environment("PKRELAY_TOKEN");

        return new AdapterOptions(environment("PKRELAY_URL") ?? DefaultUrl, token ?? "",
            string.Equals(environment("PKRELAY_ROLE"), "admin", StringComparison.OrdinalIgnoreCase),
            string.Equals(environment("PKRELAY_SIGNALR"), "true", StringComparison.OrdinalIgnoreCase));
    }
}

using CoCern.Adapter.Node;

namespace CoCern.Adapter;

public sealed class AdapterConfigurationException(string message) : Exception(message);

public sealed class AdapterOptions
{
    public const string DefaultUrl = "http://127.0.0.1:5271";

    public AdapterOptions(string url, string token, bool admin = false, bool signalR = false)
    {
        if (!TryParseOrigin(url, out var uri))
            throw new AdapterConfigurationException("COCERN_URL must be a root HTTPS origin, or HTTP loopback origin, without credentials, query, or fragment.");

        var normalized = token.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 8192 || normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '~' and not '+' and not '/' and not '='))
            throw new AdapterConfigurationException("A valid COCERN_TOKEN_FILE or COCERN_TOKEN is required.");

        BaseUri = uri;
        Token = normalized;
        Admin = admin;
        SignalR = signalR;
    }

    public Uri BaseUri { get; }
    public string Token { get; }
    public bool Admin { get; }
    public bool SignalR { get; }
    /// <summary><c>COCERN_PROFILE=chat</c>: advertise and forward only the messaging tools, which keeps tool definitions small in every turn.</summary>
    public bool ChatOnly { get; init; }
    /// <summary>Set only when the adapter runs on the node's credentials; successful <c>session_open</c> calls are then handed to the node there.</summary>
    public string? NodeFolder { get; init; }
    /// <summary>The chat ID the client exported to this process, if any (node mode only).</summary>
    public ChatLocation? ClientChat { get; init; }

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

    /// <summary>
    /// Explicit <c>COCERN_URL</c>, <c>COCERN_TOKEN_FILE</c> or <c>COCERN_TOKEN</c> settings work exactly as before. When none is set and this PC's node is enrolled
    /// (folder <c>COCERN_NODE_DIR</c>, else <see cref="NodeState.DefaultFolder"/> for the real process environment), the adapter runs on the node's hub, credential and pinned fingerprint.
    /// </summary>
    public static AdapterOptions FromEnvironment(Func<string, string?>? environment = null, Func<string, string>? readFile = null)
    {
        var processEnvironment = environment is null;
        environment ??= Environment.GetEnvironmentVariable;
        readFile ??= File.ReadAllText;
        var chatOnly = string.Equals(environment("COCERN_PROFILE"), "chat", StringComparison.OrdinalIgnoreCase);
        if (environment("COCERN_URL") is null && environment("COCERN_TOKEN_FILE") is null && environment("COCERN_TOKEN") is null && FromNode(environment, processEnvironment) is { } node) return new(node) { ChatOnly = chatOnly };
        string? token;
        var tokenFile = environment("COCERN_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            try { token = readFile(tokenFile); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                throw new AdapterConfigurationException("Unable to read COCERN_TOKEN_FILE.");
            }
        }
        else token = environment("COCERN_TOKEN");

        var url = environment("COCERN_URL") ?? DefaultUrl;
        // A pinned certificate (Connect sets it for the hub's loopback HTTPS): the token goes only to the hub that holds that certificate.
        string? fingerprint = null;
        if (environment("COCERN_FINGERPRINT") is { Length: > 0 } pinned)
        {
            fingerprint = pinned.Replace(":", "").Trim().ToUpperInvariant();
            if (!PinnedCertificate.IsFingerprint(fingerprint)) throw new AdapterConfigurationException("COCERN_FINGERPRINT must be the hub certificate's SHA-256 fingerprint: 64 hexadecimal characters.");
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new AdapterConfigurationException("COCERN_FINGERPRINT applies only to an https:// COCERN_URL.");
        }
        return new AdapterOptions(url, token ?? "",
            string.Equals(environment("COCERN_ROLE"), "admin", StringComparison.OrdinalIgnoreCase),
            string.Equals(environment("COCERN_SIGNALR"), "true", StringComparison.OrdinalIgnoreCase)) { ChatOnly = chatOnly, PinnedFingerprint = fingerprint };
    }

    /// <summary>A copy of <paramref name="other"/>, so a caller can change one init-only setting.</summary>
    private AdapterOptions(AdapterOptions other) : this(other.BaseUri.AbsoluteUri, other.Token, other.Admin, other.SignalR)
    {
        PinnedFingerprint = other.PinnedFingerprint; NodeFolder = other.NodeFolder; ClientChat = other.ClientChat; ChatOnly = other.ChatOnly;
    }

    /// <summary>The chat IDs a client exports to its MCP servers. Only these exact variables are read; Codex exports none (see docs/reference.md#wake-ups).</summary>
    private static readonly (string Provider, string Variable)[] ClientChatVariables = [("claude", "CLAUDE_CODE_SESSION_ID")];

    /// <summary>Node mode: an enrolled machine is never an administrator, whatever <c>COCERN_ROLE</c> says.</summary>
    private static AdapterOptions? FromNode(Func<string, string?> environment, bool processEnvironment)
    {
        // A test's own environment never falls back to this PC's real node folder.
        var folder = processEnvironment ? NodeState.ConfiguredFolder(environment) : environment("COCERN_NODE_DIR") is { } configured && !string.IsNullOrWhiteSpace(configured) ? configured : null;
        if (folder is null || NodeState.ReadEnrollment(folder) is not { } node) return null;
        var client = ClientChatVariables.Select(x => (x.Provider, Id: environment(x.Variable))).FirstOrDefault(x => x.Id is not null && ChatLocators.IsUuid(x.Id));
        return new AdapterOptions(node.Hub.AbsoluteUri, node.Credential, admin: false, string.Equals(environment("COCERN_SIGNALR"), "true", StringComparison.OrdinalIgnoreCase))
        {
            PinnedFingerprint = node.Fingerprint, NodeFolder = node.Folder, ClientChat = client.Id is null ? null : new ChatLocation(client.Provider, client.Id)
        };
    }
}

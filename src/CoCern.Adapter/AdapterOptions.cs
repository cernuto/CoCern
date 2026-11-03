using CoCern.Adapter.Node;
using CoCern.Contracts;

namespace CoCern.Adapter;

public sealed class AdapterConfigurationException(string message) : Exception(message);

public sealed class AdapterOptions
{

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
    /// <summary>COCERN_PROFILE=diagnostics: worker tools plus the analytics read; administrator credentials remain required.</summary>
    public bool Diagnostics { get; init; }
    /// <summary>Set only when the adapter runs on the node's credentials; successful <c>session_open</c> calls are then handed to the node there.</summary>
    public string? NodeFolder { get; init; }
    /// <summary>The chat ID the client exported to this process, if any (node mode only), as handed to the node with each session.</summary>
    public ChatLocation? ClientChat { get; init; }
    /// <summary>The chat ID the client exported to this process, if any, in either mode. It keeps this chat's request IDs its own across adapter restarts (see <see cref="CoordinationTools.RequestScope"/>).</summary>
    public ChatLocation? ExportedChat { get; init; }

    /// <summary>When set, only a hub certificate with this SHA-256 fingerprint is accepted (stored uppercase).</summary>
    public string? PinnedFingerprint
    {
        get;
        init => field = value is null ? null : CertificateFingerprint.IsValid(value) ? value.ToUpperInvariant() : throw new AdapterConfigurationException("A pinned certificate fingerprint must be 64 hexadecimal characters.");
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
        var diagnostics = string.Equals(environment("COCERN_PROFILE"), "diagnostics", StringComparison.OrdinalIgnoreCase);
        var chat = Exported(environment);
        if (environment("COCERN_URL") is null && environment("COCERN_TOKEN_FILE") is null && environment("COCERN_TOKEN") is null && FromNode(environment, processEnvironment) is { } node) return new(node) { ChatOnly = chatOnly, Diagnostics = diagnostics, ExportedChat = chat };
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

        var url = environment("COCERN_URL") ?? HubAddresses.LocalUrl;
        // A pinned certificate (Connect sets it for the hub's loopback HTTPS): the token goes only to the hub that holds that certificate.
        var fingerprint = ParseFingerprint(url, environment("COCERN_FINGERPRINT"));
        return new AdapterOptions(url, token ?? "",
            string.Equals(environment("COCERN_ROLE"), "admin", StringComparison.OrdinalIgnoreCase),
            string.Equals(environment("COCERN_SIGNALR"), "true", StringComparison.OrdinalIgnoreCase)) { ChatOnly = chatOnly, Diagnostics = diagnostics, PinnedFingerprint = fingerprint, ExportedChat = chat };
    }

    /// <summary>Normalizes an optional configured certificate pin and refuses using one with plaintext HTTP.</summary>
    internal static string? ParseFingerprint(string url, string? value)
    {
        if (value is not { Length: > 0 }) return null;
        var fingerprint = CertificateFingerprint.Normalize(value)!;
        if (!CertificateFingerprint.IsValid(fingerprint)) throw new AdapterConfigurationException("COCERN_FINGERPRINT must be the hub certificate's SHA-256 fingerprint: 64 hexadecimal characters.");
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new AdapterConfigurationException("COCERN_FINGERPRINT applies only to an https:// COCERN_URL.");
        return fingerprint;
    }

    /// <summary>A copy of <paramref name="other"/>, so a caller can change one init-only setting.</summary>
    private AdapterOptions(AdapterOptions other) : this(other.BaseUri.AbsoluteUri, other.Token, other.Admin, other.SignalR)
    {
        PinnedFingerprint = other.PinnedFingerprint; NodeFolder = other.NodeFolder; ClientChat = other.ClientChat; ChatOnly = other.ChatOnly; Diagnostics = other.Diagnostics; ExportedChat = other.ExportedChat;
    }

    /// <summary>The chat IDs a client exports to its MCP servers. Only these exact variables are read; Codex exports none (see docs/reference.md#wake-ups).</summary>
    private static readonly (string Provider, string Variable)[] ClientChatVariables = [(ChatProviders.Claude, "CLAUDE_CODE_SESSION_ID")];

    /// <summary>The chat ID the client exported, when it is one of the variables above and a UUID.</summary>
    private static ChatLocation? Exported(Func<string, string?> environment) =>
        ClientChatVariables.Select(x => (x.Provider, Id: environment(x.Variable))).FirstOrDefault(x => x.Id is not null && ChatLocators.IsUuid(x.Id)) is { Id: { } id } client ? new(client.Provider, id) : null;

    /// <summary>Node mode: an enrolled machine is never an administrator, whatever <c>COCERN_ROLE</c> says.</summary>
    private static AdapterOptions? FromNode(Func<string, string?> environment, bool processEnvironment)
    {
        // A test's own environment never falls back to this PC's real node folder.
        var folder = processEnvironment ? NodeState.ConfiguredFolder(environment) : environment("COCERN_NODE_DIR") is { } configured && !string.IsNullOrWhiteSpace(configured) ? configured : null;
        if (folder is null || NodeState.ReadEnrollment(folder) is not { } node) return null;
        return new AdapterOptions(node.Hub.AbsoluteUri, node.Credential, admin: false, string.Equals(environment("COCERN_SIGNALR"), "true", StringComparison.OrdinalIgnoreCase))
        {
            PinnedFingerprint = node.Fingerprint, NodeFolder = node.Folder, ClientChat = Exported(environment)
        };
    }
}

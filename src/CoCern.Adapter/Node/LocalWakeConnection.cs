using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoCern.Adapter.Node;

/// <summary>A private local-session cache on the adapter's original principal, separate from enrolled-node handles.</summary>
internal sealed class LocalWakeConnection
{
    internal const string FileName = "connection.json";
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoCern", "local-wake");
    public required string Folder { get; init; }
    public required AdapterOptions Options { get; init; }

    public static LocalWakeConnection? Create(AdapterOptions options, string? tokenFile, string? cacheRoot = null, Action<string>? report = null)
    {
        LocalWakeConnection? Refuse(string reason) { report?.Invoke(reason); return null; }
        if (options.NodeFolder is not null) return Refuse("node_connection");
        if (string.IsNullOrWhiteSpace(tokenFile)) return Refuse("token_file_unconfigured");
        if (!Path.IsPathFullyQualified(tokenFile)) return Refuse("token_file_not_absolute");
        tokenFile = Path.GetFullPath(tokenFile);
        // A different or replaced credential must never silently move this chat onto another principal.
        if (!File.Exists(tokenFile)) return Refuse("token_file_missing");
        if (new FileInfo(tokenFile).Length > 16384) return Refuse("token_file_oversized");
        if (File.ReadAllText(tokenFile).Trim() != options.Token) return Refuse("credential_mismatch");
        var folder = Path.Combine(cacheRoot ?? DefaultRoot, Key(options.BaseUri.AbsoluteUri, tokenFile));
        Directory.CreateDirectory(folder);
        NodeState.WritePrivately(Path.Combine(folder, FileName), JsonSerializer.SerializeToUtf8Bytes(new { hub = options.BaseUri.AbsoluteUri, tokenFile, fingerprint = options.PinnedFingerprint }));
        report?.Invoke("connection_ready");
        return new() { Folder = folder, Options = options };
    }

    public static LocalWakeConnection? Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) return null;
            var hub = ChatLocators.Text(data, "hub");
            var tokenFile = ChatLocators.Text(data, "tokenFile");
            if (hub is null || tokenFile is null || !Path.IsPathFullyQualified(tokenFile) ||
                !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), Key(hub, tokenFile), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
            if (!File.Exists(tokenFile) || new FileInfo(tokenFile).Length > 16384) return null;
            var environment = new Dictionary<string, string?> { ["COCERN_URL"] = hub, ["COCERN_TOKEN_FILE"] = tokenFile, ["COCERN_FINGERPRINT"] = ChatLocators.Text(data, "fingerprint"), ["COCERN_SIGNALR"] = "true" };
            return new() { Folder = folder, Options = AdapterOptions.FromEnvironment(key => environment.GetValueOrDefault(key)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or AdapterConfigurationException) { return null; }
    }

    /// <summary>Revalidate this exact token-file-backed connection; never discover a global folder or copy its credential.</summary>
    internal static string? NativeCredentialIdentity(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 16384 || Read(folder) is not { } connection) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var tokenFile = ChatLocators.Text(document.RootElement, "tokenFile");
            if (tokenFile is null || !NodeState.IsPrivateFile(tokenFile)) return null;
            return NativeJoinBinding.CredentialDigest("local", connection.Options.BaseUri.AbsoluteUri, connection.Options.PinnedFingerprint, connection.Options.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    /// <summary>A native Stop hook may use one matching local connection; ambiguous hubs are never guessed.</summary>
    public static string? FindForClaude(string chatId, string? cacheRoot = null)
    {
        var root = cacheRoot ?? DefaultRoot;
        if (!ChatLocators.IsUuid(chatId) || !Directory.Exists(root)) return null;
        try
        {
            var matches = Directory.EnumerateDirectories(root).Take(128).Where(folder => Read(folder) is not null && ClaudeWaiter.FindHandle(folder, chatId) is not null).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Key(string hub, string tokenFile) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hub + "\n" + (OperatingSystem.IsWindows() ? Path.GetFullPath(tokenFile).ToUpperInvariant() : Path.GetFullPath(tokenFile)))));
}

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

    public static LocalWakeConnection? Create(AdapterOptions options, string? tokenFile, string? cacheRoot = null)
    {
        if (options.NodeFolder is not null || string.IsNullOrWhiteSpace(tokenFile) || !Path.IsPathFullyQualified(tokenFile)) return null;
        tokenFile = Path.GetFullPath(tokenFile);
        // A different or replaced credential must never silently move this chat onto another principal.
        if (!File.Exists(tokenFile) || new FileInfo(tokenFile).Length > 16384 || File.ReadAllText(tokenFile).Trim() != options.Token) return null;
        var folder = Path.Combine(cacheRoot ?? DefaultRoot, Key(options.BaseUri.AbsoluteUri, tokenFile));
        Directory.CreateDirectory(folder);
        NodeState.WritePrivately(Path.Combine(folder, FileName), JsonSerializer.SerializeToUtf8Bytes(new { hub = options.BaseUri.AbsoluteUri, tokenFile, fingerprint = options.PinnedFingerprint }));
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
            if (hub is null || tokenFile is null || !Path.IsPathFullyQualified(tokenFile) || Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) != Key(hub, tokenFile)) return null;
            if (!File.Exists(tokenFile) || new FileInfo(tokenFile).Length > 16384) return null;
            var environment = new Dictionary<string, string?> { ["COCERN_URL"] = hub, ["COCERN_TOKEN_FILE"] = tokenFile, ["COCERN_FINGERPRINT"] = ChatLocators.Text(data, "fingerprint"), ["COCERN_SIGNALR"] = "true" };
            return new() { Folder = folder, Options = AdapterOptions.FromEnvironment(key => environment.GetValueOrDefault(key)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or AdapterConfigurationException) { return null; }
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

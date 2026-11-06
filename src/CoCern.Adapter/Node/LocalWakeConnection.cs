using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoCern.Adapter.Node;

/// <summary>A private local-session cache on the adapter's original principal, separate from enrolled-node handles.</summary>
internal sealed class LocalWakeConnection
{
    internal const string FileName = "connection.json";
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoCern", "local-wake");
    internal static string FolderFor(AdapterOptions options, string tokenFile, string? cacheRoot = null) =>
        Path.Combine(cacheRoot ?? DefaultRoot, Key(options.BaseUri.AbsoluteUri, tokenFile));
    public required string Folder { get; init; }
    public required AdapterOptions Options { get; init; }

    public static LocalWakeConnection? Create(AdapterOptions options, string? tokenFile, string? cacheRoot = null, Action<string>? report = null)
    {
        report ??= code => LocalWakeStatus.Report(code, cacheRoot);
        LocalWakeConnection? Refuse(string reason) { report?.Invoke(reason); return null; }
        if (options.NodeFolder is not null) return Refuse(LocalWakeCodes.NodeConnection);
        if (string.IsNullOrWhiteSpace(tokenFile)) return Refuse(LocalWakeCodes.TokenFileUnconfigured);
        if (!Path.IsPathFullyQualified(tokenFile)) return Refuse(LocalWakeCodes.TokenFileNotAbsolute);
        tokenFile = Path.GetFullPath(tokenFile);
        // A different or replaced credential must never silently move this chat onto another principal.
        if (!File.Exists(tokenFile)) return Refuse(LocalWakeCodes.TokenFileMissing);
        if (new FileInfo(tokenFile).Length > 16384) return Refuse(LocalWakeCodes.TokenFileOversized);
        if (File.ReadAllText(tokenFile).Trim() != options.Token) return Refuse(LocalWakeCodes.CredentialMismatch);
        var folder = Path.Combine(cacheRoot ?? DefaultRoot, Key(options.BaseUri.AbsoluteUri, tokenFile));
        Directory.CreateDirectory(folder);
        NodeState.WritePrivately(Path.Combine(folder, FileName), JsonSerializer.SerializeToUtf8Bytes(new { hub = options.BaseUri.AbsoluteUri, tokenFile, fingerprint = options.PinnedFingerprint }));
        report?.Invoke(LocalWakeCodes.ConnectionReady);
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

    /// <summary>
    /// The local connection a Claude Stop hook waits on. A chat's handle normally sits in one folder. The same hub session sits in two when
    /// the chat's connection was re-created under another address or pin (a pinned HTTPS folder beside the plain HTTP one, as on the hub's
    /// own PC): that is one session, not two hubs, and the folder that recorded it last is the live connection. A chat that opened a newer
    /// session elsewhere is followed to that session, as <see cref="NodeState.ChooseForChat"/> follows it within a folder. Only handles for
    /// different sessions with nothing to choose between them are ambiguous, and then nothing is guessed: a waiter on the wrong hub would
    /// wait forever. Giving up here left the chat with no waiter at all, unreachable until a human prompted it (the Manager on 2026-11-06).
    /// </summary>
    public static string? FindForClaude(string chatId, string? cacheRoot = null)
    {
        var root = cacheRoot ?? DefaultRoot;
        if (!ChatLocators.IsUuid(chatId) || !Directory.Exists(root)) return null;
        try
        {
            var matches = Directory.EnumerateDirectories(root).Take(128)
                .Where(folder => Read(folder) is not null)
                .Select(folder => (Folder: folder, Handle: ClaudeWaiter.FindHandle(folder, chatId)))
                .Where(match => match.Handle is not null)
                .Select(match => (match.Folder, Handle: match.Handle!, Written: HandleWrittenAt(match.Folder, match.Handle!.SessionId)))
                .OrderByDescending(match => match.Handle.OpenedAt).ThenByDescending(match => match.Written).ThenBy(match => match.Folder, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 0) return null;
            var newest = matches[0];
            if (matches.Length == 1 || matches.All(match => match.Handle.SessionId == newest.Handle.SessionId)) return newest.Folder;
            var tied = matches.Any(match => match.Handle.SessionId != newest.Handle.SessionId &&
                match.Handle.OpenedAt == newest.Handle.OpenedAt && match.Written == newest.Written);
            return tied ? null : newest.Folder;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>When a folder last recorded the chat's session; a record that cannot be stamped sorts last.</summary>
    private static DateTimeOffset HandleWrittenAt(string folder, string sessionId)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(NodeState.SessionHandlePath(folder, sessionId)), TimeSpan.Zero); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return DateTimeOffset.MinValue; }
    }

    private static string Key(string hub, string tokenFile) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hub + "\n" + (OperatingSystem.IsWindows() ? Path.GetFullPath(tokenFile).ToUpperInvariant() : Path.GetFullPath(tokenFile)))));
}

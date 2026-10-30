using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PkRelay.Adapter.Node;

internal enum CredentialRead { Present, Missing, Unreadable }

/// <summary>An enrolled node folder as the stdio adapter uses it: the hub, its pinned fingerprint (null for loopback HTTP) and the machine credential.</summary>
public sealed record NodeEnrollment(string Folder, Uri Hub, string? Fingerprint, string Credential)
{
    public override string ToString() => $"NodeEnrollment {{ Folder = {Folder}, Hub = {Hub} }}";
}

/// <summary>
/// The node's folder (<c>%LOCALAPPDATA%\PkRelay\node</c> unless <c>--data-dir</c> says otherwise): <c>node.json</c> <c>{ machineId, hubUrl, fingerprint, enrolled }</c>,
/// the machine <c>credential</c> (current-user-only), and <c>node.lock</c>, held exclusively for as long as this object lives.
/// </summary>
public sealed class NodeState : IDisposable
{
    private const string StateFile = "node.json";
    private const string CredentialFile = "credential";
    private const string LockFile = "node.lock";
    internal const string SessionsFolder = "sessions";
    /// <summary>The hub's bound on identifiers such as repository IDs and labels.</summary>
    internal const int MaxIdentifierLength = 256;
    private readonly FileStream _lock;

    private NodeState(string folder, FileStream @lock) { Folder = folder; _lock = @lock; }

    public string Folder { get; }
    public string MachineId { get; private set; } = "";
    public string? HubUrl { get; private set; }
    public string? Fingerprint { get; private set; }
    /// <summary>True once a credential was stored for this hub; from then on the hub address is fixed.</summary>
    public bool Enrolled { get; private set; }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PkRelay", "node");

    /// <summary>Takes the folder's lock and loads its state, creating a machine ID the first time. A second node on the same folder fails at once.</summary>
    public static NodeState Open(string? dataDir)
    {
        var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir) ? DefaultFolder : dataDir);
        FileStream @lock;
        try
        {
            Directory.CreateDirectory(folder);
            @lock = new FileStream(Path.Combine(folder, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) when (File.Exists(Path.Combine(folder, LockFile))) { throw new AdapterConfigurationException($"Another PkRelay node is already running with the data folder '{folder}'."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { throw new AdapterConfigurationException($"Unable to use the node data folder '{folder}'."); }
        var state = new NodeState(folder, @lock);
        try { state.Load(); }
        catch { @lock.Dispose(); throw; }
        return state;
    }

    /// <summary>The stored machine credential, or null when it is missing, unreadable, or not in the shape the hub issues.</summary>
    public string? ReadCredential() => TryReadCredential(out var credential) == CredentialRead.Present ? credential : null;

    /// <summary>
    /// Reads the credential and says why when there is none. Only an absent file, or one that cannot be a credential (wrong shape, so it could never authenticate), is <see cref="CredentialRead.Missing"/>.
    /// Any other failure (a sharing violation from antivirus or backup, a permissions problem) is <see cref="CredentialRead.Unreadable"/>: the secret may be intact, so callers must never renew the identity or delete the file for it.
    /// </summary>
    internal CredentialRead TryReadCredential(out string? credential) => ReadCredentialFile(Folder, out credential);

    private static CredentialRead ReadCredentialFile(string folder, out string? credential)
    {
        credential = null;
        string text;
        try { text = File.ReadAllText(Path.Combine(folder, CredentialFile)).Trim(); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return CredentialRead.Missing; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return CredentialRead.Unreadable; }
        if (text.Length != 64 || !text.All(Uri.IsHexDigit)) return CredentialRead.Missing;
        credential = text;
        return CredentialRead.Present;
    }

    /// <summary>Records the hub address and its pinned fingerprint (null for loopback HTTP, or not yet probed).</summary>
    internal void SetHub(string hubUrl, string? fingerprint)
    {
        if (HubUrl == hubUrl && Fingerprint == fingerprint) return;
        (HubUrl, Fingerprint) = (hubUrl, fingerprint);
        Save();
    }

    /// <summary>Pins the hub certificate on first use. Once pinned, the fingerprint never changes for this hub.</summary>
    internal void SetFingerprint(string fingerprint)
    {
        if (Fingerprint is not null || !PinnedCertificate.IsFingerprint(fingerprint)) throw new InvalidOperationException("The hub certificate is already pinned.");
        Fingerprint = fingerprint.ToUpperInvariant();
        Save();
    }

    /// <summary>Writes the credential to its current-user-only file first; only then does <c>node.json</c> say the node is enrolled.</summary>
    internal void SaveCredential(string credential)
    {
        if (credential.Length != 64 || !credential.All(Uri.IsHexDigit)) throw new ArgumentException("Not a machine credential.", nameof(credential));
        WritePrivately(Path.Combine(Folder, CredentialFile), Encoding.ASCII.GetBytes(credential));
        if (Enrolled) return;
        Enrolled = true;
        Save();
    }

    /// <summary>Starts over under a new machine ID: the stale credential goes first, then the new ID is saved before any enroll call uses it.</summary>
    internal void RenewMachineId()
    {
        File.Delete(Path.Combine(Folder, CredentialFile));
        MachineId = NewMachineId();
        Enrolled = false;
        Save();
    }

    private static string NewMachineId() => Guid.NewGuid().ToString("N");

    private void Load()
    {
        var path = Path.Combine(Folder, StateFile);
        if (File.Exists(path))
        {
            try { (MachineId, HubUrl, Fingerprint, Enrolled) = Parse(File.ReadAllText(path)); }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                throw new AdapterConfigurationException($"'{path}' is unreadable. Fix it, or delete the folder to enroll this PC again.");
            }
        }
        if (MachineId.Length == 0) { MachineId = NewMachineId(); Save(); }
        // A credential written just before a crash still counts: node.json catches up.
        if (!Enrolled && HubUrl is not null && TryReadCredential(out _) == CredentialRead.Present) { Enrolled = true; Save(); }
    }

    /// <summary>Parses <c>node.json</c>; throws <see cref="JsonException"/> when it is not in the shape this node writes.</summary>
    private static (string MachineId, string? HubUrl, string? Fingerprint, bool Enrolled) Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException();
        var machineId = Text(root, "machineId");
        var hubUrl = Text(root, "hubUrl");
        var fingerprint = Text(root, "fingerprint");
        if (machineId is null || machineId.Length is 0 or > 128 || !machineId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') || !char.IsAsciiLetterOrDigit(machineId[0]) ||
            (hubUrl is not null && !AdapterOptions.TryParseOrigin(hubUrl, out _)) || (fingerprint is not null && !PinnedCertificate.IsFingerprint(fingerprint)))
            throw new JsonException();
        return (machineId, hubUrl, fingerprint?.ToUpperInvariant(), root["enrolled"] is JsonValue enrolled && enrolled.TryGetValue<bool>(out var value) && value);
    }

    private static string? Text(JsonObject root, string name) => root[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new JsonException()
    };

    private void Save()
    {
        var json = new JsonObject { ["machineId"] = MachineId, ["hubUrl"] = HubUrl, ["fingerprint"] = Fingerprint, ["enrolled"] = Enrolled }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(Folder, StateFile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>The secret is never readable by anyone else: the file is created already restricted to the current user under a temporary name and only then moved over the old one (as the hub's certificate writer does). Shared by the credential and the session handles.</summary>
    internal static void WritePrivately(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = CreatePrivateFile(temporary)) { stream.Write(content); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    /// <summary>
    /// What a stdio adapter needs to run on this PC's identity: the hub, its pinned fingerprint and the machine credential.
    /// Read without taking <c>node.lock</c>, which the running node holds. Null unless the folder is fully enrolled.
    /// </summary>
    public static NodeEnrollment? ReadEnrollment(string folder)
    {
        folder = Path.GetFullPath(folder);
        (string MachineId, string? HubUrl, string? Fingerprint, bool Enrolled) state;
        try { state = Parse(File.ReadAllText(Path.Combine(folder, StateFile))); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
        if (!state.Enrolled || state.HubUrl is null || !AdapterOptions.TryParseOrigin(state.HubUrl, out var hub) || ReadCredentialFile(folder, out var credential) != CredentialRead.Present) return null;
        // The node refuses an https hub it has not pinned, and so does the adapter.
        if (hub.Scheme == Uri.UriSchemeHttps && state.Fingerprint is null) return null;
        return new(folder, hub, hub.Scheme == Uri.UriSchemeHttps ? state.Fingerprint : null, credential!);
    }

    /// <summary>The machine ID and hub of an enrolled folder, read without taking <c>node.lock</c>; null unless the folder says it is enrolled.</summary>
    internal static (string MachineId, string HubUrl)? ReadIdentity(string folder)
    {
        try
        {
            var state = Parse(File.ReadAllText(Path.Combine(Path.GetFullPath(folder), StateFile)));
            return state.Enrolled && state.HubUrl is not null ? (state.MachineId, state.HubUrl) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Writes <c>sessions\&lt;sessionId&gt;.json</c> current-user-only from creation, replacing any older copy.</summary>
    public static void WriteSessionHandle(string nodeFolder, SessionHandle handle)
    {
        if (!IsFileId(handle.SessionId)) throw new ArgumentException("Not a session ID that can name a file.", nameof(handle));
        var folder = Path.Combine(nodeFolder, SessionsFolder);
        Directory.CreateDirectory(folder);
        var json = new JsonObject { ["sessionId"] = handle.SessionId, ["sessionToken"] = handle.SessionToken, ["repositoryId"] = handle.RepositoryId, ["label"] = handle.Label, ["openedAt"] = handle.OpenedAt.ToString("O") };
        if (handle.Client is { } client) json["client"] = new JsonObject { ["provider"] = client.Provider, ["chatId"] = client.ThreadId };
        if (handle.Chat is { } chat) json["chat"] = new JsonObject { ["provider"] = chat.Provider, ["threadId"] = chat.ThreadId };
        else if (handle.Unmapped) json["unmapped"] = true;
        WritePrivately(Path.Combine(folder, handle.SessionId + ".json"), Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    /// <summary>Reads one handle file, tolerating fields it does not know. Null when the file is missing, unreadable, or not a handle for the session its name says.</summary>
    public static SessionHandle? ReadSessionHandle(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return null;
            var sessionId = Text(root, "sessionId");
            var sessionToken = Text(root, "sessionToken");
            var repositoryId = Text(root, "repositoryId");
            var label = Text(root, "label");
            if (sessionId is null || !IsFileId(sessionId) || !string.Equals(Path.GetFileNameWithoutExtension(path), sessionId, StringComparison.Ordinal) || sessionToken is not { Length: > 0 } ||
                repositoryId is not { Length: > 0 and <= MaxIdentifierLength } || label is not { Length: > 0 and <= MaxIdentifierLength } || label.Any(char.IsControl) ||
                !DateTimeOffset.TryParse(Text(root, "openedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var openedAt)) return null;
            // Chat IDs are only ever UUIDs; anything else is ignored (a bad claim falls back to transcripts, a bad outcome is located again).
            return new(sessionId, sessionToken, repositoryId, label, openedAt, Location(root["client"], "chatId"), Location(root["chat"], "threadId"),
                root["unmapped"] is JsonValue unmapped && unmapped.TryGetValue<bool>(out var value) && value);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }

    private static ChatLocation? Location(JsonNode? node, string idName) =>
        node is JsonObject given && Text(given, "provider") is "codex" or "claude" && Text(given, idName) is { } id && ChatLocators.IsUuid(id) ? new(Text(given, "provider")!, id) : null;

    /// <summary>The handle file for a relay session; the ID must be one that can name a file.</summary>
    internal static string SessionHandlePath(string nodeFolder, string sessionId) => IsFileId(sessionId) ? Path.Combine(nodeFolder, SessionsFolder, sessionId + ".json") : throw new ArgumentException("Not a session ID that can name a file.", nameof(sessionId));

    /// <summary>Every readable handle in the node folder.</summary>
    public static IReadOnlyList<SessionHandle> ReadSessionHandles(string nodeFolder) =>
        ChatLocators.Files(Path.Combine(nodeFolder, SessionsFolder), "*.json", SearchOption.TopDirectoryOnly).Select(ReadSessionHandle).OfType<SessionHandle>().ToArray();

    /// <summary>A relay session ID that is safe as a file name: ASCII letters, digits, '.', '_' or '-', starting with a letter or digit.</summary>
    internal static bool IsFileId(string id) => id.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public void Dispose() => _lock.Dispose();
}

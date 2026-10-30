using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PkRelay.Adapter.Node;

/// <summary>
/// The node's folder (<c>%LOCALAPPDATA%\PkRelay\node</c> unless <c>--data-dir</c> says otherwise): <c>node.json</c> <c>{ machineId, hubUrl, fingerprint, enrolled }</c>,
/// the machine <c>credential</c> (current-user-only), and <c>node.lock</c>, held exclusively for as long as this object lives.
/// </summary>
public sealed class NodeState : IDisposable
{
    private const string StateFile = "node.json";
    private const string CredentialFile = "credential";
    private const string LockFile = "node.lock";
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

    /// <summary>The stored machine credential, or null when it is missing or not in the shape the hub issues.</summary>
    public string? ReadCredential()
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(Folder, CredentialFile)).Trim();
            return text.Length == 64 && text.All(Uri.IsHexDigit) ? text : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
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
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException();
                var machineId = Text(root, "machineId");
                var hubUrl = Text(root, "hubUrl");
                var fingerprint = Text(root, "fingerprint");
                if (machineId is null || machineId.Length is 0 or > 128 || !machineId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') || !char.IsAsciiLetterOrDigit(machineId[0]) ||
                    (hubUrl is not null && !AdapterOptions.TryParseOrigin(hubUrl, out _)) || (fingerprint is not null && !PinnedCertificate.IsFingerprint(fingerprint)))
                    throw new JsonException();
                (MachineId, HubUrl, Fingerprint) = (machineId, hubUrl, fingerprint?.ToUpperInvariant());
                Enrolled = root["enrolled"] is JsonValue enrolled && enrolled.TryGetValue<bool>(out var value) && value;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                throw new AdapterConfigurationException($"'{path}' is unreadable. Fix it, or delete the folder to enroll this PC again.");
            }
        }
        if (MachineId.Length == 0) { MachineId = NewMachineId(); Save(); }
        // A credential written just before a crash still counts: node.json catches up.
        if (!Enrolled && HubUrl is not null && ReadCredential() is not null) { Enrolled = true; Save(); }
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

    /// <summary>The secret is never readable by anyone else: the file is created already restricted to the current user under a temporary name and only then moved over the old one (as the hub's certificate writer does).</summary>
    private static void WritePrivately(string path, byte[] content)
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

    public void Dispose() => _lock.Dispose();
}

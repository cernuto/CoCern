using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CoCern.Adapter.Node;

/// <summary>A private, connection-scoped opening attempt. Never serialize a lease into a relay reply.</summary>
internal sealed class NativeJoinBinding : IAsyncDisposable
{
    internal string Folder { get; }
    internal string Provider { get; }
    internal string NativeId { get; }
    internal string RepositoryId { get; }
    internal string Workspace { get; }
    internal string BindingKey { get; }
    internal string StatePath => Path.Combine(Folder, NodeState.SessionsFolder, "native", BindingKey + ".json");
    internal string LockPath => Path.ChangeExtension(StatePath, ".lock");
    private readonly string credentialIdentity;
    private readonly JsonElement arguments;
    private FileStream? owner;
    private string? currentSessionId;
    private const int MaxStateBytes = 16 * 1024;
    internal string Secret { get { CheckLease(); return arguments.GetProperty("nativeJoin").GetProperty("bindingSecret").GetString()!; } }
    internal string Generation { get { CheckLease(); return arguments.GetProperty("nativeJoin").GetProperty("generation").GetString()!; } }
    internal string OpenRequestId { get { CheckLease(); return arguments.GetProperty("requestId").GetString()!; } }
    internal string OwnershipKey => Digest("cocern-native-owner-v1", BindingKey, Secret, Generation, OpenRequestId);
    internal JsonElement FrozenOpenArguments { get { CheckLease(); return arguments.Clone(); } }
    internal string? CurrentHandleReference { get { CheckLease(); return currentSessionId; } }
    internal SessionHandle? CurrentHandle
    {
        get
        {
            CheckLease();
            if (currentSessionId is null) return null;
            var handle = OwnedHandle(currentSessionId);
            return handle ?? throw Failure("The private native session reference could not be verified.");
        }
    }

    private NativeJoinBinding(string folder, string provider, string nativeId, string repositoryId, string workspace,
        string identity, string key, JsonElement frozen, string? sessionId, FileStream lease)
    {
        (Folder, Provider, NativeId, RepositoryId, Workspace) = (folder, provider, nativeId, repositoryId, workspace);
        (credentialIdentity, BindingKey, arguments, currentSessionId, owner) = (identity, key, frozen.Clone(), sessionId, lease);
    }

    /// <summary>Provider names are at most 16 characters; native IDs accept only UUID N (32) or D (36) forms.</summary>
    internal static async Task<NativeJoinBinding> AcquireAsync(string folder, string provider, string nativeId,
        string machineId, string repositoryId, string workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (provider is not { Length: > 0 and <= 16 } || nativeId is not { Length: 32 or 36 })
            throw new ArgumentException("A bounded provider and N/D native UUID are required.");
        provider = provider.ToLowerInvariant();
        if (provider is not ("codex" or "claude") || !TryNativeId(nativeId, out var id))
            throw new ArgumentException("A supported provider and native chat UUID are required.");
        nativeId = id.ToString("D");
        folder = CanonicalDirectory(folder);
        ValidateIdentifier(machineId); ValidateIdentifier(repositoryId);
        workspace = CanonicalDirectory(workspace); // Never use the adapter's cwd or accept a relative native event path.
        var checkout = await Task.Run(() => CoordinationTools.GitTopLevel(workspace), ct).WaitAsync(ct);
        if (checkout is null) throw Failure("The native event checkout could not be verified with Git.");
        checkout = CanonicalDirectory(checkout);
        var prefix = Path.EndsInDirectorySeparator(checkout) ? checkout : checkout + Path.DirectorySeparatorChar;
        if (!string.Equals(workspace, checkout, StringComparison.Ordinal) && !workspace.StartsWith(prefix, StringComparison.Ordinal))
            throw Failure("Git returned a checkout outside the native event directory.");
        workspace = checkout;
        var identity = ConnectionIdentity(folder);
        var key = Digest("cocern-native-binding-v1", folder, identity, provider, nativeId, machineId, repositoryId, workspace);
        var sessions = Path.Combine(folder, NodeState.SessionsFolder);
        Directory.CreateDirectory(sessions); RejectLink(sessions);
        var directory = Path.Combine(sessions, "native");
        Directory.CreateDirectory(directory); RejectLink(directory);
        var statePath = Path.Combine(directory, key + ".json");
        var lease = await LockAsync(Path.ChangeExtension(statePath, ".lock"), ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (ConnectionIdentity(folder) != identity) throw Failure("The private connection changed while joining.");
            RejectLink(statePath, allowMissing: true);
            JsonElement frozen;
            string? current = null;
            if (File.Exists(statePath))
            {
                RequirePrivate(statePath, MaxStateBytes);
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(statePath), new JsonDocumentOptions { MaxDepth = 8 });
                    var root = Object(document.RootElement, "version", "scope", "openArguments", "currentSessionId");
                    if (root.Count is < 3 or > 4 || root["version"].GetInt32() != 1 || Text(root["scope"]) != key)
                        throw Failure("The private native binding is invalid.");
                    frozen = root["openArguments"].Clone();
                    ValidateArguments(frozen, provider, nativeId, machineId, repositoryId, workspace);
                    if (root.TryGetValue("currentSessionId", out var value))
                    {
                        current = Text(value);
                        if (!NodeState.IsFileId(current)) throw Failure("The private native session reference is invalid.");
                    }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                { throw Failure("The private native binding is corrupt; it was left unchanged."); }
            }
            else
            {
                var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var generation = Guid.NewGuid().ToString("N");
                var requestId = Guid.NewGuid().ToString("N");
                frozen = JsonSerializer.SerializeToElement(new { repositoryId, machineId, requestId,
                    nativeJoin = new { provider, nativeId, workspace, bindingSecret = secret, generation } });
                ct.ThrowIfCancellationRequested();
                Write(statePath, key, frozen, null);
            }
            var binding = new NativeJoinBinding(folder, provider, nativeId, repositoryId, workspace, identity, key, frozen, current, lease);
            _ = binding.CurrentHandle; // A missing/foreign active handle is not an invitation to choose another one.
            return binding;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    internal bool MatchesFolder(string folder) => CanonicalDirectory(folder) == Folder;
    internal bool MatchesClient(ChatLocation? client) => client is not null && client.Provider == Provider &&
        TryNativeId(client.ThreadId, out var id) && id.ToString("D") == NativeId;
    internal bool MatchesConnection(AdapterOptions authenticatedOptions)
    {
        CheckLease();
        return CredentialDigest(NodeState.HasIdentityFiles(Folder) ? "node" : "local", authenticatedOptions.BaseUri.AbsoluteUri,
            authenticatedOptions.PinnedFingerprint, authenticatedOptions.Token) == credentialIdentity;
    }

    // Only SessionHandoff's authenticated-open boundary creates an ownership marker and calls this method.
    internal bool Complete(SessionHandle handle)
    {
        CheckLease();
        if (currentSessionId is not null && currentSessionId != handle.SessionId) return false;
        var saved = OwnedHandle(handle.SessionId);
        if (saved is null || saved.SessionToken != handle.SessionToken) return false;
        Write(StatePath, BindingKey, arguments, handle.SessionId);
        currentSessionId = handle.SessionId;
        return true;
    }

    internal SessionHandle? OwnedHandle(string sessionId)
    {
        CheckLease();
        var path = NodeState.SessionHandlePath(Folder, sessionId);
        if (!File.Exists(path)) return null;
        RequirePrivate(path, 64 * 1024);
        var handle = NodeState.ReadSessionHandle(path);
        return handle is not null && handle.NativeBindingKey == OwnershipKey && handle.RepositoryId == RepositoryId &&
            MatchesClient(handle.Client) && MatchesClient(handle.Chat) && !handle.Unmapped ? handle : null;
    }

    private void CheckLease()
    {
        ObjectDisposedException.ThrowIf(owner is null, this);
        if (ConnectionIdentity(Folder) != credentialIdentity) throw Failure("The private connection changed while joining.");
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileStream? lease = null;
            try
            {
                if (File.Exists(path)) { RejectLink(path); lease = new(path, FileMode.Open, FileAccess.Write, FileShare.None); }
                else lease = NodeState.CreatePrivateFile(path);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 80 or 183)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw Failure("Another native join still owns this binding.");
                await Task.Delay(25, ct);
                continue;
            }
            try { RequirePrivate(path, 0); return lease; }
            catch { await lease.DisposeAsync(); throw; }
        }
    }

    private static void Write(string path, string key, JsonElement frozen, string? current)
    {
        var state = new Dictionary<string, object?> { ["version"] = 1, ["scope"] = key, ["openArguments"] = frozen };
        if (current is not null) state["currentSessionId"] = current;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > MaxStateBytes) throw Failure("The private native opening attempt exceeds its bound.");
        NodeState.WritePrivately(path, bytes);
    }

    private static void ValidateArguments(JsonElement frozen, string provider, string nativeId, string machine, string repo, string workspace)
    {
        var args = Object(frozen, "repositoryId", "machineId", "requestId", "nativeJoin");
        var native = Object(args["nativeJoin"], "provider", "nativeId", "workspace", "bindingSecret", "generation");
        Span<byte> secret = stackalloc byte[32];
        if (args.Count != 4 || native.Count != 5 || Text(args["repositoryId"]) != repo || Text(args["machineId"]) != machine ||
            !Guid.TryParseExact(Text(args["requestId"]), "N", out _) || Text(native["provider"]) != provider ||
            Text(native["nativeId"]) != nativeId || Text(native["workspace"]) != workspace ||
            !Guid.TryParseExact(Text(native["generation"]), "N", out _) ||
            !Convert.TryFromBase64String(Text(native["bindingSecret"]), secret, out var count) || count != 32)
            throw Failure("The private native opening attempt is invalid.");
    }

    private static Dictionary<string, JsonElement> Object(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure("The private native binding is invalid.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!keys.Contains(item.Name, StringComparer.Ordinal) || !fields.TryAdd(item.Name, item.Value)) throw Failure("The private native binding is invalid.");
        return fields;
    }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Failure("The private native binding is invalid.");
    private static void ValidateIdentifier(string value)
    {
        if (value is not { Length: > 0 and <= NodeState.MaxIdentifierLength } || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new ArgumentException("A bounded native scope identifier is required.");
    }
    private static bool TryNativeId(string value, out Guid id)
    {
        id = default;
        return value is { Length: 32 or 36 } && Guid.TryParseExact(value, value.Length == 32 ? "N" : "D", out id);
    }
    private static string CanonicalDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > 32768 || path.Any(char.IsControl)) throw new ArgumentException("An absolute native event directory is required.");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path)) throw Failure("The private connection or native checkout directory is missing.");
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            // Resolve the actual directory instead of uppercasing: NTFS can enable case sensitivity per directory.
            using var handle = OpenDirectory(path, 0, 7, nint.Zero, 3, 0x02000000, nint.Zero);
            if (handle.IsInvalid) throw Failure("The native directory could not be canonicalized.");
            var name = new StringBuilder(32768);
            var length = FinalDirectory(handle, name, (uint)name.Capacity, 0);
            if (length == 0 || length >= name.Capacity) throw Failure("The native directory could not be canonicalized.");
            path = name.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            path = Path.TrimEndingDirectorySeparator(path);
        }
        else
        {
            // A lexical Unix path does not identify its physical directory when any ancestor is a symlink.
            // Refuse such paths rather than opening a second lock namespace through an alias.
            for (DirectoryInfo? ancestor = new(path); ancestor is not null; ancestor = ancestor.Parent) RejectLink(ancestor.FullName);
        }
        return path;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security, uint mode, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint FinalDirectory(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    private static void RejectLink(string path, bool allowMissing = false)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Failure("Linked private native binding paths are not supported.");
        }
        catch (FileNotFoundException) when (allowMissing) { }
    }
    private static void RequirePrivate(string path, int maximum)
    {
        RejectLink(path);
        if (new FileInfo(path).Length > maximum || !NodeState.IsPrivateFile(path)) throw Failure("The native binding file is not private or exceeds its bound.");
    }
    private static string ConnectionIdentity(string folder) =>
        (NodeState.HasIdentityFiles(folder) ? NodeState.NativeCredentialIdentity(folder) : LocalWakeConnection.NativeCredentialIdentity(folder)) ??
        throw Failure("A verified private connection is required for native recovery.");
    internal static string CredentialDigest(string kind, string hub, string? pin, string token) =>
        Digest(kind, hub, pin ?? "", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
    private static string Digest(params string[] fields)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var field in fields) { var encoded = utf8.GetBytes(field); writer.Write(encoded.Length); writer.Write(encoded); }
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
    private static IOException Failure(string message) => new(message);
    public async ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref owner, null);
        if (lease is not null) await lease.DisposeAsync();
    }
    public override string ToString() => "Native join binding (private)";
}

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;

namespace CoCern.Adapter.Node;

internal sealed record EditClaimLease(string ResourceId, string RequestId, long? Generation, bool NewlyAcquired, string Outcome);
internal sealed record EditClaimIntent(string Id, IReadOnlyList<EditClaimLease> Targets, string Outcome);

// The directory must come from the accepted owner-private native storage boundary,
// never from event JSON. No token, proof or file content belongs in these records.
internal sealed class EditClaimLedger(string privateDirectory)
{
    private const int ByteLimit = 262144;
    private const int RetiredBytes = 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Document(int Schema, Dictionary<string, EditClaimIntent> Intents, byte[]? Retired = null);

    public bool Save(EditClaimIntent intent)
    {
        if (!Valid(intent)) return false;
        try
        {
            var existed = Directory.Exists(privateDirectory);
            var root = Prepare(create: true);
            using var gate = OpenPrivate(Path.Combine(root, "edit-claims.lock"), create: true);
            var path = Path.Combine(root, "edit-claims.json");
            if (existed && !File.Exists(path)) return false;
            var document = Load(path);
            if (document.Intents.TryGetValue(intent.Id, out var prior))
            {
                if (prior.Targets.Count != intent.Targets.Count) return false;
                for (var i = 0; i < prior.Targets.Count; i++)
                {
                    var a = prior.Targets[i]; var b = intent.Targets[i];
                    if (a.ResourceId != b.ResourceId || a.RequestId != b.RequestId ||
                        a.Generation is not null && (a.Generation != b.Generation || a.NewlyAcquired != b.NewlyAcquired)) return false;
                }
            }
            else if (document.Intents.Count >= 128 || WasRetired(document.Retired!, intent.Id)) return false;
            document.Intents[intent.Id] = intent;
            return Write(root, path, document);
        }
        catch (Exception error) when (Unavailable(error)) { return false; }
    }

    public EditClaimIntent? Read(string intentId)
    {
        try
        {
            var root = Prepare(create: false);
            using var gate = OpenPrivate(Path.Combine(root, "edit-claims.lock"), create: true);
            return Load(Path.Combine(root, "edit-claims.json"), allowMissing: false).Intents.GetValueOrDefault(intentId);
        }
        catch (Exception error) when (Unavailable(error)) { return null; }
    }

    // A missing/unreadable ledger is uncertainty, never an empty set of ownership obligations.
    public IReadOnlyList<EditClaimIntent>? Snapshot()
    {
        try
        {
            var root = Prepare(create: false);
            using var gate = OpenPrivate(Path.Combine(root, "edit-claims.lock"), create: true);
            return Load(Path.Combine(root, "edit-claims.json"), allowMissing: false).Intents.Values.OrderBy(intent => intent.Id, StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (Unavailable(error)) { return null; }
    }

    public bool RetireReleased(string intentId)
    {
        if (!Bounded(intentId, 128)) return false;
        try
        {
            var root = Prepare(create: false);
            using var gate = OpenPrivate(Path.Combine(root, "edit-claims.lock"), create: true);
            var path = Path.Combine(root, "edit-claims.json");
            var document = Load(path, allowMissing: false);
            if (!document.Intents.TryGetValue(intentId, out var intent)) return WasRetired(document.Retired!, intentId);
            if (intent.Outcome != "released" || intent.Targets.Any(target => target.Outcome != "released" || target.Generation is not > 0)) return false;
            document.Intents.Remove(intentId);
            foreach (var bit in RetiredBits(intentId)) document.Retired![bit / 8] |= (byte)(1 << (bit % 8));
            // Removal and the permanent refusal marker commit together. A failure leaves the old full receipt intact.
            return Write(root, path, document);
        }
        catch (Exception error) when (Unavailable(error)) { return false; }
    }

    // Fixed monotonic refusal filter: collisions only refuse a new intent. Bits are never cleared in this native generation.
    // Saturation reduces availability, never replay safety; a later native generation uses a different private ledger directory.
    private static bool WasRetired(byte[] filter, string id) => RetiredBits(id).All(bit => (filter[bit / 8] & (1 << (bit % 8))) != 0);
    private static int[] RetiredBits(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Enumerable.Range(0, 4).Select(index => (int)BinaryPrimitives.ReadUInt16LittleEndian(hash.AsSpan(index * 2, 2))).ToArray();
    }

    private static bool Write(string root, string path, Document document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > ByteLimit) return false;
        string? temporary = Path.Combine(root, ".edit-claims-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = OpenPrivate(temporary, create: true)) { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        finally { if (temporary is not null) { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } } }
    }

    private string Prepare(bool create)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(privateDirectory)) throw new PlatformNotSupportedException();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(privateDirectory));
        RejectLinks(root);
        if (!Directory.Exists(root))
        {
            if (!create || !Directory.Exists(Path.GetDirectoryName(root))) throw new IOException("Private directory unavailable");
            var security = new DirectorySecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        Privacy(new DirectoryInfo(root));
        return root;
    }

    private static FileStream OpenPrivate(string path, bool create)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RejectLinks(path);
        FileStream stream;
        if (!File.Exists(path))
        {
            if (!create) throw new FileNotFoundException();
            var security = new FileSecurity(); var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
        }
        else
        {
            Privacy(new FileInfo(path));
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        if (EditClaimResolver.SingleRegularLink(stream.SafeFileHandle)) return stream;
        stream.Dispose(); throw new IOException("Unsupported private file identity");
    }

    private static Document Load(string path, bool allowMissing = true)
    {
        if (!File.Exists(path)) return allowMissing ? new(2, new(StringComparer.Ordinal), new byte[RetiredBytes]) : throw new FileNotFoundException();
        using var input = OpenPrivate(path, create: false);
        if (input.Length > ByteLimit) throw new InvalidDataException();
        using var memory = new MemoryStream(); input.CopyTo(memory);
        using var parsed = JsonDocument.Parse(memory.ToArray());
        CheckDuplicates(parsed.RootElement);
        var document = parsed.RootElement.Deserialize<Document>(Json) ?? throw new InvalidDataException();
        if (document.Schema is not (1 or 2) || document.Schema == 1 && document.Retired is not null
            || document.Schema == 2 && document.Retired?.Length != RetiredBytes || document.Intents is null || document.Intents.Count > 128 ||
            document.Intents.Any(x => x.Value is null || x.Key != x.Value.Id || !Valid(x.Value))) throw new InvalidDataException();
        return document with { Schema = 2, Retired = document.Retired ?? new byte[RetiredBytes] };
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException(); CheckDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }

    private static bool Valid(EditClaimIntent intent) => intent is not null && Bounded(intent.Id, 128) &&
        Bounded(intent.Outcome, 64) && intent.Targets is { Count: > 0 and <= 128 } &&
        intent.Targets.All(x => x is not null && Bounded(x.ResourceId, 512) && Bounded(x.RequestId, 128) &&
            Bounded(x.Outcome, 64) && (x.Generation is null || x.Generation > 0)) &&
        intent.Targets.Select(x => x.ResourceId).Distinct(StringComparer.Ordinal).Count() == intent.Targets.Count &&
        intent.Targets.Select(x => x.RequestId).Distinct(StringComparer.Ordinal).Count() == intent.Targets.Count;
    private static bool Bounded(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate);

    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null) throw new InvalidDataException();
    }
    private static void Privacy(FileSystemInfo info)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User!;
        var security = info is DirectoryInfo directory ? (FileSystemSecurity)directory.GetAccessControl() : ((FileInfo)info).GetAccessControl();
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException();
        var fullControl = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!user.Equals(rule.IdentityReference)) throw new UnauthorizedAccessException();
            fullControl |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
        }
        if (!fullControl) throw new UnauthorizedAccessException();
    }
    private static bool Unavailable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        ArgumentException or NotSupportedException or System.Security.SecurityException;
}

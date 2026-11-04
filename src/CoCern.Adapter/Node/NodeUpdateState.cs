using CoCern.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoCern.Adapter.Node;

public enum NodeUpdatePhase { Committed, Starting, Rollback }

public sealed record NodeUpdateRecord(int Schema, NodeImage Current, NodeImage? Previous, NodeImage? Pending, Guid? ActivationNonce, NodeUpdatePhase Phase, string? LastFailedReleaseId, DateTimeOffset? LastFailedAtUtc);

public enum NodeUpdateReadStatus { Missing, Valid, Corrupt }

public sealed record NodeUpdateReadResult(NodeUpdateReadStatus Status, NodeUpdateRecord? Record);

public static class NodeUpdateState
{
    private const int JournalLimit = 65536;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    public static NodeUpdateReadResult Read(string updateRoot)
    {
        try
        {
            var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: false);
            var primary = ReadRecord(root, "state.json", out var primaryExists);
            if (primary is not null) return new(NodeUpdateReadStatus.Valid, primary);
            var committed = ReadRecord(root, "committed.json", out var committedExists);
            if (committed is { Phase: NodeUpdatePhase.Committed }) return new(NodeUpdateReadStatus.Valid, committed);
            return new(primaryExists || committedExists ? NodeUpdateReadStatus.Corrupt : NodeUpdateReadStatus.Missing, null);
        }
        catch (Exception exception) when (InvalidJournal(exception)) { return new(NodeUpdateReadStatus.Corrupt, null); }
    }

    public static void Write(string updateRoot, NodeUpdateRecord record)
    {
        try
        {
            var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: true);
            if (!Valid(root, record)) throw new InvalidDataException();
            var existing = Read(root);
            if (existing.Status == NodeUpdateReadStatus.Corrupt) throw new InvalidDataException();
            if (record.Phase != NodeUpdatePhase.Committed)
            {
                // A validated primary also retains the last committed Current/Previous during Starting/Rollback.
                var committed = existing.Record is { } previous
                    ? previous with { Phase = NodeUpdatePhase.Committed, Pending = null, ActivationNonce = null }
                    : null;
                if (committed is not { Phase: NodeUpdatePhase.Committed } || committed.Current.ContentId != record.Current.ContentId) throw new InvalidDataException();
                AtomicWrite(root, "committed.json", committed);
            }
            AtomicWrite(root, "state.json", record);
            if (record.Phase == NodeUpdatePhase.Committed) AtomicWrite(root, "committed.json", record);
        }
        catch (Exception exception) when (InvalidJournal(exception)) { throw new InvalidDataException("node-journal-invalid"); }
    }

    private static NodeUpdateRecord? ReadRecord(string root, string name, out bool exists)
    {
        var path = Path.Combine(root, name);
        exists = true;
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { exists = false; return null; }
        catch (DirectoryNotFoundException) { exists = false; return null; }
        catch (Exception exception) when (InvalidJournal(exception)) { return null; }
        try
        {
            NodePackage.ValidatePrivateFile(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > JournalLimit) return null;
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) return null;
            var record = JsonSerializer.Deserialize<NodeUpdateRecord>(bytes, Json);
            return record is not null && Valid(root, record) ? record : null;
        }
        catch (Exception exception) when (InvalidJournal(exception)) { return null; }
    }

    private static bool Valid(string root, NodeUpdateRecord? record)
    {
        if (record is null || record.Schema != 1 || !Enum.IsDefined(record.Phase) || !NodePackage.ValidateStoredImage(root, record.Current)) return false;
        if (record.Previous is not null && !NodePackage.ValidateStoredImage(root, record.Previous)) return false;
        if (record.Phase == NodeUpdatePhase.Committed)
        {
            if (record.Pending is not null || record.ActivationNonce is not null) return false;
        }
        else if (record.Pending is null || record.ActivationNonce is not { } nonce || nonce == Guid.Empty || !NodePackage.ValidateStoredImage(root, record.Pending)) return false;
        if ((record.LastFailedReleaseId is null) != (record.LastFailedAtUtc is null)) return false;
        return record.LastFailedReleaseId is null || record.LastFailedReleaseId is { Length: 64 } hash && hash.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');
    }

    private static void AtomicWrite(string root, string name, NodeUpdateRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > JournalLimit) throw new InvalidDataException();
        var path = Path.Combine(root, name);
        var temporary = Path.Combine(root, ".journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = NodePackage.CreatePrivateFile(temporary))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                NodePackage.ValidatePrivateFile(path);
                File.Replace(temporary, path, null);
            }
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool InvalidJournal(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or OverflowException or System.Security.SecurityException;
}

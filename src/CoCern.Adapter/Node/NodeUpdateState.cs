using CoCern.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoCern.Adapter.Node;

internal sealed record NodeUpdateRequest(NodeImage Candidate);
internal sealed record NodeReady(Guid ActivationNonce, string ContentId, int ProcessId, Guid? LaunchId = null);
internal sealed record NodeWorkerStartupContext(Guid? ActivationNonce, Guid LaunchId, string? FailedReleaseId = null, DateTimeOffset? FailedAtUtc = null, bool OrdinaryStartup = false)
{
    internal string ToArgument() => $"{(OrdinaryStartup ? "v2" : "v1")};{(ActivationNonce is { } nonce ? nonce.ToString("D") : "-")};{LaunchId:D};{FailedReleaseId ?? "-"};{(FailedAtUtc is { } failed ? failed.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}";

    internal static bool TryParse(string value, out NodeWorkerStartupContext? context)
    {
        context = null;
        if (value is null || value.Length > 192) return false;
        if (Guid.TryParseExact(value, "D", out var legacy) && legacy != Guid.Empty) { context = new(legacy, Guid.Empty); return true; }
        var fields = value.Split(';');
        if (fields.Length != 5 || fields[0] is not ("v1" or "v2") || !Guid.TryParseExact(fields[2], "D", out var launch) || launch == Guid.Empty) return false;
        var ordinary = fields[0] == "v2";
        if (ordinary && (fields[1] != "-" || fields[3] != "-" || fields[4] != "-")) return false;
        Guid? nonce = null;
        if (fields[1] != "-")
        {
            if (!Guid.TryParseExact(fields[1], "D", out var parsed) || parsed == Guid.Empty) return false;
            nonce = parsed;
        }
        if (fields[3] == "-" && fields[4] == "-") { context = new(nonce, launch, OrdinaryStartup: ordinary); return true; }
        if (fields[3].Length != 64 || !fields[3].All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F') ||
            !long.TryParse(fields[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return false;
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (at > DateTimeOffset.UtcNow.AddMinutes(15)) return false;
        context = new(nonce, launch, fields[3], at);
        return true;
    }
}

public enum NodeUpdatePhase { Committed, Starting, Rollback }

public sealed record NodeUpdateRecord(int Schema, NodeImage Current, NodeImage? Previous, NodeImage? Pending, Guid? ActivationNonce, NodeUpdatePhase Phase, string? LastFailedReleaseId, DateTimeOffset? LastFailedAtUtc);

public enum NodeUpdateReadStatus { Missing, Valid, Corrupt }

public sealed record NodeUpdateReadResult(NodeUpdateReadStatus Status, NodeUpdateRecord? Record);

public static class NodeUpdateState
{
    internal static NodeImage? ValidateWorkerStartup(string nodeFolder, Guid? nonce, string executingFolder)
    {
        try
        {
            if (nonce == Guid.Empty || !Path.IsPathFullyQualified(nodeFolder) || !Path.IsPathFullyQualified(executingFolder)) return null;
            var root = NodePackage.ValidatedUpdateRoot(Path.Combine(nodeFolder, "updates"), create: false);
            var record = Read(root).Record;
            if (record is null || (nonce is not null ? record.ActivationNonce != nonce || record.Phase == NodeUpdatePhase.Committed : record.Phase != NodeUpdatePhase.Committed)) return null;
            var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executingFolder));
            // Starting may retain Current while rollback persistence is unavailable. Both identities
            // require the recorded nonce, their exact canonical folder, and a fresh full rehash.
            var images = record.Phase == NodeUpdatePhase.Starting ? new[] { record.Pending!, record.Current } : new[] { record.Current };
            foreach (var image in images)
                if (string.Equals(actual, Path.Combine(root, "versions", image.ContentId), StringComparison.OrdinalIgnoreCase) && NodePackage.ValidateStoredImage(root, image)) return image;
            return null;
        }
        catch (Exception exception) when (InvalidJournal(exception)) { return null; }
    }
    internal static void WriteRequest(string updateRoot, NodeUpdateRequest request)
    {
        var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: false);
        if (request.Candidate.ReleaseId is null || !NodePackage.ValidateStoredImage(root, request.Candidate)) throw new InvalidDataException("node-request-invalid");
        AtomicWrite(root, "request.json", request);
    }
    internal static NodeUpdateRequest? ReadRequest(string updateRoot)
    {
        var request = ReadHandoff<NodeUpdateRequest>(updateRoot, "request.json");
        try { return request is { Candidate.ReleaseId: not null } && NodePackage.ValidateStoredImage(updateRoot, request.Candidate) ? request : null; }
        catch (Exception exception) when (InvalidJournal(exception)) { return null; }
    }
    internal static void WriteReady(string updateRoot, NodeReady ready)
    {
        var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: false);
        if (!ValidReady(ready)) throw new InvalidDataException("node-ready-invalid");
        AtomicWrite(root, "ready.json", ready);
    }
    internal static NodeReady? ReadReady(string updateRoot)
    {
        var ready = ReadHandoff<NodeReady>(updateRoot, "ready.json");
        return ready is not null && ValidReady(ready) ? ready : null;
    }
    /// <summary>
    /// The last release this node refused or failed to stage, kept in <c>updates/refusal.json</c> beside the journal rather than in <c>state.json</c>: the journal
    /// reader disallows unknown members, so a supervisor or rolled-back worker from before this field would read a journal carrying it as corrupt and stop.
    /// Only the worker writes it, and nothing in it decides what runs.
    /// </summary>
    internal static void WriteRefusal(string updateRoot, NodeUpdateRefusal refusal)
    {
        var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: false);
        if (!ValidRefusal(refusal)) throw new InvalidDataException("node-refusal-invalid");
        AtomicWrite(root, RefusalFile, refusal);
    }
    internal static NodeUpdateRefusal? ReadRefusal(string updateRoot)
    {
        var refusal = ReadHandoff<NodeUpdateRefusal>(updateRoot, RefusalFile);
        return refusal is not null && ValidRefusal(refusal) ? refusal : null;
    }
    internal static void ClearRefusal(string updateRoot)
    {
        var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: false);
        var path = Path.Combine(root, RefusalFile);
        if (!File.Exists(path)) return;
        NodePackage.ValidatePrivateFile(path);
        File.Delete(path);
    }
    internal static bool ValidRefusal(NodeUpdateRefusal? refusal) => refusal is { ReleaseId: { Length: 64 } id } && id.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F') && NodeUpdateRefusals.IsCode(refusal.Reason);
    private const string RefusalFile = "refusal.json";
    private static bool ValidReady(NodeReady ready) => ready.ActivationNonce != Guid.Empty && ready.ProcessId > 0 && ready.ContentId is { Length: 64 } id && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    private static T? ReadHandoff<T>(string updateRoot, string name) where T : class
    {
        try
        {
            var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: false);
            var path = Path.Combine(root, name);
            NodePackage.ValidatePrivateFile(path, replaceableRead: true);
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (file.Length is <= 0 or > JournalLimit) return null;
                bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
                if (file.ReadByte() != -1) return null;
            }
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (Exception exception) when (InvalidJournal(exception)) { return null; }
    }
    private const int JournalLimit = 65536;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    /// <summary>
    /// Deletes stored images and staging scratch nothing needs (see <see cref="NodePackage.PruneVersions"/>). Kept: <paramref name="running"/>, and every image
    /// either journal file names as Current, Previous or Pending, the requested candidate and the ready image. When a journal file exists but cannot be read,
    /// nothing is known to be unused and nothing is deleted. Never throws; a failure is reported by a fixed code.
    /// </summary>
    internal static void PruneVersions(string updateRoot, string? running)
    {
        try
        {
            var root = NodePackage.ValidatedUpdateRoot(updateRoot, create: false);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (running is not null) keep.Add(running);
            var records = 0;
            foreach (var name in new[] { "state.json", "committed.json" })
            {
                var record = ReadRecord(root, name, out var exists);
                if (exists && record is null) { Console.Error.WriteLine("node-update:prune-skipped"); return; }
                if (record is null) continue;
                records++;
                foreach (var image in new[] { record.Current, record.Previous, record.Pending }) if (image is not null) keep.Add(image.ContentId);
            }
            if (records == 0) return;
            foreach (var name in new[] { "request.json", "ready.json" })
            {
                if (!File.Exists(Path.Combine(root, name))) continue;
                var id = name == "request.json" ? ReadHandoff<NodeUpdateRequest>(root, name)?.Candidate?.ContentId : ReadHandoff<NodeReady>(root, name)?.ContentId;
                if (id is null) { Console.Error.WriteLine("node-update:prune-skipped"); return; }
                keep.Add(id);
            }
            NodePackage.PruneVersions(root, keep, DateTime.UtcNow);
        }
        catch (Exception exception) when (InvalidJournal(exception)) { Console.Error.WriteLine("node-update:prune-retained"); }
    }

    internal static NodeUpdateRecord? ReadPrimary(string updateRoot)
    {
        try { return ReadRecord(NodePackage.ValidatedUpdateRoot(updateRoot, create: false), "state.json", out _); }
        catch (Exception exception) when (InvalidJournal(exception)) { return null; }
    }

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
            // A promotion or rollback rehashes the images it records; only reads use remembered validations.
            if (!Valid(root, record, full: true)) throw new InvalidDataException();
            var existing = Read(root);
            if (existing.Status == NodeUpdateReadStatus.Corrupt) throw new InvalidDataException();
            // Pending is bounded recovery evidence on read; a new activation still needs all candidate bytes.
            if (record.Phase == NodeUpdatePhase.Starting && !NodePackage.ValidateStoredImage(root, record.Pending)) throw new InvalidDataException();
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
            NodePackage.ValidatePrivateFile(path, replaceableRead: true);
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (file.Length is <= 0 or > JournalLimit) return null;
                bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
                if (file.ReadByte() != -1) return null;
            }
            var record = JsonSerializer.Deserialize<NodeUpdateRecord>(bytes, Json);
            // Read on every announce and wake pass: an image this process fully validated stands while its folder listing is unchanged (see
            // NodePackage.ValidateStoredImage). Launch, startup, handoff and Write still validate in full.
            return record is not null && Valid(root, record, full: false) ? record : null;
        }
        catch (Exception exception) when (InvalidJournal(exception)) { return null; }
    }

    private static bool Valid(string root, NodeUpdateRecord? record, bool full)
    {
        if (record is null || record.Schema != 1 || !Enum.IsDefined(record.Phase) || !NodePackage.ValidateStoredImage(root, record.Current, full)) return false;
        if (record.Previous is not null && !NodePackage.ValidateStoredImage(root, record.Previous, full)) return false;
        if (record.Phase == NodeUpdatePhase.Committed)
        {
            if (record.Pending is not null || record.ActivationNonce is not null) return false;
        }
        else if (record.Pending is null || record.ActivationNonce is not { } nonce || nonce == Guid.Empty || !NodePackage.ValidImageDescriptor(record.Pending)) return false;
        if ((record.LastFailedReleaseId is null) != (record.LastFailedAtUtc is null)) return false;
        return record.LastFailedReleaseId is null || record.LastFailedReleaseId is { Length: 64 } hash && hash.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');
    }

    private static void AtomicWrite<T>(string root, string name, T record)
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

using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoCern.Host.Development;

/// <summary>
/// Removes old immutable version folders without ever breaking one in use. A candidate is first renamed to a <c>.deleting-&lt;guid&gt;</c> sibling:
/// Windows refuses that rename while any file inside is open or loaded (a running MCP backend, launcher or Stop-hook waiter, or a zip being read),
/// so a folder in use is left exactly as it was. Only a renamed folder, which no launch or pointer can name any more, is deleted. Reparse points
/// are never followed, and nothing outside the versions folder is renamed or deleted.
/// </summary>
internal static partial class VersionFolders
{
    internal const string DeletingPrefix = ".deleting-";
    internal const string StagingPrefix = ".staging-";
    private const long MaxReferenceFile = 64L * 1024 * 1024;
    private const long MaxFolderFile = 1024 * 1024;
    private const int MaxFolderFiles = 5000;

    internal static bool IsHash(string? name) => name is { Length: 64 } && name.All(char.IsAsciiHexDigit);

    internal static bool IsLink(FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>The newer of a folder's creation and last write times.</summary>
    internal static DateTime Newest(DirectoryInfo folder) => folder.CreationTimeUtc > folder.LastWriteTimeUtc ? folder.CreationTimeUtc : folder.LastWriteTimeUtc;

    /// <summary>The versions folder, when it and its parent exist and are real directories rather than links.</summary>
    internal static DirectoryInfo? Open(string versions)
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(versions)));
        if (!folder.Exists || IsLink(folder) || folder.Parent is not { } parent || IsLink(parent)) return null;
        return folder;
    }

    /// <summary>The direct child folders of <paramref name="versions"/> that are real directories (links are never candidates).</summary>
    internal static List<DirectoryInfo> Children(DirectoryInfo versions) =>
        [.. versions.EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false }).Where(folder => !IsLink(folder))];

    /// <summary>
    /// Renames each candidate to a fresh <c>.deleting-</c> sibling and returns the new paths. A candidate whose rename fails is in use (or cannot be
    /// moved) and is left untouched; <paramref name="inUse"/> counts them.
    /// </summary>
    internal static List<string> DetachAll(DirectoryInfo versions, IEnumerable<DirectoryInfo> candidates, out int inUse)
    {
        var detached = new List<string>();
        inUse = 0;
        foreach (var folder in candidates)
        {
            folder.Refresh();
            if (!folder.Exists || IsLink(folder) || !IsChild(versions, folder.FullName)) continue;
            var target = Path.Combine(versions.FullName, DeletingPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(folder.FullName, target);
                detached.Add(target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { inUse++; }
        }
        return detached;
    }

    /// <summary>Earlier <c>.deleting-</c> folders whose delete did not finish; nothing can launch from them, so they are deleted directly.</summary>
    internal static IEnumerable<string> Leftovers(IEnumerable<DirectoryInfo> children) =>
        children.Where(folder => folder.Name.StartsWith(DeletingPrefix, StringComparison.Ordinal)).Select(folder => folder.FullName);

    /// <summary>Deletes detached folders; returns how many were removed and how many could not be (those stay for a later pass).</summary>
    internal static (int Removed, int Failed) DeleteAll(DirectoryInfo versions, IEnumerable<string> detached)
    {
        int removed = 0, failed = 0;
        foreach (var path in detached.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Refuse anything that is not a detached folder directly inside this versions folder.
            if (!IsChild(versions, path) || !Path.GetFileName(path).StartsWith(DeletingPrefix, StringComparison.Ordinal)) { failed++; continue; }
            try
            {
                var folder = new DirectoryInfo(path);
                if (folder.Exists) RemoveTree(folder);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed++; }
        }
        return (removed, failed);
    }

    /// <summary>Deletes a tree without following links: a link inside is removed itself, never entered.</summary>
    private static void RemoveTree(DirectoryInfo folder)
    {
        if (IsLink(folder)) { folder.Delete(); return; }
        foreach (var entry in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }).ToList())
        {
            if (entry is DirectoryInfo child) { RemoveTree(child); continue; }
            if (!IsLink(entry) && (entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
        folder.Delete();
    }

    private static bool IsChild(DirectoryInfo versions, string path) =>
        string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))), Path.TrimEndingDirectorySeparator(versions.FullName), StringComparison.OrdinalIgnoreCase);

    /// <summary>A brief attempt at a publish lock (the same file the publisher holds exclusively); null when someone else holds it.</summary>
    internal static FileStream? TryLock(string path, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(100); }
            catch (IOException) { return null; }
        }
    }

    /// <summary>The <c>version</c> in a <c>current.json</c> pointer, when it is a well-formed hash; read without blocking its atomic replacement.</summary>
    internal static string? PointerVersion(string pointer)
    {
        try
        {
            if (!File.Exists(pointer) || new FileInfo(pointer).Length > 4096) return null;
            using var document = JsonDocument.Parse(ReadShared(pointer));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("version", out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() is { } version && IsHash(version) ? version : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Every 64-hex token in <paramref name="files"/>, in small files under <paramref name="folders"/>, and in <paramref name="text"/>. Matching any
    /// such token, not only a parsed path, keeps a version however a client escaped or quoted it. Null when a listed file exists but cannot be read,
    /// so the caller keeps everything rather than miss a launcher reference.
    /// </summary>
    internal static HashSet<string>? ReferencedHashes(IEnumerable<string> files, IEnumerable<string> folders, IEnumerable<string?> text)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string? content)
        {
            if (content is null) return;
            foreach (Match match in Hash().Matches(content)) found.Add(match.Value);
        }
        foreach (var value in text) Collect(value);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                if (info.Length > MaxReferenceFile) return null;
                Collect(ReadShared(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        }
        // Staged native candidates and wake folders hold hook command lines. A file held open exclusively there is a lock or state file, not a command.
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var root = new DirectoryInfo(folder);
                if (!root.Exists || IsLink(root)) continue;
                foreach (var file in root.EnumerateFiles("*", options).Take(MaxFolderFiles))
                {
                    if (file.Length > MaxFolderFile) continue;
                    try { Collect(ReadShared(file.FullName)); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        }
        return found;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])")]
    private static partial Regex Hash();
}

/// <summary>
/// Prunes <c>artifacts/development-adapter/versions</c>. Kept: the version <c>current.json</c> names; any version a client configuration, hook
/// command, staged native candidate or hub setting mentions (the launcher stays in its original immutable version, see the README); the
/// <c>keepRecent</c> newest by creation time; and anything created or written in the last 24 hours. Every other version folder, and a
/// <c>.staging-</c> leftover older than a day, is removed with <see cref="VersionFolders"/>' rename-then-delete, so a folder in use stays.
/// </summary>
internal static class DevelopmentAdapterVersions
{
    private static readonly TimeSpan Recent = TimeSpan.FromHours(24);

    public static void Prune(string deploymentRoot, int keepRecent, IEnumerable<string> referenceFiles, IEnumerable<string> referenceFolders, IEnumerable<string?> referenceText, ILogger logger)
    {
        var root = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(deploymentRoot)));
        if (!root.Exists || VersionFolders.IsLink(root) || VersionFolders.Open(Path.Combine(root.FullName, "versions")) is not { } versions) return;
        // Read before taking the lock, which then covers only quick work: a deployment waits at most 10 seconds for it.
        if (VersionFolders.ReferencedHashes(referenceFiles, referenceFolders, referenceText) is not { } referenced)
        {
            logger.LogWarning("Development adapter version pruning skipped: a client configuration could not be read. All versions are kept.");
            return;
        }
        List<string> detached;
        int inUse, kept;
        using (var gate = VersionFolders.TryLock(Path.Combine(root.FullName, "publish.lock"), TimeSpan.FromSeconds(2)))
        {
            if (gate is null) { logger.LogInformation("Development adapter version pruning skipped: a deployment is running."); return; }
            // Under the deployment lock, current.json cannot move to a version that is about to be renamed.
            if (VersionFolders.PointerVersion(Path.Combine(root.FullName, "current.json")) is not { } current)
            {
                logger.LogInformation("Development adapter version pruning skipped: there is no valid current version.");
                return;
            }
            var now = DateTime.UtcNow;
            var children = VersionFolders.Children(versions);
            var hashes = children.Where(folder => VersionFolders.IsHash(folder.Name)).OrderByDescending(folder => folder.CreationTimeUtc).ToList();
            var keep = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase) { current };
            keep.UnionWith(hashes.Take(Math.Max(1, keepRecent)).Select(folder => folder.Name));
            keep.UnionWith(hashes.Where(folder => VersionFolders.Newest(folder) > now - Recent).Select(folder => folder.Name));
            var old = hashes.Where(folder => !keep.Contains(folder.Name)).ToList();
            var staging = children.Where(folder => folder.Name.StartsWith(VersionFolders.StagingPrefix, StringComparison.Ordinal) && VersionFolders.Newest(folder) < now - Recent);
            detached = VersionFolders.DetachAll(versions, old.Concat(staging), out inUse);
            detached.AddRange(VersionFolders.Leftovers(children));
            kept = hashes.Count - old.Count;
        }
        // Deleting gigabytes happens outside the lock; the renamed folders are invisible to deployments and launchers.
        var (removed, failed) = VersionFolders.DeleteAll(versions, detached);
        if (removed + inUse + failed > 0)
            logger.LogInformation("Development adapter versions pruned: {Removed} removed, {InUse} in use and kept, {Failed} could not be deleted yet, {Kept} kept by rule.", removed, inUse, failed, kept);
    }
}

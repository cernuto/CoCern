using System.Collections;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using CoCern.Contracts;
using CoCern.Core;

namespace CoCern.Host;

/// <summary>
/// The doc cache's repositories: the configured ones plus those found under <c>Relay:DocCache:Roots</c> or learned from sessions'
/// workspaces, up to <see cref="RelayLimits.MaxDiscoveredDocRepositories"/>. A configured ID always wins. The roots are the only allow-list:
/// nothing outside them is ever added. A discovered repository is the user's own checkout, so it is never fetched and, as for every
/// repository, only its committed content is read.
/// </summary>
internal sealed class DocRepositoryRegistry
{
    private static readonly Regex FolderName = new("^[a-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    // Junctions and symlinks are never followed, so a scan cannot loop or leave its root.
    private static readonly EnumerationOptions Folders = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private readonly DocCacheSettings settings;
    private readonly GitCli git;
    private readonly ILogger<DocRepositoryRegistry> logger;
    private readonly Lock gate = new();
    // One scan at a time: concurrent timer and test runs would otherwise start the same git commands twice.
    private readonly SemaphoreSlim scanning = new(1, 1);
    private ImmutableDictionary<string, DocRepository> discovered = ImmutableDictionary.Create<string, DocRepository>(StringComparer.Ordinal);
    private DocRepository[] all;
    private int capNoted;

    public DocRepositoryRegistry(DocCacheSettings settings, GitCli git, ILogger<DocRepositoryRegistry> logger)
    {
        (this.settings, this.git, this.logger) = (settings, git, logger);
        all = [.. settings.Repositories];
        Ids = new IdView(this);
    }

    /// <summary>Configured first, then discovered in the order they were added. A snapshot: later additions do not change it.</summary>
    public IReadOnlyCollection<DocRepository> Repositories => Volatile.Read(ref all);

    /// <summary>A live view of every repository ID, for the doc tools, so a repository found after startup is searchable at once.</summary>
    public IReadOnlyCollection<string> Ids { get; }

    public DocRepository? Find(string id) => settings.Find(id) ?? (Volatile.Read(ref discovered).TryGetValue(id, out var found) ? found : null);

    /// <summary>
    /// Finds the git repositories (a folder holding a <c>.git</c> folder or file) under every root, down to <see cref="DocCacheSettings.RootDepth"/>
    /// levels and never inside one already found, and adds the new ones. The ID is the folder name in lower case; a name that is not an ID,
    /// an ID already taken (the first by ordinal path wins), a path already registered and a detached HEAD are skipped.
    /// </summary>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled || settings.Roots.Count == 0) return;
        await scanning.WaitAsync(cancellationToken);
        try
        {
            foreach (var path in settings.Roots.SelectMany(root => Candidates(root, settings.RootDepth)).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IdOf(path) is not { } id || Find(id) is not null || Registered(path)) continue;
                if (Full()) { NoteCap(path); break; }
                if (await BranchAsync(path, cancellationToken) is { } branch) Add(Discovered(id, path, branch));
            }
        }
        finally { scanning.Release(); }
    }

    /// <summary>
    /// A session's workspace: an existing git top-level strictly inside a root, given as a full path (forward slashes allowed, no <c>..</c>).
    /// It is registered under <paramref name="repositoryId"/> when that ID has no repository yet, else under its folder-name ID (as a scan
    /// names it). Returns the ID it is registered under (also when it already was) and whether this call added it; null when refused.
    /// Learning adds docs, never access: the caller's repository scope still decides who may read them.
    /// </summary>
    public async Task<(string? Id, bool Added)> LearnAsync(string repositoryId, string workspace, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || settings.Roots.Count == 0 || string.IsNullOrWhiteSpace(repositoryId) || repositoryId.Length > RelayLimits.MaxIdentifierLength) return (null, false);
        if (Normalize(workspace) is not { } path || settings.Roots.FirstOrDefault(candidate => Inside(path, candidate)) is not { } root || !IsRepository(path) || Linked(path, root)) return (null, false);
        var id = repositoryId;
        if (Find(repositoryId) is { } taken)
        {
            if (SamePath(taken.ClonePath, path)) return (repositoryId, false);
            if (IdOf(path) is not { } named) return (null, false);
            if (Find(named) is { } existing) return (SamePath(existing.ClonePath, path) ? named : null, false);
            id = named;
        }
        if (await BranchAsync(path, cancellationToken) is not { } branch) return (null, false);
        if (Add(Discovered(id, path, branch))) return (id, true);
        // Another call registered it first, or the cap is reached.
        return (Find(id) is { } raced && SamePath(raced.ClonePath, path) ? id : null, false);
    }

    /// <summary>Adds a discovered repository unless its ID is taken or the cap is reached; checked and added under one lock, so concurrent scans never pass the cap.</summary>
    private bool Add(DocRepository repository)
    {
        lock (gate)
        {
            if (settings.Find(repository.Id) is not null || discovered.ContainsKey(repository.Id)) return false;
            if (discovered.Count >= RelayLimits.MaxDiscoveredDocRepositories) { NoteCap(repository.ClonePath); return false; }
            // Readers take the snapshots without the lock; each is replaced whole.
            DocRepository[] next = [.. all, repository];
            Volatile.Write(ref discovered, discovered.Add(repository.Id, repository));
            Volatile.Write(ref all, next);
        }
        logger.LogInformation("Doc cache: found {Repository} at {Path} on {Branch}.", repository.Id, repository.ClonePath, repository.Branch);
        return true;
    }

    private bool Full() => Volatile.Read(ref discovered).Count >= RelayLimits.MaxDiscoveredDocRepositories;

    /// <summary>Said once per hub run, not on every scan.</summary>
    private void NoteCap(string path)
    {
        if (Interlocked.Exchange(ref capNoted, 1) == 0)
            logger.LogWarning("The doc cache has found {Limit} repositories, its limit; {Path} and any others are not indexed.", RelayLimits.MaxDiscoveredDocRepositories, path);
    }

    private bool Registered(string path) => Volatile.Read(ref all).Any(repository => SamePath(repository.ClonePath, path));

    private static DocRepository Discovered(string id, string path, string branch) =>
        new(id, path, branch, "origin", DocFormats.DefaultPatterns.Select(glob => new DocPattern(glob, DocFormats.DefaultSpec)).ToArray(), [.. DocFormats.DefaultExcludes], null, Fetch: false);

    /// <summary>The checked-out branch; null for a detached HEAD, a name unsafe in a ref, or git failing.</summary>
    private async Task<string?> BranchAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await git.RunAsync(path, ["symbolic-ref", "--short", "-q", "HEAD"], 1_024, null, cancellationToken);
            var branch = result.Text.Trim();
            return result.ExitCode == 0 && DocGlob.SafeRef(branch) ? branch : null;
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning("Doc cache: could not read the branch of {Path}. {Reason}", path, exception.Message);
            return null;
        }
    }

    /// <summary>Repository folders from one to <paramref name="depth"/> levels below <paramref name="root"/>; a found repository is not searched further.</summary>
    private static List<string> Candidates(string root, int depth)
    {
        var found = new List<string>();
        var level = new List<string> { root };
        for (var i = 0; i < depth && level.Count > 0; i++)
        {
            var next = new List<string>();
            foreach (var child in level.SelectMany(folder => Children(folder)))
                if (IsRepository(child)) found.Add(child); else next.Add(child);
            level = next;
        }
        return found;
    }

    private static string[] Children(string folder)
    {
        try { return Directory.EnumerateDirectories(folder, "*", Folders).Where(child => !string.Equals(Path.GetFileName(child), ".git", PathComparison)).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsRepository(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static string? IdOf(string path) => Path.GetFileName(path).ToLowerInvariant() is var id && FolderName.IsMatch(id) ? id : null;

    /// <summary>A full path with no <c>..</c> segment, in this system's form without a trailing separator; null otherwise.</summary>
    private static string? Normalize(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || workspace.Contains('\0') || !Path.IsPathFullyQualified(workspace) || workspace.Split('/', '\\').Contains("..")) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Strictly below the root: the root itself is never a workspace.</summary>
    private static bool Inside(string path, string root)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, PathComparison);
    }

    /// <summary>Whether a junction or symlink sits between the root and the workspace, which could lead the path out of the root.</summary>
    private static bool Linked(string path, string root)
    {
        var top = Path.TrimEndingDirectorySeparator(root);
        try
        {
            for (var folder = new DirectoryInfo(path); folder is not null && folder.FullName.Length > top.Length; folder = folder.Parent)
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);

    private sealed class IdView(DocRepositoryRegistry registry) : IReadOnlyCollection<string>
    {
        public int Count => registry.Repositories.Count;
        public IEnumerator<string> GetEnumerator() => registry.Repositories.Select(repository => repository.Id).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

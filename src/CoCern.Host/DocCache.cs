using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Orleans;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Core;

namespace CoCern.Host;

/// <summary>One include pattern and the spec name its files belong to.</summary>
internal sealed record DocPattern(string Glob, string Spec)
{
    public Regex Matcher { get; } = DocGlob.Compile(Glob);
}

/// <summary>
/// One synced repository: its clone, the branch and remote it follows, what it includes and excludes, and its read-only token file.
/// With <see cref="Fetch"/> off the hub never runs <c>git fetch</c> in the clone and reads the local branch head only, which is how the
/// start script follows the user's own checkout without touching its remote-tracking refs.
/// </summary>
internal sealed record DocRepository(string Id, string ClonePath, string Branch, string Remote, IReadOnlyList<DocPattern> Patterns, IReadOnlyList<string> Excludes, string? TokenFile, bool Fetch = true)
{
    private readonly Regex[] excluded = Excludes.Select(DocGlob.Compile).ToArray();

    /// <summary>The spec of an included, supported, not excluded path, or null. The first matching include pattern names the spec.</summary>
    public string? SpecOf(string path)
    {
        if (!DocGlob.SafePath(path) || DocFormats.Of(path) == DocFormat.Unsupported || excluded.Any(rule => rule.IsMatch(path))) return null;
        return Patterns.FirstOrDefault(pattern => pattern.Matcher.IsMatch(path))?.Spec;
    }

    /// <summary>Git pathspecs for the include patterns; excludes and file types are applied after git lists the paths.</summary>
    public IEnumerable<string> Pathspecs => Patterns.Select(pattern => ":(glob)" + pattern.Glob);
}

/// <summary><c>Relay:DocCache:*</c>. On by default; with no repositories configured it does nothing.</summary>
internal sealed record DocCacheSettings(bool Enabled, int SyncMinutes, int IdleMinutes, int MaxDocuments, IReadOnlyList<DocRepository> Repositories, long MaxRepositoryBytes = RelayLimits.MaxRepositoryDocBytes)
{
    /// <summary>Folders whose git repositories the doc cache finds and indexes on its own; also the only places a session's workspace may come from.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];
    /// <summary>How many folder levels below each root are searched for repositories.</summary>
    public int RootDepth { get; init; } = DefaultRootDepth;

    public const int DefaultRootDepth = 2;
    public const int MaxRootDepth = 4;

    /// <summary>
    /// What the repository's documents were indexed under: patterns, excludes, branch and remote, the document and byte limits, and the
    /// splitters' limits. The directory stores it, and a change forces a full resync (and returns to the configured branch).
    /// </summary>
    public string Fingerprint(DocRepository repository) => DocHash.Of(string.Join('\n',
        [DocFormats.SplitterVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), repository.Branch, repository.Remote, repository.Fetch ? "fetch" : "local",
         string.Join('|', repository.Patterns.Select(pattern => pattern.Glob + "=" + pattern.Spec)), string.Join('|', repository.Excludes),
         MaxDocuments.ToString(System.Globalization.CultureInfo.InvariantCulture), MaxRepositoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
         $"{RelayLimits.MaxDocBytes}/{RelayLimits.MaxOpenApiBytes}/{RelayLimits.MaxSectionsPerDoc}/{RelayLimits.DocChunkTokens}/{RelayLimits.DocChunkChars}"]));

    private static readonly Regex Name = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    public DocRepository? Find(string id) => Repositories.FirstOrDefault(repository => repository.Id == id);

    public static DocCacheSettings From(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Relay:DocCache");
        int Minutes(string key, int fallback, int minimum)
        {
            var value = section.GetValue(key, fallback);
            if (value != 0 && (value < minimum || value > RelayLimits.MaxDocCacheMinutes) || value == 0 && minimum > 0)
                throw new InvalidOperationException($"Relay:DocCache:{key} must be {(minimum == 0 ? "0 (off) or " : "")}from {Math.Max(minimum, 1)} to {RelayLimits.MaxDocCacheMinutes} minutes.");
            return value;
        }
        // On by default. Off reads nothing more, so settings for a cache that is off never stop the hub.
        if (!section.GetValue("Enabled", true)) return new(false, RelayLimits.DefaultDocSyncMinutes, RelayLimits.DefaultDocIdleMinutes, RelayLimits.MaxDocsPerRepository, []);
        var maxBytes = section.GetValue("MaxRepositoryBytes", RelayLimits.MaxRepositoryDocBytes);
        if (maxBytes < 1 || maxBytes > RelayLimits.MaxRepositoryDocBytesCeiling)
            throw new InvalidOperationException($"Relay:DocCache:MaxRepositoryBytes must be from 1 to {RelayLimits.MaxRepositoryDocBytesCeiling}.");
        var sync = Minutes("SyncMinutes", RelayLimits.DefaultDocSyncMinutes, 0);
        var idle = Minutes("IdleMinutes", RelayLimits.DefaultDocIdleMinutes, 2);
        var maxDocuments = section.GetValue("MaxDocuments", RelayLimits.MaxDocsPerRepository);
        if (maxDocuments < 1 || maxDocuments > RelayLimits.MaxDocsPerRepositoryCeiling)
            throw new InvalidOperationException($"Relay:DocCache:MaxDocuments must be from 1 to {RelayLimits.MaxDocsPerRepositoryCeiling}.");
        var rootDepth = section.GetValue("RootDepth", DefaultRootDepth);
        if (rootDepth < 1 || rootDepth > MaxRootDepth) throw new InvalidOperationException($"Relay:DocCache:RootDepth must be from 1 to {MaxRootDepth}.");
        string Resolve(string path) => Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
        // A blank entry is skipped, so an override can switch one root off.
        var roots = section.GetSection("Roots").GetChildren().Select(root => root.Value).Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => Resolve(root!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        var repositories = new List<DocRepository>();
        foreach (var entry in section.GetSection("Repositories").GetChildren())
        {
            var where = $"Relay:DocCache:Repositories:{entry.Key}";
            var id = entry["Id"];
            if (string.IsNullOrWhiteSpace(id) || id.Length > RelayLimits.MaxIdentifierLength) throw new InvalidOperationException($"{where}:Id is required.");
            if (repositories.Any(existing => existing.Id == id)) throw new InvalidOperationException($"{where}:Id {id} is listed twice.");
            if (string.IsNullOrWhiteSpace(entry["ClonePath"])) throw new InvalidOperationException($"{where}:ClonePath is required.");
            var branch = entry["Branch"] is { Length: > 0 } configured ? configured : "main";
            if (!DocGlob.SafeRef(branch)) throw new InvalidOperationException($"{where}:Branch is not a valid branch name.");
            var remote = entry["Remote"] is { Length: > 0 } named ? named : "origin";
            if (!DocGlob.SafeRef(remote) || remote.Contains('/')) throw new InvalidOperationException($"{where}:Remote is not a valid remote name.");
            var patterns = entry.GetSection("Patterns").GetChildren().Select(pattern => new DocPattern(pattern["Glob"] ?? "", pattern["Spec"] is { Length: > 0 } spec ? spec : DocFormats.DefaultSpec)).ToList();
            if (patterns.Count == 0) patterns = DocFormats.DefaultPatterns.Select(glob => new DocPattern(glob, DocFormats.DefaultSpec)).ToList();
            if (patterns.Count > RelayLimits.MaxDocPatterns) throw new InvalidOperationException($"{where}:Patterns lists more than {RelayLimits.MaxDocPatterns} patterns.");
            foreach (var pattern in patterns)
            {
                if (!DocGlob.SafeGlob(pattern.Glob)) throw new InvalidOperationException($"{where}:Patterns glob '{pattern.Glob}' must be a relative pattern of letters, digits, . _ - / * and ?.");
                if (!Name.IsMatch(pattern.Spec)) throw new InvalidOperationException($"{where}:Patterns spec '{pattern.Spec}' must be 1 to 64 letters, digits, . _ or -.");
            }
            // Configured excludes are added to the defaults, so build output and dependencies are never indexed.
            var excludes = DocFormats.DefaultExcludes.Concat(entry.GetSection("Exclude").GetChildren().Select(item => item.Value ?? "")).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var exclude in excludes)
                if (!DocGlob.SafeGlob(exclude)) throw new InvalidOperationException($"{where}:Exclude pattern '{exclude}' must be a relative pattern of letters, digits, . _ - / * and ?.");
            repositories.Add(new(id, Resolve(entry["ClonePath"]!), branch, remote, patterns, excludes, entry["TokenFile"] is { Length: > 0 } token ? Resolve(token) : null, entry.GetValue("Fetch", true)));
        }
        return new(true, sync, idle, maxDocuments, repositories, maxBytes) { Roots = roots, RootDepth = rootDepth };
    }
}

/// <summary>Path patterns as git's <c>:(glob)</c> magic reads them: <c>*</c> and <c>?</c> stay within a folder; <c>**</c> spans folders only as a whole segment.</summary>
internal static class DocGlob
{
    private static readonly Regex GlobCharacters = new("^[A-Za-z0-9._/*?-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex RefCharacters = new("^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant);

    public static Regex Compile(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            // As in git, ** spans folders only as a whole segment (**/ at the start or after /, or a trailing /**); elsewhere it is *.
            var segmentStart = i == 0 || glob[i - 1] == '/';
            if (segmentStart && string.CompareOrdinal(glob, i, "**/", 0, 3) == 0) { pattern.Append("(?:.*/)?"); i += 2; }
            else if (segmentStart && i + 2 == glob.Length && string.CompareOrdinal(glob, i, "**", 0, 2) == 0) { pattern.Append(".*"); i += 1; }
            else if (string.CompareOrdinal(glob, i, "**", 0, 2) == 0) { pattern.Append("[^/]*"); i += 1; }
            else if (glob[i] == '*') pattern.Append("[^/]*");
            else if (glob[i] == '?') pattern.Append("[^/]");
            else pattern.Append(Regex.Escape(glob[i].ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    public static bool SafeGlob(string glob) => glob.Length is > 0 and <= RelayLimits.MaxDocPathLength && GlobCharacters.IsMatch(glob) && SafeSegments(glob);

    /// <summary>A relative path git listed: no leading slash, no <c>..</c> or empty segments, within the path length.</summary>
    public static bool SafePath(string path) => path.Length is > 0 and <= RelayLimits.MaxDocPathLength && !path.Contains('\\') && !path.Contains('\0') && SafeSegments(path);

    /// <summary>A branch or remote name safe to put in a ref: never an option, no <c>..</c>, <c>//</c> or <c>@{</c>, no trailing <c>/</c>, <c>.</c> or <c>.lock</c>.</summary>
    public static bool SafeRef(string name) => name.Length is > 0 and <= RelayLimits.MaxDocBranchLength && RefCharacters.IsMatch(name) && name[0] is not ('-' or '.' or '/')
        && !name.Contains("..") && !name.Contains("//") && !name.EndsWith('/') && !name.EndsWith('.') && !name.EndsWith(".lock", StringComparison.Ordinal);

    private static bool SafeSegments(string path) => !path.StartsWith('/') && path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
}

internal sealed record GitResult(int ExitCode, byte[] Output, bool Truncated, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output);
}

/// <summary>
/// Runs git with an argument list, never a shell string, in one clone. Prompts are off, output is capped, and every command stops after
/// <see cref="RelayLimits.DocGitTimeoutSeconds"/>. Tests replace it to watch or slow down commands.
/// </summary>
internal class GitCli
{
    public virtual async Task<GitResult> RunAsync(string directory, IReadOnlyList<string> arguments, int maxOutputBytes, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "--no-pager" }.Concat(arguments)) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        if (!start.Environment.ContainsKey("GIT_SSH_COMMAND")) start.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RelayLimits.DocGitTimeoutSeconds));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        process.StandardInput.Close();
        // stderr is drained to the end (keeping the first 4 KB), so a chatty git never blocks on a full pipe.
        var error = ReadAsync(process.StandardError.BaseStream, 4_096, timeout.Token, drain: true);
        try
        {
            var (output, truncated) = await ReadAsync(process.StandardOutput.BaseStream, maxOutputBytes, timeout.Token);
            if (truncated)
            {
                Kill(process);
                return new(-1, output, true, "");
            }
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, output, false, Encoding.UTF8.GetString((await error).Output).Trim());
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"git {arguments[0]} ran past {RelayLimits.DocGitTimeoutSeconds} seconds.");
        }
    }

    /// <summary>
    /// Keeps at most <paramref name="maximum"/> bytes; one more means the output was cut off. Reading then stops (the caller kills git),
    /// unless <paramref name="drain"/> asks to read on to the end and discard the rest.
    /// </summary>
    private static async Task<(byte[] Output, bool Truncated)> ReadAsync(Stream stream, int maximum, CancellationToken cancellationToken, bool drain = false)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (truncated) continue;
            var keep = (int)Math.Min(read, maximum + 1L - buffer.Length);
            buffer.Write(chunk, 0, keep);
            if (buffer.Length > maximum) { truncated = true; buffer.SetLength(maximum); if (!drain) break; }
        }
        return (buffer.ToArray(), truncated);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>The git commands the doc cache uses, for one configured repository.</summary>
internal sealed class DocGit(GitCli git, DocRepository repository, ILogger? logger = null)
{
    /// <summary>Regular files only: a symlink (120000) or a submodule (160000) is never indexed.</summary>
    private static bool Regular(string mode) => mode is "100644" or "100755";

    private static readonly Regex Sha = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant);

    public DocRepository Repository => repository;

    public static bool IsCommit(string value) => Sha.IsMatch(value);

    /// <summary>
    /// Fetches the branch into its remote-tracking ref, never into submodules. The read-only token (if any) goes in git's environment,
    /// never on the command line, as an HTTP header scoped to the remote's own HTTPS URL, so no other host ever receives it.
    /// </summary>
    public async Task<bool> FetchAsync(string branch, CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>();
        if (await TokenAsync(cancellationToken) is { } token && await RemoteUrlAsync(cancellationToken) is { } url)
        {
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = $"http.{url}.extraHeader";
            environment["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        }
        var result = await git.RunAsync(repository.ClonePath, ["fetch", "--quiet", "--no-tags", "--no-recurse-submodules", repository.Remote, $"+refs/heads/{branch}:refs/remotes/{repository.Remote}/{branch}"], 65_536, environment, cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>The token from the configured file, read up to <see cref="RelayLimits.MaxDocTokenFileBytes"/>; a missing, empty or oversized file is logged.</summary>
    private async Task<string?> TokenAsync(CancellationToken cancellationToken)
    {
        if (repository.TokenFile is not { } file) return null;
        if (!File.Exists(file)) { logger?.LogWarning("Doc sync for {Repository}: token file {File} is missing; fetching without a token.", repository.Id, file); return null; }
        await using var stream = File.OpenRead(file);
        var buffer = new byte[RelayLimits.MaxDocTokenFileBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0) length += read;
        if (length > RelayLimits.MaxDocTokenFileBytes) { logger?.LogWarning("Doc sync for {Repository}: token file {File} is over {Limit} bytes; fetching without a token.", repository.Id, file, RelayLimits.MaxDocTokenFileBytes); return null; }
        var token = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        if (token.Length == 0 || token.Any(char.IsControl)) { logger?.LogWarning("Doc sync for {Repository}: token file {File} is empty or not one line; fetching without a token.", repository.Id, file); return null; }
        return token;
    }

    /// <summary>The remote's URL when it is plain HTTPS (no credentials, query or spaces), the only kind a token header is sent to.</summary>
    private async Task<string?> RemoteUrlAsync(CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(repository.ClonePath, ["remote", "get-url", repository.Remote], 4_096, null, cancellationToken);
        var url = result.Text.Trim();
        if (result.ExitCode == 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            && uri.Query.Length == 0 && uri.Fragment.Length == 0 && !url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return url;
        logger?.LogWarning("Doc sync for {Repository}: the remote {Remote} is not a plain HTTPS URL, so its token is not sent.", repository.Id, repository.Remote);
        return null;
    }

    private bool? hasRemote;

    /// <summary>Whether the last <see cref="HeadAsync"/> read the local branch because the remote has no such branch (yet).</summary>
    public bool ReadLocalBranch { get; private set; }

    /// <summary>
    /// Whether the hub fetches from the configured remote: <c>Fetch</c> is on and the clone defines that remote. Otherwise (a local-only
    /// repository, or the user's own checkout) it never fetches, and branches are read from <c>refs/heads</c>: still committed content
    /// only, never the working tree.
    /// </summary>
    public async Task<bool> HasRemoteAsync(CancellationToken cancellationToken)
    {
        if (!repository.Fetch) return false;
        if (hasRemote is { } known) return known;
        var result = await git.RunAsync(repository.ClonePath, ["remote"], 65_536, null, cancellationToken);
        if (result.ExitCode != 0) return false;
        hasRemote = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(repository.Remote, StringComparer.Ordinal);
        return hasRemote.Value;
    }

    /// <summary>
    /// The commit at the head of the branch's remote-tracking ref, or null when there is none. Without fetching (see
    /// <see cref="HasRemoteAsync"/>) it is the local branch. A branch the remote does not have (an unpushed one) is read from the local
    /// branch too, and <see cref="ReadLocalBranch"/> says so.
    /// </summary>
    public async Task<string?> HeadAsync(string branch, CancellationToken cancellationToken)
    {
        ReadLocalBranch = false;
        if (!await HasRemoteAsync(cancellationToken)) return await CommitAtAsync($"refs/heads/{branch}", cancellationToken);
        if (await CommitAtAsync($"refs/remotes/{repository.Remote}/{branch}", cancellationToken) is { } tracked) return tracked;
        var local = await CommitAtAsync($"refs/heads/{branch}", cancellationToken);
        ReadLocalBranch = local is not null;
        return local;
    }

    private async Task<string?> CommitAtAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(repository.ClonePath, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], 1_024, null, cancellationToken);
        var commit = result.Text.Trim();
        return result.ExitCode == 0 && IsCommit(commit) ? commit : null;
    }

    public async Task<bool> CommitExistsAsync(string commit, CancellationToken cancellationToken) =>
        IsCommit(commit) && (await git.RunAsync(repository.ClonePath, ["cat-file", "-e", commit + "^{commit}"], 1_024, null, cancellationToken)).ExitCode == 0;

    /// <summary>
    /// Every path at <paramref name="commit"/>, and whether it is a regular file (not a symlink or submodule). ls-tree has no glob pathspecs,
    /// so the caller applies the patterns.
    /// </summary>
    public async Task<IReadOnlyList<(string Path, bool Regular)>> ListAsync(string commit, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(repository.ClonePath, ["ls-tree", "-r", "-z", "--full-tree", commit], RelayLimits.MaxDocGitListingBytes, null, cancellationToken);
        if (result.ExitCode != 0) throw new DocSyncException(DocSyncError.Failed, "git ls-tree failed: " + (result.Truncated ? "the listing is too long" : result.Error));
        // "<mode> <type> <object>\t<path>"
        return result.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2)
            .Select(parts => (parts[1], Regular(parts[0].Split(' ')[0]))).ToArray();
    }

    /// <summary>
    /// Changed paths between two commits that the include patterns match. With <c>--no-renames</c> a rename is a delete plus an add, so an
    /// old path never lingers. Present is false for a deleted path; Regular is false when the new version is a symlink or submodule.
    /// </summary>
    public async Task<IReadOnlyList<(string Path, bool Present, bool Regular)>> DiffAsync(string from, string to, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(repository.ClonePath, ["diff", "--raw", "-z", "--no-abbrev", "--no-renames", "--no-ext-diff", "--no-textconv", from, to, "--", .. repository.Pathspecs], RelayLimits.MaxDocGitListingBytes, null, cancellationToken);
        if (result.ExitCode != 0) throw new DocSyncException(DocSyncError.Failed, "git diff failed: " + (result.Truncated ? "the listing is too long" : result.Error));
        // ":<old mode> <new mode> <old object> <new object> <status>\0<path>\0"
        var fields = result.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<(string, bool, bool)>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var header = fields[i].TrimStart(':').Split(' ');
            if (header.Length < 5) continue;
            var present = header[4] != "D";
            changes.Add((fields[i + 1], present, present && Regular(header[1])));
        }
        return changes;
    }

    /// <summary><c>git show &lt;commit&gt;:&lt;path&gt;</c>: the committed file, never the working tree. Null when missing or over <paramref name="maxBytes"/>; <paramref name="tooLarge"/> says which.</summary>
    public async Task<(string? Text, bool TooLarge)> ShowAsync(string commit, string path, int maxBytes, CancellationToken cancellationToken)
    {
        if (!IsCommit(commit) || !DocGlob.SafePath(path)) return (null, false);
        var result = await git.RunAsync(repository.ClonePath, ["show", "--no-textconv", commit + ":" + path], maxBytes, null, cancellationToken);
        if (result.Truncated) return (null, true);
        return result.ExitCode == 0 ? (new UTF8Encoding(false, false).GetString(result.Output), false) : (null, false);
    }
}

/// <summary>Reads documents for the document grains, from configured or discovered repositories and included paths only.</summary>
internal sealed class GitDocSource(DocRepositoryRegistry registry, GitCli git) : IDocSource
{
    public async Task<DocContent?> ReadAsync(string repositoryId, string path, string commit, CancellationToken cancellationToken = default)
    {
        if (registry.Find(repositoryId) is not { } repository || repository.SpecOf(path) is null) return null;
        var (text, _) = await new DocGit(git, repository).ShowAsync(commit, path, DocFormats.MaxBytes(DocFormats.Of(path)), cancellationToken);
        return text is null ? null : new DocContent(text, commit, DocHash.Of(text));
    }

    public async Task<bool> CommitExistsAsync(string repositoryId, string commit, CancellationToken cancellationToken = default) =>
        registry.Find(repositoryId) is { } repository && await new DocGit(git, repository).CommitExistsAsync(commit, cancellationToken);

    public async Task<DocContent?> ReadHeadAsync(string repositoryId, string path, string branch, CancellationToken cancellationToken = default)
    {
        if (registry.Find(repositoryId) is not { } repository || !DocGlob.SafeRef(branch)) return null;
        return await new DocGit(git, repository).HeadAsync(branch, cancellationToken) is { } head ? await ReadAsync(repositoryId, path, head, cancellationToken) : null;
    }
}

/// <summary>
/// The doc sync, shared by the timer and <c>doc_sync</c>: one run at a time per repository (the other waits). It fetches, compares the
/// branch's commit with the directory's and stops when they match; otherwise it diffs (or lists, for a first or full resync), reads each
/// changed file with <c>git show</c>, updates the directory, and then pushes the new content to document grains that are active.
/// Files that are too large, have too many sections, are not OpenAPI, are malformed, are symlinks, or are past the document or byte limit
/// are skipped and counted. A change of the sync settings (its fingerprint) forces a full resync.
/// </summary>
internal sealed class DocSyncRunner(DocCacheSettings settings, DocRepositoryRegistry registry, GitCli git, IGrainFactory grains, ITokenCounter tokens, ILogger<DocSyncRunner> logger) : IDocSync
{
    /// <summary>A runner over the configured repositories only (and those it later discovers itself), for callers without the hub's registry.</summary>
    public DocSyncRunner(DocCacheSettings settings, GitCli git, IGrainFactory grains, ITokenCounter tokens, ILogger<DocSyncRunner> logger)
        : this(settings, new DocRepositoryRegistry(settings, git, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocRepositoryRegistry>.Instance), git, grains, tokens, logger) { }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> lastManual = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string? Branch, Lazy<Task<DocSyncOutcome>> Run)> requests = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Key, Lazy<Task<DocSyncOutcome>> Run)> requestOrder = new();
    private readonly ConcurrentDictionary<string, byte> localNoted = new(StringComparer.Ordinal);
    private const int RememberedRequests = 64;

    /// <summary>
    /// <c>doc_sync</c>: a retry with the same requestId (per repository) gets the first run's outcome (even while it runs), never a second
    /// run or the rate limit; the same requestId with another branch is a conflict. The shared run ignores each caller's cancellation, so
    /// one caller giving up never fails the retries. A failed run is forgotten, so its retry runs again.
    /// </summary>
    public async Task<DocSyncOutcome> SyncNowAsync(string repositoryId, string? branch, string requestId, CancellationToken cancellationToken = default)
    {
        if (registry.Find(repositoryId) is null) throw new DocSyncException(DocSyncError.NotConfigured, "No docs are configured for this repository.");
        if (branch is not null && !DocGlob.SafeRef(branch)) throw new DocSyncException(DocSyncError.UnknownBranch, "The branch name is not valid.");
        var key = repositoryId + "\n" + requestId;
        var run = new Lazy<Task<DocSyncOutcome>>(() => RateLimitedAsync(repositoryId, branch, CancellationToken.None));
        // Only the stored entry is ever started, so two calls with one requestId run once.
        var stored = requests.GetOrAdd(key, (branch, run));
        if (ReferenceEquals(stored.Run, run))
        {
            requestOrder.Enqueue((key, run));
            // Evict only the entry that was queued: a failed run's retry stores a new one under the same key. A run still going stays, so its
            // retry joins it instead of starting a second sync; the queue shrinks again once it ends.
            while (requestOrder.Count > RememberedRequests && requestOrder.TryDequeue(out var old))
            {
                if (old.Run.IsValueCreated && !old.Run.Value.IsCompleted) { requestOrder.Enqueue(old); break; }
                if (requests.TryGetValue(old.Key, out var current) && ReferenceEquals(current.Run, old.Run)) requests.TryRemove(new KeyValuePair<string, (string?, Lazy<Task<DocSyncOutcome>>)>(old.Key, current));
            }
        }
        else if (stored.Branch != branch) throw new DocSyncException(DocSyncError.RequestConflict, "This requestId was already used for a doc_sync with another branch.");
        try { return await stored.Run.Value.WaitAsync(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { requests.TryRemove(new KeyValuePair<string, (string?, Lazy<Task<DocSyncOutcome>>)>(key, stored)); throw; }
    }

    /// <summary>
    /// Registers a session's workspace (see <see cref="DocRepositoryRegistry.LearnAsync"/>) and syncs a newly added one in the background,
    /// so the session open that reported it never waits for git.
    /// </summary>
    public async Task<string?> LearnAsync(string repositoryId, string workspace, CancellationToken cancellationToken = default)
    {
        var (id, added) = await registry.LearnAsync(repositoryId, workspace, cancellationToken);
        if (id is not null && added)
            _ = Task.Run(async () =>
            {
                try { await SyncAsync(id, null, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning("Doc sync for {Repository} failed; the next sync run retries it. {Reason}", id, exception.Message); }
            }, CancellationToken.None);
        return id;
    }

    private async Task<DocSyncOutcome> RateLimitedAsync(string repositoryId, string? branch, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var now = Environment.TickCount64;
        var interval = RelayLimits.DocSyncMinIntervalSeconds * 1000L;
        var last = lastManual.GetOrAdd(repositoryId, long.MinValue);
        if (last != long.MinValue && now - last < interval)
            throw new DocSyncException(DocSyncError.RateLimited, "A doc sync ran for this repository moments ago.", (int)Math.Ceiling((interval - (now - last)) / 1000.0));
        if (!lastManual.TryUpdate(repositoryId, now, last)) throw new DocSyncException(DocSyncError.RateLimited, "A doc sync for this repository has just started.", RelayLimits.DocSyncMinIntervalSeconds);
        return await SyncAsync(repositoryId, branch, cancellationToken);
    }

    public async Task<DocSyncOutcome> SyncAsync(string repositoryId, string? branchOverride, CancellationToken cancellationToken)
    {
        var repository = registry.Find(repositoryId) ?? throw new DocSyncException(DocSyncError.NotConfigured, "No docs are configured for this repository.");
        if (branchOverride is not null && !DocGlob.SafeRef(branchOverride)) throw new DocSyncException(DocSyncError.UnknownBranch, "The branch name is not valid.");
        var gate = locks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            try { return await RunAsync(new DocGit(git, repository, logger), branchOverride, cancellationToken); }
            catch (TimeoutException exception) { throw new DocSyncException(DocSyncError.Failed, exception.Message); }
            catch (System.ComponentModel.Win32Exception exception) { throw new DocSyncException(DocSyncError.Failed, "git could not run: " + exception.Message); }
        }
        finally { gate.Release(); }
    }

    private async Task<DocSyncOutcome> RunAsync(DocGit git, string? branchOverride, CancellationToken cancellationToken)
    {
        var repository = git.Repository;
        var directory = grains.GetGrain<IDocDirectoryGrain>(repository.Id);
        var view = await directory.ReadAsync();
        // A branch given to doc_sync is followed from then on; the configured branch is the default, and a settings change returns to it.
        var fingerprint = settings.Fingerprint(repository);
        var settingsChanged = view.Fingerprint != fingerprint;
        var branch = branchOverride ?? (view.Branch.Length > 0 && !settingsChanged ? view.Branch : repository.Branch);
        // With Fetch off, or in a clone without the remote, nothing is fetched: the local branch head is compared instead.
        var remote = await git.HasRemoteAsync(cancellationToken);
        var fetched = remote && await git.FetchAsync(branch, cancellationToken);
        var head = await git.HeadAsync(branch, cancellationToken)
            ?? throw new DocSyncException(DocSyncError.UnknownBranch, remote ? $"The clone has no branch {branch} from {repository.Remote}." : $"The clone has no branch {branch}.");
        // A branch the remote does not have yet is read locally until it is pushed: said once, not warned on every sync.
        if (git.ReadLocalBranch)
        {
            if (localNoted.TryAdd(repository.Id + "\n" + branch, 0))
                logger.LogInformation("Doc sync for {Repository}: {Remote} has no branch {Branch}, so the local branch is read until it is pushed.", repository.Id, repository.Remote, branch);
        }
        else
        {
            if (remote && !fetched) logger.LogWarning("Doc sync for {Repository}: git fetch of {Branch} failed; using the clone's last fetched commit.", repository.Id, branch);
            localNoted.TryRemove(repository.Id + "\n" + branch, out _);
        }
        var full = view.Commit.Length == 0 || view.NeedsResync || settingsChanged || branch != view.Branch || !await git.CommitExistsAsync(view.Commit, cancellationToken);
        var entries = view.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        // Files left out only for room come back once the smallest of them fits (one more document, and its bytes): on any sync, doc_sync
        // on an unchanged commit included. A repository just under its limits rereads nothing.
        var waiting = (view.CapacitySkipped ?? []).ToDictionary(item => item.Path, item => item.Bytes, StringComparer.Ordinal);
        bool Fits(long documents, long used) => documents < settings.MaxDocuments && waiting.Count > 0 && used + Math.Max(1, waiting.Values.Min()) <= settings.MaxRepositoryBytes;
        if (!full && head == view.Commit && !Fits(entries.Count, entries.Values.Sum(entry => (long)entry.Bytes))) return new(head, 0, 0, fetched);

        var skippedBefore = view.Skipped.ToHashSet(StringComparer.Ordinal);
        List<string> present;
        var removed = new List<string>();
        var unskippedGone = new List<string>();
        var skipped = new List<string>();
        if (full)
        {
            var listing = (await git.ListAsync(head, cancellationToken)).Where(item => repository.SpecOf(item.Path) is not null).ToArray();
            present = listing.Where(item => item.Regular).Select(item => item.Path).ToList();
            // A symlink or submodule that a pattern matches is skipped and counted; git show would give its target path, not a document.
            skipped.AddRange(listing.Where(item => !item.Regular).Select(item => item.Path));
            var listed = present.ToHashSet(StringComparer.Ordinal);
            removed.AddRange(entries.Keys.Where(path => !listed.Contains(path) || repository.SpecOf(path) is null));
        }
        else
        {
            present = [];
            var gone = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, isPresent, regular) in head == view.Commit ? Array.Empty<(string, bool, bool)>() : await git.DiffAsync(view.Commit, head, cancellationToken))
            {
                if (isPresent && regular) { present.Add(path); continue; }
                if (entries.ContainsKey(path)) removed.Add(path);
                if (isPresent && repository.SpecOf(path) is not null) skipped.Add(path);
                // A skipped file that was deleted is no longer skipped.
                if (!isPresent && skippedBefore.Contains(path)) unskippedGone.Add(path);
                // Deleted, or no longer a regular file: either way it no longer waits for room.
                gone.Add(path);
            }
            // The unchanged files still waiting for room are read again from this commit when, after this commit's removals, the smallest fits.
            foreach (var path in gone) waiting.Remove(path);
            var left = entries.Values.Where(entry => !removed.Contains(entry.Path)).ToArray();
            var used = left.Sum(entry => (long)entry.Bytes);
            // Only those whose last known size fits the room left: one that cannot fit would be read just to be skipped again.
            if (Fits(left.Length, used)) present.AddRange(waiting.Where(item => used + Math.Max(1, item.Value) <= settings.MaxRepositoryBytes).Select(item => item.Key));
        }
        var active = await ActiveAsync(repository.Id);
        var upserts = new List<DocEntry>();
        var pushes = new List<(DocEntry Entry, string Text)>();
        var unskipped = new List<string>(unskippedGone);
        var forRoom = new List<DocWaiting>();
        var count = entries.Count - removed.Count;
        var bytes = entries.Values.Where(entry => !removed.Contains(entry.Path)).Sum(entry => (long)entry.Bytes);
        foreach (var path in present.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (repository.SpecOf(path) is not { } spec) continue;
            var known = entries.TryGetValue(path, out var existing) && !removed.Contains(path);
            // Its size is not known yet (it is not read); the last size seen, if it was waiting before, is kept.
            if (!known && count >= settings.MaxDocuments) { skipped.Add(path); forRoom.Add(new(path, waiting.GetValueOrDefault(path))); continue; }
            var (text, tooLarge) = await git.ShowAsync(head, path, DocFormats.MaxBytes(DocFormats.Of(path)), cancellationToken);
            var reason = DocSkipReason.None;
            var sections = text is null ? null : DocFormats.Build(path, text, tokens, out reason);
            if (text is null || sections is null)
            {
                logger.LogInformation("Doc sync for {Repository}: skipped {Path} ({Reason}).", repository.Id, path, text is null ? tooLarge ? "too large" : "unreadable" : reason);
                skipped.Add(path);
                // A file that can no longer be indexed leaves the index too.
                if (known) { removed.Add(path); count--; bytes -= existing!.Bytes; }
                continue;
            }
            var size = Encoding.UTF8.GetByteCount(text);
            var after = bytes - (known ? existing!.Bytes : 0) + size;
            if (after > settings.MaxRepositoryBytes)
            {
                logger.LogInformation("Doc sync for {Repository}: skipped {Path} (the repository's {Limit} bytes are used).", repository.Id, path, settings.MaxRepositoryBytes);
                skipped.Add(path); forRoom.Add(new(path, size));
                if (known) { removed.Add(path); count--; bytes -= existing!.Bytes; }
                continue;
            }
            if (skippedBefore.Contains(path)) unskipped.Add(path);
            var hash = DocHash.Of(text);
            // An unchanged file keeps its entry and is not re-indexed.
            if (known && existing!.Hash == hash && existing.Spec == spec && existing.Bytes == size) continue;
            var entry = new DocEntry(path, spec, head, hash, sections.Count, sections.Sum(section => section.Tokens), size);
            upserts.Add(entry);
            bytes = after;
            if (!known) count++;
            if (active.Contains(DocKeys.Document(repository.Id, path))) pushes.Add((entry, text));
        }
        unskipped.AddRange(removed.Where(skippedBefore.Contains));
        await directory.ApplyAsync(new DocDirectoryUpdate(branch, head, full, [.. upserts], [.. removed.Distinct(StringComparer.Ordinal)], [.. skipped], [.. unskipped], fingerprint, [.. forRoom]));
        foreach (var (entry, text) in pushes)
        {
            try { await grains.GetGrain<IDocumentGrain>(DocKeys.Document(repository.Id, entry.Path)).ReplaceAsync(new DocRef(repository.Id, entry.Path, entry.Spec, entry.Commit, entry.Hash, branch), text); }
            catch (Exception exception) { logger.LogWarning("Doc sync for {Repository}: could not push {Path}; it reloads on its next use. {Reason}", repository.Id, entry.Path, exception.Message); }
        }
        foreach (var path in removed.Where(path => active.Contains(DocKeys.Document(repository.Id, path))))
        {
            try { await grains.GetGrain<IDocumentGrain>(DocKeys.Document(repository.Id, path)).DropAsync(); }
            catch (Exception) { /* It is gone from the directory, so it is never asked again. */ }
        }
        // After the directory has the update, never from inside it: a first search's build reads the directory, which would wait on the
        // update, which would wait on the patch. A search index not loaded is built from the directory when next used.
        if ((upserts.Count > 0 || removed.Count > 0) && await SearchActiveAsync(repository.Id))
        {
            try { await grains.GetGrain<IDocSearchGrain>(repository.Id).PatchAsync([.. upserts.Select(entry => new DocRef(repository.Id, entry.Path, entry.Spec, entry.Commit, entry.Hash, branch))], [.. removed.Distinct(StringComparer.Ordinal)]); }
            catch (Exception exception) { logger.LogWarning("Doc sync for {Repository}: could not patch the search index; its next search reads the directory again. {Reason}", repository.Id, exception.Message); }
        }
        var changed = upserts.Count + removed.Distinct(StringComparer.Ordinal).Count();
        logger.LogInformation("Doc sync for {Repository}: {Commit} on {Branch}, {Changed} changed, {Skipped} skipped{Full}.", repository.Id, head, branch, changed, skipped.Count, full ? " (full)" : "");
        return new(head, changed, skipped.Count, fetched);
    }

    /// <summary>Whether this repository's search index is active now; only then is it patched.</summary>
    private async Task<bool> SearchActiveAsync(string repositoryId) =>
        (await grains.GetGrain<IManagementGrain>(0).GetActiveGrains(DocKeys.SearchType)).Any(id => id.Key.ToString() == repositoryId);

    /// <summary>The keys of this repository's document grains that are active now; only those get pushed content.</summary>
    private async Task<HashSet<string>> ActiveAsync(string repositoryId)
    {
        var prefix = Uri.EscapeDataString(repositoryId) + "/";
        var active = await grains.GetGrain<IManagementGrain>(0).GetActiveGrains(DocKeys.DocumentType);
        return active.Select(id => id.Key.ToString()!).Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// The doc cache's own timer, outside the due-work pump, so a slow <c>git fetch</c> never holds up message delivery. At startup and then
/// every <c>Relay:DocCache:SyncMinutes</c> (0 turns the timer off; <c>doc_sync</c> still works) it scans the roots for new repositories and
/// syncs every configured and discovered one. With the doc cache off it returns at once and never builds the sync runner.
/// </summary>
internal sealed class SpecSyncService(DocCacheSettings settings, IServiceProvider services, ILogger<SpecSyncService> logger) : BackgroundService
{
    private DocSyncRunner Runner => services.GetRequiredService<DocSyncRunner>();
    private DocRepositoryRegistry Registry => services.GetRequiredService<DocRepositoryRegistry>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // With no repositories configured and no roots to search there is nothing to sync: no git runs at all.
        if (!settings.Enabled || settings.SyncMinutes == 0 || settings.Repositories.Count == 0 && settings.Roots.Count == 0) return;
        await Task.Yield();
        try
        {
            await RunOnceAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.SyncMinutes));
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One timer run: a root scan, then every configured and discovered repository; a failing one is logged and the others still sync.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try { await Registry.ScanAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning("Doc cache root scan failed; the repositories already known still sync. {Reason}", exception.Message); }
        foreach (var repository in Registry.Repositories)
        {
            try { await Runner.SyncAsync(repository.Id, null, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning("Doc sync for {Repository} failed; the cache keeps its last commit. {Reason}", repository.Id, exception.Message); }
        }
    }
}

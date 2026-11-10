using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Authentication;

namespace CoCern.Host;

/// <summary>
/// <c>GET /join/adapter.zip</c>: the published adapter folder as a zip, so another PC's one pasted setup command can download CoCern from this hub
/// instead of someone copying the folder across first. It carries no secret (the join code travels in the pasted command, never here), so it needs no
/// sign-in; like enrollment it does not exist while discovery is off, so a loopback-only hub exposes nothing, and it refuses plain HTTP from another PC
/// because the download is code the other PC will run. The folder is <see cref="DashboardConnect.ServedAdapter()"/>'s, a self-contained build when there is one.
/// </summary>
internal static class JoinEndpoints
{
    public const string AdapterZipPath = "/join/adapter.zip";
    public const string AdapterZipName = "CoCern.Adapter.zip";

    public static void MapJoinEndpoints(this WebApplication app)
    {
        if (!DiscoverySettings.Enabled(app.Configuration)) return;
        app.MapGet(AdapterZipPath, AdapterZipAsync).AllowAnonymous();
    }

    private static async Task<IResult> AdapterZipAsync(HttpContext context, DashboardConnect connect, AdapterZipCache cache)
    {
        if (!context.Request.IsHttps && !(context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)))
            return Results.Json(ToolResults.Failure(RelayErrorCodes.HttpsRequired), statusCode: StatusCodes.Status403Forbidden);
        if (context.Request.Query.ContainsKey("release"))
        {
            // The endpoint stays anonymous for initial setup; every release query, even an empty or malformed one, takes the machine credential path.
            var authentication = await context.AuthenticateAsync();
            if (!authentication.Succeeded) return Results.Challenge();
            if (RelayAuthentication.Machine(authentication.Principal!) is null)
                return Results.Json(ToolResults.Failure(RelayErrorCodes.MachineRequired), statusCode: StatusCodes.Status403Forbidden);
            var values = context.Request.Query["release"];
            if (values.Count != 1 || !AdapterZipCache.ValidReleaseId(values[0])) return Results.NotFound();
            var lease = await cache.OpenReleaseAsync(values[0]!, context.RequestAborted);
            return lease is null ? Results.NotFound() : new ReleaseResult(lease);
        }
        if (connect.ServedAdapter() is not { } adapter || !Directory.Exists(adapter.Folder)) return Results.NotFound();
        var zip = await cache.OpenAsync(adapter.Folder, context.RequestAborted);
        return Results.File(zip, "application/zip", AdapterZipName, enableRangeProcessing: true);
    }

    private sealed class ReleaseResult(AdapterReleaseLease lease) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            await using (lease)
                await Results.File(lease.Content, "application/zip", AdapterZipName, enableRangeProcessing: true).ExecuteAsync(context);
        }
    }
}

/// <summary>
/// One host-owned cache serves setup and enrolled-node releases, which may come from different adapter folders (the join package and the newest
/// self-contained node build). Archive names identify exact SHA-256 bytes, and only archives built by this process are trusted. Metadata comes from the
/// completed ZIP; source-content hashes, remembered per source folder, avoid needless rebuilds, so serving two folders in turn rebuilds neither. Between
/// full hashes, at most <see cref="FullCheckAfterMilliseconds"/> apart, file metadata stands in for content (see <see cref="GetAsync"/>). Each remembered folder's current and previous archives, and leased archives, survive pruning. Every returned stream owns its pruning
/// reference until disposal, including aborted HTTP responses.
/// </summary>
internal sealed class AdapterZipCache(string folder, Action? archiveBuilt = null)
{
    private const UnixFileMode OwnerFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly SemaphoreSlim building = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> built = new(StringComparer.Ordinal);
    /// <summary>Per source folder (full path): its memoized content stamp and its current and previous archives. Guarded by <c>sync</c>.</summary>
    private readonly Dictionary<string, Source> sources = new(StringComparer.OrdinalIgnoreCase);
    private long uses;
    /// <summary>Folders remembered at once; the least recently served is forgotten first, and its archives become prunable.</summary>
    private const int RememberedSources = 4;

    /// <summary>
    /// How long a cheap check (a folder listing, or an archive's length and write time) may stand in for a full SHA-256 before the content is hashed again.
    /// Nodes announce every 30 seconds and every announce asks for the release; hashing the whole build and archive each time read about 220 MB per announce.
    /// </summary>
    private const long FullCheckAfterMilliseconds = 5 * 60 * 1000;

    private sealed class Source
    {
        public string? Stamp { get; set; }
        /// <summary>The metadata listing seen when <see cref="Stamp"/> was last confirmed by a full content hash, at <see cref="ContentCheckedAt"/>.</summary>
        public string? Listing { get; set; }
        public long ContentCheckedAt { get; set; }
        public string? Current { get; set; }
        public string? Previous { get; set; }
        public long Used { get; set; }
    }

    private sealed class Entry(string id, long bytes, NodeRelease? release)
    {
        public string Id { get; } = id;
        public long Bytes { get; } = bytes;
        public NodeRelease? Release { get; } = release;
        public int References { get; set; }
        /// <summary>The archive's last write time when its bytes last hashed to <see cref="Id"/>, at <see cref="VerifiedAt"/>. Guarded by <c>sync</c>.</summary>
        public DateTime VerifiedWrite { get; set; }
        public long VerifiedAt { get; set; }
    }

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary><c>adapter-zip</c> in the folder that holds the hub's database, join code and <c>local-hub.json</c>.</summary>
    public static string DefaultFolder(IConfiguration configuration, IHostEnvironment environment) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RelayHostBuilderExtensions.DatabasePath(configuration, environment)))!, "adapter-zip");

    /// <summary>
    /// The zip of <paramref name="adapterFolder"/>, opened for reading after its whole content hashed to its name; the caller disposes it. This is the
    /// anonymous setup download, which no client checks against a hash, and it is rare, so it never takes the length-and-write-time shortcut.
    /// </summary>
    public async Task<Stream> OpenAsync(string adapterFolder, CancellationToken cancellationToken)
    {
        await building.WaitAsync(cancellationToken);
        try { return (await GetAsync(adapterFolder, cancellationToken, fullHash: true)).Content; }
        finally { building.Release(); }
    }

    public async Task<AdapterReleaseLease?> ReleaseAsync(string adapterFolder, CancellationToken ct)
    {
        await building.WaitAsync(ct);
        try
        {
            // The stream GetAsync verified is the one leased: no second verification of the same archive.
            var (entry, content) = await GetAsync(adapterFolder, ct);
            if (entry.Release is null) { await content.DisposeAsync(); return null; }
            return new AdapterReleaseLease(entry.Release, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
        finally { building.Release(); }
    }

    public async Task<AdapterReleaseLease?> OpenReleaseAsync(string releaseId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidReleaseId(releaseId)) return null;
        Entry? entry;
        lock (sync) built.TryGetValue(releaseId, out entry);
        if (entry?.Release is null) return null;
        try
        {
            var content = await OpenVerifiedAsync(entry, ct);
            return content is null ? null : new AdapterReleaseLease(entry.Release, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool ValidReleaseId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private string ZipPath(string id) => Path.Combine(Folder, $"{id}.zip");

    /// <summary>
    /// The archive for <paramref name="adapterFolder"/>, opened and verified. Only the serial builder accesses the source index. A metadata listing (every
    /// entry's relative path, length, write and creation time, with the link checks) decides whether the folder changed; when it is unchanged and the
    /// content was hashed within <see cref="FullCheckAfterMilliseconds"/>, the remembered archive is served without rehashing the build. A changed listing,
    /// or that time running out, hashes the full content, which still catches a same-length, same-time dependency change within that window.
    /// </summary>
    /// <param name="fullHash">Hash the archive in full before serving it (<see cref="OpenVerifiedAsync"/>).</param>
    private async Task<(Entry Entry, Stream Content)> GetAsync(string adapterFolder, CancellationToken ct, bool fullHash = false)
    {
        var folder = Path.GetFullPath(adapterFolder);
        var listing = await Task.Run(() => Listing(folder, ct), ct);
        Entry? existing = null;
        lock (sync)
        {
            if (sources.TryGetValue(folder, out var source) && source.Stamp is not null && source.Current is { } id && source.Listing == listing &&
                Environment.TickCount64 - source.ContentCheckedAt < FullCheckAfterMilliseconds)
            {
                source.Used = ++uses;
                built.TryGetValue(id, out existing);
            }
        }
        string? stamp = null;
        if (existing is null)
        {
            stamp = await Task.Run(() => Stamp(folder, ct), ct);
            lock (sync)
            {
                if (sources.TryGetValue(folder, out var source) && source.Stamp == stamp && source.Current is { } id)
                {
                    source.Used = ++uses;
                    // The listing was taken before the content hash, so a change after it shows as a different listing next time.
                    (source.Listing, source.ContentCheckedAt) = (listing, Environment.TickCount64);
                    built.TryGetValue(id, out existing);
                }
            }
        }
        if (existing is not null && await OpenVerifiedAsync(existing, ct, fullHash) is { } content) return (existing, content);
        stamp ??= await Task.Run(() => Stamp(folder, ct), ct);
        var entry = await BuildAsync(folder, listing, stamp, ct);
        return (entry, await OpenVerifiedAsync(entry, ct, fullHash) ?? throw new IOException("The adapter zip changed right after it was built."));
    }

    /// <summary>The remembered state of <paramref name="folder"/>, created when new; forgets the least recently served folder beyond the limit. Call under <c>sync</c>.</summary>
    private Source Track(string folder)
    {
        if (!sources.TryGetValue(folder, out var source)) sources[folder] = source = new Source();
        source.Used = ++uses;
        while (sources.Count > RememberedSources) sources.Remove(sources.Where(pair => pair.Value != source).MinBy(pair => pair.Value.Used).Key);
        return source;
    }

    /// <summary>
    /// The cached zip when this process built it and its bytes still hash the same, else null. The open handle that is then served shares no writes, so
    /// what was checked is what goes out. Its length and write time, read from that handle, stand in for the SHA-256 while they match the last full hash
    /// and that hash is under <see cref="FullCheckAfterMilliseconds"/> old; anything else hashes the archive again. Nodes verify length, SHA-256 and the
    /// adapter DLL hash of every download themselves, so only their release path may take that shortcut.
    /// </summary>
    /// <param name="fullHash">Always hash the archive (the setup download, which nothing checks after it leaves).</param>
    private async Task<Stream?> OpenVerifiedAsync(Entry entry, CancellationToken cancellationToken, bool fullHash = false)
    {
        FileStream stream;
        lock (sync)
        {
            if (!built.TryGetValue(entry.Id, out var known) || !ReferenceEquals(known, entry)) return null;
            try { stream = new FileStream(ZipPath(entry.Id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
            entry.References++;
        }
        try
        {
            if (stream.Length == entry.Bytes)
            {
                var written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                bool trusted;
                lock (sync) trusted = !fullHash && entry.VerifiedWrite == written && Environment.TickCount64 - entry.VerifiedAt < FullCheckAfterMilliseconds;
                if (!trusted && Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)) == entry.Id)
                {
                    trusted = true;
                    lock (sync) (entry.VerifiedWrite, entry.VerifiedAt) = (written, Environment.TickCount64);
                }
                if (trusted)
                {
                    stream.Position = 0;
                    return new OwnedStream(stream, () => Release(entry));
                }
            }
        }
        catch { await stream.DisposeAsync(); Release(entry); throw; }
        await stream.DisposeAsync(); Release(entry);
        return null;
    }

    private async Task<Entry> BuildAsync(string adapterFolder, string listing, string stamp, CancellationToken cancellationToken)
    {
        CreatePrivateFolder(Folder);
        // Only the serial builder touches part files; a lease disposed on Unix must never unlink an active build.
        foreach (var stale in Directory.EnumerateFiles(Folder, "*.part")) TryDelete(stale);
        var part = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var entry = await Task.Run(() =>
            {
                using (var output = CreatePrivateFile(part)) ZipFile.CreateFromDirectory(adapterFolder, output, CompressionLevel.Optimal, includeBaseDirectory: false);
                cancellationToken.ThrowIfCancellationRequested();
                archiveBuilt?.Invoke();
                using var written = File.OpenRead(part);
                var id = Convert.ToHexString(SHA256.HashData(written));
                var bytes = written.Length;
                // Read on the handle just hashed, which shares no writes; the move into place keeps it.
                var writtenAt = File.GetLastWriteTimeUtc(written.SafeFileHandle);
                written.Position = 0;
                using var zip = new ZipArchive(written, ZipArchiveMode.Read, leaveOpen: true);
                NodeRelease? release = null;
                if (NodePackageMetadataReader.Read(zip) is { } package && zip.GetEntry("CoCern.Adapter.dll") is { } dll)
                {
                    using var dllContent = dll.Open();
                    release = new(1, id, package, bytes, id, Convert.ToHexString(SHA256.HashData(dllContent)));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new Entry(id, bytes, release) { VerifiedWrite = writtenAt, VerifiedAt = Environment.TickCount64 };
            }, cancellationToken);
            lock (sync)
            {
                // An already leased digest has exactly these bytes; never replace its ownership record.
                if (!built.TryGetValue(entry.Id, out var existing))
                {
                    File.Move(part, ZipPath(entry.Id), overwrite: true);
                    built[entry.Id] = entry;
                }
                else
                {
                    File.Move(part, ZipPath(entry.Id), overwrite: true);
                    // The file now in place is the one just hashed to this same digest.
                    (existing.VerifiedWrite, existing.VerifiedAt) = (entry.VerifiedWrite, entry.VerifiedAt);
                    entry = existing;
                }
                var source = Track(adapterFolder);
                if (source.Current != entry.Id) { source.Previous = source.Current; source.Current = entry.Id; }
                (source.Stamp, source.Listing) = (null, null);
                Prune();
            }
            // A publication changed while copying: serve the truthful completed snapshot, but do not memoize it under the new source.
            var (afterListing, afterStamp) = await Task.Run(() => (Listing(adapterFolder, cancellationToken), Stamp(adapterFolder, cancellationToken)), cancellationToken);
            if (afterStamp == stamp)
                lock (sync)
                    if (sources.TryGetValue(adapterFolder, out var source) && source.Current == entry.Id)
                    {
                        source.Stamp = stamp;
                        // Only an unchanged listing may later stand in for this hash; otherwise the next request hashes again.
                        (source.Listing, source.ContentCheckedAt) = (afterListing == listing ? listing : null, Environment.TickCount64);
                    }
            return entry;
        }
        finally { TryDelete(part); }
    }

    private void Release(Entry entry)
    {
        lock (sync) { entry.References--; Prune(); }
    }

    private void Prune()
    {
        var kept = sources.Values.SelectMany(source => new[] { source.Current, source.Previous }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var old in built.Values.Where(e => !kept.Contains(e.Id) && e.References == 0).ToArray()) built.Remove(old.Id);
        foreach (var file in Directory.EnumerateFiles(Folder, "*.zip"))
            if (!built.ContainsKey(Path.GetFileNameWithoutExtension(file))) TryDelete(file);
    }

    /// <summary>Created owner-only on Unix, or tightened to that when it is already there; Windows keeps the data folder's inherited ACL, and the hash check covers the rest.</summary>
    private static void CreatePrivateFolder(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); return; }
        if (!Directory.Exists(path)) { Directory.CreateDirectory(path, OwnerFolder); return; }
        try { if ((File.GetUnixFileMode(path) & ~OwnerFolder) != 0) File.SetUnixFileMode(path, OwnerFolder); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
    }

    private static FileStream CreatePrivateFile(string path) => OperatingSystem.IsWindows()
        ? new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        : new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, UnixCreateMode = OwnerFile });

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A digest of the folder's metadata only: every entry's relative path, length, last write and creation time, with no file opened. Links are refused as
    /// in <see cref="Stamp"/>. Any added, removed, resized, rewritten or replaced file changes it.
    /// </summary>
    private static string Listing(string folder, CancellationToken ct)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Package links are unsupported.");
        var text = new StringBuilder();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options).OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Package links are unsupported.");
            text.Append(Path.GetRelativePath(folder, entry.FullName)).Append('\0').Append(entry is FileInfo file ? file.Length : -1).Append('\0')
                .Append(entry.LastWriteTimeUtc.Ticks).Append('\0').Append(entry.CreationTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Stamp(string folder, CancellationToken ct)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) throw new IOException("Package links are unsupported.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Package links are unsupported.");
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file) + "\0"));
            using var content = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(content));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class OwnedStream(Stream content, Action release) : Stream
    {
        private int disposed;
        public override bool CanRead => disposed == 0 && content.CanRead;
        public override bool CanSeek => disposed == 0 && content.CanSeek;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => content.Position; set => content.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => content.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);
        public override void Flush() => content.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) { try { content.Dispose(); } finally { release(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { try { await content.DisposeAsync(); } finally { release(); } }
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class AdapterReleaseLease(NodeRelease release, Stream content) : IAsyncDisposable
{
    private int disposed;
    public NodeRelease Release { get; } = release;
    public Stream Content { get; } = content;
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? Content.DisposeAsync() : ValueTask.CompletedTask;
}

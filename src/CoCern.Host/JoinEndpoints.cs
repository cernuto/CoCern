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
/// One host-owned cache serves setup and enrolled-node releases. Archive names identify exact SHA-256 bytes, and only archives built by this process
/// are trusted. Metadata comes from the completed ZIP; source-content hashes avoid needless rebuilds without trusting file times. Current, previous
/// and leased archives survive pruning. Every returned stream owns its pruning reference until disposal, including aborted HTTP responses.
/// </summary>
internal sealed class AdapterZipCache(string folder, Action? archiveBuilt = null)
{
    private const UnixFileMode OwnerFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly SemaphoreSlim building = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> built = new(StringComparer.Ordinal);
    private string? sourceStamp;
    private string? sourceId;
    private string? current;
    private string? previous;

    private sealed class Entry(string id, long bytes, NodeRelease? release)
    {
        public string Id { get; } = id;
        public long Bytes { get; } = bytes;
        public NodeRelease? Release { get; } = release;
        public int References { get; set; }
    }

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary><c>adapter-zip</c> in the folder that holds the hub's database, join code and <c>local-hub.json</c>.</summary>
    public static string DefaultFolder(IConfiguration configuration, IHostEnvironment environment) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RelayHostBuilderExtensions.DatabasePath(configuration, environment)))!, "adapter-zip");

    /// <summary>The zip of <paramref name="adapterFolder"/>, opened for reading after its hash was checked; the caller disposes it.</summary>
    public async Task<Stream> OpenAsync(string adapterFolder, CancellationToken cancellationToken)
    {
        await building.WaitAsync(cancellationToken);
        try
        {
            var entry = await GetAsync(adapterFolder, cancellationToken);
            return await OpenVerifiedAsync(entry, cancellationToken) ?? throw new IOException("The adapter zip changed right after it was built.");
        }
        finally { building.Release(); }
    }

    public async Task<AdapterReleaseLease?> ReleaseAsync(string adapterFolder, CancellationToken ct)
    {
        await building.WaitAsync(ct);
        try
        {
            var entry = await GetAsync(adapterFolder, ct);
            if (entry.Release is null) return null;
            var content = await OpenVerifiedAsync(entry, ct);
            return content is null ? null : new AdapterReleaseLease(entry.Release, content);
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

    // Only the serial builder accesses the source index. Full content hashing also detects same-length, same-mtime dependency changes.
    private async Task<Entry> GetAsync(string adapterFolder, CancellationToken ct)
    {
        var stamp = await Task.Run(() => Stamp(adapterFolder, ct), ct);
        if (stamp == sourceStamp && sourceId is { } id)
        {
            Entry? existing;
            lock (sync) built.TryGetValue(id, out existing);
            if (existing is not null)
            {
                await using var checkedContent = await OpenVerifiedAsync(existing, ct);
                if (checkedContent is not null) return existing;
            }
        }
        return await BuildAsync(adapterFolder, stamp, ct);
    }

    /// <summary>
    /// The cached zip for <paramref name="stamp"/> when this process built it and its bytes still hash the same, else null. The hash is checked on the open
    /// handle that is then served, and that handle shares no writes, so what was checked is what goes out.
    /// </summary>
    private async Task<Stream?> OpenVerifiedAsync(Entry entry, CancellationToken cancellationToken)
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
            if (stream.Length == entry.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)) == entry.Id)
            {
                stream.Position = 0;
                return new OwnedStream(stream, () => Release(entry));
            }
        }
        catch { await stream.DisposeAsync(); Release(entry); throw; }
        await stream.DisposeAsync(); Release(entry);
        return null;
    }

    private async Task<Entry> BuildAsync(string adapterFolder, string stamp, CancellationToken cancellationToken)
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
                written.Position = 0;
                using var zip = new ZipArchive(written, ZipArchiveMode.Read, leaveOpen: true);
                NodeRelease? release = null;
                if (NodePackageMetadataReader.Read(zip) is { } package && zip.GetEntry("CoCern.Adapter.dll") is { } dll)
                {
                    using var dllContent = dll.Open();
                    release = new(1, id, package, bytes, id, Convert.ToHexString(SHA256.HashData(dllContent)));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new Entry(id, bytes, release);
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
                    entry = existing;
                }
                if (current != entry.Id) { previous = current; current = entry.Id; }
                Prune();
            }
            // A publication changed while copying: serve the truthful completed snapshot, but do not memoize it under the new source.
            if (await Task.Run(() => Stamp(adapterFolder, cancellationToken), cancellationToken) == stamp) { sourceStamp = stamp; sourceId = entry.Id; }
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
        foreach (var old in built.Values.Where(e => e.Id != current && e.Id != previous && e.References == 0).ToArray()) built.Remove(old.Id);
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

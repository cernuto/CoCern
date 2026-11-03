using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CoCern.Contracts;
using CoCern.Host.Dashboard;

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
        // Tests register their own cache folder; the hub's is beside its database.
        var cache = app.Services.GetService<AdapterZipCache>() ?? new AdapterZipCache(AdapterZipCache.DefaultFolder(app.Configuration, app.Environment));
        app.MapGet(AdapterZipPath, (HttpContext context, DashboardConnect connect) => AdapterZipAsync(context, connect, cache)).AllowAnonymous();
    }

    private static async Task<IResult> AdapterZipAsync(HttpContext context, DashboardConnect connect, AdapterZipCache cache)
    {
        if (!context.Request.IsHttps && !(context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)))
            return Results.Json(ToolResults.Failure(RelayErrorCodes.HttpsRequired), statusCode: StatusCodes.Status403Forbidden);
        if (connect.ServedAdapter() is not { } adapter || !Directory.Exists(adapter.Folder)) return Results.NotFound();
        var zip = await cache.OpenAsync(adapter.Folder, context.RequestAborted);
        return Results.File(zip, "application/zip", AdapterZipName, enableRangeProcessing: true);
    }
}

/// <summary>
/// The zip of the served adapter folder as it is now, built once per folder state: its name is a hash of the folder's file names, sizes and write times,
/// so a rebuilt adapter gets a fresh zip and an unchanged one is served from the cache. The cache is in the hub's own data folder (owner-only on Unix),
/// never the shared temp folder, where another local user could plant a zip that joining PCs would run, and where other hubs on the PC would delete
/// this one's. Even so, a file is served only when its SHA-256 matches the zip this process built under that name; anything else there, left by an earlier
/// start or put there by someone else, is rebuilt. The build runs off the request thread into a private <c>.part</c> file that is moved into place (and
/// deleted whatever happens), so a second request during the build waits for it instead of reading a half-written zip.
/// </summary>
internal sealed class AdapterZipCache(string folder)
{
    private const UnixFileMode OwnerFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly SemaphoreSlim building = new(1, 1);
    private readonly ConcurrentDictionary<string, byte[]> built = new(StringComparer.Ordinal);

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary><c>adapter-zip</c> in the folder that holds the hub's database, join code and <c>local-hub.json</c>.</summary>
    public static string DefaultFolder(IConfiguration configuration, IHostEnvironment environment) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RelayHostBuilderExtensions.DatabasePath(configuration, environment)))!, "adapter-zip");

    /// <summary>The zip of <paramref name="adapterFolder"/>, opened for reading after its hash was checked; the caller disposes it.</summary>
    public async Task<Stream> OpenAsync(string adapterFolder, CancellationToken cancellationToken)
    {
        var stamp = Stamp(adapterFolder);
        if (await OpenVerifiedAsync(stamp, cancellationToken) is { } cached) return cached;
        await building.WaitAsync(cancellationToken);
        try
        {
            if (await OpenVerifiedAsync(stamp, cancellationToken) is { } waited) return waited;
            await BuildAsync(adapterFolder, stamp, cancellationToken);
            return await OpenVerifiedAsync(stamp, cancellationToken) ?? throw new IOException("The adapter zip changed right after it was built.");
        }
        finally { building.Release(); }
    }

    private string ZipPath(string stamp) => Path.Combine(Folder, $"{stamp}.zip");

    /// <summary>
    /// The cached zip for <paramref name="stamp"/> when this process built it and its bytes still hash the same, else null. The hash is checked on the open
    /// handle that is then served, and that handle shares no writes, so what was checked is what goes out.
    /// </summary>
    private async Task<Stream?> OpenVerifiedAsync(string stamp, CancellationToken cancellationToken)
    {
        if (!built.TryGetValue(stamp, out var expected)) return null;
        FileStream stream;
        try { stream = new FileStream(ZipPath(stamp), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
        try
        {
            if (CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, cancellationToken), expected))
            {
                stream.Position = 0;
                return stream;
            }
        }
        catch { await stream.DisposeAsync(); throw; }
        await stream.DisposeAsync();
        return null;
    }

    private async Task BuildAsync(string adapterFolder, string stamp, CancellationToken cancellationToken)
    {
        CreatePrivateFolder(Folder);
        var path = ZipPath(stamp);
        var part = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var hash = await Task.Run(() =>
            {
                using (var output = CreatePrivateFile(part)) ZipFile.CreateFromDirectory(adapterFolder, output, CompressionLevel.Optimal, includeBaseDirectory: false);
                using var written = File.OpenRead(part);
                return SHA256.HashData(written);
            }, cancellationToken);
            File.Move(part, path, overwrite: true);
            built[stamp] = hash;
        }
        finally { TryDelete(part); }
        // Older zips, and .part files a stopped hub left behind (two hubs never share a data folder: the database lock refuses the second).
        foreach (var stale in Directory.EnumerateFiles(Folder).Where(file => !string.Equals(file, path, StringComparison.OrdinalIgnoreCase) && (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))))
            TryDelete(stale);
        foreach (var old in built.Keys.Where(key => key != stamp)) built.TryRemove(old, out _);
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

    private static string Stamp(string folder)
    {
        var builder = new StringBuilder(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            builder.Append('\n').Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32];
    }
}

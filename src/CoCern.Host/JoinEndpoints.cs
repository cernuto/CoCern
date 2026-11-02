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
/// because the download is code the other PC will run.
/// </summary>
internal static class JoinEndpoints
{
    public const string AdapterZipPath = "/join/adapter.zip";
    public const string AdapterZipName = "CoCern.Adapter.zip";
    private static readonly SemaphoreSlim Building = new(1, 1);

    public static void MapJoinEndpoints(this WebApplication app)
    {
        if (!DiscoverySettings.Enabled(app.Configuration)) return;
        app.MapGet(AdapterZipPath, AdapterZipAsync).AllowAnonymous();
    }

    private static async Task<IResult> AdapterZipAsync(HttpContext context, DashboardConnect connect)
    {
        if (!context.Request.IsHttps && !(context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)))
            return Results.Json(ToolResults.Failure(RelayErrorCodes.HttpsRequired), statusCode: StatusCodes.Status403Forbidden);
        if (connect.AdapterDll() is not { } adapter || Path.GetDirectoryName(adapter) is not { } folder || !Directory.Exists(folder)) return Results.NotFound();
        var zip = await CachedZipAsync(folder, context.RequestAborted);
        return Results.File(zip, "application/zip", AdapterZipName, enableRangeProcessing: true);
    }

    /// <summary>
    /// The zip of the folder as it is now, built once per folder state: its name carries a hash of the folder's file names, sizes and write times, so a
    /// rebuilt adapter gets a fresh zip and an unchanged one is served straight from the temp folder. The build runs off the request thread and writes to
    /// a private file first, so a second request during the build waits for it instead of reading a half-written zip.
    /// </summary>
    private static async Task<string> CachedZipAsync(string folder, CancellationToken cancellationToken)
    {
        var cache = Path.Combine(Path.GetTempPath(), "CoCern", "adapter-zip");
        var path = Path.Combine(cache, $"{Stamp(folder)}.zip");
        if (File.Exists(path)) return path;
        await Building.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(cache);
            var building = path + "." + Guid.NewGuid().ToString("N") + ".part";
            await Task.Run(() => ZipFile.CreateFromDirectory(folder, building, CompressionLevel.Optimal, includeBaseDirectory: false), cancellationToken);
            File.Move(building, path, overwrite: true);
            foreach (var stale in Directory.EnumerateFiles(cache, "*.zip").Where(file => !string.Equals(file, path, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return path;
        }
        finally { Building.Release(); }
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

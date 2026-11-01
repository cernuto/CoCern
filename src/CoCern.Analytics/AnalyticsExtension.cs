using System.IO.Compression;
using DuckDB.NET.Data;
using CoCern.Contracts;

namespace CoCern.Analytics;

/// <summary>
/// Installs DuckDB's <c>sqlite</c> extension into a folder, so the hub can load it later without internet access. DuckDB's own
/// <c>INSTALL sqlite</c> downloads over plain HTTP, which proxies often refuse, so this downloads the signed extension over HTTPS and
/// places it where DuckDB looks (<c>&lt;folder&gt;/&lt;version&gt;/&lt;platform&gt;/</c>). DuckDB checks its signature on every load.
/// </summary>
public static class AnalyticsExtension
{
    private const string Repository = "https://extensions.duckdb.org";
    private const string FileName = "sqlite_scanner.duckdb_extension";

    /// <summary>Whether DuckDB can load the <c>sqlite</c> extension from <paramref name="directory"/> with downloads turned off.</summary>
    public static bool IsInstalled(string directory)
    {
        try
        {
            using var connection = Open(directory);
            Run(connection, "LOAD sqlite;");
            return true;
        }
        catch (DuckDBException) { return false; }
    }

    /// <summary>
    /// Makes sure <paramref name="directory"/> holds the <c>sqlite</c> extension for this DuckDB version and <paramref name="platform"/>
    /// (default: this machine's). Downloads only when it is missing. An extension for this machine is loaded once to check it; one for
    /// another platform, such as <c>windows_amd64</c> for a Windows hub built elsewhere, is checked when that hub loads it. Returns what
    /// it did, for the build log.
    /// </summary>
    public static async Task<string> InstallAsync(string directory, string? platform = null, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        string version, native;
        using (var connection = Open(directory))
        {
            version = Scalar(connection, "SELECT version()");
            native = Scalar(connection, "SELECT platform FROM pragma_platform()");
        }
        platform = string.IsNullOrWhiteSpace(platform) ? native : platform.Trim();
        if (platform.IndexOfAny([.. Path.GetInvalidFileNameChars(), '.']) >= 0) throw new ArgumentException("The platform must be a DuckDB platform name such as windows_amd64.", nameof(platform));
        var isNative = platform == native;
        var installed = Path.Combine(directory, version, platform, FileName);
        if (isNative ? IsInstalled(directory) : File.Exists(installed)) return $"The DuckDB sqlite extension for {platform} is already installed in {directory}.";

        var url = $"{Repository}/{version}/{platform}/{FileName}.gz";
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        var download = installed + ".download-" + Guid.NewGuid().ToString("N");
        var client = http ?? new HttpClient();
        try
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var gzip = new GZipStream(source, CompressionMode.Decompress);
                await using var target = File.Create(download);
                await CopyCappedAsync(gzip, target, RelayLimits.MaxAnalyticsExtensionBytes, cancellationToken);
            }
            File.Move(download, installed, overwrite: true);
            if (isNative && !IsInstalled(directory))
            {
                File.Delete(installed);
                throw new InvalidOperationException($"DuckDB could not load the extension downloaded from {url}.");
            }
            return $"Installed the DuckDB sqlite extension for {platform} from {url} into {directory}.";
        }
        finally
        {
            if (http is null) client.Dispose();
            try { File.Delete(download); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes and fails past that, so a bad download cannot fill the disk.</summary>
    internal static async Task CopyCappedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException($"The extension is larger than {limit / (1024 * 1024)} MB once decompressed.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static DuckDBConnection Open(string directory)
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        try
        {
            connection.Open();
            Run(connection, $"SET extension_directory = {AnalyticsService.Literal(Path.GetFullPath(directory))}; SET autoinstall_known_extensions = false; SET autoload_known_extensions = false;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Run(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
}

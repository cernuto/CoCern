using System.IO.Compression;
using DuckDB.NET.Data;

namespace PkRelay.Analytics;

/// <summary>
/// Installs DuckDB's <c>sqlite</c> extension into a folder, so the hub can load it later without internet access. DuckDB's own
/// <c>INSTALL sqlite</c> downloads over plain HTTP, which proxies often refuse, so this downloads the signed extension over HTTPS and
/// installs it from the file. Loading it checks DuckDB's signature.
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
    /// Makes sure <paramref name="directory"/> holds the <c>sqlite</c> extension for this DuckDB version and this machine's platform.
    /// Downloads only when it is missing. Returns what it did, for the build log.
    /// </summary>
    public static async Task<string> InstallAsync(string directory, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (IsInstalled(directory)) return $"The DuckDB sqlite extension is already installed in {directory}.";
        string url;
        using (var connection = Open(directory))
            url = $"{Repository}/{Scalar(connection, "SELECT version()")}/{Scalar(connection, "SELECT platform FROM pragma_platform()")}/{FileName}.gz";
        // DuckDB names an extension installed from a file after the file, so the download keeps the exact name in its own folder.
        var download = Path.Combine(directory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(download);
        var file = Path.Combine(download, FileName);
        var client = http ?? new HttpClient();
        try
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var gzip = new GZipStream(source, CompressionMode.Decompress);
                await using var target = File.Create(file);
                await gzip.CopyToAsync(target, cancellationToken);
            }
            using var connection = Open(directory);
            Run(connection, $"INSTALL {AnalyticsService.Literal(file)}; LOAD sqlite;");
            return $"Installed the DuckDB sqlite extension from {url} into {directory}.";
        }
        finally
        {
            if (http is null) client.Dispose();
            try { Directory.Delete(download, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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

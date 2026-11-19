using System.IO.Compression;
using DuckDB.NET.Data;
using CoCern.Contracts;

namespace CoCern.Analytics;

/// <summary>
/// Stages a local copy of DuckDB's signed <c>sqlite</c> extension in the version and platform directory DuckDB uses.
/// </summary>
public static class AnalyticsExtension
{
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

    /// <summary>The checksum manifest the build copies next to the bundled extension.</summary>
    public const string ManifestName = "SHA256SUMS";

    /// <summary>
    /// Why analytics cannot load the <c>sqlite</c> extension from <paramref name="directory"/>, or null when it can. The file for this
    /// DuckDB version and platform must be there and, when the folder has a <see cref="ManifestName"/> (the bundled folder does), match its
    /// SHA-256 sum; DuckDB then checks its signature on <c>LOAD</c>. Never throws: a missing native library is a reason too.
    /// </summary>
    public static string? Problem(string directory)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            string version, platform;
            using (var connection = Open(directory))
            {
                version = Scalar(connection, "SELECT version()");
                platform = Scalar(connection, "SELECT platform FROM pragma_platform()");
            }
            var relative = $"{version}/{platform}/{FileName}";
            var file = Path.Combine(directory, version, platform, FileName);
            if (!File.Exists(file)) return $"The DuckDB sqlite extension for {platform} is not in {directory}.";
            if (ChecksumProblem(directory, relative, file) is { } problem) return problem;
            return IsInstalled(directory) ? null : $"DuckDB could not load the sqlite extension from {directory}.";
        }
        catch (Exception exception) { return "DuckDB could not start: " + exception.Message.Split('\n', 2)[0].Trim(); }
    }

    /// <summary>
    /// Installs a local extension file (raw or gzip) or copies the packaged extension. The native DuckDB runtime checks the signature
    /// before publication. When preparing a different platform, its DuckDB runtime checks the signature when loading it.
    /// </summary>
    public static async Task<string> InstallAsync(string directory, string? platform = null, string? sourceFile = null, CancellationToken cancellationToken = default)
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
        var relative = $"{version}/{platform}/{FileName}";
        if (File.Exists(installed) && ChecksumProblem(directory, relative, installed) is null && (!isNative || IsInstalled(directory)))
            return $"The DuckDB sqlite extension for {platform} is already installed in {directory}.";

        sourceFile ??= Path.Combine(AnalyticsOptions.DefaultExtensionDirectory, version, platform, FileName);
        if (Uri.TryCreate(sourceFile, UriKind.Absolute, out var uri) && (!uri.IsFile || uri.IsUnc))
            throw new ArgumentException("Supply a file on the local filesystem for the DuckDB sqlite extension.", nameof(sourceFile));
        sourceFile = Path.GetFullPath(sourceFile);
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException($"Supply the local {version}/{platform}/{FileName} file (raw or gzip) as the final argument.", sourceFile);

        var staging = Path.Combine(Path.GetDirectoryName(installed)!, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var staged = Path.Combine(staging, FileName);
        try
        {
            await using (var source = File.OpenRead(sourceFile))
            await using (var target = File.Create(staged))
            {
                using var gzip = sourceFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : null;
                await CopyCappedAsync((Stream?)gzip ?? source, target, RelayLimits.MaxAnalyticsExtensionBytes, cancellationToken);
            }
            if (ChecksumProblem(directory, relative, staged) is { } problem) throw new InvalidDataException(problem);
            if (isNative)
            {
                using var connection = Open(directory);
                Run(connection, $"LOAD {AnalyticsService.Literal(staged)};");
            }
            File.Move(staged, installed, overwrite: true);
            return $"Installed the DuckDB sqlite extension for {platform} from {sourceFile} into {directory}.";
        }
        finally
        {
            try { File.Delete(staged); Directory.Delete(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? ChecksumProblem(string directory, string relative, string file)
    {
        var manifest = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifest)) return null;
        var expected = File.ReadLines(manifest).Select(line => line.Trim()).Where(line => line.Length > 66 && line[66..].Trim() == relative).Select(line => line[..64]).FirstOrDefault();
        if (expected is null) return $"The extension manifest in {directory} has no sum for {relative}.";
        using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase)
            ? null : $"The DuckDB sqlite extension in {directory} does not match its SHA-256 sum.";
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes, including when expanding a gzip file.</summary>
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

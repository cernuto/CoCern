using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using CoCern.Domain;
using CoCern.Analytics;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using CoCern.Storage;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The DuckDB sqlite extension for tests: the folder in <c>COCERN_DUCKDB_EXTENSIONS</c> when set; otherwise the bundled folder the build
/// unpacks next to the test binaries, where it covers this platform (Windows x64); otherwise a per-machine cache in the temp folder,
/// downloaded once when missing. After that the tests need no network.
/// </summary>
internal static class AnalyticsTestExtension
{
    private static readonly Lazy<Task<string>> Ready = new(async () =>
    {
        if (Environment.GetEnvironmentVariable("COCERN_DUCKDB_EXTENSIONS") is { Length: > 0 } configured)
        {
            var folder = Path.GetFullPath(configured);
            if (!AnalyticsExtension.IsInstalled(folder)) await AnalyticsExtension.InstallAsync(folder);
            return folder;
        }
        if (AnalyticsExtension.Problem(AnalyticsOptions.DefaultExtensionDirectory) is null) return AnalyticsOptions.DefaultExtensionDirectory;
        var directory = Path.Combine(Path.GetTempPath(), "cocern-duckdb-extensions");
        if (!AnalyticsExtension.IsInstalled(directory)) await AnalyticsExtension.InstallAsync(directory);
        return directory;
    });

    public static Task<string> DirectoryAsync() => Ready.Value;

    /// <summary>Finds an existing fixture only; never downloads an extension or changes machine configuration.</summary>
    public static string InstalledDirectory()
    {
        if (Environment.GetEnvironmentVariable("COCERN_DUCKDB_EXTENSIONS") is { Length: > 0 } configured)
        {
            var folder = Path.GetFullPath(configured);
            return AnalyticsExtension.Problem(folder) is null ? folder : throw new InvalidOperationException("The configured DuckDB test extension is not installed or valid.");
        }
        foreach (var folder in new[] { AnalyticsOptions.DefaultExtensionDirectory, Path.Combine(Path.GetTempPath(), "cocern-duckdb-extensions") })
            if (AnalyticsExtension.Problem(folder) is null) return folder;
        throw new InvalidOperationException("A preinstalled DuckDB sqlite extension is required; this test never downloads or installs one.");
    }
}

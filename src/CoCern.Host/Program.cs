using CoCern.Analytics;
using CoCern.Host;

// Composition root only: each concern is registered and mapped by its own extension.
public partial class Program
{
    public static async Task Main(string[] args)
    {
        // Build-time setup: preinstall DuckDB's sqlite extension so analytics works offline. See docs/reference.md#analytics.
        if (args is [AnalyticsInstallSwitch, ..])
        {
            await InstallAnalyticsExtensionAsync(args.Length > 1 ? args[1] : AnalyticsOptions.DefaultExtensionDirectory,
                args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null);
            return;
        }
        var builder = WebApplication.CreateBuilder(args);
        builder.AddRelayHost();
        builder.Services.AddHostedService<CoCern.Host.Development.DevelopmentAdapterService>();
        var app = builder.Build();
        app.UseRelayPipeline();
        app.MapRelayEndpoints();
        await app.RunAsync();
    }

    private const string AnalyticsInstallSwitch = "--install-analytics-extension";

    /// <summary><c>--install-analytics-extension [folder] [platform] [local-extension-file]</c>. The default source is the packaged extension.</summary>
    private static async Task InstallAnalyticsExtensionAsync(string directory, string? platform, string? sourceFile)
    {
        try { Console.WriteLine(await AnalyticsExtension.InstallAsync(directory, platform, sourceFile)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or DuckDB.NET.Data.DuckDBException or TaskCanceledException)
        {
            Console.Error.WriteLine("Could not install the DuckDB sqlite extension: " + exception.Message);
            Environment.ExitCode = 1;
        }
    }
}

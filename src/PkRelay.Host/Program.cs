using PkRelay.Analytics;
using PkRelay.Host;

// Composition root only: each concern is registered and mapped by its own extension.
public partial class Program
{
    public static async Task Main(string[] args)
    {
        // Build-time setup: preinstall DuckDB's sqlite extension so analytics works offline. See docs/analytics.md.
        if (args is [AnalyticsInstallSwitch, ..])
        {
            await InstallAnalyticsExtensionAsync(args.Length > 1 ? args[1] : AnalyticsOptions.DefaultExtensionDirectory);
            return;
        }
        var builder = WebApplication.CreateBuilder(args);
        builder.AddRelayHost();
        var app = builder.Build();
        app.UseRelayPipeline();
        app.MapRelayEndpoints();
        await app.RunAsync();
    }

    private const string AnalyticsInstallSwitch = "--install-analytics-extension";

    private static async Task InstallAnalyticsExtensionAsync(string directory)
    {
        try { Console.WriteLine(await AnalyticsExtension.InstallAsync(directory)); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or DuckDB.NET.Data.DuckDBException or TaskCanceledException)
        {
            Console.Error.WriteLine("Could not install the DuckDB sqlite extension: " + exception.Message);
            Environment.ExitCode = 1;
        }
    }
}

using CoCern.Contracts;

namespace CoCern.Analytics;

/// <summary>Settings for <see cref="AnalyticsService"/>, from <c>Relay:Analytics:*</c>.</summary>
public sealed record AnalyticsOptions
{
    /// <summary>The folder that ships next to the hub with the DuckDB <c>sqlite</c> extension preinstalled.</summary>
    public static string DefaultExtensionDirectory => Path.Combine(AppContext.BaseDirectory, "duckdb_extensions");

    /// <summary>Where DuckDB loads the <c>sqlite</c> extension from. Nothing is downloaded at run time.</summary>
    public string ExtensionDirectory { get; init; } = DefaultExtensionDirectory;

    /// <summary>How long one query may run before it is interrupted.</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(RelayLimits.DefaultAnalyticsQueryTimeoutSeconds);

    /// <summary>Rows one result keeps; the rest are cut off and the result says so.</summary>
    public int MaxRows { get; init; } = RelayLimits.DefaultAnalyticsMaxRows;

    /// <summary>The hub's live database. A snapshot at this path is refused, so DuckDB never opens the live file.</summary>
    public string? LiveDatabasePath { get; init; }

    /// <summary>The compact test-effort file <c>test_effort</c> reads, or null for none. It is refreshed outside the hub; the hub never runs git.</summary>
    public string? TestEffortFile { get; init; }

    /// <summary>Refuses settings outside their limits with a message that names the setting.</summary>
    public AnalyticsOptions Validated() =>
        QueryTimeout <= TimeSpan.Zero || QueryTimeout > TimeSpan.FromSeconds(RelayLimits.MaxAnalyticsQueryTimeoutSeconds) ? throw new InvalidOperationException($"Relay:Analytics:QueryTimeout must be more than 0 and at most {RelayLimits.MaxAnalyticsQueryTimeoutSeconds} seconds.")
        : MaxRows is < 1 or > RelayLimits.MaxAnalyticsMaxRows ? throw new InvalidOperationException($"Relay:Analytics:MaxRows must be from 1 to {RelayLimits.MaxAnalyticsMaxRows}.")
        : string.IsNullOrWhiteSpace(ExtensionDirectory) ? throw new InvalidOperationException("Relay:Analytics:ExtensionDirectory must name a folder.")
        : this;
}

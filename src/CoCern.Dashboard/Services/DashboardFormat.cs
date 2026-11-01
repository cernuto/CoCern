using System.Globalization;
using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

public static class DashboardFormat
{
    public static string Count(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    public static string Timestamp(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Not reported";

    public static string Ago(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is null) return "Not reported";
        var age = now - value.Value;
        if (age.TotalSeconds < 5) return "Just now";
        if (age.TotalSeconds < 60) return $"{Math.Max(0, (int)age.TotalSeconds)}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    public static string Bytes(long? value) => value switch
    {
        null => "—",
        < 1024 => $"{value:N0} B",
        < 1_048_576 => $"{value / 1024d:N1} KB",
        < 1_073_741_824 => $"{value / 1_048_576d:N1} MB",
        _ => $"{value / 1_073_741_824d:N1} GB"
    };

    public static string Freshness(DashboardSession session, DateTimeOffset now) =>
        session.IncarnationMismatch ? "Report stale" : session.FreshUntil is null ? "Freshness unknown" : session.FreshUntil > now ? "Report current" : "Report stale";

    public static string CountQuality(string kind) => kind.ToLowerInvariant() switch
    {
        "exact" => "Exact",
        "estimated" or "estimate" => "Estimated",
        "lowerbound" or "lower-bound" or "lower_bound" => "Lower bound",
        _ => "Unavailable"
    };

    public static string Rows(DatabaseTableMetrics table) => table.RowCount is null
        ? "—" : (CountQuality(table.CountKind) == "Lower bound" ? "≥ " : "") + Count(table.RowCount);
}

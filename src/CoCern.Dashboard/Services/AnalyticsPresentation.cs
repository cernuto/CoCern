using System.Globalization;

namespace CoCern.Dashboard.Services;

public static class AnalyticsPresentation
{
    public static string Number(double? value, string unit) => value is { } number && double.IsFinite(number)
        ? $"{number.ToString("0.##", CultureInfo.InvariantCulture)}{(unit.Length == 0 ? "" : " " + unit)}"
        : "Not measured";

    public static string Width(double? value, double maximum)
    {
        if (value is not { } number || !double.IsFinite(number) || number <= 0 || !double.IsFinite(maximum) || maximum <= 0)
            return "0";
        return (Math.Clamp(number / maximum, 0, 1) * 100).ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string ComparisonLabel(string label, double? current, double? previous, long currentCount, long previousCount, string unit) =>
        $"{label}. Current: {Number(current, unit)}, {currentCount} samples. Previous: {Number(previous, unit)}, {previousCount} samples.";
}

using System.Globalization;

namespace CoCern.Dashboard.Services;

public static class DashboardUsefulness
{
    public static T[] RankTools<T>(IEnumerable<T> rows, Func<T, string> key, Func<T, double?> bytes, string origin) => rows
        .Where(row => origin == "all" || Origin(key(row)) == origin)
        .OrderByDescending(row => bytes(row) is { } value && double.IsFinite(value) && value > 0 ? value : 0)
        .ThenBy(key, StringComparer.Ordinal).ToArray();

    public static string Origin(string key) => key.EndsWith(" [model-facing]", StringComparison.Ordinal) ? "model"
        : key.EndsWith(" [background]", StringComparison.Ordinal) ? "background" : "unknown";

    public static bool MatchesLog(string level, string category, string message, string? sessionId, string levelFilter, string search, bool hideRoutine)
    {
        if (levelFilter == "problems" ? !new[] { "Warning", "Error", "Critical", "Fatal" }.Contains(level, StringComparer.OrdinalIgnoreCase)
            : levelFilter.Length > 0 && !level.Equals(levelFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (hideRoutine && IsRoutineHttp(level, category, message)) return false;
        var term = search.Trim();
        return term.Length == 0 || new[] { category, message, sessionId ?? "" }.Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRoutineHttp(string level, string category, string message) =>
        level.Equals("Information", StringComparison.OrdinalIgnoreCase) && category == "HTTP"
        && message.Length == "Relay request completed (HTTP 200).".Length
        && message.StartsWith("Relay request completed (HTTP ", StringComparison.Ordinal)
        && message.EndsWith(").", StringComparison.Ordinal)
        && int.TryParse(message.AsSpan(30, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var status)
        && status is >= 200 and < 400;

    public static string SnapshotAge(DateTimeOffset snapshot, DateTimeOffset now)
    {
        var age = now - snapshot;
        if (age < TimeSpan.Zero) return "Snapshot timestamp is ahead of this device's clock.";
        if (age.TotalMinutes < 1) return "Less than a minute old at this render.";
        return age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes old at this render."
            : $"{age.TotalHours.ToString("0.0", CultureInfo.InvariantCulture)} hours old at this render.";
    }
}

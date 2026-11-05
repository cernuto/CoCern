using System.Globalization;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Analytics;

/// <summary>The result of reading the test-effort file for one repository. A file for another repository reads as no data.</summary>
public sealed record TestEffortRead(string Status, TestEffortReport? Report);

/// <summary>
/// Reads the compact file <c>Relay:Analytics:TestEffortFile</c> names. The hub never runs git: <c>scripts/Build-TestEffort.py</c> refreshes the file
/// outside it. Bounded by <see cref="RelayLimits.MaxTestEffortFileBytes"/>, parsed once per file change, and read only when a caller asks, never on
/// the dashboard's tick. Failures carry a fixed status, so the path never reaches a reply.
/// </summary>
internal sealed class TestEffortFile(string? path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TestEffortRead NoData = new(DashboardTestEffort.NoRunData, null);
    private static readonly TestEffortRead Unreadable = new(DashboardTestEffort.Unreadable, null);

    private sealed record Cached(long Length, DateTime WrittenUtc, TestEffortRead Read);

    private volatile Cached? _cached;

    public async Task<TestEffortRead> ReadAsync(string repositoryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return NoData;
        var read = await LoadAsync(path, cancellationToken);
        if (read.Report is not { } report) return read;
        return report.RepositoryId == repositoryId ? read : NoData;
    }

    private async Task<TestEffortRead> LoadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return NoData;
            var (length, written) = (info.Length, info.LastWriteTimeUtc);
            if (_cached is { } cached && cached.Length == length && cached.WrittenUtc == written) return cached.Read;
            var read = Parse(await ReadBoundedAsync(file, cancellationToken));
            _cached = new(length, written, read);
            return read;
        }
        catch (IOException) { return Unreadable; }
        catch (UnauthorizedAccessException) { return Unreadable; }
    }

    /// <summary>Stops at the limit whatever the file's length said a moment ago; null past it.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16_384, useAsync: true);
        var buffer = new byte[RelayLimits.MaxTestEffortFileBytes + 1];
        var total = 0;
        int count;
        while (total < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0) total += count;
        return total > RelayLimits.MaxTestEffortFileBytes ? null : buffer[..total];
    }

    private static TestEffortRead Parse(byte[]? content)
    {
        if (content is null) return Unreadable;
        try
        {
            var report = JsonSerializer.Deserialize<TestEffortReport>(content, JsonOptions);
            if (report?.IsValid() != true) return Unreadable;
            return new(report.Runs > 0 ? DashboardTestEffort.Ready : DashboardTestEffort.NoRunData, report);
        }
        catch (JsonException) { return Unreadable; }
    }
}

/// <summary>
/// The <c>test_effort</c> rows, in the shape every named query has. Periods end on the file's latest day, because the file is refreshed by hand, not
/// on the hourly snapshot's clock. Each key says whether its number is measured or an estimate, and a value with no samples is null: no recorded run
/// is not zero minutes of testing.
/// </summary>
internal static class TestEffortRows
{
    public const string NoRunDataKey = "no_run_data";
    public const string UnreadableKey = "run_data_unreadable";

    private const string Measured = "[" + TestEffortRules.Measured + "]";

    public static IReadOnlyList<IReadOnlyList<object?>> Build(TestEffortRead read, int days)
    {
        if (read.Status == DashboardTestEffort.Unreadable) return [Row(UnreadableKey, null, null, 0, 0)];
        if (read.Report is not { Days.Length: > 0 } report) return [Row(NoRunDataKey, null, null, 0, 0)];

        var rows = new List<IReadOnlyList<object?>>();
        var latest = Parse(report.Days[^1]);
        var from = latest.AddDays(1 - days);
        var before = from.AddDays(-days);
        var current = report.Days.Where(day => Parse(day) >= from).ToArray();
        var previous = report.Days.Where(day => Parse(day) >= before && Parse(day) < from).ToArray();

        void Add(string key, Func<TestEffortDay, double> value, Func<TestEffortDay, long> samples) =>
            rows.Add(Row(key, Total(current, value, samples), Total(previous, value, samples), current.Sum(samples), previous.Sum(samples)));
        void AddPercent(string key, Func<TestEffortDay, double> part) =>
            rows.Add(Row(key, Percent(current, part), Percent(previous, part), current.Sum(day => (long)day.Runs), previous.Sum(day => (long)day.Runs)));
        void AddRule(string rule, string name, Func<TestEffortDay, double> value)
        {
            var status = TestEffortRules.Status(report.Rules.First(result => result.Rule == rule));
            if (status == TestEffortRules.NotAvailable) rows.Add(Row($"{name} [{status}]", null, null, 0, 0));
            else Add($"{name} [{status}]", value, RunCount);
        }

        if (report.Runs > 0)
        {
            Add($"test_minutes {Measured}", day => day.WallMinutes, RunCount);
            Add($"case_minutes {Measured}", day => day.CaseMinutes, RunCount);
            Add($"pointless_minutes {Measured}, rule b", day => day.RuleBMinutes, RunCount);
            AddPercent($"pointless_percent {Measured}, rule b", day => day.RuleBMinutes);
            Add("pointless_minutes [estimate], rules a b c", day => day.PointlessEstimatedMinutes, RunCount);
            AddPercent("pointless_percent [estimate], rules a b c", day => day.PointlessEstimatedMinutes);
            Add($"whole_suite_minutes {Measured}, not all pointless", day => day.WholeSuiteMinutes, day => day.WholeSuiteRuns);
            AddRule("a", "rule_a_no_covering_test_minutes", day => day.RuleAMinutes);
            AddRule("c", "rule_c_whole_suite_unchanged_minutes", day => day.RuleCMinutes);
            AddRule("d", "rule_d_fixture_only_failures", day => day.FixtureOnlyFailures);
            Add($"failures {Measured}", day => day.Failures, RunCount);
        }
        else
        {
            rows.Add(Row(NoRunDataKey, null, null, 0, 0));
        }
        Add($"tests_added {Measured}", day => day.TestsAdded, day => day.Commits);
        Add($"tests_deleted {Measured}", day => day.TestsDeleted, day => day.Commits);
        if (report.Runs > 0)
        {
            rows.Add(Row($"runs_without_commit {Measured}, all recorded runs", report.RunsWithoutCommit, null, report.Runs, 0));
            // Not period figures: the whole recorded span is what "never failed" can speak for.
            foreach (var test in report.ExpensiveNeverFailed)
                rows.Add(Row($"expensive_never_failed_minutes {Measured}, all recorded runs: {test.Test}", test.Minutes, null, test.Executions, 0));
        }
        rows.Add(Row($"data_range {report.Days[0].Day}..{report.Days[^1].Day}", null, null, 0, 0));
        return rows;
    }

    private static long RunCount(TestEffortDay day) => day.Runs;

    private static DateOnly Parse(TestEffortDay day) => DateOnly.ParseExact(day.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static double? Total(TestEffortDay[] days, Func<TestEffortDay, double> value, Func<TestEffortDay, long> samples) =>
        days.Sum(samples) == 0 ? null : Math.Round(days.Sum(value), 2);

    private static double? Percent(TestEffortDay[] days, Func<TestEffortDay, double> part)
    {
        var whole = days.Sum(day => day.WallMinutes);
        return whole > 0 ? Math.Round(100 * days.Sum(part) / whole, 2) : null;
    }

    private static IReadOnlyList<object?> Row(string key, double? current, double? previous, long currentCount, long previousCount) =>
        [key, current, previous, currentCount, previousCount];
}

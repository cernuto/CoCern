using System.Globalization;

namespace CoCern.Contracts.Dashboard;

/// <summary>
/// The compact test-effort file <c>scripts/Build-TestEffort.py</c> writes and the hub reads (<c>Relay:Analytics:TestEffortFile</c>). It holds
/// numbers and test names only: rule wording is in <see cref="TestEffortRules"/>, so nothing in the file can put a path or free text on a page.
/// </summary>
public sealed record TestEffortReport
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; }
    /// <summary>The one repository this file describes. The hub answers no other repository from it.</summary>
    public string RepositoryId { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
    public int Runs { get; init; }
    public long Cases { get; init; }
    public int RunsWithoutCommit { get; init; }
    public int WholeSuiteRuns { get; init; }
    public double WholeSuiteMinutes { get; init; }
    /// <summary>Each run's recorded start to end, build included. Runs that overlapped each count in full.</summary>
    public double WallMinutes { get; init; }
    /// <summary>Each test's own duration, added across parallel threads, so it can exceed <see cref="WallMinutes"/>.</summary>
    public double CaseMinutes { get; init; }
    /// <summary>Case time spent on a test that had already run at the same commit, in any run, including runs rule b does not count whole.</summary>
    public double RepeatedCaseMinutes { get; init; }
    /// <summary>Rule b alone.</summary>
    public double PointlessMeasuredMinutes { get; init; }
    /// <summary>Runs counted by rule a, b or c, each run once.</summary>
    public double PointlessEstimatedMinutes { get; init; }
    public int Failures { get; init; }
    /// <summary>Rule d: an estimate, and apart from product failures.</summary>
    public int FixtureOnlyFailures { get; init; }
    /// <summary>Failures in runs without a commit, which rule d cannot judge.</summary>
    public int UnjudgedFailures { get; init; }
    public TestEffortRuleResult[] Rules { get; init; } = [];
    /// <summary>UTC days, oldest first; only days with a recorded run or a commit changing tests.</summary>
    public TestEffortDay[] Days { get; init; } = [];
    public TestEffortTest[] ExpensiveNeverFailed { get; init; } = [];

    /// <summary>Whether the file keeps to its shape and limits. Test names may hold only identifier characters, so one cannot carry a path.</summary>
    public bool IsValid() =>
        Schema == CurrentSchema
        && !string.IsNullOrWhiteSpace(RepositoryId) && RepositoryId.Length <= RelayLimits.MaxIdentifierLength && !RepositoryId.Any(char.IsControl)
        && new long[] { Runs, Cases, RunsWithoutCommit, WholeSuiteRuns, Failures, FixtureOnlyFailures, UnjudgedFailures }.All(count => count >= 0)
        && Amounts(WholeSuiteMinutes, WallMinutes, CaseMinutes, RepeatedCaseMinutes, PointlessMeasuredMinutes, PointlessEstimatedMinutes)
        && Rules is { } rules && rules.Select(rule => rule?.Rule).SequenceEqual(TestEffortRules.All.Select(rule => (string?)rule.Rule))
        && rules.All(rule => rule.Runs >= 0 && rule.JudgedRuns >= 0 && (rule.Minutes is null || Amounts(rule.Minutes.Value)))
        && Days is { Length: <= RelayLimits.MaxTestEffortDays } days && days.All(day => day?.IsValid() == true)
        && days.Zip(days.Skip(1)).All(pair => string.CompareOrdinal(pair.First.Day, pair.Second.Day) < 0)
        && ExpensiveNeverFailed is { Length: <= RelayLimits.MaxTestEffortTests } tests && tests.All(test => test?.IsValid() == true);

    internal static bool Amounts(params double[] values) => values.All(value => double.IsFinite(value) && value >= 0);
}

/// <summary>What one rule found. <see cref="Minutes"/> is null for rule d, which counts failures and not time.</summary>
public sealed record TestEffortRuleResult
{
    public string Rule { get; init; } = "";
    public int Runs { get; init; }
    /// <summary>Runs the data let this rule judge. Zero means the rule's number is not available, which is different from a measured zero.</summary>
    public int JudgedRuns { get; init; }
    public double? Minutes { get; init; }
}

public sealed record TestEffortDay
{
    public string Day { get; init; } = "";
    public int Runs { get; init; }
    public int WholeSuiteRuns { get; init; }
    public double WholeSuiteMinutes { get; init; }
    public double WallMinutes { get; init; }
    public double CaseMinutes { get; init; }
    public double RuleAMinutes { get; init; }
    public double RuleBMinutes { get; init; }
    public double RuleCMinutes { get; init; }
    public double PointlessEstimatedMinutes { get; init; }
    public int Failures { get; init; }
    public int FixtureOnlyFailures { get; init; }
    /// <summary>First-parent commits this day that changed a test file.</summary>
    public int Commits { get; init; }
    public int TestsAdded { get; init; }
    public int TestsDeleted { get; init; }
    public int RowsAdded { get; init; }
    public int RowsDeleted { get; init; }

    internal bool IsValid() =>
        DateOnly.TryParseExact(Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 2000
        && new[] { Runs, WholeSuiteRuns, Failures, FixtureOnlyFailures, Commits, TestsAdded, TestsDeleted, RowsAdded, RowsDeleted }.All(count => count >= 0)
        && TestEffortReport.Amounts(WholeSuiteMinutes, WallMinutes, CaseMinutes, RuleAMinutes, RuleBMinutes, RuleCMinutes, PointlessEstimatedMinutes);
}

public sealed record TestEffortTest
{
    public string Test { get; init; } = "";
    public double Minutes { get; init; }
    public int Executions { get; init; }

    internal bool IsValid() =>
        Test is { Length: > 0 and <= RelayLimits.MaxTestEffortTestNameLength } && Test.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '`' or '+' or '-')
        && Executions >= 0 && TestEffortReport.Amounts(Minutes);
}

/// <summary>A rule's fixed wording, and whether its number is measured or estimated.</summary>
public sealed record TestEffortRuleText(string Rule, string Title, string Status, string Note);

/// <summary>The four pointless-run rules. Status lives here beside the wording, so a file cannot relabel an estimate as a measurement.</summary>
public static class TestEffortRules
{
    public const string Measured = "measured";
    public const string Estimate = "estimate";
    public const string NotAvailable = "not available";

    public static IReadOnlyList<TestEffortRuleText> All { get; } =
    [
        new("a", "No covering test for the changed symbols", Estimate, "A filtered run counts when none of its test class files names a production file its commit changed. This is name matching, not coverage: a test that reaches the code indirectly is missed, so it can overcount."),
        new("b", "Rerun with no code change", Measured, "Every case in the run had already run at the same recorded commit. Uncommitted edits are not recorded, and a rerun to diagnose a failure counts too."),
        new("c", "Whole-suite run with nothing under test changed", Estimate, "An unfiltered run counts when no file under src or tests differs from the previous unfiltered run's commit. Uncommitted edits are not recorded. A whole-suite run that is only partly unrelated is not counted, because that needs coverage data."),
        new("d", "Fixture-only failures", Estimate, "Counted as failures, apart from product failures, and not in the pointless minutes. Results carry no failure text, so a failed case counts when it passed at the same commit, or at a later commit with no change under src.")
    ];

    public static TestEffortRuleText Text(string rule) => All.First(text => text.Rule == rule);

    /// <summary>The rule's status for this data: a rule that could judge no run has no number to show.</summary>
    public static string Status(TestEffortRuleResult result) => result.JudgedRuns == 0 ? NotAvailable : Text(result.Rule).Status;
}

/// <summary>The dashboard's read of the test-effort file. <see cref="Report"/> can be set with no runs: tests added and deleted come from git alone.</summary>
public sealed class DashboardTestEffort
{
    public const string Ready = "ready";
    public const string NoRunData = "no-run-data";
    public const string Unreadable = "unreadable";
    public const string Unavailable = "unavailable";

    public string RepositoryId { get; set; } = "";
    public string Status { get; set; } = Ready;
    public string? Message { get; set; }
    public TestEffortReport? Report { get; set; }
}

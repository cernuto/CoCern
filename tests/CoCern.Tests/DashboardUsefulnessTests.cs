using CoCern.Dashboard.Services;
using Xunit;

public class DashboardUsefulnessTests
{
    [Fact]
    public void Ranking_filters_before_top_ten_and_retains_previous_only_rows()
    {
        var rows = Enumerable.Range(1, 12).Select(index => (Key: $"tool{index:00} [background]", Bytes: (double?)index))
            .Append(("model [model-facing]", 100)).Append(("older [background]", null)).ToArray();
        var ranked = DashboardUsefulness.RankTools(rows, row => row.Key, row => row.Bytes, "background");
        Assert.Equal(13, ranked.Length);
        Assert.Equal("tool12 [background]", ranked.Take(10).First().Key);
        Assert.Equal("tool03 [background]", ranked.Take(10).Last().Key);
        Assert.Equal("older [background]", ranked.Last().Key);
        Assert.Single(DashboardUsefulness.RankTools(rows, row => row.Key, row => row.Bytes, "model"));
        Assert.Empty(DashboardUsefulness.RankTools(rows, row => row.Key, row => row.Bytes, "unknown"));
    }

    [Theory]
    [InlineData("task_get [model-facing]", "model")]
    [InlineData("task_get [background]", "background")]
    [InlineData("task_get [unknown]", "unknown")]
    [InlineData("unmarked", "unknown")]
    [InlineData("background_tool [unknown]", "unknown")]
    public void Origin_uses_only_explicit_suffix(string key, string origin) => Assert.Equal(origin, DashboardUsefulness.Origin(key));

    [Theory]
    [InlineData("Information", "HTTP", "Relay request completed (HTTP 200).", true)]
    [InlineData("Information", "HTTP", "Relay request completed (HTTP 302).", true)]
    [InlineData("Warning", "HTTP", "Relay request completed (HTTP 200).", false)]
    [InlineData("Information", "HTTP", "Relay request completed (HTTP 400).", false)]
    [InlineData("Information", "HTTP", "Relay request completed (HTTP 500).", false)]
    [InlineData("Information", "Other", "Relay request completed (HTTP 200).", false)]
    [InlineData("Information", "HTTP", "An important HTTP event", false)]
    public void Routine_filter_preserves_failures_and_unrecognized_events(string level, string category, string message, bool routine) =>
        Assert.Equal(routine, DashboardUsefulness.IsRoutineHttp(level, category, message));

    [Theory]
    [InlineData(" TASK_GET ")]
    [InlineData("my-repo")]
    [InlineData("SESSION-42")]
    [InlineData("ToolActivity")]
    public void Search_matches_available_metadata_case_insensitively(string search) =>
        Assert.True(DashboardUsefulness.MatchesLog("Warning", "ToolActivity", "task_get my-repo refused", "session-42", "problems", search, true));

    [Fact]
    public void Search_level_and_routine_filters_combine()
    {
        Assert.False(DashboardUsefulness.MatchesLog("Information", "HTTP", "Relay request completed (HTTP 200).", null, "", "", true));
        Assert.True(DashboardUsefulness.MatchesLog("Information", "HTTP", "Relay request completed (HTTP 200).", null, "", "", false));
        Assert.False(DashboardUsefulness.MatchesLog("Information", "Host", "Started", null, "problems", "", false));
        Assert.False(DashboardUsefulness.MatchesLog("Error", "HTTP", "Failed", null, "Warning", "", false));
        Assert.False(DashboardUsefulness.MatchesLog("Error", "HTTP", "Failed", null, "", "missing", false));
        Assert.True(DashboardUsefulness.MatchesLog("Critical", "Host", "Stopped", null, "problems", "", true));
    }

    [Fact]
    public void Snapshot_age_is_explicit_about_render_time_and_clock_skew()
    {
        var now = DateTimeOffset.Parse("2026-11-03T22:00:00Z");
        Assert.Contains("minute", DashboardUsefulness.SnapshotAge(now, now));
        Assert.Equal("12 minutes old at this render.", DashboardUsefulness.SnapshotAge(now.AddMinutes(-12), now));
        Assert.Equal("2.5 hours old at this render.", DashboardUsefulness.SnapshotAge(now.AddMinutes(-150), now));
        Assert.Contains("clock", DashboardUsefulness.SnapshotAge(now.AddMinutes(1), now));
    }
}

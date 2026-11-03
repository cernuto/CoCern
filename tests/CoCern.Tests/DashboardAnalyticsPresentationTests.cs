using System.Globalization;
using System.Reflection;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardAnalyticsPresentationTests
{
    private static object Invoke(string method, params object?[] arguments)
    {
        var type = typeof(DashboardAnalyticsPresentationTests).Assembly.GetType("CoCern.Dashboard.Services.AnalyticsPresentation");
        Assert.NotNull(type);
        var member = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(member);
        return member.Invoke(null, arguments)!;
    }

    [Fact]
    public void Unknown_and_nonfinite_values_stay_distinct_from_measured_zero()
    {
        Assert.Equal("Not measured", Invoke("Number", null, "bytes"));
        Assert.Equal("Not measured", Invoke("Number", double.NaN, "bytes"));
        Assert.Equal("Not measured", Invoke("Number", double.PositiveInfinity, "bytes"));
        Assert.Equal("0 bytes", Invoke("Number", 0d, "bytes"));
        Assert.Equal("12.5 minutes", Invoke("Number", 12.5d, "minutes"));
    }

    [Theory]
    [InlineData(null, 100d, "0")]
    [InlineData(0d, 0d, "0")]
    [InlineData(-1d, 100d, "0")]
    [InlineData(50d, 100d, "50")]
    [InlineData(200d, 100d, "100")]
    [InlineData(double.NaN, 100d, "0")]
    [InlineData(double.PositiveInfinity, 100d, "0")]
    [InlineData(double.MaxValue, double.MaxValue, "100")]
    public void Svg_widths_are_finite_bounded_and_culture_independent(double? value, double maximum, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, Invoke("Width", value, maximum));
            Assert.Equal("12.5", Invoke("Width", 1.25d, 10d));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Accessible_comparison_names_include_periods_units_and_sample_counts()
    {
        var summary = Assert.IsType<string>(Invoke("ComparisonLabel", "Worker", null, 0d, 0L, 3L, "bytes/session"));
        Assert.Contains("Worker", summary);
        Assert.Contains("Current: Not measured", summary);
        Assert.Contains("Previous: 0 bytes/session", summary);
        Assert.Contains("0 samples", summary);
        Assert.Contains("3 samples", summary);
    }
}

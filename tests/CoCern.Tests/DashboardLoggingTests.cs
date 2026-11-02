using System.Text.Json;
using CoCern.Host.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardLoggingTests
{
    [Fact]
    public async Task Host_startup_reaches_dashboard_log_without_relay_requests()
    {
        await using var host = new RelayHostFixture();
        var entries = host.Services.GetRequiredService<DashboardLogBuffer>().Snapshot();

        Assert.Contains(entries, entry => entry.Category == "Host" && entry.Level == "Information" && entry.Message == "Relay host started.");
    }

    [Fact]
    public async Task Actual_relay_http_completion_reaches_log_without_observer_feedback_or_private_query()
    {
        await using var host = new RelayHostFixture();
        using var client = host.CreateClient();
        var logs = host.Services.GetRequiredService<DashboardLogBuffer>();
        var previousSequence = logs.Snapshot().LastOrDefault()?.Sequence ?? 0;

        (await client.GetAsync("/health?token=PRIVATE-TOKEN")).EnsureSuccessStatusCode();
        var entry = Assert.Single(logs.Snapshot().Where(entry => entry.Sequence > previousSequence));
        Assert.Equal("HTTP", entry.Category);
        Assert.Equal("Relay request completed (HTTP 200).", entry.Message);
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(entry));

        using var response = await client.GetAsync("/dashboard/logs");
        response.EnsureSuccessStatusCode();
        Assert.Equal(entry.Sequence, logs.Snapshot().Last().Sequence);
    }

    [Theory]
    [InlineData("Application started. Press Ctrl+C to shut down.", "Relay host started.")]
    [InlineData("Application is shutting down...", "Relay host is stopping.")]
    public void Lifetime_events_project_only_fixed_safe_text(string template, string expected)
    {
        var logs = new DashboardLogBuffer();
        var timestamp = DateTimeOffset.UtcNow;
        var parser = new MessageTemplateParser();
        logs.Emit(new LogEvent(timestamp, LogEventLevel.Information, new Exception("PRIVATE-EXCEPTION"), parser.Parse(template),
            [new("SourceContext", new ScalarValue("Microsoft.Hosting.Lifetime")), new("Token", new ScalarValue("PRIVATE-TOKEN"))]));

        var entry = Assert.Single(logs.Snapshot());
        Assert.Equal(expected, entry.Message);
        Assert.Equal("Host", entry.Category);
        Assert.Equal(timestamp, entry.Timestamp);
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(entry));
    }

    [Theory]
    [InlineData("Other.Category", "Application started. Press Ctrl+C to shut down.")]
    [InlineData("Microsoft.Hosting.Lifetime", "Now listening on: {address}")]
    [InlineData("Microsoft.Hosting.Lifetime", "PRIVATE-ARBITRARY-MESSAGE")]
    public void Lifetime_projection_rejects_other_categories_and_dynamic_templates(string category, string template)
    {
        var logs = new DashboardLogBuffer();
        logs.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse(template),
            [new("SourceContext", new ScalarValue(category)), new("address", new ScalarValue("PRIVATE-ADDRESS"))]));

        Assert.Empty(logs.Snapshot());
    }
}

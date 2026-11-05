using System.Globalization;
using System.Net.Http.Json;
using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

public static class TestEffortPresentation
{
    public const string NoRunData = "No run data yet.";
    public const string LoadProblem = "Test effort could not be loaded. Check the hub connection.";

    public static async Task<DashboardTestEffort?> LoadAsync(HttpClient http, string repositoryId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/analytics/test-effort?repositoryId={Uri.EscapeDataString(repositoryId)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<DashboardTestEffort>(cancellationToken);
        return data?.RepositoryId == repositoryId ? data : throw new HttpRequestException("The response did not match the selected repository.");
    }

    public static bool IsLoadFailure(Exception exception) => exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException;

    public static string Minutes(double value) => $"{value.ToString("#,0.#", CultureInfo.InvariantCulture)} min";

    public static string Percent(double part, double whole) => whole > 0 ? (100 * part / whole).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "Not measured";

    public static string BadgeClass(string status) => status switch
    {
        TestEffortRules.Measured => "measured",
        TestEffortRules.Estimate => "estimate",
        _ => "not-available"
    };

    /// <summary>A rule's minutes, or why there are none. A rule that judged no run shows no number, not zero.</summary>
    public static string RuleMinutes(TestEffortRuleResult rule) =>
        TestEffortRules.Status(rule) == TestEffortRules.NotAvailable ? "Not available"
        : rule.Minutes is { } minutes ? Minutes(minutes)
        : "Counts failures";

    public static IEnumerable<string> Repositories(DashboardSnapshot snapshot, string selected) => snapshot.Sessions.Select(session => session.RepositoryId)
        .Concat(snapshot.DocCache.Grains.Select(grain => grain.Repository)).Append(selected)
        .Where(repository => !string.IsNullOrWhiteSpace(repository)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
}

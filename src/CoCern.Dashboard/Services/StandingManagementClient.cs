using System.Net.Http.Json;
using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

public static class StandingManagementClient
{
    public static async Task<DashboardStandingDecisionResult> DecideAsync(HttpClient http, DashboardStandingDecision decision)
    {
        using var response = await http.PostAsJsonAsync("api/standing/decide", decision);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return new(false, "Sign in again to manage Standing.", 0);
        return await response.Content.ReadFromJsonAsync<DashboardStandingDecisionResult>()
            ?? new(false, "No decision result was received.", 0);
    }
}

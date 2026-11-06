using System.Net.Http.Json;
using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

public static class SessionToolSettingsClient
{
    public static async Task<DashboardSessionToolSettingsResult?> ReadAsync(HttpClient http, string repositoryId, string sessionId)
    {
        using var response = await http.GetAsync($"api/sessions/tool-settings?repositoryId={Uri.EscapeDataString(repositoryId)}&sessionId={Uri.EscapeDataString(sessionId)}");
        return await response.Content.ReadFromJsonAsync<DashboardSessionToolSettingsResult>();
    }

    public static async Task<DashboardSessionToolSettingsResult?> SaveAsync(HttpClient http, DashboardSessionToolSettingsRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/sessions/tool-settings", request);
        return await response.Content.ReadFromJsonAsync<DashboardSessionToolSettingsResult>();
    }
}

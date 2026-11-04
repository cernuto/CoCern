using System.Net;
using System.Net.Http.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

public static class ManagerManagementClient
{
    public static async Task<DashboardManagerResult> ReadAsync(HttpClient http, string repositoryId, long? historyBefore, CancellationToken cancellationToken = default)
    {
        var route = "api/manager?repositoryId=" + Uri.EscapeDataString(repositoryId);
        if (historyBefore is { } before) route += "&managerHistoryBefore=" + before.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var response = await http.GetAsync(route, cancellationToken);
        return await ResultAsync(response, cancellationToken);
    }

    public static async Task<DashboardManagerResult> DecideAsync(HttpClient http, DashboardManagerDecision decision, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/manager/decide", decision, cancellationToken);
        var result = await ResultAsync(response, cancellationToken);
        if (result.Code == RelayErrorCodes.VersionConflict && result.Manager is null)
        {
            var current = await ReadAsync(http, decision.RepositoryId, null, cancellationToken);
            result = result with { Manager = current.Manager, Paused = current.Paused };
        }
        return result;
    }

    public static async Task<DashboardManagerResult> DispatchAsync(HttpClient http, DashboardDispatchDecision decision, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/dispatch", decision, cancellationToken);
        return await ResultAsync(response, cancellationToken);
    }

    private static async Task<DashboardManagerResult> ResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized
            || response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentType?.MediaType != "application/json")
            return new(false, "Sign in again to manage this hub.", null, null);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            return new(false, RelayErrorCodes.PayloadTooLarge, null, null);
        return await response.Content.ReadFromJsonAsync<DashboardManagerResult>(cancellationToken)
            ?? throw new HttpRequestException("No manager result was received.");
    }
}

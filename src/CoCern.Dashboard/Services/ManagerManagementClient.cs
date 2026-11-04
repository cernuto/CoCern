using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
            // The POST was definitively rejected. An unavailable read must never turn it into an uncertain write.
            try
            {
                var current = await ReadAsync(http, decision.RepositoryId, null, cancellationToken);
                if (current.Ok) result = result with { Manager = current.Manager, Paused = current.Paused };
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
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
            || response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentType?.MediaType is null or "text/html")
            return new(false, "Sign in again to manage this hub.", null, null);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && response.Content.Headers.ContentLength == 0)
            return new(false, RelayErrorCodes.PayloadTooLarge, null, null);
        if ((int)response.StatusCode >= 500 || (int)response.StatusCode < 200 || (int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("No definitive manager response was received.");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("ok", out var ok)
            || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !body.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(code.GetString()) || ok.GetBoolean() != response.IsSuccessStatusCode
            || ok.GetBoolean() != (code.GetString() == "Ok")
            || body.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != body.EnumerateObject().Count())
            throw new HttpRequestException("No definitive manager response was received.");
        var result = body.Deserialize<DashboardManagerResult>(JsonSerializerOptions.Web)!;
        var expected = result.Ok ? HttpStatusCode.OK : RelayErrorCodes.Classify(result.Code) switch
        {
            RelayErrorKind.Denied => HttpStatusCode.Forbidden,
            RelayErrorKind.Missing => HttpStatusCode.NotFound,
            RelayErrorKind.TooLarge => HttpStatusCode.RequestEntityTooLarge,
            RelayErrorKind.Conflict => HttpStatusCode.Conflict,
            _ => HttpStatusCode.BadRequest
        };
        if (response.StatusCode != expected || result.Ok && result.Manager is null && result.Paused is null
            || result.Manager is { History: null })
            throw new HttpRequestException("No definitive manager response was received.");
        return result;
    }
}

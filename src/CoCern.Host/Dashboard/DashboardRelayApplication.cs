using System.Diagnostics;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host.Dashboard;

public sealed class DashboardRelayApplication(RelayApplication application, DashboardTrafficBuffer traffic, DashboardLogBuffer logs) : IRelayApplication
{
    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!RelayToolCatalog.TryGet(tool, out _)) return await application.ExecuteAsync(caller, tool, arguments, cancellationToken);
        var sessionId = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
        var correlationId = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        traffic.Request(tool, sessionId, correlationId);
        try
        {
            var result = await application.ExecuteAsync(caller, tool, arguments, cancellationToken);
            traffic.Response(tool, sessionId, correlationId, result.Code, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (!result.Ok) logs.ToolFailure(tool, result.Code, FailureField(result));
            return result;
        }
        catch (Exception)
        {
            traffic.Response(tool, sessionId, correlationId, cancellationToken.IsCancellationRequested ? "Canceled" : "Unavailable", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (!cancellationToken.IsCancellationRequested) logs.ToolFailure(tool, RelayErrorCodes.Unavailable);
            throw;
        }
    }

    private static string? FailureField(RelayReply reply)
    {
        try
        {
            using var data = JsonDocument.Parse(reply.DataJson);
            return data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty("field", out var field)
                && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        }
        catch (Exception) { return null; } // Observation never changes the domain reply, including when metadata is malformed.
    }
}

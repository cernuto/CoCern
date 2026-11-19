using System.Diagnostics;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host.Dashboard;

public sealed class DashboardRelayApplication(RelayApplication application, DashboardTrafficBuffer traffic, DashboardLogBuffer logs, DashboardConnections connections) : IRelayApplication
{
    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!RelayToolCatalog.TryGet(tool, out _)) return await application.ExecuteAsync(caller, tool, arguments, cancellationToken);
        var sessionId = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
        var correlationId = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        traffic.Request(tool, sessionId, correlationId, caller.PrincipalId);
        try
        {
            var result = await application.ExecuteAsync(caller, tool, arguments, cancellationToken);
            traffic.Response(tool, sessionId, correlationId, result.Code, Stopwatch.GetElapsedTime(started).TotalMilliseconds, caller.PrincipalId);
            if (result.Ok && tool == RelayTools.SendMessage && connections.HasViewers
                && Guid.TryParseExact(sessionId, "N", out _)
                && (!arguments.TryGetProperty("taskId", out var task) || task.ValueKind == JsonValueKind.Null
                    || (task.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(task.GetString())))
                && arguments.TryGetProperty("recipientSessionId", out var recipient) && recipient.ValueKind == JsonValueKind.String
                && recipient.GetString() is { } target && Guid.TryParseExact(target, "N", out _))
                connections.PublishSend(new(sessionId!, target, DateTimeOffset.UtcNow));
            if (!result.Ok) logs.ToolFailure(tool, result.Code, FailureField(result));
            return result;
        }
        catch (Exception)
        {
            traffic.Response(tool, sessionId, correlationId, cancellationToken.IsCancellationRequested ? "Canceled" : "Unavailable", Stopwatch.GetElapsedTime(started).TotalMilliseconds, caller.PrincipalId);
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

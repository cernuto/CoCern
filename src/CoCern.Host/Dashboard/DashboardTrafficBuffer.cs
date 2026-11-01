using System.Diagnostics;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host.Dashboard;

/// <summary>Bounded operational metadata only. No arguments, result bodies or exception text are retained.</summary>
public sealed class DashboardTrafficBuffer
{
    private readonly object gate = new();
    private readonly Queue<DashboardTrafficEvent> events = new();
    private long sequence;

    public void Request(string name, string? sessionId, string correlationId) => Add(new() { Kind = "request", Name = name, SessionId = SafeSessionId(sessionId), CorrelationId = correlationId });
    public void Response(string name, string? sessionId, string correlationId, string code, double duration) => Add(new() { Kind = "response", Name = name, SessionId = SafeSessionId(sessionId), CorrelationId = correlationId, Code = SafeCode(code), DurationMilliseconds = Math.Max(0, duration) });
    public void Push(string sessionId) => Add(new() { Kind = "push", Name = "coordination.changed", SessionId = SafeSessionId(sessionId) });

    private void Add(DashboardTrafficEvent value)
    {
        if (value.Kind != "push" && !RelayToolCatalog.TryGet(value.Name, out _)) return;
        lock (gate)
        {
            events.Enqueue(value with { Sequence = ++sequence, Timestamp = DateTimeOffset.UtcNow });
            while (events.Count > 120) events.Dequeue();
        }
    }

    public DashboardTrafficEvent[] Snapshot() { lock (gate) return events.ToArray(); }
    private static string? SafeSessionId(string? value) => Guid.TryParseExact(value, "N", out _) ? value : null;
    private static string? SafeCode(string value) => value.Length is > 0 and <= 64 && value.All(char.IsAsciiLetterOrDigit) ? value : null;
}

public sealed class DashboardRelayApplication(RelayApplication application, DashboardTrafficBuffer traffic) : IRelayApplication
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
            return result;
        }
        catch (Exception)
        {
            traffic.Response(tool, sessionId, correlationId, cancellationToken.IsCancellationRequested ? "Canceled" : "Unavailable", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}

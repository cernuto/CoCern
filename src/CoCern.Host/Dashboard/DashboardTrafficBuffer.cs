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

    public void Request(string name, string? sessionId, string correlationId, string? principalId = null) => Add(new() { Kind = "request", Name = name, SessionId = SafeSessionId(sessionId), PrincipalId = SafePrincipalId(principalId), CorrelationId = correlationId });
    public void Response(string name, string? sessionId, string correlationId, string code, double duration, string? principalId = null) => Add(new() { Kind = "response", Name = name, SessionId = SafeSessionId(sessionId), PrincipalId = SafePrincipalId(principalId), CorrelationId = correlationId, Code = SafeCode(code), DurationMilliseconds = Math.Max(0, duration) });
    public void Push(string sessionId) => Add(new() { Kind = "push", Name = HubEvents.CoordinationChanged, SessionId = SafeSessionId(sessionId) });

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
    private static string? SafePrincipalId(string? value)
    {
        if (value is null) return null;
        var safe = DashboardText.Safe(value).Trim();
        return safe.Length == 0 ? null : safe;
    }
    private static string? SafeCode(string value) => value.Length is > 0 and <= 64 && value.All(char.IsAsciiLetterOrDigit) ? value : null;
}

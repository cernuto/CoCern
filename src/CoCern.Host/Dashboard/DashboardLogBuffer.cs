using CoCern.Contracts.Dashboard;
using Serilog.Core;
using Serilog.Events;

namespace CoCern.Host.Dashboard;

/// <summary>Only fixed templates enter the observer. Formatted log text and exceptions are never read.</summary>
public sealed class DashboardLogBuffer : ILogEventSink
{
    private readonly object gate = new();
    private readonly Queue<DashboardLogEntry> entries = new();
    private long sequence;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source is ScalarValue { Value: "Microsoft.Hosting.Lifetime" })
        {
            var message = logEvent.MessageTemplate.Text switch
            {
                "Application started. Press Ctrl+C to shut down." => "Relay host started.",
                "Application is shutting down..." => "Relay host is stopping.",
                _ => null
            };
            if (message is not null) Add(logEvent.Timestamp, "Information", "Host", message);
            return;
        }
        if (logEvent.MessageTemplate.Text != "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms") return;
        if (!logEvent.Properties.TryGetValue("RequestPath", out var path) || path is not ScalarValue { Value: string requestPath }
            || requestPath.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase)) return;
        // Never retain a path, query, method, body, exception or arbitrary application field.
        if (!logEvent.Properties.TryGetValue("StatusCode", out var status) || status is not ScalarValue { Value: int code } || code is < 100 or > 599) return;
        Add(logEvent.Timestamp, code >= 500 ? "Error" : code >= 400 ? "Warning" : "Information", "HTTP", $"Relay request completed (HTTP {code}).");
    }

    public void CollectionFailed() => Add(DateTimeOffset.UtcNow, "Warning", "Observer", "Observation collection was unavailable; the previous observation is retained.");

    private void Add(DateTimeOffset timestamp, string level, string category, string message)
    {
        lock (gate)
        {
            entries.Enqueue(new() { Sequence = ++sequence, Timestamp = timestamp, Level = level, Category = category, Message = message });
            while (entries.Count > 200) entries.Dequeue();
        }
    }

    public DashboardLogEntry[] Snapshot() { lock (gate) return entries.ToArray(); }
}

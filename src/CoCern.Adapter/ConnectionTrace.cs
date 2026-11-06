using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter;

/// <summary>Optional diagnostics, separate from business arguments and all native/session authority.</summary>
public static class ConnectionTrace
{
    public const string Header = "X-CoCern-Trace";
    public const string HubTimingHeader = "X-CoCern-Hub-Ms";
    internal const string MetaKey = "io.cocern/connectionTrace";
    internal const string MetaEventsKey = "io.cocern/connectionPhases";
    private static readonly AsyncLocal<Scope?> current = new();
    private static readonly AsyncLocal<ConnectionTraceStore?> capture = new();
    private static ConnectionTraceStore? processStore;
    private static int exitRegistered;
    internal static bool ReportingEnabled => processStore?.DirectoryPath is not null;
    internal static string? CurrentId => current.Value?.CorrelationId;

    /// <summary>Call only from the real executable entry point. Library/test calls remain memory-only unless given a sink.</summary>
    public static void EnableForProcess()
    {
        var configured = Environment.GetEnvironmentVariable("COCERN_CONNECTION_DIAGNOSTICS_DIR");
        if (string.Equals(configured, "off", StringComparison.OrdinalIgnoreCase)) return;
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--check", StringComparer.Ordinal))
        {
            processStore = new();
            return;
        }
        string? supplied = null;
        foreach (var option in new[] { "--connection", "--data-dir", "--dev-launch" })
        {
            var index = Array.IndexOf(arguments, option);
            if (index >= 0 && index + 1 < arguments.Length && Path.IsPathFullyQualified(arguments[index + 1]))
            { supplied = arguments[index + 1]; break; }
        }
        var tokenFile = Environment.GetEnvironmentVariable("COCERN_TOKEN_FILE");
        var tokenIndex = Array.IndexOf(arguments, "--token-file");
        if (tokenIndex >= 0 && tokenIndex + 1 < arguments.Length) tokenFile = arguments[tokenIndex + 1];
        if (supplied is null && tokenFile is { Length: > 0 } && Path.IsPathFullyQualified(tokenFile)) supplied = Path.GetDirectoryName(tokenFile);
        if (supplied is null && Environment.GetEnvironmentVariable("COCERN_NODE_DIR") is { Length: > 0 } node && Path.IsPathFullyQualified(node)) supplied = node;
        // Explicit in-memory/raw credentials are not permission to discover and write another profile's private store.
        // This also keeps executable fixtures confined to their supplied temporary connection/data directories.
        if (supplied is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COCERN_TOKEN")) &&
            CoCern.Adapter.Node.NodeState.HasIdentityFiles(CoCern.Adapter.Node.NodeState.DefaultFolder))
            supplied = CoCern.Adapter.Node.NodeState.DefaultFolder;
        processStore = new(configured ?? (supplied is null ? null : Path.Combine(supplied, "connection-diagnostics")));
        if (Interlocked.Exchange(ref exitRegistered, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { processStore?.Dispose(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        };
    }

    internal static IDisposable Capture(ConnectionTraceStore sink)
    {
        var previous = capture.Value;
        capture.Value = sink;
        return new Restore(() => capture.Value = previous);
    }

    internal static Scope Begin(string tool, string source, JsonObject? metadata = null, string totalStage = "total")
    {
        var parent = current.Value;
        var id = parent?.CorrelationId;
        if (id is null && metadata?[MetaKey] is JsonValue value && value.TryGetValue<string>(out var incoming) && ConnectionTraceStore.ValidId(incoming)) id = incoming;
        var scope = new Scope(id ?? "cc-" + Guid.NewGuid().ToString("N"), tool is { Length: > 0 and <= 64 } && RelayToolCatalog.TryGet(tool, out _) ? tool : "unknown", source,
            parent, capture.Value ?? processStore, totalStage);
        current.Value = scope;
        if (metadata?[MetaEventsKey] is JsonArray rows && rows.Count <= 24)
            foreach (var row in rows)
            {
                try
                {
                    if (row?.Deserialize<ConnectionDiagnosticEvent>() is { Source: "launcher" } item && item.CorrelationId == scope.CorrelationId &&
                        item.Stage is "queue" or "drain" or "backend_start" && ConnectionTraceStore.Valid(item)) scope.Import(item);
                }
                catch (JsonException) { }
            }
        return scope;
    }

    internal static JsonObject Metadata(JsonObject? original)
    {
        var result = original?.DeepClone().AsObject() ?? new();
        if (current.Value is not { } scope) return result;
        result[MetaKey] = scope.CorrelationId;
        result[MetaEventsKey] = JsonSerializer.SerializeToNode(scope.Events.Where(x => x.Source == "launcher").TakeLast(24).ToArray());
        return result;
    }

    internal static void Stage(string stage, long started, string outcome = "ok", string? code = null) =>
        current.Value?.Record(stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome, code);
    internal static void Duration(string stage, double milliseconds) => current.Value?.Record(stage, milliseconds);

    internal sealed class Scope(string id, string tool, string source, Scope? parent, ConnectionTraceStore? sink, string totalStage) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset at = DateTimeOffset.UtcNow;
        private readonly List<ConnectionDiagnosticEvent> events = [];
        private bool disposed;
        private string outcome = "ok";
        private string? code;
        internal string CorrelationId => id;
        internal bool IsRoot => parent is null;
        internal IReadOnlyList<ConnectionDiagnosticEvent> Events { get { lock (events) return events.ToArray(); } }
        internal void Complete(bool success, string? errorCode = null)
        {
            outcome = success ? "ok" : errorCode == "cancelled" ? "cancelled" : "error";
            code = SafeCode(errorCode);
        }
        internal void Record(string stage, double duration, string result = "ok", string? errorCode = null)
        {
            var item = new ConnectionDiagnosticEvent { CorrelationId = id, Tool = tool, Source = source, Stage = stage, StartedAt = at,
                DurationMs = Math.Round(duration, 3), Outcome = result, ErrorCode = SafeCode(errorCode) };
            if (ConnectionTraceStore.Valid(item)) Import(item);
        }
        internal void Import(ConnectionDiagnosticEvent item)
        {
            lock (events) { if (events.Count < 64) events.Add(item); }
            sink?.Record(item);
            parent?.AcceptChild(item);
        }
        private void AcceptChild(ConnectionDiagnosticEvent item) { lock (events) { if (events.Count < 64) events.Add(item); } parent?.AcceptChild(item); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Record(totalStage, Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome, code);
            current.Value = parent;
        }
    }

    internal static string? SafeCode(string? code) => code switch
    {
        null => null,
        "hub_timeout" or "hub_unavailable" or "hub_identity_changed" or "unauthorized" or "forbidden" or "rate_limited" or "hub_error" or "redirect_rejected" or "request_too_large" or "response_too_large" or "invalid_arguments" or "invalid_hub_reply" or "cancelled" or "backend_unavailable" or "native_registration_unverified" => code,
        _ => "tool_error"
    };
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}

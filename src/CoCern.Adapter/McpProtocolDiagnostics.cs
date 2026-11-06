using System.Diagnostics;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter;

/// <summary>Projects SDK protocol spans into the bounded connection log, without exporting SDK tags or exception text.</summary>
public sealed class McpProtocolDiagnostics : IDisposable
{
    internal const string SourceName = "Experimental.ModelContextProtocol";
    private const string StateKey = "CoCern.ProtocolDiagnostic";
    private readonly ActivityListener listener;
    private readonly Action<ConnectionDiagnosticEvent> record;
    private readonly string source;
    private readonly Func<bool>? shouldObserve;
    private int disposed;

    public McpProtocolDiagnostics(Action<ConnectionDiagnosticEvent> record, string source, Func<bool>? shouldObserve = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (source is not ("adapter" or "launcher" or "hub")) throw new ArgumentOutOfRangeException(nameof(source));
        this.record = record;
        this.source = source;
        this.shouldObserve = shouldObserve;
        listener = new()
        {
            ShouldListenTo = activitySource => activitySource.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => Observe(options.Name) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => Observe(options.Name) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = Started,
            ActivityStopped = Stopped
        };
        ActivitySource.AddActivityListener(listener);
    }

    private bool Observe(string name) => Volatile.Read(ref disposed) == 0 && Stage(name) is not null && (shouldObserve?.Invoke() ?? true);

    private void Started(Activity activity)
    {
        if (!Observe(activity.OperationName) || activity.GetCustomProperty(StateKey) is not null) return;
        activity.SetCustomProperty(StateKey, new State(this, "cc-" + activity.TraceId.ToHexString(), Stage(activity.OperationName)!));
    }

    private void Stopped(Activity activity)
    {
        if (Volatile.Read(ref disposed) != 0 || activity.GetCustomProperty(StateKey) is not State state || state.Owner != this) return;
        var cancelled = activity.GetTagItem("error.type") is "System.OperationCanceledException" or "System.Threading.Tasks.TaskCanceledException";
        var failed = cancelled || activity.Status == ActivityStatusCode.Error;
        var tool = activity.GetTagItem("gen_ai.tool.name") is string name && RelayToolCatalog.TryGet(name, out _) ? name : "unknown";
        var item = new ConnectionDiagnosticEvent
        {
            CorrelationId = Volatile.Read(ref state.CorrelationId), Tool = tool, Source = source, Stage = state.Stage,
            StartedAt = new DateTimeOffset(activity.StartTimeUtc), DurationMs = Math.Round(activity.Duration.TotalMilliseconds, 3),
            Outcome = cancelled ? "cancelled" : failed ? "error" : "ok",
            ErrorCode = cancelled ? "cancelled" : failed ? state.Stage == "mcp_call_tool" ? "tool_error" : "protocol_error" : null
        };
        // In particular, never copy OperationName, StatusDescription, request IDs, resource URIs, arguments or arbitrary tags.
        if (ConnectionTraceStore.Valid(item)) record(item);
    }

    /// <summary>Connects a protocol span to an existing diagnostic ID; this metadata conveys no session or caller authority.</summary>
    public static void Bind(JsonObject? metadata)
    {
        if (metadata?[ConnectionTrace.MetaKey] is JsonValue value && value.TryGetValue<string>(out var id) && ConnectionTraceStore.ValidId(id)) Bind(id);
    }

    internal static string? CurrentId => CurrentState()?.CorrelationId;

    internal static void Bind(string id)
    {
        if (!ConnectionTraceStore.ValidId(id)) return;
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            if (activity.GetCustomProperty(StateKey) is State state) Interlocked.Exchange(ref state.CorrelationId, id);
    }

    private static State? CurrentState()
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            if (activity.GetCustomProperty(StateKey) is State state) return state;
        return null;
    }

    private static string? Stage(string operation)
    {
        var separator = operation.IndexOf(' ');
        return (separator < 0 ? operation.AsSpan() : operation.AsSpan(0, separator)) switch
        {
            "initialize" => "mcp_initialize", "tools/list" => "mcp_list_tools", "tools/call" => "mcp_call_tool",
            "resources/list" => "mcp_list_resources", "resources/templates/list" => "mcp_list_resource_templates",
            "resources/read" => "mcp_read_resource", "prompts/list" => "mcp_list_prompts", "prompts/get" => "mcp_get_prompt", "ping" => "mcp_ping",
            _ => null
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) listener.Dispose();
    }

    private sealed class State(McpProtocolDiagnostics owner, string correlationId, string stage)
    {
        internal McpProtocolDiagnostics Owner { get; } = owner;
        internal string CorrelationId = correlationId;
        internal string Stage { get; } = stage;
    }
}

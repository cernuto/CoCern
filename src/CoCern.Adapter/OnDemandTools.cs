using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>
/// The two adapter-local tools behind which an adapter started with on-demand tools (<see cref="AdapterOptions.OnDemand"/>) keeps the rarely used
/// ones (<see cref="RelayTool.OnDemand"/>). Neither is a catalog tool and the hub never sees either name: <c>tool_describe</c> answers here, and
/// <c>tool_call</c> enters the chat's ordinary call path under the target's own name, so the hub sees, authorizes, logs and counts the real tool.
/// Both refuse any name outside <c>available</c>, the on-demand tools the current profile and ceiling allow, before calling anything.
/// </summary>
internal static class OnDemandTools
{
    internal const string Describe = "tool_describe";
    internal const string Call = "tool_call";

    private const string DescribeText = "Rarely used CoCern tools are not listed: docs, retained context, standing, orders, setup, analytics and diagnostics. Call with no name to list the ones this chat may use, or with name for one tool's arguments.";
    private const string CallText = "Run a tool found with tool_describe: name and its arguments. Listed tools are called directly, not through this.";

    /// <summary>Reads {"name": string, "arguments": object}; false for any other shape.</summary>
    internal static bool TryTarget(JsonElement input, out string name, out JsonElement arguments)
    {
        name = "";
        arguments = default;
        if (input.ValueKind != JsonValueKind.Object) return false;
        string? found = null;
        JsonElement? given = null;
        foreach (var property in input.EnumerateObject())
        {
            if (property.NameEquals("name") && found is null && property.Value.ValueKind == JsonValueKind.String) found = property.Value.GetString();
            else if (property.NameEquals("arguments") && given is null && property.Value.ValueKind == JsonValueKind.Object) given = property.Value;
            else return false;
        }
        if (found is null || given is null) return false;
        name = found;
        arguments = given.Value;
        return true;
    }

    internal static McpServerTool DescribeTool(Func<IReadOnlyList<RelayTool>> available, bool native) => new DescribingTool(available, native);

    internal static McpServerTool CallTool(Func<IReadOnlyList<RelayTool>> available, Func<string, JsonElement, CancellationToken, Task<AdapterReply>> call,
        Action<IReadOnlyList<CoCern.Contracts.Dashboard.ConnectionDiagnosticEvent>>? report = null) => new CallingTool(available, call, report);

    /// <summary>The named tool when <paramref name="available"/> holds exactly that name (ordinal), else null.</summary>
    private static RelayTool? Find(Func<IReadOnlyList<RelayTool>> available, string name) =>
        available().FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    /// <summary>A listed catalog tool is pointed back at its own name; an on-demand one the profile hides gets <see cref="ChatGuidance.ToolOff"/>; an unknown name gets no hint.</summary>
    private static AdapterReply Unknown(string name)
    {
        var hint = RelayToolCatalog.TryGet(name, out var known) && !known.OnDemand ? ChatGuidance.Listed(known.Name) : ChatGuidance.ToolOff(name);
        return new(false, "unknown_tool", hint is not null ? JsonSerializer.SerializeToElement(new Dictionary<string, string> { [ChatGuidance.HintField] = hint }) : null);
    }

    private static AdapterReply Invalid(string field) =>
        new(false, RelayErrorCodes.InvalidArguments, JsonSerializer.SerializeToElement(new { field }));

    private static JsonElement Arguments(RequestContext<CallToolRequestParams> request) =>
        JsonSerializer.SerializeToElement(request.Params.Arguments ?? new Dictionary<string, JsonElement>());

    private sealed class DescribingTool(Func<IReadOnlyList<RelayTool>> available, bool native) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = Describe,
            Description = DescribeText,
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { name = new { type = "string" } },
                additionalProperties = false
            })
        };

        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(McpReadResults.FromAdapter(Describe, Answer(Arguments(request))));

        /// <summary>No name: the tools this chat may call, one line each. A name: that tool's description and the schema it would have had when listed.</summary>
        internal AdapterReply Answer(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object) return Invalid("name");
            string? name = null;
            foreach (var property in input.EnumerateObject())
            {
                if (!property.NameEquals("name") || name is not null || property.Value.ValueKind != JsonValueKind.String) return Invalid("name");
                name = property.Value.GetString();
            }
            if (name is null)
                return new(true, "Ok", JsonSerializer.SerializeToElement(new { tools = available().Select(tool => new { name = tool.Name, description = tool.Description }).ToArray() }));
            if (Find(available, name) is not { } found) return Unknown(name);
            return new(true, "Ok", JsonSerializer.SerializeToElement(new { name = found.Name, description = found.Description, inputSchema = CoordinationTools.ListedSchema(found, native) }));
        }
    }

    internal sealed class CallingTool(Func<IReadOnlyList<RelayTool>> available, Func<string, JsonElement, CancellationToken, Task<AdapterReply>> call,
        Action<IReadOnlyList<CoCern.Contracts.Dashboard.ConnectionDiagnosticEvent>>? report) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = Call,
            Description = CallText,
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { name = new { type = "string" }, arguments = new { type = "object" } },
                required = new[] { "name", "arguments" },
                additionalProperties = false
            })
        };

        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        /// <summary>Refuses a target outside <c>available</c> without calling anything; otherwise runs it through the chat's own call path under its name.</summary>
        internal Task<AdapterReply> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            if (!TryTarget(input, out var name, out var arguments))
                return Task.FromResult(Invalid(input.ValueKind == JsonValueKind.Object && input.TryGetProperty("name", out var given) && given.ValueKind == JsonValueKind.String ? "arguments" : "name"));
            if (Find(available, name) is null) return Task.FromResult(Unknown(name));
            return call(name, arguments, cancellationToken);
        }

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var input = Arguments(request);
            // Diagnostics admit only catalog names, so the trace and the reply run under the target's; a refused name is recorded as "unknown".
            var tool = TryTarget(input, out var name, out _) && Find(available, name) is not null ? name : Call;
            var trace = ConnectionTrace.Begin(tool, "adapter", request.Params.Meta);
            try
            {
                var reply = await RunAsync(input, cancellationToken);
                if (!reply.Ok && reply.ConnectionTraceId is null) reply = reply with { ConnectionTraceId = trace.CorrelationId };
                var result = McpReadResults.FromAdapter(tool, reply);
                trace.Complete(reply.Ok, reply.Ok ? null : reply.Code);
                return result;
            }
            catch (OperationCanceledException) { trace.Complete(false, "cancelled"); throw; }
            catch { trace.Complete(false, "tool_error"); throw; }
            finally { trace.Dispose(); report?.Invoke(trace.Events); }
        }
    }
}

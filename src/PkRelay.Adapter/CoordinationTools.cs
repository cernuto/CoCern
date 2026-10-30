using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PkRelay.Adapter.Node;
using PkRelay.Contracts;

namespace PkRelay.Adapter;

public static class CoordinationTools
{
    /// <summary>The chat profile: what a chat needs to find peers and exchange messages, and nothing it would carry unused in every turn.</summary>
    private static readonly HashSet<string> ChatTools = [RelayTools.SessionOpen, RelayTools.SendMessage, RelayTools.CheckInbox, RelayTools.Ack, RelayTools.ChatDirectory];

    public static IReadOnlyList<string> NamesForRole(bool admin, bool chatOnly = false) => Surface(admin, chatOnly).Select(tool => tool.Name).ToArray();
    public static bool IsAllowed(string name, bool admin, bool chatOnly = false) =>
        RelayToolCatalog.TryGet(name, out var tool) && (admin || !tool.AdministratorOnly) && (!chatOnly || ChatTools.Contains(tool.Name));

    public static IEnumerable<McpServerTool> Create(RelayForwarder forwarder, bool admin, SessionHints? hints = null, SessionHandoff? handoff = null, bool chatOnly = false) =>
        Surface(admin, chatOnly).Select(tool => new ForwardedTool(tool, forwarder, hints, handoff));

    private static IEnumerable<RelayTool> Surface(bool admin, bool chatOnly) => RelayToolCatalog.ForRole(admin).Where(tool => !chatOnly || ChatTools.Contains(tool.Name));

    private sealed class ForwardedTool : McpServerTool
    {
        private readonly RelayForwarder _forwarder;
        private readonly SessionHints? _hints;
        private readonly SessionHandoff? _handoff;
        public ForwardedTool(RelayTool tool, RelayForwarder forwarder, SessionHints? hints, SessionHandoff? handoff)
        {
            _forwarder = forwarder;
            _hints = hints;
            _handoff = handoff;
            ProtocolTool = new Tool
            {
                Name = tool.Name,
                // Argument guidance shared by every tool lives once in the server instructions, not in each definition.
                Description = tool.Description,
                InputSchema = tool.Name == RelayTools.CheckInbox ? With(RelayToolCatalog.ForwardingSchema(tool), "waitSeconds", InboxWait.Schema)
                    : ConditionalRead.Applies(tool.Name) ? With(RelayToolCatalog.ForwardingSchema(tool), "ifNoneMatch", ConditionalRead.Schema)
                    : RelayToolCatalog.ForwardingSchema(tool)
            };
        }

        public override Tool ProtocolTool { get; }

        /// <summary>A hub schema plus one of the adapter's own options (<see cref="InboxWait"/>, <see cref="ConditionalRead"/>).</summary>
        private static JsonElement With(JsonElement schema, string property, object definition)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!.AsObject();
            node["properties"]!.AsObject()[property] = JsonSerializer.SerializeToNode(definition);
            return JsonSerializer.SerializeToElement(node);
        }
        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var arguments = JsonSerializer.SerializeToElement(request.Params.Arguments ?? new Dictionary<string, JsonElement>());
            var reply = ProtocolTool.Name == RelayTools.CheckInbox
                ? await InboxWait.CheckAsync(_forwarder, arguments, InboxWait.PollInterval, cancellationToken)
                : await ConditionalRead.ForwardAsync(_forwarder, ProtocolTool.Name, arguments, cancellationToken);
            if (reply.Ok && ProtocolTool.Name == RelayTools.SessionOpen) _handoff?.Record(arguments, reply);
            if (_hints is not null)
            {
                if (reply.Ok && ProtocolTool.Name == RelayTools.SessionOpen) await _hints.ObserveSessionAsync(arguments, reply, cancellationToken);
                if (reply.Ok && ProtocolTool.Name == RelayTools.CheckInbox) _hints.ResetHint();
            }
            var json = JsonSerializer.Serialize(reply, AdapterReply.JsonOptions);
            return new CallToolResult { IsError = !reply.Ok, Content = [new TextContentBlock { Text = json }] };
        }
    }
}

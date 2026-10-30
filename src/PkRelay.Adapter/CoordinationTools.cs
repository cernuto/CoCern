using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PkRelay.Adapter.Node;
using PkRelay.Contracts;

namespace PkRelay.Adapter;

public static class CoordinationTools
{
    public static IReadOnlyList<string> NamesForRole(bool admin) => RelayToolCatalog.ForRole(admin).Select(tool => tool.Name).ToArray();
    public static bool IsAllowed(string name, bool admin) => RelayToolCatalog.TryGet(name, out var tool) && (admin || !tool.AdministratorOnly);

    public static IEnumerable<McpServerTool> Create(RelayForwarder forwarder, bool admin, SessionHints? hints = null, SessionHandoff? handoff = null) =>
        RelayToolCatalog.ForRole(admin).Select(tool => new ForwardedTool(tool, forwarder, hints, handoff));

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
                Description = tool.Description + " Pass the hub tool's JSON arguments directly; include repositoryId, sessionId/sessionToken for established sessions, requestId for mutations, and applicable expected versions.",
                InputSchema = RelayToolCatalog.ForwardingSchema(tool)
            };
        }

        public override Tool ProtocolTool { get; }
        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var arguments = JsonSerializer.SerializeToElement(request.Params.Arguments ?? new Dictionary<string, JsonElement>());
            var reply = await _forwarder.ForwardAsync(ProtocolTool.Name, arguments, cancellationToken);
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

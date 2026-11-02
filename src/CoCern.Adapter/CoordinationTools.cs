using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CoCern.Adapter.Node;
using CoCern.Contracts;

namespace CoCern.Adapter;

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

    /// <summary>
    /// Every chat on a PC shares one principal, and agents reuse simple request IDs ("open-1"), so two chats' session_open calls could
    /// replay each other's reply and share a session. This adapter (one per chat) makes the ID its own; its own retries still match.
    /// </summary>
    internal static JsonElement OwnRequestId(JsonElement arguments, string? workspace)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("requestId", out var given) || given.ValueKind != JsonValueKind.String) return arguments;
        var node = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!.AsObject();
        // This process is the client machine. Keep the default beside the process-owned request ID so direct HTTP retries retain their original arguments.
        if (!node.ContainsKey("machineId")) node["machineId"] = Environment.MachineName;
        // The checkout this chat works in, so the hub can index its docs. Never advertised; a value the caller gave (even blank or null) stands.
        if (workspace is not null && !node.ContainsKey("workspace")) node["workspace"] = workspace;
        node["requestId"] = Instance + ":" + given.GetString();
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>The git top-level folder that contains <paramref name="directory"/>, or null outside a repository (or when git is missing or slow).</summary>
    internal static string? GitTopLevel(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            foreach (var argument in new[] { "rev-parse", "--show-toplevel" }) start.ArgumentList.Add(argument);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout) || !output.Wait(GitTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                return null;
            }
            var path = output.Result.Trim();
            return process.ExitCode == 0 && path.Length > 0 ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Every session_open from this process reports the workspace it started in: found once, on the first open.</summary>
    internal static JsonElement OwnRequestId(JsonElement arguments) => OwnRequestId(arguments, OwnWorkspace.Value);

    private static readonly Lazy<string?> OwnWorkspace = new(() => GitTopLevel(Environment.CurrentDirectory));

    private static readonly string Instance = Guid.NewGuid().ToString("N");

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
            if (ProtocolTool.Name == RelayTools.SessionOpen) arguments = CoordinationTools.OwnRequestId(arguments);
            var reply = ProtocolTool.Name == RelayTools.CheckInbox
                ? await InboxWait.CheckAsync(_forwarder, arguments, InboxWait.PollInterval, cancellationToken)
                : await ConditionalRead.ForwardAsync(_forwarder, ProtocolTool.Name, arguments, cancellationToken);
            if (reply.Ok && ProtocolTool.Name == RelayTools.SessionOpen) _handoff?.Record(arguments, reply);
            else if (reply.Ok) _handoff?.Touch(arguments);
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <param name="chat">The chat ID the client exported to this process (<see cref="AdapterOptions.ExportedChat"/>), if any; it keeps the chat's request IDs its own across adapter restarts.</param>
    public static IEnumerable<McpServerTool> Create(RelayForwarder forwarder, bool admin, SessionHints? hints = null, SessionHandoff? handoff = null, bool chatOnly = false, ChatLocation? chat = null, LocalChatWake? localWake = null)
    {
        // One per adapter, so every tool sees the session this chat opened.
        var calls = new ChatCalls(forwarder, hints, handoff, RequestScope(chat), localWake: localWake);
        return Surface(admin, chatOnly).Select(tool => new ForwardedTool(tool, calls));
    }

    private static IEnumerable<RelayTool> Surface(bool admin, bool chatOnly) => RelayToolCatalog.ForRole(admin).Where(tool => !chatOnly || ChatTools.Contains(tool.Name));

    /// <summary>
    /// Every chat on a PC shares one principal, and agents reuse simple request IDs ("open-1"), so two chats' session_open calls could
    /// replay each other's reply and share a session. This adapter (one per chat) makes the ID its own under <paramref name="scope"/>
    /// (see <see cref="RequestScope"/>; this process's own ID when none is given), so the chat's own retries still match.
    /// </summary>
    internal static JsonElement OwnRequestId(JsonElement arguments, string? workspace, string? scope = null)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("requestId", out var given) || given.ValueKind != JsonValueKind.String) return arguments;
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        // This process is the client machine. Keep the default beside the process-owned request ID so direct HTTP retries retain their original arguments.
        if (!node.ContainsKey("machineId")) node["machineId"] = Environment.MachineName;
        // The checkout this chat works in, so the hub can index its docs. Never advertised; a value the caller gave (even blank or null) stands.
        if (workspace is not null && !node.ContainsKey("workspace")) node["workspace"] = workspace;
        node["requestId"] = (scope ?? Instance) + ":" + given.GetString();
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// What this chat's request IDs are prefixed with. When the client exported its chat ID (Claude Code's <c>CLAUDE_CODE_SESSION_ID</c>), a hash
    /// of it: the same in every adapter process the chat starts, so a <c>session_open</c> retried after the app restarted its MCP server still gets
    /// the same session, and different for every other chat, so two chats on one PC never share one. Without a chat ID (Codex exports none),
    /// this process's own random ID, so a retry matches only within the process. The hash keeps the chat ID out of the request IDs the hub records.
    /// </summary>
    internal static string RequestScope(ChatLocation? chat) => chat is null ? Instance
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"cocern-request-scope\n{chat.Provider.ToLowerInvariant()}\n{chat.ThreadId.ToLowerInvariant()}")))[..32];

    /// <summary>
    /// <c>control_request close_sessions</c> spares the caller's own session only when the call names it (<c>sessionId</c>), so a label prefix or
    /// idle filter sent from a chat could close that chat itself. When the call names no session, this adapter adds the chat's current one.
    /// A <c>sessionId</c> the caller gave (even blank or null) stands, and so does any other command.
    /// </summary>
    internal static JsonElement WithOwnSession(JsonElement arguments, string? session)
    {
        if (session is null || arguments.ValueKind != JsonValueKind.Object || ChatLocators.Text(arguments, "command") != "close_sessions" || arguments.TryGetProperty("sessionId", out _)) return arguments;
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node["sessionId"] = session;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>The git top-level folder that contains <paramref name="directory"/>, or null outside a repository (or when git is missing or slow).</summary>
    internal static string? GitTopLevel(string directory) => GitTopLevel(directory, Git.Value);

    /// <summary>
    /// Runs <paramref name="git"/> by its full path, found on PATH alone (see <see cref="ExecutableSearch"/>): a bare "git" started in the chat's
    /// workspace would let Windows run a git.exe planted in that repository. Null without one, as when git is not installed.
    /// </summary>
    internal static string? GitTopLevel(string directory, string? git)
    {
        if (git is null) return null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            var start = new System.Diagnostics.ProcessStartInfo(git) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
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

    private static readonly Lazy<string?> Git = new(() => ExecutableSearch.Find("git"));

    /// <summary>Every session_open from this process reports the workspace it started in: found once, on the first open.</summary>
    internal static string? Workspace => OwnWorkspace.Value;

    /// <summary><see cref="OwnRequestId(JsonElement, string?, string?)"/> with this process's workspace and request scope.</summary>
    internal static JsonElement OwnRequestId(JsonElement arguments) => OwnRequestId(arguments, Workspace);

    private static readonly Lazy<string?> OwnWorkspace = new(() => GitTopLevel(Environment.CurrentDirectory));

    private static readonly string Instance = Guid.NewGuid().ToString("N");

    private sealed class ForwardedTool : McpServerTool
    {
        private readonly ChatCalls _calls;
        public ForwardedTool(RelayTool tool, ChatCalls calls)
        {
            _calls = calls;
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
            var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
            node["properties"]!.AsObject()[property] = JsonSerializer.SerializeToNode(definition);
            return JsonSerializer.SerializeToElement(node);
        }
        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var arguments = JsonSerializer.SerializeToElement(request.Params.Arguments ?? new Dictionary<string, JsonElement>());
            var reply = await _calls.CallAsync(ProtocolTool.Name, arguments, cancellationToken);
            var json = JsonSerializer.Serialize(reply, AdapterReply.JsonOptions);
            return new CallToolResult { IsError = !reply.Ok, Content = [new TextContentBlock { Text = json }] };
        }
    }
}

/// <summary>
/// One chat's calls through this adapter: what it adds to them (its own request IDs and workspace on <c>session_open</c>, its own session on
/// <c>close_sessions</c>) and what it takes from their replies (the chat's current session, the node's session handle, the optional hints).
/// </summary>
internal sealed class ChatCalls(RelayForwarder forwarder, SessionHints? hints, SessionHandoff? handoff, string requestScope, Func<string?>? workspace = null, LocalChatWake? localWake = null)
{
    private readonly Func<string?> _workspace = workspace ?? (() => CoordinationTools.Workspace);
    private string? _session;

    /// <summary>The session this chat last opened, or last used with its token, through this adapter; null until a call succeeds.</summary>
    public string? CurrentSession => Volatile.Read(ref _session);

    public async Task<AdapterReply> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        arguments = RelayForwarder.WithOrigin(arguments, background: false);
        if (tool == RelayTools.SessionOpen) arguments = CoordinationTools.OwnRequestId(arguments, _workspace(), requestScope);
        else if (tool == RelayTools.ControlRequest) arguments = CoordinationTools.WithOwnSession(arguments, CurrentSession);
        AdapterReply? originalReply = null;
        var reply = tool == RelayTools.CheckInbox
            ? await InboxWait.CheckAsync(forwarder, arguments, InboxWait.PollInterval, cancellationToken)
            : await ConditionalRead.ForwardAsync(forwarder, tool, arguments, cancellationToken, value => originalReply = value);
        if (!reply.Ok) return reply;
        Learn(tool, arguments, reply);
        localWake?.Observe(tool, arguments, originalReply ?? reply);
        if (tool == RelayTools.SessionOpen) handoff?.Record(arguments, reply);
        else
        {
            handoff?.Touch(arguments);
            // A rename reaches the node's handle too, so the node announces the name the hub now holds.
            if (tool == RelayTools.SessionBind) handoff?.Rename(arguments);
        }
        if (hints is not null)
        {
            if (tool == RelayTools.SessionOpen) await hints.ObserveSessionAsync(arguments, reply, cancellationToken);
            if (tool == RelayTools.CheckInbox) hints.ResetHint();
        }
        return reply;
    }

    /// <summary>The session a successful <c>session_open</c> returned, or that a successful call presented with its private token.</summary>
    private void Learn(string tool, JsonElement arguments, AdapterReply reply)
    {
        var session = tool == RelayTools.SessionOpen
            ? reply.Data is { ValueKind: JsonValueKind.Object } data ? ChatLocators.Text(data, "sessionId") : null
            : RelayToolCatalog.TryGet(tool, out var definition) && definition.RequiresSession && ChatLocators.Text(arguments, "sessionToken") is { Length: > 0 } ? ChatLocators.Text(arguments, "sessionId") : null;
        if (session is { Length: > 0 and <= RelayLimits.MaxIdentifierLength }) Volatile.Write(ref _session, session);
    }
}

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
    /// <summary>
    /// A native chat keeps the chat narrowing until the hub selects a profile for it; an explicit hub profile then replaces that narrowing
    /// (<see cref="ToolSurface.Observe"/> rebuilds with the selected profile).
    /// </summary>
    internal static IEnumerable<McpServerTool> CreateNative(RelayForwarder forwarder, NativeJoin join, NativeRegistrationGate registration, bool admin, bool diagnostics = false, ToolSurface? surface = null)
    {
        IEnumerable<McpServerTool> Build(string? profile, bool ceiling) => NativeSurface(profile is null ? admin && ceiling : ceiling, chatOnly: profile is null, profile is null && diagnostics, profile is not null)
            .Where(tool => SessionToolProfiles.Allows(profile, tool.Name, ceiling))
            .Select(tool => new ForwardedTool(tool, async (args, ct) =>
            {
                var reply = await registration.InvokeAsync(join, tool.Name, args, ct);
                if (reply.Ok) surface?.Observe(reply);
                return ToolSurface.StripRouting(surface is null ? reply : ToolSurface.WithRefreshGuidance(reply));
            }, native: true));
        surface?.Configure(Build);
        return Build(null, admin);
    }

    /// <summary>
    /// What a hub-proven chat gets: no session tools, and <c>peers</c>, which no raw surface lists. It keeps <c>chat_directory</c> until a name can
    /// address a message, because <c>send_message</c> still takes a recipient's session ID and <c>peers</c> shows none outside a choice.
    /// </summary>
    private static IEnumerable<RelayTool> NativeSurface(bool admin, bool chatOnly, bool diagnostics, bool eventWaits = false)
    {
        RelayToolCatalog.TryGet(RelayTools.Peers, out var peers);
        return Surface(admin, chatOnly, diagnostics, eventWaits).Where(tool => tool.Name is not (RelayTools.SessionOpen or RelayTools.SessionBind)).Append(peers);
    }

    /// <summary>The chat profile: what a chat needs to find peers and exchange messages, and nothing it would carry unused in every turn.</summary>
    private static readonly HashSet<string> ChatTools = [RelayTools.SessionOpen, RelayTools.SendMessage, RelayTools.CheckInbox, RelayTools.Ack, RelayTools.ChatDirectory];

    public static IReadOnlyList<string> NamesForRole(bool admin, bool chatOnly = false, bool diagnostics = false, bool eventWaits = false) => Surface(admin, chatOnly, diagnostics, eventWaits).Select(tool => tool.Name).ToArray();
    public static bool IsAllowed(string name, bool admin, bool chatOnly = false, bool diagnostics = false, bool eventWaits = false) =>
        RelayToolCatalog.TryGet(name, out var tool) && (admin || !tool.AdministratorOnly) && (eventWaits || name != RelayTools.EventWait) && (!chatOnly || ChatTools.Contains(tool.Name)) && (!diagnostics || InDiagnostics(tool));

    // Private integration seam. Only a future verified provider surface can supply this callback;
    // the raw registration never inspects arguments for a native ID or stamp.
    internal sealed record VerifiedInvocation(NativeHookContext Context, string ToolUseId, JsonElement Arguments);
    internal static IEnumerable<McpServerTool> CreateVerified(NativeJoin join, bool admin, bool chatOnly, bool diagnostics,
        Func<string, JsonElement, CancellationToken, Task<VerifiedInvocation?>> verify)
    {
        return NativeSurface(admin, chatOnly, diagnostics).Select(tool =>
            new ForwardedTool(tool, async (args, ct) => await verify(tool.Name, args, ct) is { } call
                ? await join.CallAsync(call.Context, call.ToolUseId, tool.Name, call.Arguments, ct) : new(false, "native_capture_required")));
    }

    /// <param name="chat">The chat ID the client exported to this process (<see cref="AdapterOptions.ExportedChat"/>), if any; it keeps the chat's request IDs its own across adapter restarts.</param>
    public static IEnumerable<McpServerTool> Create(RelayForwarder forwarder, bool admin, SessionHints? hints = null, SessionHandoff? handoff = null, bool chatOnly = false, ChatLocation? chat = null, LocalChatWake? localWake = null, bool diagnostics = false, bool eventWaits = false, string? repository = null, ToolSurface? surface = null)
    {
        // One per adapter, so every tool sees the session this chat opened.
        var calls = new ChatCalls(forwarder, hints, handoff, RequestScope(chat), localWake: localWake, repository: repository, surface: surface);
        IEnumerable<McpServerTool> Build(string? profile, bool ceiling) => Surface(profile is null ? admin && ceiling : ceiling, profile is null && chatOnly, profile is null && diagnostics, profile is null ? eventWaits : true)
            .Where(tool => SessionToolProfiles.Allows(profile, tool.Name, ceiling))
            .Select(tool => new ForwardedTool(tool, calls, forwarder.ReportConnectionDiagnostics));
        surface?.Configure(Build);
        return Build(null, admin);
    }

    private static IEnumerable<RelayTool> Surface(bool admin, bool chatOnly, bool diagnostics, bool eventWaits = false) => RelayToolCatalog.ForRole(admin, eventWaits).Where(tool => (!chatOnly || ChatTools.Contains(tool.Name)) && (!diagnostics || InDiagnostics(tool)));

    /// <summary>The diagnostics profile: the worker tools plus the administrator's two reads that change nothing and hold no secret.</summary>
    private static bool InDiagnostics(RelayTool tool) => !tool.AdministratorOnly || tool.Name is RelayTools.AnalyticsQuery or RelayTools.HubDiagnose;

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
    internal static string? GitTopLevel(string directory, string? git) => GitTopLevel(directory, git, nativeEvent: false);

    /// <summary>Discover a native event's physical checkout independently of inherited Git selectors.</summary>
    internal static string? NativeGitTopLevel(string directory) => GitTopLevel(directory, Git.Value, nativeEvent: true);

    private static string? GitTopLevel(string directory, string? git, bool nativeEvent)
    {
        try
        {
            var path = GitRead(directory, git, nativeEvent, ["rev-parse", "--show-toplevel"]);
            return path is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? NativeGitRead(string directory, params string[] arguments) => GitRead(directory, Git.Value, true, arguments);

    /// <summary>As <see cref="NativeGitRead"/>, but for a command whose empty or NUL-separated output is an answer: "" means Git succeeded and printed nothing, and null means anything else, including too much output.</summary>
    internal static string? NativeGitOutput(string directory, params string[] arguments) => GitRead(directory, Git.Value, true, arguments, raw: true);

    private static string? GitRead(string directory, string? git, bool nativeEvent, string[] arguments, bool raw = false)
    {
        if (git is null) return null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            var start = new System.Diagnostics.ProcessStartInfo(git) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            // A native event's cwd selects its checkout. Inherited repository/config selectors must not repoint it.
            if (nativeEvent)
                foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                    start.Environment.Remove(name);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            var overflowed = false;
            var output = nativeEvent ? ReadBounded(process.StandardOutput) : process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            if (!process.WaitForExit(GitTimeout) || !output.Wait(GitTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                return null;
            }
            if (raw) return process.ExitCode == 0 && !overflowed ? output.Result : null;
            var value = nativeEvent ? output.Result.TrimEnd('\r', '\n') : output.Result.Trim();
            return process.ExitCode == 0 && value.Length > 0 && (!nativeEvent || !value.Any(char.IsControl)) ? value : null;

            async Task<string> ReadBounded(StreamReader reader)
            {
                var buffer = new char[32769];
                var count = await reader.ReadBlockAsync(buffer);
                overflowed = count == buffer.Length;
                return overflowed ? "" : new string(buffer, 0, count);
            }
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
        private readonly Func<JsonElement, CancellationToken, Task<AdapterReply>> call;
        private readonly Action<IReadOnlyList<CoCern.Contracts.Dashboard.ConnectionDiagnosticEvent>>? report;
        public ForwardedTool(RelayTool tool, ChatCalls calls, Action<IReadOnlyList<CoCern.Contracts.Dashboard.ConnectionDiagnosticEvent>>? report = null)
            : this(tool, (args, ct) => calls.CallAsync(tool.Name, args, ct), report: report) { }

        public ForwardedTool(RelayTool tool, Func<JsonElement, CancellationToken, Task<AdapterReply>> call, bool native = false,
            Action<IReadOnlyList<CoCern.Contracts.Dashboard.ConnectionDiagnosticEvent>>? report = null)
        {
            this.call = call;
            this.report = report;
            ProtocolTool = new Tool
            {
                Name = tool.Name,
                // Argument guidance shared by every tool lives once in the server instructions, not in each definition.
                Description = tool.Description,
                OutputSchema = McpReadResults.OutputSchema(tool.Name),
                InputSchema = tool.Name == RelayTools.CheckInbox ? With(RelayToolCatalog.ForwardingSchema(tool), "waitSeconds", InboxWait.Schema)
                    : ConditionalRead.Applies(tool.Name) ? With(RelayToolCatalog.ForwardingSchema(tool), "ifNoneMatch", ConditionalRead.Schema)
                    : RelayToolCatalog.ForwardingSchema(tool)
            };
            if (native)
            {
                var schema = JsonNode.Parse(ProtocolTool.InputSchema.GetRawText())!.AsObject();
                var self = new HashSet<string>(["sessionId", "sessionToken", "repositoryId", "requestId", "_cocernNative"], StringComparer.Ordinal);
                foreach (var field in self) schema["properties"]!.AsObject().Remove(field);
                if (schema["required"] is JsonArray required)
                    foreach (var field in required.Where(field => field is not null && self.Contains(field.GetValue<string>())).ToArray()) required.Remove(field);
                ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
            }
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
            var trace = ConnectionTrace.Begin(ProtocolTool.Name, "adapter", request.Params.Meta);
            try
            {
                var arguments = JsonSerializer.SerializeToElement(request.Params.Arguments ?? new Dictionary<string, JsonElement>());
                var reply = await call(arguments, cancellationToken);
                if (!reply.Ok && reply.ConnectionTraceId is null) reply = reply with { ConnectionTraceId = trace.CorrelationId };
                var result = McpReadResults.FromAdapter(ProtocolTool.Name, reply);
                trace.Complete(reply.Ok, reply.Ok ? null : reply.Code);
                return result;
            }
            catch (OperationCanceledException) { trace.Complete(false, "cancelled"); throw; }
            catch { trace.Complete(false, "tool_error"); throw; }
            finally { trace.Dispose(); report?.Invoke(trace.Events); }
        }
    }
}

/// <summary>
/// One chat's calls through this adapter: what it adds to them (its own request IDs and workspace on <c>session_open</c>, its own session on
/// <c>close_sessions</c> and on any call that needs a session and leaves it out) and what it takes from their replies (the chat's current session,
/// the node's session handle, the optional hints).
/// </summary>
internal sealed class ChatCalls(RelayForwarder forwarder, SessionHints? hints, SessionHandoff? handoff, string requestScope, Func<string?>? workspace = null, LocalChatWake? localWake = null, string? wakeStatusRoot = null, string? repository = null, ToolSurface? surface = null)
{
    private readonly Func<string?> _workspace = workspace ?? (() => CoordinationTools.Workspace);
    private readonly RelayForwarder _forwarder = forwarder.Tagged(CallOrigins.Mcp);
    private readonly Dictionary<string, (string Id, string Token)> _own = new(StringComparer.Ordinal);
    // Never evict: forgetting a binding could turn a retry into a new write under another session.
    private const int MaxImplicitRequests = 4096;
    private readonly Dictionary<(string Repository, string Tool, string RequestId), (string Id, string Token)> _implicitRequests = new();
    private readonly Lock _handleLock = new();
    private string? _session;

    /// <summary>The session this chat last opened, or last used with its token, through this adapter; null until a call succeeds.</summary>
    public string? CurrentSession => Volatile.Read(ref _session);

    public async Task<AdapterReply> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (ProtectedCheckoutRoot.NamedIn(arguments)) return new(false, "native_invocation_conflict");
        arguments = RelayForwarder.WithOrigin(arguments, background: false);
        if (!TryWithRepository(tool, ref arguments))
            return new(false, RelayErrorCodes.InvalidArguments, JsonSerializer.SerializeToElement(new { field = "repositoryId", hint = ChatGuidance.MissingRepository }));
        if (surface is not null && CurrentSession is not null && tool is not (RelayTools.SessionOpen or RelayTools.GetStatus) && surface.ShouldPoll())
        {
            var repositoryId = ChatLocators.Text(arguments, "repositoryId");
            (string Id, string Token) own = default;
            if (repositoryId is { Length: > 0 })
            {
                lock (_handleLock)
                {
                    if (!_own.TryGetValue(repositoryId, out own) && (handoff?.FindOwn(repositoryId) ?? localWake?.FindOwn(repositoryId)) is { } found)
                        own = (found.SessionId, found.SessionToken);
                }
            }
            if (own.Id is { Length: > 0 } && own.Token is { Length: > 0 } && own.Id == CurrentSession)
            {
                var statusArgs = JsonSerializer.SerializeToElement(new { repositoryId, sessionId = own.Id, sessionToken = own.Token });
                var status = await _forwarder.ReadOwnSettingsAsync(RelayForwarder.WithOrigin(statusArgs, background: true), cancellationToken);
                if (status.Ok) surface.Observe(status, own.Id);
            }
        }
        var suppliedToken = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("sessionToken", out _);
        if (tool == RelayTools.SessionOpen) arguments = CoordinationTools.OwnRequestId(arguments, _workspace(), requestScope);
        else if (tool == RelayTools.ControlRequest) arguments = CoordinationTools.WithOwnSession(arguments, CurrentSession);
        else if (!TryWithOwnHandle(tool, ref arguments))
            return new(false, "implicit_request_capacity", JsonSerializer.SerializeToElement(new { guidance = "For a new write, supply your own sessionId and sessionToken. Reconcile an uncertain write in its original session." }));
        arguments = WithActingSession(tool, arguments);
        AdapterReply? originalReply = null;
        var reply = tool == RelayTools.CheckInbox
            ? await InboxWait.CheckAsync(_forwarder, arguments, InboxWait.PollInterval, cancellationToken)
            : await ConditionalRead.ForwardAsync(_forwarder, tool, arguments, cancellationToken, value => originalReply = value);
        if (!reply.Ok) return reply;
        Learn(tool, arguments, reply, suppliedToken);
        if (CurrentSession is { } ownSession) surface?.Observe(reply, ownSession);
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
        if (tool == RelayTools.SessionOpen) reply = _forwarder.WithOwnStartHint(reply);
        if (surface is not null && tool is RelayTools.SessionOpen or RelayTools.GetStatus) reply = ToolSurface.WithRefreshGuidance(reply);
        reply = ToolSurface.StripRouting(reply);
        return tool is RelayTools.SessionOpen or RelayTools.GetStatus ? LocalWakeStatus.AddTo(reply, wakeStatusRoot) : reply;
    }

    /// <summary>
    /// A model can leave out <c>repositoryId</c>, and the hub would call that <c>RepositoryDenied</c> and send the chat looking for an access problem it doesn't have. A blank counts as left out. This fills in the configured repository.
    /// A read can also use the only repository this chat has a session in, here or on disk. A write can't: what this chat holds can change between a send with a lost reply and its retry, which would then apply in another repository.
    /// With nothing to use it answers without calling the hub. A value the caller gave stands, and the hub still authorizes whatever is sent.
    /// </summary>
    private bool TryWithRepository(string tool, ref JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return true;
        if (arguments.TryGetProperty("repositoryId", out var given) && given.ValueKind != JsonValueKind.Null && !(given.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(given.GetString()))) return true;
        var known = repository;
        if (known is null && RelayToolCatalog.TryGet(tool, out var definition) && !definition.IsMutation)
        {
            lock (_handleLock) known = _own.Count == 1 ? _own.Keys.First() : null;
            known ??= handoff?.OwnRepository() ?? localWake?.OwnRepository();
        }
        if (known is null) return false;
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node["repositoryId"] = known;
        arguments = JsonSerializer.SerializeToElement(node);
        return true;
    }

    /// <summary>
    /// Bind every joined call to this chat's own handle, without changing operation target fields. Public acting fields never supply proof.
    /// </summary>
    private JsonElement WithActingSession(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return arguments;
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node.Remove("actingSessionId");
        node.Remove("actingSessionToken");
        if (tool == RelayTools.SessionOpen || ChatLocators.Text(arguments, "repositoryId") is not { Length: > 0 } repository)
            return JsonSerializer.SerializeToElement(node);
        (string Id, string Token) own = default;
        lock (_handleLock)
        {
            var request = ChatLocators.Text(arguments, "requestId");
            var bound = request is { Length: > 0 } && _implicitRequests.TryGetValue((repository, tool, request), out own);
            if (!bound && !_own.TryGetValue(repository, out own))
            {
                if ((handoff?.FindOwn(repository) ?? localWake?.FindOwn(repository)) is { } found) own = (found.SessionId, found.SessionToken);
            }
        }
        if (own.Id is { Length: > 0 } && own.Token is { Length: > 0 })
        {
            node["actingSessionId"] = own.Id;
            node["actingSessionToken"] = own.Token;
        }
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// A chat's memory of its handle does not outlast its context: when a long conversation is compacted, the token goes with it. The adapter
    /// already holds the handle, so a call that needs a session and leaves it out gets this chat's own: the one it last used in that repository
    /// through this adapter, or, after an adapter restart, the handle on disk with this chat's ID. Anything the caller gave stands (even blank),
    /// and a call naming another session never gains this chat's token. Without either, the call goes on as it was and the hub refuses it.
    /// </summary>
    private bool TryWithOwnHandle(string tool, ref JsonElement arguments)
    {
        if (!RelayToolCatalog.TryGet(tool, out var definition) || !definition.RequiresSession || arguments.ValueKind != JsonValueKind.Object || arguments.TryGetProperty("sessionToken", out _) ||
            ChatLocators.Text(arguments, "repositoryId") is not { Length: > 0 } repository) return true;

        var requestId = definition.IsMutation ? ChatLocators.Text(arguments, "requestId") : null;
        var bind = requestId is { Length: > 0 and <= RelayLimits.MaxIdentifierLength };
        var key = (repository, tool, requestId!);
        (string Id, string Token) own = default;
        lock (_handleLock)
        {
            var bound = bind && _implicitRequests.TryGetValue(key, out own);
            if (!bound && !_own.TryGetValue(repository, out own))
            {
                if ((handoff?.FindOwn(repository) ?? localWake?.FindOwn(repository)) is not { } found) return true;
                own = (found.SessionId, found.SessionToken);
            }
            if (arguments.TryGetProperty("sessionId", out var named) && !(named.ValueKind == JsonValueKind.String && named.GetString() == own.Id)) return true;
            if (bind && !bound)
            {
                if (_implicitRequests.Count >= MaxImplicitRequests) return false;
                _implicitRequests.Add(key, own);
            }
        }

        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node["sessionId"] = own.Id;
        node["sessionToken"] = own.Token;
        arguments = JsonSerializer.SerializeToElement(node);
        return true;
    }

    /// <summary>The session a successful <c>session_open</c> returned, or that a successful call presented with its private token.</summary>
    private void Learn(string tool, JsonElement arguments, AdapterReply reply, bool suppliedToken)
    {
        var opened = tool == RelayTools.SessionOpen;
        if (!opened && !(RelayToolCatalog.TryGet(tool, out var definition) && definition.RequiresSession)) return;
        var source = opened ? reply.Data ?? default : arguments;
        var token = ChatLocators.Text(source, "sessionToken");
        if (!opened && token is not { Length: > 0 }) return;
        if (ChatLocators.Text(source, "sessionId") is not { Length: > 0 and <= RelayLimits.MaxIdentifierLength } session) return;
        lock (_handleLock)
        {
            if (token is { Length: > 0 } && ChatLocators.Text(arguments, "repositoryId") is { Length: > 0 } repository)
            {
                if (!opened)
                {
                    var known = _own.TryGetValue(repository, out var remembered) ? remembered :
                        (handoff?.FindOwn(repository) ?? localWake?.FindOwn(repository)) is { } found ? (found.SessionId, found.SessionToken) : default;
                    if (known != (session, token)) return;
                }
                // A late implicit reply from A must not undo a newer authenticated switch to B.
                if (!opened && !suppliedToken && _own.TryGetValue(repository, out var current) && current != (session, token)) return;
                _own[repository] = (session, token);
            }
            Volatile.Write(ref _session, session);
        }
    }
}

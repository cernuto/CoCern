using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter.Node;

namespace CoCern.Adapter.Setup;

/// <summary>A client settings file that cannot be read or parsed with confidence. Setup exits 5 and leaves the file as it was.</summary>
internal sealed class ClientSettingsException(string message) : Exception(message);

internal enum ClientChange { Unchanged, Created, Updated }

/// <summary>
/// How an existing <c>cocern</c> entry's variables should change, given the ones it has now: each name mapped to its new value, or to null to
/// remove it. Null or empty leaves the entry as it is.
/// </summary>
internal delegate IReadOnlyDictionary<string, string?>? EntryRefresh(IReadOnlyDictionary<string, string> variables);

/// <summary>A decided, not yet written, change to one settings file. <see cref="Apply"/> backs up an existing file, then replaces it atomically.</summary>
internal sealed class ClientPlan
{
    private readonly byte[]? _content;
    private readonly bool _existed;

    private ClientPlan(string path, byte[]? content, bool existed, bool refreshes) => (Path, _content, _existed, Refreshes) = (path, content, existed, refreshes);

    public string Path { get; }
    public bool IsUnchanged => _content is null;
    /// <summary>The write changes the variables of a <c>cocern</c> entry the file already has (see <see cref="EntryRefresh"/>), rather than adding one.</summary>
    public bool Refreshes { get; }
    public static ClientPlan Unchanged(string path) => new(path, null, true, false);
    public static ClientPlan Write(string path, byte[] content, bool existed, bool refreshes = false) => new(path, content, existed, refreshes);

    public ClientChange Apply(TimeProvider clock)
    {
        if (_content is null) return ClientChange.Unchanged;
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(folder);
        if (_existed) Backup(clock);
        var temp = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileName(Path)}.cocern-tmp-{Guid.NewGuid():N}");
        try
        {
            // The replacement takes the original's mode (these files can hold API keys); a rename would otherwise leave it world-readable on Unix.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = _existed ? File.GetUnixFileMode(Path) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options)) stream.Write(_content);
            if (_existed) File.Replace(temp, Path, null); else File.Move(temp, Path);
        }
        catch { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
        return _existed ? ClientChange.Updated : ClientChange.Created;
    }

    private void Backup(TimeProvider clock)
    {
        var stem = $"{Path}.cocern-backup-{clock.GetLocalNow():yyyyMMddHHmmss}";
        for (var n = 0; ; n++)
        {
            try { File.Copy(Path, n == 0 ? stem : $"{stem}-{n}", overwrite: false); return; }
            catch (IOException) when (File.Exists(n == 0 ? stem : $"{stem}-{n}")) { }
        }
    }
}

/// <summary>
/// Registers the CoCern adapter as an MCP server in the user's Codex and Claude Code settings. Both are only ever added to:
/// Codex's config.toml is appended to byte for byte; Claude Code's .claude.json is parsed and written back with indented formatting (its values, order and every other property are kept).
/// The one other change is an <see cref="EntryRefresh"/> a caller asks for: the variables of an existing <c>cocern</c> entry, edited in place.
/// </summary>
internal static class ClientSettings
{
    internal static string? StageNativeCandidate(string folder, string provider, string command, string? developmentRoot = null, string? clientProfile = null)
    {
        try { return StageNativeCandidateCore(folder, provider, command, developmentRoot, clientProfile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { return null; }
    }

    private static string? StageNativeCandidateCore(string folder, string provider, string command, string? developmentRoot, string? clientProfile)
    {
        if (provider is not ("claude" or "codex") || NativeJoinHook.ReadConnection(folder) is null) return null;
        if (clientProfile is not null)
        {
            if (!Path.IsPathFullyQualified(clientProfile) || clientProfile.Length > 32768 || clientProfile.Any(char.IsControl) || !Directory.Exists(clientProfile)) return null;
            clientProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientProfile));
        }
        folder = Path.GetFullPath(folder);
        var scope = NodeState.HasIdentityFiles(folder) ? NodeState.NativeCredentialIdentity(folder) : LocalWakeConnection.NativeCredentialIdentity(folder);
        if (scope is null) return null;
        var path = Path.Combine(folder, "native-client-" + provider + ".candidate.json");
        if (File.Exists(path))
        {
            if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 65536) return null;
            using var previous = JsonDocument.Parse(File.ReadAllBytes(path));
            if (ChatLocators.Text(previous.RootElement, "kind") != "cocern-native-candidate-v1" || ChatLocators.Text(previous.RootElement, "provider") != provider ||
                ChatLocators.Text(previous.RootElement, "state") != "inactive") return null;
            if (ChatLocators.Text(previous.RootElement, "profile") is { } previousProfile && previousProfile != clientProfile) return null;
        }
        var invocation = command + " --native-hook " + provider + " --connection " + LogonStart.Quote(folder) +
            (clientProfile is null ? "" : " --profile " + LogonStart.Quote(clientProfile)) +
            (developmentRoot is null ? "" : " --development-root " + LogonStart.Quote(Path.GetFullPath(developmentRoot)));
        JsonObject Handler(bool stop = false) => stop
            ? new() { ["type"] = "command", ["command"] = invocation, ["asyncRewake"] = true, ["timeout"] = StopHookTimeoutSeconds }
            : new() { ["type"] = "command", ["command"] = invocation, ["timeout"] = 20 };
        var hooks = new JsonObject
        {
            ["SessionStart"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(Handler()) }),
            ["PreToolUse"] = new JsonArray(new JsonObject { ["matcher"] = "^mcp__cocern__.*$", ["hooks"] = new JsonArray(Handler()) })
        };
        if (provider == "claude") hooks["Stop"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(Handler(stop: true)) });
        var candidate = new JsonObject
        {
            ["kind"] = "cocern-native-candidate-v1", ["state"] = "inactive", ["provider"] = provider,
            ["connection"] = folder, ["credentialScope"] = scope, ["profile"] = clientProfile,
            ["helperVersion"] = typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D"), ["hooks"] = hooks
        };
        candidate["definition"] = NativeCheckout.Hash(NativeJoinHook.Canonical(JsonSerializer.SerializeToElement(candidate)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(candidate);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) NodeState.WritePrivately(path, bytes);
        return path;
    }

    // No installed provider/profile handshake has been demonstrated. Candidate presence is never an activation receipt.
    internal static ClientPlan PlanNativePromotion(string path, string folder, string provider) => ClientPlan.Unchanged(path);

    internal static void StageLocalNativeCandidate(string provider, string dotnet, string adapter, IReadOnlyDictionary<string, string> environment, string? cacheRoot = null,
        string? developmentRoot = null, string? clientProfile = null)
    {
        try
        {
            var options = AdapterOptions.FromEnvironment(key => environment.GetValueOrDefault(key));
            if (LocalWakeConnection.Create(options, environment.GetValueOrDefault("COCERN_TOKEN_FILE"), cacheRoot) is { } connection)
                StageNativeCandidate(connection.Folder, provider, SetupEnvironment.AdapterCommandLine(dotnet, adapter), developmentRoot, clientProfile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or AdapterConfigurationException)
        { /* Raw setup remains usable when the optional private candidate cannot be staged. */ }
    }

    internal static void StageDevelopmentNativeCandidate(string configurationPath, string provider, string dotnet, string launcher, string developmentRoot, string? cacheRoot)
    {
        IReadOnlyDictionary<string, string>? environment = null;
        var bytes = ReadAll(configurationPath);
        if (bytes is null) return;
        if (provider == "codex") RefreshCodex(Strict.GetString(bytes, Bom(bytes), bytes.Length - Bom(bytes)), variables => { environment = variables; return null; });
        else if (ParseClaudeJson(configurationPath, bytes)["mcpServers"]?[Name] is JsonObject entry) environment = Variables(entry);
        var configurationFolder = Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        var profile = provider == "codex" ? configurationFolder : Path.Combine(configurationFolder, ".claude");
        if (environment is not null) StageLocalNativeCandidate(provider, dotnet, launcher, environment, cacheRoot, developmentRoot, profile);
    }

    private const string Table = "mcp_servers";
    private const string Name = "cocern";
    private static readonly UTF8Encoding Strict = new(false, true);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ClientChange RegisterCodex(string configTomlPath, string dotnetPath, string adapterDll, TimeProvider? clock = null) => PlanCodex(configTomlPath, dotnetPath, adapterDll).Apply(clock ?? TimeProvider.System);

    public static ClientChange RegisterClaudeCode(string claudeJsonPath, string dotnetPath, string adapterDll, TimeProvider? clock = null) => PlanClaudeCode(claudeJsonPath, dotnetPath, adapterDll).Apply(clock ?? TimeProvider.System);

    internal static ClientPlan PlanDevelopmentCodex(string path, string dotnetPath, string launcherDll, string deploymentRoot, string tokenFile, bool replaceLauncher = false)
    {
        var original = ReadAll(path) ?? throw Malformed(path, "no existing CoCern connection was found");
        string text;
        try { text = Strict.GetString(original, Bom(original), original.Length - Bom(original)); }
        catch (DecoderFallbackException) { throw Malformed(path, "it is not valid UTF-8"); }
        var reader = new TomlReader(text);
        if (!reader.HasCoCern()) throw Malformed(path, "no existing CoCern connection was found");
        IReadOnlyDictionary<string, string>? variables = null;
        RefreshCodex(text, current => { variables = current; return null; });
        if (variables is null || !TiedToToken(variables, tokenFile)) throw Malformed(path, "its CoCern entry is not tied to this hub's token file");
        string[] entry = [Table, Name];
        var command = reader.Assignments.Where(x => x.Table.SequenceEqual(entry) && x.Key.SequenceEqual(new[] { "command" })).ToArray();
        var arguments = reader.Assignments.Where(x => x.Table.SequenceEqual(entry) && x.Key.SequenceEqual(new[] { "args" })).ToArray();
        if (command.Length != 1 || arguments.Length != 1 || TomlInline.String(text, command[0].ValueStart, command[0].ValueEnd) is null)
            throw Malformed(path, "its CoCern launch entry is not a supported command and args table");
        // Our canonical launcher entry is stable: updating current.json is enough to restart its backend, without rewriting config each build.
        var rawArguments = text[arguments[0].ValueStart..arguments[0].ValueEnd];
        var suffix = $", '--dev-launch', {TomlString(deploymentRoot)}]";
        if (!replaceLauncher && rawArguments.StartsWith('[') && rawArguments.EndsWith(suffix, StringComparison.Ordinal)
            && TomlInline.String(rawArguments, 1, rawArguments.Length - suffix.Length) is { } existingLauncher && IsDeployedLauncher(existingLauncher, deploymentRoot))
            return ClientPlan.Unchanged(path);
        var edits = new[]
        {
            (Line: command[0], Text: TomlString(dotnetPath)),
            (Line: arguments[0], Text: $"[{TomlString(launcherDll)}, '--dev-launch', {TomlString(deploymentRoot)}]")
        };
        var result = text;
        foreach (var edit in edits.OrderByDescending(x => x.Line.ValueStart)) result = result[..edit.Line.ValueStart] + edit.Text + result[edit.Line.ValueEnd..];
        return result == text ? ClientPlan.Unchanged(path) : ClientPlan.Write(path, [.. original.AsSpan(0, Bom(original)), .. Encoding.UTF8.GetBytes(result)], existed: true, refreshes: true);
    }

    /// <summary>
    /// The Claude Code counterpart of <see cref="PlanDevelopmentCodex"/>: points the user-scope <c>cocern</c> entry in <c>~/.claude.json</c> at the
    /// development launcher, so each new deployment reaches Claude chats through <c>current.json</c> instead of a stale fixed adapter path.
    /// Claude is optional on a development PC: with no settings file or no <c>cocern</c> entry there is nothing to change.
    /// </summary>
    internal static ClientPlan PlanDevelopmentClaudeCode(string path, string dotnetPath, string launcherDll, string deploymentRoot, string tokenFile, bool replaceLauncher = false)
    {
        if (ReadAll(path) is not { } original) return ClientPlan.Unchanged(path);
        var root = ParseClaudeJson(path, original);
        if (root["mcpServers"] is not JsonObject servers || servers[Name] is not JsonObject entry) return ClientPlan.Unchanged(path);
        if (Variables(entry) is not { } variables || !TiedToToken(variables, tokenFile)) throw Malformed(path, "its CoCern entry is not tied to this hub's token file");
        // As for Codex, a launcher entry already in place stays put: the launcher follows current.json to each new version.
        if (!replaceLauncher && entry["args"] is JsonArray { Count: 3 } current && JsonText(current[1]) == "--dev-launch"
            && JsonText(current[2]) is { } configuredRoot && SamePath(configuredRoot, deploymentRoot)
            && JsonText(current[0]) is { } existingLauncher && IsDeployedLauncher(existingLauncher, deploymentRoot))
            return ClientPlan.Unchanged(path);
        var arguments = new JsonArray(launcherDll, "--dev-launch", deploymentRoot);
        if (JsonText(entry["command"]) == dotnetPath && JsonNode.DeepEquals(entry["args"], arguments)) return ClientPlan.Unchanged(path);
        entry["command"] = dotnetPath;
        entry["args"] = arguments;
        return ClientPlan.Write(path, Rewrite(original, root), existed: true, refreshes: true);
    }

    /// <summary>A development entry belongs to this hub only when its <c>COCERN_TOKEN_FILE</c> is the hub's own token file.</summary>
    private static bool TiedToToken(IReadOnlyDictionary<string, string> variables, string tokenFile) =>
        variables.TryGetValue("COCERN_TOKEN_FILE", out var configured) && Path.IsPathFullyQualified(configured) && SamePath(configured, tokenFile);

    /// <summary>A launcher path is one of this deployment's immutable versions: <c>versions/&lt;64 hex&gt;/CoCern.Adapter.dll</c> under the root, and present.</summary>
    private static bool IsDeployedLauncher(string launcher, string deploymentRoot) =>
        File.Exists(launcher) && Path.GetFileName(launcher) == "CoCern.Adapter.dll"
        && Path.GetDirectoryName(launcher) is { } versionFolder && Path.GetFileName(versionFolder) is { Length: 64 } version && version.All(char.IsAsciiHexDigit)
        && SamePath(launcher, Development.AdapterDeployment.AdapterPath(deploymentRoot, new(version, "")));

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? JsonText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <param name="environment">Variables for the adapter, such as <c>COCERN_TOKEN_FILE</c> on the hub's own PC; none for an enrolled node.</param>
    /// <param name="refresh">How to change the variables of a <c>cocern</c> entry the file already has; without one, such an entry is left as it is.</param>
    public static ClientPlan PlanCodex(string path, string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment = null, EntryRefresh? refresh = null)
    {
        var original = ReadAll(path);
        if (original is null)
            return ClientPlan.Write(path, Encoding.UTF8.GetBytes(Entry(dotnetPath, adapterDll, environment, Environment.NewLine)), existed: false);
        string text;
        try { text = Strict.GetString(original, Bom(original), original.Length - Bom(original)); }
        catch (DecoderFallbackException) { throw Malformed(path, "it is not valid UTF-8"); }
        bool exists;
        try { exists = CodexHasCoCern(text); }
        catch (ClientSettingsException exception) { throw Malformed(path, exception.Message); }
        if (exists)
        {
            if (refresh is null || RefreshCodex(text, refresh) is not { } refreshed) return ClientPlan.Unchanged(path);
            return ClientPlan.Write(path, [.. original.AsSpan(0, Bom(original)), .. Encoding.UTF8.GetBytes(refreshed)], existed: true, refreshes: true);
        }
        var eol = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        var separator = text.Length == 0 ? "" : (text.EndsWith('\n') ? "" : eol) + eol;
        return ClientPlan.Write(path, [.. original, .. Encoding.UTF8.GetBytes(separator + Entry(dotnetPath, adapterDll, environment, eol))], existed: true);
    }

    public static ClientPlan PlanClaudeCode(string path, string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment = null, EntryRefresh? refresh = null) =>
        PlanClaudeJson(path, Launch(dotnetPath, adapterDll, new JsonObject { ["type"] = "stdio" }), environment, refresh);

    /// <summary>The Claude desktop app's <c>claude_desktop_config.json</c>: the same <c>mcpServers</c> shape, without a transport type.</summary>
    public static ClientPlan PlanClaudeDesktop(string path, string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment = null, EntryRefresh? refresh = null) =>
        PlanClaudeJson(path, Launch(dotnetPath, adapterDll, new JsonObject()), environment, refresh);

    /// <summary>Adds <c>command</c> and <c>args</c> to <paramref name="entry"/>: the exe alone when one is beside the dll, else dotnet with the dll (see <see cref="SetupEnvironment.AdapterLaunch"/>).</summary>
    private static JsonObject Launch(string dotnetPath, string adapterDll, JsonObject entry)
    {
        var (command, arguments) = SetupEnvironment.AdapterLaunch(dotnetPath, adapterDll);
        entry["command"] = command;
        entry["args"] = new JsonArray(arguments.Select(argument => (JsonNode?)argument).ToArray());
        return entry;
    }

    private static ClientPlan PlanClaudeJson(string path, JsonObject entry, IReadOnlyDictionary<string, string>? environment, EntryRefresh? refresh)
    {
        var original = ReadAll(path);
        if (environment is { Count: > 0 }) entry["env"] = new JsonObject(environment.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
        if (original is null) return ClientPlan.Write(path, Encoding.UTF8.GetBytes(Render(new JsonObject { ["mcpServers"] = new JsonObject { [Name] = entry } }, "\n") + "\n"), existed: false);
        var root = ParseClaudeJson(path, original);
        JsonObject servers;
        if (root["mcpServers"] is { } existing)
        {
            if (existing is not JsonObject found) throw Malformed(path, "'mcpServers' is not an object");
            if (found.ContainsKey(Name))
            {
                // An entry whose variables cannot be read (env not an object of strings) is never changed.
                if (refresh is null || found[Name] is not JsonObject current || Variables(current) is not { } variables ||
                    Changes(variables.Select(x => (x.Key, x.Value)), refresh) is not { } changes) return ClientPlan.Unchanged(path);
                if (current["env"] is not JsonObject env) current["env"] = env = new JsonObject();
                foreach (var (name, value) in changes)
                    if (value is null) env.Remove(name); else env[name] = value;
                return ClientPlan.Write(path, Rewrite(original, root), existed: true, refreshes: true);
            }
            servers = found;
        }
        else root["mcpServers"] = servers = new JsonObject();
        servers[Name] = entry;
        return ClientPlan.Write(path, Rewrite(original, root), existed: true);
    }

    /// <summary>A Claude JSON settings file as an object: strict UTF-8 and JSON, no comments or trailing commas, an object at the top.</summary>
    private static JsonObject ParseClaudeJson(string path, byte[] original)
    {
        try
        {
            var text = Strict.GetString(original, Bom(original), original.Length - Bom(original));
            return JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }) as JsonObject ?? throw new JsonException("the top level is not an object");
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException) { throw Malformed(path, exception is DecoderFallbackException ? "it is not valid UTF-8" : "it is not valid JSON"); }
    }

    /// <summary>An entry's <c>env</c> as names and values: empty when it has none, null when it is anything but an object of strings.</summary>
    private static Dictionary<string, string>? Variables(JsonObject entry)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry["env"] is null) return variables;
        if (entry["env"] is not JsonObject env) return null;
        foreach (var (name, value) in env)
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var given)) return null;
            variables[name] = given;
        }
        return variables;
    }

    /// <summary>The edited document in the original's own style: its line ends, a final newline when it had one, and its byte order mark.</summary>
    private static byte[] Rewrite(byte[] original, JsonObject root)
    {
        var crlf = Strict.GetString(original).Contains("\r\n");
        var rendered = Render(root, crlf ? "\r\n" : "\n");
        var trailing = original.Length > 0 && original[^1] == (byte)'\n' ? (crlf ? "\r\n" : "\n") : "";
        return (Bom(original) == 3 ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(Encoding.UTF8.GetBytes(rendered + trailing)).ToArray();
    }

    /// <summary>
    /// Adds the CoCern prompt hook as a <c>UserPromptSubmit</c> command hook to a JSON hooks file (Claude Code's settings.json, Codex's
    /// hooks.json; both use <c>{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command",...}]}]}}</c>). Every other entry is kept; a file
    /// that already runs <c>--prompt-hook</c> is left as it was.
    /// </summary>
    public static ClientPlan PlanPromptHook(string path, string command) => PlanHooks(path, PromptHook(command));

    /// <summary>
    /// Adds the CoCern Stop hook to Claude Code's settings.json: when a turn ends it waits on the chat's inbox in the background and, with
    /// <c>asyncRewake</c>, wakes the idle chat when the hub queues a wake. Every other hook is kept; an older CoCern Stop handler (another
    /// adapter path or timeout) is replaced, so setup moves it forward, and a file that already has this exact handler is left as it was.
    /// </summary>
    public static ClientPlan PlanStopHook(string path, string command) => PlanHooks(path, StopHook(command));

    /// <summary>Claude Code's settings.json with the Stop hook and, when asked, the prompt hook: one plan, so neither write loses the other.</summary>
    public static ClientPlan PlanClaudeHooks(string path, string stopCommand, string? promptCommand) =>
        promptCommand is null ? PlanHooks(path, StopHook(stopCommand)) : PlanHooks(path, StopHook(stopCommand), PromptHook(promptCommand));

    /// <summary>
    /// A CoCern command handler for one hook event. A handler whose command contains <paramref name="Marker"/> is ours, and with
    /// <paramref name="AdapterOnly"/> only when the command also runs the CoCern adapter, so another tool's handler that happens to use the
    /// same switch is never replaced or removed.
    /// </summary>
    private sealed record HookSpec(string Event, string Marker, JsonObject Handler, bool ReplaceOwned, bool AdapterOnly = false)
    {
        public bool Owns(string command) => command.Contains(Marker, StringComparison.Ordinal)
            && (!AdapterOnly || command.Contains("CoCern.Adapter.dll", StringComparison.OrdinalIgnoreCase) || command.Contains("CoCern.Adapter.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static HookSpec PromptHook(string command) =>
        new("UserPromptSubmit", "--prompt-hook", new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = PromptHookTimeoutSeconds }, ReplaceOwned: false);

    private static HookSpec StopHook(string command) =>
        new("Stop", "--stop-hook", new JsonObject { ["type"] = "command", ["command"] = command, ["asyncRewake"] = true, ["timeout"] = StopHookTimeoutSeconds }, ReplaceOwned: true, AdapterOnly: true);

    /// <summary>
    /// CoCern command handlers in a JSON hooks file (<c>{"hooks":{"Event":[{"hooks":[{...}]}]}}</c>), all applied to one copy of the file.
    /// Without <see cref="HookSpec.ReplaceOwned"/> a handler of ours already present is left as it was; with it, ours is rewritten in place
    /// and any duplicate of ours removed. Every other handler is kept.
    /// </summary>
    private static ClientPlan PlanHooks(string path, params HookSpec[] specs)
    {
        var original = ReadAll(path);
        JsonObject root;
        if (original is null) root = new JsonObject();
        else
        {
            try
            {
                var text = Strict.GetString(original, Bom(original), original.Length - Bom(original));
                root = (text.Trim().Length == 0 ? new JsonObject() : JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }) as JsonObject) ?? throw new JsonException("the top level is not an object");
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException) { throw Malformed(path, exception is DecoderFallbackException ? "it is not valid UTF-8" : "it is not valid JSON"); }
        }
        var changed = false;
        foreach (var spec in specs) changed |= ApplyHook(path, root, spec);
        if (original is null) return ClientPlan.Write(path, Encoding.UTF8.GetBytes(Render(root, "\n") + "\n"), existed: false);
        return changed ? ClientPlan.Write(path, Rewrite(original, root), existed: true) : ClientPlan.Unchanged(path);
    }

    /// <summary>Applies one handler to <paramref name="root"/>; true when that changed it.</summary>
    private static bool ApplyHook(string path, JsonObject root, HookSpec spec)
    {
        if (root["hooks"] is { } hooksNode && hooksNode is not JsonObject) throw Malformed(path, "'hooks' is not an object");
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        if (hooks[spec.Event] is { } eventNode && eventNode is not JsonArray) throw Malformed(path, $"'hooks.{spec.Event}' is not an array");
        var groups = hooks[spec.Event] as JsonArray ?? (JsonArray)(hooks[spec.Event] = new JsonArray());
        var owned = groups.OfType<JsonObject>().Select(entry => entry["hooks"] as JsonArray).OfType<JsonArray>()
            .SelectMany(list => list.OfType<JsonObject>().Where(entry => JsonText(entry["command"]) is { } text && spec.Owns(text)).Select(entry => (List: list, Entry: entry)))
            .ToArray();
        if (owned.Length == 0)
        {
            groups.Add(new JsonObject { ["hooks"] = new JsonArray(spec.Handler.DeepClone()) });
            return true;
        }
        if (!spec.ReplaceOwned || owned.Length == 1 && JsonNode.DeepEquals(owned[0].Entry, spec.Handler)) return false;
        owned[0].List[owned[0].List.IndexOf(owned[0].Entry)] = spec.Handler.DeepClone();
        foreach (var (list, entry) in owned.Skip(1)) list.Remove(entry);
        foreach (var empty in groups.OfType<JsonObject>().Where(entry => entry["hooks"] is JsonArray { Count: 0 }).ToArray()) groups.Remove(empty);
        return true;
    }

    /// <summary>The client-side limit on one prompt hook run; the hook itself gives up on the hub much sooner (about one second).</summary>
    internal const int PromptHookTimeoutSeconds = 5;

    /// <summary>
    /// The client-side limit on one Stop hook run: a day, the same as the hub's default idle limit for sessions. Claude Code enforces a hook's
    /// timeout even with <c>asyncRewake</c> (60 seconds by default), so the default would silently end every wait; the hook stops waiting
    /// itself shortly before this (<see cref="Node.StopHook.WaitLimit"/>).
    /// </summary>
    internal const int StopHookTimeoutSeconds = 24 * 60 * 60;

    private static string Render(JsonObject root, string eol)
    {
        var text = root.ToJsonString(Pretty).Replace("\r\n", "\n");
        return eol == "\n" ? text : text.Replace("\n", eol);
    }

    private static byte[]? ReadAll(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw Malformed(path, "it cannot be read"); }
    }

    private static int Bom(byte[] bytes) => bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;

    private static ClientSettingsException Malformed(string path, string reason) => new($"Setup stopped: '{path}' could not be understood ({reason}). It was not changed, and nothing else was changed. Fix or move the file, or use --skip-client-config.");

    /// <summary>The table to append. Single-quoted literal strings need no escaping of Windows backslashes.</summary>
    private static string Entry(string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment, string eol)
    {
        var (command, arguments) = SetupEnvironment.AdapterLaunch(dotnetPath, adapterDll);
        return $"[{Table}.{Name}]{eol}command = {TomlString(command)}{eol}args = [{string.Join(", ", arguments.Select(TomlString))}]{eol}"
            + (environment is { Count: > 0 } ? $"env = {{ {string.Join(", ", environment.Select(pair => $"{pair.Key} = {TomlString(pair.Value)}"))} }}{eol}" : "");
    }

    internal static string TomlString(string value)
    {
        if (!value.Any(c => c == '\'' || char.IsControl(c))) return $"'{value}'";
        var builder = new StringBuilder("\"");
        foreach (var c in value)
            builder.Append(c switch { '\\' => "\\\\", '"' => "\\\"", _ when char.IsControl(c) => $"\\u{(int)c:X4}", _ => c.ToString() });
        return builder.Append('"').ToString();
    }

    /// <summary>
    /// True when the text already defines <c>mcp_servers.cocern</c> (as a table, a sub-table, a dotted key, or a key inside <c>[mcp_servers]</c>).
    /// A conservative reader: anything it cannot follow with confidence throws <see cref="ClientSettingsException"/>, so the caller leaves the file alone.
    /// </summary>
    internal static bool CodexHasCoCern(string text) => new TomlReader(text).HasCoCern();

    /// <summary>
    /// The config.toml text with the <c>cocern</c> entry's variables changed as <paramref name="refresh"/> says, or null when it leaves them alone.
    /// Only an entry written as a <c>[mcp_servers.cocern]</c> table is edited, with its variables either in an <c>env = { NAME = "value", ... }</c>
    /// inline table (the shape setup and Connect write) or in a <c>[mcp_servers.cocern.env]</c> table of <c>NAME = "value"</c> lines. Any other
    /// shape, or a variable that is not a plain string, is left as it is. Only the edited value or line changes; every other byte is kept.
    /// </summary>
    internal static string? RefreshCodex(string text, EntryRefresh refresh)
    {
        var reader = new TomlReader(text);
        if (!reader.HasCoCern()) return null;
        string[] entry = [Table, Name], envTable = [Table, Name, "env"];
        var header = reader.Headers.Where(x => x.Table.SequenceEqual(entry)).ToArray();
        // Defined (or added to) through dotted keys or an inline table elsewhere, or with deeper tables: not a shape edited here.
        if (header.Length != 1 || reader.Headers.Any(x => x.Table.Count > 3 && x.Table.Take(3).SequenceEqual(envTable)) ||
            reader.Assignments.Any(x => x.Table.Count + x.Key.Count >= 2 && x.Table.Concat(x.Key).Take(2).SequenceEqual(entry) && x.Table.Count < 2)) return null;
        var inline = reader.Assignments.Where(x => x.Table.SequenceEqual(entry) && x.Key[0] == "env").ToArray();
        var tableHeader = reader.Headers.Where(x => x.Table.SequenceEqual(envTable)).ToArray();
        var tableLines = reader.Assignments.Where(x => x.Table.SequenceEqual(envTable)).ToArray();
        if (inline.Length > 1 || inline.Any(x => x.Key.Count != 1) || tableHeader.Length > 1 || inline.Length + tableHeader.Length > 1 || tableLines.Any(x => x.Key.Count != 1)) return null;
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";

        if (inline is [var env])
        {
            if (TomlInline.Strings(text, env.ValueStart, env.ValueEnd) is not { } pairs) return null;
            if (Changes(pairs.Select(x => (x.Key, x.Value)), refresh) is not { } changes) return null;
            var parts = new List<string>();
            foreach (var pair in pairs)
                if (!changes.TryGetValue(pair.Key, out var value)) parts.Add(text[pair.Start..pair.ValueEnd]);
                else if (value is not null) parts.Add($"{text[pair.Start..pair.KeyEnd]} = {TomlString(value)}");
            parts.AddRange(changes.Where(x => x.Value is not null && pairs.All(pair => pair.Key != x.Key)).Select(x => $"{TomlKey(x.Key)} = {TomlString(x.Value!)}"));
            return text[..env.ValueStart] + (parts.Count == 0 ? "{}" : "{ " + string.Join(", ", parts) + " }") + text[env.ValueEnd..];
        }

        var lines = new List<(TomlAssignment Line, string Value)>();
        foreach (var line in tableLines)
        {
            if (TomlInline.String(text, line.ValueStart, line.ValueEnd) is not { } value) return null;
            lines.Add((line, value));
        }
        if (Changes(lines.Select(x => (x.Line.Key[0], x.Value)), refresh) is not { } edits) return null;
        var added = edits.Where(x => x.Value is not null && lines.All(line => line.Line.Key[0] != x.Key)).Select(x => (x.Key, Value: x.Value!)).ToArray();
        // Edits apply from the end of the text back, so each one's positions still hold.
        var spans = new List<(int Start, int End, string Text)>();
        foreach (var (line, _) in lines)
        {
            if (!edits.TryGetValue(line.Key[0], out var value)) continue;
            spans.Add(value is null ? (line.LineStart, line.LineEnd, "") : (line.ValueStart, line.ValueEnd, TomlString(value)));
        }
        if (added.Length > 0)
        {
            // New lines go after the env table's last line; with no env at all, an inline env goes after the entry table's last line.
            var after = tableHeader is [var envHeader] ? tableLines.Select(x => x.LineEnd).DefaultIfEmpty(envHeader.End).Max()
                : reader.Assignments.Where(x => x.Table.SequenceEqual(entry)).Select(x => x.LineEnd).DefaultIfEmpty(header[0].End).Max();
            var body = tableHeader.Length == 1 ? string.Concat(added.Select(x => $"{TomlKey(x.Key)} = {TomlString(x.Value)}{eol}"))
                : $"env = {{ {string.Join(", ", added.Select(x => $"{TomlKey(x.Key)} = {TomlString(x.Value)}"))} }}{eol}";
            spans.Add((after, after, (after == text.Length && after > 0 && text[after - 1] != '\n' ? eol : "") + body));
        }
        var result = text;
        foreach (var (start, end, replacement) in spans.OrderByDescending(x => x.Start)) result = result[..start] + replacement + result[end..];
        return result;
    }

    /// <summary>What <paramref name="refresh"/> changes among the entry's <paramref name="variables"/>, or null when nothing; an entry naming a variable twice is never edited.</summary>
    private static Dictionary<string, string?>? Changes(IEnumerable<(string Name, string Value)> variables, EntryRefresh refresh)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in variables) if (!current.TryAdd(name, value)) return null;
        var changes = refresh(current)?.Where(x => x.Value is null ? current.ContainsKey(x.Key) : !current.TryGetValue(x.Key, out var now) || now != x.Value).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        return changes is { Count: > 0 } ? changes : null;
    }

    /// <summary>A key as TOML writes it: bare when it can be, else quoted like a value.</summary>
    private static string TomlKey(string key) => key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : TomlString(key);

    /// <summary>One <c>key = value</c> as the reader found it: its table and key, where its line starts and ends (past the newline), and its value without what follows it.</summary>
    private sealed record TomlAssignment(List<string> Table, List<string> Key, int LineStart, int ValueStart, int ValueEnd, int LineEnd);

    /// <summary>Reads the inline tables and single-line strings a CoCern entry's variables are written in; anything else is null, never a guess.</summary>
    private static class TomlInline
    {
        /// <summary>One <c>KEY = "value"</c> of an inline table: where the pair and its key start and end, and the decoded key and value.</summary>
        public sealed record Pair(string Key, string Value, int Start, int KeyEnd, int ValueEnd);

        /// <summary>The string pairs of the inline table from <paramref name="start"/> to <paramref name="end"/>; null when it holds anything else.</summary>
        public static List<Pair>? Strings(string text, int start, int end)
        {
            if (end - start < 2 || text[start] != '{' || text[end - 1] != '}') return null;
            var close = end - 1;
            var pairs = new List<Pair>();
            var p = Spaces(text, start + 1, close);
            if (p == close) return pairs;
            while (true)
            {
                var pairStart = p;
                if (Key(text, p, close) is not { } key) return null;
                p = Spaces(text, key.End, close);
                if (p >= close || text[p] != '=') return null;
                p = Spaces(text, p + 1, close);
                if (Read(text, p, close) is not { } value) return null;
                pairs.Add(new(key.Key, value.Value, pairStart, key.End, value.End));
                p = Spaces(text, value.End, close);
                if (p == close) return pairs;
                if (text[p] != ',') return null;
                p = Spaces(text, p + 1, close);
                if (p >= close) return null;
            }
        }

        /// <summary>The value from <paramref name="start"/> to <paramref name="end"/> when it is exactly one single-line string.</summary>
        public static string? String(string text, int start, int end) => Read(text, start, end) is { } read && read.End == end ? read.Value : null;

        private static int Spaces(string text, int p, int limit)
        {
            while (p < limit && text[p] is ' ' or '\t') p++;
            return p;
        }

        /// <summary>A one-part key, bare or quoted, and the position after it; a dotted one is null.</summary>
        private static (string Key, int End)? Key(string text, int p, int limit)
        {
            (string Key, int End)? key;
            if (p < limit && text[p] is '"' or '\'') key = Read(text, p, limit);
            else
            {
                var start = p;
                while (p < limit && (char.IsAsciiLetterOrDigit(text[p]) || text[p] is '_' or '-')) p++;
                key = p == start ? null : (text[start..p], p);
            }
            if (key is not { } found) return null;
            var next = Spaces(text, found.End, limit);
            return next < limit && text[next] == '.' ? null : found;
        }

        /// <summary>A single-line literal (<c>'...'</c>) or basic (<c>"..."</c>) string and the position after it; a multi-line one, or a basic one with an escape TOML does not define, is null.</summary>
        private static (string Value, int End)? Read(string text, int p, int limit)
        {
            if (p >= limit || text[p] is not ('"' or '\'')) return null;
            var quote = text[p];
            if (p + 2 < limit && text[p + 1] == quote && text[p + 2] == quote) return null;
            var builder = new StringBuilder();
            for (var i = p + 1; i < limit; i++)
            {
                var c = text[i];
                if (c is '\n' or '\r') return null;
                if (c == quote) return (builder.ToString(), i + 1);
                if (quote == '"' && c == '\\')
                {
                    if (++i >= limit) return null;
                    switch (text[i])
                    {
                        case 'b': builder.Append('\b'); break;
                        case 't': builder.Append('\t'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'r': builder.Append('\r'); break;
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case 'u' or 'U':
                            var digits = text[i] == 'u' ? 4 : 8;
                            if (i + digits >= limit || !int.TryParse(text.AsSpan(i + 1, digits), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code) || !Rune.IsValid(code)) return null;
                            builder.Append(new Rune(code).ToString());
                            i += digits;
                            break;
                        default: return null;
                    }
                    continue;
                }
                builder.Append(c);
            }
            return null;
        }
    }

    private sealed class TomlReader(string text)
    {
        private int _p;

        /// <summary>Each table header the text has: its name and where its line ends (past the newline).</summary>
        public List<(List<string> Table, int End)> Headers { get; } = [];
        /// <summary>Each <c>key = value</c> the text has, top-level ones included, in order.</summary>
        public List<TomlAssignment> Assignments { get; } = [];

        private int LineEnd(int from) => text.IndexOf('\n', from) is var newline && newline >= 0 ? newline + 1 : text.Length;

        public bool HasCoCern()
        {
            var table = new List<string>();
            var found = false;
            while (true)
            {
                SkipBlank();
                if (_p >= text.Length) return found;
                if (text[_p] == '[')
                {
                    if (_p + 1 < text.Length && text[_p + 1] == '[') throw new ClientSettingsException("it uses an array of tables");
                    _p++;
                    table = Key();
                    Inline();
                    if (_p >= text.Length || text[_p] != ']') throw new ClientSettingsException("a table header is not closed");
                    _p++;
                    EndOfLine();
                    Headers.Add((table, LineEnd(_p)));
                    if (table.Count >= 2 && table[0] == Table && table[1] == Name) found = true;
                    continue;
                }
                var lineStart = _p == 0 ? 0 : text.LastIndexOf('\n', _p - 1) + 1;
                var key = Key();
                Inline();
                if (_p >= text.Length || text[_p] != '=') throw new ClientSettingsException("a line is not a table header or key = value");
                _p++;
                var (valueStart, valueEnd) = Value();
                Assignments.Add(new(table, key, lineStart, valueStart, valueEnd, LineEnd(_p)));
                if (table.Count == 0 && key[0] == Table)
                {
                    if (key.Count == 1) throw new ClientSettingsException("'mcp_servers' is assigned as a value");
                    if (key[1] == Name) found = true;
                }
                else if (table is [Table] && key[0] == Name) found = true;
            }
        }

        private void Inline() { while (_p < text.Length && text[_p] is ' ' or '\t') _p++; }

        private void SkipBlank()
        {
            while (_p < text.Length)
            {
                if (text[_p] is ' ' or '\t' or '\r' or '\n') _p++;
                else if (text[_p] == '#') ToLineEnd();
                else break;
            }
        }

        private void ToLineEnd() { while (_p < text.Length && text[_p] != '\n') _p++; }

        private void EndOfLine()
        {
            Inline();
            if (_p < text.Length && text[_p] == '#') ToLineEnd();
            if (_p < text.Length && text[_p] is not ('\n' or '\r')) throw new ClientSettingsException("there is text after a table header");
        }

        private List<string> Key()
        {
            var parts = new List<string>();
            while (true)
            {
                Inline();
                if (_p >= text.Length) throw new ClientSettingsException("a key is incomplete");
                var c = text[_p];
                if (c is '"' or '\'')
                {
                    var end = text.IndexOf(c, _p + 1);
                    var line = text.IndexOf('\n', _p + 1);
                    if (end < 0 || (line >= 0 && line < end)) throw new ClientSettingsException("a quoted key is not closed");
                    var body = text[(_p + 1)..end];
                    // An escape other than \\ or \" would need real TOML decoding; refuse rather than guess.
                    if (c == '"') { body = body.Replace("\\\\", "\\").Replace("\\\"", "\""); if (body.Contains('\\')) throw new ClientSettingsException("a quoted key uses escapes"); }
                    parts.Add(body);
                    _p = end + 1;
                }
                else
                {
                    var start = _p;
                    while (_p < text.Length && (char.IsAsciiLetterOrDigit(text[_p]) || text[_p] is '_' or '-')) _p++;
                    if (_p == start) throw new ClientSettingsException("a key is empty or has unsupported characters");
                    parts.Add(text[start.._p]);
                }
                Inline();
                if (_p < text.Length && text[_p] == '.') { _p++; continue; }
                return parts;
            }
        }

        /// <summary>
        /// Skips one value: strings (including multi-line), nested arrays and inline tables, and a trailing comment. Returns where the value starts
        /// and ends, without the whitespace and comment after it.
        /// </summary>
        private (int Start, int End) Value()
        {
            var depth = 0;
            Inline();
            if (_p >= text.Length || text[_p] is '\n' or '\r' or '#') throw new ClientSettingsException("a key has no value");
            var (start, end) = (_p, _p);
            while (_p < text.Length)
            {
                var c = text[_p];
                if (c == '\n' && depth == 0) break;
                if (c == '#') { ToLineEnd(); continue; }
                if (c is '"' or '\'') { SkipString(c); end = _p; continue; }
                if (c is '[' or '{') depth++;
                else if (c is ']' or '}' && --depth < 0) throw new ClientSettingsException("brackets do not match");
                _p++;
                if (c is not (' ' or '\t' or '\r' or '\n')) end = _p;
            }
            if (depth != 0) throw new ClientSettingsException("a bracket is not closed");
            return (start, end);
        }

        private void SkipString(char quote)
        {
            var triple = new string(quote, 3);
            if (string.CompareOrdinal(text, _p, triple, 0, 3) == 0)
            {
                _p += 3;
                while (_p < text.Length)
                {
                    if (quote == '"' && text[_p] == '\\') { _p += 2; continue; }
                    if (string.CompareOrdinal(text, _p, triple, 0, 3) == 0)
                    {
                        _p += 3;
                        for (var extra = 0; extra < 2 && _p < text.Length && text[_p] == quote; extra++) _p++;
                        return;
                    }
                    _p++;
                }
                throw new ClientSettingsException("a multi-line string is not closed");
            }
            _p++;
            while (_p < text.Length)
            {
                var c = text[_p];
                if (c == '\n') break;
                if (quote == '"' && c == '\\') { _p += 2; continue; }
                if (c == quote) { _p++; return; }
                _p++;
            }
            throw new ClientSettingsException("a string is not closed");
        }
    }
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoCern.Adapter.Setup;

/// <summary>A client settings file that cannot be read or parsed with confidence. Setup exits 5 and leaves the file as it was.</summary>
internal sealed class ClientSettingsException(string message) : Exception(message);

internal enum ClientChange { Unchanged, Created, Updated }

/// <summary>A decided, not yet written, change to one settings file. <see cref="Apply"/> backs up an existing file, then replaces it atomically.</summary>
internal sealed class ClientPlan
{
    private readonly byte[]? _content;
    private readonly bool _existed;

    private ClientPlan(string path, byte[]? content, bool existed) => (Path, _content, _existed) = (path, content, existed);

    public string Path { get; }
    public bool IsUnchanged => _content is null;
    public static ClientPlan Unchanged(string path) => new(path, null, true);
    public static ClientPlan Write(string path, byte[] content, bool existed) => new(path, content, existed);

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
/// </summary>
internal static class ClientSettings
{
    private const string Table = "mcp_servers";
    private const string Name = "cocern";
    private static readonly UTF8Encoding Strict = new(false, true);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ClientChange RegisterCodex(string configTomlPath, string dotnetPath, string adapterDll, TimeProvider? clock = null) => PlanCodex(configTomlPath, dotnetPath, adapterDll).Apply(clock ?? TimeProvider.System);

    public static ClientChange RegisterClaudeCode(string claudeJsonPath, string dotnetPath, string adapterDll, TimeProvider? clock = null) => PlanClaudeCode(claudeJsonPath, dotnetPath, adapterDll).Apply(clock ?? TimeProvider.System);

    /// <param name="environment">Variables for the adapter, such as <c>COCERN_TOKEN_FILE</c> on the hub's own PC; none for an enrolled node.</param>
    public static ClientPlan PlanCodex(string path, string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment = null)
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
        if (exists) return ClientPlan.Unchanged(path);
        var eol = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        var separator = text.Length == 0 ? "" : (text.EndsWith('\n') ? "" : eol) + eol;
        return ClientPlan.Write(path, [.. original, .. Encoding.UTF8.GetBytes(separator + Entry(dotnetPath, adapterDll, environment, eol))], existed: true);
    }

    public static ClientPlan PlanClaudeCode(string path, string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment = null) =>
        PlanClaudeJson(path, Launch(dotnetPath, adapterDll, new JsonObject { ["type"] = "stdio" }), environment);

    /// <summary>The Claude desktop app's <c>claude_desktop_config.json</c>: the same <c>mcpServers</c> shape, without a transport type.</summary>
    public static ClientPlan PlanClaudeDesktop(string path, string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment = null) =>
        PlanClaudeJson(path, Launch(dotnetPath, adapterDll, new JsonObject()), environment);

    /// <summary>Adds <c>command</c> and <c>args</c> to <paramref name="entry"/>: the exe alone when one is beside the dll, else dotnet with the dll (see <see cref="SetupEnvironment.AdapterLaunch"/>).</summary>
    private static JsonObject Launch(string dotnetPath, string adapterDll, JsonObject entry)
    {
        var (command, arguments) = SetupEnvironment.AdapterLaunch(dotnetPath, adapterDll);
        entry["command"] = command;
        entry["args"] = new JsonArray(arguments.Select(argument => (JsonNode?)argument).ToArray());
        return entry;
    }

    private static ClientPlan PlanClaudeJson(string path, JsonObject entry, IReadOnlyDictionary<string, string>? environment)
    {
        var original = ReadAll(path);
        if (environment is { Count: > 0 }) entry["env"] = new JsonObject(environment.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
        if (original is null) return ClientPlan.Write(path, Encoding.UTF8.GetBytes(Render(new JsonObject { ["mcpServers"] = new JsonObject { [Name] = entry } }, "\n") + "\n"), existed: false);
        JsonObject root;
        try
        {
            var text = Strict.GetString(original, Bom(original), original.Length - Bom(original));
            root = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }) as JsonObject ?? throw new JsonException("the top level is not an object");
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException) { throw Malformed(path, exception is DecoderFallbackException ? "it is not valid UTF-8" : "it is not valid JSON"); }
        JsonObject servers;
        if (root["mcpServers"] is { } existing)
        {
            if (existing is not JsonObject found) throw Malformed(path, "'mcpServers' is not an object");
            if (found.ContainsKey(Name)) return ClientPlan.Unchanged(path);
            servers = found;
        }
        else root["mcpServers"] = servers = new JsonObject();
        servers[Name] = entry;
        var crlf = Strict.GetString(original).Contains("\r\n");
        var rendered = Render(root, crlf ? "\r\n" : "\n");
        var trailing = original.Length > 0 && original[^1] == (byte)'\n' ? (crlf ? "\r\n" : "\n") : "";
        var bytes = (Bom(original) == 3 ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(Encoding.UTF8.GetBytes(rendered + trailing)).ToArray();
        return ClientPlan.Write(path, bytes, existed: true);
    }

    /// <summary>
    /// Adds the CoCern prompt hook as a <c>UserPromptSubmit</c> command hook to a JSON hooks file (Claude Code's settings.json, Codex's
    /// hooks.json; both use <c>{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command",...}]}]}}</c>). Every other entry is kept; a file
    /// that already runs <c>--prompt-hook</c> is left as it was.
    /// </summary>
    public static ClientPlan PlanPromptHook(string path, string command)
    {
        const string Event = "UserPromptSubmit";
        var original = ReadAll(path);
        var handler = new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = PromptHookTimeoutSeconds };
        var group = new JsonObject { ["hooks"] = new JsonArray(handler) };
        if (original is null) return ClientPlan.Write(path, Encoding.UTF8.GetBytes(Render(new JsonObject { ["hooks"] = new JsonObject { [Event] = new JsonArray(group) } }, "\n") + "\n"), existed: false);
        JsonObject root;
        try
        {
            var text = Strict.GetString(original, Bom(original), original.Length - Bom(original));
            root = (text.Trim().Length == 0 ? new JsonObject() : JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }) as JsonObject) ?? throw new JsonException("the top level is not an object");
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException) { throw Malformed(path, exception is DecoderFallbackException ? "it is not valid UTF-8" : "it is not valid JSON"); }
        if (root["hooks"] is { } hooksNode && hooksNode is not JsonObject) throw Malformed(path, "'hooks' is not an object");
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        if (hooks[Event] is { } eventNode && eventNode is not JsonArray) throw Malformed(path, $"'hooks.{Event}' is not an array");
        var groups = hooks[Event] as JsonArray ?? (JsonArray)(hooks[Event] = new JsonArray());
        var present = groups.OfType<JsonObject>().SelectMany(entry => entry["hooks"] as JsonArray ?? []).OfType<JsonObject>()
            .Any(entry => entry["command"] is JsonValue existing && existing.TryGetValue<string>(out var text) && text.Contains("--prompt-hook", StringComparison.Ordinal));
        if (present) return ClientPlan.Unchanged(path);
        groups.Add(group);
        var crlf = Strict.GetString(original).Contains("\r\n");
        var rendered = Render(root, crlf ? "\r\n" : "\n");
        var trailing = original.Length > 0 && original[^1] == (byte)'\n' ? (crlf ? "\r\n" : "\n") : "";
        var bytes = (Bom(original) == 3 ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(Encoding.UTF8.GetBytes(rendered + trailing)).ToArray();
        return ClientPlan.Write(path, bytes, existed: true);
    }

    /// <summary>The client-side limit on one prompt hook run; the hook itself gives up on the hub much sooner (about one second).</summary>
    internal const int PromptHookTimeoutSeconds = 5;

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

    private sealed class TomlReader(string text)
    {
        private int _p;

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
                    if (table.Count >= 2 && table[0] == Table && table[1] == Name) found = true;
                    continue;
                }
                var key = Key();
                Inline();
                if (_p >= text.Length || text[_p] != '=') throw new ClientSettingsException("a line is not a table header or key = value");
                _p++;
                Value();
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

        /// <summary>Skips one value: strings (including multi-line), nested arrays and inline tables, and a trailing comment.</summary>
        private void Value()
        {
            var depth = 0;
            Inline();
            if (_p >= text.Length || text[_p] is '\n' or '\r' or '#') throw new ClientSettingsException("a key has no value");
            while (_p < text.Length)
            {
                var c = text[_p];
                if (c == '\n' && depth == 0) break;
                if (c == '#') { ToLineEnd(); continue; }
                if (c is '"' or '\'') { SkipString(c); continue; }
                if (c is '[' or '{') depth++;
                else if (c is ']' or '}' && --depth < 0) throw new ClientSettingsException("brackets do not match");
                _p++;
            }
            if (depth != 0) throw new ClientSettingsException("a bracket is not closed");
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

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PkRelay.Adapter.Node;

/// <summary>Where a relay session lives: the client (<c>codex</c> or <c>claude</c>) and its own thread or session ID.</summary>
public sealed record ChatLocation(string Provider, string ThreadId);

/// <summary>Finds the chat that opened a relay session by reading the client's own transcripts.</summary>
public interface IChatLocator
{
    string Provider { get; }
    /// <summary>The one chat whose transcript records the <c>session_open</c> result for this session; null when none or several do.</summary>
    ChatLocation? Locate(string relaySessionId, DateTimeOffset openedAt);
}

/// <summary>The production locators, rooted in the user's profile.</summary>
public static class ChatLocators
{
    /// <summary>A transcript last written this long before the session opened cannot hold its result.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<IChatLocator> Default()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [new CodexChatLocator(Path.Combine(profile, ".codex", "sessions")), new ClaudeChatLocator(Path.Combine(profile, ".claude", "projects"))];
    }

    /// <summary>True when <paramref name="text"/> is a successful relay reply whose <c>data.sessionId</c> is this session. Only that field is read; the token beside it is never touched.</summary>
    internal static bool IsOpenReply(string? text, string relaySessionId)
    {
        if (text is null || !text.Contains(relaySessionId, StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String && id.ValueEquals(relaySessionId);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The transcript's lines, read while the client may still be writing it; an unreadable file has none.</summary>
    internal static IEnumerable<string> Lines(string path)
    {
        StreamReader reader;
        try { reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { yield break; }
        using (reader)
        {
            while (true)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { yield break; }
                if (line is null) yield break;
                yield return line;
            }
        }
    }

    internal static IEnumerable<string> Files(string folder, string pattern, SearchOption option)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = option == SearchOption.AllDirectories, IgnoreInaccessible = true }).ToArray() : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static bool WrittenSince(string path, DateTimeOffset openedAt)
    {
        try { return File.GetLastWriteTimeUtc(path) >= (openedAt - Window).UtcDateTime; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The single location, or null for none or several.</summary>
    internal static ChatLocation? Single(string provider, HashSet<string> ids) => ids.Count == 1 ? new(provider, ids.First()) : null;

    internal static bool IsUuid(string text) => text.Length == 36 && Guid.TryParseExact(text, "D", out _);

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Codex keeps one rollout per thread, <c>sessions\YYYY\MM\DD\rollout-&lt;time&gt;-&lt;threadId&gt;.jsonl</c>. Each MCP call it completes is an
/// <c>event_msg</c> line whose <c>payload</c> is <c>item_completed</c> with an <c>McpToolCall</c> item carrying <c>tool</c> and <c>result.content[].text</c>.
/// Only such a line for <c>session_open</c> counts; the same ID in a <c>chat_directory</c> result, a message or code output does not.
/// </summary>
public sealed class CodexChatLocator(string root) : IChatLocator
{
    public string Provider => "codex";

    public ChatLocation? Locate(string relaySessionId, DateTimeOffset openedAt)
    {
        var threads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ChatLocators.Files(root, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.Length < 36 || !ChatLocators.IsUuid(stem[^36..]) || !ChatLocators.WrittenSince(file, openedAt)) continue;
            if (ChatLocators.Lines(file).Any(line => IsOpenResult(line, relaySessionId))) threads.Add(stem[^36..]);
            if (threads.Count > 1) return null;
        }
        return ChatLocators.Single(Provider, threads);
    }

    private static bool IsOpenResult(string line, string relaySessionId)
    {
        if (!line.Contains("McpToolCall", StringComparison.Ordinal) || !line.Contains("session_open", StringComparison.Ordinal) || !line.Contains(relaySessionId, StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (ChatLocators.Text(record, "type") != "event_msg" || !record.TryGetProperty("payload", out var payload) || ChatLocators.Text(payload, "type") != "item_completed" ||
                !payload.TryGetProperty("item", out var item) || ChatLocators.Text(item, "type") != "McpToolCall" || ChatLocators.Text(item, "tool") != "session_open" ||
                !item.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
            return content.EnumerateArray().Any(x => ChatLocators.Text(x, "type") == "text" && ChatLocators.IsOpenReply(ChatLocators.Text(x, "text"), relaySessionId));
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>
/// Claude Code keeps one transcript per session, <c>projects\&lt;project&gt;\&lt;sessionId&gt;.jsonl</c>. An <c>assistant</c> line holds a <c>tool_use</c> (<c>id</c>, <c>name</c> <c>mcp__&lt;server&gt;__session_open</c>);
/// a later <c>user</c> line holds the matching <c>tool_result</c> (<c>tool_use_id</c>, <c>content</c>). Only a result paired with a <c>session_open</c> call counts.
/// </summary>
public sealed class ClaudeChatLocator(string root) : IChatLocator
{
    public string Provider => "claude";

    public ChatLocation? Locate(string relaySessionId, DateTimeOffset openedAt)
    {
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] projects;
        try { projects = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        foreach (var file in projects.SelectMany(x => ChatLocators.Files(x, "*.jsonl", SearchOption.TopDirectoryOnly)))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!ChatLocators.IsUuid(stem) || !ChatLocators.WrittenSince(file, openedAt)) continue;
            if (Holds(file, relaySessionId)) sessions.Add(stem);
            if (sessions.Count > 1) return null;
        }
        return ChatLocators.Single(Provider, sessions);
    }

    private static bool Holds(string file, string relaySessionId)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ChatLocators.Lines(file))
        {
            if (line.Contains("tool_use", StringComparison.Ordinal) && line.Contains("session_open", StringComparison.Ordinal))
                foreach (var block in Blocks(line, "assistant", "tool_use"))
                    if (ChatLocators.Text(block, "name") is { } name && (name == "session_open" || name.EndsWith("__session_open", StringComparison.Ordinal)) && ChatLocators.Text(block, "id") is { } id) calls.Add(id);
            if (calls.Count == 0 || !line.Contains("tool_result", StringComparison.Ordinal) || !line.Contains(relaySessionId, StringComparison.Ordinal)) continue;
            foreach (var block in Blocks(line, "user", "tool_result"))
            {
                if (ChatLocators.Text(block, "tool_use_id") is not { } id || !calls.Contains(id) || !block.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind == JsonValueKind.String ? ChatLocators.IsOpenReply(content.GetString(), relaySessionId)
                    : content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(x => ChatLocators.Text(x, "type") == "text" && ChatLocators.IsOpenReply(ChatLocators.Text(x, "text"), relaySessionId))) return true;
            }
        }
        return false;
    }

    /// <summary>The content blocks of one kind in a line of the given record type; cloned so they outlive the parsed line.</summary>
    private static List<JsonElement> Blocks(string line, string recordType, string blockType)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (ChatLocators.Text(record, "type") != recordType || !record.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return [];
            return content.EnumerateArray().Where(x => ChatLocators.Text(x, "type") == blockType).Select(x => x.Clone()).ToList();
        }
        catch (JsonException) { return []; }
    }
}

/// <summary>
/// The chats this PC announces: one per session handle a stdio adapter wrote, newest first and at most <see cref="MaxChats"/>.
/// A chat ID the client gave the adapter wins; otherwise the locators must agree on exactly one chat. Found chats are remembered; unfound ones are retried with back-off, since transcripts can be large.
/// </summary>
internal sealed class NodeChats(string nodeFolder, IReadOnlyList<IChatLocator> locators)
{
    /// <summary>The hub's <c>DiscoveryLimits.MaxChatsPerMachine</c>; the adapter does not reference the domain layer.</summary>
    public const int MaxChats = 64;
    private const int MaxRetryCycles = 32;
    private readonly Dictionary<string, ChatLocation> _located = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Misses, int Skip)> _unfound = new(StringComparer.Ordinal);

    public JsonArray Read(Func<string, bool> allowed)
    {
        var handles = NodeState.ReadSessionHandles(nodeFolder).Where(x => allowed(x.RepositoryId)).OrderByDescending(x => x.OpenedAt).Take(MaxChats).ToArray();
        var present = handles.Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _located.Keys.Concat(_unfound.Keys).Where(x => !present.Contains(x)).ToArray()) { _located.Remove(gone); _unfound.Remove(gone); }
        return new JsonArray(handles.Select(handle =>
        {
            var location = Locate(handle);
            return (JsonNode)new JsonObject
            {
                ["provider"] = location?.Provider ?? "unknown", ["threadId"] = location?.ThreadId ?? "unmapped", ["relaySessionId"] = handle.SessionId,
                ["repositoryId"] = handle.RepositoryId, ["label"] = handle.Label, ["wakeable"] = location is not null, ["waiterRunning"] = false
            };
        }).ToArray());
    }

    private ChatLocation? Locate(SessionHandle handle)
    {
        if (handle.Client is { } client) return client;
        if (_located.TryGetValue(handle.SessionId, out var known)) return known;
        if (_unfound.TryGetValue(handle.SessionId, out var unfound) && unfound.Skip > 0) { _unfound[handle.SessionId] = unfound with { Skip = unfound.Skip - 1 }; return null; }
        var found = locators.Select(x => x.Locate(handle.SessionId, handle.OpenedAt)).OfType<ChatLocation>().Distinct().ToArray();
        if (found.Length == 1) { _located[handle.SessionId] = found[0]; _unfound.Remove(handle.SessionId); return found[0]; }
        var misses = unfound.Misses + 1;
        _unfound[handle.SessionId] = (misses, Math.Min(1 << Math.Min(misses, 5), MaxRetryCycles) - 1);
        return null;
    }
}

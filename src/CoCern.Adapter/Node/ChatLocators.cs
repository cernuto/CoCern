using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>Where a relay session lives: the client (<c>codex</c> or <c>claude</c>) and its own thread or session ID.</summary>
public sealed record ChatLocation(string Provider, string ThreadId);

/// <summary>A session still being located: its relay ID, when it was opened, and the chat ID the client claimed for it, if any (only ever trusted once verified).</summary>
public sealed record ChatSearch(string RelaySessionId, DateTimeOffset OpenedAt, string? ClaimedChatId = null);

public enum ChatMatchOutcome { Pending, Located, Ambiguous }

/// <summary>What one scan concluded for a session. Pending means not known yet (not found, or the budget ran out); Ambiguous is final.</summary>
public sealed record ChatMatch(ChatMatchOutcome Outcome, ChatLocation? Location = null)
{
    public static readonly ChatMatch Pending = new(ChatMatchOutcome.Pending);
}

/// <summary>The time one announcement cycle may spend locating chats.</summary>
public sealed class ScanBudget(TimeProvider clock, TimeSpan limit)
{
    private readonly long _start = clock.GetTimestamp();
    public bool Exhausted => clock.GetElapsedTime(_start) >= limit;
    public static ScanBudget Unlimited() => new(TimeProvider.System, TimeSpan.MaxValue);
}

/// <summary>Finds the chat that opened a relay session by reading the client's own transcripts.</summary>
public interface IChatLocator
{
    string Provider { get; }
    /// <summary>A one-off, unbounded search: the one chat whose transcript records the <c>session_open</c> result for this session; null when none or several do.</summary>
    ChatLocation? Locate(string relaySessionId, DateTimeOffset openedAt);
    /// <summary>One bounded pass for every pending session: each transcript is opened at most once and read only past what earlier passes already read. State carries over between calls.</summary>
    IReadOnlyDictionary<string, ChatMatch> Scan(IReadOnlyCollection<ChatSearch> pending, ScanBudget budget);
}

/// <summary>The production locators, rooted in the user's profile.</summary>
public static class ChatLocators
{
    /// <summary>A transcript last written this long before the session opened cannot hold its result.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    /// <summary>Longer lines are skipped without being buffered whole; a <c>session_open</c> result is far smaller.</summary>
    public const int DefaultMaxLineBytes = 1024 * 1024;

    public static IReadOnlyList<IChatLocator> Default()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [new CodexChatLocator(Path.Combine(profile, ".codex", "sessions")), new ClaudeChatLocator(Path.Combine(profile, ".claude", "projects"))];
    }

    /// <summary>The session a successful relay reply opened: <c>data.sessionId</c> when <c>ok</c> is true. Only those fields are read; the token beside them is never touched.</summary>
    internal static string? OpenedSessionId(string? text)
    {
        if (text is null || !text.Contains("sessionId", StringComparison.Ordinal)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("data", out var data) ? Text(data, "sessionId") : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The complete lines from byte <paramref name="start"/>, each with the position just after it. A line longer than <paramref name="maxLineBytes"/> is skipped (null) without being
    /// buffered whole, and a final line with no newline yet is left for a later pass, since the client may still be writing it.
    /// </summary>
    internal static IEnumerable<(string? Line, long End)> ReadLines(Stream stream, long start, int maxLineBytes)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        var skipping = false;
        var position = start;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var from = 0;
            while (from < read)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', from, read - from);
                var length = (newline < 0 ? read : newline) - from;
                if (!skipping && line.Length + length > maxLineBytes) { skipping = true; line.SetLength(0); }
                if (!skipping) line.Write(buffer, from, length);
                if (newline < 0) { position += length; break; }
                position += length + 1;
                from = newline + 1;
                var text = skipping ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                skipping = false;
                line.SetLength(0);
                yield return (text, position);
            }
        }
    }

    internal static Stream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static IEnumerable<string> Files(string folder, string pattern, SearchOption option)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = option == SearchOption.AllDirectories, IgnoreInaccessible = true }).ToArray() : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static bool IsUuid(string text) => text.Length == 36 && Guid.TryParseExact(text, "D", out _);

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// The shared scanning engine. Per transcript it remembers how far each pending session has been read, so a pass reads only new lines and a pass cut short by the budget resumes.
/// A session is located when exactly one transcript holds its result and every transcript that could hold it has been read to its end; two holders make it ambiguous.
/// A chat ID the client claimed wins only once its own transcript (the one file of that name) is seen to hold the result.
/// </summary>
public abstract class TranscriptLocator(int maxLineBytes, Func<string, Stream>? open) : IChatLocator
{
    private readonly Func<string, Stream> _open = open ?? ChatLocators.OpenShared;
    private readonly ScanState _state = new();

    public abstract string Provider { get; }

    /// <summary>A transcript and the chat ID its name gives.</summary>
    protected sealed record Transcript(string Path, string ChatId, DateTime LastWriteUtc, long Length);

    protected abstract IEnumerable<Transcript> Transcripts();

    /// <summary>The sessions whose <c>session_open</c> result this line records. <paramref name="calls"/> is per-transcript memory the matcher may use.</summary>
    protected abstract IEnumerable<string> OpenedSessions(string line, HashSet<string> calls, IReadOnlyCollection<string> pending);

    public ChatLocation? Locate(string relaySessionId, DateTimeOffset openedAt) =>
        Scan(new ScanState(), [new ChatSearch(relaySessionId, openedAt)], ScanBudget.Unlimited()).GetValueOrDefault(relaySessionId) is { Outcome: ChatMatchOutcome.Located, Location: var location } ? location : null;

    public IReadOnlyDictionary<string, ChatMatch> Scan(IReadOnlyCollection<ChatSearch> pending, ScanBudget budget) => Scan(_state, pending, budget);

    private sealed class FileState
    {
        public readonly Dictionary<string, long> Offsets = new(StringComparer.Ordinal);
        public readonly HashSet<string> Calls = new(StringComparer.Ordinal);
    }

    private sealed class ScanState
    {
        public readonly Dictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, HashSet<string>> Holders = new(StringComparer.Ordinal);
    }

    private Dictionary<string, ChatMatch> Scan(ScanState state, IReadOnlyCollection<ChatSearch> pending, ScanBudget budget)
    {
        var ids = pending.Select(x => x.RelaySessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in state.Holders.Keys.Where(x => !ids.Contains(x)).ToArray()) state.Holders.Remove(gone);
        foreach (var file in state.Files.Values) foreach (var gone in file.Offsets.Keys.Where(x => !ids.Contains(x)).ToArray()) file.Offsets.Remove(gone);
        foreach (var id in ids) if (!state.Holders.ContainsKey(id)) state.Holders[id] = new(StringComparer.OrdinalIgnoreCase);
        if (pending.Count == 0) return [];

        var transcripts = Transcripts().ToArray();
        var named = transcripts.GroupBy(x => x.ChatId, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var claimed = pending.Select(x => x.ClaimedChatId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Claimed transcripts first, so a verified claim needs no wider search when the budget is short.
        foreach (var transcript in transcripts.OrderBy(x => claimed.Contains(x.ChatId) ? 0 : 1))
        {
            var needing = pending.Where(x => transcript.LastWriteUtc >= (x.OpenedAt - ChatLocators.Window).UtcDateTime).Select(x => x.RelaySessionId).ToArray();
            if (needing.Length == 0) continue;
            if (!state.Files.TryGetValue(transcript.Path, out var file)) state.Files[transcript.Path] = file = new();
            var start = needing.Min(x => file.Offsets.GetValueOrDefault(x));
            if (start >= transcript.Length) { complete.Add(transcript.Path); continue; }
            if (budget.Exhausted) break;
            var (position, outcome) = Read(transcript, file, start, ids, state, budget);
            foreach (var id in needing) file.Offsets[id] = Math.Max(file.Offsets.GetValueOrDefault(id), position);
            if (outcome == ReadOutcome.Finished) complete.Add(transcript.Path);
            // An unreadable transcript is not complete: the sessions it might hold stay pending until it reads cleanly (or their locate window ends), so a fork never wins by default.
            else if (outcome == ReadOutcome.Failed) continue;
            else break;
        }

        var results = new Dictionary<string, ChatMatch>(StringComparer.Ordinal);
        foreach (var search in pending)
        {
            var holders = state.Holders[search.RelaySessionId];
            var covered = transcripts.Where(x => x.LastWriteUtc >= (search.OpenedAt - ChatLocators.Window).UtcDateTime).All(x => complete.Contains(x.Path));
            results[search.RelaySessionId] =
                search.ClaimedChatId is { } claim && named.GetValueOrDefault(claim) == 1 && holders.Contains(claim) ? new(ChatMatchOutcome.Located, new(Provider, claim)) :
                holders.Count > 1 ? new(ChatMatchOutcome.Ambiguous) :
                holders.Count == 1 && covered ? new(ChatMatchOutcome.Located, new(Provider, holders.First())) :
                ChatMatch.Pending;
        }
        return results;
    }

    private enum ReadOutcome { Finished, Budget, Failed }

    /// <summary>Reads one transcript from <paramref name="start"/>; returns where it stopped and why: its end, the budget, or a read failure.</summary>
    private (long Position, ReadOutcome Outcome) Read(Transcript transcript, FileState file, long start, HashSet<string> ids, ScanState state, ScanBudget budget)
    {
        var position = start;
        try
        {
            using var stream = _open(transcript.Path);
            foreach (var (line, end) in ChatLocators.ReadLines(stream, start, maxLineBytes))
            {
                if (line is not null)
                    foreach (var opened in OpenedSessions(line, file.Calls, ids))
                        if (state.Holders.TryGetValue(opened, out var holders)) holders.Add(transcript.ChatId);
                position = end;
                if (budget.Exhausted) return (position, ReadOutcome.Budget);
            }
            return (position, ReadOutcome.Finished);
        }
        // Passed over this time and read again from here next pass; what it may hold is unknown, so it does not count as read to its end.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (position, ReadOutcome.Failed); }
    }

    protected static IEnumerable<Transcript> Describe(IEnumerable<string> paths, Func<string, string?> chatId)
    {
        foreach (var path in paths)
        {
            if (chatId(Path.GetFileNameWithoutExtension(path)) is not { } id) continue;
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            yield return new(path, id, info.LastWriteTimeUtc, info.Length);
        }
    }
}

/// <summary>
/// Codex keeps one rollout per thread, <c>sessions\YYYY\MM\DD\rollout-&lt;time&gt;-&lt;threadId&gt;.jsonl</c>. Each MCP call it completes is an
/// <c>event_msg</c> line whose <c>payload</c> is <c>item_completed</c> with an <c>McpToolCall</c> item carrying <c>tool</c> and <c>result.content[].text</c>.
/// Only such a line for <c>session_open</c> counts; the same ID in a <c>chat_directory</c> result, a message or code output does not.
/// </summary>
public sealed class CodexChatLocator(string root, int maxLineBytes = ChatLocators.DefaultMaxLineBytes, Func<string, Stream>? open = null) : TranscriptLocator(maxLineBytes, open)
{
    public override string Provider => ChatProviders.Codex;

    protected override IEnumerable<Transcript> Transcripts() =>
        Describe(ChatLocators.Files(root, "rollout-*.jsonl", SearchOption.AllDirectories), stem => stem.Length >= 36 && ChatLocators.IsUuid(stem[^36..]) ? stem[^36..] : null);

    protected override IEnumerable<string> OpenedSessions(string line, HashSet<string> calls, IReadOnlyCollection<string> pending)
    {
        if (!line.Contains("McpToolCall", StringComparison.Ordinal) || !line.Contains("session_open", StringComparison.Ordinal)) return [];
        try
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (ChatLocators.Text(record, "type") != "event_msg" || !record.TryGetProperty("payload", out var payload) || ChatLocators.Text(payload, "type") != "item_completed" ||
                !payload.TryGetProperty("item", out var item) || ChatLocators.Text(item, "type") != "McpToolCall" || ChatLocators.Text(item, "tool") != "session_open" ||
                !item.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return [];
            return content.EnumerateArray().Where(x => ChatLocators.Text(x, "type") == "text").Select(x => ChatLocators.OpenedSessionId(ChatLocators.Text(x, "text"))).OfType<string>().ToArray();
        }
        catch (JsonException) { return []; }
    }
}

/// <summary>
/// Claude Code keeps one transcript per session, <c>projects\&lt;project&gt;\&lt;sessionId&gt;.jsonl</c>. An <c>assistant</c> line holds a <c>tool_use</c> (<c>id</c>, <c>name</c> <c>mcp__&lt;server&gt;__session_open</c>);
/// a later <c>user</c> line holds the matching <c>tool_result</c> (<c>tool_use_id</c>, <c>content</c>). Only a result paired with a <c>session_open</c> call counts.
/// </summary>
public sealed class ClaudeChatLocator(string root, int maxLineBytes = ChatLocators.DefaultMaxLineBytes, Func<string, Stream>? open = null) : TranscriptLocator(maxLineBytes, open)
{
    public override string Provider => ChatProviders.Claude;

    protected override IEnumerable<Transcript> Transcripts()
    {
        string[] projects;
        try { projects = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
        return Describe(projects.SelectMany(x => ChatLocators.Files(x, "*.jsonl", SearchOption.TopDirectoryOnly)), stem => ChatLocators.IsUuid(stem) ? stem : null);
    }

    protected override IEnumerable<string> OpenedSessions(string line, HashSet<string> calls, IReadOnlyCollection<string> pending)
    {
        if (line.Contains("tool_use", StringComparison.Ordinal) && line.Contains("session_open", StringComparison.Ordinal))
            foreach (var block in Blocks(line, "assistant", "tool_use"))
                if (ChatLocators.Text(block, "name") is { } name && (name == "session_open" || name.EndsWith("__session_open", StringComparison.Ordinal)) && ChatLocators.Text(block, "id") is { } id) calls.Add(id);
        if (calls.Count == 0 || !line.Contains("tool_result", StringComparison.Ordinal) || !pending.Any(x => line.Contains(x, StringComparison.Ordinal))) return [];
        var opened = new List<string>();
        foreach (var block in Blocks(line, "user", "tool_result"))
        {
            if (ChatLocators.Text(block, "tool_use_id") is not { } id || !calls.Contains(id) || !block.TryGetProperty("content", out var content)) continue;
            if (content.ValueKind == JsonValueKind.String) opened.Add(ChatLocators.OpenedSessionId(content.GetString()) ?? "");
            else if (content.ValueKind == JsonValueKind.Array)
                opened.AddRange(content.EnumerateArray().Where(x => ChatLocators.Text(x, "type") == "text").Select(x => ChatLocators.OpenedSessionId(ChatLocators.Text(x, "text"))).OfType<string>());
        }
        return opened.Where(x => x.Length > 0);
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
/// Each handle is located only within <c>locateWindow</c> of opening, within <c>budget</c> per cycle; the outcome (its chat, or permanently unmapped) is written back into the handle, so it is never searched again, even after a restart.
/// A located Codex chat is wakeable only while <c>codexFound</c> says the executable is installed; a located Claude chat reports <c>waiterRunning</c> while its own waiter process is alive.
/// Handles that stay unmapped for <see cref="PruneAfter"/> are deleted. The hub's announce reply does not say when a relay session ends, so that is the only pruning signal.
/// </summary>
internal sealed class NodeChats(string nodeFolder, IReadOnlyList<IChatLocator> locators, TimeProvider clock, TimeSpan locateWindow, TimeSpan budget, Func<bool>? codexFound = null)
{
    /// <summary>The hub's <c>DiscoveryLimits.MaxChatsPerMachine</c>; the adapter does not reference the domain layer.</summary>
    public const int MaxChats = 64;
    public static readonly TimeSpan DefaultLocateWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);
    /// <summary>How long a handle that never mapped to a chat is kept before its file is deleted.</summary>
    public static readonly TimeSpan PruneAfter = TimeSpan.FromHours(24);
    /// <summary>How long a chat may go without calling CoCern (its adapter touches the handle) before its handle is deleted, unless its waiter runs.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromDays(7);

    public JsonArray Read(Func<string, bool> allowed)
    {
        var now = clock.GetUtcNow();
        var waiters = ClaudeWaiter.Running(nodeFolder);
        var handles = Prune(NodeState.ReadSessionHandles(nodeFolder), now, waiters).Where(x => allowed(x.RepositoryId)).OrderByDescending(x => x.OpenedAt).Take(MaxChats).ToArray();
        for (var i = 0; i < handles.Length; i++)
            if (handles[i] is { Chat: null, Unmapped: false } open && now - open.OpenedAt > locateWindow) handles[i] = Persist(open with { Unmapped = true });
        var pending = handles.Where(x => x is { Chat: null, Unmapped: false }).ToArray();
        if (pending.Length > 0)
        {
            var scan = new ScanBudget(clock, budget);
            var matches = locators.Select(locator => locator.Scan(pending.Select(x => new ChatSearch(x.SessionId, x.OpenedAt, x.Client?.Provider == locator.Provider ? x.Client.ThreadId : null)).ToArray(), scan)).ToArray();
            for (var i = 0; i < handles.Length; i++)
            {
                if (handles[i] is not { Chat: null, Unmapped: false } handle) continue;
                var found = matches.Select(x => x.GetValueOrDefault(handle.SessionId) ?? ChatMatch.Pending).ToArray();
                var located = found.Where(x => x.Outcome == ChatMatchOutcome.Located).Select(x => x.Location!).Distinct().ToArray();
                if (found.Any(x => x.Outcome == ChatMatchOutcome.Ambiguous) || located.Length > 1) handles[i] = Persist(handle with { Unmapped = true });
                else if (located.Length == 1) handles[i] = Persist(handle with { Chat = located[0] });
            }
        }
        var codex = codexFound?.Invoke() ?? true;
        return Fit(new JsonArray(Current(handles).Select(handle => (JsonNode)new JsonObject
        {
            ["provider"] = handle.Chat?.Provider ?? ChatProviders.Unknown, ["threadId"] = handle.Chat?.ThreadId ?? "unmapped", ["relaySessionId"] = handle.SessionId,
            ["repositoryId"] = handle.RepositoryId, ["label"] = handle.Label, ["wakeable"] = handle.Chat is { } chat && (chat.Provider != ChatProviders.Codex || codex),
            ["waiterRunning"] = handle.Chat is { Provider: ChatProviders.Claude } claude && waiters.Contains(claude.ThreadId), ["openedAt"] = handle.OpenedAt.ToString("O")
        }).ToArray()));
    }

    /// <summary>
    /// Room the announcement leaves for everything but the chat list: a machine name of up to 128 characters, even fully escaped, the client list and the envelope.
    /// </summary>
    private const int EnvelopeReserveBytes = 2048;

    /// <summary>Bounds the chat list by its serialized size as well as its count, so the announcement never exceeds the hub's request limit; the oldest chats (last, as the list is newest first) are dropped first.</summary>
    internal static JsonArray Fit(JsonArray chats)
    {
        while (chats.Count > 0 && Encoding.UTF8.GetByteCount(chats.ToJsonString()) > CoCern.Contracts.RelayLimits.MaxPayloadBytes - EnvelopeReserveBytes) chats.RemoveAt(chats.Count - 1);
        return chats;
    }

    /// <summary>
    /// One provider thread maps to one current relay session: when several located handles name the same chat, only the newest is kept, and each older handle file is deleted,
    /// and its session token with it. A handle whose file cannot be deleted now is still left out of the announcement and is tried again next cycle.
    /// </summary>
    private IEnumerable<SessionHandle> Current(SessionHandle[] handles)
    {
        var newest = handles.Where(x => x.Chat is not null).GroupBy(x => (x.Chat!.Provider.ToLowerInvariant(), x.Chat.ThreadId.ToLowerInvariant()))
            .Select(group => group.OrderByDescending(x => x.OpenedAt).ThenBy(x => x.SessionId, StringComparer.Ordinal).First().SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            if (handle.Chat is not null && !newest.Contains(handle.SessionId)) { Delete(handle); continue; }
            yield return handle;
        }
    }

    /// <summary>When the chat last called CoCern: its handle file's write time (which its adapter refreshes), and never before it opened.</summary>
    private DateTimeOffset LastActive(SessionHandle handle)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(NodeState.SessionHandlePath(nodeFolder, handle.SessionId));
            return written > handle.OpenedAt.UtcDateTime ? new DateTimeOffset(written, TimeSpan.Zero) : handle.OpenedAt;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return handle.OpenedAt; }
    }

    /// <summary>The node's normal delete of one of its own handle files; false when it could not be deleted now.</summary>
    private bool Delete(SessionHandle handle)
    {
        try { File.Delete(NodeState.SessionHandlePath(nodeFolder, handle.SessionId)); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Deletes the node's own handle files for chats that stopped calling CoCern for <see cref="IdleAfter"/>, and for chats that never mapped
    /// (or never will) and opened more than <see cref="PruneAfter"/> ago; returns the rest. A chat with a live waiter is always kept. Otherwise a
    /// handle carrying the chat ID its client captured is kept while in use: <c>--await-inbox --chat</c> finds its session through that claim even when it was never located.
    /// </summary>
    private IEnumerable<SessionHandle> Prune(IEnumerable<SessionHandle> handles, DateTimeOffset now, HashSet<string> waiters)
    {
        foreach (var handle in handles)
        {
            var age = now - handle.OpenedAt;
            if ((handle.Client ?? handle.Chat) is { } chat && waiters.Contains(chat.ThreadId)) { yield return handle; continue; }
            if (now - LastActive(handle) > IdleAfter && Delete(handle)) continue;
            if (handle.Client is not null) { yield return handle; continue; }
            // Kept when it could not be deleted now; tried again next cycle.
            if (handle.Chat is null && (handle.Unmapped || age > locateWindow) && age > PruneAfter && Delete(handle)) continue;
            yield return handle;
        }
    }

    /// <summary>Writes the outcome back through the private writer, token intact. If that fails the outcome still holds for this cycle and is simply found again later.</summary>
    private SessionHandle Persist(SessionHandle handle)
    {
        try { NodeState.WriteSessionHandle(nodeFolder, handle); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return handle;
    }
}

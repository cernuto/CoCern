using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>
/// <c>--prompt-hook</c>: a <c>UserPromptSubmit</c> hook for Claude Code and Codex. For each human message it prints one line about the
/// chat's own relay session (attention and inbox counts, the standing revision, the top item's ID and kind) at no tool call for the model, or nothing when nothing waits.
/// For a Claude chat it then adds the messages not printed before (<see cref="HookMail"/>), sharing the wake's character limit. The status line is a count only and marks nothing.
/// It reads the hook's JSON from standard input for the chat's <c>session_id</c> and uses this PC's node credentials and session handles.
/// It never fails or blocks: each step has a short timeout (<c>COCERN_HOOK_TIMEOUT_MS</c>), the last status is reused for
/// <see cref="RelayLimits.PromptHookCacheSeconds"/> seconds, and on any error it prints nothing.
/// </summary>
public static class PromptHook
{
    internal const string CacheFolder = "hooks";

    /// <summary>Reads the hook input, prints the status line and any new mail, and returns 0, whatever happens.</summary>
    public static Task<int> RunAsync(TextReader input, TextWriter output, string nodeFolder) => RunAsync(input, output, nodeFolder, null, null, chat => LocalWakeConnection.FindForClaude(chat));

    internal static async Task<int> RunAsync(TextReader input, TextWriter output, string nodeFolder, HttpMessageHandler? handler, TimeProvider? clock, Func<string, string?>? findLocal = null)
    {
        try
        {
            // One deadline covers reading the input and the hub call together.
            var timeout = Timeout(Environment.GetEnvironmentVariable("COCERN_HOOK_TIMEOUT_MS"));
            var started = System.Diagnostics.Stopwatch.StartNew();
            var chat = await ReadSessionIdAsync(input).WaitAsync(timeout);
            var remaining = timeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero) return 0;
            var line = await LineForChatAsync(chat, nodeFolder, remaining, handler, clock);
            var printed = false;
            if (chat is not null)
            {
                // A new prompt means any client prompt the chat reported is over. Side by side with the mail, so it adds no time; with no report from a hook it makes no call.
                var clearing = WaitingHook.ClearAsync(chat, nodeFolder, findLocal, timeout, handler);
                // The poll has a full timeout of its own, so a status read that took its time does not cost the chat its mail. All of it stays well inside the client's limit.
                await HookMail.DeliverAsync(chat, nodeFolder, findLocal, ClaudeWaiter.MaxPrintedInbox - line.Length - 1, timeout, handler, async text =>
                {
                    await output.WriteLineAsync(line.Length > 0 ? line + "\n" + text : text);
                    await output.FlushAsync();
                    printed = true;
                });
                await clearing;
            }
            if (!printed && line.Length > 0) await output.WriteLineAsync(line);
        }
        catch (Exception) { /* A hook must never fail the human's message. */ }
        return 0;
    }

    /// <summary>The configured timeout, or the default when it is missing or outside 1 ms to <see cref="RelayLimits.MaxPromptHookTimeoutMs"/>.</summary>
    internal static TimeSpan Timeout(string? setting) =>
        TimeSpan.FromMilliseconds(int.TryParse(setting, out var ms) && ms is > 0 and <= RelayLimits.MaxPromptHookTimeoutMs ? ms : RelayLimits.DefaultPromptHookTimeoutMs);

    /// <summary>The line to print for one hook input, or empty. All work, the hub call included, ends within <paramref name="timeout"/>.</summary>
    internal static async Task<string> LineAsync(string input, string nodeFolder, TimeSpan timeout, HttpMessageHandler? handler = null, TimeProvider? clock = null) =>
        await LineForChatAsync(await ReadSessionIdAsync(new StringReader(input)), nodeFolder, timeout, handler, clock);

    private static async Task<string> LineForChatAsync(string? chat, string nodeFolder, TimeSpan timeout, HttpMessageHandler? handler, TimeProvider? clock)
    {
        using var limit = new CancellationTokenSource(timeout);
        try { return await ComputeAsync(chat, Path.GetFullPath(nodeFolder), handler, clock ?? TimeProvider.System, limit.Token).WaitAsync(timeout); }
        catch (Exception) { return ""; }
    }

    private static async Task<string> ComputeAsync(string? chat, string folder, HttpMessageHandler? handler, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (chat is null || !ChatLocators.IsUuid(chat)) return "";
        var threadId = Guid.ParseExact(chat, "D").ToString("D");
        var cache = Path.Combine(folder, CacheFolder, threadId + ".json");
        var now = clock.GetUtcNow();
        if (Cached(cache, now) is { } line) return line;
        if (NodeState.ReadEnrollment(folder) is not { } enrollment || FindHandle(folder, threadId) is not { } handle) return "";
        var options = new AdapterOptions(enrollment.Hub.AbsoluteUri, enrollment.Credential) { PinnedFingerprint = enrollment.Fingerprint };
        using var http = new HttpClient(handler ?? RelayForwarder.CreateHandler(options), disposeHandler: handler is null);
        var arguments = new JsonObject { ["repositoryId"] = handle.RepositoryId, ["sessionId"] = handle.SessionId, ["sessionToken"] = handle.SessionToken, ["statusProjection"] = "prompt" };
        var reply = await new RelayForwarder(http, options, CallOrigins.Node).ForwardAsync(RelayTools.GetStatus, RelayForwarder.WithOrigin(JsonSerializer.SerializeToElement(arguments), background: true), cancellationToken);
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data || Format(data) is not { } formatted) return "";
        Remember(cache, now, formatted);
        return formatted;
    }

    /// <summary>
    /// One line from a <c>get_status</c> reply's <c>attention</c> block, null when it has none, or empty when nothing waits, so a quiet chat pays nothing for this hook.
    /// Only counts, a revision, a hub-made item ID, a known kind and the quoted task ID are printed; no message or note text.
    /// </summary>
    internal static string? Format(JsonElement status)
    {
        if (!status.TryGetProperty("attention", out var attention) || attention.ValueKind != JsonValueKind.Object) return null;
        static int Count(JsonElement block, string name) => block.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= 0 ? number : 0;
        var actionable = Count(attention, "actionable");
        var inbox = Count(attention, "inbox");
        if (actionable == 0 && inbox == 0) return "";
        var line = new StringBuilder("CoCern: ").Append(actionable).Append(actionable == 1 ? " attention item" : " attention items");
        if (Count(attention, "urgent") is var urgent and > 0) line.Append(" (").Append(urgent).Append(" urgent)");
        line.Append(", ").Append(inbox).Append(" inbox");
        if (attention.TryGetProperty("standing", out var standing) && standing.TryGetInt64(out var revision) && revision > 0) line.Append(", standing r").Append(revision);
        if (attention.TryGetProperty("top", out var top) && top.ValueKind == JsonValueKind.Object && top.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            id.GetString() is { Length: > 2 and <= 24 } itemId && itemId.StartsWith("a-", StringComparison.Ordinal) && itemId[2..].All(char.IsAsciiDigit) &&
            top.TryGetProperty("k", out var kind) && kind.ValueKind == JsonValueKind.String && AttentionKinds.All.Contains(kind.GetString()!))
        {
            line.Append("; top ").Append(itemId).Append(' ').Append(kind.GetString());
            if (top.TryGetProperty("t", out var task) && task.ValueKind == JsonValueKind.String && task.GetString() is { Length: > 0 and <= 256 } taskId) line.Append(' ').Append(JsonSerializer.Serialize(taskId));
        }
        return line.Append('.').ToString();
    }

    /// <summary>The session handle chosen (see <see cref="NodeState.ChooseForChat"/>) among those whose client or located chat is this thread, for any provider.</summary>
    internal static SessionHandle? FindHandle(string nodeFolder, string threadId) =>
        NodeState.ChooseForChat(NodeState.ReadSessionHandles(nodeFolder)
            .Where(handle => string.Equals(handle.Client?.ThreadId, threadId, StringComparison.OrdinalIgnoreCase) || string.Equals(handle.Chat?.ThreadId, threadId, StringComparison.OrdinalIgnoreCase)), threadId);

    /// <summary>Reads the hook input to its end (so the client's write never blocks) and returns its top-level <c>session_id</c>, or null.</summary>
    internal static async Task<string?> ReadSessionIdAsync(TextReader input)
    {
        var scanner = new SessionIdScanner();
        var buffer = new char[8192];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory())) > 0) scanner.Feed(buffer.AsSpan(0, read), final: false);
        scanner.Feed([], final: true);
        return scanner.SessionId;
    }

    /// <summary>Finds the top-level <c>session_id</c> in hook JSON fed in pieces, holding only the current token, so a long pasted prompt cannot truncate it.</summary>
    private sealed class SessionIdScanner
    {
        private const int MaxTokenBytes = 4 * 1024 * 1024;
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
        private byte[] _bytes = new byte[16 * 1024];
        private int _pending;
        private JsonReaderState _state;
        private bool _started, _next, _done;
        public string? SessionId { get; private set; }

        public void Feed(ReadOnlySpan<char> chars, bool final)
        {
            if (_done) return;
            var needed = _pending + _encoder.GetByteCount(chars, final);
            if (needed > MaxTokenBytes) { _done = true; return; }
            if (needed > _bytes.Length) Array.Resize(ref _bytes, Math.Max(needed, _bytes.Length * 2));
            _pending += _encoder.GetBytes(chars, _bytes.AsSpan(_pending), final);
            try { Scan(final); }
            catch (JsonException) { _done = true; }
        }

        private void Scan(bool final)
        {
            var reader = new Utf8JsonReader(_bytes.AsSpan(0, _pending), final, _state);
            while (!_done && reader.Read())
            {
                if (!_started) { _started = true; _done = reader.TokenType != JsonTokenType.StartObject; }
                else if (_next) { _done = true; if (reader.TokenType == JsonTokenType.String) SessionId = reader.GetString(); }
                else if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("session_id")) _next = true;
            }
            var consumed = (int)reader.BytesConsumed;
            _bytes.AsSpan(consumed, _pending - consumed).CopyTo(_bytes);
            _pending -= consumed;
            _state = reader.CurrentState;
        }
    }

    private static string? Cached(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject cached || cached["at"] is not JsonValue at || !at.TryGetValue<long>(out var ticks)) return null;
            var age = now - new DateTimeOffset(ticks, TimeSpan.Zero);
            return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(RelayLimits.PromptHookCacheSeconds) && cached["line"] is JsonValue line && line.TryGetValue<string>(out var text) ? text : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Remember(string path, DateTimeOffset now, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            NodeState.WritePrivately(path, Encoding.UTF8.GetBytes(new JsonObject { ["at"] = now.UtcTicks, ["line"] = line }.ToJsonString()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The cache is a convenience. */ }
    }
}

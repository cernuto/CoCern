using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;

namespace PkRelay.Adapter.Node;

/// <summary>
/// <c>--prompt-hook</c>: a <c>UserPromptSubmit</c> hook for Claude Code and Codex. For each human message it prints one line about the
/// chat's own relay session (attention and inbox counts, the standing revision, the top item's ID and kind) at no tool call for the model.
/// It reads the hook's JSON from standard input for the chat's <c>session_id</c> and uses this PC's node credentials and session handles.
/// It never fails or blocks: the hub call has a short timeout (<c>PKRELAY_HOOK_TIMEOUT_MS</c>), the last answer is reused for
/// <see cref="RelayLimits.PromptHookCacheSeconds"/> seconds, and on any error it prints nothing. It only reads; nothing is marked shown.
/// </summary>
public static class PromptHook
{
    internal const string CacheFolder = "hooks";
    private const int MaxInputChars = 64 * 1024;

    /// <summary>Reads the hook input, prints at most one line and returns 0, whatever happens.</summary>
    public static Task<int> RunAsync(TextReader input, TextWriter output, string nodeFolder) => RunAsync(input, output, nodeFolder, null, null);

    internal static async Task<int> RunAsync(TextReader input, TextWriter output, string nodeFolder, HttpMessageHandler? handler, TimeProvider? clock)
    {
        try
        {
            // One deadline covers reading the input and the hub call together.
            var timeout = Timeout(Environment.GetEnvironmentVariable("PKRELAY_HOOK_TIMEOUT_MS"));
            var started = System.Diagnostics.Stopwatch.StartNew();
            var text = await ReadInputAsync(input).WaitAsync(timeout);
            var remaining = timeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero) return 0;
            var line = await LineAsync(text, nodeFolder, remaining, handler, clock);
            if (line.Length > 0) await output.WriteLineAsync(line);
        }
        catch (Exception) { /* A hook must never fail the human's message. */ }
        return 0;
    }

    /// <summary>The configured timeout, or the default when it is missing or outside 1 ms to <see cref="RelayLimits.MaxPromptHookTimeoutMs"/>.</summary>
    internal static TimeSpan Timeout(string? setting) =>
        TimeSpan.FromMilliseconds(int.TryParse(setting, out var ms) && ms is > 0 and <= RelayLimits.MaxPromptHookTimeoutMs ? ms : RelayLimits.DefaultPromptHookTimeoutMs);

    /// <summary>The line to print for one hook input, or empty. All work, the hub call included, ends within <paramref name="timeout"/>.</summary>
    internal static async Task<string> LineAsync(string input, string nodeFolder, TimeSpan timeout, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        using var limit = new CancellationTokenSource(timeout);
        try { return await ComputeAsync(input, Path.GetFullPath(nodeFolder), handler, clock ?? TimeProvider.System, limit.Token).WaitAsync(timeout); }
        catch (Exception) { return ""; }
    }

    private static async Task<string> ComputeAsync(string input, string folder, HttpMessageHandler? handler, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (JsonNode.Parse(input) is not JsonObject hook || hook["session_id"] is not JsonValue value || !value.TryGetValue<string>(out var chat) || !ChatLocators.IsUuid(chat)) return "";
        var threadId = Guid.ParseExact(chat, "D").ToString("D");
        var cache = Path.Combine(folder, CacheFolder, threadId + ".json");
        var now = clock.GetUtcNow();
        if (Cached(cache, now) is { } line) return line;
        if (NodeState.ReadEnrollment(folder) is not { } enrollment || FindHandle(folder, threadId) is not { } handle) return "";
        var options = new AdapterOptions(enrollment.Hub.AbsoluteUri, enrollment.Credential) { PinnedFingerprint = enrollment.Fingerprint };
        using var http = new HttpClient(handler ?? RelayForwarder.CreateHandler(options), disposeHandler: handler is null);
        var arguments = new JsonObject { ["repositoryId"] = handle.RepositoryId, ["sessionId"] = handle.SessionId, ["sessionToken"] = handle.SessionToken, ["attentionDetail"] = true };
        var reply = await new RelayForwarder(http, options).ForwardAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(arguments), cancellationToken);
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data || Format(data) is not { } formatted) return "";
        Remember(cache, now, formatted);
        return formatted;
    }

    /// <summary>
    /// One line from a <c>get_status</c> reply's <c>attention</c> block, or null when it has none. Only counts, a revision, a hub-made item ID,
    /// a known kind and the quoted task ID are printed; no message or note text.
    /// </summary>
    internal static string? Format(JsonElement status)
    {
        if (!status.TryGetProperty("attention", out var attention) || attention.ValueKind != JsonValueKind.Object) return null;
        static int Count(JsonElement block, string name) => block.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= 0 ? number : 0;
        var actionable = Count(attention, "actionable");
        var line = new StringBuilder("PkRelay: ").Append(actionable).Append(actionable == 1 ? " attention item" : " attention items");
        if (Count(attention, "urgent") is var urgent and > 0) line.Append(" (").Append(urgent).Append(" urgent)");
        line.Append(", ").Append(Count(attention, "inbox")).Append(" inbox");
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

    /// <summary>The newest session handle whose client or located chat is this thread, for any provider.</summary>
    internal static SessionHandle? FindHandle(string nodeFolder, string threadId) =>
        NodeState.ReadSessionHandles(nodeFolder)
            .Where(handle => string.Equals(handle.Client?.ThreadId, threadId, StringComparison.OrdinalIgnoreCase) || string.Equals(handle.Chat?.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(handle => handle.OpenedAt).FirstOrDefault();

    private static async Task<string> ReadInputAsync(TextReader input)
    {
        var buffer = new char[MaxInputChars];
        var total = 0;
        while (total < buffer.Length && await input.ReadAsync(buffer.AsMemory(total)) is var read and > 0) total += read;
        return new string(buffer, 0, total);
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

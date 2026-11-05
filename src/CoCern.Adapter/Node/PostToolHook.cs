using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoCern.Adapter.Node;

/// <summary>
/// <c>--post-tool-hook</c>: a Claude Code <c>PostToolUse</c> hook, so mail reaches a chat during a turn and not only when it is idle or Paul next types. After each tool call it
/// runs <see cref="HookMail"/> and adds any unprinted message to the model's context through <c>hookSpecificOutput.additionalContext</c>; with no mail it prints nothing.
/// It always exits 0. The input's and output's shape come from Claude Code's hooks reference and have not been tried on a real client.
/// </summary>
public static class PostToolHook
{
    /// <summary>The model's own inbox read. Its text reaches the model in the tool result, so the hook records those messages instead of repeating them straight after.</summary>
    internal const string CheckInboxTool = "mcp__cocern__check_inbox";
    /// <summary>A tool's output can be large. Past this the input is only drained, and the next tool call polls instead.</summary>
    private const int MaxInputChars = 4 * 1024 * 1024;

    public static Task<int> RunAsync(TextReader input, TextWriter output, string nodeFolder) =>
        RunAsync(input, output, nodeFolder, null, chat => LocalWakeConnection.FindForClaude(chat));

    internal static async Task<int> RunAsync(TextReader input, TextWriter output, string nodeFolder, HttpMessageHandler? handler, Func<string, string?>? findLocal = null)
    {
        try
        {
            // One deadline covers reading the input and the header poll together.
            var timeout = PromptHook.Timeout(Environment.GetEnvironmentVariable("COCERN_HOOK_TIMEOUT_MS"));
            var started = System.Diagnostics.Stopwatch.StartNew();
            var text = await ReadBoundedAsync(input).WaitAsync(timeout);
            var remaining = timeout - started.Elapsed;
            if (text is null || remaining <= TimeSpan.Zero) return 0;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (ChatLocators.Text(root, "hook_event_name") != "PostToolUse" || ChatLocators.Text(root, "session_id") is not { } chat) return 0;
            // A subagent's event carries its parent's session ID, but its context goes to the subagent: mail printed there would be recorded as delivered and never reach the chat.
            if (ChatLocators.Text(root, "agent_id") is { Length: > 0 }) return 0;
            var readByModel = ChatLocators.Text(root, "tool_name") == CheckInboxTool && root.TryGetProperty("tool_response", out var response) ? MessagesRead(response) : null;
            await HookMail.DeliverAsync(chat, nodeFolder, findLocal, ClaudeWaiter.MaxPrintedInbox, remaining, handler, async block =>
            {
                await output.WriteLineAsync(Envelope(block));
                await output.FlushAsync();
            }, readByModel);
        }
        catch (Exception) { /* A hook must never fail the turn. */ }
        return 0;
    }

    private static string Envelope(string context) =>
        new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PostToolUse", ["additionalContext"] = context } }.ToJsonString();

    /// <summary>Reads the input to its end, so the client's write never blocks; null when it was too long to keep.</summary>
    private static async Task<string?> ReadBoundedAsync(TextReader input)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        var tooLong = false;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory())) > 0)
        {
            if (tooLong) continue;
            if (text.Length + read > MaxInputChars) { tooLong = true; text.Clear(); continue; }
            text.Append(buffer, 0, read);
        }
        return tooLong ? null : text.ToString();
    }

    /// <summary>
    /// The messages a check_inbox tool result gave the model in full. The tool's reply is <c>{ok, code, data}</c> as JSON text in one MCP text block; how the client wraps that block
    /// in <c>tool_response</c> is not confirmed, so the plausible forms are accepted and anything else finds nothing, which costs one repeat. Only the reply's own items count, and the
    /// text inside a message is never parsed, so a message cannot name another one as already read.
    /// </summary>
    internal static IReadOnlyList<string> MessagesRead(JsonElement response)
    {
        var ids = new List<string>();
        switch (response.ValueKind)
        {
            case JsonValueKind.Array:
                Blocks(response);
                break;
            case JsonValueKind.String:
                ReplyText(response.GetString());
                break;
            case JsonValueKind.Object:
                if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array) Blocks(content);
                else Reply(response);
                break;
        }
        return ids;

        void Blocks(JsonElement blocks)
        {
            foreach (var block in blocks.EnumerateArray()) ReplyText(ChatLocators.Text(block, "text"));
        }

        void ReplyText(string? text)
        {
            if (text is null) return;
            try
            {
                using var reply = JsonDocument.Parse(text);
                Reply(reply.RootElement);
            }
            catch (JsonException) { /* Not a reply this hook understands. */ }
        }

        void Reply(JsonElement reply)
        {
            if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;
            foreach (var item in items.EnumerateArray())
            {
                // A piece of an oversized message, or a header without its text, did not give the model the message.
                if (ChatLocators.Text(item, "text") is null || ChatLocators.Text(item, "messageId") is not { Length: > 0 } id) continue;
                if (item.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True) continue;
                ids.Add(id);
            }
        }
    }
}

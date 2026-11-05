using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>
/// <c>--waiting-hook</c>: reports a Claude chat stopped at a client prompt on "Waiting on you", so the row does not depend on the model remembering to. A <c>Notification</c> of type
/// <c>permission_prompt</c> or a <c>PreToolUse</c> of <c>AskUserQuestion</c> sets the chat's own <c>waitingFor</c> through <c>ack</c>; <c>Stop</c> and <c>PostToolUseFailure</c> clear it here,
/// and <see cref="PromptHook"/> and <see cref="PostToolHook"/> clear it on their events through <see cref="ClearAsync"/>. The hooks reference names no event for an answered prompt, so an
/// allowed tool shows as waiting until it finishes. This hook writes nothing to standard output and always exits 0, so it can never influence the tool call or the turn.
/// The hub keeps a report the model made itself: every call here carries <see cref="RelayLimits.HookWaitingField"/>, and the hub neither replaces nor clears a report without that mark.
/// A marker file per chat records what a hook set, so ordinary turns make no hub call. None of this has been tried on a real client.
/// </summary>
public static class WaitingHook
{
    internal const string QuestionTool = "AskUserQuestion";
    internal const string PermissionText = "Claude Code is waiting for your permission.";
    internal const string QuestionText = "Claude Code asked you a question and is waiting for your answer.";
    private const int MaxToolNameLength = 120;
    /// <summary>How often a report tries for the chat's lock, and how long apart: a parallel hook holds it for one small hub call at most.</summary>
    private const int LockTries = 3;
    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(50);

    public static Task<int> RunAsync(TextReader input, string nodeFolder) => RunAsync(input, nodeFolder, null, chat => LocalWakeConnection.FindForClaude(chat));

    internal static async Task<int> RunAsync(TextReader input, string nodeFolder, HttpMessageHandler? handler, Func<string, string?>? findLocal = null, TimeProvider? clock = null)
    {
        try
        {
            var timeout = PromptHook.Timeout(Environment.GetEnvironmentVariable("COCERN_HOOK_TIMEOUT_MS"));
            var text = await PostToolHook.ReadBoundedAsync(input).WaitAsync(timeout);
            if (text is null) return 0;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (ChatLocators.Text(root, "session_id") is not { } chat || ChatLocators.Text(root, "agent_id") is { Length: > 0 }) return 0;
            switch (ChatLocators.Text(root, "hook_event_name"))
            {
                case "Notification" when ChatLocators.Text(root, "notification_type") == "permission_prompt":
                    await SetAsync(chat, nodeFolder, findLocal, PermissionReport(ChatLocators.Text(root, "tool_name")), timeout, handler, clock);
                    break;
                case "PreToolUse" when ChatLocators.Text(root, "tool_name") == QuestionTool:
                    await SetAsync(chat, nodeFolder, findLocal, QuestionText, timeout, handler, clock);
                    break;
                case "Stop" or "PostToolUseFailure":
                    await ClearAsync(chat, nodeFolder, findLocal, timeout, handler, clock);
                    break;
            }
        }
        catch (Exception) { /* A hook must never trouble the chat: hub down, slow, no node or no handle all end quietly. */ }
        return 0;
    }

    /// <summary>The fixed text, naming the tool only when the input gives a plain tool name. The notification's message is never read, because it could carry command contents.</summary>
    internal static string PermissionReport(string? toolName) =>
        toolName is { Length: > 0 and <= MaxToolNameLength } && toolName.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '_' or '-' or '.' or ':')
            ? $"Claude Code is waiting for your permission to use {toolName}."
            : PermissionText;

    private static async Task SetAsync(string chat, string nodeFolder, Func<string, string?>? findLocal, string text, TimeSpan timeout, HttpMessageHandler? handler, TimeProvider? clock)
    {
        if (Locate(chat, nodeFolder, findLocal) is not { } place || ClaudeWaiter.ConnectionOptions(place.Folder) is not { } options || ClaudeWaiter.FindHandle(place.Folder, place.ThreadId) is not { } handle) return;
        Directory.CreateDirectory(Path.GetDirectoryName(place.Marker)!);
        using var turn = await TakeTurnAsync(place.Lock, LockTries);
        if (turn is null) return;
        var route = WakeState.Key(handle.RepositoryId, handle.SessionId);
        // Written first: a reply that never comes may still have set the report, and a clear that finds nothing there changes nothing.
        WriteMarker(place.Marker, route, (clock ?? TimeProvider.System).GetUtcNow());
        var reply = await AckAsync(options, handle, text, timeout, handler);
        // The hub kept the model's own report, or refused: this hook set nothing, so it has nothing to clear. With no answer at all the marker stays.
        if (reply.Version is not null && !(reply.Ok && reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("waitingFor", out var mark) && mark.ValueKind == JsonValueKind.Object))
            File.Delete(place.Marker);
    }

    /// <summary>
    /// Clears what a hook of this chat set. With no marker it returns at once with no hub call, which is every ordinary turn. Never throws. It goes only to the session the marker
    /// names: a chat that moved on leaves the old report to expire, and an old marker past the report's own limit has nothing left to clear.
    /// </summary>
    internal static async Task ClearAsync(string chat, string nodeFolder, Func<string, string?>? findLocal, TimeSpan timeout, HttpMessageHandler? handler, TimeProvider? clock = null)
    {
        try
        {
            if (Locate(chat, nodeFolder, findLocal) is not { } place || !File.Exists(place.Marker)) return;
            using var turn = await TakeTurnAsync(place.Lock, 1);
            if (turn is null) return;
            var (route, at) = ReadMarker(place.Marker);
            if (route is null || (clock ?? TimeProvider.System).GetUtcNow() - at > TimeSpan.FromHours(RelayLimits.WaitingForMaxHours)
                || ClaudeWaiter.ConnectionOptions(place.Folder) is not { } options || ClaudeWaiter.FindHandle(place.Folder, place.ThreadId) is not { } handle
                || WakeState.Key(handle.RepositoryId, handle.SessionId) != route)
            {
                File.Delete(place.Marker);
                return;
            }
            var reply = await AckAsync(options, handle, "", timeout, handler);
            // Only a clear the hub took, a report it kept for the model, or a session it will never accept again ends it; any other failure leaves it for the next event to retry.
            if (reply.Ok || IsPermanent(reply)) File.Delete(place.Marker);
        }
        catch (Exception) { /* A hook must never trouble the chat. */ }
    }

    /// <summary>The hub answered that this session is closed or not this caller's: no later call can change its report, and closing already dropped it.</summary>
    private static bool IsPermanent(AdapterReply reply) => reply.Version is not null && reply.Code is RelayErrorCodes.SessionDenied or RelayErrorCodes.SessionUnknown;

    private static async Task<AdapterReply> AckAsync(AdapterOptions options, SessionHandle handle, string text, TimeSpan timeout, HttpMessageHandler? handler)
    {
        using var http = new HttpClient(handler ?? RelayForwarder.CreateHandler(options), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(5) };
        var arguments = new JsonObject
        {
            ["repositoryId"] = handle.RepositoryId, ["sessionId"] = handle.SessionId, ["sessionToken"] = handle.SessionToken, ["requestId"] = "waiting-hook-" + Guid.NewGuid().ToString("N"),
            ["waitingFor"] = text, [RelayLimits.HookWaitingField] = true
        };
        using var limit = new CancellationTokenSource(timeout);
        return await new RelayForwarder(http, options, CallOrigins.Node).ForwardAsync(RelayTools.Ack, RelayForwarder.WithOrigin(JsonSerializer.SerializeToElement(arguments), background: true), limit.Token);
    }

    private sealed record Place(string Folder, string ThreadId, string Marker, string Lock);

    /// <summary>Where this chat's marker lives, in the folder its other hooks use. One small file per chat, replaced in place.</summary>
    private static Place? Locate(string chat, string nodeFolder, Func<string, string?>? findLocal)
    {
        if (!ChatLocators.IsUuid(chat)) return null;
        var threadId = Guid.ParseExact(chat, "D").ToString("D");
        var folder = Path.GetFullPath(findLocal?.Invoke(chat) ?? nodeFolder);
        var hooks = Path.Combine(folder, PromptHook.CacheFolder);
        return new(folder, threadId, Path.Combine(hooks, threadId + ".waiting.json"), Path.Combine(hooks, threadId + ".waiting.lock"));
    }

    /// <summary>The right to change this chat's report, or null while a parallel hook keeps it. The operating system releases it even if the process is killed.</summary>
    private static async Task<FileStream?> TakeTurnAsync(string path, int tries)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < tries) { await Task.Delay(LockWait); }
            catch (IOException) { return null; }
        }
    }

    private static void WriteMarker(string path, string route, DateTimeOffset now) =>
        NodeState.WritePrivately(path, System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["route"] = route, ["at"] = now.UtcTicks }.ToJsonString()));

    private static (string? Route, DateTimeOffset At) ReadMarker(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["route"] is JsonValue route && route.TryGetValue<string>(out var text)
                && root["at"] is JsonValue at && at.TryGetValue<long>(out var ticks) && ticks is >= 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks
                ? (text, new DateTimeOffset(ticks, TimeSpan.Zero)) : (null, default);
        }
        catch (JsonException) { return (null, default); }
    }
}

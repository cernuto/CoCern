using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>
/// <c>--waiting-hook</c>: reports a Claude chat stopped at a client prompt on "Waiting on you", so the row does not depend on the model remembering to. A <c>Notification</c> of type
/// <c>permission_prompt</c> or a <c>PreToolUse</c> of <c>AskUserQuestion</c> sets the chat's own <c>waitingFor</c> through <c>ack</c>. A wrongly hidden wait is worse than a row that lingers, so
/// a tool finishing clears only the question it answers: <c>AskUserQuestion</c>'s <c>tool_use_id</c> is recorded and only its own <c>PostToolUse</c> or <c>PostToolUseFailure</c> matches. A permission
/// notification names no tool call, and the hooks reference names no event for an answered prompt, so that row, and a question with no <c>tool_use_id</c>, lasts until <c>UserPromptSubmit</c> or
/// <c>Stop</c>; an allowed tool's row can last until the turn ends. This hook writes nothing to standard output and always exits 0, so it can never influence the tool call or the turn.
/// The hub keeps a report the model made itself: every call here carries <see cref="RelayLimits.HookWaitingField"/>, and the hub neither replaces nor clears a report without that mark.
/// A clear that timed out here may still reach the hub later, and the lock only orders what is sent, so each report has an ID (<see cref="RelayLimits.HookReportIdField"/>) and a clear names the one it
/// means: the hub leaves any other report standing.
/// A question raises that same notification about six seconds later, so one saying exactly <see cref="QuestionNotification"/> repeats the question's report instead of replacing it, and the answer still clears it. The text is only compared, never sent.
/// If a client words it differently, the row lasts until the turn ends, as a permission prompt's does. A marker file per chat records what a hook set, so ordinary turns make no hub call. A Claude Code 2.1.317 desktop chat showed these events; this hook itself has not been tried on a real client.
/// </summary>
public static class WaitingHook
{
    internal const string QuestionTool = "AskUserQuestion";
    /// <summary>The message of the <c>permission_prompt</c> notification an open question raises, as Claude Code 2.1.317 words it.</summary>
    internal const string QuestionNotification = "Claude needs your permission to use " + QuestionTool;
    internal const string PermissionText = "Claude Code is waiting for your permission.";
    internal const string QuestionText = "Claude Code asked you a question and is waiting for your answer.";
    private const int MaxToolNameLength = 120;
    /// <summary>The questions one marker tracks by ID. Past this the row simply lasts until the turn ends, so the file stays small.</summary>
    private const int MaxQuestions = 8;
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
                // Repeated, not skipped: if the question's first report never landed, this one still shows the wait. With no question on record it has no ID to match, so it lasts until the turn ends.
                case "Notification" when ChatLocators.Text(root, "notification_type") == "permission_prompt" && ChatLocators.Text(root, "message") == QuestionNotification:
                    await SetAsync(chat, nodeFolder, findLocal, QuestionText, PendingQuestion(chat, nodeFolder, findLocal), timeout, handler, clock);
                    break;
                case "Notification" when ChatLocators.Text(root, "notification_type") == "permission_prompt":
                    await SetAsync(chat, nodeFolder, findLocal, PermissionReport(ChatLocators.Text(root, "tool_name")), null, timeout, handler, clock);
                    break;
                case "PreToolUse" when ChatLocators.Text(root, "tool_name") == QuestionTool:
                    await SetAsync(chat, nodeFolder, findLocal, QuestionText, ChatLocators.Text(root, "tool_use_id") ?? "", timeout, handler, clock);
                    break;
                case "Stop":
                    await ClearAsync(chat, nodeFolder, findLocal, timeout, handler, clock);
                    break;
                case "PostToolUseFailure":
                    await ToolFinishedAsync(chat, ChatLocators.Text(root, "tool_use_id"), nodeFolder, findLocal, timeout, handler, clock);
                    break;
            }
        }
        catch (Exception) { /* A hook must never trouble the chat: hub down, slow, no node or no handle all end quietly. */ }
        return 0;
    }

    private static string PendingQuestion(string chat, string nodeFolder, Func<string, string?>? findLocal) =>
        Locate(chat, nodeFolder, findLocal) is { } place && ReadMarker(place.Marker) is { Questions.Length: > 0 } marker ? marker.Questions[^1] : "";

    /// <summary>The fixed text, naming the tool only when the input gives a plain tool name. The notification's message is never read, because it could carry command contents.</summary>
    internal static string PermissionReport(string? toolName) =>
        toolName is { Length: > 0 and <= MaxToolNameLength } && toolName.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '_' or '-' or '.' or ':')
            ? $"Claude Code is waiting for your permission to use {toolName}."
            : PermissionText;

    /// <param name="question">The asking call's <c>tool_use_id</c>, empty when the input gave none; null for a permission prompt. Without an ID to match, the row waits for the turn to end.</param>
    private static async Task SetAsync(string chat, string nodeFolder, Func<string, string?>? findLocal, string text, string? question, TimeSpan timeout, HttpMessageHandler? handler, TimeProvider? clock)
    {
        if (Locate(chat, nodeFolder, findLocal) is not { } place || ClaudeWaiter.ConnectionOptions(place.Folder) is not { } options || ClaudeWaiter.FindHandle(place.Folder, place.ThreadId) is not { } handle) return;
        Directory.CreateDirectory(Path.GetDirectoryName(place.Marker)!);
        using var turn = await TakeTurnAsync(place.Lock, LockTries);
        if (turn is null) return;
        var route = WakeState.Key(handle.RepositoryId, handle.SessionId);
        var previous = ReadMarker(place.Marker) is { } read && read.Route == route ? read : null;
        var questions = (previous?.Questions ?? []).ToList();
        var tracked = question is { Length: > 0 } && (questions.Contains(question) || questions.Count < MaxQuestions);
        if (tracked && !questions.Contains(question!)) questions.Add(question!);
        // A marker with nothing pending has had its clear sent, and that clear may still land, so a new report takes a new ID. One still pending has sent no clear, so it keeps its ID,
        // and the hub's report matches this marker whether or not this call lands.
        var reportId = previous is { ReportId: { } current } && (previous.UntilTurnEnd || previous.Questions.Length > 0) ? current : Guid.NewGuid().ToString("N");
        // Written first: a reply that never comes may still have set the report, and a clear that finds nothing there changes nothing.
        WriteMarker(place.Marker, new(route, (clock ?? TimeProvider.System).GetUtcNow().UtcTicks, previous?.UntilTurnEnd == true || !tracked, questions.ToArray(), reportId));
        var reply = await AckAsync(options, handle, text, reportId, timeout, handler);
        if (reply.Ok)
        {
            // Without a mark back, the hub kept the model's own report: nothing of a hook's is there to clear now.
            if (!(reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("waitingFor", out var mark) && mark.ValueKind == JsonValueKind.Object)) File.Delete(place.Marker);
        }
        else if (reply.Version is not null)
        {
            // Refused, so this call set nothing; whatever an earlier hook set is still there and still needs clearing.
            if (previous is null || IsPermanent(reply)) File.Delete(place.Marker);
            else WriteMarker(place.Marker, previous);
        }
    }

    /// <summary>
    /// A new prompt or the end of the turn: whatever a hook of this chat set is over. With no marker it returns at once with no hub call, which is every ordinary turn. Never throws.
    /// </summary>
    internal static Task ClearAsync(string chat, string nodeFolder, Func<string, string?>? findLocal, TimeSpan timeout, HttpMessageHandler? handler, TimeProvider? clock = null) =>
        SettleAsync(chat, nodeFolder, findLocal, marker => marker with { UntilTurnEnd = false, Questions = [] }, timeout, handler, clock);

    /// <summary>
    /// A tool call finished, with or failing: it ends the wait only if it is the question the row is for, and an unrelated parallel call leaves the row alone. No ID, no match. A marker with
    /// nothing pending is a clear that has not reached the hub yet, and any event retries it. Never throws.
    /// </summary>
    internal static Task ToolFinishedAsync(string chat, string? toolUseId, string nodeFolder, Func<string, string?>? findLocal, TimeSpan timeout, HttpMessageHandler? handler, TimeProvider? clock = null) =>
        SettleAsync(chat, nodeFolder, findLocal, marker => marker with { Questions = marker.Questions.Where(question => question != toolUseId).ToArray() }, timeout, handler, clock);

    /// <summary>
    /// Applies one event to the marker and clears the report once nothing is pending. It goes only to the session the marker names: a chat that moved on leaves the old report to expire, and a
    /// marker past the report's own limit has nothing left to clear. The marker goes only when the hub cleared or kept the report, or will never know this session; any other failure leaves
    /// it, with nothing pending, for the next event to retry.
    /// </summary>
    private static async Task SettleAsync(string chat, string nodeFolder, Func<string, string?>? findLocal, Func<Marker, Marker> apply, TimeSpan timeout, HttpMessageHandler? handler, TimeProvider? clock)
    {
        try
        {
            if (Locate(chat, nodeFolder, findLocal) is not { } place || !File.Exists(place.Marker)) return;
            using var turn = await TakeTurnAsync(place.Lock, 1);
            if (turn is null) return;
            if (ReadMarker(place.Marker) is not { } marker || (clock ?? TimeProvider.System).GetUtcNow() - new DateTimeOffset(marker.At, TimeSpan.Zero) > TimeSpan.FromHours(RelayLimits.WaitingForMaxHours)
                || ClaudeWaiter.ConnectionOptions(place.Folder) is not { } options || ClaudeWaiter.FindHandle(place.Folder, place.ThreadId) is not { } handle
                || WakeState.Key(handle.RepositoryId, handle.SessionId) != marker.Route)
            {
                File.Delete(place.Marker);
                return;
            }
            var left = apply(marker);
            if (left.UntilTurnEnd != marker.UntilTurnEnd || left.Questions.Length != marker.Questions.Length) WriteMarker(place.Marker, left);
            if (left.UntilTurnEnd || left.Questions.Length > 0) return;
            // A marker from before report IDs names none, and its report has none either.
            var reply = await AckAsync(options, handle, "", marker.ReportId, timeout, handler);
            if (reply.Ok || IsPermanent(reply)) File.Delete(place.Marker);
        }
        catch (Exception) { /* A hook must never trouble the chat. */ }
    }

    /// <summary>The hub answered that this session is closed or not this caller's: no later call can change its report, and closing already dropped it.</summary>
    private static bool IsPermanent(AdapterReply reply) => reply.Version is not null && reply.Code is RelayErrorCodes.SessionDenied or RelayErrorCodes.SessionUnknown;

    private static async Task<AdapterReply> AckAsync(AdapterOptions options, SessionHandle handle, string text, string? reportId, TimeSpan timeout, HttpMessageHandler? handler)
    {
        using var http = new HttpClient(handler ?? RelayForwarder.CreateHandler(options), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(5) };
        var arguments = new JsonObject
        {
            ["repositoryId"] = handle.RepositoryId, ["sessionId"] = handle.SessionId, ["sessionToken"] = handle.SessionToken, ["requestId"] = "waiting-hook-" + Guid.NewGuid().ToString("N"),
            ["waitingFor"] = text, [RelayLimits.HookWaitingField] = true
        };
        if (reportId is not null) arguments[RelayLimits.HookReportIdField] = reportId;
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

    /// <summary>What a hook of this chat set, on which route, and what still ends it.</summary>
    /// <param name="At">UTC ticks of the latest report.</param>
    /// <param name="UntilTurnEnd">Only the next prompt or the end of the turn can end this wait.</param>
    /// <param name="Questions">The <c>tool_use_id</c>s of the questions still open.</param>
    /// <param name="ReportId">The ID of the report a hook last sent, which a clear names; null in a marker from before report IDs.</param>
    private sealed record Marker(string Route, long At, bool UntilTurnEnd, string[] Questions, string? ReportId);

    private static void WriteMarker(string path, Marker marker) =>
        NodeState.WritePrivately(path, System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["route"] = marker.Route, ["at"] = marker.At, ["untilTurnEnd"] = marker.UntilTurnEnd, ["questions"] = new JsonArray(marker.Questions.Select(question => (JsonNode)question).ToArray()), ["reportId"] = marker.ReportId
        }.ToJsonString()));

    /// <summary>The marker at <paramref name="path"/>, or null when there is none or it cannot be read.</summary>
    private static Marker? ReadMarker(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["route"] is not JsonValue route || !route.TryGetValue<string>(out var text)
                || root["at"] is not JsonValue at || !at.TryGetValue<long>(out var ticks) || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks) return null;
            var untilTurnEnd = root["untilTurnEnd"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
            var questions = root["questions"] is JsonArray list ? list.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var id) ? id : null).OfType<string>().ToArray() : [];
            var reportId = root["reportId"] is JsonValue report && report.TryGetValue<string>(out var named) && named.Length > 0 ? named : null;
            return new(text, ticks, untilTurnEnd, questions, reportId);
        }
        catch (JsonException) { return null; }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PkRelay.Adapter.Node;

/// <summary>
/// <c>--await-inbox --chat &lt;claudeSessionId&gt;</c>: a Claude chat's own inbox waiter on node credentials. Claude chats are never woken by the node;
/// the chat runs this as a background command and is re-invoked when it exits. While it runs, <c>waiters\&lt;threadId&gt;.pid</c> (current-user-only,
/// <c>{ pid, startedAt }</c>) tells the node to announce <c>waiterRunning</c>. It reads the node folder without taking <c>node.lock</c>.
/// </summary>
public static class ClaudeWaiter
{
    internal const string WaitersFolder = "waiters";
    internal const string StopFile = "stop";
    /// <summary>Process start times are compared within this, so a PID reused by a later process never counts as the waiter.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Returns "queued", "timeout" or "stopped" as <see cref="WakeWatcher.AwaitInboxAsync"/> does. Throws <see cref="AdapterConfigurationException"/> when the node is not enrolled or no handle belongs to the chat.</summary>
    public static Task<string> RunAsync(string nodeFolder, string claudeSessionId, Action<string> deliver, TimeSpan? timeout, CancellationToken cancellationToken) =>
        RunAsync(nodeFolder, claudeSessionId, deliver, timeout, cancellationToken, null, null);

    internal static async Task<string> RunAsync(string nodeFolder, string claudeSessionId, Action<string> deliver, TimeSpan? timeout, CancellationToken cancellationToken, HttpMessageHandler? handler, Func<HttpMessageHandler>? hubHandler, TimeProvider? clock = null)
    {
        var folder = Path.GetFullPath(nodeFolder);
        if (!ChatLocators.IsUuid(claudeSessionId)) throw new AdapterConfigurationException("--chat needs this Claude chat's session ID (a UUID).");
        var threadId = Normalize(claudeSessionId);
        var enrollment = NodeState.ReadEnrollment(folder) ?? throw new AdapterConfigurationException($"This PC's node in '{folder}' is not enrolled with a hub yet. Start it once with --node --hub https://<hub-address>:5273/.");
        var handle = FindHandle(folder, threadId) ?? throw new AdapterConfigurationException($"No relay session on this PC belongs to Claude chat {threadId}. Call session_open from that chat first, with PkRelay running on the node's credentials.");
        var options = new AdapterOptions(enrollment.Hub.AbsoluteUri, enrollment.Credential, signalR: true) { PinnedFingerprint = enrollment.Fingerprint };
        var runtime = new WakeRuntime(options, Path.Combine(folder, WaitersFolder, threadId + ".db"), Path.Combine(folder, StopFile), enrollment.Hub.AbsoluteUri) { HubHandler = hubHandler };
        WakeTarget[] targets = [new(new WakeRoute(handle.RepositoryId, "default", NodeState.SessionHandlePath(folder, handle.SessionId), threadId, Environment.MachineName), new WakeSession(handle.SessionId, handle.SessionToken), new InboxWaitDispatcher(deliver))];
        // One waiter per chat: a second one would replace the live waiter's pid file and then fail on its exclusive state.
        if (IsAlive(PidPath(folder, threadId))) throw new AdapterConfigurationException($"An inbox waiter for Claude chat {threadId} is already running on this PC.");
        using var self = Process.GetCurrentProcess();
        var written = WritePid(folder, threadId, self.Id, self.StartTime.ToUniversalTime());
        try { return await WakeWatcher.RunAsync(runtime, _ => Task.FromResult<IReadOnlyList<WakeTarget>>(targets), exitWhenQueued: true, timeout, cancellationToken, handler, clock); }
        finally { RemoveIfUnchanged(PidPath(folder, threadId), written); }
    }

    /// <summary>The handle whose captured or located Claude chat ID is this one; the newest when several are.</summary>
    internal static SessionHandle? FindHandle(string nodeFolder, string claudeSessionId) =>
        NodeState.ReadSessionHandles(nodeFolder).Where(handle => IsClaude(handle.Client, claudeSessionId) || IsClaude(handle.Chat, claudeSessionId)).OrderByDescending(handle => handle.OpenedAt).FirstOrDefault();

    /// <summary>The chats whose waiter is alive now. Any pid file naming a process that has exited, or a later process that reused its ID, is removed.</summary>
    internal static HashSet<string> Running(string nodeFolder)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ChatLocators.Files(Path.Combine(nodeFolder, WaitersFolder), "*.pid", SearchOption.TopDirectoryOnly))
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            var threadId = Path.GetFileNameWithoutExtension(path);
            if (ChatLocators.IsUuid(threadId) && Alive(text)) running.Add(Normalize(threadId));
            else RemoveIfUnchanged(path, text);
        }
        return running;
    }

    /// <summary>Writes the pid file current-user-only and returns its content.</summary>
    internal static string WritePid(string nodeFolder, string threadId, int processId, DateTime startedAtUtc)
    {
        var text = new JsonObject { ["pid"] = processId, ["startedAt"] = startedAtUtc.ToUniversalTime().ToString("O") }.ToJsonString();
        Directory.CreateDirectory(Path.Combine(nodeFolder, WaitersFolder));
        NodeState.WritePrivately(PidPath(nodeFolder, threadId), Encoding.UTF8.GetBytes(text));
        return text;
    }

    private static string PidPath(string nodeFolder, string threadId) => Path.Combine(nodeFolder, WaitersFolder, Normalize(threadId) + ".pid");

    private static string Normalize(string uuid) => Guid.ParseExact(uuid, "D").ToString("D");

    private static bool IsClaude(ChatLocation? location, string claudeSessionId) =>
        location is { Provider: "claude" } && string.Equals(location.ThreadId, claudeSessionId, StringComparison.OrdinalIgnoreCase);

    private static bool IsAlive(string path)
    {
        try { return Alive(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Alive(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || root["pid"] is not JsonValue pid || !pid.TryGetValue<int>(out var id) || id <= 0 ||
                root["startedAt"] is not JsonValue started || !started.TryGetValue<string>(out var startedText) ||
                !DateTime.TryParse(startedText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var startedAt)) return false;
            using var process = Process.GetProcessById(id);
            return !process.HasExited && (process.StartTime.ToUniversalTime() - startedAt.ToUniversalTime()).Duration() < StartTolerance;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or FormatException) { return false; }
    }

    /// <summary>Deletes the file only while it still holds <paramref name="expected"/>, so a waiter that started since is never cleaned up by mistake.</summary>
    private static void RemoveIfUnchanged(string path, string expected)
    {
        try { if (File.ReadAllText(path) == expected) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

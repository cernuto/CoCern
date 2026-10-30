using System.Text;
using System.Text.Json;

namespace PkRelay.Adapter;

/// <summary>
/// Whether a Codex thread is mid-turn, read from the tail of its rollout (<c>sessions\YYYY\MM\DD\rollout-&lt;time&gt;-&lt;threadId&gt;.jsonl</c>):
/// the last <c>task_started</c>, <c>task_complete</c> or <c>turn_aborted</c> event decides. A rollout not written for <see cref="StaleAfter"/>
/// counts as idle, so a crashed or abandoned turn never holds wakes back for long.
/// </summary>
public static class CodexActivity
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private const int TailBytes = 256 * 1024;

    /// <summary>The default sessions folder under the user's profile.</summary>
    public static string DefaultSessionsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public static bool IsBusy(string sessionsRoot, string threadId, DateTimeOffset now)
    {
        try
        {
            if (!Guid.TryParseExact(threadId, "D", out _) || !Directory.Exists(sessionsRoot)) return false;
            var rollout = Directory.EnumerateFiles(sessionsRoot, "rollout-*-" + threadId + ".jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
            if (rollout is null || now.UtcDateTime - rollout.LastWriteTimeUtc > StaleAfter) return false;
            return LastTurnEvent(rollout.FullName) == "task_started";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string? LastTurnEvent(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n');
        // The first line of a partial tail may be cut off; it fails to parse and is skipped like any other unreadable line.
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"event_msg\"", StringComparison.Ordinal) || !(line.Contains("task_started", StringComparison.Ordinal) || line.Contains("task_complete", StringComparison.Ordinal) || line.Contains("turn_aborted", StringComparison.Ordinal))) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "event_msg" &&
                    document.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("type", out var kind) && kind.GetString() is "task_started" or "task_complete" or "turn_aborted")
                    return kind.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }
}

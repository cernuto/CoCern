using System.Text;
using System.Text.Json;

namespace CoCern.Adapter;

/// <summary>
/// Whether a Codex thread is mid-turn, from its rollout (<c>sessions\YYYY\MM\DD\rollout-&lt;time&gt;-&lt;threadId&gt;.jsonl</c>): the last
/// <c>task_started</c>, <c>task_complete</c> or <c>turn_aborted</c> event decides. Each rollout is followed incrementally, reading only what
/// was appended since the last check, so a turn stays busy however much it writes; a fixed tail lost the start of long turns (one turn wrote
/// 10 MB) and let wakes stack behind them. A rollout not written for <see cref="StaleAfter"/> counts as idle, so a crashed or abandoned turn
/// never holds wakes back for long.
/// <para>
/// What is followed, and where each thread's rollout was found, is shared by every instance in the process and bounded, so callers that build a
/// new instance per check still resume each rollout where the last check stopped instead of rereading it from byte 0. Only the date folders a
/// rollout can be written in now (a day either side of today) are listed on each check; the whole sessions tree is searched only for a thread
/// whose rollout has not been found yet, and at most every <see cref="FullSearchEvery"/> for one that is not found.
/// </para>
/// </summary>
public sealed class CodexActivity(string sessionsRoot)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FullSearchEvery = TimeSpan.FromMinutes(5);
    private const int Remembered = 256;
    private static readonly byte[] EventMarker = "\"event_msg\""u8.ToArray();
    private static readonly Lock Gate = new();
    /// <summary>Rollouts being followed, by full path. Guarded by <see cref="Gate"/>.</summary>
    private static readonly Dictionary<string, Followed> FollowedRollouts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where each thread's rollout was last found, by sessions root and thread. Guarded by <see cref="Gate"/>.</summary>
    private static readonly Dictionary<(string Root, string Thread), Located> Locations = new();
    private static long uses;

    /// <summary>The default sessions folder under the user's profile.</summary>
    public static string DefaultSessionsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public bool IsBusy(string threadId, DateTimeOffset now)
    {
        try
        {
            if (!Guid.TryParseExact(threadId, "D", out _) || !Directory.Exists(sessionsRoot)) return false;
            var rollout = Find(threadId, now);
            if (rollout is null || now.UtcDateTime - rollout.LastWriteTimeUtc > StaleAfter) return false;
            lock (Gate)
            {
                if (!FollowedRollouts.TryGetValue(rollout.FullName, out var followed) || rollout.Length < followed.Offset)
                    FollowedRollouts[rollout.FullName] = followed = new(rollout.FullName);
                followed.Used = ++uses;
                Bound(FollowedRollouts, entry => entry.Used);
                followed.Advance();
                return followed.LastTurnEvent == "task_started";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The thread's newest rollout: from the date folders written now, the path it was found at before, or, when neither has one, a full search.</summary>
    private FileInfo? Find(string threadId, DateTimeOffset now)
    {
        var pattern = "rollout-*-" + threadId + ".jsonl";
        var candidates = new List<FileInfo>();
        // A rollout's folder is its thread's start date in local time, which is within a day of today's UTC date.
        for (var offset = -1; offset <= 1; offset++)
        {
            var day = now.UtcDateTime.Date.AddDays(offset);
            var folder = Path.Combine(sessionsRoot, day.Year.ToString("D4"), day.Month.ToString("D2"), day.Day.ToString("D2"));
            if (Directory.Exists(folder)) candidates.AddRange(new DirectoryInfo(folder).EnumerateFiles(pattern));
        }
        var key = (Path.GetFullPath(sessionsRoot), threadId.ToLowerInvariant());
        Located? located;
        lock (Gate) Locations.TryGetValue(key, out located);
        var searchedAt = located?.SearchedAt;
        // A thread started on an earlier day keeps appending to its original rollout, wherever that was found.
        if (located?.Path is { } known && File.Exists(known)) candidates.Add(new FileInfo(known));
        else if (candidates.Count == 0 && (searchedAt is null || now - searchedAt >= FullSearchEvery))
        {
            candidates.AddRange(Directory.EnumerateFiles(sessionsRoot, pattern, SearchOption.AllDirectories).Select(path => new FileInfo(path)));
            searchedAt = now;
        }
        var rollout = candidates.OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        lock (Gate)
        {
            Locations[key] = new(rollout?.FullName ?? (located?.Path is { } path && File.Exists(path) ? path : null), searchedAt ?? DateTimeOffset.MinValue) { Used = ++uses };
            Bound(Locations, entry => entry.Used);
        }
        return rollout;
    }

    /// <summary>Drops the least recently used entry once there are more than <see cref="Remembered"/>. Call under <see cref="Gate"/>.</summary>
    private static void Bound<TKey, TValue>(Dictionary<TKey, TValue> entries, Func<TValue, long> used) where TKey : notnull
    {
        while (entries.Count > Remembered) entries.Remove(entries.MinBy(entry => used(entry.Value)).Key);
    }

    private sealed record Located(string? Path, DateTimeOffset SearchedAt)
    {
        public long Used { get; init; }
    }

    /// <summary>One rollout read up to <see cref="Offset"/>, the end of its last complete line.</summary>
    private sealed class Followed(string path)
    {
        public string Path { get; } = path;
        public long Offset { get; private set; }
        public string? LastTurnEvent { get; private set; }
        public long Used { get; set; }

        public void Advance()
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= Offset) return;
            stream.Seek(Offset, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            using var line = new MemoryStream();
            var position = Offset;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var start = 0;
                for (int newline; (newline = Array.IndexOf(buffer, (byte)'\n', start, read - start)) >= 0; start = newline + 1)
                {
                    line.Write(buffer, start, newline - start);
                    position += line.Length + 1;
                    if (TurnEvent(line) is { } turnEvent) LastTurnEvent = turnEvent;
                    line.SetLength(0);
                }
                line.Write(buffer, start, read - start);
            }
            // A trailing line without its newline is still being written; it is read again, whole, next time.
            Offset = position;
        }

        private static string? TurnEvent(MemoryStream line)
        {
            var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
            if (bytes.IndexOf(EventMarker) < 0) return null;
            var text = Encoding.UTF8.GetString(bytes);
            if (!text.Contains("task_started", StringComparison.Ordinal) && !text.Contains("task_complete", StringComparison.Ordinal) && !text.Contains("turn_aborted", StringComparison.Ordinal)) return null;
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "event_msg" &&
                       document.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                       payload.TryGetProperty("type", out var kind) && kind.GetString() is "task_started" or "task_complete" or "turn_aborted"
                    ? kind.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
    }
}

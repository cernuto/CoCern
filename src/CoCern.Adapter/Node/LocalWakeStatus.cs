using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CoCern.Adapter.Node;

/// <summary>
/// Why local wake is or is not working in this adapter process, as one private file per process: <c>status\&lt;pid&gt;.json</c> under the local-wake root.
/// It holds fixed codes and counts only, never a token, path or message. Nothing here can throw or fail the adapter.
/// </summary>
internal static class LocalWakeStatus
{
    private const string FolderName = "status";
    private const string InvalidCode = "invalid_code";
    private const int MaxCodeLength = 64;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<string, Recorder> Recorders = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Records a fixed reason code (<c>^[a-z_]{1,64}$</c>; anything else is stored as <c>invalid_code</c>) for this process.</summary>
    /// <param name="code">The reason.</param>
    /// <param name="root">The local-wake root; null means <see cref="LocalWakeConnection.DefaultRoot"/>.</param>
    public static void Report(string code, string? root = null) => Use(root, recorder => recorder.Report(IsFixedCode(code) ? code : InvalidCode));

    /// <summary>Records how many sessions this process observes and how many watchers it runs.</summary>
    public static void Counts(int observed, int watchers, string? root = null) => Use(root, recorder => recorder.Counts(Math.Max(observed, 0), Math.Max(watchers, 0)));

    private static void Use(string? root, Action<Recorder> action)
    {
        try
        {
            root ??= LocalWakeConnection.DefaultRoot;
            // A relative root would land in whatever folder the chat runs in.
            if (!Path.IsPathFullyQualified(root)) return;
            action(Recorders.GetOrAdd(Path.GetFullPath(root), static folder => new(Path.Combine(folder, FolderName))));
        }
        catch (Exception) { /* A status file must never be the reason an adapter fails. */ }
    }

    private static bool IsFixedCode(string? code) => code is { Length: > 0 and <= MaxCodeLength } && code.All(c => c is '_' or (>= 'a' and <= 'z'));

    private sealed class Recorder(string folder)
    {
        private readonly Lock gate = new();
        private readonly string path = Path.Combine(folder, Environment.ProcessId + ".json");
        private string? code;
        private DateTimeOffset? codeAt;
        private int observed;
        private int watchers;
        private bool unwritten = true;
        private bool pruned;

        public void Report(string value)
        {
            lock (gate)
            {
                if (code != value) { code = value; codeAt = DateTimeOffset.UtcNow; unwritten = true; }
                WriteIfChanged();
            }
        }

        public void Counts(int sessions, int active)
        {
            lock (gate)
            {
                if ((observed, watchers) != (sessions, active)) { (observed, watchers) = (sessions, active); unwritten = true; }
                WriteIfChanged();
            }
        }

        private void WriteIfChanged()
        {
            if (!unwritten) return;
            Directory.CreateDirectory(folder);
            if (!pruned) { pruned = true; Prune(); }
            NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(new
            {
                pid = Environment.ProcessId,
                processStartedAt = ProcessStartedAt(),
                adapterVersion = typeof(LocalWakeStatus).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                code,
                codeAt,
                observed,
                watchers
            }));
            unwritten = false;
        }

        /// <summary>Keeps the folder bounded: a file goes when its process has ended or after <see cref="MaxAge"/>. A process whose file went writes it again on its next change.</summary>
        private void Prune()
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge || HasEnded(file)) File.Delete(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Tried again by the next adapter that starts. */ }
            }
        }

        private static bool HasEnded(string file)
        {
            if (Path.GetExtension(file) != ".json" || !int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || pid <= 0) return false;
            try { using var process = Process.GetProcessById(pid); return process.HasExited; }
            catch (ArgumentException) { return true; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; } // A process this user may not inspect still runs.
        }

        private static DateTimeOffset? ProcessStartedAt()
        {
            try { using var process = Process.GetCurrentProcess(); return new DateTimeOffset(process.StartTime).ToUniversalTime(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
        }
    }
}

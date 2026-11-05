using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoCern.Adapter.Node;

/// <summary>The reason codes this adapter's wake path reports. Setup reports its own through the same <see cref="LocalWakeStatus.Report"/>.</summary>
internal static class LocalWakeCodes
{
    public const string Starting = "starting", SetupReached = "setup_reached", SetupComplete = "setup_complete", SetupReturnedNull = "setup_returned_null", SetupThrew = "setup_threw", NativeRegistration = "native_registration", NodeConnection = "node_connection", ConnectionReady = "connection_ready",
        Watching = "watching", NoLiveWatcher = "no_live_watcher", NoObservedSessions = "no_observed_sessions", ObservedNotLocated = "observed_not_located", NoCodexTargets = "no_codex_targets", Observed = "observed",
        ObservedUnmapped = "observed_unmapped", NoCodexExecutable = "no_codex_executable", LoopFailed = "loop_failed", WatcherFailed = "watcher_failed", WatcherUnavailable = "watcher_unavailable",
        ObserveHandleMismatch = "observe_handle_mismatch", ObserveFailed = "observe_failed", RestoreLockBusy = "restore_lock_busy", RecordLockBusy = "record_lock_busy",
        ObserveReplyNotOk = "observe_reply_not_ok", ObserveNoReplyData = "observe_no_reply_data", ObserveBadSessionId = "observe_bad_session_id", ObserveNotSessionReply = "observe_not_session_reply", ObserveNotOwned = "observe_not_owned", WatcherLockHeld = "watcher_lock_held", WatcherHandleChanged = "watcher_handle_changed", RearmHubUnavailable = "rearm_hub_unavailable", RearmFailed = "rearm_failed", RearmRecordFailed = "rearm_record_failed", RearmOwnerNotLauncher = "rearm_owner_not_launcher",
        RearmSkipNoOwner = "rearm_skip_no_owner", RearmSkipNotCodex = "rearm_skip_not_codex", RearmSkipNotOwned = "rearm_skip_not_owned", RearmSkipNotArmedHere = "rearm_skip_not_armed_here", RearmSkipUnlocated = "rearm_skip_unlocated", RearmSkipStale = "rearm_skip_stale", RearmSkipRefused = "rearm_skip_refused", RearmSkipTransient = "rearm_skip_transient", RearmSkipOlderSession = "rearm_skip_older_session";
}

/// <summary>
/// Why local wake is or is not working in this adapter process, as one private file per process: <c>status\&lt;pid&gt;.json</c> under the local-wake root.
/// It holds fixed codes and counts only, never a token, path or message. Nothing here can throw or fail the adapter.
/// </summary>
internal static class LocalWakeStatus
{
    private const string FolderName = "status";
    private const string InvalidCode = "invalid_code";
    private const int MaxCodeLength = 64;
    private const string StartingState = "starting", Healthy = "healthy", Degraded = "degraded", Off = "off", NotApplicable = "not_applicable";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<string, Recorder> Recorders = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Records a fixed reason code (<c>^[a-z_]{1,64}$</c>; anything else is stored as <c>invalid_code</c>) for this process.</summary>
    /// <param name="code">The reason.</param>
    /// <param name="root">The local-wake root; null means <see cref="LocalWakeConnection.DefaultRoot"/>.</param>
    public static void Report(string code, string? root = null) => Use(root, recorder => recorder.Report(IsFixedCode(code) ? code : InvalidCode));

    /// <summary>Records how many sessions this process observes and how many watchers it runs.</summary>
    public static void Counts(int observed, int watchers, string? root = null) => Use(root, recorder => recorder.Counts(Math.Max(observed, 0), Math.Max(watchers, 0)));

    /// <summary>The state and reason a chat should be told about, or null while local wake is healthy, starting, not this adapter's job, or never reported.</summary>
    internal static (string State, string Reason)? Problem(string? root = null) => Existing(root)?.Problem;

    internal static string? LastCode(string? root = null) => Existing(root)?.Code;

    /// <summary>Sends this root's state-change lines to <paramref name="error"/> instead of standard error.</summary>
    internal static void UseError(string root, TextWriter error) => Use(root, recorder => recorder.Error = error);

    /// <summary>Adds <c>localWake { state, reason }</c> to a reply while there is a problem; a healthy adapter spends nothing on it.</summary>
    internal static AdapterReply AddTo(AdapterReply reply, string? root = null)
    {
        if (Problem(root) is not { } problem || reply.Data is not { ValueKind: JsonValueKind.Object } data) return reply;
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        node["localWake"] = new JsonObject { ["state"] = problem.State, ["reason"] = problem.Reason };
        return reply with { Data = JsonSerializer.SerializeToElement(node) };
    }

    /// <summary>The state a code puts local wake in; null for a routine exit, which is recorded but changes nothing. A code not listed, such as a setup failure, means no local wake.</summary>
    private static string? StateOf(string code) => code switch
    {
        LocalWakeCodes.Starting or LocalWakeCodes.SetupReached or LocalWakeCodes.SetupComplete or LocalWakeCodes.ConnectionReady => StartingState,
        LocalWakeCodes.NativeRegistration or LocalWakeCodes.NodeConnection => NotApplicable,
        LocalWakeCodes.Watching or LocalWakeCodes.NoLiveWatcher or LocalWakeCodes.NoObservedSessions or LocalWakeCodes.ObservedNotLocated or LocalWakeCodes.NoCodexTargets or LocalWakeCodes.Observed => Healthy,
        LocalWakeCodes.ObservedUnmapped or LocalWakeCodes.NoCodexExecutable or LocalWakeCodes.LoopFailed or LocalWakeCodes.WatcherFailed or LocalWakeCodes.WatcherUnavailable or
            LocalWakeCodes.ObserveHandleMismatch or LocalWakeCodes.ObserveFailed or LocalWakeCodes.RestoreLockBusy or LocalWakeCodes.RecordLockBusy => Degraded,
        LocalWakeCodes.ObserveReplyNotOk or LocalWakeCodes.ObserveNoReplyData or LocalWakeCodes.ObserveBadSessionId or LocalWakeCodes.ObserveNotSessionReply or LocalWakeCodes.ObserveNotOwned or
            LocalWakeCodes.WatcherLockHeld or LocalWakeCodes.WatcherHandleChanged or LocalWakeCodes.RearmHubUnavailable or LocalWakeCodes.RearmFailed or LocalWakeCodes.RearmRecordFailed or LocalWakeCodes.RearmOwnerNotLauncher or
            LocalWakeCodes.RearmSkipNoOwner or LocalWakeCodes.RearmSkipNotCodex or LocalWakeCodes.RearmSkipNotOwned or LocalWakeCodes.RearmSkipNotArmedHere or LocalWakeCodes.RearmSkipUnlocated or
            LocalWakeCodes.RearmSkipStale or LocalWakeCodes.RearmSkipRefused or LocalWakeCodes.RearmSkipTransient or LocalWakeCodes.RearmSkipOlderSession => null,
        _ => Off
    };

    private static string Key(string? root)
    {
        root ??= LocalWakeConnection.DefaultRoot;
        return Path.IsPathFullyQualified(root) ? Path.GetFullPath(root) : root;
    }

    private static Recorder? Existing(string? root)
    {
        try { return Recorders.TryGetValue(Key(root), out var recorder) ? recorder : null; }
        catch (Exception) { return null; }
    }

    private static void Use(string? root, Action<Recorder> action)
    {
        try
        {
            // A relative root would land in whatever folder the chat runs in, so it keeps the state in memory only.
            action(Recorders.GetOrAdd(Key(root), static key => new(Path.IsPathFullyQualified(key) ? Path.Combine(key, FolderName) : null)));
        }
        catch (Exception) { /* A status file must never be the reason an adapter fails. */ }
    }

    private static bool IsFixedCode(string? code) => code is { Length: > 0 and <= MaxCodeLength } && code.All(c => c is '_' or (>= 'a' and <= 'z'));

    private sealed class Recorder(string? folder)
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
        private string state = StartingState;
        private string? stateReason;
        private DateTimeOffset? stateAt;
        private string? code;
        private DateTimeOffset? codeAt;
        private bool setupReached;
        private int observed;
        private DateTimeOffset? observedAt;
        private int watchers;
        private DateTimeOffset? watchersAt;
        private bool unwritten = true;
        private bool pruned;
        private bool warned;

        public TextWriter? Error { get; set; }
        public string? Code { get { lock (gate) return code; } }
        public (string State, string Reason)? Problem { get { lock (gate) return state is Degraded or Off && stateReason is not null ? (state, stateReason) : null; } }

        public void Report(string value)
        {
            lock (gate)
            {
                var next = StateOf(value);
                // The loop repeats its reason every pass; that is no change, and it must not bury the last routine exit.
                if (next is not null && value == stateReason) { WriteIfChanged(); return; }
                var now = DateTimeOffset.UtcNow;
                counts[value] = counts.GetValueOrDefault(value) + 1;
                setupReached |= value is not (LocalWakeCodes.Starting or LocalWakeCodes.NativeRegistration);
                if (code != value) { code = value; codeAt = now; unwritten = true; }
                if (next is not null)
                {
                    stateReason = value;
                    unwritten = true;
                    stateAt ??= now;
                    if (next != state) { state = next; stateAt = now; Say($"CoCern local wake: {next} ({value})."); }
                }
                WriteIfChanged();
            }
        }

        public void Counts(int sessions, int active)
        {
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (observed != sessions) { observed = sessions; observedAt = now; unwritten = true; }
                if (watchers != active) { watchers = active; watchersAt = now; unwritten = true; }
                WriteIfChanged();
            }
        }

        private void WriteIfChanged()
        {
            if (!unwritten) return;
            try
            {
                if (folder is null) throw new IOException();
                var path = Path.Combine(folder, Environment.ProcessId + ".json");
                Directory.CreateDirectory(folder);
                if (!pruned) { pruned = true; Prune(path); }
                NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    pid = Environment.ProcessId,
                    processStartedAt = ProcessStartedAt(),
                    adapterVersion = typeof(LocalWakeStatus).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                    setupReached,
                    state,
                    stateReason,
                    stateAt,
                    code,
                    codeAt,
                    observed,
                    observedAt,
                    watchers,
                    watchersAt,
                    codeCounts = counts
                }));
                unwritten = false;
            }
            catch (Exception)
            {
                if (!warned) { warned = true; Say("CoCern local wake status could not be written; local wake is not affected."); }
            }
        }

        private void Say(string line)
        {
            try { (Error ?? Console.Error).WriteLine(line); }
            catch (Exception) { /* A closed standard error must not stop the adapter either. */ }
        }

        /// <summary>Keeps the folder bounded: a file goes when its process has ended or after <see cref="MaxAge"/>. A process whose file went writes it again on its next change.</summary>
        private void Prune(string own)
        {
            foreach (var file in Directory.EnumerateFiles(folder!))
            {
                try
                {
                    if (string.Equals(file, own, StringComparison.OrdinalIgnoreCase)) continue;
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

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>Uses the MCP process's own principal and lifetime to wake its local Codex chats; no machine enrollment or second daemon.</summary>
public sealed class LocalChatWake : IAsyncDisposable
{
    private readonly NodeTransport transport;
    private readonly NodeChats chats;
    private readonly SessionHandoff handoff;
    private readonly ConcurrentDictionary<string, byte> observed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Func<WakeRuntime, WakeTarget, CancellationToken, Task> watch;
    private Task? running;
    private readonly string? statusRoot;
    private readonly TaskCompletionSource firstPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HttpMessageHandler? hub;
    private readonly ArmedChats armed;
    private readonly HashSet<string> rearmRefused = new(StringComparer.Ordinal);
    private readonly HashSet<(string Session, string Code)> rearmReported = [];
    private DateTimeOffset? rearmAt = DateTimeOffset.MinValue;
    private Task? rearming;
    private string? observeProblem;
    private string? watchFailure;
    internal LocalWakeConnection Connection { get; }
    /// <summary>Completes when the loop has finished a pass that included the first re-arm attempt's result.</summary>
    internal Task FirstPass => firstPass.Task;
    /// <summary>Whether a re-arm attempt is still due or scheduled.</summary>
    internal bool RearmPending => rearmAt is not null;
    /// <summary>How recently a chat must have called the hub for a restarted adapter to watch it again, so abandoned chats are not woken. A handle is touched at most hourly.</summary>
    internal static readonly TimeSpan RearmWindow = TimeSpan.FromHours(24);
    /// <summary>How long a restarted adapter waits before trying again when the hub could not be reached, as when both restart together, or a chat it armed may yet qualify.</summary>
    internal static readonly TimeSpan RearmRetry = TimeSpan.FromSeconds(60);

    internal LocalChatWake(LocalWakeConnection connection, NodeTransport? transport = null, Func<WakeRuntime, WakeTarget, CancellationToken, Task>? watch = null, string? statusRoot = null, HttpMessageHandler? hub = null, ArmedChats? armed = null)
    {
        // Beside the connection folders, so a status file always lands in the root this connection itself lives under.
        this.statusRoot = statusRoot ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(connection.Folder));
        this.hub = hub;
        this.armed = armed ?? ArmedChats.ForThisProcess(connection.Folder);
        Connection = connection;
        this.transport = transport ?? new();
        chats = new(connection.Folder, this.transport.Locators, this.transport.Clock, NodeChats.DefaultLocateWindow, NodeChats.DefaultBudget);
        handoff = SessionHandoff.ForFolder(connection.Folder, connection.Options.ExportedChat);
        handoff.Report = ObserveProblem;
        this.watch = watch ?? ((runtime, target, ct) => WakeWatcher.RunAsync(runtime, _ => Task.FromResult<IReadOnlyList<WakeTarget>>([target]), false, null, ct, clock: this.transport.Clock));
    }

    public static LocalChatWake? TryCreate(AdapterOptions options, string? tokenFile, string? cacheRoot = null, Action<string>? report = null)
    {
        report ??= code => LocalWakeStatus.Report(code, cacheRoot);
        try { return LocalWakeConnection.Create(options, tokenFile, cacheRoot, report) is { } connection ? new(connection) : null; }
        catch (IOException) { report?.Invoke(LocalWakeCodes.SetupIoError); return null; }
        catch (UnauthorizedAccessException) { report?.Invoke(LocalWakeCodes.SetupAccessDenied); return null; }
        catch (ArgumentException) { report?.Invoke(LocalWakeCodes.SetupInvalidPath); return null; }
    }

    /// <summary>The adapter's way in to local wake: says why there is none, and leaves <see cref="TryCreate"/> to say why setup itself refused.</summary>
    internal static LocalChatWake? Open(AdapterOptions options, string? tokenFile, NativeRegistration? registration, string? cacheRoot = null, Action<string>? report = null,
        Func<LocalWakeConnection, LocalChatWake>? create = null)
    {
        report ??= code => LocalWakeStatus.Report(code, cacheRoot);
        string? last = null;
        void Record(string code) { last = code; report(code); }
        if (options.NodeFolder is not null) { Record(LocalWakeCodes.NodeConnection); return null; }
        if (registration is not null)
        {
            // A registered local client already owns an exact private connection. Do not depend on an inherited token-file variable,
            // create another cache folder, or let an enrolled node adopt this local credential's native generations.
            var identity = NativeJoinBinding.CredentialDigest("local", options.BaseUri.AbsoluteUri, options.PinnedFingerprint, options.Token);
            if (!registration.StillCurrent() || LocalWakeConnection.NativeCredentialIdentity(registration.Folder) != identity ||
                LocalWakeConnection.Read(registration.Folder) is not { } connection)
            { Record(LocalWakeCodes.CredentialMismatch); return null; }
            Record(LocalWakeCodes.ConnectionReady);
            return create is null ? new(connection) : create(connection);
        }
        Record(LocalWakeCodes.SetupReached);
        LocalChatWake? wake;
        try { wake = TryCreate(options, tokenFile, cacheRoot, Record); }
        catch (Exception) { Record(LocalWakeCodes.SetupThrew); throw; }
        if (SetupOutcome(wake, last) is { } outcome) Record(outcome);
        return wake;
    }

    /// <summary>Three startup attempts for transient file I/O only, before the adapter starts serving calls.</summary>
    internal static Task<LocalChatWake?> OpenAsync(AdapterOptions options, string? tokenFile, NativeRegistration? registration, string? cacheRoot = null, CancellationToken ct = default,
        Func<LocalWakeConnection, LocalChatWake>? create = null) =>
        RetryStartupAsync(() =>
        {
            string? reason = null;
            var wake = Open(options, tokenFile, registration, cacheRoot, code => { reason = code; LocalWakeStatus.Report(code, cacheRoot); }, create);
            return (wake, reason);
        }, Task.Delay, ct);

    internal static async Task<LocalChatWake?> RetryStartupAsync(Func<(LocalChatWake? Wake, string? Reason)> attempt,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        for (var index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (wake, reason) = attempt();
            if (wake is not null || index == 2 || reason is not (LocalWakeCodes.SetupIoError or LocalWakeCodes.TokenFileMissing)) return wake;
            // Keep the same options and credential expectation; a mismatch or access refusal is terminal.
            await delay(TimeSpan.FromMilliseconds(index == 0 ? 250 : 750), ct);
        }
    }

    /// <summary>What to report after setup: nothing when setup already gave its own reason, which is more specific than this.</summary>
    internal static string? SetupOutcome(LocalChatWake? wake, string? last) => last != LocalWakeCodes.SetupReached ? null : wake is null ? LocalWakeCodes.SetupReturnedNull : LocalWakeCodes.SetupComplete;

    /// <summary>Only a successful session-authenticated reply may restore a pre-existing handle after reconnect.</summary>
    public void Observe(string tool, JsonElement arguments, AdapterReply reply)
    {
        if (!reply.Ok) { Report(LocalWakeCodes.ObserveReplyNotOk); return; }
        try
        {
            string? session;
            if (tool == RelayTools.SessionOpen)
            {
                if (reply.Data is not { ValueKind: JsonValueKind.Object } data) { Report(LocalWakeCodes.ObserveNoReplyData); return; }
                session = ChatLocators.Text(data, "sessionId");
                if (session is null || !NodeState.IsFileId(session)) { Report(LocalWakeCodes.ObserveBadSessionId); return; }
                handoff.RecordLocal(arguments, reply);
                var prior = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, session));
                if (prior is null || prior.RepositoryId != ChatLocators.Text(arguments, "repositoryId") || prior.SessionToken != ChatLocators.Text(data, "sessionToken")) { ObserveProblem(LocalWakeCodes.ObserveHandleMismatch); return; }
            }
            else
            {
                if (!SessionHandoff.IsSessionReply(tool, arguments, reply)) { Report(LocalWakeCodes.ObserveNotSessionReply); return; }
                handoff.Restore(tool, arguments, reply);
                session = ChatLocators.Text(arguments, "sessionId");
                if (session is null || !NodeState.IsFileId(session)) { Report(LocalWakeCodes.ObserveBadSessionId); return; }
                var handle = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, session));
                if (handle is null || handle.RepositoryId != ChatLocators.Text(arguments, "repositoryId") || handle.SessionToken != ChatLocators.Text(arguments, "sessionToken")) { ObserveProblem(LocalWakeCodes.ObserveHandleMismatch); return; }
                if (tool is RelayTools.SessionBind or RelayTools.SessionLabelSet) handoff.Rename(arguments, reply.Version);
            }
            var current = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, session));
            if (current is null) { ObserveProblem(LocalWakeCodes.ObserveHandleMismatch); return; }
            if (!OwnsNative(current)) { Report(LocalWakeCodes.ObserveNotOwned); return; }
            // A chat of another provider is woken its own way, so its adapter has nothing to re-arm.
            if (observed.TryAdd(session, 0) && Connection.Options.ExportedChat is not { Provider: not ChatProviders.Codex } && !armed.Record(session)) Report(LocalWakeCodes.RearmRecordFailed);
            if (Interlocked.Exchange(ref observeProblem, null) is not null) Report(LocalWakeCodes.Observed);
            changed.Writer.TryWrite(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { ObserveProblem(LocalWakeCodes.ObserveFailed); }
    }

    private void Report(string code) => LocalWakeStatus.Report(code, statusRoot);

    /// <summary>A failed call that may leave this chat with no watcher. It stays the reason while no session is observed, however many passes run, until a call succeeds.</summary>
    private void ObserveProblem(string code) { Volatile.Write(ref observeProblem, code); Report(code); }

    /// <summary>The loop's reason for the pass it just finished. A failed watcher counts once, so one that recovers clears it.</summary>
    private string PassReason(SessionHandle[] targets, bool noCodex, int live)
    {
        if (Interlocked.Exchange(ref watchFailure, null) is { } failure) return failure;
        if (noCodex) return LocalWakeCodes.NoCodexExecutable;
        if (live > 0) return LocalWakeCodes.Watching;
        if (targets.Length > 0) return LocalWakeCodes.NoLiveWatcher; // Another adapter holds the chat's lock, or the handle just changed; the next pass tries again.
        if (observed.IsEmpty) return Volatile.Read(ref observeProblem) ?? LocalWakeCodes.NoObservedSessions;
        // A chat of another provider is woken its own way, and this loop has nothing to do for it.
        if (Connection.Options.ExportedChat is { Provider: not ChatProviders.Codex }) return LocalWakeCodes.NoCodexTargets;
        var unlocated = NodeState.ReadSessionHandles(Connection.Folder).Where(x => observed.ContainsKey(x.SessionId) && x.Chat is null).ToArray();
        return unlocated.Any(x => x.Unmapped) ? LocalWakeCodes.ObservedUnmapped : unlocated.Length > 0 ? LocalWakeCodes.ObservedNotLocated : LocalWakeCodes.NoCodexTargets;
    }

    /// <summary>This chat's newest session in a repository among its local handles; see <see cref="SessionHandoff.FindOwn"/>.</summary>
    internal SessionHandle? FindOwn(string repository) => handoff.FindOwn(repository);

    /// <summary>The only repository this chat has local sessions in; see <see cref="SessionHandoff.OwnRepository"/>.</summary>
    internal string? OwnRepository() => handoff.OwnRepository();

    public Task RunAsync(CancellationToken ct) => running ??= LoopAsync(ct);

    /// <summary>
    /// <c>observed</c> lives in memory, so after a backend swap an idle chat has no watcher until it next calls the hub. This re-arms only the chats this owner armed
    /// (<see cref="ArmedChats"/>), located and active within <see cref="RearmWindow"/>, each only after one session-authenticated background read succeeds: the same rule
    /// as <see cref="Observe"/>, and the read neither touches the handle nor writes anything. A chat merely touched, located or armed by another owner is left alone, since
    /// an unexpected turn in a thread someone left is the worse failure. It keeps trying on <see cref="RearmRetry"/> while a chat it armed may yet qualify: not located yet, or a refusal that passes; the window ends that. Old and new backends of one owner may overlap; the exclusive state file (<see cref="OpenState"/>) lets one dispatch.
    /// </summary>
    internal async Task RearmAsync(CancellationToken ct)
    {
        var now = transport.Clock.GetUtcNow();
        if (rearmAt is not { } due || now < due) return;
        rearmAt = now + RearmRetry;
        if (armed.OwnerFallback is { } fallback) ReportRearm("", fallback);
        var retry = false;
        armed.Prune(session => File.Exists(NodeState.SessionHandlePath(Connection.Folder, session)));
        var skips = new List<(string Session, string Code)>();
        var pending = new List<SessionHandle>();
        var handles = NodeState.ReadSessionHandles(Connection.Folder);
        // A chat already watched through its newer session must not gain a second watcher through an older one that NodeChats could not delete. Thread IDs compare as NodeChats.Current does.
        var threads = handles.Where(x => observed.ContainsKey(x.SessionId) && x.Chat is { Provider: ChatProviders.Codex }).Select(x => x.Chat!.ThreadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in handles.Where(x => !observed.ContainsKey(x.SessionId)).OrderByDescending(x => x.OpenedAt))
        {
            if (RearmSkip(handle, now) is { } skip)
            {
                skips.Add((handle.SessionId, skip));
                // A handle not located yet may still be; unmapped is final.
                retry |= skip == LocalWakeCodes.RearmSkipUnlocated && handle is { Chat: null, Unmapped: false };
            }
            else if (threads.Add(handle.Chat!.ThreadId)) pending.Add(handle);
            else skips.Add((handle.SessionId, LocalWakeCodes.RearmSkipOlderSession));
        }
        // In order, so a run of one reason costs one status write.
        foreach (var skip in skips.OrderBy(x => x.Code, StringComparer.Ordinal)) ReportRearm(skip.Session, skip.Code);
        LocalWakeStatus.WriteCounts(statusRoot);
        if (pending.Count == 0) { if (!retry) rearmAt = null; return; }
        using var http = new HttpClient(hub ?? RelayForwarder.CreateHandler(Connection.Options), disposeHandler: hub is null) { Timeout = TimeSpan.FromSeconds(15) };
        var forwarder = new RelayForwarder(http, Connection.Options, CallOrigins.WakeWatcher);
        foreach (var handle in pending)
        {
            var reply = await forwarder.ProbeHandleAsync(handle, ct);
            // No version means the hub never answered, which says nothing about this session, and the rest would only wait out the same timeout.
            if (reply is { Ok: false, Version: null }) { Report(LocalWakeCodes.RearmHubUnavailable); return; }
            if (reply.Ok && reply.Version is not null && SessionHandoff.IsSessionReply(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { sessionId = handle.SessionId }), reply))
                observed.TryAdd(handle.SessionId, 0);
            else if (!reply.Ok && reply.Code is RelayErrorCodes.RateLimited or RelayErrorCodes.Busy or RelayErrorCodes.Paused) { retry = true; ReportRearm(handle.SessionId, LocalWakeCodes.RearmSkipTransient); }
            else { rearmRefused.Add(handle.SessionId); ReportRearm(handle.SessionId, LocalWakeCodes.RearmSkipRefused); }
        }
        if (!retry) rearmAt = null;
    }

    /// <summary>Each session's reason counts once, however many attempts find it again.</summary>
    private void ReportRearm(string session, string code) { if (rearmReported.Add((session, code))) Report(code); }

    /// <summary>Why a handle is not re-armed, or null when it may be, once its read succeeds.</summary>
    private string? RearmSkip(SessionHandle handle, DateTimeOffset now)
    {
        if (!armed.HasOwner) return LocalWakeCodes.RearmSkipNoOwner;
        if (handle.Chat is { Provider: not ChatProviders.Codex }) return LocalWakeCodes.RearmSkipNotCodex;
        if (!OwnsNative(handle)) return LocalWakeCodes.RearmSkipNotOwned;
        if (!armed.IsArmedHere(handle.SessionId)) return LocalWakeCodes.RearmSkipNotArmedHere;
        // Before unlocated, so the window ends the retries for a chat that never gets located.
        if (now - new DateTimeOffset(File.GetLastWriteTimeUtc(NodeState.SessionHandlePath(Connection.Folder, handle.SessionId)), TimeSpan.Zero) > RearmWindow) return LocalWakeCodes.RearmSkipStale;
        if (handle.Chat is null || handle.Unmapped) return LocalWakeCodes.RearmSkipUnlocated;
        return rearmRefused.Contains(handle.SessionId) ? LocalWakeCodes.RearmSkipRefused : null;
    }

    /// <summary>Runs a due attempt beside the loop: a slow hub read (up to 15 seconds each) must not hold up the watchers of chats already observed. Its end wakes the loop to start any it re-armed.</summary>
    private void StartRearm(CancellationToken ct)
    {
        if (rearming is { IsCompleted: false } || rearmAt is not { } due || transport.Clock.GetUtcNow() < due) return;
        rearming = Task.Run(async () =>
        {
            try { await RearmAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception) { Report(LocalWakeCodes.RearmFailed); }
            changed.Writer.TryWrite(true);
        }, CancellationToken.None);
    }

    /// <summary>The pass interval while the sessions folder is watched for changes and nothing is being located: a change starts a pass at once.</summary>
    internal static readonly TimeSpan QuietPass = TimeSpan.FromSeconds(30);
    /// <summary>The pass interval otherwise: while a session is still being located (its transcript line may land after the reply), or without a change signal.</summary>
    internal static readonly TimeSpan BusyPass = TimeSpan.FromSeconds(4);
    /// <summary>The first retry of a chat whose watcher another adapter holds; each further miss doubles it up to <see cref="HeldRetryLimit"/>, each jittered.</summary>
    internal static readonly TimeSpan HeldRetryFirst = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan HeldRetryLimit = TimeSpan.FromSeconds(60);

    /// <summary>When to try a chat again whose watcher another adapter holds, after <paramref name="misses"/> attempts in a row: ±20% around a doubling delay.</summary>
    internal static TimeSpan HeldRetry(int misses)
    {
        var delay = Math.Min(HeldRetryLimit.TotalMilliseconds, HeldRetryFirst.TotalMilliseconds * Math.Pow(2, Math.Clamp(misses - 1, 0, 8)));
        return TimeSpan.FromMilliseconds(delay * (0.8 + 0.4 * Random.Shared.NextDouble()));
    }

    private volatile bool sessionsWatchFailed;

    /// <summary>
    /// Starts a pass as soon as a session handle or native binding changes on disk, from this or any other process, so the loop can wait
    /// <see cref="QuietPass"/> between passes without waking a chat any later. Null when the folder cannot be watched; the loop then keeps <see cref="BusyPass"/>.
    /// </summary>
    private FileSystemWatcher? WatchSessions()
    {
        FileSystemWatcher? watcher = null;
        try
        {
            // Events name paths under the watched spelling, so the comparison below uses the same one.
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Connection.Folder));
            var sessions = Path.Combine(folder, NodeState.SessionsFolder);
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            // Handles sit in sessions\, bindings in sessions\native and sessions\native-chats. Wake databases, call fences and status files are not passes' business.
            bool Relevant(string? path)
            {
                if (path is null || Path.GetDirectoryName(path) is not { } directory) return false;
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".unknown", StringComparison.OrdinalIgnoreCase)) return false;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return string.Equals(directory, sessions, comparison) || string.Equals(Path.GetDirectoryName(directory), sessions, comparison) &&
                    Path.GetFileName(directory) is { } name && (name.Equals("native", comparison) || name.Equals("native-chats", comparison));
            }
            void Changed(object? _, FileSystemEventArgs change) { if (Relevant(change.FullPath) || change is RenamedEventArgs renamed && Relevant(renamed.OldFullPath)) changed.Writer.TryWrite(true); }
            watcher.Created += Changed; watcher.Changed += Changed; watcher.Deleted += Changed; watcher.Renamed += (sender, change) => Changed(sender, change);
            // A lost notification (a full buffer) or a watcher that stopped: pass now, and keep the short interval from here on.
            watcher.Error += (_, _) => { sessionsWatchFailed = true; changed.Writer.TryWrite(true); };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { watcher?.Dispose(); return null; }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        var ending = lifetime.Token;
        var active = new Dictionary<string, (string Session, CancellationTokenSource Stop, Task Task)>();
        // Chats whose watcher another adapter holds: when to try again (Environment.TickCount64), and how many tries in a row have missed. Not retried every pass.
        var held = new Dictionary<string, (long Due, int Misses)>(StringComparer.Ordinal);
        using var sessionsWatch = WatchSessions();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var settled = true;
                var busy = true;
                try
                {
                    // The existing bounded locator verifies native identity before a Codex route is used. A handle still being located keeps passes short.
                    var locating = chats.Locate(_ => true);
                    StartRearm(lifetime.Token);
                    settled = rearming is null or { IsCompleted: true };
                    // One observation per pass, shared by every target below. The snapshot holds at most one native binding per chat: of a thread that ran in
                    // several checkouts, the one confirmed most recently. So a native thread is one target, a legacy handle never joins a thread that has a
                    // native binding, and a group that still mixes them is skipped.
                    // A binding a join holds right now is left out of the snapshot. Its chat is neither started nor stopped this pass (a join that confirms a
                    // live session touches the state while it holds the lease), and the next pass comes at the short interval, once the join has let go.
                    var snapshot = NativeJoinBinding.DispatchSnapshot(Connection.Folder, out var joining);
                    var native = snapshot.Where(x => x.Chat is { Provider: ChatProviders.Codex }).ToArray();
                    var targets = native.Concat(NodeState.ReadSessionHandles(Connection.Folder).Where(x => observed.ContainsKey(x.SessionId) && OwnsNative(x) &&
                        x.Chat is { Provider: ChatProviders.Codex } && !native.Any(n => n.Chat == x.Chat))).GroupBy(x => x.Chat!.ThreadId)
                        .Where(group => group.Count() == 1 || group.All(x => x.NativeBindingKey is null)).SelectMany(group => group)
                        .Where(x => !NativeJoinBinding.IsJoining(joining, x.Chat!.ThreadId, anyChat: false)).ToArray();
                    var ended = false;
                    foreach (var gone in active.Keys.Where(key => active[key].Task.IsCompleted ||
                        !targets.Any(x => x.Chat!.ThreadId == key && x.SessionId == active[key].Session) && !NativeJoinBinding.IsJoining(joining, key, anyChat: true)).ToArray())
                    { var previous = active[gone]; ended |= previous.Task.IsCompleted; previous.Stop.Cancel(); await previous.Task; previous.Stop.Dispose(); active.Remove(gone); }
                    foreach (var gone in held.Keys.Where(thread => !targets.Any(x => x.Chat!.ThreadId == thread) && !NativeJoinBinding.IsJoining(joining, thread, anyChat: true)).ToArray()) held.Remove(gone);
                    var noCodex = false;
                    var looked = false;
                    string? codex = null;
                    var now = Environment.TickCount64;
                    foreach (var handle in targets)
                    {
                        var thread = handle.Chat!.ThreadId;
                        if (active.ContainsKey(thread) || held.TryGetValue(thread, out var wait) && now < wait.Due) continue;
                        if (!looked) { codex = transport.CodexExecutable(); looked = true; }
                        if (codex is null) { noCodex = true; continue; }
                        // The exclusive state is taken before anything else is read: a chat another adapter watches costs one failed open, not an observation.
                        if (OpenState(thread, out var elsewhere) is not { } state)
                        {
                            if (!elsewhere) { ended = true; continue; } // Not held elsewhere: the next pass tries again, as it always has.
                            var misses = held.TryGetValue(thread, out var before) ? before.Misses + 1 : 1;
                            held[thread] = (now + (long)HeldRetry(misses).TotalMilliseconds, misses);
                            continue;
                        }
                        held.Remove(thread);
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var task = WatchAsync(handle, codex, state, snapshot, stop.Token);
                        active[thread] = (handle.SessionId, stop, task);
                        // A watcher that ends is restarted by a pass soon after, as often as the short interval allows and no more.
                        _ = task.ContinueWith(_ => Task.Delay(BusyPass, ending).ContinueWith(_ => changed.Writer.TryWrite(true), TaskScheduler.Default),
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    var live = active.Values.Count(value => !value.Task.IsCompleted);
                    Report(PassReason(targets, noCodex, live));
                    LocalWakeStatus.Counts(observed.Count, live, statusRoot);
                    busy = locating || ended || noCodex || joining.Count > 0;
                }
                // One bad pass used to end this loop for the life of the adapter, with nothing to show for it.
                catch (Exception error) when (error is not OperationCanceledException || !lifetime.IsCancellationRequested) { Report(LocalWakeCodes.LoopFailed); }
                if (settled) firstPass.TrySetResult();
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                waiting.CancelAfter(NextPass(busy || sessionsWatch is null || sessionsWatchFailed, held.Values.Select(x => x.Due)));
                try { await changed.Reader.ReadAsync(waiting.Token); } catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { firstPass.TrySetResult(); if (rearming is not null) await rearming; foreach (var value in active.Values) value.Stop.Cancel(); await Task.WhenAll(active.Values.Select(value => value.Task)); foreach (var value in active.Values) value.Stop.Dispose(); }
    }

    /// <summary>How long the loop waits for a change before its next pass: the interval, cut short by a due re-arm attempt or a held chat's retry.</summary>
    /// <param name="retries">When held chats are next tried, in <see cref="Environment.TickCount64"/> milliseconds.</param>
    private TimeSpan NextPass(bool busy, IEnumerable<long> retries)
    {
        var next = busy ? BusyPass : QuietPass;
        var tick = Environment.TickCount64;
        foreach (var due in retries)
            if (due > tick && TimeSpan.FromMilliseconds(due - tick) < next) next = TimeSpan.FromMilliseconds(due - tick);
        if (rearmAt is { } rearm && transport.Clock.GetUtcNow() is var now && rearm > now && rearm - now < next) next = rearm - now;
        return next < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : next;
    }

    /// <summary>
    /// The chat's exclusive watcher state, which also keeps duplicate MCP processes from dispatching twice; null when it cannot be had now.
    /// <paramref name="elsewhere"/> then says another live adapter holds it (retried after a backoff); otherwise the failure is reported and the next pass tries again.
    /// </summary>
    private WakeState? OpenState(string thread, out bool elsewhere)
    {
        elsewhere = false;
        try { return new WakeState(Path.Combine(Connection.Folder, "wake-" + thread + ".db")); }
        catch (IOException) { elsewhere = true; Report(LocalWakeCodes.WatcherLockHeld); return null; }
        catch (Exception error) when (error is UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Volatile.Write(ref watchFailure, LocalWakeCodes.WatcherUnavailable);
            Console.Error.WriteLine("CoCern local wake is temporarily unavailable; the adapter will retry.");
            return null;
        }
        catch (Exception) { Volatile.Write(ref watchFailure, LocalWakeCodes.WatcherFailed); return null; }
    }

    /// <param name="state">The chat's exclusive state, already held; disposed when the watch ends.</param>
    /// <param name="snapshot">The pass's own observation, which <paramref name="handle"/> came from.</param>
    private async Task WatchAsync(SessionHandle handle, string codex, WakeState state, IReadOnlyList<SessionHandle> snapshot, CancellationToken ct)
    {
        using var owned = state;
        try
        {
            var latest = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, handle.SessionId));
            // A native handle is checked against the pass's observation; every dispatch is fenced against the binding again (NativeJoinBinding.ForDispatch).
            if (latest is null || latest.Chat != handle.Chat || latest.RepositoryId != handle.RepositoryId || latest.SessionToken != handle.SessionToken ||
                (handle.NativeBindingKey is null ? !OwnsNative(latest) : !snapshot.Contains(handle))) { Report(LocalWakeCodes.WatcherHandleChanged); return; }
            var thread = handle.Chat!.ThreadId;
            var path = Path.Combine(Connection.Folder, "wake-" + thread + ".db");
            var options = handle.NativeBindingKey is null ? Connection.Options : NativeJoin.BackgroundOptions(Connection.Options);
            var runtime = new WakeRuntime(options, path, Path.Combine(Connection.Folder, "stop"), Connection.Options.BaseUri.AbsoluteUri)
            {
                State = state, HubHandler = transport.HubHandler, Interval = transport.WakeInterval,
                Reconciled = (_, status) =>
                {
                    NativeStartupEvidence.RecordLocalWake(Connection.Folder, handle, status);
                    // The hub answered that this native session has ended: stop dispatching it here and after a restart, until the chat reopens it.
                    if (status == WakeReconciler.SessionGone && handle.NativeBindingKey is not null) { NativeJoinBinding.MarkSessionUnknown(Connection.Folder, handle); changed.Writer.TryWrite(true); }
                }
            };
            var route = new WakeRoute(handle.RepositoryId, "default", NodeState.SessionHandlePath(Connection.Folder, handle.SessionId), thread, Environment.MachineName);
            await watch(runtime, new(route, new(handle.SessionId, handle.SessionToken), NativeJoinBinding.ForDispatch(Connection.Folder, handle, transport.CodexDispatcher(codex))), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { Report(LocalWakeCodes.WatcherLockHeld); /* Reported as before; the exclusive state itself is taken in OpenState. A later pass retries. */ }
        catch (Exception error) when (error is UnauthorizedAccessException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            Volatile.Write(ref watchFailure, LocalWakeCodes.WatcherUnavailable);
            Console.Error.WriteLine("CoCern local wake is temporarily unavailable; the adapter will retry.");
        }
        // Anything else would fault this task and then the loop that awaits it.
        catch (Exception) { Volatile.Write(ref watchFailure, LocalWakeCodes.WatcherFailed); }
        finally { NativeStartupEvidence.ClearLocalWake(Connection.Folder, handle); }
    }

    private bool OwnsNative(SessionHandle handle) => Connection.Options.ExportedChat is not { } client ||
        ((handle.Client is null || handle.Client == client) && (handle.Chat is null || handle.Chat == client));

    public async ValueTask DisposeAsync() { await stopping.CancelAsync(); if (running is not null) await running; stopping.Dispose(); }
}

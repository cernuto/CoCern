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
    private string? observeProblem;
    private string? watchFailure;
    internal LocalWakeConnection Connection { get; }
    /// <summary>Completes when the loop has finished its first pass.</summary>
    internal Task FirstPass => firstPass.Task;

    internal LocalChatWake(LocalWakeConnection connection, NodeTransport? transport = null, Func<WakeRuntime, WakeTarget, CancellationToken, Task>? watch = null, string? statusRoot = null)
    {
        // Beside the connection folders, so a status file always lands in the root this connection itself lives under.
        this.statusRoot = statusRoot ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(connection.Folder));
        Connection = connection;
        this.transport = transport ?? new();
        chats = new(connection.Folder, this.transport.Locators, this.transport.Clock, NodeChats.DefaultLocateWindow, NodeChats.DefaultBudget);
        handoff = SessionHandoff.ForFolder(connection.Folder, connection.Options.ExportedChat);
        handoff.Report = ObserveProblem;
        this.watch = watch ?? ((runtime, target, ct) => WakeWatcher.RunAsync(runtime, _ => Task.FromResult<IReadOnlyList<WakeTarget>>([target]), false, null, ct, clock: this.transport.Clock));
    }

    public static LocalChatWake? TryCreate(AdapterOptions options, string? tokenFile, string? cacheRoot = null)
    {
        try { return LocalWakeConnection.Create(options, tokenFile, cacheRoot) is { } connection ? new(connection) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The adapter's way in to local wake: says why there is none, and leaves <see cref="TryCreate"/> to say why setup itself refused.</summary>
    internal static LocalChatWake? Open(AdapterOptions options, string? tokenFile, bool registered, string? cacheRoot = null)
    {
        if (registered) { LocalWakeStatus.Report(LocalWakeCodes.NativeRegistration, cacheRoot); return null; }
        if (options.NodeFolder is not null) { LocalWakeStatus.Report(LocalWakeCodes.NodeConnection, cacheRoot); return null; }
        LocalWakeStatus.Report(LocalWakeCodes.SetupReached, cacheRoot);
        LocalChatWake? wake;
        try { wake = TryCreate(options, tokenFile, cacheRoot); }
        catch (Exception) { LocalWakeStatus.Report(LocalWakeCodes.SetupThrew, cacheRoot); throw; }
        if (SetupOutcome(wake, LocalWakeStatus.LastCode(cacheRoot)) is { } outcome) LocalWakeStatus.Report(outcome, cacheRoot);
        return wake;
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
                if (tool == RelayTools.SessionBind) handoff.Rename(arguments);
            }
            var current = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, session));
            if (current is null) { ObserveProblem(LocalWakeCodes.ObserveHandleMismatch); return; }
            if (!OwnsNative(current)) { Report(LocalWakeCodes.ObserveNotOwned); return; }
            observed.TryAdd(session, 0);
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

    public Task RunAsync(CancellationToken ct) => running ??= LoopAsync(ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        var active = new Dictionary<string, (string Session, CancellationTokenSource Stop, Task Task)>();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    chats.Read(_ => true); // The existing bounded locator verifies native identity before a Codex route is used.
                    var native = NativeJoinBinding.DispatchSnapshot(Connection.Folder).Where(x => x.Chat is { Provider: ChatProviders.Codex }).ToArray();
                    var targets = native.Concat(NodeState.ReadSessionHandles(Connection.Folder).Where(x => observed.ContainsKey(x.SessionId) && OwnsNative(x) &&
                        x.Chat is { Provider: ChatProviders.Codex } && !native.Any(n => n.Chat == x.Chat))).GroupBy(x => x.Chat!.ThreadId)
                        .Where(group => group.Count() == 1 || group.All(x => x.NativeBindingKey is null)).SelectMany(group => group).ToArray();
                    foreach (var gone in active.Keys.Where(key => !targets.Any(x => x.Chat!.ThreadId == key && x.SessionId == active[key].Session) || active[key].Task.IsCompleted).ToArray())
                    { var previous = active[gone]; previous.Stop.Cancel(); await previous.Task; previous.Stop.Dispose(); active.Remove(gone); }
                    var noCodex = false;
                    foreach (var handle in targets)
                    {
                        var thread = handle.Chat!.ThreadId;
                        if (active.ContainsKey(thread)) continue;
                        if (transport.CodexExecutable() is not { } codex) { noCodex = true; continue; }
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        active[thread] = (handle.SessionId, stop, WatchAsync(handle, codex, stop.Token));
                    }
                    var live = active.Values.Count(value => !value.Task.IsCompleted);
                    Report(PassReason(targets, noCodex, live));
                    LocalWakeStatus.Counts(observed.Count, live, statusRoot);
                }
                // One bad pass used to end this loop for the life of the adapter, with nothing to show for it.
                catch (Exception error) when (error is not OperationCanceledException || !lifetime.IsCancellationRequested) { Report(LocalWakeCodes.LoopFailed); }
                firstPass.TrySetResult();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(4));
                try { await changed.Reader.ReadAsync(wait.Token); } catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { firstPass.TrySetResult(); foreach (var value in active.Values) value.Stop.Cancel(); await Task.WhenAll(active.Values.Select(value => value.Task)); foreach (var value in active.Values) value.Stop.Dispose(); }
    }

    private async Task WatchAsync(SessionHandle handle, string codex, CancellationToken ct)
    {
        try
        {
            var latest = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, handle.SessionId));
            if (latest is null || latest.Chat != handle.Chat || latest.RepositoryId != handle.RepositoryId || latest.SessionToken != handle.SessionToken ||
                (handle.NativeBindingKey is null ? !OwnsNative(latest) : !NativeJoinBinding.DispatchSnapshot(Connection.Folder).Contains(handle))) { Report(LocalWakeCodes.WatcherHandleChanged); return; }
            var thread = handle.Chat!.ThreadId;
            var path = Path.Combine(Connection.Folder, "wake-" + thread + ".db");
            using var state = new WakeState(path); // Exclusive per-native-chat state also prevents duplicate MCP processes from dispatching twice.
            var options = handle.NativeBindingKey is null ? Connection.Options : NativeJoin.BackgroundOptions(Connection.Options);
            var runtime = new WakeRuntime(options, path, Path.Combine(Connection.Folder, "stop"), Connection.Options.BaseUri.AbsoluteUri) { State = state, HubHandler = transport.HubHandler, Interval = transport.WakeInterval };
            var route = new WakeRoute(handle.RepositoryId, "default", NodeState.SessionHandlePath(Connection.Folder, handle.SessionId), thread, Environment.MachineName);
            await watch(runtime, new(route, new(handle.SessionId, handle.SessionToken), NativeJoinBinding.ForDispatch(Connection.Folder, handle, transport.CodexDispatcher(codex))), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { Report(LocalWakeCodes.WatcherLockHeld); /* Another live adapter owns the native chat; retry after it releases the state. */ }
        catch (Exception error) when (error is UnauthorizedAccessException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            Volatile.Write(ref watchFailure, LocalWakeCodes.WatcherUnavailable);
            Console.Error.WriteLine("CoCern local wake is temporarily unavailable; the adapter will retry.");
        }
        // Anything else would fault this task and then the loop that awaits it.
        catch (Exception) { Volatile.Write(ref watchFailure, LocalWakeCodes.WatcherFailed); }
    }

    private bool OwnsNative(SessionHandle handle) => Connection.Options.ExportedChat is not { } client ||
        ((handle.Client is null || handle.Client == client) && (handle.Chat is null || handle.Chat == client));

    public async ValueTask DisposeAsync() { await stopping.CancelAsync(); if (running is not null) await running; stopping.Dispose(); }
}

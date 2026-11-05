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
    internal LocalWakeConnection Connection { get; }

    internal LocalChatWake(LocalWakeConnection connection, NodeTransport? transport = null, Func<WakeRuntime, WakeTarget, CancellationToken, Task>? watch = null)
    {
        Connection = connection;
        this.transport = transport ?? new();
        chats = new(connection.Folder, this.transport.Locators, this.transport.Clock, NodeChats.DefaultLocateWindow, NodeChats.DefaultBudget);
        handoff = SessionHandoff.ForFolder(connection.Folder, connection.Options.ExportedChat);
        this.watch = watch ?? ((runtime, target, ct) => WakeWatcher.RunAsync(runtime, _ => Task.FromResult<IReadOnlyList<WakeTarget>>([target]), false, null, ct, clock: this.transport.Clock));
    }

    public static LocalChatWake? TryCreate(AdapterOptions options, string? tokenFile, string? cacheRoot = null, Action<string>? report = null)
    {
        report ??= code => LocalWakeStatus.Report(code, cacheRoot);
        try { return LocalWakeConnection.Create(options, tokenFile, cacheRoot, report) is { } connection ? new(connection) : null; }
        catch (IOException) { report?.Invoke("setup_io_error"); return null; }
        catch (UnauthorizedAccessException) { report?.Invoke("setup_access_denied"); return null; }
        catch (ArgumentException) { report?.Invoke("setup_invalid_path"); return null; }
    }

    /// <summary>Only a successful session-authenticated reply may restore a pre-existing handle after reconnect.</summary>
    public void Observe(string tool, JsonElement arguments, AdapterReply reply)
    {
        if (!reply.Ok) return;
        try
        {
            string? session;
            if (tool == RelayTools.SessionOpen)
            {
                if (reply.Data is not { ValueKind: JsonValueKind.Object } data) return;
                session = ChatLocators.Text(data, "sessionId");
                if (session is null || !NodeState.IsFileId(session)) return;
                handoff.RecordLocal(arguments, reply);
                var prior = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, session));
                if (prior is null || prior.RepositoryId != ChatLocators.Text(arguments, "repositoryId") || prior.SessionToken != ChatLocators.Text(data, "sessionToken")) return;
            }
            else
            {
                if (!SessionHandoff.IsSessionReply(tool, arguments, reply)) return;
                handoff.Restore(tool, arguments, reply);
                session = ChatLocators.Text(arguments, "sessionId");
                if (session is null || !NodeState.IsFileId(session)) return;
                var handle = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, session));
                if (handle is null || handle.RepositoryId != ChatLocators.Text(arguments, "repositoryId") || handle.SessionToken != ChatLocators.Text(arguments, "sessionToken")) return;
                if (tool == RelayTools.SessionBind) handoff.Rename(arguments);
            }
            var current = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, session));
            if (current is null || !OwnsNative(current)) return;
            observed.TryAdd(session, 0);
            changed.Writer.TryWrite(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
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
                chats.Read(_ => true); // The existing bounded locator verifies native identity before a Codex route is used.
                var native = NativeJoinBinding.DispatchSnapshot(Connection.Folder).Where(x => x.Chat is { Provider: ChatProviders.Codex }).ToArray();
                var targets = native.Concat(NodeState.ReadSessionHandles(Connection.Folder).Where(x => observed.ContainsKey(x.SessionId) && OwnsNative(x) &&
                    x.Chat is { Provider: ChatProviders.Codex } && !native.Any(n => n.Chat == x.Chat))).GroupBy(x => x.Chat!.ThreadId)
                    .Where(group => group.Count() == 1 || group.All(x => x.NativeBindingKey is null)).SelectMany(group => group).ToArray();
                foreach (var gone in active.Keys.Where(key => !targets.Any(x => x.Chat!.ThreadId == key && x.SessionId == active[key].Session) || active[key].Task.IsCompleted).ToArray())
                { var previous = active[gone]; previous.Stop.Cancel(); await previous.Task; previous.Stop.Dispose(); active.Remove(gone); }
                foreach (var handle in targets)
                {
                    var thread = handle.Chat!.ThreadId;
                    if (active.ContainsKey(thread) || transport.CodexExecutable() is not { } codex) continue;
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    active[thread] = (handle.SessionId, stop, WatchAsync(handle, codex, stop.Token));
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(4));
                try { await changed.Reader.ReadAsync(wait.Token); } catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { foreach (var value in active.Values) value.Stop.Cancel(); await Task.WhenAll(active.Values.Select(value => value.Task)); foreach (var value in active.Values) value.Stop.Dispose(); }
    }

    private async Task WatchAsync(SessionHandle handle, string codex, CancellationToken ct)
    {
        var latest = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(Connection.Folder, handle.SessionId));
        if (latest is null || latest.Chat != handle.Chat || latest.RepositoryId != handle.RepositoryId || latest.SessionToken != handle.SessionToken ||
            (handle.NativeBindingKey is null ? !OwnsNative(latest) : !NativeJoinBinding.DispatchSnapshot(Connection.Folder).Contains(handle))) return;
        var thread = handle.Chat!.ThreadId;
        try
        {
            var path = Path.Combine(Connection.Folder, "wake-" + thread + ".db");
            using var state = new WakeState(path); // Exclusive per-native-chat state also prevents duplicate MCP processes from dispatching twice.
            var options = handle.NativeBindingKey is null ? Connection.Options : NativeJoin.BackgroundOptions(Connection.Options);
            var runtime = new WakeRuntime(options, path, Path.Combine(Connection.Folder, "stop"), Connection.Options.BaseUri.AbsoluteUri) { State = state, HubHandler = transport.HubHandler, Interval = transport.WakeInterval };
            var route = new WakeRoute(handle.RepositoryId, "default", NodeState.SessionHandlePath(Connection.Folder, handle.SessionId), thread, Environment.MachineName);
            await watch(runtime, new(route, new(handle.SessionId, handle.SessionToken), NativeJoinBinding.ForDispatch(Connection.Folder, handle, transport.CodexDispatcher(codex))), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { /* Another live adapter owns the native chat; retry after it releases the state. */ }
        catch (Exception error) when (error is UnauthorizedAccessException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        { Console.Error.WriteLine("CoCern local wake is temporarily unavailable; the adapter will retry."); }
    }

    private bool OwnsNative(SessionHandle handle) => Connection.Options.ExportedChat is not { } client ||
        ((handle.Client is null || handle.Client == client) && (handle.Chat is null || handle.Chat == client));

    public async ValueTask DisposeAsync() { await stopping.CancelAsync(); if (running is not null) await running; stopping.Dispose(); }
}

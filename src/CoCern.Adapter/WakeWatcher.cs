using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>One chat to wake: its route, the relay session whose inbox decides, and how the fixed prompt reaches it.</summary>
public sealed record WakeTarget(WakeRoute Route, WakeSession Session, IWakeDispatcher Dispatcher);

/// <summary>Where a watcher runs: the hub and the credential it calls with, its exclusive state database and its stop file.</summary>
public sealed record WakeRuntime(AdapterOptions Options, string StatePath, string StopFile, string RelayUrl)
{
    public Func<IDisposable?>? TryEnterDispatch { get; init; }
    /// <summary>What this watcher's calls report as their origin: a waiter running on it reports as itself.</summary>
    internal string CallOrigin { get; init; } = CallOrigins.WakeWatcher;
    /// <summary>Pulsed when the targets may have changed, so the watcher re-reads them now rather than at its next fallback pass.</summary>
    public WakeNudge? Nudge { get; init; }
    /// <summary>Replaces the hint channel's handler (tests); null keeps SignalR's own, pinned when <see cref="AdapterOptions.PinnedFingerprint"/> is set.</summary>
    public Func<HttpMessageHandler>? HubHandler { get; init; }
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DefaultHintedInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DefaultIdleInterval = TimeSpan.FromMinutes(RelayLimits.CodexWakeIdleMinutes);
    /// <summary>
    /// The fallback pass when no hint arrives. Unset, it is <see cref="DefaultInterval"/>, lengthened to <see cref="DefaultHintedInterval"/> while
    /// hints are working, or to <see cref="DefaultIdleInterval"/> for a route with no pending work. A value set here is used as it is,
    /// so a caller that chose a pace keeps it.
    /// </summary>
    public TimeSpan? Interval { get; init; }
    /// <summary>The fallback pass while hints are working, for a caller that wants both set (tests); unset follows <see cref="Interval"/>.</summary>
    public TimeSpan? HintedInterval { get; init; }
    internal TimeSpan FallbackWait(bool hinted, bool idle = false) => hinted
        ? HintedInterval ?? Interval ?? (idle ? DefaultIdleInterval : DefaultHintedInterval)
        : Interval ?? DefaultInterval;
    /// <summary>How often a route may be woken.</summary>
    public WakeLimits Limits { get; init; } = WakeLimits.Default;
    /// <summary>A state the caller already owns (and disposes); null opens <see cref="StatePath"/> for the run.</summary>
    internal WakeState? State { get; init; }
    /// <summary>Private observation after the authenticated reconciliation, never a substitute for a session probe.</summary>
    internal Action<WakeTarget, string>? Reconciled { get; init; }
}

/// <summary>Tells a running watcher that its targets may have changed. Pulses never queue up: one pending pulse is enough.</summary>
public sealed class WakeNudge
{
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, long> _lastHint = new(StringComparer.Ordinal);
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(500);
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private bool _all;

    public void Pulse() => Pulse(null);

    /// <summary>A session hint dirties only that route; a target-list change dirties the whole inventory.</summary>
    internal void Pulse(string? sessionId, string reason = "session-hint")
    {
        lock (_gate)
        {
            if (sessionId is null) _all = true;
            else if (!_sessions.TryAdd(sessionId, reason))
            {
                if (reason == "session-hint" || _sessions[sessionId] == reason) return;
                _sessions[sessionId] = reason;
            }
        }
        _changes.Writer.TryWrite(true);
    }

    /// <summary>Release the first hint for each route immediately and batch subsequent hints for that route. Inventory and control changes wake immediately.</summary>
    internal async Task<Dictionary<string, string>?> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                if (_all)
                {
                    _all = false;
                    _sessions.Clear();
                    return null;
                }
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var key in _lastHint.Where(pair => System.Diagnostics.Stopwatch.GetElapsedTime(pair.Value, now) >= BatchWindow).Select(pair => pair.Key).ToArray())
                    _lastHint.Remove(key);
                Dictionary<string, string>? ready = null;
                foreach (var (sessionId, reason) in _sessions)
                {
                    if (reason == "session-hint" && _lastHint.TryGetValue(sessionId, out var previous))
                    {
                        var remaining = BatchWindow - System.Diagnostics.Stopwatch.GetElapsedTime(previous, now);
                        if (next == Timeout.InfiniteTimeSpan || remaining < next) next = remaining;
                        continue;
                    }
                    (ready ??= new(StringComparer.Ordinal)).Add(sessionId, reason);
                    if (reason == "session-hint") _lastHint[sessionId] = now;
                }
                if (ready is not null)
                {
                    foreach (var sessionId in ready.Keys) _sessions.Remove(sessionId);
                    return ready;
                }
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(next);
            try { await _changes.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }
}

public static class WakeWatcher
{

    /// <summary>Watches every enrolled route until stopped, queueing one fixed inbox-check prompt per new message batch into its Codex chat.</summary>
    public static Task RunAsync(WakeConfiguration config, CancellationToken cancellationToken) =>
        RunAsync(config, new CodexWakeDispatcher(config.CodexPath, new CodexActivity(CodexActivity.DefaultSessionsRoot), queue: new CodexQueue(CodexQueue.DefaultHome)), exitWhenQueued: false, timeout: null, cancellationToken);

    /// <summary>
    /// Waits on one enrolled route until a message arrives that has not already woken it, hands the fixed inbox-check
    /// prompt to <paramref name="deliver"/>, and returns "queued". Returns "timeout", "stopped" or "canceled" otherwise.
    /// A session that runs this as a background command is re-invoked when it exits, so waiting costs no model turns.
    /// </summary>
    public static Task<string> AwaitInboxAsync(WakeConfiguration config, Action<string> deliver, TimeSpan? timeout, CancellationToken cancellationToken) =>
        RunAsync(config, new InboxWaitDispatcher(deliver), exitWhenQueued: true, timeout, cancellationToken);

    /// <summary>A configuration file's routes are fixed for the run; their private session handles are reread on each pass.</summary>
    internal static async Task<string> RunAsync(WakeConfiguration config, IWakeDispatcher dispatcher, bool exitWhenQueued, TimeSpan? timeout, CancellationToken cancellationToken, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        var runtime = new WakeRuntime(config.AdapterOptions(), config.StatePath, config.StopFile, config.RelayUrl) { Limits = config.ResolvedLimits() };
        return await RunAsync(runtime, _ => Task.FromResult(config.ReadTargets(dispatcher)),
            exitWhenQueued, timeout, cancellationToken, handler, clock);
    }

    /// <summary>
    /// Reconciles every target until stopped. Targets are read again on every pass and after every hint, so a target that appears is picked up
    /// without a restart and one that disappears is no longer reconciled (its hint connection is closed).
    /// </summary>
    internal static async Task<string> RunAsync(WakeRuntime runtime, Func<CancellationToken, Task<IReadOnlyList<WakeTarget>>> targets, bool exitWhenQueued, TimeSpan? timeout, CancellationToken cancellationToken, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        var options = runtime.Options;
        using var ownState = runtime.State is null ? new WakeState(runtime.StatePath, runtime.Limits) : null;
        var state = runtime.State ?? ownState!;
        using var http = new HttpClient(handler ?? RelayForwarder.CreateHandler(options), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
        var forwarder = new RelayForwarder(http, options, runtime.CallOrigin);
        // The reconciler reads only the relay URL and stop file from its configuration.
        var settings = new WakeConfiguration(runtime.RelayUrl, "", "", runtime.StatePath, runtime.StopFile, []);
        var changes = runtime.Nudge ?? new WakeNudge();
        var watches = new Dictionary<string, Watch>(StringComparer.Ordinal);
        // Rejection applies only to this exact private handle, not every future handle for the same chat or session ID.
        var rejected = new Dictionary<string, (string Repository, WakeSession Session)>(StringComparer.Ordinal);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit) lifetime.CancelAfter(limit);
        var stopMonitor = MonitorStop();
        var lastStatus = new Dictionary<string, string>();
        // Each route's jitter factor, drawn once per read so the due time PollWait computes holds still while the watcher waits,
        // and how many reads in a row did not reach the hub.
        var pace = new Dictionary<string, double>();
        var unreachable = new Dictionary<string, int>();
        var idleRoutes = new HashSet<string>(StringComparer.Ordinal);
        var lastRead = new Dictionary<string, long>(StringComparer.Ordinal);
        Dictionary<string, string>? dirty = null;
        var timer = clock ?? TimeProvider.System;
        var fullPassAt = timer.GetTimestamp();
        // Route retries cannot move the independent full-recovery deadline, nor make idle neighbors poll at their pace.
        var fullFallback = runtime.FallbackWait(hinted: true, idle: true);
        try
        {
            while (!lifetime.IsCancellationRequested && !File.Exists(runtime.StopFile))
            {
                var current = (await targets(lifetime.Token)).DistinctBy(x => x.Session.SessionId, StringComparer.Ordinal).ToArray();
                var reconcileAll = dirty is null || timer.GetElapsedTime(fullPassAt) >= fullFallback;
                HashSet<string> live = [with(capacity: current.Length, comparer: StringComparer.Ordinal), .. current.Select(x => x.Session.SessionId)];
                foreach (var gone in rejected.Keys.Where(id => !live.Contains(id)).ToArray()) rejected.Remove(gone);
                foreach (var gone in lastRead.Keys.Where(id => !live.Contains(id)).ToArray()) lastRead.Remove(gone);
                foreach (var gone in watches.Keys.Where(x => !live.Contains(x)).ToArray())
                {
                    var watch = watches[gone];
                    watches.Remove(gone);
                    lastStatus.Remove(WakeState.Key(watch.Target.Route.ThreadId));
                    pace.Remove(WakeState.Key(watch.Target.Route.ThreadId));
                    unreachable.Remove(WakeState.Key(watch.Target.Route.ThreadId));
                    idleRoutes.Remove(WakeState.Key(watch.Target.Route.ThreadId));
                    lastRead.Remove(gone);
                    await watch.Connection.DisposeAsync();
                }
                for (var i = 0; i < current.Length && !File.Exists(runtime.StopFile); i++)
                {
                    var target = current[i];
                    var routeKey = WakeState.Key(target.Route.ThreadId);
                    if (watches.TryGetValue(target.Session.SessionId, out var oldWatch) &&
                        (oldWatch.Target.Session != target.Session || oldWatch.Target.Route != target.Route))
                    {
                        watches.Remove(target.Session.SessionId);
                        var oldRouteKey = WakeState.Key(oldWatch.Target.Route.ThreadId);
                        lastStatus.Remove(oldRouteKey);
                        pace.Remove(oldRouteKey);
                        unreachable.Remove(oldRouteKey);
                        idleRoutes.Remove(oldRouteKey);
                        lastStatus.Remove(routeKey);
                        pace.Remove(routeKey);
                        unreachable.Remove(routeKey);
                        idleRoutes.Remove(routeKey);
                        lastRead.Remove(target.Session.SessionId);
                        await oldWatch.Connection.DisposeAsync();
                    }
                    if (rejected.TryGetValue(target.Session.SessionId, out var refused))
                    {
                        if (refused.Repository == target.Route.RepositoryId && refused.Session == target.Session) continue;
                        rejected.Remove(target.Session.SessionId);
                        lastStatus.Remove(routeKey);
                        pace.Remove(routeKey);
                        unreachable.Remove(routeKey);
                        lastRead.Remove(target.Session.SessionId);
                    }
                    // A hint selects its route; a timer selects only due routes. New/replaced handles have no last read and run now.
                    // Full recovery is independent of both, so a stream of one session's hints cannot postpone its neighbors forever.
                    if (!reconcileAll && !dirty!.ContainsKey(target.Session.SessionId)
                        && lastRead.TryGetValue(target.Session.SessionId, out var readAt) && timer.GetElapsedTime(readAt) < PollWait(target)) continue;
                    string status;
                    InboxCoverage coverage;
                    using (var dispatch = runtime.TryEnterDispatch?.Invoke())
                    {
                        if (runtime.TryEnterDispatch is not null && dispatch is null)
                        {
                            lastRead[target.Session.SessionId] = timer.GetTimestamp();
                            idleRoutes.Remove(routeKey);
                            Record(routeKey, "route-changed");
                            continue;
                        }
                        var subscribed = watches.TryGetValue(target.Session.SessionId, out var listening) && listening.Listening;
                        coverage = listening?.Coverage ?? new InboxCoverage();
                        var readReason = ReadReason(target.Session.SessionId, routeKey, subscribed, reconcileAll);
                        var reconciler = new WakeReconciler(settings, state, forwarder, target.Dispatcher, clock, coverage);
                        status = await reconciler.ReconcileAsync(target.Route, target.Session, lifetime.Token, readReason);
                        lastRead[target.Session.SessionId] = timer.GetTimestamp();
                        // An initial read before joining can race a missed hint; only a read made while listening proves quiet coverage.
                        if (status == "empty" && reconciler.CanWaitQuietly && subscribed && listening!.Listening) idleRoutes.Add(routeKey);
                        else idleRoutes.Remove(routeKey);
                        runtime.Reconciled?.Invoke(target, status);
                    }
                    if (!lastStatus.TryGetValue(routeKey, out var previous) || previous != status || status == "queued")
                        Console.Error.WriteLine(WakeReconciler.IsRejection(status)
                            ? $"CoCern wake route {i + 1}: session rejected; waiting for this chat's replacement session handle. Reopen its CoCern session from that chat."
                            : $"CoCern wake route {i + 1}: {status}.");
                    if (WakeReconciler.IsRejection(status))
                    {
                        rejected[target.Session.SessionId] = (target.Route.RepositoryId, target.Session);
                        lastStatus.Remove(routeKey);
                        pace.Remove(routeKey);
                        unreachable.Remove(routeKey);
                        idleRoutes.Remove(routeKey);
                        lastRead.Remove(target.Session.SessionId);
                        if (watches.Remove(target.Session.SessionId, out var refusedWatch)) await refusedWatch.Connection.DisposeAsync();
                        continue;
                    }
                    Record(routeKey, status);
                    if (exitWhenQueued && status == "queued") return status;
                    if (!watches.TryGetValue(target.Session.SessionId, out var watch)) watches[target.Session.SessionId] = watch = Watch.Create(options, runtime.HubHandler, target, coverage, changes, lifetime.Token);
                    watch.Target = target;
                    // A connection that started but never joined the session's group is retried too.
                    if (watch.Connection.State == HubConnectionState.Disconnected || (watch.Connection.State == HubConnectionState.Connected && !watch.Subscribed))
                    {
                        try
                        {
                            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                            if (watch.Connection.State == HubConnectionState.Disconnected) await watch.Connection.StartAsync(connectTimeout.Token);
                            await watch.SubscribeAsync(connectTimeout.Token);
                            // A change can land between the initial status read and the server adding this connection to its groups.
                            changes.Pulse(target.Session.SessionId, "subscribed");
                        }
                        catch (Exception) { /* Reconcile on the next fixed fallback even when hints fail. */ }
                    }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (reconcileAll) fullPassAt = timer.GetTimestamp();
                var remaining = current.Length == 0 ? runtime.FallbackWait(false) : fullFallback - timer.GetElapsedTime(fullPassAt);
                foreach (var target in current)
                {
                    // A rejected handle makes no more hub calls, but replacements must still be discovered at the short pace.
                    var until = rejected.ContainsKey(target.Session.SessionId) ? runtime.FallbackWait(false)
                        : lastRead.TryGetValue(target.Session.SessionId, out var readAt) ? PollWait(target) - timer.GetElapsedTime(readAt)
                        : runtime.FallbackWait(false);
                    if (until < remaining) remaining = until;
                }
                wait.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
                try
                {
                    dirty = await changes.WaitAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { dirty = new(StringComparer.Ordinal); }
            }
        }
        // A stop file or wait timeout ends the run; only the caller's own cancellation propagates.
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            try { await stopMonitor; } catch (OperationCanceledException) { }
            foreach (var watch in watches.Values) await watch.Connection.DisposeAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return File.Exists(runtime.StopFile) ? "stopped" : "timeout";

        string ReadReason(string sessionId, string routeKey, bool subscribed, bool fullPass)
        {
            if (!lastRead.ContainsKey(sessionId)) return "initial";
            if (dirty is null) return "targets";
            if (dirty.TryGetValue(sessionId, out var reason)) return reason;
            if (fullPass) return "recovery";
            if (!subscribed) return "disconnected";
            if (idleRoutes.Contains(routeKey)) return "recovery";
            return lastStatus.GetValueOrDefault(routeKey) is "empty" or "queued" ? "pending" : "retry";
        }

        TimeSpan PollWait(WakeTarget target)
        {
            var routeKey = WakeState.Key(target.Route.ThreadId);
            var hinted = watches.TryGetValue(target.Session.SessionId, out var watch) && watch.Listening
                && lastStatus.TryGetValue(routeKey, out var status) && status is "empty" or "queued";
            // A hinted route, and a caller's explicit interval, keep their pace exactly.
            if (hinted || runtime.Interval is not null) return runtime.FallbackWait(hinted, idleRoutes.Contains(routeKey));
            var wait = lastStatus.GetValueOrDefault(routeKey) == WakeReconciler.HubUnreachable
                ? RetryPacing.Unreachable(unreachable.GetValueOrDefault(routeKey))
                : runtime.FallbackWait(false);
            return wait * pace.GetValueOrDefault(routeKey, 1.0);
        }

        // Every read's status, with a fresh jitter factor and the count of reads in a row that did not reach the hub.
        void Record(string routeKey, string status)
        {
            lastStatus[routeKey] = status;
            pace[routeKey] = RetryPacing.Factor();
            unreachable[routeKey] = status == WakeReconciler.HubUnreachable ? unreachable.GetValueOrDefault(routeKey) + 1 : 0;
        }

        async Task MonitorStop()
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (File.Exists(runtime.StopFile)) { await lifetime.CancelAsync(); return; }
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
            }
        }
    }

    /// <summary>One target's hint connection. The target is refreshed every pass, so a reconnect re-watches with the latest session.</summary>
    private sealed class Watch(HubConnection connection, WakeTarget target, InboxCoverage coverage)
    {
        public HubConnection Connection { get; } = connection;
        public InboxCoverage Coverage { get; } = coverage;
        public WakeTarget Target { get; set; } = target;
        // Bumped on every reconnect or close; the group was joined only while _joined equals it.
        private int _generation, _joined = -1;
        public bool Subscribed => Volatile.Read(ref _joined) == Volatile.Read(ref _generation);
        public bool Listening => Connection.State == HubConnectionState.Connected && Subscribed;

        public async Task SubscribeAsync(CancellationToken cancellationToken)
        {
            var generation = Volatile.Read(ref _generation);
            var current = Target;
            await Connection.WatchSessionAsync(current.Session.SessionId, current.Session.SessionToken, current.Route.RepositoryId, cancellationToken);
            Volatile.Write(ref _joined, generation);
        }

        public static Watch Create(AdapterOptions options, Func<HttpMessageHandler>? hubHandler, WakeTarget target, InboxCoverage coverage, WakeNudge changes, CancellationToken lifetime)
        {
            var watch = new Watch(RelayHubConnection.Create(options, hubHandler), target, coverage);
            var sessionId = target.Session.SessionId;
            watch.Connection.On<JsonElement>(HubEvents.CoordinationChanged, hint =>
            {
                if (RelayHubConnection.SessionHintReason(hint, sessionId, target.Route.RepositoryId) is { } reason)
                    changes.Pulse(sessionId, reason);
            });
            // A lost channel also ends a long wait, so the short fallback takes over at once rather than after it.
            watch.Connection.Reconnecting += _ => { Interlocked.Increment(ref watch._generation); changes.Pulse(sessionId, "disconnected"); return Task.CompletedTask; };
            watch.Connection.Closed += _ => { Interlocked.Increment(ref watch._generation); changes.Pulse(sessionId, "disconnected"); return Task.CompletedTask; };
            watch.Connection.Reconnected += async _ =>
            {
                Interlocked.Increment(ref watch._generation);
                try
                {
                    using var reconnectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    reconnectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await watch.SubscribeAsync(reconnectTimeout.Token);
                    changes.Pulse(sessionId, "reconnect");
                }
                catch (Exception) { Console.Error.WriteLine("CoCern wake hints unavailable; fallback reconciliation remains active."); }
            };
            return watch;
        }
    }
}

/// <summary>Hands the fixed inbox-check prompt to the waiting process's caller instead of queueing it into a chat client.</summary>
public sealed class InboxWaitDispatcher(Action<string> deliver) : IWakeDispatcher
{
    public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken cancellationToken)
    {
        deliver(prompt);
        return Task.FromResult(true);
    }
}

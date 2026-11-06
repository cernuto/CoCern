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
    /// <summary>
    /// The fallback pass when no hint arrives. Unset, it is <see cref="DefaultInterval"/>, lengthened to <see cref="DefaultHintedInterval"/> while
    /// hints are working; a value set here is used as it is, so a caller that chose a pace keeps it.
    /// </summary>
    public TimeSpan? Interval { get; init; }
    /// <summary>The fallback pass while hints are working, for a caller that wants both set (tests); unset follows <see cref="Interval"/>.</summary>
    public TimeSpan? HintedInterval { get; init; }
    internal TimeSpan FallbackWait(bool hinted) => hinted ? HintedInterval ?? Interval ?? DefaultHintedInterval : Interval ?? DefaultInterval;
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
    internal readonly Channel<bool> Changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Pulse() => Changes.Writer.TryWrite(true);
}

/// <summary>
/// Waits after a hint until hints stop for a quiet gap, or until a cap since the wait began, then drops the hints that came meanwhile. A
/// burst of messages then costs one reconcile pass instead of one per message, and a steady stream still gets a pass at least every cap.
/// </summary>
internal static class HintCoalescer
{
    public static async Task WaitAsync(ChannelReader<bool> hints, TimeSpan quiet, TimeSpan limit, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (limit - started.Elapsed is var left && left > TimeSpan.Zero)
        {
            using var gap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gap.CancelAfter(left < quiet ? left : quiet);
            try { await hints.ReadAsync(gap.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
        }
        while (hints.TryRead(out _)) { }
    }
}

public static class WakeWatcher
{
    /// <summary>How long hints must stop before a pass runs: a lone message is read about this soon after it arrives.</summary>
    internal static readonly TimeSpan HintQuiet = TimeSpan.FromMilliseconds(500);
    /// <summary>The longest a stream of hints can hold a pass back.</summary>
    internal static readonly TimeSpan HintCoalesceLimit = TimeSpan.FromSeconds(3);

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
        var handles = config.Routes.Distinct().ToDictionary(route => route, WakeConfiguration.ReadSession);
        Task<IReadOnlyList<WakeTarget>> Targets(CancellationToken _)
        {
            foreach (var route in config.Routes)
                try { handles[route] = WakeConfiguration.ReadSession(route); }
                catch (AdapterConfigurationException) { /* A handle being replaced stays suspended until its complete replacement is readable. */ }
            return Task.FromResult<IReadOnlyList<WakeTarget>>(config.Routes.Select(route => new WakeTarget(route, handles[route], dispatcher)).ToArray());
        }
        return await RunAsync(runtime, Targets,
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
        var changed = runtime.Nudge?.Changes ?? Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var watches = new Dictionary<string, Watch>(StringComparer.Ordinal);
        // Rejection applies only to this exact private handle, not every future handle for the same chat or session ID.
        var rejected = new Dictionary<string, (string Repository, WakeSession Session)>(StringComparer.Ordinal);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit) lifetime.CancelAfter(limit);
        var stopMonitor = MonitorStop();
        var lastStatus = new Dictionary<string, string>();
        var coverage = new InboxCoverage();
        try
        {
            while (!lifetime.IsCancellationRequested && !File.Exists(runtime.StopFile))
            {
                var current = (await targets(lifetime.Token)).DistinctBy(x => x.Session.SessionId, StringComparer.Ordinal).ToArray();
                var live = current.Select(x => x.Session.SessionId).ToHashSet(StringComparer.Ordinal);
                foreach (var gone in rejected.Keys.Where(id => !live.Contains(id)).ToArray()) rejected.Remove(gone);
                foreach (var gone in watches.Keys.Where(x => !live.Contains(x)).ToArray())
                {
                    var watch = watches[gone];
                    watches.Remove(gone);
                    lastStatus.Remove(WakeState.Key(watch.Target.Route.ThreadId));
                    await watch.Connection.DisposeAsync();
                }
                var settled = current.Length > 0;
                for (var i = 0; i < current.Length && !File.Exists(runtime.StopFile); i++)
                {
                    var target = current[i];
                    var routeKey = WakeState.Key(target.Route.ThreadId);
                    if (watches.TryGetValue(target.Session.SessionId, out var oldWatch) &&
                        (oldWatch.Target.Session != target.Session || oldWatch.Target.Route.RepositoryId != target.Route.RepositoryId))
                    {
                        watches.Remove(target.Session.SessionId);
                        await oldWatch.Connection.DisposeAsync();
                    }
                    if (rejected.TryGetValue(target.Session.SessionId, out var refused))
                    {
                        if (refused.Repository == target.Route.RepositoryId && refused.Session == target.Session) { settled = false; continue; }
                        rejected.Remove(target.Session.SessionId);
                        lastStatus.Remove(routeKey);
                    }
                    string status;
                    using (var dispatch = runtime.TryEnterDispatch?.Invoke())
                    {
                        if (runtime.TryEnterDispatch is not null && dispatch is null) continue;
                        status = await new WakeReconciler(settings, state, forwarder, target.Dispatcher, clock, coverage).ReconcileAsync(target.Route, target.Session, lifetime.Token);
                        runtime.Reconciled?.Invoke(target, status);
                    }
                    if (!lastStatus.TryGetValue(routeKey, out var previous) || previous != status || status == "queued")
                        Console.Error.WriteLine(status == WakeReconciler.SessionRejected
                            ? $"CoCern wake route {i + 1}: session rejected; waiting for this chat's replacement session handle. Reopen its CoCern session from that chat."
                            : $"CoCern wake route {i + 1}: {status}.");
                    lastStatus[routeKey] = status;
                    settled &= status is "empty" or "queued";
                    if (status == WakeReconciler.SessionRejected)
                    {
                        rejected[target.Session.SessionId] = (target.Route.RepositoryId, target.Session);
                        if (watches.Remove(target.Session.SessionId, out var refusedWatch)) await refusedWatch.Connection.DisposeAsync();
                        continue;
                    }
                    if (exitWhenQueued && status == "queued") return status;
                    if (!watches.TryGetValue(target.Session.SessionId, out var watch)) watches[target.Session.SessionId] = watch = Watch.Create(options, runtime.HubHandler, target, changed, lifetime.Token);
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
                        }
                        catch (Exception) { /* Reconcile on the next fixed fallback even when hints fail. */ }
                    }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                // The longer wait only while a hint is sure to bring the next wake: every target's channel joined, and none waiting out a retry no hint
                // will announce (a rate limit, a busy chat, a hub error).
                wait.CancelAfter(runtime.FallbackWait(settled && current.All(target => watches.TryGetValue(target.Session.SessionId, out var known) && known.Listening)));
                try
                {
                    await changed.Reader.ReadAsync(wait.Token);
                    await HintCoalescer.WaitAsync(changed.Reader, HintQuiet, HintCoalesceLimit, lifetime.Token);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
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
    private sealed class Watch(HubConnection connection, WakeTarget target)
    {
        public HubConnection Connection { get; } = connection;
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

        public static Watch Create(AdapterOptions options, Func<HttpMessageHandler>? hubHandler, WakeTarget target, Channel<bool> changed, CancellationToken lifetime)
        {
            var watch = new Watch(RelayHubConnection.Create(options, hubHandler), target);
            var sessionId = target.Session.SessionId;
            watch.Connection.On<JsonElement>(HubEvents.CoordinationChanged, hint =>
            {
                if (hint.ValueKind == JsonValueKind.Object && hint.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String && session.GetString() == sessionId)
                    changed.Writer.TryWrite(true);
            });
            // A lost channel also ends a long wait, so the short fallback takes over at once rather than after it.
            watch.Connection.Reconnecting += _ => { Interlocked.Increment(ref watch._generation); changed.Writer.TryWrite(true); return Task.CompletedTask; };
            watch.Connection.Closed += _ => { Interlocked.Increment(ref watch._generation); changed.Writer.TryWrite(true); return Task.CompletedTask; };
            watch.Connection.Reconnected += async _ =>
            {
                Interlocked.Increment(ref watch._generation);
                try
                {
                    using var reconnectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    reconnectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await watch.SubscribeAsync(reconnectTimeout.Token);
                    changed.Writer.TryWrite(true);
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

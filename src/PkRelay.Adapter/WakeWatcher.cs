using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;

namespace PkRelay.Adapter;

public static class WakeWatcher
{
    /// <summary>Watches every enrolled route until stopped, queueing one fixed inbox-check prompt per new message batch into its Codex chat.</summary>
    public static Task RunAsync(WakeConfiguration config, CancellationToken cancellationToken) =>
        RunAsync(config, new CodexWakeDispatcher(config.CodexPath), exitWhenQueued: false, timeout: null, cancellationToken);

    /// <summary>
    /// Waits on one enrolled route until a message arrives that has not already woken it, hands the fixed inbox-check
    /// prompt to <paramref name="deliver"/>, and returns "queued". Returns "timeout", "stopped" or "canceled" otherwise.
    /// A session that runs this as a background command is re-invoked when it exits, so waiting costs no model turns.
    /// </summary>
    public static Task<string> AwaitInboxAsync(WakeConfiguration config, Action<string> deliver, TimeSpan? timeout, CancellationToken cancellationToken) =>
        RunAsync(config, new InboxWaitDispatcher(deliver), exitWhenQueued: true, timeout, cancellationToken);

    internal static async Task<string> RunAsync(WakeConfiguration config, IWakeDispatcher dispatcher, bool exitWhenQueued, TimeSpan? timeout, CancellationToken cancellationToken, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        var options = config.AdapterOptions();
        using var state = new WakeState(config.StatePath);
        using var http = new HttpClient(handler ?? RelayForwarder.CreateHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
        var reconciler = new WakeReconciler(config, state, new(http, options), dispatcher, clock);
        var routes = config.Routes.Select(route => (Route: route, Session: WakeConfiguration.ReadSession(route))).ToArray();
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var connections = new List<HubConnection>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit) lifetime.CancelAfter(limit);
        var stopMonitor = MonitorStop();
        var lastStatus = new Dictionary<string, string>();
        try
        {
            foreach (var route in routes)
            {
                var connection = RelayHubConnection.Create(options);
                connections.Add(connection);
                connection.On<JsonElement>(RelayHubConnection.ChangedEvent, hint =>
                {
                    if (hint.ValueKind == JsonValueKind.Object && hint.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String && session.GetString() == route.Session.SessionId)
                        changed.Writer.TryWrite(true);
                });
                connection.Reconnected += async _ =>
                {
                    try
                    {
                        using var reconnectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        reconnectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await connection.WatchSessionAsync(route.Session.SessionId, route.Session.SessionToken, route.Route.RepositoryId, reconnectTimeout.Token);
                        changed.Writer.TryWrite(true);
                    }
                    catch (Exception) { Console.Error.WriteLine("PkRelay wake hints unavailable; fallback reconciliation remains active."); }
                };
            }
            while (!lifetime.IsCancellationRequested && !File.Exists(config.StopFile))
            {
                for (var i = 0; i < routes.Length && !File.Exists(config.StopFile); i++)
                {
                    var route = routes[i];
                    var status = await reconciler.ReconcileAsync(route.Route, route.Session, lifetime.Token);
                    var routeKey = WakeState.Key(route.Route.ThreadId);
                    if (!lastStatus.TryGetValue(routeKey, out var previous) || previous != status || status == "queued")
                        Console.Error.WriteLine($"PkRelay wake route {i + 1}: {status}.");
                    lastStatus[routeKey] = status;
                    if (exitWhenQueued && status == "queued") return status;
                    var connection = connections[i];
                    if (connection.State == HubConnectionState.Disconnected)
                    {
                        try
                        {
                            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                            await connection.StartAsync(connectTimeout.Token);
                            await connection.WatchSessionAsync(route.Session.SessionId, route.Session.SessionToken, route.Route.RepositoryId, connectTimeout.Token);
                        }
                        catch (Exception) { /* Reconcile on the next fixed fallback even when hints fail. */ }
                    }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await changed.Reader.ReadAsync(wait.Token);
                    await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                    while (changed.Reader.TryRead(out _)) { }
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
            foreach (var connection in connections) await connection.DisposeAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return File.Exists(config.StopFile) ? "stopped" : "timeout";

        async Task MonitorStop()
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (File.Exists(config.StopFile)) { await lifetime.CancelAsync(); return; }
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
            }
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

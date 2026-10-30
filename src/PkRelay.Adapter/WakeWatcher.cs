using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace PkRelay.Adapter;

public static class WakeWatcher
{
    public static async Task RunAsync(WakeConfiguration config, CancellationToken cancellationToken)
    {
        var options = config.AdapterOptions();
        using var state = new WakeState(config.StatePath);
        using var http = new HttpClient(RelayForwarder.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        var reconciler = new WakeReconciler(config, state, new(http, options), new CodexWakeDispatcher(config.CodexPath));
        var routes = config.Routes.Select(route => (Route: route, Session: WakeConfiguration.ReadSession(route))).ToArray();
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var connections = new List<HubConnection>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopMonitor = MonitorStop();
        var lastStatus = new Dictionary<string, string>();
        try
        {
            foreach (var route in routes)
            {
                var connection = Connection(options);
                connections.Add(connection);
                connection.On<JsonElement>("coordination.changed", hint =>
                {
                    if (hint.ValueKind == JsonValueKind.Object && hint.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String && session.GetString() == route.Session.SessionId)
                        changed.Writer.TryWrite(true);
                });
                connection.Reconnected += async _ =>
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await Watch(connection, route.Route, route.Session, timeout.Token);
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
                    var connection = connections[i];
                    if (connection.State == HubConnectionState.Disconnected)
                    {
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(2));
                            await connection.StartAsync(timeout.Token);
                            await Watch(connection, route.Route, route.Session, timeout.Token);
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
        finally
        {
            await lifetime.CancelAsync();
            try { await stopMonitor; } catch (OperationCanceledException) { }
            foreach (var connection in connections) await connection.DisposeAsync();
        }

        async Task MonitorStop()
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (File.Exists(config.StopFile)) { await lifetime.CancelAsync(); return; }
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
            }
        }
    }

    private static Task Watch(HubConnection connection, WakeRoute route, WakeSession session, CancellationToken token) =>
        connection.InvokeAsync("WatchSession", session.SessionId, session.SessionToken, route.RepositoryId, token);

    private static HubConnection Connection(AdapterOptions options) => new HubConnectionBuilder()
        .WithUrl(new Uri(options.BaseUri, "hubs/coordination"), connection =>
        {
            connection.AccessTokenProvider = () => Task.FromResult<string?>(options.Token);
            connection.Transports = HttpTransportType.LongPolling;
            connection.HttpMessageHandlerFactory = handler =>
            {
                DisableRedirects(handler);
                return new PinnedOriginHandler(options.BaseUri, handler);
            };
            connection.TransportMaxBufferSize = RelayForwarder.MaxResponseBytes;
            connection.ApplicationMaxBufferSize = RelayForwarder.MaxResponseBytes;
        }).WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)]).Build();

    private static void DisableRedirects(HttpMessageHandler handler)
    {
        if (handler is HttpClientHandler client) client.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        if (handler is DelegatingHandler { InnerHandler: { } inner }) DisableRedirects(inner);
    }
}

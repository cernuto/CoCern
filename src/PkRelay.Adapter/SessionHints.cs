using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace PkRelay.Adapter;

/// <summary>Optional compact hints. Durable inbox reads remain authoritative.</summary>
public sealed class SessionHints(AdapterOptions options, Action<string>? writeHint = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _writeHint = writeHint ?? Console.Error.WriteLine;
    private HubConnection? _connection;
    private int _hintPending;

    public void ResetHint() => Interlocked.Exchange(ref _hintPending, 0);

    public async Task ObserveSessionAsync(JsonElement arguments, AdapterReply reply, CancellationToken cancellationToken)
    {
        if (!options.SignalR || reply.Data is not { ValueKind: JsonValueKind.Object } data ||
            !TryString(data, "sessionId", out var sessionId) || !TryString(data, "sessionToken", out var sessionToken) ||
            !TryString(arguments, "repositoryId", out var repositoryId)) return;

        // Optional hint setup cannot turn a committed session_open into a failed call.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _gate.WaitAsync(timeout.Token);
            try
            {
                if (_connection is not null) await _connection.DisposeAsync();
                _connection = new HubConnectionBuilder()
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
                    })
                    .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)])
                    .Build();
                var connection = _connection;
                connection.On<JsonElement>("coordination.changed", hint =>
                {
                    if (TryString(hint, "sessionId", out var recipient) && recipient == sessionId &&
                        Interlocked.Exchange(ref _hintPending, 1) == 0)
                        _writeHint("PkRelay coordination changed; use check_inbox for durable messages.");
                });
                connection.Reconnected += async _ =>
                {
                    try
                    {
                        using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await connection.InvokeAsync("WatchSession", sessionId, sessionToken, repositoryId, retryTimeout.Token);
                    }
                    catch (Exception) { /* Hints are optional; do not expose invocation arguments in logs. */ }
                };
                await connection.StartAsync(timeout.Token);
                await connection.InvokeAsync("WatchSession", sessionId, sessionToken, repositoryId, timeout.Token);
                ResetHint();
            }
            finally { _gate.Release(); }
        }
        catch (Exception)
        {
            _writeHint("PkRelay optional hints are unavailable; use check_inbox for durable messages.");
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return value.Length is > 0 and <= 8192;
    }

    private static void DisableRedirects(HttpMessageHandler handler)
    {
        if (handler is HttpClientHandler clientHandler) clientHandler.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler socketsHandler) socketsHandler.AllowAutoRedirect = false;
        if (handler is DelegatingHandler { InnerHandler: { } inner }) DisableRedirects(inner);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null) await _connection.DisposeAsync();
            _connection = null;
        }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}

/// <summary>Rejects SignalR negotiation redirects before any credential-bearing request leaves the pinned origin.</summary>
public sealed class PinnedOriginHandler(Uri origin, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } target || Uri.Compare(origin, target, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
            throw new HttpRequestException("Relay origin redirect rejected.");
        return base.SendAsync(request, cancellationToken);
    }
}

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using PkRelay.Adapter.Node;

namespace PkRelay.Adapter;

/// <summary>The one way this adapter opens the hub's SignalR hint channel: pinned origin, no redirects, bounded buffers.</summary>
internal static class RelayHubConnection
{
    public const string ChangedEvent = "coordination.changed";
    public const string DiscoveryChangedEvent = "discovery.changed";

    /// <summary>Builds the connection. With <see cref="AdapterOptions.PinnedFingerprint"/> set, SignalR's own handler accepts only that certificate; <paramref name="handler"/> replaces it in tests.</summary>
    public static HubConnection Create(AdapterOptions options, Func<HttpMessageHandler>? handler = null) => new HubConnectionBuilder()
        .WithUrl(new Uri(options.BaseUri, "hubs/coordination"), connection =>
        {
            connection.AccessTokenProvider = () => Task.FromResult<string?>(options.Token);
            connection.Transports = HttpTransportType.LongPolling;
            connection.HttpMessageHandlerFactory = original =>
            {
                var inner = handler?.Invoke() ?? original;
                if (handler is null && options.PinnedFingerprint is { } fingerprint && !Pin(inner, fingerprint))
                    throw new InvalidOperationException("The hint channel cannot pin the hub certificate.");
                DisableRedirects(inner);
                return new PinnedOriginHandler(options.BaseUri, inner);
            };
            connection.TransportMaxBufferSize = RelayForwarder.MaxResponseBytes;
            connection.ApplicationMaxBufferSize = RelayForwarder.MaxResponseBytes;
        })
        .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)])
        .Build();

    public static Task WatchSessionAsync(this HubConnection connection, string sessionId, string sessionToken, string repositoryId, CancellationToken cancellationToken) =>
        connection.InvokeAsync("WatchSession", sessionId, sessionToken, repositoryId, cancellationToken);

    public static Task WatchMachineAsync(this HubConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync("WatchMachine", cancellationToken);

    /// <summary>Sets the pinned-certificate check on every TLS-capable handler in the chain; false when there is none, so the caller fails closed.</summary>
    private static bool Pin(HttpMessageHandler handler, string fingerprint)
    {
        var pinned = false;
        if (handler is HttpClientHandler clientHandler) { clientHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => PinnedCertificate.Matches(certificate, fingerprint); pinned = true; }
        if (handler is SocketsHttpHandler socketsHandler) { socketsHandler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => PinnedCertificate.Matches(certificate, fingerprint); pinned = true; }
        if (handler is DelegatingHandler { InnerHandler: { } inner }) pinned |= Pin(inner, fingerprint);
        return pinned;
    }

    private static void DisableRedirects(HttpMessageHandler handler)
    {
        if (handler is HttpClientHandler clientHandler) clientHandler.AllowAutoRedirect = false;
        if (handler is SocketsHttpHandler socketsHandler) socketsHandler.AllowAutoRedirect = false;
        if (handler is DelegatingHandler { InnerHandler: { } inner }) DisableRedirects(inner);
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

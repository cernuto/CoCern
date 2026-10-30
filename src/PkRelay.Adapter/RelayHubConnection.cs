using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace PkRelay.Adapter;

/// <summary>The one way this adapter opens the hub's SignalR hint channel: pinned origin, no redirects, bounded buffers.</summary>
internal static class RelayHubConnection
{
    public const string ChangedEvent = "coordination.changed";

    public static HubConnection Create(AdapterOptions options) => new HubConnectionBuilder()
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

    public static Task WatchSessionAsync(this HubConnection connection, string sessionId, string sessionToken, string repositoryId, CancellationToken cancellationToken) =>
        connection.InvokeAsync("WatchSession", sessionId, sessionToken, repositoryId, cancellationToken);

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

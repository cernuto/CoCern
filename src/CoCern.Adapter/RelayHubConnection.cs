using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using CoCern.Adapter.Node;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>The one way this adapter opens the hub's SignalR hint channel: pinned origin, no redirects, bounded buffers.</summary>
internal static class RelayHubConnection
{

    /// <summary>Uses the adapter's LAN transport and optional certificate pin. Tests can supply an in-memory handler.</summary>
    public static HubConnection Create(AdapterOptions options, Func<HttpMessageHandler>? handler = null) => new HubConnectionBuilder()
        .WithUrl(new Uri(options.BaseUri, HubEvents.Path), connection =>
        {
            connection.AccessTokenProvider = () => Task.FromResult<string?>(options.Token);
            connection.Transports = HttpTransportType.LongPolling;
            connection.HttpMessageHandlerFactory = original =>
            {
                original.Dispose();
                var inner = handler?.Invoke() ?? RelayForwarder.CreateHandler(options);
                return new PinnedOriginHandler(options.BaseUri, inner);
            };
            connection.TransportMaxBufferSize = RelayForwarder.MaxResponseBytes;
            connection.ApplicationMaxBufferSize = RelayForwarder.MaxResponseBytes;
        })
        .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)])
        .Build();

    public static Task WatchSessionAsync(this HubConnection connection, string sessionId, string sessionToken, string repositoryId, CancellationToken cancellationToken) =>
        connection.InvokeAsync(HubEvents.WatchSession, sessionId, sessionToken, repositoryId, cancellationToken);

    internal static string? SessionHintReason(JsonElement hint, string sessionId, string repositoryId)
    {
        if (hint.ValueKind != JsonValueKind.Object) return null;
        if (hint.TryGetProperty("sessionId", out var session))
        {
            if (session.ValueKind != JsonValueKind.String || session.GetString() != sessionId) return null;
            var eventId = hint.TryGetProperty("eventId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            return eventId switch
            {
                "control" => "control-hint",
                not null when eventId.StartsWith("deadline:", StringComparison.Ordinal) => "deadline",
                _ => "session-hint"
            };
        }
        return hint.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "control"
            && (!hint.TryGetProperty("repositoryId", out var repository) || repository.ValueKind == JsonValueKind.String && repository.GetString() == repositoryId)
            ? "control-hint" : null;
    }

    public static Task WatchMachineAsync(this HubConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync(HubEvents.WatchMachine, cancellationToken);
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

using System.Net;

namespace CoCern.Host;

/// <summary>Identifies same-PC access through a local listener, rather than a network connection forwarded over loopback.</summary>
internal static class HubLocalRequest
{
    internal static bool IsLocal(HttpContext? context)
    {
        if (context?.Connection.RemoteIpAddress is not { } peer
            || !IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)) return false;

        // Socket metadata and the listener recorded by Kestrel are server-owned; Host and forwarding headers cannot prove locality.
        // With an unknown network or socket port, fail closed rather than treating its loopback connections as same-PC access.
        return context.RequestServices?.GetService<HubListener>()?.Https is not { } network
            || network.Port > 0 && context.Connection.LocalPort > 0 && context.Connection.LocalPort != network.Port;
    }
}

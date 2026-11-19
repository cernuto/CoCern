using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CoCern.Contracts;

/// <summary>Network destinations shared by CoCern's local and LAN services.</summary>
public static class LanTransport
{
    public static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>Checks the URI and literal addresses. DNS names are validated when the socket connects.</summary>
    public static bool IsLocalDestination(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && (uri.HostNameType == UriHostNameType.Dns ||
            IPAddress.TryParse(uri.IdnHost, out var address) && IsLocalAddress(address));

    public static SocketsHttpHandler CreateHandler() => CreateHandler(Dns.GetHostAddressesAsync);

    internal static SocketsHttpHandler CreateHandler(Func<string, CancellationToken, Task<IPAddress[]>> resolve) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context, resolve, cancellationToken),
        SslOptions =
        {
            CertificateChainPolicy = new X509ChainPolicy
            {
                DisableCertificateDownloads = true,
                RevocationMode = X509RevocationMode.NoCheck,
                ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") } // TLS server authentication
            }
        }
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken cancellationToken)
    {
        var uri = context.InitialRequestMessage.RequestUri;
        if (uri is null || !IsLocalDestination(uri))
            throw new HttpRequestException("CoCern requires a loopback or LAN destination.");

        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? new[] { literal }
            : await resolve(endpoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsLocalAddress(address) ||
            uri.IsLoopback && !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)))
            throw new HttpRequestException("The CoCern service resolved outside its local network boundary.");

        // Connect to the checked addresses. TLS still verifies the hostname from the original request.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

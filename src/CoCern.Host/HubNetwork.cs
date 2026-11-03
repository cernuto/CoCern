using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CoCern.Host;

/// <summary>The hub's self-signed TLS certificate. Other PCs pin its SHA-256 fingerprint, so an existing file is reused and never silently replaced.</summary>
public static class HubCertificate
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(730);

    /// <summary>Loads the certificate at <paramref name="path"/>, or creates and saves a self-signed ECDSA P-256 one for <paramref name="hostName"/> and every non-loopback IPv4 address. A file that exists but cannot be loaded fails with a clear message and is left untouched.</summary>
    public static X509Certificate2 LoadOrCreate(string path, string hostName)
    {
        if (!File.Exists(path)) Create(path, hostName);
        return Load(path);
    }

    /// <summary>Uppercase hex SHA-256 of the raw certificate; comparisons are case-insensitive.</summary>
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static X509Certificate2 Load(string path)
    {
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            if (certificate.HasPrivateKey) return certificate;
            certificate.Dispose();
            throw new CryptographicException("The file holds no private key.");
        }
        catch (Exception error) when (error is CryptographicException or IOException)
        {
            throw new InvalidOperationException($"The hub certificate file '{path}' exists but could not be loaded. It was left unchanged. Restore it from a backup, or delete it to create a new certificate (every PC that pinned the old fingerprint must then re-pin the new one).", error);
        }
    }

    private static void Create(string path, string hostName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        foreach (var address in NonLoopbackIPv4Addresses()) names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now + Validity);
        WritePrivately(path, certificate.Export(X509ContentType.Pfx));
    }

    /// <summary>The private key must never be readable by anyone else, so the file is created already restricted to the current user under a temporary name and only then moved into place.</summary>
    internal static void WritePrivately(string path, byte[] pfx)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = CreatePrivateFile(temporary)) stream.Write(pfx);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { /* another start created it first: use that one */ }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    internal static IEnumerable<IPAddress> NonLoopbackIPv4Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(i => i.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Distinct();
}

/// <summary>What the network listener serves, once Kestrel has set it up: its HTTPS port and certificate fingerprint. Null while it is off.</summary>
internal sealed class HubListener
{
    public (int Port, string Fingerprint)? Https { get; set; }
    /// <summary>The loopback HTTPS listener local chats pin: its port and certificate fingerprint. Null while it is off.</summary>
    public (int Port, string Fingerprint)? Local { get; set; }
}

/// <summary>The optional network listener: HTTPS on every interface with the hub certificate, beside the loopback HTTP endpoint that keeps working unchanged. Off unless <c>Relay:Network:Enabled</c> is true.</summary>
internal static class HubNetwork
{
    public const string DefaultUrl = "http://127.0.0.1:5271";
    private const int DefaultHttpsPort = 5273;
    private const int DefaultLocalHttpsPort = 5272;
    public const string CertificateFile = "hub-certificate.pfx";
    /// <summary>Where the certificate was made before it moved beside the database: the content root's data folder.</summary>
    private const string ContentRootCertificatePath = "data/" + CertificateFile;

    public static bool Enabled(IConfiguration configuration) => bool.TryParse(configuration["Relay:Network:Enabled"], out var enabled) && enabled;

    /// <summary>Plain HTTP beyond loopback would carry bearer tokens in clear text before any handler could refuse them, so such a url stops startup.</summary>
    public static void RefusePlainNetworkHttp(IConfiguration configuration)
    {
        // Both places Kestrel reads addresses from: urls, and any endpoints in configuration (Kestrel:Endpoints:<name>:Url).
        var urls = (configuration["urls"] ?? DefaultUrl).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"] ?? "").Where(url => url.Length > 0));
        foreach (var url in urls)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
            var loopback = Uri.TryCreate(url.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"), UriKind.Absolute, out var uri)
                && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
            if (!loopback) throw new InvalidOperationException($"The url '{url}' would serve plain HTTP beyond this PC, so tokens would cross the network in clear text. Bind http to 127.0.0.1 or localhost, and set Relay:Network:Enabled=true for HTTPS on the network.");
        }
    }

    /// <summary>
    /// The HTTPS listeners, both with the hub certificate (see <see cref="CertificatePath"/>):
    /// <list type="bullet">
    /// <item>Loopback HTTPS (<c>Relay:Local:HttpsPort</c>, default 5272; 0 turns it off): local chats connect here and pin the certificate,
    /// so a process that takes the port while the hub is down never receives their token. Its address and fingerprint go to
    /// <c>local-hub.json</c> beside the database, where Connect finds them.</item>
    /// <item>Network HTTPS on every interface, only with <c>Relay:Network:Enabled</c>.</item>
    /// </list>
    /// Kestrel ignores <c>urls</c> once endpoints are set in code, so the loopback HTTP endpoint(s) are recreated here too.
    /// </summary>
    public static void Configure(IConfiguration configuration, IHostEnvironment environment, KestrelServerOptions options)
    {
        var network = Enabled(configuration);
        var localPort = configuration.GetValue("Relay:Local:HttpsPort", DefaultLocalHttpsPort);
        if (!network && localPort == 0) return;
        var port = configuration.GetValue("Relay:Network:HttpsPort", DefaultHttpsPort);
        var logger = options.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger("CoCern.Host.HubNetwork");
        foreach (var url in (configuration["urls"] ?? DefaultUrl).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ListenHttp(options, url);
        var certificate = HubCertificate.LoadOrCreate(CertificatePath(configuration, environment, logger), Environment.MachineName);
        // The certificate's private key is a persisted Windows key container that only Dispose removes, so release it when the host stops.
        options.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(certificate.Dispose);
        var fingerprint = HubCertificate.Fingerprint(certificate);
        var listener = options.ApplicationServices.GetRequiredService<HubListener>();
        if (localPort != 0)
        {
            options.Listen(IPAddress.Loopback, localPort, listen => listen.UseHttps(certificate));
            listener.Local = (localPort, fingerprint);
            WriteLocalHub(configuration, environment, localPort, fingerprint, logger);
        }
        if (network)
        {
            options.ListenAnyIP(port, listen => listen.UseHttps(certificate));
            listener.Https = (port, fingerprint);
        }
        logger?.LogInformation("Hub HTTPS: this PC on port {LocalPort}, other PCs {Network}; certificate fingerprint {Fingerprint}", localPort, network ? $"on port {port}" : "off", fingerprint);
    }

    public const string LocalHubFile = "local-hub.json";

    /// <summary>
    /// The hub certificate's file: <c>Relay:Network:CertificatePath</c> when set (relative to the content root), else
    /// <c>hub-certificate.pfx</c> beside the database, with <c>local-hub.json</c>. The content root depends on how the hub starts (Visual
    /// Studio uses <c>src/CoCern.Host</c>, <c>dotnet CoCern.Host.dll</c> the current folder) while the database does not, so every way of
    /// starting one hub finds the one certificate whose fingerprint other PCs and this PC's apps pinned. A certificate made before at the
    /// content root's <c>data</c> folder, with none beside the database yet, is moved there, so its fingerprint survives; when the move
    /// fails, it is used where it is.
    /// </summary>
    internal static string CertificatePath(IConfiguration configuration, IHostEnvironment environment, ILogger? logger = null)
    {
        var configured = configuration["Relay:Network:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.Combine(environment.ContentRootPath, configured);
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RelayHostBuilderExtensions.DatabasePath(configuration, environment))!, CertificateFile));
        var earlier = Path.GetFullPath(Path.Combine(environment.ContentRootPath, ContentRootCertificatePath));
        if (File.Exists(path) || !File.Exists(earlier) || string.Equals(path, earlier, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written afresh rather than moved, so the copy is private to this user however the old file's permissions were inherited.
            HubCertificate.WritePrivately(path, File.ReadAllBytes(earlier));
            File.Delete(earlier);
            logger?.LogInformation("Moved the hub certificate from {From} to {To}, beside the database; its fingerprint is unchanged.", earlier, path);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that did land is the same certificate; otherwise the old file still serves, so the fingerprint stays the same either way.
            if (File.Exists(path))
            {
                logger?.LogWarning("Copied the hub certificate from {From} to {To}, beside the database, but could not remove the old copy; delete it. {Reason}", earlier, path, exception.Message);
                return path;
            }
            logger?.LogWarning("Could not move the hub certificate from {From} to {To}; using it where it is. {Reason}", earlier, path, exception.Message);
            return earlier;
        }
    }

    /// <summary>Records where local chats reach the hub over pinned HTTPS. Not a secret; rewritten each start. Best effort.</summary>
    private static void WriteLocalHub(IConfiguration configuration, IHostEnvironment environment, int port, string fingerprint, ILogger? logger)
    {
        try
        {
            var folder = Path.GetDirectoryName(RelayHostBuilderExtensions.DatabasePath(configuration, environment))!;
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, LocalHubFile);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new { url = $"https://127.0.0.1:{port}/", fingerprint }));
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { logger?.LogWarning("Could not write {File}; Connect will use plain HTTP on this PC.", LocalHubFile); }
    }

    private static void ListenHttp(KestrelServerOptions options, string url)
    {
        if (!Uri.TryCreate(url.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException($"With the hub's own HTTPS listeners, every configured url must be an http address, but found '{url}'. The HTTPS endpoints are Relay:Local:HttpsPort and Relay:Network:HttpsPort.");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) options.ListenLocalhost(uri.Port);
        else if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)) options.Listen(address, uri.Port);
        else throw new InvalidOperationException($"The hub cannot bind the url '{url}': use an IP address or localhost.");
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace PkRelay.Host;

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
    private static void WritePrivately(string path, byte[] pfx)
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

    private static IEnumerable<IPAddress> NonLoopbackIPv4Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(i => i.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Distinct();
}

/// <summary>The optional network listener: HTTPS on every interface with the hub certificate, beside the loopback HTTP endpoint that keeps working unchanged. Off unless <c>Relay:Network:Enabled</c> is true.</summary>
internal static class HubNetwork
{
    public const string DefaultUrl = "http://127.0.0.1:5271";
    private const int DefaultHttpsPort = 5273;
    private const string DefaultCertificatePath = "data/hub-certificate.pfx";

    public static bool Enabled(IConfiguration configuration) => bool.TryParse(configuration["Relay:Network:Enabled"], out var enabled) && enabled;

    /// <summary>Kestrel ignores <c>urls</c> once endpoints are set in code, so this recreates the loopback HTTP endpoint(s) itself and adds the HTTPS one.</summary>
    public static void Configure(IConfiguration configuration, IHostEnvironment environment, KestrelServerOptions options)
    {
        if (!Enabled(configuration)) return;
        var port = configuration.GetValue("Relay:Network:HttpsPort", DefaultHttpsPort);
        var configured = configuration["Relay:Network:CertificatePath"];
        var path = Path.Combine(environment.ContentRootPath, string.IsNullOrWhiteSpace(configured) ? DefaultCertificatePath : configured);
        foreach (var url in (configuration["urls"] ?? DefaultUrl).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ListenHttp(options, url);
        var certificate = HubCertificate.LoadOrCreate(path, Environment.MachineName);
        // The certificate's private key is a persisted Windows key container that only Dispose removes, so release it when the host stops.
        options.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(certificate.Dispose);
        options.ListenAnyIP(port, listen => listen.UseHttps(certificate));
        var logger = options.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger("PkRelay.Host.HubNetwork");
        logger?.LogInformation("Hub listening on https port {Port}; certificate fingerprint {Fingerprint}", port, HubCertificate.Fingerprint(certificate));
    }

    private static void ListenHttp(KestrelServerOptions options, string url)
    {
        if (!Uri.TryCreate(url.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException($"Relay:Network:Enabled requires every configured url to be an http address, but found '{url}'. The HTTPS endpoint is configured by Relay:Network:HttpsPort.");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) options.ListenLocalhost(uri.Port);
        else if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)) options.Listen(address, uri.Port);
        else throw new InvalidOperationException($"Relay:Network:Enabled cannot bind the url '{url}': use an IP address or localhost.");
    }
}

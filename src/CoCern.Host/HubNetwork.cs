using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using CoCern.Contracts;

namespace CoCern.Host;

/// <summary>The hub's self-signed TLS certificate. Other PCs pin its SHA-256 fingerprint, so an existing file is reused and never silently replaced.</summary>
public static class HubCertificate
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(730);

    /// <summary>Loads the hub's existing identity, restoring its private backup if needed. Only a new hub with no identity evidence may create one.</summary>
    public static X509Certificate2 LoadOrCreate(string path, string hostName, bool allowCreate = true) =>
        LoadOrCreate(path, hostName, allowCreate, expectedFingerprint: null);

    internal static X509Certificate2 LoadOrCreate(string path, string hostName, bool allowCreate, string? expectedFingerprint)
    {
        var backupPath = path + ".backup";
        var markerPath = path + ".identity";
        var hasBackup = EvidenceExists(backupPath);
        var hasMarker = EvidenceExists(markerPath);
        if (!EvidenceExists(path))
        {
            if (hasBackup)
            {
                using var backup = Load(backupPath, out var backupBytes);
                var fingerprint = CertificateFingerprint.Of(backup);
                ValidateFingerprint(backupPath, fingerprint, expectedFingerprint);
                if (EvidenceExists(markerPath)) ValidateMarker(markerPath, fingerprint);
                WritePrivately(path, backupBytes);
                // A concurrent start may have won the primary write. Never derive artifacts from the losing candidate.
                ValidateCertificate(path, fingerprint);
            }
            else
            {
                if (hasMarker || !allowCreate || expectedFingerprint is not null)
                    throw RecoveryError(path, "The established hub identity is missing, and no automatic backup is available.");
                Create(path, hostName);
            }
        }

        var certificate = Load(path, out var primaryBytes);
        try
        {
            var fingerprint = CertificateFingerprint.Of(certificate);
            ValidateFingerprint(path, fingerprint, expectedFingerprint);
            // Validate all existing evidence before creating anything missing. Conflicting files must remain untouched.
            if (EvidenceExists(backupPath)) ValidateCertificate(backupPath, fingerprint);
            if (EvidenceExists(markerPath)) ValidateMarker(markerPath, fingerprint);
            if (!EvidenceExists(backupPath)) WritePrivately(backupPath, primaryBytes);
            ValidateCertificate(backupPath, fingerprint);
            if (!EvidenceExists(markerPath)) WritePrivately(markerPath, System.Text.Encoding.UTF8.GetBytes(fingerprint));
            ValidateMarker(markerPath, fingerprint);
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    internal static bool EvidenceExists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw RecoveryError(path, "An identity file path is occupied by a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw RecoveryError(path, "The identity file could not be inspected.", error);
        }
    }

    private static byte[] ReadValidatedCertificate(string path, string? expected, out string fingerprint)
    {
        using var certificate = Load(path, out var bytes);
        fingerprint = CertificateFingerprint.Of(certificate);
        ValidateFingerprint(path, fingerprint, expected);
        return bytes;
    }

    internal static void ValidateCertificate(string path, string fingerprint) =>
        ReadValidatedCertificate(path, fingerprint, out _);

    internal static void CopyLegacyCertificate(string source, string destination, string? expected)
    {
        var bytes = ReadValidatedCertificate(source, expected, out var fingerprint);
        WritePrivately(destination, bytes);
        ValidateCertificate(destination, fingerprint);
    }

    private static void ValidateFingerprint(string path, string actual, string? expected)
    {
        if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
            throw RecoveryError(path, "The certificate does not match the hub's established identity.");
    }

    private static void ValidateMarker(string path, string fingerprint)
    {
        try
        {
            if (!string.Equals(File.ReadAllText(path), fingerprint, StringComparison.Ordinal))
                throw RecoveryError(path, "The identity marker does not match the hub certificate.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw RecoveryError(path, "The identity marker could not be read.", error);
        }
    }

    internal static InvalidOperationException RecoveryError(string path, string reason, Exception? error = null) =>
        new($"{reason} Identity file: '{path}'. Restore the original hub certificate and its matching backup and identity marker from a trusted backup, then restart the hub. Existing identity files were not replaced.", error);

    private static X509Certificate2 Load(string path, out byte[] bytes)
    {
        try
        {
            bytes = File.ReadAllBytes(path);
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, null);
            if (certificate.HasPrivateKey) return certificate;
            certificate.Dispose();
            throw new CryptographicException("The file holds no private key.");
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw RecoveryError(path, "The hub certificate file exists but could not be loaded. It was left unchanged.", error);
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
    public const string CertificateFile = "hub-certificate.pfx";
    /// <summary>Where the certificate was made before it moved beside the database: the content root's data folder.</summary>
    private const string ContentRootCertificatePath = "data/" + CertificateFile;

    public static bool Enabled(IConfiguration configuration) => bool.TryParse(configuration["Relay:Network:Enabled"], out var enabled) && enabled;

    /// <summary>Plain HTTP beyond loopback would carry bearer tokens in clear text before any handler could refuse them, so such a url stops startup.</summary>
    public static void RefusePlainNetworkHttp(IConfiguration configuration)
    {
        // Both places Kestrel reads addresses from: urls, and any endpoints in configuration (Kestrel:Endpoints:<name>:Url).
        var urls = (configuration["urls"] ?? HubAddresses.LocalUrl).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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
        var localPort = configuration.GetValue("Relay:Local:HttpsPort", HubAddresses.LocalHttpsPort);
        if (!network && localPort == 0) return;
        var port = configuration.GetValue("Relay:Network:HttpsPort", HubAddresses.NetworkHttpsPort);
        var logger = options.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger("CoCern.Host.HubNetwork");
        foreach (var url in (configuration["urls"] ?? HubAddresses.LocalUrl).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ListenHttp(options, url);
        var localRecord = Path.Combine(Path.GetDirectoryName(RelayHostBuilderExtensions.DatabasePath(configuration, environment))!, LocalHubRecord.FileName);
        var hasLocalRecord = HubCertificate.EvidenceExists(localRecord);
        var expectedFingerprint = hasLocalRecord ? CertificateFingerprint.Normalize(LocalHubRecord.Read(Path.GetDirectoryName(localRecord)!)?.Fingerprint) : null;
        if (hasLocalRecord && (expectedFingerprint is null || !CertificateFingerprint.IsValid(expectedFingerprint)))
            throw HubCertificate.RecoveryError(localRecord, "The recorded hub identity could not be read or is invalid.");
        var certificate = HubCertificate.LoadOrCreate(CertificatePath(configuration, environment, logger, expectedFingerprint), Environment.MachineName, allowCreate: !hasLocalRecord, expectedFingerprint);
        // The certificate's private key is a persisted Windows key container that only Dispose removes, so release it when the host stops.
        options.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(certificate.Dispose);
        var fingerprint = CertificateFingerprint.Of(certificate);
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


    /// <summary>
    /// The hub certificate's file: <c>Relay:Network:CertificatePath</c> when set (relative to the content root), else
    /// <c>hub-certificate.pfx</c> beside the database, with <c>local-hub.json</c>. The content root depends on how the hub starts (Visual
    /// Studio uses <c>src/CoCern.Host</c>, <c>dotnet CoCern.Host.dll</c> the current folder) while the database does not, so every way of
    /// starting one hub finds the one certificate whose fingerprint other PCs and this PC's apps pinned. A certificate made before at the
    /// content root's <c>data</c> folder, with none beside the database yet, is copied there, so its fingerprint survives. The source is
    /// retained because another hub may still use it; when the copy fails, it is used where it is.
    /// </summary>
    internal static string CertificatePath(IConfiguration configuration, IHostEnvironment environment, ILogger? logger = null, string? expectedFingerprint = null)
    {
        var configured = configuration["Relay:Network:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.Combine(environment.ContentRootPath, configured);
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RelayHostBuilderExtensions.DatabasePath(configuration, environment))!, CertificateFile));
        // This hub's recovery evidence takes precedence over importing a possibly different legacy identity.
        if (HubCertificate.EvidenceExists(path + ".backup") || HubCertificate.EvidenceExists(path + ".identity")) return path;
        var earlier = Path.GetFullPath(Path.Combine(environment.ContentRootPath, ContentRootCertificatePath));
        if (HubCertificate.EvidenceExists(path) || !HubCertificate.EvidenceExists(earlier) || string.Equals(path, earlier, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return path;
        // A startup record belongs to this database, so verify a legacy candidate before importing any bytes.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write a private copy however the source's permissions were inherited; another hub may still use the source.
            HubCertificate.CopyLegacyCertificate(earlier, path, expectedFingerprint);
            logger?.LogInformation("Copied the hub certificate from {From} to {To}, beside the database, retaining the source; its fingerprint is unchanged.", earlier, path);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that did land is the same certificate; otherwise the old file still serves, so the fingerprint stays the same either way.
            if (File.Exists(path))
            {
                logger?.LogWarning("The hub certificate copy from {From} to {To}, beside the database, exists despite a copy error; retaining the source. {Reason}", earlier, path, exception.Message);
                return path;
            }
            logger?.LogWarning("Could not copy the hub certificate from {From} to {To}; using it where it is. {Reason}", earlier, path, exception.Message);
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
            var target = Path.Combine(folder, LocalHubRecord.FileName);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, new LocalHubRecord($"https://127.0.0.1:{port}/", fingerprint).ToJson());
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { logger?.LogWarning("Could not write {File}; Connect will use plain HTTP on this PC.", LocalHubRecord.FileName); }
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

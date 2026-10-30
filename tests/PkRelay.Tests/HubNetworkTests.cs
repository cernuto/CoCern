using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using PkRelay.Host;
using Xunit;

namespace PkRelay.Tests;

public sealed class HubNetworkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pkrelay-network-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string CertificatePath => Path.Combine(root, "nested", "hub-certificate.pfx");

    [Fact]
    public void Certificate_is_reused_across_starts()
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        using var second = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        Assert.Equal(HubCertificate.Fingerprint(first), HubCertificate.Fingerprint(second));
        Assert.Matches("^[0-9A-F]{64}$", HubCertificate.Fingerprint(first));
        Assert.True(second.HasPrivateKey);
        using var key = second.GetECDsaPublicKey();
        Assert.Equal(256, key!.KeySize);
        Assert.InRange((second.NotAfter - second.NotBefore).TotalDays, 729, 732);
    }

    [Fact]
    public void Certificate_names_the_host_and_has_no_password()
    {
        using var certificate = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("hub-pc", names.EnumerateDnsNames());
        Assert.DoesNotContain(names.EnumerateIPAddresses(), IPAddress.IsLoopback);
        using var reloaded = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, null);
        Assert.Equal(HubCertificate.Fingerprint(certificate), HubCertificate.Fingerprint(reloaded));
    }

    [Fact]
    public void Certificate_file_that_cannot_be_loaded_fails_startup_and_is_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CertificatePath)!);
        File.WriteAllText(CertificatePath, "not a certificate");
        var error = Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(CertificatePath, "hub-pc"));
        Assert.Contains("could not be loaded", error.Message);
        Assert.Equal("not a certificate", File.ReadAllText(CertificatePath));
    }

    [Fact]
    public void Certificate_file_is_private_to_current_user()
    {
        using var _ = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(CertificatePath).GetAccessControl();
            var me = WindowsIdentity.GetCurrent().User!;
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(rules);
            Assert.Equal(me, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.False(rule.IsInherited);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CertificatePath));
        Assert.Equal([CertificatePath], Directory.GetFiles(Path.GetDirectoryName(CertificatePath)!));
    }

    [Fact]
    public async Task Network_listener_serves_health_over_https_with_the_generated_certificate()
    {
        var (httpPort, httpsPort) = (FreePort(), FreePort());
        await using var app = await StartAsync(httpPort, httpsPort, enabled: true);
        X509Certificate2? presented = null;
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => { presented = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.RawData); return true; } };
        using var client = new HttpClient(handler);
        var response = await client.GetAsync($"https://127.0.0.1:{httpsPort}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stored = HubCertificate.LoadOrCreate(CertificatePath, "ignored-because-reused");
        Assert.NotNull(presented);
        Assert.Equal(HubCertificate.Fingerprint(stored), HubCertificate.Fingerprint(presented!));
        using var loopback = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await loopback.GetAsync($"http://127.0.0.1:{httpPort}/health")).StatusCode);
    }

    [Fact]
    public async Task Disabled_network_opens_no_https_listener_and_creates_no_certificate()
    {
        var (httpPort, httpsPort) = (FreePort(), FreePort());
        await using var app = await StartAsync(httpPort, httpsPort, enabled: false);
        using var loopback = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await loopback.GetAsync($"http://127.0.0.1:{httpPort}/health")).StatusCode);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, httpsPort));
        Assert.False(File.Exists(CertificatePath));
    }

    private async Task<WebApplication> StartAsync(int httpPort, int httpsPort, bool enabled)
    {
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{httpPort}",
            ["Relay:DatabasePath"] = Path.Combine(root, "relay.db"),
            ["Relay:SiloPort"] = FreePort().ToString(),
            ["Relay:GatewayPort"] = FreePort().ToString(),
            ["Relay:ClusterId"] = Guid.NewGuid().ToString("N"),
            ["Relay:Clients:0:Token"] = "network-test-secret-not-a-production-token",
            ["Relay:Clients:0:PrincipalId"] = "test-user",
            ["Relay:Clients:0:Repositories:0"] = "repo",
            ["Relay:Network:Enabled"] = enabled ? "true" : "false",
            ["Relay:Network:HttpsPort"] = httpsPort.ToString(),
            ["Relay:Network:CertificatePath"] = Path.Combine("nested", "hub-certificate.pfx")
        });
        builder.AddRelayHost();
        var app = builder.Build();
        app.UseRelayPipeline();
        app.MapRelayEndpoints();
        await app.StartAsync();
        return app;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

public sealed class HubNetworkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-network-" + Guid.NewGuid().ToString("N"));

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
        await app.StopAsync();
    }

    [SkippableFact]
    public async Task Stopping_the_host_removes_the_key_container_the_hub_certificate_created()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("Only Windows keeps a loaded certificate's private key in a persisted key container.");
        List<HttpsConnectionAdapterOptions> listeners = [];
        List<string> containers = [];
        // Another host can keep its own certificate alive while this host stops.
        var foreignCertificate = HubCertificate.LoadOrCreate(Path.Combine(root, "foreign-certificate.pfx"), "other-hub");
        try
        {
            var foreignContainer = KeyContainer(foreignCertificate);
            containers.Add(foreignContainer);
            string hubContainer;
            var (httpPort, httpsPort) = (FreePort(), FreePort());
            await using (var app = await StartAsync(httpPort, httpsPort, enabled: true, configureHttps: listeners.Add))
            {
                var certificate = Assert.IsType<X509Certificate2>(Assert.Single(listeners.Select(listener => listener.ServerCertificate).Distinct()));
                hubContainer = KeyContainer(certificate);
                containers.Add(hubContainer);
                Assert.NotEqual(foreignContainer, hubContainer);
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"https://127.0.0.1:{httpsPort}/health")).StatusCode);
                Assert.Contains(hubContainer, KeyContainers());
                await app.StopAsync();
            }
            var remaining = KeyContainers();
            Assert.DoesNotContain(hubContainer, remaining);
            Assert.Contains(foreignContainer, remaining);
        }
        finally
        {
            // Pass or fail, this test's keys leave the user's profile: disposing a certificate removes its container, and a container file
            // still there under a name this test recorded is deleted.
            foreignCertificate.Dispose();
            foreach (var certificate in listeners.Select(listener => listener.ServerCertificate).OfType<X509Certificate2>().Distinct()) certificate.Dispose();
            DeleteKeyContainers(containers);
        }
    }

    [Fact]
    public async Task Disabled_network_opens_no_network_https_listener_but_serves_this_pc_over_pinned_https()
    {
        var (httpPort, httpsPort, localPort) = (FreePort(), FreePort(), FreePort());
        await using var app = await StartAsync(httpPort, httpsPort, enabled: false, localPort);
        using var loopback = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await loopback.GetAsync($"http://127.0.0.1:{httpPort}/health")).StatusCode);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, httpsPort));
        // Local chats get loopback HTTPS with the hub certificate, recorded for Connect.
        Assert.True(File.Exists(CertificatePath));
        using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, localPort);
        Assert.True(File.Exists(Path.Combine(root, HubNetwork.LocalHubFile)));
    }

    [Fact]
    public void The_certificate_defaults_to_the_database_folder_however_the_hub_is_started()
    {
        var expected = Path.Combine(root, "data", HubNetwork.CertificateFile);
        // Visual Studio: the content root is the project folder and the database ../../data/relay.db. The start script: the content root is
        // the folder it runs from and the database an absolute path. One certificate, beside local-hub.json, either way.
        Assert.Equal(expected, HubNetwork.CertificatePath(Settings("../../data/relay.db"), new HubEnvironment(Path.Combine(root, "src", "CoCern.Host"))));
        Assert.Equal(expected, HubNetwork.CertificatePath(Settings(Path.Combine(root, "data", "relay.db")), new HubEnvironment(Path.Combine(root, "elsewhere"))));
        // With the default database (the content root's data folder) it stays where it always was.
        Assert.Equal(expected, HubNetwork.CertificatePath(Settings(null), new HubEnvironment(root)));
        // A configured path is kept as configured, relative to the content root.
        var content = Path.Combine(root, "src", "CoCern.Host");
        Assert.Equal(Path.Combine(content, "keys/hub.pfx"), HubNetwork.CertificatePath(Settings("../../data/relay.db", "keys/hub.pfx"), new HubEnvironment(content)));
        // Choosing the path writes nothing.
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_certificate_made_at_the_content_root_moves_beside_the_database_and_keeps_its_fingerprint()
    {
        var content = Path.Combine(root, "src", "CoCern.Host");
        var earlier = Path.Combine(content, "data", HubNetwork.CertificateFile);
        string fingerprint;
        using (var made = HubCertificate.LoadOrCreate(earlier, "hub-pc")) fingerprint = HubCertificate.Fingerprint(made);

        var path = HubNetwork.CertificatePath(Settings("../../data/relay.db"), new HubEnvironment(content));
        Assert.Equal(Path.Combine(root, "data", HubNetwork.CertificateFile), path);
        Assert.False(File.Exists(earlier));
        using var moved = HubCertificate.LoadOrCreate(path, "ignored-because-reused");
        Assert.Equal(fingerprint, HubCertificate.Fingerprint(moved));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        // Started the other way, from the repository with the same database, the hub finds the same certificate.
        Assert.Equal(path, HubNetwork.CertificatePath(Settings(Path.Combine(root, "data", "relay.db")), new HubEnvironment(root)));
    }

    [Fact]
    public void A_certificate_already_beside_the_database_wins_and_the_one_at_the_content_root_is_left_alone()
    {
        var content = Path.Combine(root, "src", "CoCern.Host");
        var earlier = Path.Combine(content, "data", HubNetwork.CertificateFile);
        var beside = Path.Combine(root, "data", HubNetwork.CertificateFile);
        using (HubCertificate.LoadOrCreate(earlier, "hub-pc")) { }
        string fingerprint;
        using (var current = HubCertificate.LoadOrCreate(beside, "hub-pc")) fingerprint = HubCertificate.Fingerprint(current);
        var before = File.ReadAllBytes(earlier);

        Assert.Equal(beside, HubNetwork.CertificatePath(Settings("../../data/relay.db"), new HubEnvironment(content)));
        Assert.Equal(before, File.ReadAllBytes(earlier));
        using var used = HubCertificate.LoadOrCreate(beside, "ignored-because-reused");
        Assert.Equal(fingerprint, HubCertificate.Fingerprint(used));
    }

    [Fact]
    public async Task Without_a_configured_path_the_hub_makes_its_certificate_beside_the_database_and_records_its_fingerprint()
    {
        var (httpPort, httpsPort, localPort) = (FreePort(), FreePort(), FreePort());
        await using var app = await StartAsync(httpPort, httpsPort, enabled: false, localPort, configureCertificate: false);
        var path = Path.Combine(root, HubNetwork.CertificateFile);
        using var stored = HubCertificate.LoadOrCreate(path, "ignored-because-reused");
        using var local = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, HubNetwork.LocalHubFile)));
        Assert.Equal(HubCertificate.Fingerprint(stored), local.RootElement.GetProperty("fingerprint").GetString());
        Assert.False(File.Exists(CertificatePath));
    }

    [Fact]
    public async Task With_both_https_listeners_off_no_certificate_is_made()
    {
        var (httpPort, httpsPort) = (FreePort(), FreePort());
        await using var app = await StartAsync(httpPort, httpsPort, enabled: false, localPort: 0);
        using var loopback = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await loopback.GetAsync($"http://127.0.0.1:{httpPort}/health")).StatusCode);
        Assert.False(File.Exists(CertificatePath));
    }

    private async Task<WebApplication> StartAsync(int httpPort, int httpsPort, bool enabled, int? localPort = null, Action<HttpsConnectionAdapterOptions>? configureHttps = null, bool configureCertificate = true)
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
            ["Relay:Local:HttpsPort"] = (localPort ?? FreePort()).ToString(),
            ["Relay:Network:CertificatePath"] = configureCertificate ? Path.Combine("nested", "hub-certificate.pfx") : null
        });
        if (configureHttps is not null) builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(configureHttps));
        builder.AddRelayHost();
        var app = builder.Build();
        app.UseRelayPipeline();
        app.MapRelayEndpoints();
        await app.StartAsync();
        return app;
    }

    [SupportedOSPlatform("windows")]
    private static string KeyContainer(X509Certificate2 certificate)
    {
        using var key = Assert.IsType<ECDsaCng>(certificate.GetECDsaPrivateKey());
        return Assert.IsType<string>(key.Key.UniqueName);
    }

    /// <summary>The current user's CNG key store.</summary>
    private static string KeyFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto", "Keys");

    /// <summary>File names in the current user's CNG key store, compared only with the containers owned by this test.</summary>
    private static HashSet<string> KeyContainers() =>
        Directory.Exists(KeyFolder) ? Directory.GetFiles(KeyFolder).Select(Path.GetFileName).OfType<string>().ToHashSet() : [];

    /// <summary>Deletes what is left of the named containers, which only this test created.</summary>
    private static void DeleteKeyContainers(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var file = Path.Combine(KeyFolder, Path.GetFileName(name));
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IConfiguration Settings(string? databasePath, string? certificatePath = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Relay:DatabasePath"] = databasePath, ["Relay:Network:CertificatePath"] = certificatePath }).Build();

    private sealed class HubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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

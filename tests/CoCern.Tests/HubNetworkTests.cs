using CoCern.Adapter;
using CoCern.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Microsoft.Extensions.DependencyInjection;
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
    public void Certificate_file_is_private_to_current_user()
    {
        using var certificate = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        foreach (var path in IdentityArtifacts(CertificatePath)) AssertPrivate(path);
        Assert.Equal(IdentityArtifacts(CertificatePath).Order(), Directory.GetFiles(Path.GetDirectoryName(CertificatePath)!).Order());
    }

    [Fact]
    public void Missing_primary_restores_the_exact_backup_and_private_key()
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        var original = File.ReadAllBytes(CertificatePath);
        // Also exercise recovery of an older installation with a backup but no marker yet.
        File.WriteAllBytes(CertificatePath + ".backup", original);
        File.Delete(CertificatePath + ".identity");
        File.Delete(CertificatePath);

        using var restored = HubCertificate.LoadOrCreate(CertificatePath, "a-different-host");

        Assert.Equal(original, File.ReadAllBytes(CertificatePath));
        Assert.Equal(CertificateFingerprint.Of(first), CertificateFingerprint.Of(restored));
        using var originalPublicKey = first.GetECDsaPublicKey();
        using var restoredKey = restored.GetECDsaPrivateKey();
        Assert.NotNull(restoredKey);
        byte[] challenge = [1, 2, 3, 4];
        Assert.True(originalPublicKey!.VerifyData(challenge, restoredKey.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
        Assert.Equal(CertificateFingerprint.Of(first), File.ReadAllText(CertificatePath + ".identity"));
        AssertPrivate(CertificatePath);
        AssertPrivate(CertificatePath + ".identity");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Corrupt_backup_fails_without_creating_or_replacing_an_identity(bool primaryExists)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CertificatePath)!);
        byte[]? original = null;
        if (primaryExists)
        {
            using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
            original = File.ReadAllBytes(CertificatePath);
        }
        File.WriteAllText(CertificatePath + ".backup", "damaged backup");
        File.Delete(CertificatePath + ".identity");

        var error = Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(CertificatePath, "hub-pc"));

        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("damaged backup", File.ReadAllText(CertificatePath + ".backup"));
        Assert.False(File.Exists(CertificatePath + ".identity"));
        if (primaryExists) Assert.Equal(original, File.ReadAllBytes(CertificatePath));
        else Assert.False(File.Exists(CertificatePath));
    }

    [Fact]
    public void Established_marker_prevents_regeneration_when_both_copies_are_lost()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CertificatePath)!);
        var marker = new string('A', 64);
        File.WriteAllText(CertificatePath + ".identity", marker);

        var error = Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(CertificatePath, "hub-pc"));

        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(marker, File.ReadAllText(CertificatePath + ".identity"));
        Assert.False(File.Exists(CertificatePath));
        Assert.False(File.Exists(CertificatePath + ".backup"));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("backup")]
    [InlineData("primary")]
    public async Task Recorded_identity_rejects_a_divergent_candidate_before_any_file_changes(string candidate)
    {
        var database = Path.Combine(root, "separate", "relay.db");
        var destination = Path.Combine(root, "separate", HubNetwork.CertificateFile);
        var source = candidate == "legacy" ? Path.Combine(root, "data", HubNetwork.CertificateFile) : destination;
        using var different = HubCertificate.LoadOrCreate(source, "different-hub");
        if (candidate != "legacy")
        {
            File.Delete(destination + ".identity");
            if (candidate == "backup") File.Delete(destination);
            else File.Delete(destination + ".backup");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var recordPath = Path.Combine(Path.GetDirectoryName(database)!, LocalHubRecord.FileName);
        File.WriteAllText(recordPath, new LocalHubRecord("https://127.0.0.1:5272/", new string('A', 64)).ToJson());
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigureNetworkAsync(database));

        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private async Task ConfigureNetworkAsync(string database)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Configuration.AddConfiguration(Settings(database));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{FreePort()}",
            ["Relay:Local:HttpsPort"] = FreePort().ToString()
        });
        builder.Services.AddSingleton<HubListener>();
        await using var app = builder.Build();
        var options = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions { ApplicationServices = app.Services };
        try { HubNetwork.Configure(builder.Configuration, builder.Environment, options); }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Concurrent_and_repeated_starts_share_one_complete_private_identity()
    {
        using var ready = new CountdownEvent(8);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            using var certificate = HubCertificate.LoadOrCreate(CertificatePath, $"hub-{index}");
            Assert.True(certificate.HasPrivateKey);
            return CertificateFingerprint.Of(certificate);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var allReady = ready.Wait(TimeSpan.FromSeconds(30));
        start.Set();
        var fingerprints = await Task.WhenAll(attempts);
        Assert.True(allReady);
        Assert.Single(fingerprints.Distinct());
        using var repeated = HubCertificate.LoadOrCreate(CertificatePath, "repeated");
        using var backup = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath + ".backup", null);
        Assert.Equal(fingerprints[0], CertificateFingerprint.Of(repeated));
        Assert.Equal(fingerprints[0], CertificateFingerprint.Of(backup));
        Assert.Equal(fingerprints[0], File.ReadAllText(CertificatePath + ".identity"));
        Assert.Equal(IdentityArtifacts(CertificatePath).Order(), Directory.GetFiles(Path.GetDirectoryName(CertificatePath)!).Order());
        foreach (var path in IdentityArtifacts(CertificatePath)) AssertPrivate(path);
    }

    private static string[] IdentityArtifacts(string path) => [path, path + ".backup", path + ".identity"];

    private static void AssertPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            var me = WindowsIdentity.GetCurrent().User!;
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(rules);
            Assert.Equal(me, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.False(rule.IsInherited);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
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
        Assert.Equal(CertificateFingerprint.Of(stored), CertificateFingerprint.Of(presented!));
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
        Assert.True(File.Exists(Path.Combine(root, LocalHubRecord.FileName)));
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

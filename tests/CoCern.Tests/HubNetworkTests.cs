using CoCern.Contracts;
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
    public void Certificate_is_reused_across_starts()
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        using var second = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        Assert.Equal(CertificateFingerprint.Of(first), CertificateFingerprint.Of(second));
        Assert.Matches("^[0-9A-F]{64}$", CertificateFingerprint.Of(first));
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
        Assert.Equal(CertificateFingerprint.Of(certificate), CertificateFingerprint.Of(reloaded));
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

    [Fact]
    public void Existing_primary_gains_a_private_backup_and_identity_marker()
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        var original = File.ReadAllBytes(CertificatePath);
        File.Delete(CertificatePath + ".backup");
        File.Delete(CertificatePath + ".identity");

        using var reused = HubCertificate.LoadOrCreate(CertificatePath, "ignored");

        Assert.Equal(original, File.ReadAllBytes(CertificatePath + ".backup"));
        Assert.Equal(CertificateFingerprint.Of(first), File.ReadAllText(CertificatePath + ".identity"));
        foreach (var path in IdentityArtifacts(CertificatePath)) AssertPrivate(path);
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
    public void Backup_without_a_private_key_cannot_replace_a_missing_primary()
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        using var publicOnly = X509CertificateLoader.LoadCertificate(first.RawData);
        var invalidBackup = publicOnly.Export(X509ContentType.Pfx);
        File.WriteAllBytes(CertificatePath + ".backup", invalidBackup);
        File.Delete(CertificatePath);

        Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(CertificatePath, "hub-pc"));

        Assert.False(File.Exists(CertificatePath));
        Assert.Equal(invalidBackup, File.ReadAllBytes(CertificatePath + ".backup"));
    }

    [Fact]
    public void Conflicting_backup_is_left_unchanged_and_does_not_establish_a_marker()
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        var original = File.ReadAllBytes(CertificatePath);
        var otherPath = Path.Combine(root, "other", "other.pfx");
        using var other = HubCertificate.LoadOrCreate(otherPath, "other-hub");
        var conflicting = File.ReadAllBytes(otherPath);
        File.WriteAllBytes(CertificatePath + ".backup", conflicting);
        File.Delete(CertificatePath + ".identity");

        Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(CertificatePath, "hub-pc"));

        Assert.Equal(original, File.ReadAllBytes(CertificatePath));
        Assert.Equal(conflicting, File.ReadAllBytes(CertificatePath + ".backup"));
        Assert.False(File.Exists(CertificatePath + ".identity"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conflicting_marker_is_left_unchanged_and_prevents_recovery(bool primaryMissing)
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        var original = File.ReadAllBytes(CertificatePath);
        File.WriteAllBytes(CertificatePath + ".backup", original);
        File.WriteAllText(CertificatePath + ".identity", new string('0', 64));
        if (primaryMissing) File.Delete(CertificatePath);

        Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(CertificatePath, "hub-pc"));

        Assert.Equal(original, File.ReadAllBytes(CertificatePath + ".backup"));
        Assert.Equal(new string('0', 64), File.ReadAllText(CertificatePath + ".identity"));
        if (primaryMissing) Assert.False(File.Exists(CertificatePath));
        else Assert.Equal(original, File.ReadAllBytes(CertificatePath));
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

    [Fact]
    public void Creation_can_be_explicitly_forbidden_without_any_identity_files()
    {
        var error = Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(CertificatePath, "hub-pc", allowCreate: false));

        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        foreach (var path in IdentityArtifacts(CertificatePath)) Assert.False(File.Exists(path));
    }

    [Fact]
    public void Forbidding_creation_still_allows_restoring_the_established_backup()
    {
        using var first = HubCertificate.LoadOrCreate(CertificatePath, "hub-pc");
        var original = File.ReadAllBytes(CertificatePath);
        File.Delete(CertificatePath);

        using var restored = HubCertificate.LoadOrCreate(CertificatePath, "ignored", allowCreate: false);

        Assert.Equal(original, File.ReadAllBytes(CertificatePath));
        Assert.Equal(CertificateFingerprint.Of(first), CertificateFingerprint.Of(restored));
        Assert.True(restored.HasPrivateKey);
    }

    [Fact]
    public async Task Existing_local_hub_record_prevents_a_new_identity_at_startup()
    {
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, LocalHubRecord.FileName);
        var record = new LocalHubRecord("https://127.0.0.1:5272/", new string('A', 64)).ToJson();
        File.WriteAllText(recordPath, record);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Relay:DatabasePath"] = Path.Combine(root, "relay.db"),
            ["Relay:Network:CertificatePath"] = CertificatePath,
            ["urls"] = $"http://127.0.0.1:{FreePort()}"
        });
        builder.Services.AddSingleton<HubListener>();
        await using var app = builder.Build();
        var options = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions { ApplicationServices = app.Services };

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => HubNetwork.Configure(builder.Configuration, builder.Environment, options));
            Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(CertificatePath));
            Assert.Equal(record, File.ReadAllText(recordPath));
        }
        finally { await app.StopAsync(); }
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

    [Theory]
    [InlineData("legacy")]
    [InlineData("backup")]
    [InlineData("primary")]
    public async Task Recorded_identity_allows_a_matching_candidate_and_retains_its_source(string candidate)
    {
        var database = Path.Combine(root, "separate", "relay.db");
        var destination = Path.Combine(root, "separate", HubNetwork.CertificateFile);
        var source = candidate == "legacy" ? Path.Combine(root, "data", HubNetwork.CertificateFile) : destination;
        using var original = HubCertificate.LoadOrCreate(source, "established-hub");
        var originalBytes = File.ReadAllBytes(source);
        var fingerprint = CertificateFingerprint.Of(original);
        if (candidate != "legacy")
        {
            File.Delete(destination + ".identity");
            if (candidate == "backup") File.Delete(destination);
            else File.Delete(destination + ".backup");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(database)!, LocalHubRecord.FileName), new LocalHubRecord("https://127.0.0.1:5272/", fingerprint).ToJson());

        await ConfigureNetworkAsync(database);

        Assert.Equal(originalBytes, File.ReadAllBytes(source));
        Assert.Equal(originalBytes, File.ReadAllBytes(destination));
        Assert.Equal(originalBytes, File.ReadAllBytes(destination + ".backup"));
        Assert.Equal(fingerprint, File.ReadAllText(destination + ".identity"));
        Assert.Equal(fingerprint, LocalHubRecord.Read(Path.GetDirectoryName(database)!)!.Fingerprint);
        foreach (var artifact in IdentityArtifacts(destination)) AssertPrivate(artifact);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"url\":\"https://127.0.0.1:5272/\",\"fingerprint\":\"unusable\"}")]
    [InlineData("{\"url\":\"https://127.0.0.1:5272/\"}")]
    public async Task Unusable_recorded_identity_prevents_legacy_import_and_stays_unchanged(string record)
    {
        var legacyPath = Path.Combine(root, "data", HubNetwork.CertificateFile);
        using var legacy = HubCertificate.LoadOrCreate(legacyPath, "legacy-hub");
        var database = Path.Combine(root, "separate", "relay.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(database)!, LocalHubRecord.FileName), record);
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
    public void A_certificate_made_at_the_content_root_is_copied_beside_the_database_and_keeps_its_fingerprint()
    {
        var content = Path.Combine(root, "src", "CoCern.Host");
        var earlier = Path.Combine(content, "data", HubNetwork.CertificateFile);
        string fingerprint;
        using (var made = HubCertificate.LoadOrCreate(earlier, "hub-pc")) fingerprint = CertificateFingerprint.Of(made);

        var path = HubNetwork.CertificatePath(Settings("../../data/relay.db"), new HubEnvironment(content));
        Assert.Equal(Path.Combine(root, "data", HubNetwork.CertificateFile), path);
        Assert.True(File.Exists(earlier));
        using var copied = HubCertificate.LoadOrCreate(path, "ignored-because-reused");
        Assert.Equal(fingerprint, CertificateFingerprint.Of(copied));
        foreach (var artifact in IdentityArtifacts(path)) AssertPrivate(artifact);
        Assert.Equal(IdentityArtifacts(path).Order(), Directory.GetFiles(Path.GetDirectoryName(path)!).Order());
        // Started the other way, from the repository with the same database, the hub finds the same certificate.
        Assert.Equal(path, HubNetwork.CertificatePath(Settings(Path.Combine(root, "data", "relay.db")), new HubEnvironment(root)));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Destination_identity_evidence_prevents_importing_a_different_legacy_identity(bool backupExists, bool markerExists, bool corrupt)
    {
        var legacyPath = Path.Combine(root, "data", HubNetwork.CertificateFile);
        var database = Path.Combine(root, "separate", "relay.db");
        var destination = Path.Combine(root, "separate", HubNetwork.CertificateFile);
        using var legacy = HubCertificate.LoadOrCreate(legacyPath, "legacy-hub");
        using var owned = HubCertificate.LoadOrCreate(destination, "separate-hub");
        var legacyBytes = File.ReadAllBytes(legacyPath);
        var ownedBytes = File.ReadAllBytes(destination);
        var marker = File.ReadAllText(destination + ".identity");
        File.Delete(destination);
        if (!backupExists) File.Delete(destination + ".backup");
        if (!markerExists) File.Delete(destination + ".identity");
        if (corrupt)
        {
            if (backupExists) File.WriteAllText(destination + ".backup", "damaged backup");
            else File.WriteAllText(destination + ".identity", marker = "damaged marker");
        }

        var resolved = HubNetwork.CertificatePath(Settings(database), new HubEnvironment(root));

        Assert.Equal(destination, resolved);
        Assert.False(File.Exists(destination));
        if (backupExists && !corrupt)
        {
            using var restored = HubCertificate.LoadOrCreate(resolved, "ignored");
            Assert.Equal(CertificateFingerprint.Of(owned), CertificateFingerprint.Of(restored));
            Assert.Equal(ownedBytes, File.ReadAllBytes(destination));
            foreach (var artifact in IdentityArtifacts(destination)) AssertPrivate(artifact);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => HubCertificate.LoadOrCreate(resolved, "ignored"));
            Assert.False(File.Exists(destination));
            if (backupExists) Assert.Equal("damaged backup", File.ReadAllText(destination + ".backup"));
            else Assert.False(File.Exists(destination + ".backup"));
        }
        if (markerExists || backupExists && !corrupt) Assert.Equal(marker, File.ReadAllText(destination + ".identity"));
        else Assert.False(File.Exists(destination + ".identity"));
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void A_certificate_already_beside_the_database_wins_and_the_one_at_the_content_root_is_left_alone()
    {
        var content = Path.Combine(root, "src", "CoCern.Host");
        var earlier = Path.Combine(content, "data", HubNetwork.CertificateFile);
        var beside = Path.Combine(root, "data", HubNetwork.CertificateFile);
        using (HubCertificate.LoadOrCreate(earlier, "hub-pc")) { }
        string fingerprint;
        using (var current = HubCertificate.LoadOrCreate(beside, "hub-pc")) fingerprint = CertificateFingerprint.Of(current);
        var before = File.ReadAllBytes(earlier);

        Assert.Equal(beside, HubNetwork.CertificatePath(Settings("../../data/relay.db"), new HubEnvironment(content)));
        Assert.Equal(before, File.ReadAllBytes(earlier));
        using var used = HubCertificate.LoadOrCreate(beside, "ignored-because-reused");
        Assert.Equal(fingerprint, CertificateFingerprint.Of(used));
    }

    [Fact]
    public async Task Without_a_configured_path_the_hub_makes_its_certificate_beside_the_database_and_records_its_fingerprint()
    {
        var (httpPort, httpsPort, localPort) = (FreePort(), FreePort(), FreePort());
        await using var app = await StartAsync(httpPort, httpsPort, enabled: false, localPort, configureCertificate: false);
        var path = Path.Combine(root, HubNetwork.CertificateFile);
        using var stored = HubCertificate.LoadOrCreate(path, "ignored-because-reused");
        using var local = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, LocalHubRecord.FileName)));
        Assert.Equal(CertificateFingerprint.Of(stored), local.RootElement.GetProperty("fingerprint").GetString());
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

    [Fact]
    public async Task A_second_hub_with_a_custom_database_retains_the_original_certificate_across_stops_and_restarts()
    {
        var originalPath = Path.Combine(root, "data", HubNetwork.CertificateFile);
        byte[] originalBytes;
        string fingerprint;
        using (var original = HubCertificate.LoadOrCreate(originalPath, "main-hub"))
        {
            fingerprint = CertificateFingerprint.Of(original);
            originalBytes = File.ReadAllBytes(originalPath);
            // StartAsync uses a custom database beside the content root, separate from the main hub's data folder.
            for (var start = 0; start < 2; start++)
            {
                await using var app = await StartAsync(FreePort(), FreePort(), enabled: false, configureCertificate: false);
                var destination = Path.Combine(root, HubNetwork.CertificateFile);
                Assert.True(File.Exists(originalPath));
                Assert.Equal(originalBytes, File.ReadAllBytes(originalPath));
                using var copied = HubCertificate.LoadOrCreate(destination, "ignored-because-reused");
                Assert.Equal(fingerprint, CertificateFingerprint.Of(copied));
                Assert.True(copied.HasPrivateKey);
                using var local = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, LocalHubRecord.FileName)));
                Assert.Equal(fingerprint, local.RootElement.GetProperty("fingerprint").GetString());
                foreach (var artifact in IdentityArtifacts(destination)) AssertPrivate(artifact);
                await app.StopAsync();
            }
        }
        var mainPath = HubNetwork.CertificatePath(Settings(null), new HubEnvironment(root));
        Assert.Equal(originalPath, mainPath);
        using var restartedMain = HubCertificate.LoadOrCreate(mainPath, "main-hub");
        Assert.Equal(fingerprint, CertificateFingerprint.Of(restartedMain));
        Assert.True(restartedMain.HasPrivateKey);
        Assert.Equal(originalBytes, File.ReadAllBytes(mainPath));
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

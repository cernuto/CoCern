using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

public sealed class NodeReleaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-release-" + Guid.NewGuid().ToString("N"));
    private const string Admin = "release-test-admin";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task Enrolled_nonadmin_gets_package_truth_and_exact_verified_bytes()
    {
        // Nodes are offered only self-contained builds: they run without a known .NET host and refuse framework-dependent ones.
        var folder = Package(selfContained: true);
        await using var host = Host(folder);
        using var machine = Client(host, await Enroll(host));
        using var announce = await machine.PostAsJsonAsync("/api/discovery/announce", new { machineName = "PC", chats = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, announce.StatusCode);
        var body = await announce.Content.ReadFromJsonAsync<JsonObject>();
        var release = body!["nodeRelease"]?.Deserialize<NodeRelease>(Json);
        Assert.NotNull(release);
        Assert.Equal("7.8.9+served-package", release.Package.ProductVersion);
        Assert.Equal(1, release.Schema);
        Assert.Equal(1, release.Package.Schema);
        Assert.Equal(1, release.Package.SupervisorProtocol);
        Assert.True(release.Package.SelfContained);
        Assert.Equal(release.ZipSha256, release.ReleaseId);
        Assert.Matches("^[A-F0-9]{64}$", release.ReleaseId);
        using var download = await machine.GetAsync("/join/adapter.zip?release=" + release.ReleaseId);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(release.ZipBytes, bytes.LongLength);
        Assert.Equal(release.ZipSha256, Convert.ToHexString(SHA256.HashData(bytes)));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var dll = zip.GetEntry("CoCern.Adapter.dll")!.Open();
        Assert.Equal(release.AdapterDllSha256, Convert.ToHexString(SHA256.HashData(dll)));
        AssertMetadata(release.Package, NodePackageMetadataReader.Read(zip));
        using var denied = await machine.PostAsJsonAsync("/api/tools/control_request", new { repositoryId = "repo", requestId = "publish-denied", command = "revoke_machine", machineId = "pc", reason = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData("?release=")]
    public async Task Every_release_query_requires_an_enrolled_machine_even_when_malformed(string query)
    {
        await using var host = Host(Package(), jwt: true);
        using var anonymous = Client(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/join/adapter.zip" + query)).StatusCode);
        foreach (var token in new[] { RelayHostFixture.Token, Admin, RelayHostFixture.Jwt(scope: "relay") })
        {
            using var configured = Client(host, token);
            Assert.Equal(HttpStatusCode.Forbidden, (await configured.GetAsync("/join/adapter.zip" + query)).StatusCode);
        }
        using var machine = Client(host, await Enroll(host));
        Assert.Equal(HttpStatusCode.NotFound, (await machine.GetAsync("/join/adapter.zip" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/join/adapter.zip")).StatusCode);
    }

    [Fact]
    public async Task Revoked_and_unauthenticated_machines_receive_no_descriptor_or_release()
    {
        await using var host = Host(Package());
        using var machine = Client(host, await Enroll(host));
        using var admin = Client(host, Admin);
        using var revoke = await admin.PostAsJsonAsync("/api/tools/control_request", new { repositoryId = "repo", requestId = "revoke", command = "revoke_machine", machineId = "pc", reason = "test" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        foreach (var client in new[] { machine, Client(host) })
        {
            using (client)
            {
                using var response = await client.PostAsJsonAsync("/api/discovery/announce", new { machineName = "PC" });
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.DoesNotContain("nodeRelease", await response.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/join/adapter.zip?release=" + new string('A', 64))).StatusCode);
            }
        }
    }

    [Fact]
    public async Task Publication_race_never_returns_B_under_As_identity_and_leases_prevent_pruning()
    {
        var folder = Package();
        var cache = new AdapterZipCache(CacheFolder);
        var a = await cache.ReleaseAsync(folder, default);
        Assert.NotNull(a);
        var original = await Bytes(a.Content);
        File.WriteAllText(Path.Combine(folder, "generation.txt"), "B");
        await using var b = await cache.ReleaseAsync(folder, default);
        Assert.NotNull(b);
        Assert.NotEqual(a.Release.ReleaseId, b.Release.ReleaseId);
        File.WriteAllText(Path.Combine(folder, "generation.txt"), "C");
        await using var c = await cache.ReleaseAsync(folder, default);
        Assert.NotNull(c);
        await using (var slower = await cache.OpenReleaseAsync(a.Release.ReleaseId, default))
        {
            Assert.NotNull(slower);
            Assert.Equal(original, await Bytes(slower.Content));
            Assert.Equal(a.Release.ReleaseId, Convert.ToHexString(SHA256.HashData(await Bytes(slower.Content))));
        }
        Assert.Equal(3, Directory.GetFiles(CacheFolder, "*.zip").Length);
        await a.DisposeAsync();
        await a.DisposeAsync();
        Assert.False(a.Content.CanRead);
        Assert.Equal(2, Directory.GetFiles(CacheFolder, "*.zip").Length);
        Assert.Null(await cache.OpenReleaseAsync(a.Release.ReleaseId, default));
        await using var previous = await cache.OpenReleaseAsync(b.Release.ReleaseId, default);
        Assert.NotNull(previous);
        Assert.Equal(b.Release.ReleaseId, Convert.ToHexString(SHA256.HashData(await Bytes(previous.Content))));
    }

    [Fact]
    public async Task Cache_rejects_planted_or_modified_archive_and_cancellation_propagates()
    {
        var folder = Package();
        var cache = new AdapterZipCache(CacheFolder);
        string id;
        await using (var lease = await cache.ReleaseAsync(folder, default))
        {
            Assert.NotNull(lease);
            id = lease.Release.ReleaseId;
        }
        var path = Assert.Single(Directory.GetFiles(CacheFolder, "*.zip"));
        File.WriteAllText(path, "planted");
        Assert.Null(await cache.OpenReleaseAsync(id, default));
        Assert.Null(await new AdapterZipCache(CacheFolder).OpenReleaseAsync(id, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReleaseAsync(folder, cancellation.Token));
        Assert.Empty(Directory.GetFiles(CacheFolder, "*.part"));
    }

    private string CacheFolder => Path.Combine(root, "cache");
    private string Package(string? protocol = "1", Platform platform = Platform.AnyCpu, bool selfContained = false)
    {
        var folder = Path.Combine(root, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var source = "using System.Reflection; [assembly: AssemblyInformationalVersion(\"7.8.9+served-package\")] " + (protocol is null ? "" : "[assembly: AssemblyMetadata(\"CoCernNodeSupervisorProtocol\", \"" + protocol + "\")] ") + "public static class Candidate { }";
        var compilation = CSharpCompilation.Create("CoCern.Adapter", [CSharpSyntaxTree.ParseText(source)], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, platform: platform));
        var emitted = compilation.Emit(Path.Combine(folder, "CoCern.Adapter.dll"));
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        if (selfContained)
        {
            // A win-x64 self-contained publish as the metadata reader sees it: the runtime it carries, and x64 PE images for the apphost and runtime.
            File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.deps.json"), "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/win-x64\"}}");
            File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}]}}");
            var native = CSharpCompilation.Create("Native", [CSharpSyntaxTree.ParseText("public static class Native { }")], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, platform: Platform.X64));
            foreach (var file in new[] { "CoCern.Adapter.exe", "coreclr.dll", "hostfxr.dll" }) Assert.True(native.Emit(Path.Combine(folder, file)).Success);
            return folder;
        }
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.deps.json"), "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0\"}}");
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"frameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}]}}");
        return folder;
    }

    private RelayHostFixture Host(string folder, bool enabled = true, bool jwt = false, IPAddress? remote = null, Action<IServiceCollection>? services = null) => new(configureJwt: jwt, remoteAddress: remote, settings: new Dictionary<string, string?>
    {
        ["Relay:Discovery:Enabled"] = enabled.ToString(),
        ["Relay:Discovery:Repositories:0"] = "repo",
        ["Relay:Connect:AdapterPath"] = Path.Combine(folder, "CoCern.Adapter.dll"),
        ["Relay:Clients:1:Token"] = Admin,
        ["Relay:Clients:1:PrincipalId"] = "release-admin",
        ["Relay:Clients:1:IsAdministrator"] = "true",
        ["Relay:Clients:1:Repositories:0"] = "repo"
    }, configureServices: collection => { collection.AddSingleton(new AdapterZipCache(CacheFolder)); services?.Invoke(collection); });

    private static HttpClient Client(RelayHostFixture host, string? token = null)
    {
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> Enroll(RelayHostFixture host)
    {
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/api/discovery/enroll", new { machineId = "pc", machineName = "PC", joinCode = RelayHostFixture.JoinCode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["credential"]!.GetValue<string>();
    }

    private static async Task<byte[]> Bytes(Stream content)
    {
        content.Position = 0;
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static void AssertMetadata(NodePackageMetadata expected, NodePackageMetadata? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected with { Frameworks = actual.Frameworks }, actual);
        Assert.Equal(expected.Frameworks, actual.Frameworks);
    }
}

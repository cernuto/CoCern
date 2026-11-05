using System.IO.Compression;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CoCern.Adapter.Development;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

public sealed class NodePackageTests(ITestOutputHelper output)
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "node-package-fixtures", Guid.NewGuid().ToString("N")));
    private static readonly Uri Hub = new("https://enrolled.invalid/prefix/");
    private static readonly NodeHostCapabilities Host = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");

    [Theory]
    [InlineData("positive")]
    [InlineData("cross-origin-redirect")]
    public async Task Production_pinned_origin_pipeline_authenticates_only_its_enrolled_endpoint(string mode)
    {
        var (release, bytes) = Archive(Package());
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        // Import through the same Windows PFX path as the existing TLS fixtures.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        var received = 0;
        var redirected = 0;
        var authenticated = false;
        await using var other = await TlsServer(certificate, context => { Interlocked.Increment(ref redirected); return context.Response.WriteAsync("unexpected"); });
        var otherOrigin = Origin(other);
        await using var app = await TlsServer(certificate, async context =>
        {
            Interlocked.Increment(ref received);
            authenticated = context.Request.Headers.Authorization.ToString() == "Bearer machine-test";
            if (mode is "same-origin-redirect" or "cross-origin-redirect")
            {
                context.Response.StatusCode = 307;
                context.Response.Headers.Location = mode == "same-origin-redirect" ? "/redirected" : otherOrigin.ToString();
                return;
            }
            await context.Response.Body.WriteAsync(bytes);
        });
        var origin = Origin(app);
        var pin = mode == "wrong-pin" ? new string('A', 64) : CertificateFingerprint.Of(certificate);
        using var transport = new NodeTransport().CreateHandler(pin, _ => { });
        var concrete = Assert.IsType<HttpClientHandler>(transport);
        Assert.False(concrete.AllowAutoRedirect);
        Assert.False(concrete.UseCookies);
        using var http = new HttpClient(new PinnedOriginHandler(origin, transport)) { Timeout = TimeSpan.FromSeconds(10) };
        var staged = await NodePackage.StageAsync(release, mode == "wrong-hub" ? otherOrigin : origin, http, "machine-test", Updates(), Host, default);
        output.WriteLine("PinnedPipelineMode={0};HTTPRequests={1};RedirectTargetRequests={2};Authenticated={3}", mode, received, redirected, authenticated);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal(0, redirected);
        Assert.Equal(mode is "wrong-pin" or "wrong-hub" ? 0 : 1, received);
        if (mode == "positive") Assert.NotNull(staged); else Assert.Null(staged);
        if (received != 0) Assert.True(authenticated);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(otherOrigin));
        Assert.Equal(0, redirected);
    }

    [Fact]
    public async Task Verified_archive_is_staged_privately_with_shared_tree_digest_and_original_state_untouched()
    {
        var package = Package();
        var (release, bytes) = Archive(package);
        using var handler = new ReplyHandler(_ => Response(bytes));
        using var http = new HttpClient(handler);
        var updates = Updates();
        var before = Snapshot(Path.GetDirectoryName(updates)!);
        var staged = await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default);
        Assert.NotNull(staged);
        Assert.Equal(release, staged.Release);
        Assert.Equal(release.AdapterDllSha256, staged.Image.AdapterDllSha256);
        Assert.Equal(AdapterDeployment.ContentHash(package), staged.Image.ContentId);
        Assert.Equal(Path.Combine(updates, "versions", staged.Image.ContentId), staged.Directory);
        Assert.Equal(staged.Image.ContentId, AdapterDeployment.ContentHash(staged.Directory));
        Assert.Equal(new Uri(Hub, "/join/adapter.zip?release=" + release.ReleaseId), Assert.Single(handler.Requests));
        Assert.False(handler.Disposed);
        Assert.Equal(before, Snapshot(Path.GetDirectoryName(updates)!, excludeUpdates: true));
        AssertOwnerOnly(staged.Directory);
        AssertOwnerOnly(Path.Combine(staged.Directory, "CoCern.Adapter.dll"));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.Location.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(301)]
    public async Task Unavailable_or_redirect_response_never_requests_another_location(int status)
    {
        var (release, bytes) = Archive(Package());
        using var handler = new ReplyHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Headers = { Location = new Uri("https://other.invalid/credential-target") }, Content = new ByteArrayContent(bytes) });
        using var http = new HttpClient(handler);
        var updates = Updates();
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        Assert.Single(handler.Requests);
        AssertNoVersion(updates);
    }

    [Theory]
    [InlineData("hash")]
    public async Task Descriptor_or_content_disagreement_never_commits(string defect)
    {
        var (release, bytes) = Archive(Package());
        release = defect switch
        {
            "schema" => release with { Schema = 2 },
            "package-schema" => release with { Package = release.Package with { Schema = 2 } },
            "protocol" => release with { Package = release.Package with { SupervisorProtocol = 2 } },
            "release-id" => release with { ReleaseId = new string('A', 64) },
            "hash" => release with { ReleaseId = new string('B', 64), ZipSha256 = new string('B', 64) },
            "length" => release with { ZipBytes = release.ZipBytes + 1 },
            "dll" => release with { AdapterDllSha256 = new string('C', 64) },
            "version" => release with { Package = release.Package with { ProductVersion = "other" } },
            "framework" => release with { Package = release.Package with { Frameworks = [new("Microsoft.NETCore.App", "10.0.1")] } },
            "rid" => release with { Package = release.Package with { Rid = "win-arm64" } },
            _ => release with { Package = release.Package with { ProductVersion = new string('X', 65537) } }
        };
        using var http = Client(bytes);
        var updates = Updates();
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        AssertNoVersion(updates);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("\\\\server\\share\\escape")]
    public async Task Windows_path_alias_or_escape_is_rejected_before_committing(string name)
    {
        var package = Package();
        var (release, bytes) = Archive(package, archive => WriteEntry(archive, name, "attacker"u8.ToArray()));
        using var http = Client(bytes);
        var updates = Updates();
        var before = Snapshot(root);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        AssertNoVersion(updates);
        Assert.Equal(before, Snapshot(root, true));
    }

    [Fact]
    public async Task Actual_stream_limit_refuses_compression_bomb_even_when_declared_length_lies()
    {
        var (release, bytes) = Archive(Package(), archive =>
        {
            using var stream = archive.CreateEntry("bomb", CompressionLevel.SmallestSize).Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 129; i++) stream.Write(block);
        });
        var central = CentralHeaders(bytes).Last();
        BitConverter.GetBytes(1u).CopyTo(bytes, central + 24);
        var local = checked((int)BitConverter.ToUInt32(bytes, central + 42));
        BitConverter.GetBytes(1u).CopyTo(bytes, local + 22);
        release = Describe(release.Package, bytes, release.AdapterDllSha256);
        using var http = Client(bytes);
        var updates = Updates();
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        AssertNoVersion(updates);
    }

    [Theory]
    [InlineData("updates")]
    public async Task Reparse_point_in_updates_or_ancestor_is_refused(string location)
    {
        var (release, bytes) = Archive(Package());
        var updates = Updates();
        var target = Path.Combine(root, "foreign");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "preserved"), "unrelated");
        string link;
        if (location == "updates") link = updates;
        else if (location == "versions") { PrivateFixtureDirectory(updates); link = Path.Combine(updates, "versions"); }
        else { link = Path.Combine(root, "alias"); updates = Path.Combine(link, "node", "updates"); }
        Directory.CreateSymbolicLink(link, target);
        using var http = Client(bytes);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        Assert.Equal(["preserved"], Directory.GetFiles(target).Select(path => Path.GetFileName(path)!).ToArray());
    }

    [Theory]
    [InlineData("http://external.invalid/")]
    public async Task Invalid_enrolled_origin_is_refused_without_a_request(string hub)
    {
        var (release, bytes) = Archive(Package());
        using var handler = new ReplyHandler(_ => Response(bytes));
        using var http = new HttpClient(handler);
        Assert.Null(await NodePackage.StageAsync(release, new Uri(hub), http, "machine-test", Updates(), Host, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Launch_uses_known_dotnet_and_argument_boundaries_and_rejects_modified_image()
    {
        var package = Package();
        File.WriteAllText(Path.Combine(package, "CoCern.Adapter.exe"), "unused-apphost");
        var updates = Updates();
        var image = InstallImage(package, updates);
        var node = Path.GetDirectoryName(updates)!;
        var nonce = Guid.NewGuid();
        var spec = NodePackage.Launch(image, node, nonce, Host);
        Assert.NotNull(spec);
        Assert.Equal(Host.DotnetPath, spec.FileName);
        Assert.Equal([Path.Combine(updates, "versions", image.ContentId, "CoCern.Adapter.dll"), "--node-worker", "--data-dir", node, "--activation-nonce", nonce.ToString("D")], spec.Arguments);
        Assert.Equal(4, NodePackage.Launch(image, node, null, Host)!.Arguments.Length);
        File.AppendAllText(Path.Combine(updates, "versions", image.ContentId, "CoCern.Adapter.dll"), "changed");
        Assert.Null(NodePackage.Launch(image, node, null, Host));
    }

    [Fact]
    public void Launch_rejects_foreign_content_id_without_traversing_it()
    {
        var package = Package();
        var metadata = NodePackageMetadataReader.Read(package)!;
        var image = new NodeImage("../../foreign", metadata, DllHash(package), new string('A', 64));
        Assert.Null(NodePackage.Launch(image, Path.GetDirectoryName(Updates())!, null, Host));
    }

    private string Updates()
    {
        var node = Path.Combine(root, "original node data " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(node);
        foreach (var name in new[] { "credential", "node.json", "wake.db", "session.json" }) File.WriteAllText(Path.Combine(node, name), "preserved-" + name);
        return Path.Combine(node, "updates");
    }

    private string Package(bool twoFrameworks = false, Platform platform = Platform.AnyCpu)
    {
        var folder = Path.Combine(root, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        const string source = "using System.Reflection; [assembly: AssemblyInformationalVersion(\"7.8.9+package\")] [assembly: AssemblyMetadata(\"CoCernNodeSupervisorProtocol\", \"1\")] public static class Candidate { }";
        var compilation = CSharpCompilation.Create("CoCern.Adapter", [CSharpSyntaxTree.ParseText(source)], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, platform: platform));
        var emitted = compilation.Emit(Path.Combine(folder, "CoCern.Adapter.dll"));
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.deps.json"), "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0\"}}");
        var frameworks = twoFrameworks ? new[] { new NodeFramework("Microsoft.NETCore.App", "10.0.0"), new NodeFramework("Microsoft.AspNetCore.App", "10.0.0") } : [new NodeFramework("Microsoft.NETCore.App", "10.0.0")];
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json"), JsonSerializer.Serialize(new { runtimeOptions = new { tfm = "net10.0", frameworks = frameworks.Select(f => new { name = f.Name, version = f.Version }) } }));
        return folder;
    }

    private static (NodeRelease Release, byte[] Bytes) Archive(string package, Action<ZipArchive>? modify = null, bool fallbackMetadata = false)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var path in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories)) WriteEntry(archive, Path.GetRelativePath(package, path).Replace('\\', '/'), File.ReadAllBytes(path));
            modify?.Invoke(archive);
        }
        var bytes = buffer.ToArray();
        var metadata = NodePackageMetadataReader.Read(package);
        if (!fallbackMetadata) Assert.NotNull(metadata);
        metadata ??= new(1, "7.8.9+package", "net10.0", "any", false, [new("Microsoft.NETCore.App", "10.0.0")], 1);
        return (Describe(metadata, bytes, DllHash(package)), bytes);
    }

    private static NodeRelease Describe(NodePackageMetadata metadata, byte[] bytes, string dllHash)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new(1, hash, metadata, bytes.LongLength, hash, dllHash);
    }

    private static ZipArchiveEntry WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
        return entry;
    }

    private static string DllHash(string folder) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "CoCern.Adapter.dll"))));
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpClient Client(byte[] bytes) => new(new ReplyHandler(_ => Response(bytes)));
    private static void AssertNoVersion(string updates) => Assert.True(!Directory.Exists(Path.Combine(updates, "versions")) || !Directory.EnumerateDirectories(Path.Combine(updates, "versions")).Any());

    private static string[] Snapshot(string folder, bool excludeUpdates = false) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(p => !excludeUpdates || !Path.GetRelativePath(folder, p).Split(Path.DirectorySeparatorChar).Contains("updates")).Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(folder, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

    private static void AssertOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        var security = info is DirectoryInfo directory ? (FileSystemSecurity)directory.GetAccessControl() : ((FileInfo)info).GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))) Assert.Equal(user, rule.IdentityReference);
    }

    private static NodeImage InstallImage(string package, string updates)
    {
        var id = AdapterDeployment.ContentHash(package);
        var destination = Path.Combine(updates, "versions", id);
        foreach (var folder in new[] { updates, Path.Combine(updates, "versions"), destination }) PrivateFixtureDirectory(folder);
        foreach (var file in Directory.GetFiles(package)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        return new(id, NodePackageMetadataReader.Read(package)!, DllHash(package), new string('A', 64));
    }

    private static void PrivateFixtureDirectory(string folder)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectorySecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).Create(security);
    }

    private static IEnumerable<int> CentralHeaders(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length - 46; i++) if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) yield return i;
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(reply(request));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static async Task<WebApplication> TlsServer(X509Certificate2 certificate, RequestDelegate serve)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.Run(serve);
        await app.StartAsync();
        return app;
    }

    private static Uri Origin(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
}

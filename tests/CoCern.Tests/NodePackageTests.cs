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
    [InlineData("positive")] [InlineData("wrong-pin")] [InlineData("same-origin-redirect")]
    [InlineData("cross-origin-redirect")] [InlineData("wrong-hub")]
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
    public void Content_hash_preserves_existing_deployment_digest_without_publishing()
    {
        var package = Package();
        var deployed = AdapterDeployment.Publish(package, Path.Combine(root, "development"));
        Assert.Equal(deployed.Version, AdapterDeployment.ContentHash(package));
        Assert.Matches("^[a-f0-9]{64}$", AdapterDeployment.ContentHash(package));
        Assert.False(File.Exists(Path.Combine(package, "current.json")));
    }

    [Theory]
    [InlineData("relative")] [InlineData("wrong-leaf")] [InlineData("public-existing")]
    public async Task Staging_requires_original_absolute_private_updates_root(string defect)
    {
        var (release, bytes) = Archive(Package());
        var updates = Updates();
        updates = defect switch { "relative" => "relative-node/updates", "wrong-leaf" => Path.Combine(Path.GetDirectoryName(updates)!, "credential"), _ => updates };
        if (defect == "public-existing") Directory.CreateDirectory(updates);
        using var http = Client(bytes);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
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

    [Fact]
    public async Task Equal_metadata_with_distinct_framework_arrays_and_order_is_accepted()
    {
        var package = Package(twoFrameworks: true);
        var (release, bytes) = Archive(package);
        release = release with { Package = release.Package with { Frameworks = release.Package.Frameworks.Reverse().Select(f => new NodeFramework(f.Name, f.Version)).ToArray() } };
        var host = Host with { InstalledFrameworks = [new("Microsoft.NETCore.App", "10.0.14"), new("Microsoft.AspNetCore.App", "10.0.14")] };
        using var http = Client(bytes);
        Assert.NotNull(await NodePackage.StageAsync(release, Hub, http, "machine-test", Updates(), host, default));
    }

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(500)]
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

    [Fact]
    public async Task Same_origin_redirect_is_also_refused()
    {
        var (release, _) = Archive(Package());
        using var handler = new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri(Hub, "/different") } });
        using var http = new HttpClient(handler);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", Updates(), Host, default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("pin")] [InlineData("timeout")] [InlineData("truncated")]
    public async Task Transport_failure_retains_original_node_and_does_not_dispose_http(string failure)
    {
        var (release, bytes) = Archive(Package());
        using var handler = new ReplyHandler(_ => failure switch
        {
            "pin" => throw new HttpRequestException("fixed-test-pin-failure"),
            "timeout" => throw new TaskCanceledException("fixed-test-timeout"),
            _ => Response(bytes[..(bytes.Length / 2)])
        });
        using var http = new HttpClient(handler);
        var updates = Updates();
        var before = Snapshot(Path.GetDirectoryName(updates)!);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        Assert.False(handler.Disposed);
        Assert.Equal(before, Snapshot(Path.GetDirectoryName(updates)!, true));
        AssertNoVersion(updates);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Chunked_transfer_checks_actual_byte_count_without_a_content_length(bool truncated)
    {
        var (release, bytes) = Archive(Package());
        var transferred = truncated ? bytes[..(bytes.Length / 2)] : bytes.Concat(new byte[] { 0 }).ToArray();
        using var handler = new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(transferred)) });
        using var http = new HttpClient(handler);
        var updates = Updates();
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        AssertNoVersion(updates);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_before_and_during_download()
    {
        var (release, bytes) = Archive(Package());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var http = Client(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodePackage.StageAsync(release, Hub, http, "machine-test", Updates(), Host, canceled.Token));
        using var during = new CancellationTokenSource();
        using var handler = new ReplyHandler(_ => { during.Cancel(); throw new OperationCanceledException(during.Token); });
        using var duringHttp = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodePackage.StageAsync(release, Hub, duringHttp, "machine-test", Updates(), Host, during.Token));
    }

    [Theory]
    [InlineData("schema")] [InlineData("package-schema")] [InlineData("protocol")] [InlineData("release-id")]
    [InlineData("hash")] [InlineData("length")] [InlineData("dll")]
    [InlineData("version")] [InlineData("framework")] [InlineData("rid")] [InlineData("manifest-limit")]
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
    [InlineData("../escape")] [InlineData("a/../../escape")] [InlineData("a\\..\\escape")]
    [InlineData("/escape")] [InlineData("\\\\server\\share\\escape")] [InlineData("C:/escape")]
    [InlineData("file:stream")] [InlineData("a\0b")] [InlineData("a//b")]
    [InlineData("a/./b")] [InlineData("trailing.")] [InlineData("trailing ")]
    [InlineData("CON")] [InlineData("aux.txt")] [InlineData("dir/COM1.log")] [InlineData("LPT9")]
    [InlineData("conin$")] [InlineData("com¹.txt")] [InlineData("CON .txt")]
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

    [Theory]
    [InlineData("duplicate")] [InlineData("case")] [InlineData("ancestor-file")] [InlineData("ancestor-case-alias")]
    [InlineData("symlink")] [InlineData("hardlink-extra")] [InlineData("reparse")]
    [InlineData("zip64-extra")] [InlineData("declared-file-limit")] [InlineData("declared-overflow")]
    [InlineData("entry-count")] [InlineData("runtime-policy")] [InlineData("runtimeconfig-limit")]
    [InlineData("declared-total-limit")]
    public async Task Archive_structure_and_runtime_policy_are_validated(string defect)
    {
        var package = Package();
        if (defect == "runtime-policy") File.WriteAllText(Path.Combine(package, "CoCern.Adapter.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"rollForward\":\"Major\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        if (defect == "runtimeconfig-limit") File.AppendAllText(Path.Combine(package, "CoCern.Adapter.runtimeconfig.json"), new string(' ', 65537));
        var (release, bytes) = Archive(package, archive =>
        {
            if (defect is "duplicate" or "case") WriteEntry(archive, defect == "case" ? "cocern.adapter.dll" : "CoCern.Adapter.dll", [1]);
            if (defect == "ancestor-file") { WriteEntry(archive, "a", [1]); WriteEntry(archive, "a/b", [2]); }
            if (defect == "ancestor-case-alias") { WriteEntry(archive, "a/x", [1]); WriteEntry(archive, "A/y", [2]); }
            if (defect is "symlink" or "reparse") { var entry = WriteEntry(archive, "link", [1]); entry.ExternalAttributes = defect == "symlink" ? unchecked((int)0xA1FF0000) : 0x400; }
            if (defect == "entry-count") for (var i = 0; i < 8192; i++) WriteEntry(archive, "entry" + i, []);
            if (defect == "declared-total-limit") for (var i = 0; i < 7; i++) WriteEntry(archive, "large" + i, []);
        }, fallbackMetadata: defect is "runtime-policy" or "runtimeconfig-limit");
        if (defect is "hardlink-extra" or "zip64-extra") bytes = InsertCentralExtra(bytes, defect == "hardlink-extra" ? (ushort)0x000d : (ushort)0x0001);
        if (defect is "declared-file-limit" or "declared-overflow") PatchFirstCentralLength(bytes, defect == "declared-file-limit" ? 128u * 1024 * 1024 + 1 : uint.MaxValue);
        if (defect == "declared-total-limit")
        {
            foreach (var central in CentralHeaders(bytes).Skip(3))
            {
                BitConverter.GetBytes(128u * 1024 * 1024).CopyTo(bytes, central + 24);
                BitConverter.GetBytes(128u * 1024 * 1024).CopyTo(bytes, checked((int)BitConverter.ToUInt32(bytes, central + 42)) + 22);
            }
        }
        release = Describe(release.Package, bytes, release.AdapterDllSha256);
        using var http = Client(bytes);
        var updates = Updates();
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        AssertNoVersion(updates);
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
    [InlineData("oversize-zip")] [InlineData("zero-length")] [InlineData("negative-length")]
    public async Task Invalid_zip_length_is_refused_before_any_transfer(string defect)
    {
        var (release, bytes) = Archive(Package());
        release = release with { ZipBytes = defect switch { "oversize-zip" => 256L * 1024 * 1024 + 1, "negative-length" => -1, _ => 0 } };
        using var handler = new ReplyHandler(_ => Response(bytes));
        using var http = new HttpClient(handler);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", Updates(), Host, default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("CoCern.Adapter.exe")] [InlineData("coreclr.dll")] [InlineData("hostfxr.dll")]
    public async Task Self_contained_missing_runtime_file_cannot_be_staged(string missing)
    {
        var package = SelfContainedPackage();
        var metadata = NodePackageMetadataReader.Read(package)!;
        Assert.True(metadata.SelfContained);
        File.Delete(Path.Combine(package, missing));
        var (_, bytes) = Archive(package, fallbackMetadata: true);
        var release = Describe(metadata, bytes, DllHash(package));
        using var http = Client(bytes);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", Updates(), new(null, [], "win-x64"), default));
    }

    [Fact]
    public async Task Matching_self_contained_package_needs_no_installed_runtime()
    {
        var (release, bytes) = Archive(SelfContainedPackage());
        using var http = Client(bytes);
        var updates = Updates();
        var staged = await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, new(null, [], "win-x64"), default);
        Assert.NotNull(staged);
        var launch = NodePackage.Launch(staged.Image, Path.GetDirectoryName(updates)!, null, new(null, [], "win-x64"));
        Assert.NotNull(launch);
        Assert.Equal(Path.Combine(staged.Directory, "CoCern.Adapter.exe"), launch.FileName);
        Assert.Equal(["--node-worker", "--data-dir", Path.GetDirectoryName(updates)!], launch.Arguments);
        Assert.Null(NodePackage.Launch(staged.Image, Path.GetDirectoryName(updates)!, null, new(null, [], "win-arm64")));
    }

    [Fact]
    public async Task Existing_content_is_rehashed_and_metadata_checked_instead_of_name_trusted()
    {
        var package = Package();
        var (release, bytes) = Archive(package);
        using var http = Client(bytes);
        var updates = Updates();
        var first = await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default);
        Assert.NotNull(first);
        var second = await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default);
        Assert.NotNull(second);
        Assert.Equal(first.Directory, second.Directory);
        File.AppendAllText(Path.Combine(first.Directory, "CoCern.Adapter.deps.json"), "tampered");
        var tampered = File.ReadAllBytes(Path.Combine(first.Directory, "CoCern.Adapter.deps.json"));
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        Assert.Equal(tampered, File.ReadAllBytes(Path.Combine(first.Directory, "CoCern.Adapter.deps.json")));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Existing_content_hardlink_is_refused_without_changing_retained_bytes(bool stage)
    {
        var (release, bytes) = Archive(Package());
        using var http = Client(bytes);
        var updates = Updates();
        var first = await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default);
        Assert.NotNull(first);
        var path = Path.Combine(first.Directory, "CoCern.Adapter.dll");
        var retained = Path.Combine(root, "retained-" + Guid.NewGuid().ToString("N") + ".dll");
        Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(retained), StringComparison.OrdinalIgnoreCase);
        File.Move(path, retained);
        Assert.True(CreateHardLink(path, retained, IntPtr.Zero), "Unable to create owned fixture hardlink: " + Marshal.GetLastWin32Error());
        var before = File.ReadAllBytes(retained);
        if (stage) Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, Host, default));
        else Assert.Null(NodePackage.Launch(first.Image, Path.GetDirectoryName(updates)!, null, Host));
        Assert.Equal(before, File.ReadAllBytes(retained));
    }

    [Theory]
    [InlineData("updates")] [InlineData("versions")] [InlineData("ancestor")]
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
    [InlineData("http://external.invalid/")] [InlineData("https://user:secret@enrolled.invalid/")]
    [InlineData("file:///C:/node/")]
    public async Task Invalid_enrolled_origin_is_refused_without_a_request(string hub)
    {
        var (release, bytes) = Archive(Package());
        using var handler = new ReplyHandler(_ => Response(bytes));
        using var http = new HttpClient(handler);
        Assert.Null(await NodePackage.StageAsync(release, new Uri(hub), http, "machine-test", Updates(), Host, default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("rid")] [InlineData("runtime-major")] [InlineData("runtime-minor")] [InlineData("runtime-patch")]
    [InlineData("missing-dotnet")] [InlineData("relative-dotnet")]
    public async Task Incompatible_host_never_stages(string defect)
    {
        var (release, bytes) = Archive(Package());
        var host = defect switch
        {
            "rid" => Host with { WindowsRid = "linux-x64" },
            "runtime-major" => Host with { InstalledFrameworks = [new("Microsoft.NETCore.App", "11.0.0")] },
            "runtime-minor" => Host with { InstalledFrameworks = [new("Microsoft.NETCore.App", "10.1.0")] },
            "runtime-patch" => Host with { InstalledFrameworks = [new("Microsoft.NETCore.App", "10.0.0")] },
            "missing-dotnet" => Host with { DotnetPath = null },
            _ => Host with { DotnetPath = "dotnet.exe" }
        };
        if (defect == "runtime-patch") release = release with { Package = release.Package with { Frameworks = [new("Microsoft.NETCore.App", "10.0.1")] } };
        using var http = Client(bytes);
        Assert.Null(await NodePackage.StageAsync(release, Hub, http, "machine-test", Updates(), host, default));
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

    [Fact]
    public void No_known_dotnet_is_a_valid_self_contained_host_but_cannot_launch_framework_dependent()
    {
        var host = NodePackage.ReadHostCapabilities(null);
        Assert.Null(host.DotnetPath);
        Assert.Empty(host.InstalledFrameworks);
        Assert.Contains(host.WindowsRid, new[] { "win-x64", "win-arm64" });
        var updates = Updates();
        var image = InstallImage(Package(), updates);
        Assert.Null(NodePackage.Launch(image, Path.GetDirectoryName(updates)!, null, host));
    }

    [Fact]
    public void Runtime_inventory_uses_only_the_explicit_existing_dotnet_host()
    {
        var host = NodePackage.ReadHostCapabilities(Host.DotnetPath);
        Assert.Equal(Host.DotnetPath, host.DotnetPath);
        Assert.Contains(host.InstalledFrameworks, framework => framework.Name == "Microsoft.NETCore.App" && Version.Parse(framework.Version).Major == 10);
        Assert.Contains(host.WindowsRid, new[] { "win-x64", "win-arm64" });
        var package = Package();
        var candidate = Path.Combine(package, "CoCern.Adapter.exe");
        File.Copy(Path.Combine(package, "CoCern.Adapter.dll"), candidate);
        Assert.Null(NodePackage.ReadHostCapabilities(candidate).DotnetPath);
        Assert.Null(NodePackage.ReadHostCapabilities("dotnet.exe").DotnetPath);
    }

    [Fact]
    public async Task Actual_self_contained_package_is_verified_and_launch_spec_needs_no_dotnet()
    {
        var fixture = Environment.GetEnvironmentVariable("COCERN_NODE_RELEASE_FIXTURE");
        output.WriteLine("ActualSelfContainedFixtureProvided={0}", !string.IsNullOrEmpty(fixture));
        if (string.IsNullOrEmpty(fixture)) return;
        Assert.True(Path.IsPathFullyQualified(fixture));
        Assert.Equal("FC11DF747593B260DED14CF054F4371423AE4760DE7376438C91815A26A4EA94", DllHash(fixture));
        var (release, bytes) = Archive(fixture);
        Assert.True(release.Package.SelfContained);
        var host = new NodeHostCapabilities(null, [], "win-x64");
        using var http = Client(bytes);
        var updates = Updates();
        var staged = await NodePackage.StageAsync(release, Hub, http, "machine-test", updates, host, default);
        Assert.NotNull(staged);
        var launch = NodePackage.Launch(staged.Image, Path.GetDirectoryName(updates)!, null, host);
        Assert.NotNull(launch);
        Assert.Equal(Path.Combine(staged.Directory, "CoCern.Adapter.exe"), launch.FileName);
        Assert.Equal(["--node-worker", "--data-dir", Path.GetDirectoryName(updates)!], launch.Arguments);
        output.WriteLine("ActualSelfContainedStaged=True;LaunchSpecOnly=True;ContentId={0}", staged.Image.ContentId);
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

    private string SelfContainedPackage()
    {
        // These PE-only fixtures are never executed. The separately provided actual publish is tested above.
        var folder = Package();
        var native = Package(platform: Platform.X64);
        var runtime = Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json");
        File.WriteAllText(runtime, File.ReadAllText(runtime).Replace("frameworks", "includedFrameworks", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.deps.json"), "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0/win-x64\"}}");
        foreach (var name in new[] { "CoCern.Adapter.exe", "coreclr.dll", "hostfxr.dll" }) File.Copy(Path.Combine(native, "CoCern.Adapter.dll"), Path.Combine(folder, name));
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

    private static void PatchFirstCentralLength(byte[] bytes, uint length) => BitConverter.GetBytes(length).CopyTo(bytes, CentralHeaders(bytes).First() + 24);

    private static byte[] InsertCentralExtra(byte[] original, ushort id)
    {
        var central = CentralHeaders(original).First();
        var offset = central + 46 + BitConverter.ToUInt16(original, central + 28);
        var bytes = new byte[original.Length + 4];
        original.AsSpan(0, offset).CopyTo(bytes);
        BitConverter.GetBytes(id).CopyTo(bytes, offset);
        original.AsSpan(offset).CopyTo(bytes.AsSpan(offset + 4));
        BitConverter.GetBytes((ushort)(BitConverter.ToUInt16(original, central + 30) + 4)).CopyTo(bytes, central + 30);
        var end = bytes.Length - 22;
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, end + 12) + 4).CopyTo(bytes, end + 12);
        return bytes;
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

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { inner.Dispose(); base.Dispose(disposing); }
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

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

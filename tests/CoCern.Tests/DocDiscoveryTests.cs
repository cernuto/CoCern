using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Doc discovery: repositories found under configured roots, and repositories learned from the workspace a session reports. These start
/// many git processes and hubs, so they share the doc cache tests' non-parallel collection.
/// </summary>
[Collection(nameof(DocCacheTests))]
public sealed class DocDiscoveryTests
{
    private const string AdminToken = "doc-discovery-admin-test-secret-not-production";
    private const string WorkerToken = "doc-discovery-worker-test-secret-not-production";

    [Fact]
    public async Task A_workspace_outside_every_root_or_not_a_git_top_level_is_refused_and_the_session_still_opens()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        var inside = Repo(Path.Combine(root, "inside"));
        Directory.CreateDirectory(Path.Combine(inside, "docs", "deep"));
        var outside = Repo(temp.Folder("elsewhere", "outside"));
        await using var host = LearnHost(root);
        using var admin = Admin(host);
        var registry = host.Services.GetRequiredService<DocRepositoryRegistry>();
        var sync = host.Services.GetRequiredService<IDocSync>();

        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(await Tool(admin, "session_open", new { repositoryId = "repo", requestId = "outside-1", workspace = TopLevel(outside) })));
        foreach (var refused in new[] { TopLevel(outside), outside, Path.Combine(inside, "docs", "deep"), Path.Combine(root, "..", "elsewhere", "outside"), Path.Combine(root, "missing"), root, "relative/path", "" })
            Assert.Null(await sync.LearnAsync("repo", refused));
        Assert.Null(registry.Find("repo"));
        Assert.Null(registry.Find("outside"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Tool(admin, "doc_search", new { repositoryId = "repo", query = "readme" })).Status);
    }

    [Fact]
    public async Task A_second_repository_for_an_id_that_has_docs_is_learned_under_its_folder_name_and_stays_scoped()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        var alpha = Repo(Path.Combine(root, "alpha-docs"), files: new() { ["docs/a.md"] = "# Zebra\n\nThe zebra guide.\n" });
        var beta = Repo(Path.Combine(root, "beta-docs"), files: new() { ["docs/b.md"] = "# Quokka\n\nThe quokka guide.\n" });
        var badName = Repo(Path.Combine(root, "Bad Name"));
        await using var host = LearnHost(root);
        using var admin = Admin(host);
        var registry = host.Services.GetRequiredService<DocRepositoryRegistry>();
        var sync = host.Services.GetRequiredService<IDocSync>();

        Assert.Equal("repo", await sync.LearnAsync("repo", TopLevel(alpha)));
        Assert.Equal("beta-docs", await sync.LearnAsync("repo", TopLevel(beta)));
        // A repository is already registered under "repo" (whether or not it has docs), and "bad name" is not an id.
        Assert.Null(await sync.LearnAsync("repo", TopLevel(badName)));
        AssertSamePath(alpha, registry.Find("repo")!.ClonePath);
        AssertSamePath(beta, registry.Find("beta-docs")!.ClonePath);

        JsonElement[] hits = [];
        await WaitUntilAsync(async () =>
        {
            var reply = await Tool(admin, "doc_search", new { repositoryId = "repo", query = "zebra" });
            return reply.Status == HttpStatusCode.OK && (hits = Hits(reply)).Length > 0;
        });
        Assert.Equal("docs/a.md", hits[0].GetProperty("path").GetString());
        Assert.Empty(Hits(await Tool(admin, "doc_search", new { repositoryId = "repo", query = "quokka" })));
        // Learning adds docs, never access: an administrator reads every repository, a worker scoped to repo may not read beta-docs.
        Assert.Equal(HttpStatusCode.OK, (await Tool(admin, "doc_search", new { repositoryId = "beta-docs", query = "quokka" })).Status);
        using var worker = host.AuthorizedClient();
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkerToken);
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_search", new { repositoryId = "beta-docs", query = "quokka" })));
    }

    [Fact]
    public async Task A_workspace_already_registered_under_another_repository_keeps_that_id_and_never_binds_the_sessions()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        var scanned = Repo(Path.Combine(root, "alpha-docs"), files: new() { ["docs/a.md"] = "# Zebra\n\nThe zebra guide.\n" });
        await using var host = LearnHost(root);
        using var admin = Admin(host);
        var registry = host.Services.GetRequiredService<DocRepositoryRegistry>();
        var sync = host.Services.GetRequiredService<IDocSync>();

        // Found by a scan under its folder name, then reported as the workspace of a session of repo2, which has no repository yet.
        await registry.ScanAsync(CancellationToken.None);
        AssertSamePath(scanned, registry.Find("alpha-docs")!.ClonePath);
        Assert.Equal("alpha-docs", await sync.LearnAsync("repo2", TopLevel(scanned)));
        Assert.Null(registry.Find("repo2"));

        // Learned for a session of repo, then reported by a session of repo2: still only repo's.
        var learned = Repo(Path.Combine(root, "beta-docs"), files: new() { ["docs/b.md"] = "# Quokka\n\nThe quokka guide.\n" });
        Assert.Equal("repo", await sync.LearnAsync("repo", TopLevel(learned)));
        Assert.Equal("repo", await sync.LearnAsync("repo2", TopLevel(learned)));
        Assert.Null(registry.Find("repo2"));
        // Nor under its folder name, though that ID is free, for a session whose own ID is taken: one path, one ID.
        Assert.Equal("repo", await sync.LearnAsync("alpha-docs", TopLevel(learned)));
        Assert.Null(registry.Find("beta-docs"));
        Assert.Equal(2, registry.Repositories.Count);

        // repo2 gained no docs: the admin, who may read repo2, finds nothing configured there.
        Assert.Equal(HttpStatusCode.BadRequest, (await Tool(admin, "doc_search", new { repositoryId = "repo2", query = "zebra" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Tool(admin, "doc_search", new { repositoryId = "repo2", query = "quokka" })).Status);
    }

    // ---- Helpers ----

    private static DocRepositoryRegistry Registry(string root, int? depth = null, bool configured = false)
    {
        var extra = new Dictionary<string, string?> { ["Relay:DocCache:Roots:0"] = root };
        if (depth is { } value) extra["Relay:DocCache:RootDepth"] = value.ToString(CultureInfo.InvariantCulture);
        var settings = DocCacheSettings.From(Configuration(extra, configured), new FakeEnvironment());
        return new DocRepositoryRegistry(settings, new GitCli(), NullLogger<DocRepositoryRegistry>.Instance);
    }

    /// <summary>A hub with the doc cache on, one root, no configured repositories and no timer; the admin client may read repo and repo2.</summary>
    private static RelayHostFixture LearnHost(string root) => new(settings: new Dictionary<string, string?>
    {
        ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-admin", ["Relay:Clients:1:IsAdministrator"] = "true",
        ["Relay:Clients:1:Repositories:0"] = "repo", ["Relay:Clients:1:Repositories:1"] = "repo2",
        ["Relay:Clients:2:Token"] = WorkerToken, ["Relay:Clients:2:PrincipalId"] = "doc-worker", ["Relay:Clients:2:Repositories:0"] = "repo",
        ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0", ["Relay:DocCache:Roots:0"] = root
    });

    private static HttpClient Admin(RelayHostFixture host)
    {
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Tool(HttpClient client, string tool, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, arguments);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone());
    }

    private static (HttpStatusCode, string) Status((HttpStatusCode Status, JsonElement Body) reply) => (reply.Status, reply.Body.GetProperty("code").GetString()!);

    private static JsonElement[] Hits((HttpStatusCode Status, JsonElement Body) reply)
    {
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(reply));
        using var document = JsonDocument.Parse(reply.Body.GetProperty("dataJson").GetString()!);
        return document.RootElement.GetProperty("hits").EnumerateArray().Select(hit => hit.Clone()).ToArray();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(50);
        }
    }

    private static void AssertSamePath(string expected, string actual) =>
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)), ignoreCase: OperatingSystem.IsWindows());

    private static Microsoft.Extensions.Configuration.IConfiguration Configuration(Dictionary<string, string?> extra, bool configured = true)
    {
        var values = new Dictionary<string, string?> { ["Relay:DocCache:Enabled"] = "true" };
        if (configured) { values["Relay:DocCache:Repositories:0:Id"] = "repo"; values["Relay:DocCache:Repositories:0:ClonePath"] = "clone"; }
        foreach (var (key, value) in extra) values[key] = value;
        return new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>A git repository at <paramref name="path"/> on <paramref name="branch"/> with one commit of <paramref name="files"/>.</summary>
    private static string Repo(string path, string branch = "main", Dictionary<string, string>? files = null)
    {
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", branch);
        foreach (var (file, text) in files ?? new() { ["README.md"] = "# Readme\n\nSome text.\n" })
        {
            var full = Path.Combine(path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Git(path, "add", "-A");
        Git(path, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "first");
        return path;
    }

    private static string TopLevel(string path) => Git(path, "rev-parse", "--show-toplevel").Trim();

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        return output.Result;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var folder in Directory.EnumerateDirectories(from)) CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await capture(request);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"code\":\"ok\",\"dataJson\":\"{}\",\"version\":1}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>A temporary folder, removed with everything in it (git's read-only object files included).</summary>
    private sealed class TempFolder : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-discovery-" + Guid.NewGuid().ToString("N"));

        public string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([root, .. parts])).FullName;

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

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

    // ---- Settings ----

    [Fact]
    public void Roots_resolve_against_the_content_root_and_root_depth_defaults_to_2_and_must_be_from_1_to_4()
    {
        var environment = new FakeEnvironment();
        var absolute = Path.Combine(Path.GetTempPath(), "cocern-absolute-root");
        var settings = DocCacheSettings.From(Configuration(new() { ["Relay:DocCache:Roots:0"] = "roots/a", ["Relay:DocCache:Roots:1"] = absolute }), environment);
        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(environment.ContentRootPath, "roots/a")), Path.GetFullPath(absolute) }, settings.Roots);
        Assert.Equal(2, settings.RootDepth);
        Assert.Empty(DocCacheSettings.From(Configuration([]), environment).Roots);
        foreach (var depth in new[] { 1, 4 })
            Assert.Equal(depth, DocCacheSettings.From(Configuration(new() { ["Relay:DocCache:RootDepth"] = depth.ToString(CultureInfo.InvariantCulture) }), environment).RootDepth);
        foreach (var bad in new[] { "0", "5", "-1" })
            Assert.Contains("RootDepth", Assert.Throws<InvalidOperationException>(() => DocCacheSettings.From(Configuration(new() { ["Relay:DocCache:RootDepth"] = bad }), environment)).Message);
    }

    // ---- Root scan ----

    [Fact]
    public async Task The_scan_finds_repositories_within_the_depth_through_plain_folders_and_never_inside_a_found_repository()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        var one = Repo(Path.Combine(root, "one"), branch: "trunk");
        var two = Repo(Path.Combine(root, "plain", "two"));
        Repo(Path.Combine(root, "plain", "deeper", "three"));
        var outer = Repo(Path.Combine(root, "outer"));
        Repo(Path.Combine(outer, "inner"));
        // A worktree's .git is a file, not a folder.
        Git(outer, "worktree", "add", "-q", "-b", "feature", Path.Combine(root, "wt"));
        var upper = Repo(Path.Combine(root, "Upper.Docs"));
        Directory.CreateDirectory(Path.Combine(root, "empty-folder"));

        var registry = Registry(root);
        await registry.ScanAsync(CancellationToken.None);
        Assert.Equal(new[] { "one", "outer", "two", "upper.docs", "wt" }, registry.Repositories.Select(repository => repository.Id).Order(StringComparer.Ordinal));
        var found = registry.Find("one")!;
        AssertSamePath(one, found.ClonePath);
        Assert.Equal(("trunk", false), (found.Branch, found.Fetch));
        Assert.Equal(DocFormats.DefaultPatterns, found.Patterns.Select(pattern => pattern.Glob));
        Assert.Equal(DocFormats.DefaultExcludes, found.Excludes);
        AssertSamePath(two, registry.Find("two")!.ClonePath);
        AssertSamePath(upper, registry.Find("upper.docs")!.ClonePath);
        Assert.Equal("feature", registry.Find("wt")!.Branch);
        Assert.Null(registry.Find("inner"));
        Assert.Null(registry.Find("three"));

        var deeper = Registry(root, depth: 3);
        await deeper.ScanAsync(CancellationToken.None);
        Assert.NotNull(deeper.Find("three"));
        Assert.Null(deeper.Find("inner"));
        var shallow = Registry(root, depth: 1);
        await shallow.ScanAsync(CancellationToken.None);
        Assert.NotNull(shallow.Find("one"));
        Assert.Null(shallow.Find("two"));
    }

    [Fact]
    public async Task Names_that_are_not_ids_are_skipped_a_configured_id_wins_and_a_duplicate_name_keeps_the_first_path()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        Repo(Path.Combine(root, "has space"));
        Repo(Path.Combine(root, "bad+name"));
        Repo(Path.Combine(root, new string('a', 65)));
        Repo(Path.Combine(root, "repo"));
        var first = Repo(Path.Combine(root, "a", "dup"));
        Repo(Path.Combine(root, "b", "dup"));

        var registry = Registry(root, configured: true);
        await registry.ScanAsync(CancellationToken.None);
        Assert.Equal(new[] { "dup", "repo" }, registry.Repositories.Select(repository => repository.Id).Order(StringComparer.Ordinal));
        var configured = registry.Find("repo")!;
        AssertSamePath(Path.Combine(Path.GetTempPath(), "clone"), configured.ClonePath);
        Assert.True(configured.Fetch);
        AssertSamePath(first, registry.Find("dup")!.ClonePath);
    }

    [Fact]
    public async Task A_repository_with_a_detached_head_is_skipped()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        Git(Repo(Path.Combine(root, "detached")), "checkout", "-q", "--detach");
        Repo(Path.Combine(root, "attached"));
        var registry = Registry(root);
        await registry.ScanAsync(CancellationToken.None);
        Assert.Null(registry.Find("detached"));
        Assert.Equal("main", registry.Find("attached")!.Branch);
    }

    [Fact]
    public async Task Discovered_repositories_stop_at_the_cap_even_when_scans_run_at_once()
    {
        Assert.Equal(64, RelayLimits.MaxDiscoveredDocRepositories);
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        // One real repository, copied: much faster than one git init and commit per folder.
        var template = Repo(temp.Folder("template", "t"));
        for (var i = 0; i < RelayLimits.MaxDiscoveredDocRepositories + 2; i++) CopyDirectory(Path.Combine(template, ".git"), Path.Combine(root, $"r{i:D2}", ".git"));
        var registry = Registry(root);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => registry.ScanAsync(CancellationToken.None)));
        var ids = registry.Repositories.Select(repository => repository.Id).ToArray();
        Assert.Equal(RelayLimits.MaxDiscoveredDocRepositories, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        await registry.ScanAsync(CancellationToken.None);
        Assert.Equal(RelayLimits.MaxDiscoveredDocRepositories, registry.Repositories.Count);
    }

    [Fact]
    public async Task Each_sync_run_discovers_new_repositories_and_the_doc_tools_see_them_without_a_restart()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        await using var host = LearnHost(root);
        using var admin = Admin(host);
        Assert.Equal(HttpStatusCode.BadRequest, (await Tool(admin, "doc_search", new { repositoryId = "repo", query = "zebra" })).Status);
        // Made after the hub started: the next timer run finds it and syncs it.
        Repo(Path.Combine(root, "repo"), files: new() { ["docs/guide.md"] = "# Zebra crossings\n\nHow the zebra service works.\n" });
        await host.Services.GetServices<IHostedService>().OfType<SpecSyncService>().Single().RunOnceAsync(CancellationToken.None);
        Assert.False(host.Services.GetRequiredService<DocRepositoryRegistry>().Find("repo")!.Fetch);
        Assert.Equal("docs/guide.md", Assert.Single(Hits(await Tool(admin, "doc_search", new { repositoryId = "repo", query = "zebra" }))).GetProperty("path").GetString());
    }

    // ---- Learning from sessions ----

    [Fact]
    public async Task A_session_workspace_inside_a_root_is_learned_under_the_sessions_repository_and_searched_end_to_end()
    {
        using var temp = new TempFolder();
        var root = temp.Folder("roots");
        var workspace = Repo(Path.Combine(root, "service-a"), files: new() { ["docs/guide.md"] = "# Zebra crossings\n\nHow the zebra service works.\n" });
        File.WriteAllText(Path.Combine(workspace, "docs", "draft.md"), "# Draft\n\nAn uncommitted quokka note.\n");
        await using var host = LearnHost(root);
        using var admin = Admin(host);
        var registry = host.Services.GetRequiredService<DocRepositoryRegistry>();
        Assert.Null(registry.Find("repo"));

        // The workspace as the adapter sends it: git's own top-level path.
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(await Tool(admin, "session_open", new { repositoryId = "repo", requestId = "learn-1", workspace = TopLevel(workspace) })));
        await WaitUntilAsync(() => Task.FromResult(registry.Find("repo") is not null));
        var learned = registry.Find("repo")!;
        AssertSamePath(workspace, learned.ClonePath);
        Assert.Equal(("main", false), (learned.Branch, learned.Fetch));
        Assert.Equal(DocFormats.DefaultPatterns, learned.Patterns.Select(pattern => pattern.Glob));

        JsonElement[] hits = [];
        await WaitUntilAsync(async () =>
        {
            var reply = await Tool(admin, "doc_search", new { repositoryId = "repo", query = "zebra" });
            return reply.Status == HttpStatusCode.OK && (hits = Hits(reply)).Length > 0;
        });
        Assert.Equal("docs/guide.md", hits[0].GetProperty("path").GetString());
        // Only committed content is read.
        Assert.Empty(Hits(await Tool(admin, "doc_search", new { repositoryId = "repo", query = "quokka" })));
    }

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

    // ---- The adapter reports its workspace ----

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "D:/elsewhere/checkout")]
    [InlineData(true, "")]
    [InlineData(true, null)]
    public async Task Session_open_gains_the_adapters_git_workspace_only_when_the_caller_gave_none(bool explicitWorkspace, string? supplied)
    {
        const string workspace = "C:/work/cocern";
        JsonElement sent = default;
        using var handler = new CaptureHandler(async request => sent = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
        using var client = new HttpClient(handler);
        var arguments = new JsonObject { ["repositoryId"] = "repo", ["requestId"] = "open-1" };
        if (explicitWorkspace) arguments["workspace"] = supplied;
        var prepared = CoordinationTools.OwnRequestId(JsonSerializer.SerializeToElement(arguments), workspace);
        Assert.True((await new RelayForwarder(client, new AdapterOptions("https://remote-hub.example", "client-secret")).ForwardAsync("session_open", prepared)).Ok);
        Assert.True(sent.TryGetProperty("workspace", out var value));
        Assert.Equal(explicitWorkspace ? supplied : workspace, value.ValueKind == JsonValueKind.Null ? null : value.GetString());
        Assert.Equal(explicitWorkspace, arguments.ContainsKey("workspace"));
        // The machine default still applies beside it.
        Assert.True(sent.TryGetProperty("machineId", out _));
    }

    [Fact]
    public void Outside_a_git_repository_the_adapter_adds_no_workspace_and_never_advertises_one()
    {
        var arguments = JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-2" });
        Assert.False(CoordinationTools.OwnRequestId(arguments, null).TryGetProperty("workspace", out _));
        using var client = new HttpClient();
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        foreach (var admin in new[] { false, true })
        {
            var open = CoordinationTools.Create(forwarder, admin).Single(tool => tool.ProtocolTool.Name == RelayTools.SessionOpen);
            Assert.False(open.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("workspace", out _));
        }
        Assert.True(RelayToolCatalog.TryGet(RelayTools.SessionOpen, out var catalog));
        Assert.False(RelayToolCatalog.ForwardingSchema(catalog).GetProperty("properties").TryGetProperty("workspace", out _));
    }

    [Fact]
    public void The_adapter_workspace_is_the_git_top_level_of_its_folder()
    {
        using var temp = new TempFolder();
        var repository = Repo(temp.Folder("checkout"));
        var nested = Directory.CreateDirectory(Path.Combine(repository, "src", "deep")).FullName;
        AssertSamePath(repository, CoordinationTools.GitTopLevel(nested)!);
        AssertSamePath(repository, CoordinationTools.GitTopLevel(repository)!);
        Assert.Null(CoordinationTools.GitTopLevel(temp.Folder("not-git")));
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

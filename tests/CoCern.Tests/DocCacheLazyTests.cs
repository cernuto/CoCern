using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// DOCCACHE-LAZY-1: doc_search answers from one shared index per repository, so a search loads no document grain; only doc_section
/// activates the one document it reads, and that document still idles out and reloads. The tests through the directory and the tools stay
/// red until the directory searches through <see cref="IDocSearchGrain"/>; the search grain's own test passes on its own.
/// </summary>
[Collection(nameof(DocCacheTests))]
public sealed class DocCacheLazyTests
{
    private const string AdminToken = "doc-cache-lazy-admin-test-secret-not-production";
    private static readonly ITokenCounter Tokens = new O200kTokenCounter();

    private static Dictionary<string, string> Ledgers(int count) =>
        Enumerable.Range(0, count).ToDictionary(i => $"docs/d{i:00}.md", i => $"# Doc {i}\n\nLedger entries for doc {i}{(i == 7 ? " with ledger ledger ledger" : "")}.\n");

    private static readonly Dictionary<string, string> Ranking = new()
    {
        ["docs/camel.md"] = "# Fields\n\nThe taskClaim flag is set.\n",
        ["docs/snake.md"] = "# Columns\n\nThe task_claim column holds it.\n",
        ["docs/url.md"] = "# Routes\n\nCall /v1/tasks/{id}/claim to hold a task.\n",
        ["docs/rank.md"] = "# Guide\n\n## Other\n\nThis mentions claim in passing among many other words about something else.\n\n## Claim\n\nTake a task.\n"
    };

    [Fact]
    public async Task A_doc_search_over_many_documents_activates_no_document_grain()
    {
        using var git = GitRepositories.Create(Ledgers(12));
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var hits = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "ledger", limit = 5 }));
        Assert.Equal(5, hits.Length);
        Assert.Equal("docs/d07.md", hits[0].GetProperty("path").GetString());
        var outcome = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").SearchAsync("ledger", null, null, 5, TimeSpan.FromSeconds(30));
        Assert.Equal((12, 0, 5), (outcome.Documents, outcome.NotSearched, outcome.Hits.Length));
        Assert.Empty(await ActiveDocumentsAsync(host));
        Assert.Equal(0, host.Services.GetRequiredService<DocCacheActivity>().Snapshot(DateTimeOffset.UtcNow).ActiveDocuments);
    }

    [Fact]
    public async Task A_doc_section_activates_exactly_the_one_document_it_reads()
    {
        var files = Ledgers(8);
        using var git = GitRepositories.Create(files);
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var top = Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "ledger" }))[0];
        Assert.Empty(await ActiveDocumentsAsync(host));
        var path = top.GetProperty("path").GetString()!;
        var section = Data(await Tool(client, "doc_section", new { repositoryId = "repo", path, anchor = top.GetProperty("anchor").GetString() }));
        Assert.Equal(files[path], section.GetProperty("text").GetString());
        Assert.Equal(new[] { DocKeys.Document("repo", path) }, await ActiveDocumentsAsync(host));
        // Further searches still load nothing more.
        Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "entries" }));
        Assert.Equal(new[] { DocKeys.Document("repo", path) }, await ActiveDocumentsAsync(host));
    }

    [Fact]
    public async Task An_idle_document_deactivates_after_IdleMinutes_and_a_later_doc_section_reloads_it()
    {
        var files = Ledgers(4);
        using var git = GitRepositories.Create(files);
        await using var host = DocHost(git, new() { ["Relay:DocCache:IdleMinutes"] = "2" });
        using var client = host.AuthorizedClient();
        await SyncAsync(host);
        var collection = host.Services.GetRequiredService<IOptions<GrainCollectionOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(2), collection.ClassSpecificCollectionAge[typeof(DocumentGrain).FullName!]);
        const string path = "docs/d03.md";
        var anchor = DocFormats.Slug("Doc 3");
        Assert.Equal(files[path], Data(await Tool(client, "doc_section", new { repositoryId = "repo", path, anchor })).GetProperty("text").GetString());
        Assert.Equal(new[] { DocKeys.Document("repo", path) }, await ActiveDocumentsAsync(host));
        Assert.Equal(1, DocumentRow(host, path).Reads);

        var management = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IManagementGrain>(0);
        // Used moments ago, so not yet idle for IdleMinutes; once it has been (an age of zero stands in for the wait), it is collected.
        await management.ForceActivationCollection(TimeSpan.FromMinutes(2));
        Assert.Equal(new[] { DocKeys.Document("repo", path) }, await ActiveDocumentsAsync(host));
        await management.ForceActivationCollection(TimeSpan.Zero);
        await WaitUntilAsync(async () => (await ActiveDocumentsAsync(host)).Length == 0);

        // Searching never brings it back; reading the section does, transparently, with one fresh read.
        Assert.Equal(path, Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "ledger", path })).Single().GetProperty("path").GetString());
        Assert.Empty(await ActiveDocumentsAsync(host));
        Assert.Equal(files[path], Data(await Tool(client, "doc_section", new { repositoryId = "repo", path, anchor })).GetProperty("text").GetString());
        Assert.Equal(new[] { DocKeys.Document("repo", path) }, await ActiveDocumentsAsync(host));
        var row = DocumentRow(host, path);
        Assert.Equal((true, 1, git.Head()), (row.Loaded, row.Reads, row.Commit));
    }

    [Fact]
    public async Task Directory_search_hits_equal_the_shared_index_hits_and_keep_headings_first()
    {
        using var git = GitRepositories.Create(Ranking);
        await using var host = DocHost(git);
        await SyncAsync(host);
        var outcome = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").SearchAsync("claim", null, null, 10, TimeSpan.FromSeconds(30));
        Assert.Equal(Expected(Ranking, git.Head(), "claim", 10), outcome.Hits);
        Assert.Equal("Guide > Claim", outcome.Hits[0].Heading);
        Assert.Equal(new[] { "claim", "other" }, outcome.Hits.Where(hit => hit.Path == "docs/rank.md").Select(hit => hit.Anchor));
        Assert.Empty(await ActiveDocumentsAsync(host));
    }

    [Fact]
    public async Task The_search_grain_builds_lazily_from_the_directory_and_patches_by_content_hash()
    {
        using var git = GitRepositories.Create(Ranking);
        await using var host = DocHost(git);
        await SyncAsync(host);
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var search = grains.GetGrain<IDocSearchGrain>("repo");
        Assert.False((await search.StatsAsync()).Built);

        var first = await search.SearchAsync("claim", null, null, 10, TimeSpan.FromSeconds(30), "");
        Assert.Equal(Expected(Ranking, git.Head(), "claim", 10), first.Outcome.Hits);
        Assert.Equal((4, 0, false, false), (first.Outcome.Documents, first.Outcome.NotSearched, first.Outcome.PathUnknown, first.FellBack));
        Assert.Equal(Ranking.Keys.Order(StringComparer.Ordinal), first.Searched.Select(item => item.Path));
        Assert.Equal(2, first.Searched.Single(item => item.Path == "docs/rank.md").Matches);
        Assert.Equal((true, 4, 4, 4), Stats(await search.StatsAsync()));
        Assert.Empty(await ActiveDocumentsAsync(host));

        // A sync changes one file; the patch rereads only the path whose content hash changed.
        git.Commit(new() { ["docs/camel.md"] = "# Fields\n\nThe ledger flag is set.\n" });
        await SyncAsync(host);
        var directory = grains.GetGrain<IDocDirectoryGrain>("repo");
        var view = await directory.ReadAsync();
        await search.PatchAsync(view.Entries.Select(entry => new DocRef("repo", entry.Path, entry.Spec, entry.Commit, entry.Hash, view.Branch)).ToArray(), []);
        Assert.Equal((true, 4, 4, 5), Stats(await search.StatsAsync()));
        var ledger = Assert.Single((await search.SearchAsync("ledger", null, null, 10, TimeSpan.FromSeconds(30), "")).Outcome.Hits);
        Assert.Equal(("docs/camel.md", git.Head()), (ledger.Path, ledger.Commit));
        Assert.DoesNotContain((await search.SearchAsync("claim", null, null, 10, TimeSpan.FromSeconds(30), "")).Outcome.Hits, hit => hit.Path == "docs/camel.md");

        // A removed path is gone from the hits and unknown to a path search.
        await search.PatchAsync([], ["docs/snake.md"]);
        var removed = await search.SearchAsync("claim", null, null, 10, TimeSpan.FromSeconds(30), "");
        Assert.Equal(3, removed.Outcome.Documents);
        Assert.DoesNotContain(removed.Outcome.Hits, hit => hit.Path == "docs/snake.md");
        Assert.True((await search.SearchAsync("claim", null, "docs/snake.md", 10, TimeSpan.FromSeconds(30), "")).Outcome.PathUnknown);
        Assert.Empty((await search.SearchAsync("claim", "nothing", null, 10, TimeSpan.FromSeconds(30), "")).Outcome.Hits);
        Assert.Empty(await ActiveDocumentsAsync(host));
    }

    // ---- Helpers (local copies of the DocCacheTests helpers) ----

    private static (bool, int, int, int) Stats(DocSearchStats stats) => (stats.Built, stats.Documents, stats.Indexed, stats.Reads);

    /// <summary>The hits one shared index over <paramref name="files"/> gives, every file read at <paramref name="commit"/>.</summary>
    private static DocHit[] Expected(Dictionary<string, string> files, string commit, string query, int limit)
    {
        using var index = new DocSearchIndex(Tokens);
        index.Apply(files.Select(file => new DocSearchDocument(file.Key, DocFormats.DefaultSpec, commit, DocHash.Of(file.Value), file.Value)), []);
        return index.Search(query, null, null, limit).Hits;
    }

    /// <summary>This repository's document grains active in the silo now, by key.</summary>
    private static async Task<string[]> ActiveDocumentsAsync(RelayHostFixture host) =>
        (await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IManagementGrain>(0).GetActiveGrains(DocKeys.DocumentType))
            .Select(id => id.Key.ToString()!).Where(key => key.StartsWith("repo/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    private static CoCern.Contracts.Dashboard.DashboardDocGrain DocumentRow(RelayHostFixture host, string path) =>
        Assert.Single(host.Services.GetRequiredService<DocCacheActivity>().Snapshot(DateTimeOffset.UtcNow).Grains, row => row.Kind == "Document" && row.Repository == "repo" && row.Path == path);

    private static RelayHostFixture DocHost(GitRepositories git, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo",
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Clone
        };
        foreach (var (key, value) in settings ?? []) all[key] = value;
        return new RelayHostFixture(settings: all);
    }

    private static Task<DocSyncOutcome> SyncAsync(RelayHostFixture host) => host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Tool(HttpClient client, string tool, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, arguments);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone());
    }

    private static (HttpStatusCode, string) Status((HttpStatusCode Status, JsonElement Body) reply) => (reply.Status, reply.Body.GetProperty("code").GetString()!);

    private static JsonElement Data((HttpStatusCode Status, JsonElement Body) reply)
    {
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(reply));
        using var document = JsonDocument.Parse(reply.Body.GetProperty("dataJson").GetString()!);
        return document.RootElement.Clone();
    }

    private static JsonElement[] Hits((HttpStatusCode Status, JsonElement Body) reply) => Data(reply).GetProperty("hits").EnumerateArray().ToArray();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(50);
        }
    }

    /// <summary>A temporary origin (bare), a work clone that commits and pushes, and the hub's clone; git fetches from a path, with no network.</summary>
    private sealed class GitRepositories : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-doc-lazy-" + Guid.NewGuid().ToString("N"));
        private string Work => Path.Combine(root, "work");
        private string Origin => Path.Combine(root, "origin.git");
        public string Clone => Path.Combine(root, "clone");

        public static GitRepositories Create(Dictionary<string, string> files)
        {
            var repositories = new GitRepositories();
            Directory.CreateDirectory(repositories.Work);
            Run(repositories.Work, "init", "-q", "-b", "main");
            repositories.Write(files);
            Run(repositories.Work, "add", "-A");
            Run(repositories.Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "first");
            Run(repositories.root, "clone", "-q", "--bare", repositories.Work, repositories.Origin);
            Run(repositories.Work, "remote", "add", "origin", repositories.Origin);
            Run(repositories.Work, "fetch", "-q", "origin");
            Run(repositories.root, "clone", "-q", repositories.Origin, repositories.Clone);
            return repositories;
        }

        public string Head(string branch = "main") => Run(Work, "rev-parse", branch).Trim();

        public void Commit(Dictionary<string, string> files)
        {
            Write(files);
            Run(Work, "add", "-A");
            Run(Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "change");
            Run(Work, "push", "-q", "origin", "main");
        }

        private void Write(Dictionary<string, string> files)
        {
            foreach (var (path, text) in files)
            {
                var full = Path.Combine(Work, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
        }

        private static string Run(string directory, params string[] arguments)
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

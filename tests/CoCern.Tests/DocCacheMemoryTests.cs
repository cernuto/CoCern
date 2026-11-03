using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// What a loaded document costs the hub once it is indexed. Each test loads 40 realistic markdown documents through doc_search and
/// doc_section, so it shares the doc cache tests' non-parallel collection: nothing else allocates in the process while memory is measured.
/// </summary>
[Collection(nameof(DocCacheTests))]
public sealed class DocCacheMemoryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string AdminToken = "doc-cache-memory-admin-test-secret-not-production";
    private const int Documents = 40;
    private const string WarmupPath = "docs/warmup.md";
    private const string CommonWord = "ledger";
    private const double MaxRetainedKilobytesPerDocument = 600;

    [Fact]
    public async Task Loaded_documents_retain_little_memory_once_indexed()
    {
        using var git = GitRepositories.Create(Corpus());
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        await SyncAsync(host);

        // One document loaded before the baseline pays for the one-time costs (Lucene statics, the analyzer, the first request), so the
        // difference below is what each further loaded document keeps.
        await WarmUpAsync(client);
        var before = SettledManagedBytes();

        await LoadEveryDocumentAsync(client);
        AssertAllLoaded(host);
        var after = SettledManagedBytes();

        var perDocument = (after - before) / (double)Documents / 1024;
        output.WriteLine($"Retained per loaded document: {perDocument:F0} KB ({before:N0} -> {after:N0} bytes for {Documents} documents).");
        Assert.True(perDocument < MaxRetainedKilobytesPerDocument,
            $"Each loaded document retained {perDocument:F0} KB of managed memory once indexed ({before:N0} bytes before, {after:N0} after loading {Documents}); the limit is {MaxRetainedKilobytesPerDocument:F0} KB.");

        // A smaller cache must still be the same index: every document still answers its own words and the common one.
        await AssertSearchesStillAnswerAsync(client);
        AssertAllLoaded(host);
        GC.KeepAlive(host);
        GC.KeepAlive(client);
    }

    [Fact]
    public async Task Loaded_documents_keep_answering_searches_and_a_doc_sync_reindexes_the_changed_one()
    {
        using var git = GitRepositories.Create(Corpus());
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        using var admin = Admin(host);
        await SyncAsync(host);
        await WarmUpAsync(client);
        await LoadEveryDocumentAsync(client);
        AssertAllLoaded(host);
        await AssertSearchesStillAnswerAsync(client);

        // One document changes and is synced by doc_sync: its loaded cache is reindexed, the others are left as they were.
        const string revised = "zqrevisedx";
        git.Commit(new() { [PathOf(5)] = Markdown(5, revised) });
        var sync = await Tool(admin, "doc_sync", new { repositoryId = "repo", requestId = "memory-sync-1" });
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(sync));
        var synced = Data(sync);
        Assert.Equal((git.Head(), 1), (synced.GetProperty("commit").GetString(), synced.GetProperty("changed").GetInt32()));

        var hit = Assert.Single(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = revised, limit = RelayLimits.MaxDocSearchResults })));
        Assert.Equal((PathOf(5), "overview"), (hit.GetProperty("path").GetString(), hit.GetProperty("anchor").GetString()));
        Assert.Equal(git.Head()[..12], hit.GetProperty("commit").GetString());
        Assert.Empty(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = Code(5), limit = RelayLimits.MaxDocSearchResults })));
        var section = Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = PathOf(5), anchor = "overview" }));
        Assert.Contains(revised, section.GetProperty("text").GetString());

        var grains = host.Services.GetRequiredService<IGrainFactory>();
        Assert.Equal(2, (await grains.GetGrain<IDocumentGrain>(DocKeys.Document("repo", PathOf(5))).StatsAsync()).IndexRuns);
        Assert.Equal(1, (await grains.GetGrain<IDocumentGrain>(DocKeys.Document("repo", PathOf(6))).StatsAsync()).IndexRuns);
        var unchanged = Assert.Single(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = Code(6), limit = RelayLimits.MaxDocSearchResults })));
        Assert.Equal(PathOf(6), unchanged.GetProperty("path").GetString());
        // A second search after the reindex still finds the new word: the reindexed cache stays searchable, not just its first reader.
        Assert.Single(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = revised, path = PathOf(5) })));
    }

    // ---- Loading and measuring ----

    private static async Task WarmUpAsync(HttpClient client)
    {
        Assert.NotEmpty(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = CommonWord, path = WarmupPath })));
        Assert.Contains("warmup", Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = WarmupPath, anchor = "overview" })).GetProperty("text").GetString());
    }

    /// <summary>A search whose word is in every document loads them all; a section read of each then reads every loaded cache.</summary>
    private static async Task LoadEveryDocumentAsync(HttpClient client)
    {
        Assert.Equal(RelayLimits.DefaultDocSearchResults, Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = CommonWord })).Length);
        for (var i = 0; i < Documents; i++)
        {
            var section = Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = PathOf(i), anchor = "overview" }));
            Assert.Contains(Code(i), section.GetProperty("text").GetString());
        }
    }

    private static void AssertAllLoaded(RelayHostFixture host)
    {
        var snapshot = host.Services.GetRequiredService<DocCacheActivity>().Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(Documents + 1, snapshot.Grains.Count(row => row.Kind == "Document" && row.Loaded));
    }

    private static async Task AssertSearchesStillAnswerAsync(HttpClient client)
    {
        foreach (var i in new[] { 0, 6, 13, 27, Documents - 1 })
        {
            var hit = Assert.Single(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = Code(i), limit = RelayLimits.MaxDocSearchResults })));
            Assert.Equal((PathOf(i), "overview"), (hit.GetProperty("path").GetString(), hit.GetProperty("anchor").GetString()));
        }
        var common = Data(await Tool(client, "doc_search", new { repositoryId = "repo", query = CommonWord, limit = RelayLimits.MaxDocSearchResults }));
        // Hits that do not fit the reply's token budget are counted in "more"; together they are the limit, since all 40 documents match.
        var more = common.TryGetProperty("more", out var count) ? count.GetInt32() : 0;
        Assert.True(common.GetProperty("hits").GetArrayLength() > 0);
        Assert.Equal(RelayLimits.MaxDocSearchResults, common.GetProperty("hits").GetArrayLength() + more);
        Assert.False(common.TryGetProperty("partial", out _));
    }

    /// <summary>Managed bytes still reachable after full, blocking, compacting collections (the large object heap included).</summary>
    private static long SettledManagedBytes()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    // ---- The corpus: 40 markdown documents of 6 to 12 KB, plus one warm-up document ----

    private static readonly string[] Vocabulary =
    [
        "account", "adjust", "after", "agent", "allocate", "amount", "approve", "archive", "audit", "balance", "batch", "before", "billing",
        "branch", "budget", "buffer", "calendar", "cancel", "capture", "carrier", "catalog", "channel", "charge", "checkout", "client", "close",
        "column", "commit", "compare", "confirm", "contract", "credit", "currency", "customer", "cycle", "daily", "debit", "default", "deliver",
        "deposit", "detail", "discount", "dispatch", "document", "draft", "due", "entry", "error", "estimate", "event", "expense", "export",
        "field", "filter", "finance", "forecast", "freight", "fulfil", "general", "grant", "group", "history", "hold", "import", "invoice",
        "issue", "item", "journal", "label", "limit", "line", "local", "lookup", "manual", "margin", "match", "merchant", "method", "monthly",
        "network", "notice", "number", "offset", "order", "owner", "package", "partner", "payment", "payout", "pending", "period", "policy",
        "posting", "price", "process", "product", "profile", "quarter", "queue", "quote", "rate", "receipt", "record", "refund", "region",
        "release", "remit", "report", "request", "reserve", "retry", "return", "review", "revenue", "route", "schedule", "segment", "settle",
        "shipment", "signal", "source", "statement", "status", "stock", "store", "summary", "supplier", "support", "tax", "tenant", "term",
        "threshold", "ticket", "total", "track", "transfer", "update", "vendor", "verify", "volume", "warehouse", "weekly", "window", "yearly"
    ];

    private static Dictionary<string, string> Corpus()
    {
        var files = Enumerable.Range(0, Documents).ToDictionary(PathOf, i => Markdown(i, Code(i)));
        files[WarmupPath] = "# Overview\n\nThe warmup " + CommonWord + " note is loaded before memory is measured.\n\n## Detail\n\nIt has one more short section.\n";
        return files;
    }

    private static string PathOf(int i) => $"docs/d{i:00}.md";

    /// <summary>A word found in one document only: letters alone, so the analyzer keeps it as one term.</summary>
    private static string Code(int i) => "zq" + (char)('a' + i / 26) + (char)('a' + i % 26) + "x";

    /// <summary>A deterministic document of 6 to 12 KB: an overview naming its own word and the common one, then headed sections of varied prose.</summary>
    private static string Markdown(int i, string code)
    {
        var random = new Random(1_000 + i);
        var target = 6_000 + i * 2_731 % 6_000;
        var text = new StringBuilder();
        text.Append("# Overview\n\n").Append($"This {CommonWord} guide describes the {code} workflow. ").Append(Paragraph(random)).Append("\n\n");
        for (var heading = 0; Encoding.UTF8.GetByteCount(text.ToString()) < target; heading++)
        {
            text.Append(heading % 3 == 2 ? "### " : "## ").Append(Title(random)).Append("\n\n");
            var paragraphs = random.Next(1, 4);
            for (var p = 0; p < paragraphs; p++) text.Append(Paragraph(random)).Append("\n\n");
            if (heading % 4 == 1) text.Append("- ").Append(Word(random)).Append(' ').Append(Word(random)).Append("\n- ").Append(Word(random)).Append(' ').Append(Word(random)).Append("\n\n");
        }
        return text.ToString();
    }

    private static string Title(Random random)
    {
        var first = Word(random);
        return char.ToUpperInvariant(first[0]) + first[1..] + " " + Word(random) + " " + Word(random);
    }

    private static string Paragraph(Random random)
    {
        var text = new StringBuilder();
        var sentences = random.Next(3, 7);
        for (var s = 0; s < sentences; s++)
        {
            var words = Enumerable.Range(0, random.Next(8, 18)).Select(_ => Word(random)).ToArray();
            words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
            text.Append(string.Join(' ', words)).Append(s % 3 == 1 ? "; it is checked again later. " : ". ");
        }
        return text.ToString().TrimEnd();
    }

    private static string Word(Random random) => Vocabulary[random.Next(Vocabulary.Length)];

    // ---- Helpers, copied from DocCacheTests ----

    private static RelayHostFixture DocHost(GitRepositories git) => new(null, settings: new Dictionary<string, string?>
    {
        ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-memory-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo",
        ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
        ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Clone
    });

    private static Task<DocSyncOutcome> SyncAsync(RelayHostFixture host)
    {
        _ = host.Services;
        return host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None);
    }

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

    private static JsonElement Data((HttpStatusCode Status, JsonElement Body) reply)
    {
        using var document = JsonDocument.Parse(reply.Body.GetProperty("dataJson").GetString()!);
        return document.RootElement.Clone();
    }

    private static JsonElement[] Hits((HttpStatusCode Status, JsonElement Body) reply)
    {
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(reply));
        return Data(reply).GetProperty("hits").EnumerateArray().ToArray();
    }

    /// <summary>A temporary origin (bare), a work clone that commits and pushes, and the hub's clone. Everything is local: git fetches from a path.</summary>
    private sealed class GitRepositories : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-doc-memory-" + Guid.NewGuid().ToString("N"));
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

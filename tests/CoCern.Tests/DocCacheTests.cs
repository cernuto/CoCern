using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Orleans;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

/// <summary>These tests start many git processes and hubs; they run on their own so other classes' timing checks keep their margin.</summary>
[CollectionDefinition(nameof(DocCacheTests), DisableParallelization = true)]
public sealed class DocCacheCollection;

[Collection(nameof(DocCacheTests))]
public sealed class DocCacheTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Focused_document_reads_preserve_exact_code_and_pin_changed_budget_continuations_to_the_source_commit()
    {
        var source = "# Exact\n\n" + string.Concat(Enumerable.Range(0, 64).Select(index => $"Rule {index}: retain exact identifiers, versions and source evidence before proceeding.\n"))
            + "\n```csharp\npublic static string Read(string[] values)\n{\n\tvar needlevalue = values[0] + \"🧭\";\n\treturn needlevalue;\n}\n```\n";
        var unrelated = "# Unrelated\n\n" + new string('x', 4_000) + "\n";
        using var git = GitRepositories.Create(new() { ["docs/exact.md"] = source + unrelated });
        using var other = GitRepositories.Create(new() { ["docs/exact.md"] = source.Replace("needlevalue", "foreignvalue", StringComparison.Ordinal) + unrelated });
        await using var host = DocHost(git, settings: new()
        {
            ["Relay:DocCache:Repositories:1:Id"] = "repo2", ["Relay:DocCache:Repositories:1:ClonePath"] = other.Clone
        });
        using var worker = host.AuthorizedClient();
        await SyncAsync(host);
        await host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo2", null, CancellationToken.None);
        var commit = git.Head();
        var tools = host.Services.GetRequiredService<DocCacheTool>();
        var search = await tools.SearchAsync(new JsonObject { ["repositoryId"] = "repo", ["query"] = "needlevalue" });
        Assert.True(search.Ok, search.Code);
        using var searchJson = JsonDocument.Parse(search.DataJson);
        var hit = Assert.Single(searchJson.RootElement.GetProperty("hits").EnumerateArray());
        var snippet = hit.GetProperty("snippet").GetString()!;
        Assert.Contains(snippet, source, StringComparison.Ordinal);
        Assert.Contains("needlevalue", snippet, StringComparison.Ordinal);
        Assert.Equal(commit, hit.GetProperty("sourceCommit").GetString());
        Assert.True(hit.GetProperty("snippetClipped").GetBoolean());
        var excerpt = Data(await Tool(worker, "doc_section", new
        {
            repositoryId = "repo", path = "docs/exact.md", anchor = hit.GetProperty("excerptAnchor").GetString(), expectedCommit = commit, maxTokens = 512
        }));
        Assert.StartsWith(excerpt.GetProperty("text").GetString()!, source[source.IndexOf(snippet, StringComparison.Ordinal)..], StringComparison.Ordinal);

        // The core call maps the exact reader to this regression; HTTP continuations below also exercise its authenticated transport.
        var full = await tools.SectionAsync(new JsonObject { ["repositoryId"] = "repo", ["path"] = "docs/exact.md", ["anchor"] = "exact" });
        Assert.True(full.Ok, full.Code);
        var arguments = new JsonObject { ["repositoryId"] = "repo", ["path"] = "docs/exact.md", ["anchor"] = "exact", ["maxTokens"] = 64, ["expectedCommit"] = commit };
        var collected = new StringBuilder();
        JsonObject? firstNext = null;
        var focusedTokens = 0;
        for (var page = 0; page < 100; page++)
        {
            var result = await Tool(worker, "doc_section", arguments);
            Assert.Equal((HttpStatusCode.OK, "Ok"), Status(result));
            var data = Data(result);
            var text = data.GetProperty("text").GetString()!;
            Assert.Equal(commit, data.GetProperty("sourceCommit").GetString());
            Assert.True(Tokens.Count(text) <= arguments["maxTokens"]!.GetValue<int>());
            Assert.True(Tokens.Count(data.GetRawText()) <= RelayLimits.MaxDocReplyTokens);
            collected.Append(text);
            if (page == 0) focusedTokens = Tokens.Count(data.GetRawText());
            if (!data.TryGetProperty("nextRead", out var next)) break;
            firstNext ??= JsonNode.Parse(next.GetRawText())!.AsObject();
            arguments = JsonNode.Parse(next.GetRawText())!.AsObject();
            arguments["repositoryId"] = "repo";
            arguments["maxTokens"] = page % 2 == 0 ? 512 : 96;
        }
        Assert.Equal(source, collected.ToString());
        Assert.NotNull(firstNext);
        output.WriteLine($"Exact doc page: full={Encoding.UTF8.GetByteCount(full.DataJson)} bytes/{Tokens.Count(full.DataJson)} tokens; focused64={focusedTokens} complete reply tokens (same fixture).");
        Assert.True(focusedTokens < Tokens.Count(full.DataJson));

        var measured = host.Services.GetRequiredService<ReadEfficiencyMeasurements>()
            .Read("repo", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow).FocusedDocument;
        Assert.True(measured.Samples > 0);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(source) * measured.Samples, measured.SourceBytes);

        // A native caller's home repository remains repo; the public target travels with the pinned continuation.
        using var admin = Admin(host);
        var cross = Data(await Tool(admin, "doc_section", new { repositoryId = "repo", repository = "repo2", path = "docs/exact.md", anchor = "exact", maxTokens = 64 }));
        var crossNext = JsonNode.Parse(cross.GetProperty("nextRead").GetRawText())!.AsObject();
        Assert.Equal("repo2", crossNext["repository"]!.GetValue<string>());
        crossNext["repositoryId"] = "repo";
        var crossContinued = Data(await Tool(admin, "doc_section", crossNext));
        Assert.Equal(other.Head(), crossContinued.GetProperty("sourceCommit").GetString());

        // Unchanged files retain their per-file source commit when another file changes; a changed source refuses an old cursor.
        git.Commit(new() { ["docs/other.md"] = "# Other\n\nA new committed file.\n" });
        await SyncAsync(host);
        var unchangedResult = await Tool(worker, "doc_section", new { repositoryId = "repo", path = "docs/exact.md", anchor = "exact", expectedCommit = commit, maxTokens = 64 });
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(unchangedResult));
        var unchanged = Data(unchangedResult);
        Assert.Equal(commit, unchanged.GetProperty("sourceCommit").GetString());
        git.Commit(new() { ["docs/exact.md"] = source.Replace("needlevalue", "changedvalue", StringComparison.Ordinal) });
        await SyncAsync(host);
        firstNext!["repositoryId"] = "repo";
        Assert.Equal((HttpStatusCode.Conflict, "VersionConflict"), Status(await Tool(worker, "doc_section", firstNext)));
        firstNext["repositoryId"] = "other";
        firstNext["repository"] = "other";
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_section", firstNext)));
    }

    private static JsonElement DirectoryObservation(RelayHostFixture host, string repository) => JsonSerializer.SerializeToElement(
        Assert.Single(host.Services.GetRequiredService<DocCacheActivity>().Snapshot(DateTimeOffset.UtcNow).Grains, row => row.Kind == "Directory" && row.Repository == repository));

    private static readonly ITokenCounter Tokens = new O200kTokenCounter();
    private const string AdminToken = "doc-cache-admin-test-secret-not-production";

    private const string OpenApiJson = """
    {
      "openapi": "3.0.1",
      "info": { "title": "Orders", "version": "1" },
      "paths": {
        "/v1/orders": {
          "post": {
            "tags": ["orders"], "operationId": "createOrder", "summary": "Create an order",
            "parameters": [ { "name": "X-Request-Id", "in": "header", "required": true, "schema": { "type": "string", "format": "uuid" } } ],
            "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NewOrder" } } } },
            "responses": { "201": { "description": "Created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Order" } } } },
                           "409": { "description": "Duplicate request" } }
          },
          "get": { "tags": ["orders"], "summary": "List orders", "responses": { "200": { "description": "Orders", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Order" } } } } } } }
        },
        "/v1/orders/{id}": {
          "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
          "get": { "summary": "Read one order", "responses": { "200": { "description": "The order" }, "404": { "description": "No such order" } } },
          "delete": { "tags": ["admin"], "responses": { "204": { "description": "Deleted" }, "default": { "$ref": "#/components/responses/Problem" } } }
        }
      },
      "components": {
        "schemas": {
          "Order": { "description": "A placed order.", "required": ["id"], "properties": { "id": { "type": "string" }, "lines": { "type": "array", "items": { "$ref": "#/components/schemas/Line" } }, "status": { "$ref": "#/components/schemas/Status" } } },
          "NewOrder": { "properties": { "lines": { "type": "array", "items": { "type": "object" } } } },
          "Line": { "allOf": [ { "$ref": "#/components/schemas/Base" } ] }
        }
      }
    }
    """;

    private const string OpenApiYaml = """
    openapi: 3.0.1
    info: { title: Orders, version: "1" }
    paths:
      /v1/orders:
        post:
          tags: [orders]
          operationId: createOrder
          summary: Create an order
          parameters:
            - name: X-Request-Id
              in: header
              required: true
              schema: { type: string, format: uuid }
          requestBody:
            content:
              application/json:
                schema: { $ref: '#/components/schemas/NewOrder' }
          responses:
            "201":
              description: Created
              content:
                application/json:
                  schema: { $ref: '#/components/schemas/Order' }
            "409": { description: Duplicate request }
        get:
          tags: [orders]
          summary: List orders
          responses:
            "200":
              description: Orders
              content:
                application/json:
                  schema:
                    type: array
                    items: { $ref: '#/components/schemas/Order' }
      /v1/orders/{id}:
        parameters:
          - &id { name: id, in: path, required: true, schema: { type: string } }
        get:
          summary: Read one order
          responses:
            "200": { description: The order }
            "404": { description: No such order }
        delete:
          tags: [admin]
          responses:
            "204": { description: Deleted }
            default: { $ref: '#/components/responses/Problem' }
    components:
      schemas:
        Order:
          description: A placed order.
          required: [id]
          properties:
            id: { type: string }
            lines: { type: array, items: { $ref: '#/components/schemas/Line' } }
            status: { $ref: '#/components/schemas/Status' }
        NewOrder:
          properties:
            lines: { type: array, items: { type: object } }
        Line:
          allOf:
            - $ref: '#/components/schemas/Base'
    """;

    // ---- Access, limits and the off switch ----

    [Fact]
    public async Task Every_tool_checks_the_repository_scope_and_doc_sync_needs_an_administrator()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
        await using var host = DocHost(git);
        using var worker = host.AuthorizedClient();
        await SyncAsync(host);
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_search", new { repositoryId = "other", query = "text" })));
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_section", new { repositoryId = "other", path = "docs/a.md", anchor = "alpha" })));
        Assert.Equal((HttpStatusCode.Forbidden, "RepositoryDenied"), Status(await Tool(worker, "doc_sync", new { repositoryId = "other", requestId = "w-1" })));
        Assert.Equal((HttpStatusCode.Forbidden, "AdministratorRequired"), Status(await Tool(worker, "doc_sync", new { repositoryId = "repo", requestId = "w-2" })));
        Assert.Equal((HttpStatusCode.OK, "Ok"), Status(await Tool(worker, "doc_section", new { repositoryId = "repo", path = "docs/a.md", anchor = "alpha" })));
    }

    /// <summary>Indexes the files in one document grain each, in a plain silo, and runs one fan-out search.</summary>
    private static DocHit[] Search(Dictionary<string, string> files, string query)
    {
        using var git = GitRepositories.Create(files);
        using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Clone
        });
        host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None).GetAwaiter().GetResult();
        return host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").SearchAsync(query, null, null, 5, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Hits;
    }

    [Fact]
    public async Task Fetch_never_recurses_into_submodules_and_sends_the_token_only_to_the_remote_url()
    {
        using var git = GitRepositories.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
        var tokenFile = Path.Combine(Path.GetTempPath(), "cocern-doc-token-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, "read-only-secret\n");
        try
        {
            var watcher = new WatchingGit { FakeFetch = true };
            await using var host = DocHost(git, watcher, new() { ["Relay:DocCache:Repositories:0:TokenFile"] = tokenFile });
            var runner = host.Services.GetRequiredService<DocSyncRunner>();
            // A local-path remote never gets the header.
            await runner.SyncAsync("repo", null, CancellationToken.None);
            var (arguments, environment) = watcher.Fetches.Last();
            Assert.Contains("--no-recurse-submodules", arguments);
            Assert.False(environment?.ContainsKey("GIT_CONFIG_COUNT") ?? false);
            // An HTTPS remote gets it, scoped to that URL, and never on the command line.
            git.SetCloneRemote("https://git.example.com/org/repo.git");
            await runner.SyncAsync("repo", null, CancellationToken.None);
            (arguments, environment) = watcher.Fetches.Last();
            Assert.Equal(new[] { "fetch", "--quiet", "--no-tags", "--no-recurse-submodules", "origin", "+refs/heads/main:refs/remotes/origin/main" }, arguments);
            Assert.Equal("http.https://git.example.com/org/repo.git.extraHeader", environment!["GIT_CONFIG_KEY_0"]);
            Assert.Equal("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:read-only-secret")), environment["GIT_CONFIG_VALUE_0"]);
            Assert.DoesNotContain(arguments, argument => argument.Contains("read-only-secret"));
            // Remote URLs with credentials, and empty or oversized token files, send nothing.
            git.SetCloneRemote("https://user:pass@git.example.com/org/repo.git");
            await runner.SyncAsync("repo", null, CancellationToken.None);
            Assert.False(watcher.Fetches.Last().Environment?.ContainsKey("GIT_CONFIG_COUNT") ?? false);
            git.SetCloneRemote("https://git.example.com/org/repo.git");
            foreach (var content in new[] { "  \n", new string('t', RelayLimits.MaxDocTokenFileBytes + 1) })
            {
                await File.WriteAllTextAsync(tokenFile, content);
                await runner.SyncAsync("repo", null, CancellationToken.None);
                Assert.False(watcher.Fetches.Last().Environment?.ContainsKey("GIT_CONFIG_COUNT") ?? false);
            }
        }
        finally { File.Delete(tokenFile); }
    }

    [Fact]
    public async Task A_file_named_like_an_option_is_only_a_path_and_symlinks_are_skipped()
    {
        using var git = GitRepositories.Create(new() { ["--output=x.md"] = "# Dash\n\nDash refunds.\n", ["docs/real.md"] = "# Real\n\nReal refunds.\n" });
        git.SymlinkAndCommit("docs/passwd.md", "/etc/passwd");
        git.SymlinkAndCommit("docs/link.md", "real.md");
        await using var host = DocHost(git);
        using var client = host.AuthorizedClient();
        var outcome = await SyncAsync(host);
        var view = await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").ReadAsync();
        Assert.Equal(new[] { "--output=x.md", "docs/real.md" }, view.Entries.Select(entry => entry.Path));
        Assert.Equal(new[] { "docs/link.md", "docs/passwd.md" }, view.Skipped);
        Assert.Equal(2, outcome.Skipped);
        Assert.Contains("Dash refunds", Data(await Tool(client, "doc_section", new { repositoryId = "repo", path = "--output=x.md", anchor = "dash" })).GetProperty("text").GetString());
        Assert.False(File.Exists(Path.Combine(git.Clone, "x.md")));
        Assert.Empty(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "root etc passwd" })));
        // A symlink added later is skipped by the incremental sync too.
        git.SymlinkAndCommit("docs/late.md", "real.md");
        Assert.Equal(1, (await host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None)).Skipped);
    }

    // ---- Helpers ----

    /// <param name="usageWrites">How often a directory writes its counted usage; the hub's default when null.</param>
    private static RelayHostFixture DocHost(GitRepositories git, GitCli? cli = null, Dictionary<string, string?>? settings = null, string? databasePath = null, TimeSpan? usageWrites = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo",
            ["Relay:Clients:1:Repositories:1"] = "repo2",
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Clone
        };
        foreach (var (key, value) in settings ?? []) all[key] = value;
        return new RelayHostFixture(databasePath, settings: all, configureServices: services =>
        {
            if (cli is not null) { services.RemoveAll<GitCli>(); services.AddSingleton(cli); }
            if (usageWrites is { } interval) services.AddSingleton(new DocUsageOptions { WriteInterval = interval });
        });
    }

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

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>Watches git commands: counts concurrent fetches and shows, can hold fetches, and can slow every show or one path's.</summary>
    private sealed class WatchingGit : GitCli
    {
        private int fetches, shows;
        public int Commands;
        private TaskCompletionSource? hold;
        public int FetchesStarted;
        public int PeakFetches;
        public int PeakShows;
        public TimeSpan ShowDelay { get; set; }
        public string? SlowPath { get; init; }
        public string? FailPath { get; set; }
        /// <summary>Answer fetch as done without running it, so a remote URL that is never reached can still be checked.</summary>
        public bool FakeFetch { get; init; }
        public System.Collections.Concurrent.ConcurrentQueue<(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment)> Fetches { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> Shows { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<string>> Arguments { get; } = new();

        public void HoldFetch() => hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseFetch() => hold?.TrySetResult();

        private TaskCompletionSource? slowHold;
        /// <summary>Holds every read of <see cref="SlowPath"/> until <see cref="ReleaseSlow"/>, so a search's timeout passes however slow the machine is.</summary>
        public void HoldSlow() => slowHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseSlow() => slowHold?.TrySetResult();

        public override async Task<GitResult> RunAsync(string directory, IReadOnlyList<string> arguments, int maxOutputBytes, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Commands);
            Arguments.Enqueue(arguments.ToArray());
            if (arguments[0] == "fetch")
            {
                Interlocked.Increment(ref FetchesStarted);
                Fetches.Enqueue((arguments.ToArray(), environment is null ? null : new Dictionary<string, string>(environment)));
                if (FakeFetch) return new GitResult(0, [], false, "");
                Max(ref PeakFetches, Interlocked.Increment(ref fetches));
                try { if (hold is { } wait) await wait.Task; return await base.RunAsync(directory, arguments, maxOutputBytes, environment, cancellationToken); }
                finally { Interlocked.Decrement(ref fetches); }
            }
            if (arguments[0] == "show")
            {
                Shows.Enqueue(arguments[^1]);
                if (FailPath is { } fail && arguments[^1].EndsWith(":" + fail, StringComparison.Ordinal)) throw new TimeoutException("git show ran too long.");
                Max(ref PeakShows, Interlocked.Increment(ref shows));
                try
                {
                    if (ShowDelay > TimeSpan.Zero && (SlowPath is null || arguments[^1].EndsWith(":" + SlowPath, StringComparison.Ordinal))) await Task.Delay(ShowDelay, cancellationToken);
                    if (slowHold is { } slow && SlowPath is not null && arguments[^1].EndsWith(":" + SlowPath, StringComparison.Ordinal)) await slow.Task.WaitAsync(cancellationToken);
                    return await base.RunAsync(directory, arguments, maxOutputBytes, environment, cancellationToken);
                }
                finally { Interlocked.Decrement(ref shows); }
            }
            return await base.RunAsync(directory, arguments, maxOutputBytes, environment, cancellationToken);
        }

        private static void Max(ref int target, int value)
        {
            for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
                if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }

    /// <summary>
    /// A temporary origin (bare), a work clone that commits and pushes, and the hub's clone. Everything is local: git fetches from a path,
    /// with no network.
    /// </summary>
    private sealed class GitRepositories : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-doc-" + Guid.NewGuid().ToString("N"));
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
            Push("change");
        }

        public void SymlinkAndCommit(string link, string target)
        {
            var full = Path.Combine(Work, link);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.CreateSymbolicLink(full, target);
            Push("symlink");
        }

        public void SetCloneRemote(string url) => Run(Clone, "remote", "set-url", "origin", url);

        public void WriteInClone(string path, string text)
        {
            var full = Path.Combine(Clone, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public void CommitInCloneOnly(string path, string text)
        {
            WriteInClone(path, text);
            Run(Clone, "add", path);
            Run(Clone, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "local only");
        }

        private void Push(string message)
        {
            Run(Work, "add", "-A");
            Run(Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", message);
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

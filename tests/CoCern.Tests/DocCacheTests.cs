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
        using var git = GitCheckout.Create(new() { ["docs/exact.md"] = source + unrelated });
        using var other = GitCheckout.Create(new() { ["docs/exact.md"] = source.Replace("needlevalue", "foreignvalue", StringComparison.Ordinal) + unrelated });
        await using var host = DocHost(git, settings: new()
        {
            ["Relay:DocCache:Repositories:1:Id"] = "repo2", ["Relay:DocCache:Repositories:1:ClonePath"] = other.Work
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
        using var git = GitCheckout.Create(new() { ["docs/a.md"] = "# Alpha\n\nText.\n" });
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
        using var git = GitCheckout.Create(files);
        using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Work
        });
        host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None).GetAwaiter().GetResult();
        return host.Services.GetRequiredService<IGrainFactory>().GetGrain<IDocDirectoryGrain>("repo").SearchAsync(query, null, null, 5, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Hits;
    }

    [Fact]
    public async Task Sync_indexes_new_local_commits_with_legacy_fetch_and_token_settings()
    {
        using var git = GitCheckout.Create(new() { ["docs/a.md"] = "# Alpha\n\nInitial text.\n" });
        git.SetOrigin("https://git.example.invalid/org/repo.git");
        var watcher = new LocalGitOnly();
        await using var host = DocHost(git, watcher, new()
        {
            ["Relay:DocCache:Repositories:0:Fetch"] = "true",
            ["Relay:DocCache:Repositories:0:TokenFile"] = Path.Combine(git.Work, "absent-token-file")
        });
        var runner = host.Services.GetRequiredService<DocSyncRunner>();
        Assert.Equal(git.Head(), (await runner.SyncAsync("repo", null, CancellationToken.None)).Commit);
        git.Commit(new() { ["docs/a.md"] = "# Alpha\n\nUpdated local content.\n" });
        var sync = await runner.SyncAsync("repo", null, CancellationToken.None);
        Assert.Equal(git.Head(), sync.Commit);
        Assert.False(sync.Fetched);
        Assert.Equal(0, watcher.FetchAttempts);
        using var worker = host.AuthorizedClient();
        var hits = Hits(await Tool(worker, "doc_search", new { repositoryId = "repo", query = "Updated local content" }));
        Assert.Equal(sync.Commit, Assert.Single(hits).GetProperty("sourceCommit").GetString());
    }

    [Fact]
    public async Task A_file_named_like_an_option_is_only_a_path_and_symlinks_are_skipped()
    {
        using var git = GitCheckout.Create(new() { ["--output=x.md"] = "# Dash\n\nDash refunds.\n", ["docs/real.md"] = "# Real\n\nReal refunds.\n" });
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
        Assert.False(File.Exists(Path.Combine(git.Work, "x.md")));
        Assert.Empty(Hits(await Tool(client, "doc_search", new { repositoryId = "repo", query = "root etc passwd" })));
        // A symlink added later is skipped by the incremental sync too.
        git.SymlinkAndCommit("docs/late.md", "real.md");
        Assert.Equal(1, (await host.Services.GetRequiredService<DocSyncRunner>().SyncAsync("repo", null, CancellationToken.None)).Skipped);
    }

    // ---- Helpers ----

    /// <param name="usageWrites">How often a directory writes its counted usage; the hub's default when null.</param>
    private static RelayHostFixture DocHost(GitCheckout git, GitCli? cli = null, Dictionary<string, string?>? settings = null, string? databasePath = null, TimeSpan? usageWrites = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "doc-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo",
            ["Relay:Clients:1:Repositories:1"] = "repo2",
            ["Relay:DocCache:Enabled"] = "true", ["Relay:DocCache:SyncMinutes"] = "0",
            ["Relay:DocCache:Repositories:0:Id"] = "repo", ["Relay:DocCache:Repositories:0:ClonePath"] = git.Work
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

    private sealed class LocalGitOnly : GitCli
    {
        public int FetchAttempts;

        public override Task<GitResult> RunAsync(string directory, IReadOnlyList<string> arguments, int maxOutputBytes, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            if (arguments.Contains("fetch", StringComparer.Ordinal))
            {
                Interlocked.Increment(ref FetchAttempts);
                throw new InvalidOperationException("Document indexing attempted a remote fetch.");
            }
            return base.RunAsync(directory, arguments, maxOutputBytes, environment, cancellationToken);
        }
    }

    private sealed class GitCheckout : IDisposable
    {
        public string Work { get; } = Path.Combine(Path.GetTempPath(), "cocern-doc-" + Guid.NewGuid().ToString("N"));

        public static GitCheckout Create(Dictionary<string, string> files)
        {
            var checkout = new GitCheckout();
            Directory.CreateDirectory(checkout.Work);
            Run(checkout.Work, "init", "-q", "-b", "main");
            checkout.Commit(files);
            return checkout;
        }

        public string Head() => Run(Work, "rev-parse", "HEAD").Trim();

        public void Commit(Dictionary<string, string> files)
        {
            Write(files);
            Run(Work, "add", "-A");
            CommitIndex();
        }

        public void SymlinkAndCommit(string link, string target)
        {
            // The index stores a link as its target text in a mode-120000 blob.
            Write(new() { [link] = target });
            var blob = Run(Work, "hash-object", "-w", "--", link).Trim();
            Run(Work, "update-index", "--add", "--cacheinfo", "120000", blob, link);
            CommitIndex();
        }

        public void SetOrigin(string url) => Run(Work, "remote", "add", "origin", url);

        private void CommitIndex() => Run(Work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "fixture");

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
                foreach (var file in Directory.EnumerateFiles(Work, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Work, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

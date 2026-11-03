using CoCern.Core;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

/// <summary>The shared per-repository search index on its own: no grains, no git, no hub.</summary>
public sealed class DocSearchIndexTests
{
    private static readonly ITokenCounter Tokens = new O200kTokenCounter();

    private static DocSearchDocument Doc(string path, string text, string spec = "docs", string commit = "c1") => new(path, spec, commit, DocHash.Of(text), text);

    private static (string Path, int Matches)[] Counts(DocIndexResult result) =>
        result.Matches.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)).ToArray();

    private static DocSearchIndex Index(Dictionary<string, string> files)
    {
        var index = new DocSearchIndex(Tokens);
        index.Apply(files.Select(file => Doc(file.Key, file.Value)), []);
        return index;
    }

    [Fact]
    public void A_search_returns_one_hit_per_section_with_the_whole_sections_tokens_and_the_paths_commit()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("Each refund needs an order and a reason.", 15)) + "\n\n";
        var policy = "# Policy\n\n" + string.Concat(Enumerable.Repeat(paragraph, 60));
        var sections = DocFormats.Build("docs/policy.md", policy, Tokens, out _)!;
        Assert.True(Assert.Single(sections).Chunks.Count > 1);
        using var index = Index(new() { ["docs/policy.md"] = policy, ["docs/other.md"] = "# Other\n\nA refund in passing.\n\n## More\n\nAnother refund.\n" });
        var result = index.Search("refund", null, null, 10);
        Assert.Equal("policy", Assert.Single(result.Hits, hit => hit.Path == "docs/policy.md").Anchor);
        Assert.Equal(sections[0].Tokens, result.Hits.Single(hit => hit.Path == "docs/policy.md").Tokens);
        Assert.Equal(new[] { "more", "other" }, result.Hits.Where(hit => hit.Path == "docs/other.md").Select(hit => hit.Anchor).Order(StringComparer.Ordinal));
        Assert.All(result.Hits, hit => Assert.Equal("c1", hit.Commit));
        Assert.Equal(result.Hits, DocHit.Ranked(result.Hits).ToArray());
    }

    [Fact]
    public void Upserting_a_path_again_replaces_only_that_paths_chunks()
    {
        using var index = Index(new() { ["docs/a.md"] = "# Alpha\n\nOld refunds text.\n", ["docs/b.md"] = "# Beta\n\nRefunds in beta.\n" });
        const string changed = "# Alpha\n\nNew credits text.\n";
        Assert.True(index.Upsert(Doc("docs/a.md", changed, commit: "c2")));
        Assert.Equal("docs/b.md", Assert.Single(index.Search("refunds", null, null, 10).Hits).Path);
        Assert.Empty(index.Search("old", null, null, 10).Hits);
        var credits = Assert.Single(index.Search("credits", null, null, 10).Hits);
        Assert.Equal(("docs/a.md", "alpha", "c2"), (credits.Path, credits.Anchor, credits.Commit));
        Assert.True(index.Contains("docs/a.md", DocHash.Of(changed)));
        Assert.False(index.Contains("docs/a.md", DocHash.Of("# Alpha\n\nOld refunds text.\n")));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void The_same_content_at_a_new_commit_is_not_reindexed_but_carries_the_new_commit()
    {
        const string text = "# Alpha\n\nLedger text.\n";
        using var index = Index(new() { ["docs/a.md"] = text });
        Assert.False(index.Upsert(Doc("docs/a.md", text, commit: "c2")));
        Assert.Equal("c2", Assert.Single(index.Search("ledger", null, null, 10).Hits).Commit);
    }

    [Fact]
    public void Remove_drops_a_path_and_its_hits()
    {
        using var index = Index(new() { ["docs/a.md"] = "# Alpha\n\nRefunds here.\n", ["docs/b.md"] = "# Beta\n\nRefunds there.\n" });
        index.Remove("docs/a.md");
        index.Remove("docs/never.md");
        var result = index.Search("refunds", null, null, 10);
        Assert.Equal("docs/b.md", Assert.Single(result.Hits).Path);
        Assert.Equal(new[] { ("docs/b.md", 1) }, Counts(result));
        Assert.False(index.Contains("docs/a.md", DocHash.Of("# Alpha\n\nRefunds here.\n")));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Spec_and_path_narrow_a_search_and_its_counts()
    {
        using var index = new DocSearchIndex(Tokens);
        index.Apply([Doc("docs/a.md", "# Alpha\n\nLedger alpha.\n"), Doc("api/b.md", "# Beta\n\nLedger beta.\n", spec: "api")], []);
        var api = index.Search("ledger", "api", null, 10);
        Assert.Equal("api/b.md", Assert.Single(api.Hits).Path);
        Assert.Equal(new[] { ("api/b.md", 1) }, Counts(api));
        var one = index.Search("ledger", null, "docs/a.md", 10);
        Assert.Equal("docs/a.md", Assert.Single(one.Hits).Path);
        Assert.Equal(new[] { ("docs/a.md", 1) }, Counts(one));
        var none = index.Search("ledger", "nothing", null, 10);
        Assert.Empty(none.Hits);
        Assert.Empty(none.Matches);
        // A caller's own filter (the paths that have answered) narrows further.
        Assert.Equal("api/b.md", Assert.Single(index.Search("ledger", null, null, 10, path => path.StartsWith("api/", StringComparison.Ordinal)).Hits).Path);
    }

    [Fact]
    public void Claim_finds_api_word_forms_and_ranks_the_heading_first()
    {
        using var index = Index(new()
        {
            ["docs/camel.md"] = "# Fields\n\nThe taskClaim flag is set.\n",
            ["docs/snake.md"] = "# Columns\n\nThe task_claim column holds it.\n",
            ["docs/url.md"] = "# Routes\n\nCall /v1/tasks/{id}/claim to hold a task.\n",
            ["docs/rank.md"] = "# Guide\n\n## Other\n\nThis mentions claim in passing among many other words about something else.\n\n## Claim\n\nTake a task.\n"
        });
        var hits = index.Search("claim", null, null, 10).Hits;
        foreach (var path in new[] { "docs/camel.md", "docs/snake.md", "docs/url.md" }) Assert.Contains(hits, hit => hit.Path == path);
        Assert.Equal(new[] { "claim", "other" }, hits.Where(hit => hit.Path == "docs/rank.md").Select(hit => hit.Anchor));
        Assert.Equal("Guide > Claim", hits[0].Heading);
        Assert.True(hits[0].HeadingMatch);
        Assert.Contains("[claim]", hits.Single(hit => hit.Path == "docs/url.md").Snippet);
        // Search syntax is read as words and never fails.
        foreach (var query in new[] { "/v1/tasks/{id}: \"claim\"", "a:b", "{}", "claim AND", "\"" }) _ = index.Search(query, null, null, 10);
    }

    [Fact]
    public void How_do_I_claim_a_task_ranks_the_claim_heading_first()
    {
        using var index = Index(new()
        {
            ["docs/guide.md"] = "# Guide\n\n## How do I get started\n\nHow do I do it? Do a build, then do a run. I do a lot of it, and a lot more. How do I know? I do, and I do a lot.\n\n## Claim\n\nTake a task for your session.\n\n## Tasks\n\nA task list, with a task per line.\n",
            ["docs/faq.md"] = "# FAQ\n\n## How do I do this\n\nHow do I do a thing? I do it. Do I? How? A lot of how and do and I.\n"
        });
        var hits = index.Search("how do I claim a task", null, null, 5).Hits;
        Assert.Equal(("docs/guide.md", "claim"), (hits[0].Path, hits[0].Anchor));
        Assert.Equal(1f, hits[0].Coverage);
        Assert.All(hits.Where(hit => hit.Anchor.StartsWith("how-do", StringComparison.Ordinal)), hit => Assert.Equal(0f, hit.Coverage));
    }

    [Fact]
    public void Snippets_bracket_the_matches_and_fall_back_to_the_chunk_start_when_only_the_heading_matched()
    {
        using var index = Index(new() { ["docs/a.md"] = "# Fast\n\nFast refunds take five days.\n", ["docs/b.md"] = "# Refunds\n\nNothing   else\nhere.\n" });
        var hits = index.Search("refunds", null, null, 10).Hits;
        Assert.Contains("[refunds]", hits.Single(hit => hit.Path == "docs/a.md").Snippet);
        Assert.Equal("Nothing else here.", hits.Single(hit => hit.Path == "docs/b.md").Snippet);
        Assert.All(hits, hit => Assert.True(hit.Snippet.Length <= CoCern.Contracts.RelayLimits.MaxDocSnippetChars + 1));
    }

    [Fact]
    public void Matches_count_each_searched_paths_sections_up_to_the_limit_including_none()
    {
        using var index = Index(new()
        {
            ["docs/a.md"] = "# One\n\nLedger one.\n\n# Two\n\nLedger two.\n\n# Three\n\nLedger three.\n",
            ["docs/b.md"] = "# Single\n\nLedger single.\n",
            ["docs/c.md"] = "# Unrelated\n\nNothing here.\n",
            // Its splitter refuses it: indexed as empty, still searched.
            ["docs/bad.json"] = "{ not json"
        });
        Assert.True(index.Contains("docs/bad.json", DocHash.Of("{ not json")));
        var two = index.Search("ledger", null, null, 2);
        Assert.Equal(2, two.Hits.Length);
        Assert.Equal(new[] { ("docs/a.md", 2), ("docs/b.md", 1), ("docs/bad.json", 0), ("docs/c.md", 0) }, Counts(two));
        var ten = index.Search("ledger", null, null, 10);
        Assert.Equal(4, ten.Hits.Length);
        Assert.Equal(new[] { ("docs/a.md", 3), ("docs/b.md", 1), ("docs/bad.json", 0), ("docs/c.md", 0) }, Counts(ten));
        var nothing = index.Search("   ", null, null, 10);
        Assert.Empty(nothing.Hits);
        Assert.Equal(4, nothing.Matches.Count);
        Assert.All(nothing.Matches.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public async Task Searches_running_while_a_path_is_upserted_never_fail_and_always_see_one_whole_version()
    {
        var files = Enumerable.Range(0, 10).ToDictionary(i => $"docs/d{i}.md", i => $"# Doc {i}\n\nLedger {i}.\n");
        static string Live(int version) => $"# Live\n\nLedger intro {version}.\n\n## One\n\nLedger one {version}.\n\n## Two\n\nLedger two {version}.\n";
        files["docs/live.md"] = Live(0);
        using var index = Index(files);
        using var done = new CancellationTokenSource();
        var searches = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            do
            {
                var result = index.Search("ledger", null, null, 30);
                Assert.Equal(3, result.Matches["docs/live.md"]);
                Assert.Single(result.Hits.Where(hit => hit.Path == "docs/live.md").Select(hit => hit.Commit).Distinct());
                Interlocked.Increment(ref searches);
            } while (!done.IsCancellationRequested);
        })).ToArray();
        var writer = Task.Run(() =>
        {
            for (var version = 1; version <= 40; version++) Assert.True(index.Upsert(Doc("docs/live.md", Live(version), commit: "v" + version)));
        });
        try { await writer; }
        finally { done.Cancel(); }
        await Task.WhenAll(readers);
        Assert.True(searches >= 4);
        Assert.True(index.Contains("docs/live.md", DocHash.Of(Live(40))));
        Assert.All(index.Search("ledger", null, "docs/live.md", 10).Hits, hit => Assert.Equal("v40", hit.Commit));
    }
}

using System.Diagnostics;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Highlight;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using PkRelay.Contracts;

namespace PkRelay.Core;

/// <summary>What a document grain needs to load its file: where it is, and the commit and hash the directory recorded.</summary>
[GenerateSerializer, Immutable]
public sealed record DocRef(
    [property: Id(0)] string RepositoryId, [property: Id(1)] string Path, [property: Id(2)] string Spec,
    [property: Id(3)] string Commit, [property: Id(4)] string Hash, [property: Id(5)] string Branch);

/// <summary>One document as the directory lists it.</summary>
[GenerateSerializer, Immutable]
public sealed record DocEntry(
    [property: Id(0)] string Path, [property: Id(1)] string Spec, [property: Id(2)] string Commit,
    [property: Id(3)] string Hash, [property: Id(4)] int Sections, [property: Id(5)] int Tokens);

/// <summary>
/// One search hit: the best chunk of one section. <see cref="Tokens"/> is what the whole section costs. <see cref="Coverage"/> is the
/// share of the query's distinct terms the chunk (heading and body) contains, from 0 to 1.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record DocHit(
    [property: Id(0)] string Path, [property: Id(1)] string Anchor, [property: Id(2)] string Heading, [property: Id(3)] string Snippet,
    [property: Id(4)] float Score, [property: Id(5)] int Tokens, [property: Id(6)] string Commit, [property: Id(7)] bool HeadingMatch,
    [property: Id(8)] float Coverage)
{
    /// <summary>
    /// Best first: more of the query's terms, then the BM25 score, then a heading match. Each document has its own index, so BM25 scores
    /// are only roughly comparable across documents; covering more terms (such as both the verb and the path) decides first.
    /// </summary>
    public static IOrderedEnumerable<DocHit> Ranked(IEnumerable<DocHit> hits) =>
        hits.OrderByDescending(hit => hit.Coverage).ThenByDescending(hit => hit.Score).ThenByDescending(hit => hit.HeadingMatch)
            .ThenBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.Anchor, StringComparer.Ordinal);
}

/// <summary>A document grain's hits; <see cref="FellBack"/> when its recorded commit was gone and it read the branch head instead.</summary>
[GenerateSerializer, Immutable]
public sealed record DocGrainHits([property: Id(0)] bool FellBack, [property: Id(1)] DocHit[] Hits);

/// <summary>A section from one chunk on: the chunks in order, so the text put together is exactly the section text from that chunk.</summary>
[GenerateSerializer, Immutable]
public sealed record DocSectionChunks(
    [property: Id(0)] string Path, [property: Id(1)] string Anchor, [property: Id(2)] string Heading, [property: Id(3)] string Commit,
    [property: Id(4)] int Tokens, [property: Id(5)] int FirstChunk, [property: Id(6)] string[] Chunks, [property: Id(7)] int[] ChunkTokens);

/// <summary>A merged search over the directory's documents. <see cref="NotSearched"/> documents did not answer before the search timeout.</summary>
[GenerateSerializer, Immutable]
public sealed record DocSearchOutcome(
    [property: Id(0)] DocHit[] Hits, [property: Id(1)] int Documents, [property: Id(2)] int NotSearched, [property: Id(3)] bool PathUnknown,
    [property: Id(4)] int PeakConcurrency);

[GenerateSerializer, Immutable]
public sealed record DocDirectoryView(
    [property: Id(0)] string Branch, [property: Id(1)] string Commit, [property: Id(2)] bool NeedsResync, [property: Id(3)] DateTimeOffset? SyncedAt,
    [property: Id(4)] DocEntry[] Entries, [property: Id(5)] string[] Skipped);

/// <summary>
/// One sync's changes. <see cref="Full"/> replaces the skipped list and clears a pending full resync. <see cref="Skipped"/> are files
/// left out (too large, too many sections, or past the document limit); <see cref="Unskipped"/> are files no longer skipped.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record DocDirectoryUpdate(
    [property: Id(0)] string Branch, [property: Id(1)] string Commit, [property: Id(2)] bool Full, [property: Id(3)] DocEntry[] Upserts,
    [property: Id(4)] string[] Removed, [property: Id(5)] string[] Skipped, [property: Id(6)] string[] Unskipped);

/// <summary>For tests and diagnostics: what a document grain holds now.</summary>
[GenerateSerializer, Immutable]
public sealed record DocGrainStats([property: Id(0)] bool Loaded, [property: Id(1)] string Commit, [property: Id(2)] string Hash, [property: Id(3)] int IndexRuns, [property: Id(4)] int Reads, [property: Id(5)] int Sections);

/// <summary>The directory's persisted state: the branch and commit it follows, and each document's entry.</summary>
[GenerateSerializer]
public sealed class DocDirectoryState
{
    [Id(0)] public string Branch { get; set; } = "";
    [Id(1)] public string Commit { get; set; } = "";
    [Id(2)] public bool NeedsResync { get; set; }
    [Id(3)] public DateTimeOffset? SyncedAt { get; set; }
    [Id(4)] public Dictionary<string, DocEntry> Docs { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Paths a sync left out, at most <see cref="RelayLimits.MaxDocsPerRepositoryCeiling"/>.</summary>
    [Id(5)] public SortedSet<string> Skipped { get; set; } = new(StringComparer.Ordinal);
}

public static class DocKeys
{
    /// <summary>A document grain's key: <c>{repository}/{path}</c>, with the repository ID escaped.</summary>
    public static string Document(string repositoryId, string path) => Uri.EscapeDataString(repositoryId) + "/" + path;
    public static readonly GrainType DocumentType = GrainType.Create("pkrelay.doc");
}

public interface IDocumentGrain : IGrainWithStringKey
{
    Task<DocGrainHits> SearchAsync(DocRef doc, string query, int top);
    Task<DocSectionChunks?> SectionAsync(DocRef doc, string anchor);
    /// <summary>Pushes new content from a sync; an unchanged content hash is not re-indexed. Returns whether it re-indexed.</summary>
    Task<bool> ReplaceAsync(DocRef doc, string markdown);
    /// <summary>The file was deleted: drop the index now.</summary>
    Task DropAsync();
    Task<DocGrainStats> StatsAsync();
}

public interface IDocDirectoryGrain : IGrainWithStringKey
{
    [ReadOnly] Task<DocDirectoryView> ReadAsync();
    Task ApplyAsync(DocDirectoryUpdate update);
    Task MarkResyncAsync();
    [ReadOnly] Task<DocSearchOutcome> SearchAsync(string query, string? spec, string? path, int limit, TimeSpan timeout);
    [ReadOnly] Task<DocSectionChunks?> SectionAsync(string path, string anchor);
}

/// <summary>
/// One synced document's in-memory Lucene index (markdown, text or OpenAPI), keyed by <c>{repository}/{path}</c>. Nothing is persisted: idle deactivation (the grain
/// type's collection age, <c>Relay:DocCache:IdleMinutes</c>) disposes the index, and the next call loads the file again with
/// <c>git show &lt;commit&gt;:&lt;path&gt;</c> at the commit the directory passes. One Lucene document per section chunk; BM25 ranking with
/// headings weighted 3 times. Orleans runs one call at a time, which is Lucene's single-writer rule.
/// </summary>
[GrainType("pkrelay.doc")]
public sealed class DocumentGrain(IDocSource source, ITokenCounter tokens) : Grain, IDocumentGrain
{
    private const string AnchorField = "anchor";
    private const string PartField = "part";
    private const string TokensField = "tokens";
    /// <summary>Separates a section anchor from a chunk number: <c>errors~2</c> is the second chunk of the section <c>errors</c>.</summary>
    public const char ChunkSeparator = '~';
    private readonly RAMDirectory directory = new();
    private readonly Analyzer analyzer = new ApiDocAnalyzer();
    private IndexWriter writer = null!;
    private SearcherManager searchers = null!;
    private IReadOnlyList<DocSection> sections = [];
    private Dictionary<string, int> byAnchor = new(StringComparer.Ordinal);
    private string? loadedFor;
    private string? contentHash;
    private string commit = "";
    private int indexRuns, reads;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        writer = new IndexWriter(directory, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer) { Similarity = new BM25Similarity(), OpenMode = OpenMode.CREATE });
        searchers = new SearcherManager(writer, true, new Bm25SearcherFactory());
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        searchers.Dispose(); writer.Dispose(); directory.Dispose(); analyzer.Dispose();
        return Task.CompletedTask;
    }

    public async Task<DocGrainHits> SearchAsync(DocRef doc, string query, int top)
    {
        var fellBack = await EnsureLoadedAsync(doc);
        if (DocQuery.Parse(analyzer, query) is not { } parsed || sections.Count == 0) return new(fellBack, []);
        var queryTerms = DocQuery.Terms(analyzer, query);
        var highlighter = new Highlighter(new SimpleHTMLFormatter("[", "]"), new QueryScorer(parsed, DocQuery.Body)) { TextFragmenter = new SimpleFragmenter(RelayLimits.MaxDocSnippetChars / 2) };
        var searcher = searchers.Acquire();
        try
        {
            var candidates = new List<DocHit>();
            foreach (var scored in searcher.Search(parsed, Math.Max(top, 1) * 4).ScoreDocs)
            {
                var stored = searcher.Doc(scored.Doc);
                var heading = stored.Get(DocQuery.Heading);
                var body = stored.Get(DocQuery.Body);
                var headingTerms = DocQuery.Terms(analyzer, heading);
                var inHeading = queryTerms.Count(headingTerms.Contains);
                var covered = inHeading == queryTerms.Count ? inHeading : DocQuery.Terms(analyzer, heading + "\n" + body).Count(queryTerms.Contains);
                // The body rides in Snippet until the hits are chosen, so only returned hits are highlighted.
                candidates.Add(new(doc.Path, stored.Get(AnchorField), heading, body, scored.Score, stored.GetField(TokensField).GetInt32Value() ?? 0, commit,
                    inHeading > 0, queryTerms.Count == 0 ? 0 : (float)covered / queryTerms.Count));
            }
            // One hit per section: its best chunk.
            var hits = DocHit.Ranked(candidates).DistinctBy(hit => hit.Anchor).Take(top).Select(hit => hit with { Snippet = Snippet(highlighter, hit.Snippet) }).ToArray();
            return new(fellBack, hits);
        }
        finally { searchers.Release(searcher); }
    }

    public async Task<DocSectionChunks?> SectionAsync(DocRef doc, string anchor)
    {
        await EnsureLoadedAsync(doc);
        var separator = anchor.LastIndexOf(ChunkSeparator);
        var name = separator < 0 ? anchor : anchor[..separator];
        var first = 1;
        if (separator >= 0 && (!int.TryParse(anchor[(separator + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out first) || first < 1)) return null;
        if (!byAnchor.TryGetValue(name, out var index) || first > sections[index].Chunks.Count) return null;
        var section = sections[index];
        var chunks = section.Chunks.Skip(first - 1).ToArray();
        return new(doc.Path, section.Anchor, section.HeadingPath, commit, section.Tokens, first, chunks.Select(chunk => chunk.Text).ToArray(), chunks.Select(chunk => chunk.Tokens).ToArray());
    }

    public Task<bool> ReplaceAsync(DocRef doc, string markdown)
    {
        var hash = DocHash.Of(markdown);
        // A push is the content itself, so a pending load for an older version is no longer needed.
        var changed = hash != contentHash;
        if (changed) Index(doc.Path, markdown, hash);
        commit = doc.Commit;
        loadedFor = doc.Hash;
        return Task.FromResult(changed);
    }

    public Task DropAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task<DocGrainStats> StatsAsync() => Task.FromResult(new DocGrainStats(loadedFor is not null, commit, contentHash ?? "", indexRuns, reads, sections.Count));

    /// <summary>
    /// Loads the file at the directory's recorded commit when this activation has not loaded that version yet. When the commit is gone
    /// (a force push, then garbage collection) it reads the branch head and returns true, so the directory schedules a full resync.
    /// When neither read works, nothing is loaded and the next call tries again.
    /// </summary>
    private async Task<bool> EnsureLoadedAsync(DocRef doc)
    {
        if (loadedFor == doc.Hash) return false;
        reads++;
        var content = await source.ReadAsync(doc.RepositoryId, doc.Path, doc.Commit);
        var fellBack = content is null;
        if (content is null && doc.Branch.Length > 0) content = await source.ReadHeadAsync(doc.RepositoryId, doc.Path, doc.Branch);
        if (content is null) return fellBack;
        if (content.Hash != contentHash) Index(doc.Path, content.Text, content.Hash);
        commit = content.Commit;
        loadedFor = doc.Hash;
        return fellBack;
    }

    /// <summary>
    /// Re-indexes the whole file: delete everything, add every section chunk, commit. Sections the new version lacks disappear. A file its
    /// splitter refuses (the sync skips those) indexes as empty.
    /// </summary>
    private void Index(string path, string content, string hash)
    {
        sections = DocFormats.Build(path, content, tokens, out _) ?? [];
        byAnchor = sections.Select((section, index) => (section.Anchor, index)).ToDictionary(item => item.Anchor, item => item.index, StringComparer.Ordinal);
        writer.DeleteAll();
        foreach (var section in sections)
            for (var part = 0; part < section.Chunks.Count; part++)
            {
                // The first chunk's heading line is indexed as the heading only, so a heading match and a body match stay apart.
                var body = part == 0 ? section.Chunks[0].Text[Math.Min(section.HeadingLength, section.Chunks[0].Text.Length)..] : section.Chunks[part].Text;
                writer.AddDocument(new Document
                {
                    new StringField(AnchorField, section.Anchor, Field.Store.YES),
                    new Int32Field(PartField, part, Field.Store.YES),
                    new TextField(DocQuery.Heading, section.HeadingPath, Field.Store.YES),
                    new TextField(DocQuery.Body, body, Field.Store.YES),
                    new Int32Field(TokensField, section.Tokens, Field.Store.YES)
                });
            }
        writer.Commit();
        searchers.MaybeRefresh();
        contentHash = hash;
        indexRuns++;
    }

    /// <summary>The lines around the best matches, matches in brackets, on one line; the start of the chunk when only the heading matched.</summary>
    private string Snippet(Highlighter highlighter, string body)
    {
        string text;
        try { text = string.Join(" … ", highlighter.GetBestFragments(analyzer, DocQuery.Body, body, 2)); }
        catch (InvalidTokenOffsetsException) { text = ""; }
        if (text.Length == 0) text = body;
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= RelayLimits.MaxDocSnippetChars ? text : text[..RelayLimits.MaxDocSnippetChars].TrimEnd() + "…";
    }

    private sealed class Bm25SearcherFactory : SearcherFactory
    {
        public override IndexSearcher NewSearcher(IndexReader reader) => new(reader) { Similarity = new BM25Similarity() };
    }
}

/// <summary>
/// One repository's document directory (keyed by the repository ID), persisted through the hub's grain storage: the branch and commit
/// it follows, and per document its path, spec, indexed commit, content hash, section count and token total. Searches fan out from here
/// to the document grains, at most <see cref="RelayLimits.MaxDocFanOut"/> at a time, and merge their hits by score. Reads are
/// <see cref="ReadOnlyAttribute"/>, so searches run side by side; a sync's update waits for them.
/// </summary>
[GrainType("pkrelay.docdir")]
public sealed class DocDirectoryGrain([PersistentState("docs", "relay")] IPersistentState<DocDirectoryState> state) : Grain, IDocDirectoryGrain
{
    private string Repository => this.GetPrimaryKeyString();

    public Task<DocDirectoryView> ReadAsync() => Task.FromResult(new DocDirectoryView(state.State.Branch, state.State.Commit, state.State.NeedsResync, state.State.SyncedAt,
        state.State.Docs.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray(), state.State.Skipped.ToArray()));

    public async Task ApplyAsync(DocDirectoryUpdate update)
    {
        var current = state.State;
        current.Branch = update.Branch;
        current.Commit = update.Commit;
        current.SyncedAt = DateTimeOffset.UtcNow;
        foreach (var path in update.Removed) current.Docs.Remove(path);
        foreach (var entry in update.Upserts) current.Docs[entry.Path] = entry;
        if (update.Full) { current.NeedsResync = false; current.Skipped.Clear(); }
        foreach (var path in update.Unskipped) current.Skipped.Remove(path);
        foreach (var path in update.Skipped) if (current.Skipped.Count < RelayLimits.MaxDocsPerRepositoryCeiling) current.Skipped.Add(path);
        await state.WriteStateAsync();
    }

    public async Task MarkResyncAsync()
    {
        if (state.State.NeedsResync) return;
        state.State.NeedsResync = true;
        await state.WriteStateAsync();
    }

    public async Task<DocSearchOutcome> SearchAsync(string query, string? spec, string? path, int limit, TimeSpan timeout)
    {
        var current = state.State;
        if (path is not null && !current.Docs.ContainsKey(path)) return new([], 0, 0, true, 0);
        var candidates = current.Docs.Values.Where(entry => (path is null || entry.Path == path) && (spec is null || entry.Spec == spec))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var results = new DocGrainHits?[candidates.Length];
        var gate = new SemaphoreSlim(RelayLimits.MaxDocFanOut);
        var running = 0;
        var peak = 0;
        using var expired = new CancellationTokenSource(timeout);
        async Task AskAsync(int index)
        {
            try { await gate.WaitAsync(expired.Token); }
            catch (OperationCanceledException) { return; }
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            try { results[index] = await GrainFactory.GetGrain<IDocumentGrain>(DocKeys.Document(Repository, candidates[index].Path)).SearchAsync(Ref(candidates[index]), query, limit); }
            catch (Exception) { results[index] = new(false, []); /* A failing document is left out; the others still answer. */ }
            finally { Interlocked.Decrement(ref running); gate.Release(); }
        }
        var all = Task.WhenAll(Enumerable.Range(0, candidates.Length).Select(AskAsync));
        try { await all.WaitAsync(expired.Token); }
        catch (OperationCanceledException) { /* Past the search timeout: merge what has answered. */ }
        var answered = results.ToArray();
        if (answered.Any(result => result?.FellBack == true)) this.AsReference<IDocDirectoryGrain>().MarkResyncAsync().Ignore();
        var hits = DocHit.Ranked(answered.Where(result => result is not null).SelectMany(result => result!.Hits)).Take(limit).ToArray();
        return new(hits, candidates.Length, answered.Count(result => result is null), false, peak);
    }

    public Task<DocSectionChunks?> SectionAsync(string path, string anchor) =>
        state.State.Docs.TryGetValue(path, out var entry)
            ? GrainFactory.GetGrain<IDocumentGrain>(DocKeys.Document(Repository, path)).SectionAsync(Ref(entry), anchor)
            : Task.FromResult<DocSectionChunks?>(null);

    private DocRef Ref(DocEntry entry) => new(Repository, entry.Path, entry.Spec, entry.Commit, entry.Hash, state.State.Branch);

    private static void InterlockedMax(ref int target, int value)
    {
        for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}

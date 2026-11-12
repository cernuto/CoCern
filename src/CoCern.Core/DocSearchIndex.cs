using System.Collections.Immutable;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Highlight;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using CoCern.Contracts;

namespace CoCern.Core;

/// <summary>One file to index: its path and spec, the commit it was read at, its content hash, and its text.</summary>
public sealed record DocSearchDocument(string Path, string Spec, string Commit, string Hash, string Text);

/// <summary>
/// A search's hits, best first, and for every path it searched how many of that path's sections matched (at most the limit, 0 for none):
/// what a document grain's own search used to return, for the directory's usage counts.
/// </summary>
public sealed record DocIndexResult(DocHit[] Hits, IReadOnlyDictionary<string, int> Matches);

/// <summary>
/// One repository's in-memory Lucene index: every section chunk of every file, one Lucene document each, with its path, anchor and part,
/// heading and body indexed as a document grain indexes them (the same fields, analyzer, BM25 and heading boost). Spec, commit, content hash
/// and the sections themselves sit in a side map, so the text is held once. Writes (<see cref="Apply"/>) run one at a time and each merges
/// away deleted chunks and refreshes the searchers once, so a caller with many changed files passes them in one call; searches run side
/// by side and each sees one whole version of the index and its side map.
/// </summary>
public sealed class DocSearchIndex : IDisposable
{
    private const string PathField = "path";
    private const string AnchorField = "anchor";
    private const string PartField = "part";
    /// <summary>The score share a hit keeps when it has none of the query's content terms (only stop words matched).</summary>
    public const float CoverageFloor = 0.1f;

    private sealed record Indexed(string Spec, string Hash, string Commit, IReadOnlyList<DocSection> Sections, IReadOnlyDictionary<string, int> ByAnchor);

    private readonly ITokenCounter tokens;
    private readonly RAMDirectory directory = new();
    private readonly Analyzer analyzer = new ApiDocAnalyzer();
    private readonly IndexWriter writer;
    private readonly SearcherManager searchers;
    // Writers run one at a time; `current` pairs the searcher with the side map it was refreshed for.
    private readonly object writes = new();
    private readonly object current = new();
    private ImmutableDictionary<string, Indexed> documents = ImmutableDictionary.Create<string, Indexed>(StringComparer.Ordinal);
    private bool disposed;

    public DocSearchIndex(ITokenCounter tokens)
    {
        this.tokens = tokens;
        writer = new IndexWriter(directory, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)
        {
            Similarity = new BM25Similarity(), OpenMode = OpenMode.CREATE,
            // A replaced path leaves deleted chunks behind; merging away every one keeps BM25's statistics to live chunks.
            MergePolicy = new TieredMergePolicy { ForceMergeDeletesPctAllowed = 0 }
        });
        searchers = new SearcherManager(writer, true, new Bm25SearcherFactory());
    }

    /// <summary>The number of files indexed, including files their splitter refused (indexed as empty).</summary>
    public int Count => Volatile.Read(ref documents).Count;

    /// <summary>Whether <paramref name="path"/> is indexed with exactly this content.</summary>
    public bool Contains(string path, string hash) => Volatile.Read(ref documents).TryGetValue(path, out var indexed) && indexed.Hash == hash;

    /// <summary>Indexes one file, replacing its earlier chunks; returns whether it re-indexed (an unchanged hash only updates spec and commit).</summary>
    public bool Upsert(DocSearchDocument document) => Apply([document], []) > 0;

    public void Remove(string path) => Apply([], [path]);

    /// <summary>
    /// Removes, then upserts, as one batch, and returns how many files it re-indexed. Each changed path's chunks are deleted by its path
    /// term and its sections added again; a file its splitter refuses indexes as empty, as a document grain does. Deleted chunks are merged
    /// away and the searchers refreshed once for the whole batch, both costing about as much for one file as for many.
    /// </summary>
    public int Apply(IEnumerable<DocSearchDocument> upserts, IEnumerable<string> removed)
    {
        lock (writes)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = documents.ToBuilder();
            var deleted = false;
            var reindexed = 0;
            foreach (var path in removed)
                if (next.Remove(path)) { writer.DeleteDocuments(new Term(PathField, path)); deleted = true; }
            foreach (var document in upserts)
            {
                if (next.TryGetValue(document.Path, out var existing))
                {
                    if (existing.Hash == document.Hash) { next[document.Path] = existing with { Spec = document.Spec, Commit = document.Commit }; continue; }
                    writer.DeleteDocuments(new Term(PathField, document.Path));
                    deleted = true;
                }
                var sections = DocFormats.Build(document.Path, document.Text, tokens, out _) ?? [];
                foreach (var section in sections)
                    for (var part = 0; part < section.Chunks.Count; part++)
                    {
                        // As in a document grain: the first chunk's heading line is indexed as the heading only, and no text is stored.
                        writer.AddDocument(new Document
                        {
                            new StringField(PathField, document.Path, Field.Store.NO),
                            new SortedDocValuesField(PathField, new BytesRef(document.Path)),
                            new StringField(AnchorField, section.Anchor, Field.Store.YES),
                            new Int32Field(PartField, part, Field.Store.YES),
                            new TextField(DocQuery.Heading, section.HeadingPath, Field.Store.NO),
                            new TextField(DocQuery.Body, BodyOf(section, part), Field.Store.NO)
                        });
                    }
                next[document.Path] = new Indexed(document.Spec, document.Hash, document.Commit, sections,
                    sections.Select((section, index) => (section.Anchor, index)).ToDictionary(item => item.Anchor, item => item.index, StringComparer.Ordinal));
                reindexed++;
            }
            if (deleted) writer.ForceMergeDeletes(true);
            lock (current)
            {
                searchers.MaybeRefreshBlocking();
                Volatile.Write(ref documents, next.ToImmutable());
            }
            return reindexed;
        }
    }

    /// <summary>
    /// The best <paramref name="limit"/> hits over the files that pass <paramref name="spec"/>, <paramref name="path"/> and
    /// <paramref name="include"/>, scored exactly as a document grain scores its own: BM25 scaled by the IDF-weighted coverage of the
    /// query's content terms, at most one hit per section (its best chunk) and <paramref name="limit"/> per file, then merged with
    /// <see cref="DocHit.Ranked"/>. Only the returned hits are highlighted.
    /// </summary>
    public DocIndexResult Search(string query, string? spec, string? path, int limit, Func<string, bool>? include = null)
    {
        IndexSearcher searcher;
        ImmutableDictionary<string, Indexed> docs;
        lock (current)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            searcher = searchers.Acquire();
            docs = documents;
        }
        try
        {
            var matches = docs.Where(doc => (path is null || doc.Key == path) && (spec is null || doc.Value.Spec == spec) && (include is null || include(doc.Key)))
                .Select(doc => doc.Key).Order(StringComparer.Ordinal).ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
            if (matches.Count == 0 || DocQuery.Parse(analyzer, query) is not { } parsed) return new([], matches);
            var queryTerms = DocQuery.Terms(analyzer, query);
            var content = queryTerms.Where(term => !DocQuery.IsStopTerm(term)).ToArray();
            var highlighter = new Highlighter(new SimpleHTMLFormatter("", ""), new QueryScorer(parsed, DocQuery.Body)) { TextFragmenter = new SimpleFragmenter(RelayLimits.MaxDocSnippetChars / 2) };
            var weights = content.ToDictionary(term => term, term => Idf(searcher.IndexReader, term), StringComparer.Ordinal);
            var total = weights.Values.Sum();
            // Which chunks hold each query term, read once per query from the index instead of analyzing every candidate chunk's text again.
            // The analyzer only splits and rewrites single words, so a chunk's indexed heading and body terms are exactly the terms of its text.
            var inHeading = queryTerms.ToDictionary(term => term, term => Holders(searcher.IndexReader, DocQuery.Heading, term), StringComparer.Ordinal);
            var inBody = content.ToDictionary(term => term, term => Holders(searcher.IndexReader, DocQuery.Body, term), StringComparer.Ordinal);
            // A document grain took its own top 4 × limit chunks; so does each file here.
            var collector = new PerPathCollector(Math.Max(limit, 1) * 4, matches.ContainsKey);
            // Let Lucene skip excluded paths before collecting chunks, without changing the query's scores.
            if (matches.Count < docs.Count)
                searcher.Search(parsed, new Lucene.Net.Queries.TermsFilter(PathField, matches.Keys.Select(key => new BytesRef(key)).ToArray()), collector);
            else
                searcher.Search(parsed, collector);
            var hits = new List<DocHit>();
            foreach (var (file, best) in collector.Best)
            {
                var indexed = docs[file];
                var candidates = new List<DocHit>();
                foreach (var (score, doc) in best.UnorderedItems.Select(item => item.Priority).OrderByDescending(item => item.Score).ThenBy(item => item.Doc))
                {
                    var stored = searcher.Doc(doc);
                    var section = indexed.Sections[indexed.ByAnchor[stored.Get(AnchorField)]];
                    var part = stored.GetField(PartField).GetInt32Value() ?? 0;
                    var body = BodyOf(section, part);
                    var coverage = total <= 0 ? 1f : (float)(weights.Where(weight => inHeading[weight.Key].Get(doc) || inBody[weight.Key].Get(doc)).Sum(weight => weight.Value) / total);
                    // The body rides in Snippet until the hits are chosen, so only returned hits are highlighted.
                    candidates.Add(new(file, section.Anchor, section.HeadingPath, body, score * (CoverageFloor + (1 - CoverageFloor) * coverage * coverage),
                        section.Tokens, indexed.Commit, queryTerms.Any(term => inHeading[term].Get(doc)), coverage)
                    {
                        ExcerptAnchor = section.Anchor + DocumentGrain.ChunkSeparator + (part + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ExcerptOffset = part == 0 ? Math.Min(section.HeadingLength, section.Chunks[0].Text.Length) : 0
                    });
                }
                // One hit per section: its best chunk.
                var own = DocHit.Ranked(candidates).DistinctBy(hit => hit.Anchor).Take(limit).ToArray();
                matches[file] = own.Length;
                hits.AddRange(own);
            }
            return new(DocHit.Ranked(hits).Take(limit).Select(hit => Snippet(highlighter, hit)).ToArray(), matches);
        }
        finally { searchers.Release(searcher); }
    }

    /// <summary>BM25's inverse document frequency of a term over the index's chunks, from its rarer field.</summary>
    private static double Idf(IndexReader reader, string term)
    {
        var frequency = Math.Max(reader.DocFreq(new Term(DocQuery.Body, term)), reader.DocFreq(new Term(DocQuery.Heading, term)));
        return Math.Log(1 + (reader.NumDocs - frequency + 0.5) / (frequency + 0.5));
    }

    /// <summary>The chunks (by index-wide document number) whose <paramref name="field"/> holds <paramref name="term"/>.</summary>
    private static FixedBitSet Holders(IndexReader reader, string field, string term)
    {
        var holders = new FixedBitSet(reader.MaxDoc);
        foreach (var leaf in reader.Leaves)
        {
            if (leaf.AtomicReader.GetTermDocsEnum(new Term(field, term)) is not { } docs) continue;
            for (var doc = docs.NextDoc(); doc != DocIdSetIterator.NO_MORE_DOCS; doc = docs.NextDoc()) holders.Set(leaf.DocBase + doc);
        }
        return holders;
    }

    /// <summary>A chunk's indexed body: the first chunk without its heading line, which is indexed as the heading.</summary>
    private static string BodyOf(DocSection section, int part) =>
        part == 0 ? section.Chunks[0].Text[Math.Min(section.HeadingLength, section.Chunks[0].Text.Length)..] : section.Chunks[part].Text;

    /// <summary>One query-relevant, contiguous source span. Never rewrite whitespace or inject match markers into code.</summary>
    private DocHit Snippet(Highlighter highlighter, DocHit hit)
    {
        var body = hit.Snippet;
        if (body.Length == 0) return hit with { ExcerptAnchor = hit.Anchor, ExcerptOffset = 0, SnippetClipped = false };
        string? fragment;
        try { fragment = highlighter.GetBestFragment(analyzer, DocQuery.Body, body); }
        catch (InvalidTokenOffsetsException) { fragment = null; }
        var start = fragment is { Length: > 0 } ? body.IndexOf(fragment, StringComparison.Ordinal) : 0;
        if (start < 0) { start = 0; fragment = null; }
        var length = Math.Min(fragment?.Length ?? RelayLimits.MaxDocSnippetChars / 2, RelayLimits.MaxDocSnippetChars);
        var end = Math.Min(body.Length, start + length);
        // Include complete adjacent lines where they fit, retaining the exact source bytes represented by the string.
        var lineStart = start == 0 ? 0 : body.LastIndexOf('\n', start - 1) + 1;
        if (end - lineStart <= RelayLimits.MaxDocSnippetChars) start = lineStart;
        var lineEnd = body.IndexOf('\n', end);
        if (lineEnd >= 0 && lineEnd + 1 - start <= RelayLimits.MaxDocSnippetChars) end = lineEnd + 1;
        if (start > 0 && start < body.Length && char.IsLowSurrogate(body[start]) && char.IsHighSurrogate(body[start - 1])) start--;
        end = Math.Min(end, start + RelayLimits.MaxDocSnippetChars);
        if (end > start && end < body.Length && char.IsHighSurrogate(body[end - 1]) && char.IsLowSurrogate(body[end])) end--;
        var offset = hit.ExcerptOffset + start;
        return hit with
        {
            Snippet = body[start..end],
            ExcerptAnchor = hit.ExcerptAnchor + (offset > 0 ? DocumentGrain.OffsetSeparator + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""),
            ExcerptOffset = offset,
            SnippetClipped = offset > 0 || end < body.Length || hit.Tokens > tokens.Count(body)
        };
    }

    public void Dispose()
    {
        lock (writes)
            lock (current)
            {
                if (disposed) return;
                disposed = true;
                searchers.Dispose(); writer.Dispose(); directory.Dispose(); analyzer.Dispose();
            }
    }

    /// <summary>
    /// Keeps each included file's best chunks, as Lucene's own top-N would: the higher score, then the earlier document. The file of a
    /// chunk comes from the path's doc values, looked up once per segment and path.
    /// </summary>
    private sealed class PerPathCollector(int perPath, Func<string, bool> include) : ICollector
    {
        // The queue's head is its worst entry: the lowest score, then the later document.
        private static readonly Comparer<(float Score, int Doc)> Worst = Comparer<(float Score, int Doc)>.Create((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : b.Doc.CompareTo(a.Doc));
        private readonly BytesRef bytes = new();
        private Scorer scorer = null!;
        private SortedDocValues? paths;
        private string?[] names = [];
        private bool[] looked = [];
        private int docBase;

        public Dictionary<string, PriorityQueue<int, (float Score, int Doc)>> Best { get; } = new(StringComparer.Ordinal);

        public bool AcceptsDocsOutOfOrder => true;

        public void SetScorer(Scorer scorer) => this.scorer = scorer;

        public void SetNextReader(AtomicReaderContext context)
        {
            docBase = context.DocBase;
            paths = context.AtomicReader.GetSortedDocValues(PathField);
            names = new string?[paths?.ValueCount ?? 0];
            looked = new bool[names.Length];
        }

        public void Collect(int doc)
        {
            if (paths is null) return;
            var ord = paths.GetOrd(doc);
            if (ord < 0) return;
            if (!looked[ord])
            {
                looked[ord] = true;
                paths.LookupOrd(ord, bytes);
                var name = bytes.Utf8ToString();
                if (include(name)) names[ord] = name;
            }
            if (names[ord] is not { } path) return;
            if (!Best.TryGetValue(path, out var best)) Best[path] = best = new(Worst);
            var entry = (scorer.GetScore(), docBase + doc);
            if (best.Count < perPath) best.Enqueue(entry.Item2, entry); else best.EnqueueDequeue(entry.Item2, entry);
        }
    }

    private sealed class Bm25SearcherFactory : SearcherFactory
    {
        public override IndexSearcher NewSearcher(IndexReader reader) => new(reader) { Similarity = new BM25Similarity() };
    }
}

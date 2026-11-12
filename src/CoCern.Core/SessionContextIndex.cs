using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.State;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Highlight;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using static CoCern.Domain.JsonFields;

namespace CoCern.Core;

/// <summary>Disposable derived index, following DocSearchIndex; persisted entries remain the authority.</summary>
public sealed class SessionContextIndex : IDisposable
{
    private readonly RAMDirectory directory = new();
    private readonly Analyzer analyzer = new ApiDocAnalyzer();
    private readonly IndexWriter writer;
    private readonly ITokenCounter tokens;
    private readonly Dictionary<string, long> versions = new(StringComparer.Ordinal);
    private long revision = -1;

    public SessionContextIndex(ITokenCounter tokens)
    {
        this.tokens = tokens;
        writer = new(directory, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer) { Similarity = new BM25Similarity(), OpenMode = OpenMode.CREATE });
    }

    public void Apply(SessionContextState state)
    {
        if (revision == state.Revision) return;
        foreach (var old in versions.Keys.Where(id => !state.Entries.ContainsKey(id)).ToArray()) { writer.DeleteDocuments(new Term("entry", old)); versions.Remove(old); }
        foreach (var item in state.Entries)
        {
            var entry = item.Value!.AsObject();
            var version = Number(entry, "version");
            if (versions.TryGetValue(item.Key, out var indexed) && indexed == version) continue;
            writer.DeleteDocuments(new Term("entry", item.Key));
            var part = 0;
            foreach (var text in Parts(Text(entry, "text"), tokens))
            {
                writer.AddDocument(new Document
                {
                    new StringField("entry", item.Key, Field.Store.YES), new StoredField("part", (part++).ToString(CultureInfo.InvariantCulture)),
                    new TextField(DocQuery.Heading, Text(entry, "title"), Field.Store.NO), new TextField(DocQuery.Body, text, Field.Store.YES)
                });
            }
            versions[item.Key] = version;
        }
        writer.Flush(false, true);
        writer.ForceMergeDeletes();
        revision = state.Revision;
    }

    public JsonObject Search(SessionContextState state, string query, int limit)
    {
        var hits = new JsonArray();
        var result = new JsonObject { ["contextId"] = state.ContextId, ["revision"] = state.Revision, ["encoding"] = tokens.Encoding, ["hits"] = hits };
        if (DocQuery.Parse(analyzer, query) is not { } parsed) return result;
        using var reader = writer.GetReader(true);
        var searcher = new IndexSearcher(reader) { Similarity = new BM25Similarity() };
        var highlighter = new Highlighter(new SimpleHTMLFormatter("[", "]"), new QueryScorer(parsed, DocQuery.Body)) { TextFragmenter = new SimpleFragmenter(320) };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scored in searcher.Search(parsed, Math.Max(1, reader.MaxDoc)).ScoreDocs)
        {
            var doc = searcher.Doc(scored.Doc);
            var id = doc.Get("entry");
            if (!seen.Add(id) || state.Entries[id] is not JsonObject entry) continue;
            var body = doc.Get(DocQuery.Body);
            var snippet = highlighter.GetBestFragment(analyzer, DocQuery.Body, body) ?? body;
            snippet = Prefix(snippet, SessionContextLimits.SnippetTokens, tokens);
            hits.Add(new JsonObject
            {
                ["entryId"] = id, ["version"] = Number(entry, "version"), ["kind"] = Text(entry, "kind"), ["title"] = Text(entry, "title"),
                ["snippet"] = snippet, ["sourceRefs"] = entry["sourceRefs"]?.DeepClone(),
                ["generation"] = state.Generation,
                ["uri"] = SessionContextResourceUri.PartUri(state.RepositoryId, state.ContextId, id, Number(entry, "version"), int.Parse(doc.Get("part"), CultureInfo.InvariantCulture), state.Generation)
            });
            if (!Fits(result, SessionContextLimits.ReplyTokens, tokens)) { hits.RemoveAt(hits.Count - 1); result["truncated"] = true; break; }
            if (hits.Count >= limit) break;
        }
        return result;
    }

    public static IEnumerable<string> Parts(string text, ITokenCounter tokens)
    {
        while (text.Length > 0)
        {
            var part = Prefix(text, SessionContextLimits.PartTokens, tokens);
            // Escaped JSON and control-heavy text must also fit the final transport, not just the raw text budget.
            while (part.Length > 0 && (Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(part)) > 3072
                || tokens.Count(System.Text.Json.JsonSerializer.Serialize(part)) > 800))
            {
                var length = part.Length / 2;
                if (length > 0 && char.IsHighSurrogate(part[length - 1]) && char.IsLowSurrogate(part[length])) length--;
                part = part[..length];
            }
            if (part.Length == 0) throw new InvalidOperationException("Cannot form a context part.");
            yield return part;
            text = text[part.Length..];
        }
    }

    public static string Prefix(string text, int budget, ITokenCounter tokens)
    {
        var length = Math.Clamp(tokens.PrefixLength(text, budget), 0, text.Length);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length];
    }

    public static bool Fits(JsonObject data, int budget, ITokenCounter tokens)
    {
        var json = data.ToJsonString();
        return Encoding.UTF8.GetByteCount(json) <= SessionContextLimits.ReplyBytes && tokens.Count(json) <= budget;
    }

    public static bool FitsResource(JsonObject data, ITokenCounter tokens)
    {
        var text = SessionContextResourceReply.Markdown(data.ToJsonString());
        return Encoding.UTF8.GetByteCount(text) <= SessionContextLimits.ReplyBytes && tokens.Count(text) <= SessionContextLimits.ReplyTokens;
    }

    public void Dispose() { writer.Dispose(); analyzer.Dispose(); directory.Dispose(); }
}

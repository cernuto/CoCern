using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Util;
using PkRelay.Contracts;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace PkRelay.Core;

/// <summary>One section before chunking: its anchor, heading path and exact text.</summary>
/// <param name="Anchor">The jump-to key, unique within the file.</param>
/// <param name="HeadingPath">Such as <c>Tasks &gt; Claim &gt; Errors</c>.</param>
/// <param name="Text">The section text that <c>doc_section</c> returns.</param>
/// <param name="HeadingLength">Characters at the start of <paramref name="Text"/> that are the heading line, indexed as the heading only.</param>
public sealed record DocSectionText(string Anchor, string HeadingPath, string Text, int HeadingLength);

/// <summary>A section split into chunks of at most <see cref="RelayLimits.DocChunkTokens"/> tokens; the chunks put together are exactly the section text.</summary>
public sealed record DocSection(string Anchor, string HeadingPath, string Text, int Tokens, IReadOnlyList<DocChunk> Chunks, int HeadingLength);

public sealed record DocChunk(string Text, int Tokens);

public enum DocFormat { Unsupported, Markdown, Text, OpenApiJson, OpenApiYaml }

/// <summary>Why a synced file was skipped (and counted) rather than indexed.</summary>
public enum DocSkipReason { None, Unsupported, TooLarge, TooManySections, NotOpenApi, Malformed }

/// <summary>Picks a splitter by file type: markdown headings, text paragraphs, or OpenAPI operations and schemas (JSON or YAML).</summary>
public static class DocFormats
{
    /// <summary>The default include patterns, all mapped to <see cref="DefaultSpec"/>.</summary>
    public static readonly IReadOnlyList<string> DefaultPatterns = ["**/*.md", "**/*.txt", "**/*.json", "**/*.yaml", "**/*.yml"];
    /// <summary>Always excluded, before any configured exclude.</summary>
    public static readonly IReadOnlyList<string> DefaultExcludes = ["**/node_modules/**", "**/bin/**", "**/obj/**", "**/.git/**"];
    public const string DefaultSpec = "docs";

    public static DocFormat Of(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => DocFormat.Markdown,
        ".txt" => DocFormat.Text,
        ".json" => DocFormat.OpenApiJson,
        ".yaml" or ".yml" => DocFormat.OpenApiYaml,
        _ => DocFormat.Unsupported
    };

    public static int MaxBytes(DocFormat format) => format is DocFormat.OpenApiJson or DocFormat.OpenApiYaml ? RelayLimits.MaxOpenApiBytes : RelayLimits.MaxDocBytes;

    /// <summary>The file's sections with chunks and token counts, or null with the reason it is skipped.</summary>
    public static IReadOnlyList<DocSection>? Build(string path, string content, ITokenCounter tokens, out DocSkipReason reason)
    {
        var format = Of(path);
        reason = DocSkipReason.None;
        if (format == DocFormat.Unsupported) { reason = DocSkipReason.Unsupported; return null; }
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes(format)) { reason = DocSkipReason.TooLarge; return null; }
        IReadOnlyList<DocSectionText> split;
        switch (format)
        {
            case DocFormat.Markdown: split = MarkdownSections.Split(content); break;
            case DocFormat.Text: split = TextSections.Split(Path.GetFileName(path), content, tokens); break;
            default:
                var root = format == DocFormat.OpenApiJson ? OpenApiSections.ParseJson(content) : OpenApiSections.ParseYaml(content);
                if (root is null) { reason = DocSkipReason.Malformed; return null; }
                if (OpenApiSections.Split(root) is not { } operations) { reason = DocSkipReason.NotOpenApi; return null; }
                split = operations; break;
        }
        if (split.Count > RelayLimits.MaxSectionsPerDoc) { reason = DocSkipReason.TooManySections; return null; }
        return split.Select(section => new DocSection(section.Anchor, section.HeadingPath, section.Text, tokens.Count(section.Text), DocChunks.Chunk(section.Text, tokens), section.HeadingLength)).ToArray();
    }

    /// <summary>A GitHub-style slug: lower case, letters, digits, <c>-</c> and <c>_</c>, spaces as <c>-</c>.</summary>
    public static string Slug(string title)
    {
        var slug = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') slug.Append(c);
            else if (c is ' ' or '/' or '.') slug.Append('-');
            if (slug.Length >= RelayLimits.MaxDocAnchorLength - 8) break;
        }
        var text = slug.ToString().Trim('-');
        return text.Length == 0 ? "section" : text;
    }

    /// <summary><paramref name="slug"/>, or with <c>-1</c>, <c>-2</c> appended until it is unused.</summary>
    public static string Unique(string slug, HashSet<string> used)
    {
        var candidate = slug;
        for (var n = 1; !used.Add(candidate); n++) candidate = slug + "-" + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }
}

/// <summary>Splits markdown into sections at ATX headings (<c>#</c> to <c>######</c>) outside fenced code blocks, so code blocks stay whole.</summary>
public static class MarkdownSections
{
    public const string TopAnchor = "top";

    public static IReadOnlyList<DocSectionText> Split(string markdown)
    {
        var sections = new List<DocSectionText>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<(int Level, string Title)>();
        var start = 0;
        var headingLength = 0;
        string? anchor = null;
        var path = "";
        string? fence = null;
        void Close(int end)
        {
            var text = markdown[start..end];
            if (anchor is null && string.IsNullOrWhiteSpace(text)) return;
            sections.Add(new(anchor ?? DocFormats.Unique(TopAnchor, used), path, text, headingLength));
        }
        foreach (var (position, line) in DocChunks.Lines(markdown))
        {
            var content = line.TrimEnd('\n', '\r');
            var trimmed = content.TrimStart(' ');
            var indent = content.Length - trimmed.Length;
            if (indent <= 3 && DocChunks.FenceOf(trimmed) is { } marker)
            {
                if (fence is null) fence = marker;
                else if (marker[0] == fence[0] && marker.Length >= fence.Length && trimmed.TrimEnd().Length == marker.Length) fence = null;
            }
            else if (fence is null && indent <= 3 && Heading(trimmed) is { } heading)
            {
                if (position > 0 || anchor is not null) Close(position);
                while (stack.Count > 0 && stack[^1].Level >= heading.Level) stack.RemoveAt(stack.Count - 1);
                stack.Add(heading);
                path = string.Join(" > ", stack.Select(item => item.Title));
                anchor = DocFormats.Unique(DocFormats.Slug(heading.Title), used);
                start = position;
                headingLength = line.Length;
            }
        }
        Close(markdown.Length);
        return sections;
    }

    private static (int Level, string Title)? Heading(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#') level++;
        if (level is 0 or > 6 || level < trimmed.Length && trimmed[level] is not (' ' or '\t')) return null;
        var title = trimmed[level..].Trim();
        // An optional closing sequence of #s is not part of the title.
        var closing = title.TrimEnd('#');
        if (closing.Length < title.Length && (closing.Length == 0 || closing[^1] is ' ' or '\t')) title = closing.TrimEnd();
        return (level, title);
    }
}

/// <summary>Splits plain text at blank lines and merges the paragraphs into chunk-sized sections <c>p1</c>, <c>p2</c>, ...</summary>
public static class TextSections
{
    public static IReadOnlyList<DocSectionText> Split(string fileName, string text, ITokenCounter tokens)
    {
        var sections = new List<DocSectionText>();
        if (string.IsNullOrWhiteSpace(text)) return sections;
        foreach (var chunk in DocChunks.Chunk(text, tokens, fences: false))
        {
            var first = chunk.Text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
            if (first.Length > RelayLimits.MaxTextHeadingChars) first = first[..RelayLimits.MaxTextHeadingChars].TrimEnd() + "…";
            sections.Add(new("p" + (sections.Count + 1).ToString(CultureInfo.InvariantCulture), fileName + " > " + first, chunk.Text, 0));
        }
        return sections;
    }
}

/// <summary>
/// Splits an OpenAPI (or Swagger) document into one section per operation and one per schema. A <c>$ref</c> is shown by the name it
/// points to, one level deep, never expanded. JSON and YAML are read into the same JSON nodes first, so both give the same sections.
/// </summary>
public static class OpenApiSections
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];
    private static readonly Regex OperationId = new("^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

    /// <summary>The JSON, or null when it is malformed or nested past <see cref="RelayLimits.MaxOpenApiDepth"/>.</summary>
    public static JsonNode? ParseJson(string json)
    {
        try { return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = RelayLimits.MaxOpenApiDepth }); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The first YAML document as JSON nodes, read event by event: mappings become objects, sequences arrays, and plain <c>true</c>,
    /// <c>false</c> and <c>null</c> scalars their JSON values; every other scalar is a string. No type is ever instantiated from the YAML.
    /// Null when the YAML is malformed, nested past <see cref="RelayLimits.MaxOpenApiDepth"/>, has a non-scalar or repeated key, or holds
    /// more than <see cref="RelayLimits.MaxOpenApiNodes"/> nodes with its aliases expanded (which refuses alias bombs).
    /// </summary>
    public static JsonNode? ParseYaml(string yaml)
    {
        try
        {
            var reader = new YamlReader(new Parser(new StringReader(yaml)));
            return reader.Read();
        }
        catch (YamlException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The sections, or null when the document is not OpenAPI (no top-level <c>openapi</c> or <c>swagger</c>).</summary>
    public static IReadOnlyList<DocSectionText>? Split(JsonNode root)
    {
        if (root is not JsonObject document || !document.ContainsKey("openapi") && !document.ContainsKey("swagger")) return null;
        var sections = new List<DocSectionText>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (document["paths"] is JsonObject paths)
            foreach (var (route, value) in paths)
            {
                if (value is not JsonObject item) continue;
                foreach (var method in Methods)
                {
                    if (item[method] is not JsonObject operation) continue;
                    var tag = operation["tags"] is JsonArray { Count: > 0 } tags && Scalar(tags[0]) is { Length: > 0 } first ? first : "untagged";
                    var title = method.ToUpperInvariant() + " " + route;
                    var id = Scalar(operation["operationId"]);
                    var anchor = DocFormats.Unique(id is not null && OperationId.IsMatch(id) ? id : DocFormats.Slug(method + "-" + route), used);
                    var head = title + "\n";
                    sections.Add(new(anchor, tag + " > " + title, head + Operation(operation, item["parameters"]), head.Length));
                }
            }
        var schemas = document["components"]?["schemas"] as JsonObject ?? document["definitions"] as JsonObject;
        if (schemas is not null)
            foreach (var (name, value) in schemas)
            {
                var head = name + "\n";
                sections.Add(new(DocFormats.Unique(DocFormats.Slug("schema-" + name), used), "Schemas > " + name, head + Schema(value), head.Length));
            }
        return sections;
    }

    private static string Operation(JsonObject operation, JsonNode? shared)
    {
        var text = new StringBuilder();
        if (Scalar(operation["summary"]) is { Length: > 0 } summary) text.Append("Summary: ").Append(summary).Append('\n');
        if (Scalar(operation["description"]) is { Length: > 0 } description) text.Append(description.TrimEnd()).Append('\n');
        var parameters = new List<JsonNode?>();
        if (shared is JsonArray common) parameters.AddRange(common);
        if (operation["parameters"] is JsonArray own) parameters.AddRange(own);
        var body = new List<string>();
        if (parameters.Count > 0)
        {
            text.Append("Parameters:\n");
            foreach (var parameter in parameters.OfType<JsonObject>())
            {
                if (RefName(parameter) is { } reference) { text.Append("- ").Append(reference).Append(" (ref)\n"); continue; }
                var name = Scalar(parameter["name"]) ?? "?";
                var location = Scalar(parameter["in"]) ?? "?";
                // A Swagger 2 body parameter carries the request body's schema.
                if (location == "body") { body.Add(TypeName(parameter["schema"])); continue; }
                text.Append("- ").Append(name).Append(" (").Append(location).Append(Flag(parameter["required"]) ? ", required" : "").Append("): ")
                    .Append(parameter["schema"] is { } schema ? TypeName(schema) : Scalar(parameter["type"]) ?? "?").Append('\n');
            }
        }
        if (operation["requestBody"] is JsonObject request)
        {
            if (RefName(request) is { } reference) body.Add(reference);
            else if (request["content"] is JsonObject content)
                foreach (var (type, media) in content) body.Add(type + (media?["schema"] is { } schema ? " " + TypeName(schema) : ""));
        }
        if (body.Count > 0) text.Append("Request body: ").Append(string.Join(", ", body)).Append('\n');
        if (operation["responses"] is JsonObject responses && responses.Count > 0)
        {
            text.Append("Responses:\n");
            foreach (var (code, value) in responses)
            {
                text.Append("- ").Append(code);
                if (value is JsonObject response)
                {
                    if (RefName(response) is { } reference) text.Append(": ").Append(reference);
                    else
                    {
                        if (Scalar(response["description"]) is { Length: > 0 } said) text.Append(": ").Append(said.Trim());
                        var schemas = response["content"] is JsonObject content
                            ? content.Select(media => media.Value?["schema"]).Where(schema => schema is not null).Select(TypeName).Distinct().ToArray()
                            : response["schema"] is { } schema ? [TypeName(schema)] : [];
                        if (schemas.Length > 0) text.Append(" (").Append(string.Join(", ", schemas)).Append(')');
                    }
                }
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    private static string Schema(JsonNode? schema)
    {
        var text = new StringBuilder();
        if (schema is not JsonObject value) return text.ToString();
        if (RefName(value) is { } reference) return "Same as " + reference + "\n";
        if (Scalar(value["description"]) is { Length: > 0 } description) text.Append(description.TrimEnd()).Append('\n');
        if (value["properties"] is null && TypeName(value) is var type and not "object") text.Append("Type: ").Append(type).Append('\n');
        var required = value["required"] is JsonArray names ? names.Select(Scalar).Where(name => name is not null).ToHashSet(StringComparer.Ordinal) : [];
        if (value["properties"] is JsonObject properties && properties.Count > 0)
        {
            text.Append("Properties:\n");
            foreach (var (name, property) in properties)
                text.Append("- ").Append(name).Append(": ").Append(TypeName(property)).Append(required.Contains(name) ? " (required)" : "").Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A schema's type in a few words: a referenced name, <c>array of X</c>, a type and format, or a composition keyword.</summary>
    private static string TypeName(JsonNode? schema)
    {
        if (schema is not JsonObject value) return "?";
        if (RefName(value) is { } reference) return reference;
        var type = Scalar(value["type"]);
        if (type == "array") return "array of " + (value["items"] is JsonObject items ? RefName(items) ?? Scalar(items["type"]) ?? "?" : "?");
        if (type is not null) return Scalar(value["format"]) is { Length: > 0 } format ? type + " (" + format + ")" : type;
        foreach (var keyword in new[] { "oneOf", "anyOf", "allOf" })
            if (value[keyword] is JsonArray options)
                return keyword + " " + string.Join("|", options.Select(option => option is JsonObject item ? RefName(item) ?? Scalar(item["type"]) ?? "?" : "?"));
        return "object";
    }

    private static string? RefName(JsonObject value) => Scalar(value["$ref"]) is { Length: > 0 } reference ? reference[(reference.LastIndexOf('/') + 1)..] : null;

    private static bool Flag(JsonNode? node) => node is JsonValue value && (value.TryGetValue<bool>(out var flag) ? flag : value.TryGetValue<string>(out var text) && text == "true");

    private static string? Scalar(JsonNode? node) => node is JsonValue value ? value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
        _ => null
    } : null;

    /// <summary>Builds JSON nodes from YAML parser events, with depth and node limits; an alias copies its anchor's node and counts its nodes again.</summary>
    private sealed class YamlReader(IParser parser)
    {
        private readonly Dictionary<string, (JsonNode? Node, int Count)> anchors = new(StringComparer.Ordinal);
        private int nodes;

        public JsonNode? Read()
        {
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _)) return null;
            parser.Consume<DocumentStart>();
            if (parser.Accept<DocumentEnd>(out _)) return null;
            var (root, _) = Node(0);
            return root;
        }

        private (JsonNode? Node, int Count) Node(int depth)
        {
            if (depth > RelayLimits.MaxOpenApiDepth) throw new InvalidDataException("YAML is nested too deeply.");
            if (parser.TryConsume<AnchorAlias>(out var alias))
            {
                if (!anchors.TryGetValue(alias.Value.Value, out var anchored)) throw new InvalidDataException("Unknown YAML alias.");
                Count(anchored.Count);
                return (anchored.Node?.DeepClone(), anchored.Count);
            }
            var start = nodes;
            JsonNode? node;
            string anchor;
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                anchor = scalar.Anchor.IsEmpty ? "" : scalar.Anchor.Value;
                Count(1);
                node = scalar.Style == ScalarStyle.Plain ? scalar.Value switch
                {
                    "true" or "True" or "TRUE" => JsonValue.Create(true),
                    "false" or "False" or "FALSE" => JsonValue.Create(false),
                    "null" or "Null" or "NULL" or "~" or "" => null,
                    _ => JsonValue.Create(scalar.Value)
                } : JsonValue.Create(scalar.Value);
            }
            else if (parser.TryConsume<SequenceStart>(out var sequence))
            {
                anchor = sequence.Anchor.IsEmpty ? "" : sequence.Anchor.Value;
                Count(1);
                var array = new JsonArray();
                while (!parser.TryConsume<SequenceEnd>(out _)) array.Add(Node(depth + 1).Node);
                node = array;
            }
            else if (parser.TryConsume<MappingStart>(out var mapping))
            {
                anchor = mapping.Anchor.IsEmpty ? "" : mapping.Anchor.Value;
                Count(1);
                var map = new JsonObject();
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    if (!parser.TryConsume<Scalar>(out var key)) throw new InvalidDataException("A YAML key is not a scalar.");
                    Count(1);
                    var (value, _) = Node(depth + 1);
                    if (!map.TryAdd(key.Value, value)) throw new InvalidDataException("A YAML key repeats.");
                }
                node = map;
            }
            else throw new InvalidDataException("Unexpected YAML event.");
            var count = nodes - start;
            if (anchor.Length > 0) anchors[anchor] = (node, count);
            return (node, count);
        }

        private void Count(int added)
        {
            nodes += added;
            if (nodes > RelayLimits.MaxOpenApiNodes) throw new InvalidDataException("YAML has too many nodes once its aliases are expanded.");
        }
    }
}

/// <summary>Paragraph-boundary chunking shared by every format.</summary>
public static class DocChunks
{
    /// <summary>
    /// Splits text at paragraph boundaries (blank lines, outside code fences when <paramref name="fences"/>) into chunks within the chunk
    /// token and character limits. A paragraph or code block too long on its own is split at line ends, and a line too long by length.
    /// The chunks put together are exactly the text.
    /// </summary>
    public static IReadOnlyList<DocChunk> Chunk(string text, ITokenCounter tokens, bool fences = true)
    {
        var chunks = new List<DocChunk>();
        var current = new StringBuilder();
        var estimate = 0;
        foreach (var (piece, count) in Blocks(text, fences).SelectMany(block => Fit(block, tokens)))
        {
            if (current.Length > 0 && (estimate + count > RelayLimits.DocChunkTokens || current.Length + piece.Length > RelayLimits.DocChunkChars))
            {
                chunks.Add(Measured(current.ToString(), tokens));
                current.Clear(); estimate = 0;
            }
            current.Append(piece); estimate += count;
        }
        if (current.Length > 0 || chunks.Count == 0) chunks.Add(Measured(current.ToString(), tokens));
        return chunks;
    }

    private static DocChunk Measured(string text, ITokenCounter tokens) => new(text, tokens.Count(text));

    /// <summary>A block as pieces that each fit a chunk: whole, or by lines, or by length.</summary>
    private static IEnumerable<(string Text, int Tokens)> Fit(string block, ITokenCounter tokens)
    {
        var count = tokens.Count(block);
        if (count <= RelayLimits.DocChunkTokens && block.Length <= RelayLimits.DocChunkChars) { yield return (block, count); yield break; }
        foreach (var (_, line) in Lines(block))
        {
            var rest = line;
            while (rest.Length > 0)
            {
                var length = Math.Min(rest.Length, RelayLimits.DocChunkChars);
                length = Math.Max(1, Math.Min(length, tokens.PrefixLength(rest[..length], RelayLimits.DocChunkTokens)));
                // Never cut a surrogate pair in two.
                if (length < rest.Length && length > 1 && char.IsHighSurrogate(rest[length - 1])) length--;
                var piece = rest[..length];
                yield return (piece, tokens.Count(piece));
                rest = rest[length..];
            }
        }
    }

    /// <summary>Paragraphs, each with the blank lines after it; with <paramref name="fences"/>, a fenced code block is never split at its blank lines.</summary>
    private static IEnumerable<string> Blocks(string text, bool fences)
    {
        var start = 0;
        string? fence = null;
        var blank = false;
        foreach (var (position, line) in Lines(text))
        {
            var trimmed = line.TrimStart(' ').TrimEnd('\n', '\r');
            var isBlank = trimmed.Trim().Length == 0;
            if (fence is null && blank && !isBlank && position > start)
            {
                yield return text[start..position];
                start = position;
            }
            if (fences && FenceOf(trimmed) is { } marker)
            {
                if (fence is null) fence = marker;
                else if (marker[0] == fence[0] && marker.Length >= fence.Length) fence = null;
            }
            blank = fence is null && isBlank;
        }
        if (text.Length > start) yield return text[start..];
    }

    /// <summary>Each line with its line ending, and where it starts.</summary>
    internal static IEnumerable<(int Position, string Line)> Lines(string text)
    {
        for (var position = 0; position < text.Length;)
        {
            var newline = text.IndexOf('\n', position);
            var end = newline < 0 ? text.Length : newline + 1;
            yield return (position, text[position..end]);
            position = end;
        }
    }

    internal static string? FenceOf(string trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~')) return null;
        var length = 0;
        while (length < trimmed.Length && trimmed[length] == trimmed[0]) length++;
        return length >= 3 ? trimmed[..length] : null;
    }
}

/// <summary>Content hashes that decide whether a document changed.</summary>
public static class DocHash
{
    public static string Of(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..32];
}

/// <summary>
/// Indexes API docs: splits <c>taskClaim</c>, <c>task_claim</c> and <c>/v1/tasks/{id}</c> into words, lower-cases and stems words, and keeps
/// upper-case HTTP verbs (<c>GET</c>, <c>POST</c>, ...) and status codes (<c>404</c>) as exact terms.
/// </summary>
public sealed class ApiDocAnalyzer : Analyzer
{
    private static readonly HashSet<string> Verbs = new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"], StringComparer.Ordinal);

    private const WordDelimiterFlags Delimiting = WordDelimiterFlags.GENERATE_WORD_PARTS | WordDelimiterFlags.GENERATE_NUMBER_PARTS
        | WordDelimiterFlags.SPLIT_ON_CASE_CHANGE | WordDelimiterFlags.STEM_ENGLISH_POSSESSIVE;

    protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
    {
        var source = new WhitespaceTokenizer(LuceneVersion.LUCENE_48, reader);
        TokenStream stream = new WordDelimiterFilter(LuceneVersion.LUCENE_48, source, Delimiting, null);
        stream = new ApiTermFilter(stream);
        stream = new PorterStemFilter(stream);
        return new TokenStreamComponents(source, stream);
    }

    /// <summary>Whether <paramref name="term"/> is kept exactly: an upper-case HTTP verb or a three-digit status code.</summary>
    public static bool IsExactTerm(string term) =>
        Verbs.Contains(term) || term.Length == 3 && term[0] is >= '1' and <= '5' && char.IsAsciiDigit(term[1]) && char.IsAsciiDigit(term[2]);

    private sealed class ApiTermFilter : TokenFilter
    {
        private readonly ICharTermAttribute term;
        private readonly IKeywordAttribute keyword;

        public ApiTermFilter(TokenStream input) : base(input)
        {
            term = AddAttribute<ICharTermAttribute>();
            keyword = AddAttribute<IKeywordAttribute>();
        }

        public override bool IncrementToken()
        {
            if (!m_input.IncrementToken()) return false;
            if (IsExactTerm(term.ToString())) { keyword.IsKeyword = true; return true; }
            var buffer = term.Buffer;
            for (var i = 0; i < term.Length; i++) buffer[i] = char.ToLowerInvariant(buffer[i]);
            return true;
        }
    }
}

/// <summary>Turns agent text into a Lucene query that never uses query syntax.</summary>
public static class DocQuery
{
    public const string Heading = "heading";
    public const string Body = "body";
    /// <summary>A heading match counts three times a body match, so a title match outranks a passing mention.</summary>
    public const float HeadingBoost = 3f;

    /// <summary>
    /// The query over <see cref="Heading"/> and <see cref="Body"/>, or null when the text has no searchable words. Every special character
    /// is escaped and the operator words AND, OR and NOT are read as words, so the text is never a parse error.
    /// </summary>
    public static Query? Parse(Analyzer analyzer, string text)
    {
        var words = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => word is "AND" or "OR" or "NOT" ? word.ToLowerInvariant() : word));
        if (words.Length == 0) return null;
        var parser = new MultiFieldQueryParser(LuceneVersion.LUCENE_48, [Heading, Body], analyzer, new Dictionary<string, float> { [Heading] = HeadingBoost, [Body] = 1f });
        try
        {
            var query = parser.Parse(QueryParserBase.Escape(words));
            return query is BooleanQuery { Clauses.Count: 0 } ? null : query;
        }
        catch (ParseException) { return null; }
    }

    /// <summary>The analyzed terms of <paramref name="text"/>, to tell whether a heading matched the query.</summary>
    public static HashSet<string> Terms(Analyzer analyzer, string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        using var stream = analyzer.GetTokenStream(Heading, text);
        var term = stream.AddAttribute<ICharTermAttribute>();
        stream.Reset();
        while (stream.IncrementToken()) terms.Add(term.ToString());
        stream.End();
        return terms;
    }
}

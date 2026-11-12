using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Orleans;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>The doc cache tools as the hub calls them; a hub with the cache off gets <see cref="DocCacheTool.Off"/>.</summary>
public interface IDocCacheTools
{
    Task<string?> ResolveNativeAsync(RelayCaller caller, string? checkout, string? common, string? origin, string? repositoryId = null) => Task.FromResult<string?>(null);
    Task<RelayReply> SearchAsync(JsonObject args);
    /// <summary><c>doc_search</c> without a query: the repositories with docs that <paramref name="caller"/> may read.</summary>
    Task<RelayReply> ListAsync(RelayCaller caller);
    Task<RelayReply> SectionAsync(JsonObject args);
    Task<RelayReply> SyncAsync(JsonObject args, CancellationToken cancellationToken);

    /// <summary>Advisory, after a session opens: see <see cref="IDocSync.LearnAsync"/>. A hub with the cache off learns nothing.</summary>
    Task<string?> LearnAsync(string repositoryId, string workspace) => Task.FromResult<string?>(null);
}

/// <summary>
/// The doc cache tools: <c>doc_search</c> and <c>doc_section</c> read a repository's synced docs through its directory grain, and
/// <c>doc_sync</c> runs the sync now (the caller is already checked to be an administrator). The repository scope was checked at the
/// boundary. Replies fit <see cref="RelayLimits.MaxDocReplyTokens"/> tokens and <see cref="RelayLimits.MaxDocReplyBytes"/> bytes; what does
/// not fit is reported as a count. <paramref name="repositories"/> is read on every call, so a live view sees repositories found later.
/// </summary>
public sealed class DocCacheTool(IGrainFactory grains, ITokenCounter tokens, IDocSync sync, IReadOnlyCollection<string> repositories) : IDocCacheTools
{
    private const int CommitLength = 12;

    private bool Known(string repository) => repositories.Contains(repository, StringComparer.Ordinal);

    public Task<string?> ResolveNativeAsync(RelayCaller caller, string? checkout, string? common, string? origin, string? repositoryId = null) =>
        sync.ResolveNativeAsync(caller, checkout, common, origin, repositoryId);

    public Task<string?> LearnAsync(string repositoryId, string workspace) => sync.LearnAsync(repositoryId, workspace);

    /// <summary>How long one search waits for its documents; tests shorten it.</summary>
    internal TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(RelayLimits.DocSearchTimeoutSeconds);

    public Task<RelayReply> ListAsync(RelayCaller caller) =>
        Task.FromResult(Ok(new { repositories = repositories.Where(caller.MayAccess).Order(StringComparer.OrdinalIgnoreCase).ToArray() }));

    public async Task<RelayReply> SearchAsync(JsonObject args)
    {
        var repository = Text(args, "repositoryId");
        if (!Known(repository)) return NotConfigured(repositories.Count);
        var limit = args["limit"] is null ? RelayLimits.DefaultDocSearchResults : (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultDocSearchResults), 1, RelayLimits.MaxDocSearchResults);
        var outcome = await Directory(repository).SearchAsync(Text(args, "query"), Optional(args, "spec"), Optional(args, "path"), limit, SearchTimeout);
        if (outcome.PathUnknown) return Error(NotFound, new { field = "path" });
        var items = new JsonArray();
        var reply = new JsonObject { ["repository"] = repository, ["hits"] = items };
        if (outcome.NotSearched > 0) { reply["partial"] = true; reply["notSearched"] = outcome.NotSearched; }
        for (var index = 0; index < outcome.Hits.Length; index++)
        {
            var hit = outcome.Hits[index];
            items.Add(new JsonObject
            {
                ["path"] = hit.Path, ["anchor"] = hit.Anchor, ["heading"] = hit.Heading, ["snippet"] = hit.Snippet,
                ["score"] = Math.Round(hit.Score, 2), ["tokens"] = hit.Tokens, ["commit"] = Short(hit.Commit),
                ["sourceCommit"] = hit.Commit, ["excerptAnchor"] = hit.ExcerptAnchor ?? hit.Anchor, ["snippetClipped"] = hit.SnippetClipped
            });
            if (Fits(reply)) continue;
            items.RemoveAt(items.Count - 1);
            reply["more"] = outcome.Hits.Length - index;
            break;
        }
        return Ok(reply);
    }

    /// <summary>
    /// One section from its start, or from a chunk anchor such as <c>errors~3</c> (or <c>errors~3+1200</c>, a point inside a chunk). A
    /// section over the budget returns the whole chunks that fit, with <c>next</c> (the anchor to continue from) and <c>moreTokens</c>
    /// (what the rest costs); a first chunk that alone is over the budget is cut where it fits, and <c>next</c> points inside it.
    /// </summary>
    public async Task<RelayReply> SectionAsync(JsonObject args)
    {
        var repository = Text(args, "repositoryId");
        if (!Known(repository)) return NotConfigured(repositories.Count);
        var sourceLimit = RelayLimits.MaxDocReplyTokens;
        if (args.ContainsKey("maxTokens") && (args["maxTokens"] is not JsonValue budget || !budget.TryGetValue<int>(out sourceLimit)
            || sourceLimit < 64 || sourceLimit > RelayLimits.MaxDocReplyTokens)) return Error(InvalidArguments, new { field = "maxTokens" });
        string? expectedCommit = null;
        if (args.ContainsKey("expectedCommit") && (args["expectedCommit"] is not JsonValue pin || !pin.TryGetValue<string>(out expectedCommit)
            || expectedCommit is not { Length: >= 1 and <= 256 } || expectedCommit.Any(char.IsControl))) return Error(InvalidArguments, new { field = "expectedCommit" });
        if (await Directory(repository).SectionAsync(Text(args, "path"), Text(args, "anchor")) is not { } section) return Error(NotFound, new { field = "anchor" });
        if (expectedCommit is not null && !string.Equals(expectedCommit, section.Commit, StringComparison.Ordinal))
            return Error(RelayErrorCodes.VersionConflict, new { field = "expectedCommit", sourceCommit = section.Commit, readCurrentState = true });
        var focused = args.ContainsKey("maxTokens") || expectedCommit is not null;
        JsonObject Page(string text, int nextChunk = 0, int nextOffset = 0, int moreTokens = 0)
        {
            var page = new JsonObject
            {
                ["path"] = section.Path, ["anchor"] = section.Anchor, ["heading"] = section.Heading, ["commit"] = Short(section.Commit),
                ["sourceCommit"] = section.Commit, ["tokens"] = section.Tokens, ["text"] = text
            };
            if (focused)
            {
                page["textTokens"] = tokens.Count(text);
                page["clipped"] = section.FirstChunk > 1 || section.FirstOffset > 0 || nextChunk > 0;
            }
            if (nextChunk > 0)
            {
                var next = Anchor(section.Anchor, nextChunk, nextOffset);
                page["next"] = next;
                page["moreTokens"] = moreTokens;
                if (focused) page["nextRead"] = new JsonObject
                {
                    ["repository"] = repository, ["path"] = section.Path, ["anchor"] = next, ["expectedCommit"] = section.Commit, ["maxTokens"] = sourceLimit
                };
            }
            return page;
        }
        var text = new StringBuilder();
        var taken = 0;
        JsonObject? reply = null;
        while (taken < section.Chunks.Length)
        {
            var candidate = text.ToString() + section.Chunks[taken];
            var more = taken + 1 < section.Chunks.Length;
            var page = Page(candidate, more ? section.FirstChunk + taken + 1 : 0, 0, more ? section.ChunkTokens.Skip(taken + 1).Sum() : 0);
            if (tokens.Count(candidate) > sourceLimit || !Fits(page)) break;
            text.Append(section.Chunks[taken]);
            reply = page;
            taken++;
        }
        if (taken == 0 && section.Chunks.Length > 0)
        {
            // Cut the exact source only at a character boundary. Complete continuation metadata participates in every budget check.
            var first = section.Chunks[0];
            JsonObject Prefix(string prefix, int length) => Page(prefix, section.FirstChunk, section.FirstOffset + length,
                tokens.Count(first[length..]) + section.ChunkTokens.Skip(1).Sum());
            var length = LongestFit(first, sourceLimit, Prefix);
            if (length == 0) return Error(PayloadTooLarge, new { field = "sourceMetadata", detailFetchRequired = true });
            reply = Prefix(first[..length], length);
        }
        var result = reply ?? Page("");
        return Fits(result) ? Ok(result) : Error(PayloadTooLarge, new { field = "sourceMetadata", detailFetchRequired = true });
    }

    public async Task<RelayReply> SyncAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var repository = Text(args, "repositoryId");
        if (!Known(repository)) return NotConfigured(repositories.Count);
        try
        {
            var outcome = await sync.SyncNowAsync(repository, Optional(args, "branch"), Text(args, "requestId"), cancellationToken);
            return Ok(new { commit = outcome.Commit, changed = outcome.Changed, skipped = outcome.Skipped, fetched = outcome.Fetched });
        }
        catch (DocSyncException exception)
        {
            return exception.Error switch
            {
                DocSyncError.RateLimited => Error(RateLimited, new { retryAfterSeconds = exception.RetryAfterSeconds }),
                DocSyncError.UnknownBranch => Error(InvalidArguments, new { field = "branch", message = exception.Message }),
                DocSyncError.NotConfigured => NotConfigured(repositories.Count),
                DocSyncError.RequestConflict => Error(RequestConflict, new { message = exception.Message }),
                _ => Error(Unavailable, new { message = exception.Message })
            };
        }
    }

    /// <summary>An exact source prefix fitting both the source-text budget and complete reply, never half a surrogate pair.</summary>
    private int LongestFit(string chunk, int sourceLimit, Func<string, int, JsonObject> page)
    {
        int low = 0, high = chunk.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var prefix = chunk[..middle];
            if (tokens.Count(prefix) <= sourceLimit && Fits(page(prefix, middle))) low = middle; else high = middle - 1;
        }
        // BPE counts are not strictly monotonic: shortening a fitting prefix to keep a surrogate pair intact can increase its count.
        // Recheck the final source and fully composed page instead of assuming the binary-search decision still holds.
        while (low > 0)
        {
            if (char.IsHighSurrogate(chunk[low - 1])) { low--; continue; }
            var prefix = chunk[..low];
            if (tokens.Count(prefix) <= sourceLimit && Fits(page(prefix, low))) return low;
            low--;
        }
        return 0;
    }

    private static string Anchor(string anchor, int chunk, int offset) =>
        anchor + DocumentGrain.ChunkSeparator + chunk.ToString(CultureInfo.InvariantCulture) + (offset > 0 ? DocumentGrain.OffsetSeparator + offset.ToString(CultureInfo.InvariantCulture) : "");

    private IDocDirectoryGrain Directory(string repository) => grains.GetGrain<IDocDirectoryGrain>(repository);

    /// <summary>Whether the reply fits its budgets with room left for the count fields added after packing (<c>more</c>, <c>next</c>, <c>moreTokens</c>).</summary>
    private bool Fits(JsonObject reply)
    {
        var json = reply.ToJsonString();
        return Encoding.UTF8.GetByteCount(json) <= RelayLimits.MaxDocReplyBytes - ReserveBytes && tokens.Count(json) <= RelayLimits.MaxDocReplyTokens - ReserveTokens;
    }

    private const int ReserveBytes = 64 + RelayLimits.MaxDocAnchorLength;
    private const int ReserveTokens = 64;

    private static string? Optional(JsonObject args, string field) => Text(args, field) is { Length: > 0 } value ? value : null;

    private static string Short(string commit) => commit.Length > CommitLength ? commit[..CommitLength] : commit;

    /// <summary>A hub with no repositories configured or found says so with its own code; otherwise this repository is just not one of them.</summary>
    private static RelayReply NotConfigured(int repositories) => repositories == 0
        ? Error(NoRepositoriesConfigured, new { message = "No repositories are configured for the doc cache on this hub (Relay:DocCache:Repositories)." })
        : Error(DocCacheUnavailable, new { message = "No docs are synced for this repository on this hub." });

    /// <summary>Every doc cache tool on a hub with <c>Relay:DocCache:Enabled</c> off.</summary>
    public static readonly IDocCacheTools Off = new TurnedOff();

    /// <summary>Native repository resolution is independent of document indexing; document operations remain disabled.</summary>
    public static IDocCacheTools OffWithNativeResolution(Func<RelayCaller, string?, string?, string?, string?, Task<string?>> resolve) => new TurnedOff(resolve);

    public static bool IsEnabled(IDocCacheTools tools) => tools is not TurnedOff;

    private sealed class TurnedOff(Func<RelayCaller, string?, string?, string?, string?, Task<string?>>? resolve = null) : IDocCacheTools
    {
        public Task<string?> ResolveNativeAsync(RelayCaller caller, string? checkout, string? common, string? origin, string? repositoryId = null) =>
            resolve?.Invoke(caller, checkout, common, origin, repositoryId) ?? Task.FromResult<string?>(null);
        private static Task<RelayReply> Unavailable() =>
            Task.FromResult(Replies.Error(RelayErrorCodes.DocCacheUnavailable, new { message = "The doc cache is turned off on this hub (Relay:DocCache:Enabled)." }));
        public Task<RelayReply> SearchAsync(JsonObject args) => Unavailable();
        public Task<RelayReply> ListAsync(RelayCaller caller) => Unavailable();
        public Task<RelayReply> SectionAsync(JsonObject args) => Unavailable();
        public Task<RelayReply> SyncAsync(JsonObject args, CancellationToken cancellationToken) => Unavailable();
    }
}

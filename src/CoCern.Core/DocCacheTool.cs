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

/// <summary>
/// The doc cache tools: <c>doc_search</c> and <c>doc_section</c> read a repository's synced docs through its directory grain, and
/// <c>doc_sync</c> runs the sync now (the caller is already checked to be an administrator). The repository scope was checked at the
/// boundary. Replies fit <see cref="RelayLimits.MaxDocReplyTokens"/> tokens and <see cref="RelayLimits.MaxDocReplyBytes"/> bytes; what does
/// not fit is reported as a count.
/// </summary>
/// <summary>The doc cache tools as the hub calls them; a hub with the cache off gets <see cref="DocCacheTool.Off"/>.</summary>
public interface IDocCacheTools
{
    Task<RelayReply> SearchAsync(JsonObject args);
    Task<RelayReply> SectionAsync(JsonObject args);
    Task<RelayReply> SyncAsync(JsonObject args, CancellationToken cancellationToken);
}

public sealed class DocCacheTool(IGrainFactory grains, ITokenCounter tokens, IDocSync sync, IReadOnlyCollection<string> repositories) : IDocCacheTools
{
    private readonly HashSet<string> configured = new(repositories, StringComparer.Ordinal);
    private const int CommitLength = 12;

    /// <summary>How long one search waits for its documents; tests shorten it.</summary>
    internal TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(RelayLimits.DocSearchTimeoutSeconds);

    public async Task<RelayReply> SearchAsync(JsonObject args)
    {
        var repository = Text(args, "repositoryId");
        if (!configured.Contains(repository)) return NotConfigured(configured.Count);
        var limit = args["limit"] is null ? RelayLimits.DefaultDocSearchResults : (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultDocSearchResults), 1, RelayLimits.MaxDocSearchResults);
        var outcome = await Directory(repository).SearchAsync(Text(args, "query"), Optional(args, "spec"), Optional(args, "path"), limit, SearchTimeout);
        if (outcome.PathUnknown) return Error(NotFound, new { field = "path" });
        var items = new JsonArray();
        var reply = new JsonObject { ["hits"] = items };
        if (outcome.NotSearched > 0) { reply["partial"] = true; reply["notSearched"] = outcome.NotSearched; }
        for (var index = 0; index < outcome.Hits.Length; index++)
        {
            var hit = outcome.Hits[index];
            items.Add(new JsonObject
            {
                ["path"] = hit.Path, ["anchor"] = hit.Anchor, ["heading"] = hit.Heading, ["snippet"] = hit.Snippet,
                ["score"] = Math.Round(hit.Score, 2), ["tokens"] = hit.Tokens, ["commit"] = Short(hit.Commit)
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
        if (!configured.Contains(repository)) return NotConfigured(configured.Count);
        if (await Directory(repository).SectionAsync(Text(args, "path"), Text(args, "anchor")) is not { } section) return Error(NotFound, new { field = "anchor" });
        var reply = new JsonObject
        {
            ["path"] = section.Path, ["anchor"] = section.Anchor, ["heading"] = section.Heading, ["commit"] = Short(section.Commit), ["tokens"] = section.Tokens, ["text"] = ""
        };
        var text = new StringBuilder();
        var taken = 0;
        while (taken < section.Chunks.Length)
        {
            reply["text"] = text.ToString() + section.Chunks[taken];
            if (!Fits(reply)) break;
            text.Append(section.Chunks[taken]);
            taken++;
        }
        reply["text"] = text.ToString();
        if (taken == 0)
        {
            // Even the first chunk is over the budget (very long tokens or heavy JSON escaping): send the longest start that fits.
            var first = section.Chunks[0];
            var length = LongestFit(reply, first);
            reply["text"] = first[..length];
            reply["next"] = Anchor(section.Anchor, section.FirstChunk, section.FirstOffset + length);
            reply["moreTokens"] = tokens.Count(first[length..]) + section.ChunkTokens.Skip(1).Sum();
        }
        else if (taken < section.Chunks.Length)
        {
            reply["next"] = Anchor(section.Anchor, section.FirstChunk + taken, 0);
            reply["moreTokens"] = section.ChunkTokens.Skip(taken).Sum();
        }
        return Ok(reply);
    }

    public async Task<RelayReply> SyncAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var repository = Text(args, "repositoryId");
        if (!configured.Contains(repository)) return NotConfigured(configured.Count);
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
                DocSyncError.NotConfigured => NotConfigured(configured.Count),
                DocSyncError.RequestConflict => Error(RequestConflict, new { message = exception.Message }),
                _ => Error(Unavailable, new { message = exception.Message })
            };
        }
    }

    /// <summary>The longest start of <paramref name="chunk"/> that fits the reply, at least one character and never half a surrogate pair.</summary>
    private int LongestFit(JsonObject reply, string chunk)
    {
        int low = 1, high = chunk.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            reply["text"] = chunk[..middle];
            if (Fits(reply)) low = middle; else high = middle - 1;
        }
        if (low < chunk.Length && low > 1 && char.IsHighSurrogate(chunk[low - 1])) low--;
        return low;
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

    /// <summary>A hub with no repositories configured says so with its own code; otherwise this repository is just not one of them.</summary>
    private static RelayReply NotConfigured(int repositories) => repositories == 0
        ? Error(NoRepositoriesConfigured, new { message = "No repositories are configured for the doc cache on this hub (Relay:DocCache:Repositories)." })
        : Error(DocCacheUnavailable, new { message = "No docs are synced for this repository on this hub." });

    /// <summary>Every doc cache tool on a hub with <c>Relay:DocCache:Enabled</c> off.</summary>
    public static readonly IDocCacheTools Off = new TurnedOff();

    private sealed class TurnedOff : IDocCacheTools
    {
        private static Task<RelayReply> Unavailable() =>
            Task.FromResult(Replies.Error(RelayErrorCodes.DocCacheUnavailable, new { message = "The doc cache is turned off on this hub (Relay:DocCache:Enabled)." }));
        public Task<RelayReply> SearchAsync(JsonObject args) => Unavailable();
        public Task<RelayReply> SectionAsync(JsonObject args) => Unavailable();
        public Task<RelayReply> SyncAsync(JsonObject args, CancellationToken cancellationToken) => Unavailable();
    }
}

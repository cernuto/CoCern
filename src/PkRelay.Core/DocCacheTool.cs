using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Orleans;
using PkRelay.Contracts;
using PkRelay.Domain;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Core;

/// <summary>
/// The doc cache tools: <c>doc_search</c> and <c>doc_section</c> read a repository's synced docs through its directory grain, and
/// <c>doc_sync</c> runs the sync now (the caller is already checked to be an administrator). The repository scope was checked at the
/// boundary. Replies fit <see cref="RelayLimits.MaxDocReplyTokens"/> tokens and <see cref="RelayLimits.MaxDocReplyBytes"/> bytes; what does
/// not fit is reported as a count.
/// </summary>
public sealed class DocCacheTool(IGrainFactory grains, ITokenCounter tokens, IDocSync sync, IReadOnlyCollection<string> repositories)
{
    private readonly HashSet<string> configured = new(repositories, StringComparer.Ordinal);
    private const int CommitLength = 12;

    /// <summary>How long one search waits for its documents; tests shorten it.</summary>
    internal TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(RelayLimits.DocSearchTimeoutSeconds);

    public async Task<RelayReply> SearchAsync(JsonObject args)
    {
        var repository = Text(args, "repositoryId");
        if (!configured.Contains(repository)) return NotConfigured();
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
    /// One section from its start, or from a chunk anchor such as <c>errors~3</c>. A section over the budget returns whole chunks that fit,
    /// with <c>next</c> (the anchor to continue from) and <c>moreTokens</c> (what the rest costs).
    /// </summary>
    public async Task<RelayReply> SectionAsync(JsonObject args)
    {
        var repository = Text(args, "repositoryId");
        if (!configured.Contains(repository)) return NotConfigured();
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
            if (!Fits(reply) && taken > 0) break;
            text.Append(section.Chunks[taken]);
            taken++;
        }
        reply["text"] = text.ToString();
        if (taken < section.Chunks.Length)
        {
            reply["next"] = section.Anchor + DocumentGrain.ChunkSeparator + (section.FirstChunk + taken).ToString(CultureInfo.InvariantCulture);
            reply["moreTokens"] = section.ChunkTokens.Skip(taken).Sum();
        }
        return Ok(reply);
    }

    public async Task<RelayReply> SyncAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var repository = Text(args, "repositoryId");
        if (!configured.Contains(repository)) return NotConfigured();
        try
        {
            var outcome = await sync.SyncNowAsync(repository, Optional(args, "branch"), cancellationToken);
            return Ok(new { commit = outcome.Commit, changed = outcome.Changed, skipped = outcome.Skipped, fetched = outcome.Fetched });
        }
        catch (DocSyncException exception)
        {
            return exception.Error switch
            {
                DocSyncError.RateLimited => Error(RateLimited, new { retryAfterSeconds = exception.RetryAfterSeconds }),
                DocSyncError.UnknownBranch => Error(InvalidArguments, new { field = "branch", message = exception.Message }),
                DocSyncError.NotConfigured => NotConfigured(),
                _ => Error(Unavailable, new { message = exception.Message })
            };
        }
    }

    private IDocDirectoryGrain Directory(string repository) => grains.GetGrain<IDocDirectoryGrain>(repository);

    private bool Fits(JsonObject reply)
    {
        var json = reply.ToJsonString();
        return Encoding.UTF8.GetByteCount(json) <= RelayLimits.MaxDocReplyBytes && tokens.Count(json) <= RelayLimits.MaxDocReplyTokens;
    }

    private static string? Optional(JsonObject args, string field) => Text(args, field) is { Length: > 0 } value ? value : null;

    private static string Short(string commit) => commit.Length > CommitLength ? commit[..CommitLength] : commit;

    private static RelayReply NotConfigured() => Error(DocCacheUnavailable, new { message = "No docs are synced for this repository on this hub." });
}

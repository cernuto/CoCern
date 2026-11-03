using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// Tools that read copies of committed state rather than an aggregate: <c>search</c> over the full-text index and <c>analytics_query</c>
/// over the analytics snapshot. A hub with either turned off answers for it here.
/// </summary>
internal sealed class SearchTools(IFullTextSearch? search, IAnalyticsTool analytics)
{
    /// <summary>
    /// Full-text search over committed state, as the verified session (checked before dispatch): the request's repository plus that
    /// session's own inbox. The reply is compact: per hit its ID, kind, status, date and snippet, and its board when not default. The
    /// agent opens a hit with task_get, context_get or check_inbox. Weakest hits give way until the reply fits its byte budget.
    /// </summary>
    public async Task<RelayReply> SearchAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (search is null) return Error(SearchUnavailable);
        var limit = args["limit"] is null ? RelayLimits.DefaultSearchResults : (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultSearchResults), 1, RelayLimits.MaxSearchResults);
        var kinds = args["kinds"] is JsonArray requested ? Strings(requested).Distinct(StringComparer.Ordinal).ToArray() : [];
        var status = Text(args, "status");
        IReadOnlyList<SearchHit> hits;
        // The index is a read-only copy of committed state: a database fault, or a store closing during shutdown, makes search unavailable
        // for this call, never an argument error or an internal error.
        try { hits = await search.SearchAsync(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "query"), kinds, limit, status.Length > 0 ? status : null, cancellationToken); }
        catch (Exception exception) when (Aggregates.IsStoreFault(exception)) { return Error(Unavailable); }
        var items = new JsonArray();
        foreach (var hit in hits)
        {
            var item = new JsonObject { ["id"] = hit.DocId, ["k"] = hit.Kind, ["s"] = hit.Status };
            // A document with no recorded time has no date to show.
            if (hit.UpdatedAt > DateTimeOffset.MinValue) item["at"] = hit.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd");
            item["x"] = hit.Snippet;
            if (hit.BoardId is { } board) item["b"] = board;
            items.Add(item);
        }
        var reply = new JsonObject { ["hits"] = items };
        while (items.Count > 0 && Encoding.UTF8.GetByteCount(reply.ToJsonString()) > RelayLimits.MaxSearchReplyBytes)
        {
            items.RemoveAt(items.Count - 1);
            reply["truncated"] = true;
        }
        return Ok(reply);
    }

    /// <summary>
    /// Administrator only, checked here because no aggregate handles this tool: a session's self-reported role is not an authority.
    /// An analytics-off hub answers <see cref="AnalyticsUnavailable"/> once the caller is known to be an administrator.
    /// </summary>
    public Task<RelayReply> AnalyticsAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken) =>
        !caller.IsAdministrator ? Task.FromResult(Error(AdministratorRequired))
        : analytics.RunAsync(caller, args, cancellationToken);
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PkRelay.Contracts;
using PkRelay.Domain;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Core;

/// <summary>
/// The <c>analytics_query</c> tool: a named query for the request's repository, or ad hoc SQL when the hub allows it. The caller is
/// already checked to be an administrator. Every ad hoc query is logged, run or refused. The reply is compact rows within
/// <see cref="RelayLimits.MaxAnalyticsReplyBytes"/>; a larger result is refused with a request for a narrower query.
/// </summary>
public sealed class AnalyticsTool(IAnalyticsQueries queries, bool allowAdHocSql, ILogger? logger = null)
{
    public bool AllowAdHocSql => allowAdHocSql;

    public async Task<RelayReply> RunAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken)
    {
        var name = Text(args, "name");
        AnalyticsResult result;
        try
        {
            if (name == AnalyticsQueryNames.AdHocSql)
            {
                var sql = Text(args, "sql");
                if (!allowAdHocSql)
                {
                    logger?.LogWarning("Ad hoc analytics SQL refused (Relay:Analytics:AllowAdHocSql is off) for {Principal}: {Sql}", caller.PrincipalId, sql);
                    return Error(AnalyticsRefused, new { message = "Ad hoc SQL is turned off on this hub; use a named query." });
                }
                logger?.LogInformation("Ad hoc analytics SQL by {Principal}: {Sql}", caller.PrincipalId, sql);
                result = await queries.RunSqlAsync(sql, cancellationToken);
            }
            else
            {
                var parameters = args["params"] is JsonObject given ? JsonSerializer.SerializeToElement(given) : default;
                result = await queries.RunNamedAsync(name, Text(args, "repositoryId"), parameters, cancellationToken);
            }
        }
        catch (AnalyticsException exception)
        {
            return exception.Error switch
            {
                AnalyticsError.Unavailable => Error(AnalyticsUnavailable, new { message = exception.Message }),
                AnalyticsError.UnknownQuery => Error(InvalidArguments, new { field = "name", message = exception.Message }),
                AnalyticsError.InvalidArguments => Error(InvalidArguments, new { field = name == AnalyticsQueryNames.AdHocSql ? "sql" : "params", message = exception.Message }),
                AnalyticsError.Refused => Error(AnalyticsRefused, new { message = exception.Message }),
                AnalyticsError.Timeout => Error(AnalyticsTimeout, new { message = exception.Message }),
                _ => Error(AnalyticsFailed, new { message = exception.Message })
            };
        }
        var rows = new JsonArray(result.Rows.Select(row => (JsonNode)new JsonArray(row.Select(value => JsonSerializer.SerializeToNode(value)).ToArray())).ToArray());
        var reply = new JsonObject
        {
            ["cols"] = new JsonArray(result.Columns.Select(column => (JsonNode)column).ToArray()),
            ["rows"] = rows,
            ["at"] = result.SnapshotAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
        if (result.Truncated) reply["truncated"] = true;
        var size = Encoding.UTF8.GetByteCount(reply.ToJsonString());
        if (size > RelayLimits.MaxAnalyticsReplyBytes)
            return Error(PayloadTooLarge, new { message = $"The result is {size} bytes, more than {RelayLimits.MaxAnalyticsReplyBytes}; ask a narrower query (fewer rows or columns, shorter values)." });
        return Ok(reply);
    }
}

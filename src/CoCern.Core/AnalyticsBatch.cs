using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>Selected public reports, checked for one snapshot and bounded as one serialized response.</summary>
public static class AnalyticsBatch
{
    // Shared with request validation so the router and direct invocation enforce the same selection contract.
    public static string? Problem(JsonObject args)
    {
        if (args["reports"] is not JsonArray reports || (reports.Count < 1 || reports.Count > AnalyticsQueryNames.All.Count)) return "reports";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in reports)
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name) || !AnalyticsQueryNames.All.Contains(name) || !names.Add(name)) return "reports";
        if (args.ContainsKey("sql")) return "sql";
        if (args["params"] is not null && args["params"] is not JsonObject) return "params";
        if (args.ContainsKey("includeDetails") && (args["includeDetails"] is not JsonValue details || !details.TryGetValue<bool>(out _))) return "includeDetails";
        if (args.ContainsKey("ifNoneMatch") && (args["ifNoneMatch"] is not JsonValue tag || !tag.TryGetValue<string>(out var text) || text.Length is 0 or > 128 || text.Any(char.IsControl))) return "ifNoneMatch";
        return null;
    }

    public static async Task<RelayReply> RunAsync(IAnalyticsQueries queries, JsonObject args, CancellationToken cancellationToken)
    {
        if (Problem(args) is { } field) return Error(InvalidArguments, new { field });
        var reports = new JsonObject();
        var snapshots = new HashSet<DateTimeOffset>();
        var errors = 0;
        var details = args["includeDetails"]?.GetValue<bool>() == true;
        var parameters = args["params"] is JsonObject given ? JsonSerializer.SerializeToElement(given) : default;
        foreach (var name in args["reports"]!.AsArray().Select(node => node!.GetValue<string>()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await queries.RunNamedAsync(name, JsonFields.Text(args, "repositoryId"), parameters, cancellationToken);
                snapshots.Add(result.SnapshotAt);
                var selected = details ? result.Rows : result.Rows.Take(1);
                var entry = new JsonObject
                {
                    ["cols"] = new JsonArray(result.Columns.Select(column => (JsonNode)column).ToArray()),
                    ["rows"] = new JsonArray(selected.Select(row => (JsonNode)new JsonArray(row.Select(Cell).ToArray())).ToArray()),
                    ["count"] = result.Rows.Count
                };
                if (result.Truncated) entry["truncated"] = true;
                if (!details && result.Rows.Count > 1) entry["more"] = true;
                reports[name] = entry;
            }
            catch (AnalyticsException exception)
            {
                errors++;
                reports[name] = new JsonObject { ["error"] = exception.Error.ToString() };
            }
        }
        // Never combine rows from snapshots replaced between individual query executions.
        if (snapshots.Count > 1) return Error(AnalyticsUnavailable, new { message = "Analytics snapshot changed during the batch; retry." });
        var reply = new JsonObject { ["reports"] = reports, ["summary"] = new JsonObject { ["ok"] = reports.Count - errors, ["errors"] = errors } };
        if (snapshots.Count == 1) reply["at"] = snapshots.Single().ToString("O");
        // Hash the request scope as well as its compact representation; errors never produce a reusable conditional tag.
        if (errors == 0)
        {
            var scope = JsonSerializer.Serialize(new { repositoryId = JsonFields.Text(args, "repositoryId"), parameters = args["params"], selection = args["reports"], details });
            var etag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + reply.ToJsonString()))).ToLowerInvariant();
            reply["etag"] = etag;
            if (JsonFields.Text(args, "ifNoneMatch") == etag)
                reply = new JsonObject { ["unchanged"] = true, ["etag"] = etag, ["at"] = reply["at"]?.DeepClone() };
        }
        if (Encoding.UTF8.GetByteCount(reply.ToJsonString()) > RelayLimits.MaxAnalyticsReplyBytes)
            return Error(PayloadTooLarge, new { message = "Batch exceeds 8192 bytes; select fewer reports or omit includeDetails." });
        return Ok(reply);
    }

    private static JsonNode? Cell(object? value) => JsonSerializer.SerializeToNode(value is double number && !double.IsFinite(number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : value);
}

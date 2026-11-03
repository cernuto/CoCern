using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Analytics;

namespace CoCern.Host.Dashboard;

/// <summary>Projects only the public named reports, within the existing analytics reply budget.</summary>
public sealed class DashboardAnalytics(IAnalyticsTool tool, IServiceProvider services)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UnknownHistory = "History coverage is unknown; the selected periods may be incomplete.";

    public async Task<IResult> GetAsync(HttpContext context)
    {
        var repositoryValues = context.Request.Query["repositoryId"];
        var dayValues = context.Request.Query["days"];
        var repository = repositoryValues.ToString();
        if (repositoryValues.Count != 1 || string.IsNullOrWhiteSpace(repository) || repository.Length > RelayLimits.MaxIdentifierLength || repository.Any(char.IsControl)
            || dayValues.Count != 1 || !int.TryParse(dayValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < 1 or > 14)
            return Results.BadRequest(new { message = "Choose a repository and a days value from 1 to 14." });
        if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } principal)
            return Results.StatusCode(403);

        var cancellation = context.RequestAborted;
        // The tool's wire timestamp has second precision. Check the actual source timestamp too,
        // so a refresh twice in one second cannot produce a mixed comparison.
        var source = services.GetService<IAnalyticsQueries>() as AnalyticsService;
        var sourceAt = source?.SnapshotAt;
        var sourcePath = source?.SnapshotPath;
        var snapshot = new DashboardAnalyticsSnapshot { RepositoryId = repository, Days = days, Message = UnknownHistory };
        var reports = new List<DashboardAnalyticsQuery>();
        var rowCount = 0;
        foreach (var name in AnalyticsQueryNames.All)
        {
            cancellation.ThrowIfCancellationRequested();
            var report = new DashboardAnalyticsQuery { Name = name };
            try
            {
                var reply = await tool.RunAsync(new RelayCaller(principal, true, [repository]), new JsonObject
                {
                    ["name"] = name, ["repositoryId"] = repository, ["params"] = new JsonObject { ["days"] = days }
                }, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (Encoding.UTF8.GetByteCount(reply.DataJson) > RelayLimits.MaxAnalyticsReplyBytes)
                    return State("too-large", "The analytics result exceeds the reply limit. Choose a shorter period.");
                using var json = JsonDocument.Parse(reply.DataJson);
                var data = json.RootElement;
                if (!reply.Ok)
                {
                    if (reply.Code == RelayErrorCodes.AnalyticsUnavailable)
                    {
                        var missing = data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                            && message.GetString() == "No analytics snapshot is loaded yet.";
                        return State(missing ? "no-snapshot" : "unavailable", missing ? "The first hourly analytics snapshot is not available yet." : "Analytics is unavailable on this hub.");
                    }
                    if (reply.Code == RelayErrorCodes.PayloadTooLarge)
                        return State("too-large", "The analytics result exceeds the reply limit. Choose a shorter period.");
                    report.Error = reply.Code == RelayErrorCodes.AnalyticsTimeout ? "This report timed out." : "This report could not be loaded.";
                }
                else
                {
                    var at = data.GetProperty("at").GetDateTimeOffset();
                    if (source is not null && (source.SnapshotAt != sourceAt || source.SnapshotPath != sourcePath))
                        return State("error", "The hourly snapshot changed during this request. Refresh to compare one snapshot.");
                    if (snapshot.SnapshotAt is { } previous && previous != at)
                        return State("error", "The hourly snapshot changed during this request. Refresh to compare one snapshot.");
                    snapshot.SnapshotAt = at;
                    var columns = data.GetProperty("cols").EnumerateArray().Select(x => x.GetString()).ToArray();
                    if (!columns.SequenceEqual(new[] { "key", "cur", "prev", "cur_n", "prev_n" })) throw new JsonException();
                    var rows = new List<DashboardAnalyticsRow>();
                    foreach (var row in data.GetProperty("rows").EnumerateArray())
                    {
                        if (++rowCount > RelayLimits.MaxAnalyticsMaxRows) return State("too-large", "The analytics result exceeds the row limit. Choose a shorter period.");
                        if (row.GetArrayLength() != 5 || row[0].ValueKind != JsonValueKind.String) throw new JsonException();
                        var key = row[0].GetString()!;
                        if (key.Length > RelayLimits.MaxIdentifierLength) return State("too-large", "An analytics label exceeds the reply limit.");
                        if (key.Any(char.IsControl)) throw new JsonException();
                        var currentCount = row[3].GetInt64();
                        var previousCount = row[4].GetInt64();
                        if (currentCount < 0 || previousCount < 0) throw new JsonException();
                        rows.Add(new DashboardAnalyticsRow { Key = key, Current = Number(row[1]), Previous = Number(row[2]), CurrentCount = currentCount, PreviousCount = previousCount });
                    }
                    report.Rows = rows.ToArray();
                    report.Truncated = data.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Tool diagnostics can contain local paths; none are part of the viewer contract.
                report.Rows = [];
                report.Error = "This report could not be loaded.";
            }
            reports.Add(report);
            snapshot.Queries = reports.ToArray();
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length > RelayLimits.MaxAnalyticsReplyBytes)
                return State("too-large", "The combined analytics result exceeds the reply limit. Choose a shorter period.");
        }
        if (source is not null)
        {
            try
            {
                // The repository is a SQL string value, never an identifier. This fixed internal
                // read exposes only coverage, under the dashboard's existing viewer authorization.
                var value = repository.Replace("'", "''", StringComparison.Ordinal);
                var coverage = await source.RunSqlAsync($"SELECT history_incomplete FROM workflow_history_coverage WHERE repository_id = '{value}'", cancellation);
                if (coverage.Rows.Any(row => row.Count > 0 && row[0] is true))
                    snapshot.Message = "Workflow history is incomplete: retained activity cannot establish every blocked or ready transition. Durations exclude unknown starts. " + UnknownHistory;
            }
            catch (AnalyticsException) { /* An older snapshot may not carry coverage metadata. */ }
        }
        if (source is not null && (source.SnapshotAt != sourceAt || source.SnapshotPath != sourcePath))
            return State("error", "The hourly snapshot changed during this request. Refresh to compare one snapshot.");
        if (reports.All(x => x.Error is not null))
        {
            snapshot.Status = "error";
            snapshot.Message = "Analytics reports could not be loaded. " + UnknownHistory;
        }
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length > RelayLimits.MaxAnalyticsReplyBytes)
            return State("too-large", "The combined analytics result exceeds the reply limit. Choose a shorter period.");
        // A conditional dashboard refresh saves transferring and reprocessing an unchanged report.
        // Bind the tag to the entire response, including repository, period and snapshot identity.
        if (snapshot.Status == "ready" && reports.All(report => report.Error is null))
        {
            var tag = "\"" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions))).ToLowerInvariant() + "\"";
            context.Response.Headers.ETag = tag;
            context.Response.Headers.CacheControl = "private, no-cache";
            if (context.Request.Headers.IfNoneMatch.Any(value => value?.Split(',').Any(candidate => candidate.Trim() == tag) == true))
                return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Ok(snapshot);

        IResult State(string status, string message) => Results.Ok(new DashboardAnalyticsSnapshot
        {
            RepositoryId = repository, Days = days, Status = status, Message = message
        });
    }

    private static double? Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        throw new JsonException();
    }
}

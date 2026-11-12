using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host.Dashboard;

/// <summary>Read-only dashboard projection of the existing retained-context authorization and version boundaries.</summary>
internal sealed class DashboardRetainedContext(DashboardSecurity security, RepositoryCatalog repositories, SessionContextTools retained,
    IConfiguration configuration, DocRepositoryRegistry documents)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IResult> RepositoriesAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        ct.ThrowIfCancellationRequested();
        if (!AllowedQuery(context)) return Invalid<DashboardRetainedContextRepositories>();
        var caller = security.Caller(context.User, DateTimeOffset.UtcNow);
        if (caller is null) return Task.FromResult(Result<DashboardRetainedContextRepositories>(false, RelayErrorCodes.RepositoryDenied, "", 0));
        try
        {
            // Include configured namespaces even while their sessions and document grains are idle, plus repositories discovered since startup.
            var authorized = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var supplied in RepositoryCatalog.ConfiguredNames(configuration).Concat(documents.Ids))
            {
                ct.ThrowIfCancellationRequested();
                var canonical = repositories.Find(supplied);
                if (canonical is { Length: > 0 and <= RelayLimits.MaxIdentifierLength } && !canonical.Any(char.IsControl) && caller.MayAccess(canonical))
                    authorized.Add(canonical);
            }
            var items = new List<string>();
            var bytes = 0;
            var truncated = false;
            foreach (var repository in authorized)
            {
                ct.ThrowIfCancellationRequested();
                var addedBytes = JsonSerializer.SerializeToUtf8Bytes(repository, Json).Length + 1;
                if (items.Count >= 128 || bytes + addedBytes > SessionContextLimits.ReplyBytes - 512) { truncated = true; break; }
                items.Add(repository);
                bytes += addedBytes;
            }
            return Task.FromResult(Result(true, "Ok", "", 0, new DashboardRetainedContextRepositories { Items = items.ToArray(), Truncated = truncated }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result<DashboardRetainedContextRepositories>(false, RelayErrorCodes.Unavailable, "", 0));
        }
    }

    public Task<IResult> ListAsync(HttpContext context)
    {
        if (!AllowedQuery(context, "repositoryId", "taskId", "cursor", "limit")
            || !Query(context, "repositoryId", RelayLimits.MaxIdentifierLength, true, out var supplied)
            || !Query(context, "taskId", RelayLimits.MaxIdentifierLength, false, out var taskId)
            || !Query(context, "cursor", 2048, false, out var cursor)
            || !Limit(context, SessionContextLimits.DiscoveryRows, out var limit))
            return Invalid<DashboardRetainedContextList>();
        var args = new JsonObject { ["repositoryId"] = supplied, ["limit"] = limit };
        if (taskId is not null) args["taskId"] = taskId;
        if (cursor is not null) args["contextCursor"] = cursor;
        return ReadAsync<DashboardRetainedContextList>(context, supplied!, RelayTools.SessionContextGet, args);
    }

    public Task<IResult> ManifestAsync(HttpContext context)
    {
        if (!AllowedQuery(context, "repositoryId", "contextId")
            || !Identity(context, out var supplied, out var contextId))
            return Invalid<DashboardRetainedContextManifest>();
        return ReadAsync<DashboardRetainedContextManifest>(context, supplied!, RelayTools.SessionContextGet,
            new JsonObject { ["repositoryId"] = supplied, ["contextId"] = contextId });
    }

    public Task<IResult> SearchAsync(HttpContext context)
    {
        if (!AllowedQuery(context, "repositoryId", "contextId", "query", "limit")
            || !Identity(context, out var supplied, out var contextId)
            || !Query(context, "query", 512, true, out var query)
            || !Limit(context, SessionContextLimits.SearchHits, out var limit))
            return Invalid<DashboardRetainedContextSearch>();
        return ReadAsync<DashboardRetainedContextSearch>(context, supplied!, RelayTools.SessionContextSearch,
            new JsonObject { ["repositoryId"] = supplied, ["contextId"] = contextId, ["query"] = query, ["limit"] = limit });
    }

    public Task<IResult> PartAsync(HttpContext context)
    {
        // Entry addresses must carry generation, version and part. An unversioned/current read is never inferred.
        if (!AllowedQuery(context, "uri") || !Query(context, "uri", 1024, true, out var uri)
            || !SessionContextResourceUri.TryParse(uri, out var address) || address.EntryId is null)
            return Invalid<DashboardRetainedContextPart>();
        return ReadAsync<DashboardRetainedContextPart>(context, address.RepositoryId, RelayTools.SessionContextGet,
            new JsonObject
            {
                ["repositoryId"] = address.RepositoryId, ["contextId"] = address.ContextId, ["entryId"] = address.EntryId,
                ["entryVersion"] = address.EntryVersion, ["contextGeneration"] = address.Generation, ["part"] = address.Part
            });
    }

    private async Task<IResult> ReadAsync<T>(HttpContext context, string supplied, string operation, JsonObject args) where T : class
    {
        var ct = context.RequestAborted;
        ct.ThrowIfCancellationRequested();
        // The viewer cookie contains no relay entitlements. Resolve the exact current enrollment for every read.
        var caller = security.Caller(context.User, DateTimeOffset.UtcNow);
        var repository = repositories.Find(supplied);
        if (caller is null || repository is null || !caller.MayAccess(repository))
            return Result<T>(false, RelayErrorCodes.RepositoryDenied, "", 0);
        args["repositoryId"] = repository;
        try
        {
            // This public service authorizes before entering its trusted grain boundary; no session or internal caller is invented here.
            var reply = await retained.RunAsync(caller, operation, args, ct).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            var data = reply.Ok ? JsonSerializer.Deserialize<T>(reply.DataJson, Json) : null;
            if (reply.Ok && data is null) return Result<T>(false, RelayErrorCodes.Unavailable, repository, reply.Version);
            return Result(reply.Ok, reply.Code, repository, reply.Version, data);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();
            return Result<T>(false, RelayErrorCodes.Unavailable, repository, 0);
        }
    }

    private static bool Identity(HttpContext context, out string? repository, out string? contextId)
    {
        contextId = null;
        return Query(context, "repositoryId", RelayLimits.MaxIdentifierLength, true, out repository)
            && Query(context, "contextId", SessionContextLimits.IdLength, true, out contextId)
            && SessionContextLimits.ValidId(contextId);
    }

    private static bool AllowedQuery(HttpContext context, params string[] fields) =>
        context.Request.Query.Keys.All(key => fields.Contains(key, StringComparer.Ordinal));

    private static bool Query(HttpContext context, string name, int maximum, bool required, out string? value)
    {
        value = null;
        if (!context.Request.Query.TryGetValue(name, out var values)) return !required;
        if (values.Count != 1 || values[0] is not { } supplied || supplied.Length > maximum || supplied.Any(char.IsControl)) return false;
        if (string.IsNullOrWhiteSpace(supplied)) return !required;
        value = supplied;
        return true;
    }

    private static bool Limit(HttpContext context, int maximum, out int limit)
    {
        limit = maximum;
        return Query(context, "limit", 10, false, out var value) && (value is null
            || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit >= 1 && limit <= maximum);
    }

    private static Task<IResult> Invalid<T>() where T : class =>
        Task.FromResult(Result<T>(false, RelayErrorCodes.InvalidArguments, "", 0));

    private static IResult Result<T>(bool ok, string code, string repository, long version, T? data = null) where T : class =>
        Results.Json(new DashboardRetainedContextReply<T>
        {
            Ok = ok, Code = code, SampledAt = DateTimeOffset.UtcNow, RepositoryId = repository, Version = version, Data = data
        }, statusCode: ok ? StatusCodes.Status200OK : code switch
        {
            RelayErrorCodes.RepositoryDenied => StatusCodes.Status403Forbidden,
            RelayErrorCodes.NotFound => StatusCodes.Status404NotFound,
            SessionContextLimits.VersionUnavailable or SessionContextLimits.ListRestartRequired => StatusCodes.Status409Conflict,
            RelayErrorCodes.Unavailable or RelayErrorCodes.StorageFailure or SessionContextLimits.SearchUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        });
}

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host.Dashboard;

internal static class DashboardSessionToolSettings
{
    public static async Task<IResult> ReadAsync(HttpContext context, RelayApplication application, RepositoryCatalog repositories)
    {
        if (!DashboardSecurity.SecureTransport(context) || context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } principal)
            return Results.StatusCode(403);
        var repository = repositories.Find(context.Request.Query["repositoryId"].ToString());
        var session = context.Request.Query["sessionId"].ToString();
        if (repository is null || !ValidId(session)) return Refused(RelayErrorCodes.InvalidArguments, 400);
        var reply = await application.ReadSessionToolSettingsAsync(new RelayCaller(principal, true, [repository]), repository, session);
        return Result(reply);
    }

    public static async Task<IResult> SaveAsync(HttpContext context, IRelayApplication application, RepositoryCatalog repositories)
    {
        if (!DashboardSecurity.SecureTransport(context) || !DashboardSecurity.SameOrigin(context)
            || context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } principal)
            return Results.StatusCode(403);
        const int maximum = 16 * 1024;
        if (context.Request.ContentLength > maximum) return Results.StatusCode(413);
        using var stream = new MemoryStream();
        var buffer = new byte[maximum + 1];
        while (stream.Length <= maximum)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, maximum + 1 - (int)stream.Length), context.RequestAborted);
            if (read == 0) break;
            stream.Write(buffer, 0, read);
        }
        if (stream.Length > maximum) return Results.StatusCode(413);
        DashboardSessionToolSettingsRequest? request;
        try { request = JsonSerializer.Deserialize<DashboardSessionToolSettingsRequest>(stream.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 4 }); }
        catch (JsonException) { return Refused(RelayErrorCodes.InvalidArguments, 400); }
        if (request is null || !ValidId(request.SessionId) || request.ExpectedVersion < 0 || request.Profile is not ("worker" or "chat" or "diagnostics" or "admin")
            || string.IsNullOrWhiteSpace(request.HumanWords) || request.HumanWords.Length > 1024)
            return Refused(RelayErrorCodes.InvalidArguments, 400);
        var repository = repositories.Find(request.RepositoryId);
        if (repository is null) return Refused(RelayErrorCodes.RepositoryDenied, 403);
        var args = new JsonObject
        {
            ["requestId"] = "dashboard-settings-" + Guid.NewGuid().ToString("N"), ["repositoryId"] = repository,
            ["targetSessionId"] = request.SessionId, ["profile"] = request.Profile,
            ["expectedVersion"] = request.ExpectedVersion, ["humanWords"] = request.HumanWords
        };
        var reply = await application.ExecuteAsync(new RelayCaller(principal, true, [repository]), RelayTools.SessionToolSettingsSet,
            JsonSerializer.SerializeToElement(args), context.RequestAborted);
        return Result(reply);
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= RelayLimits.MaxIdentifierLength && !value.Any(char.IsControl);
    private static IResult Refused(string code, int status) => Results.Json(new DashboardSessionToolSettingsResult { Code = code }, statusCode: status);
    private static IResult Result(RelayReply reply)
    {
        if (!reply.Ok) return Refused(reply.Code, ToolResults.Status(reply));
        try
        {
            var data = JsonNode.Parse(reply.DataJson) as JsonObject;
            if (data is null || data["revision"] is not JsonValue revision || !revision.TryGetValue<long>(out var version))
                return Refused(RelayErrorCodes.Unavailable, 503);
            var profile = data["profile"] is JsonValue value && value.TryGetValue<string>(out var selected) ? selected : null;
            return Results.Ok(new DashboardSessionToolSettingsResult { Ok = true, Profile = profile, Revision = version });
        }
        catch (JsonException) { return Refused(RelayErrorCodes.Unavailable, 503); }
    }
}

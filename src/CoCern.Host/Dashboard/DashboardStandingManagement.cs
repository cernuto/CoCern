using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host.Dashboard;

public static class DashboardStandingManagement
{
    public static async Task<IResult> DecideAsync(HttpContext context, IRelayApplication application)
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
        DashboardStandingDecision? decision;
        try { decision = JsonSerializer.Deserialize<DashboardStandingDecision>(stream.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 4 }); }
        catch (JsonException) { return Invalid(); }
        if (decision is null || decision.ExpectedRevision is null or < 0 || decision.Action is not ("approve" or "remove")
            || string.IsNullOrWhiteSpace(decision.RepositoryId) || string.IsNullOrWhiteSpace(decision.BoardId)
            || string.IsNullOrWhiteSpace(decision.Id) || string.IsNullOrWhiteSpace(decision.RequestId)) return Invalid();
        // Authority comes only from the signed-in viewer; standing_put retains validation and history.
        var args = new JsonObject
        {
            ["repositoryId"] = decision.RepositoryId, ["boardId"] = decision.BoardId,
            ["requestId"] = decision.RequestId, ["expectedRevision"] = decision.ExpectedRevision.Value,
            [decision.Action] = new JsonArray(decision.Id)
        };
        var reply = await application.ExecuteAsync(new RelayCaller(principal, true, [decision.RepositoryId]), RelayTools.StandingPut,
            JsonSerializer.SerializeToElement(args), context.RequestAborted);
        return Results.Json(new DashboardStandingDecisionResult(reply.Ok, reply.Code, reply.Version), statusCode: ToolResults.Status(reply));
    }

    private static IResult Invalid() => Results.BadRequest(new DashboardStandingDecisionResult(false, RelayErrorCodes.InvalidArguments, 0));
}

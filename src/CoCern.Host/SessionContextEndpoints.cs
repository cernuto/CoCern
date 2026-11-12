using CoCern.Contracts;
using CoCern.Core;

namespace CoCern.Host;

public static class SessionContextEndpoints
{
    public static void MapSessionContextResources(this WebApplication app) => app.MapGet("/api/session-context/resource",
        async (HttpContext context, string uri, SessionContextTools retained, CancellationToken ct) =>
        {
            var reply = await retained.ReadResourceAsync(RelayAuthentication.Caller(context.User), uri, ct);
            // Missing and unauthorized addresses share a bounded public projection; neither can reveal private state.
            return Results.Json(reply, statusCode: reply.Ok ? 200 : reply.Code == RelayErrorCodes.RepositoryDenied ? 403 : 400);
        }).RequireAuthorization();
}

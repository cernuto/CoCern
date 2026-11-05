using CoCern.Contracts.Dashboard;
using CoCern.Storage;

namespace CoCern.Host.Dashboard;

public static class DashboardDatabaseManagement
{
    public const string Confirmation = "RESET ALL COORDINATION DATA";

    public static async Task<IResult> ReadAsync(HttpContext context, SqliteStore store)
    {
        if (!DashboardSecurity.LocalBrowser(context)) return Results.StatusCode(403);
        return Results.Ok(new DashboardDatabaseResetStatus(await store.CoordinationResetPendingAsync(context.RequestAborted)));
    }

    public static async Task<IResult> ScheduleAsync(HttpContext context, SqliteStore store)
    {
        if (!DashboardSecurity.LocalBrowser(context) || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        if (!context.Request.HasJsonContentType()) return Results.BadRequest();
        DashboardDatabaseResetRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<DashboardDatabaseResetRequest>(context.RequestAborted); }
        catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
        catch (BadHttpRequestException) { return Results.BadRequest(); }
        if (request is null || (!request.Cancel && request.Confirmation != Confirmation)) return Results.BadRequest();
        await store.ScheduleCoordinationResetAsync(request.Cancel, context.RequestAborted);
        return Results.Ok(new DashboardDatabaseResetStatus(await store.CoordinationResetPendingAsync(context.RequestAborted)));
    }
}

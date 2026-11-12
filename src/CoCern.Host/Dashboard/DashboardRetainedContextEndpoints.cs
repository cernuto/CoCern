namespace CoCern.Host.Dashboard;

public static class DashboardRetainedContextEndpoints
{
    public static RouteGroupBuilder MapDashboardRetainedContext(this RouteGroupBuilder api)
    {
        api.MapGet("retained-context/repositories", (HttpContext context, DashboardRetainedContext retained) => retained.RepositoriesAsync(context))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("retained-context", (HttpContext context, DashboardRetainedContext retained) => retained.ListAsync(context))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("retained-context/manifest", (HttpContext context, DashboardRetainedContext retained) => retained.ManifestAsync(context))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("retained-context/search", (HttpContext context, DashboardRetainedContext retained) => retained.SearchAsync(context))
            .RequireAuthorization(DashboardSecurity.Policy);
        api.MapGet("retained-context/part", (HttpContext context, DashboardRetainedContext retained) => retained.PartAsync(context))
            .RequireAuthorization(DashboardSecurity.Policy);
        return api;
    }
}

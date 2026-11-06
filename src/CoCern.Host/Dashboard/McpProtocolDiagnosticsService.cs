using CoCern.Adapter;

namespace CoCern.Host.Dashboard;

/// <summary>Observes this host's MCP requests only, including when several hosts run in the same test process.</summary>
internal sealed class McpProtocolDiagnosticsService(DashboardConnectionDiagnostics diagnostics, IHttpContextAccessor accessor) : IHostedService, IDisposable
{
    private McpProtocolDiagnostics? listener;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        listener = new(diagnostics.Record, "hub", () => accessor.HttpContext is { } context &&
            ReferenceEquals(context.RequestServices.GetService<DashboardConnectionDiagnostics>(), diagnostics));
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { listener?.Dispose(); listener = null; }
}

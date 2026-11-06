using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using Microsoft.AspNetCore.Authorization;

namespace CoCern.Host.Dashboard;

/// <summary>Read-only local diagnostics and bounded authenticated reported timing ingestion.</summary>
public sealed class DashboardConnectionDiagnostics : IDisposable
{
    private readonly ConnectionTraceStore store;
    private readonly string? developmentDirectory;
    private readonly bool includeLocalClients;
    private readonly object rateGate = new();
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> rate = new(StringComparer.Ordinal);
    public DashboardConnectionDiagnostics(IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RelayHostBuilderExtensions.DatabasePath(configuration, environment)))!, "connection-diagnostics");
        store = new(directory);
        includeLocalClients = configuration.GetValue("Relay:ConnectionDiagnostics:IncludeLocalClients", true);
        var adapter = configuration["Relay:Connect:AdapterPath"];
        if (adapter is not null && Path.GetDirectoryName(adapter) is { } version &&
            Path.GetDirectoryName(version) is { } versions && Path.GetFileName(versions) == "versions" &&
            Path.GetDirectoryName(versions) is { } deployment) developmentDirectory = Path.Combine(deployment, "connection-diagnostics");
    }
    public ConnectionDiagnosticsSnapshot Read()
    {
        var rows = store.Read().Events.AsEnumerable();
        if (includeLocalClients)
        {
            var roots = new List<string> { ConnectionTraceStore.DefaultDirectory,
                Path.Combine(CoCern.Adapter.Node.NodeState.DefaultFolder, "connection-diagnostics") };
            if (developmentDirectory is not null) roots.Add(developmentDirectory);
            var wakeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoCern", "local-wake");
            try
            {
                if (Directory.Exists(wakeRoot) && !new DirectoryInfo(wakeRoot).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    roots.AddRange(new DirectoryInfo(wakeRoot).EnumerateDirectories().Take(64).Where(x => !x.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        .OrderByDescending(x => x.LastWriteTimeUtc).Take(16).Select(x => Path.Combine(x.FullName, "connection-diagnostics")));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) rows = rows.Concat(new ConnectionTraceStore(root).Read().Events);
        }
        return new() { Events = rows.Distinct().OrderByDescending(x => x.StartedAt).Take(ConnectionTraceStore.MaxEvents).ToArray() };
    }
    internal void Record(ConnectionDiagnosticEvent item) => store.Record(item);
    public void Dispose() => store.Dispose();
    internal bool AcceptRate(ClaimsPrincipal principal)
    {
        var name = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (name is not { Length: > 0 and <= 256 }) return false;
        lock (rateGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var old in rate.Where(x => now - x.Value.At > TimeSpan.FromSeconds(2)).Select(x => x.Key).ToArray()) rate.Remove(old);
            if (!rate.TryGetValue(name, out var window))
            {
                if (rate.Count >= 128) return false;
                window = (now, 0);
            }
            if (window.Count >= 32) return false;
            rate[name] = (window.At, window.Count + 1);
            return true;
        }
    }
    internal async Task<IResult> IngestAsync(HttpContext context)
    {
        if (!AcceptRate(context.User)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        if (context.Request.ContentLength is > 16384) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                if (read == 0) break;
                if (bytes.Length + read > 16384) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                bytes.Write(buffer, 0, read);
            }
            var batch = JsonSerializer.Deserialize<ConnectionDiagnosticsBatch>(bytes.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (batch?.Events is not { Length: > 0 and <= 24 } rows || rows.Any(x => !ConnectionTraceStore.Valid(x) || x.Source == "hub"))
                return Results.BadRequest(new { code = "invalid_connection_diagnostics" });
            foreach (var row in rows) store.Record(row);
            return Results.NoContent();
        }
        catch (JsonException) { return Results.BadRequest(new { code = "invalid_connection_diagnostics" }); }
    }
}

public sealed class ConnectionTimingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DashboardConnectionDiagnostics diagnostics)
    {
        const string prefix = "/api/tools/";
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal) ||
            !RelayToolCatalog.TryGet(path[prefix.Length..], out var tool)) { await next(context); return; }
        var supplied = context.Request.Headers[ConnectionTrace.Header].ToString();
        var id = ConnectionTraceStore.ValidId(supplied) ? supplied : "cc-" + Guid.NewGuid().ToString("N");
        var at = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ConnectionTrace.Header] = id;
            context.Response.Headers[ConnectionTrace.HubTimingHeader] = Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });
        var outcome = "ok";
        try { await next(context); if (context.Response.StatusCode >= 400) outcome = "error"; }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch { outcome = "error"; throw; }
        finally
        {
            diagnostics.Record(new() { CorrelationId = id, Tool = tool.Name, Source = "hub", Stage = "hub_execution", StartedAt = at,
                DurationMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3), Outcome = outcome });
        }
    }
}

public static class ConnectionDiagnosticsEndpoints
{
    public static void MapConnectionDiagnostics(this WebApplication app) =>
        app.MapPost("/api/connection-diagnostics", (HttpContext context, DashboardConnectionDiagnostics diagnostics) => diagnostics.IngestAsync(context))
            .RequireAuthorization(new AuthorizationPolicyBuilder("Relay").RequireAuthenticatedUser().Build());
}

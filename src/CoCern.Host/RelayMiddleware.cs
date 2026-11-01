using CoCern.Contracts;

namespace CoCern.Host;

internal static class RelayMiddleware
{
    /// <summary>Unhandled failures become the stable error envelope; exception details never reach the client.</summary>
    public static IApplicationBuilder UseRelayErrorEnvelope(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (BadHttpRequestException exception)
        {
            var code = exception.StatusCode == StatusCodes.Status413PayloadTooLarge ? RelayErrorCodes.PayloadTooLarge : RelayErrorCodes.InvalidArguments;
            await WriteFailureAsync(context, exception.StatusCode, code);
        }
        catch (Exception) { await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, RelayErrorCodes.InternalError); }
    });

    /// <summary>Marks coordination responses uncacheable and buffers tool bodies under the public envelope bound, including chunked bodies.</summary>
    public static IApplicationBuilder UseBoundedToolBodies(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp") || path.StartsWithSegments("/hubs")) context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsPost(context.Request.Method) && (path.StartsWithSegments("/api/tools") || path.StartsWithSegments("/api/discovery") || path.StartsWithSegments("/mcp")))
        {
            if (context.Request.ContentLength > RelayLimits.MaxPayloadBytes || await TryBufferAsync(context) is not { } buffered)
            {
                await WriteFailureAsync(context, StatusCodes.Status413PayloadTooLarge, RelayErrorCodes.PayloadTooLarge);
                return;
            }
            context.Request.Body = buffered;
        }
        await next(context);
    });

    private static async Task<MemoryStream?> TryBufferAsync(HttpContext context)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted);
            if (read == 0) break;
            if (buffer.Length + read > RelayLimits.MaxPayloadBytes) { await buffer.DisposeAsync(); return null; }
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        context.Response.RegisterForDisposeAsync(buffer);
        return buffer;
    }

    private static async Task WriteFailureAsync(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { ok = false, code });
    }
}

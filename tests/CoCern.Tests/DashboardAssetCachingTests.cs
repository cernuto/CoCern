using System.Net;
using CoCern.Host;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardAssetCachingTests
{
    [Fact]
    public async Task Failed_assets_override_cache_headers_while_valid_bundles_and_conditional_loaders_keep_working()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseRelayErrorEnvelope();
        app.UseDashboardBoundary();
        app.Run(context =>
        {
            // Model the long-lived headers a static-file handler may set before finding a missing asset or failing to read it.
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Response.Headers.Expires = "Wed, 01 Jan 2031 00:00:00 GMT";
            context.Response.ContentType = "application/wasm";
            context.Response.Headers.ETag = "\"bundle\"";
            if (context.Request.Path.Value!.Contains("missing", StringComparison.Ordinal))
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            else if (context.Request.Path.Value.Contains("unreadable", StringComparison.Ordinal))
                throw new IOException("The startup asset cannot be read.");
            else if (context.Request.Headers.IfNoneMatch == "\"bundle\"")
                context.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var missing = await client.GetAsync("/dashboard/_framework/missing.wasm");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());
        Assert.False(missing.Content.Headers.Contains("Expires"));
        using var unreadable = await client.GetAsync("/dashboard/_framework/unreadable.wasm");
        Assert.Equal(HttpStatusCode.InternalServerError, unreadable.StatusCode);
        Assert.Equal("no-store", unreadable.Headers.CacheControl?.ToString());

        using var bundle = await client.GetAsync("/dashboard/_framework/runtime.fingerprint.wasm");
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", bundle.Headers.CacheControl?.ToString());
        using var loaderRequest = new HttpRequestMessage(HttpMethod.Head, "/dashboard/_framework/dotnet.js");
        loaderRequest.Headers.IfNoneMatch.Add(new("\"bundle\""));
        using var loader = await client.SendAsync(loaderRequest);
        Assert.Equal(HttpStatusCode.NotModified, loader.StatusCode);
        Assert.Equal("no-cache", loader.Headers.CacheControl?.ToString());
        using var graph = await client.GetAsync("/dashboard/session-graph.css?v=live-sends");
        Assert.Equal("no-cache", graph.Headers.CacheControl?.ToString());
        Assert.False(graph.Content.Headers.Contains("Expires"));
        using var outside = await client.GetAsync("/other/missing.wasm");
        Assert.Equal("public, max-age=31536000, immutable", outside.Headers.CacheControl?.ToString());
    }
}

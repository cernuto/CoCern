using System.Net;
using System.Text;
using System.Text.Json;
using CoCern.Adapter;
using Xunit;

namespace CoCern.Tests;

public sealed class AdapterTests
{
    [Fact]
    public async Task ForwardsOnlyFixedRouteWithBearerAndRawArguments()
    {
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:5271/api/tools/task_get", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("client-secret", request.Headers.Authorization.Parameter);
            Assert.Equal("{\"repositoryId\":\"repo\",\"taskId\":\"one\"}", await request.Content!.ReadAsStringAsync(token));
            return Json("{\"ok\":true,\"code\":\"ok\",\"dataJson\":\"{\\\"title\\\":\\\"One\\\"}\",\"version\":4}");
        });
        using var client = new HttpClient(handler);
        var result = await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{\"repositoryId\":\"repo\",\"taskId\":\"one\"}"));
        Assert.True(result.Ok);
        Assert.Equal(4, result.Version);
        Assert.Equal("One", result.Data!.Value.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.InternalServerError, "hub_error")]
    public async Task ShapesHttpFailuresWithoutLeakingBody(HttpStatusCode status, string code)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("sensitive-server-detail") }));
        using var client = new HttpClient(handler);
        var result = await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"));
        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("client-secret", JsonSerializer.Serialize(result));
        if (status == HttpStatusCode.Unauthorized)
        {
            Assert.Equal("The hub didn't accept this PC's sign-in. Reconnect this PC to the hub and try again.", result.Data?.GetProperty("message").GetString());
            Assert.Null(result.Version);
        }
        else Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("tls")]
    public async Task ShapesTransportFailuresWithoutLeakingException(string failure)
    {
        using var handler = new StubHandler((_, _) =>
        {
            if (failure == "io") throw new IOException("client-secret");
            var kind = failure switch
            {
                "dns" => HttpRequestError.NameResolutionError,
                "socket" => HttpRequestError.ConnectionError,
                "tls" => HttpRequestError.SecureConnectionError,
                _ => HttpRequestError.Unknown
            };
            throw new HttpRequestException(kind, "client-secret");
        });
        using var client = new HttpClient(handler);
        var result = await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"));
        Assert.False(result.Ok);
        Assert.Equal("hub_unavailable", result.Code);
        Assert.Equal("Can't reach the hub. Check that it is running and try again.", result.Data?.GetProperty("message").GetString());
        Assert.Null(result.Version);
        Assert.DoesNotContain("client-secret", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("http://example.com/hubs/coordination/negotiate")]
    [InlineData("https://127.0.0.1:5271/hubs/coordination/negotiate")]
    public async Task SignalRRedirectCannotSendCredentialsToAnotherOrigin(string url)
    {
        using var guard = new PinnedOriginHandler(Options().BaseUri, new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Credentials escaped origin")));
        using var client = new HttpClient(guard);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
    }

    [Fact]
    public async Task WorkerCannotForwardAdminTool()
    {
        using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected admin request"));
        using var client = new HttpClient(handler);
        Assert.Equal("unknown_tool", (await new RelayForwarder(client, Options()).ForwardAsync("control_request", Parse("{}"))).Code);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:password@example.com")]
    public void RejectsUnsafeBaseUrls(string url) => Assert.Throws<AdapterConfigurationException>(() => new AdapterOptions(url, "token"));

    [Fact]
    public void MissingTokenFailsWithoutPrintingTokenOrPath()
    {
        var error = Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(_ => null));
        Assert.Contains("COCERN_TOKEN", error.Message);
        Assert.Throws<AdapterConfigurationException>(() => new AdapterOptions("http://localhost", " \r\n"));
        Assert.Throws<AdapterConfigurationException>(() => new AdapterOptions("http://localhost", "unsafe\r\nvalue"));
    }

    private static AdapterOptions Options() => new("http://127.0.0.1:5271", "client-secret");
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}

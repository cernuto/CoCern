using System.Net;
using System.Text;
using System.Text.Json;
using PkRelay.Adapter;
using Xunit;

namespace PkRelay.Tests;

/// <summary>The adapter passes the hub's own domain failure through on every status the hub answers failures with, and nothing else.</summary>
public sealed class ForwarderErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "RequestIdRequired")]
    [InlineData(HttpStatusCode.BadRequest, "ResourceUnknown")]
    [InlineData(HttpStatusCode.BadRequest, "InvalidArguments")]
    [InlineData(HttpStatusCode.Forbidden, "RepositoryDenied")]
    [InlineData(HttpStatusCode.NotFound, "NotFound")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge")]
    [InlineData(HttpStatusCode.Conflict, "VersionConflict")]
    public async Task Domain_failure_keeps_its_code_data_and_version(HttpStatusCode status, string code)
    {
        var result = await Forward(status, Envelope(false, code, "{\\\"error\\\":\\\"detail\\\"}", 3));
        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Equal(3, result.Version);
        Assert.Equal("detail", result.Data!.Value.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "RepositoryDenied", "hub_error")]
    [InlineData(HttpStatusCode.Forbidden, "RequestIdRequired", "forbidden")]
    [InlineData(HttpStatusCode.NotFound, "VersionConflict", "hub_error")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "NotFound", "request_too_large")]
    public async Task Code_that_does_not_match_its_status_stays_generic(HttpStatusCode status, string code, string expected)
    {
        var result = await Forward(status, Envelope(false, code, "{\\\"secret\\\":\\\"sensitive\\\"}", 3));
        Assert.Equal(expected, result.Code);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "hub_error")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.NotFound, "hub_error")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "request_too_large")]
    public async Task Unknown_code_success_flag_or_non_envelope_never_leaks(HttpStatusCode status, string expected)
    {
        foreach (var body in new[] { Envelope(false, "private_server_detail", "{\\\"secret\\\":\\\"sensitive\\\"}", 3), Envelope(true, "Ok", "{\\\"secret\\\":\\\"sensitive\\\"}", 3), "sensitive-server-detail" })
        {
            var result = await Forward(status, body);
            Assert.False(result.Ok);
            Assert.Equal(expected, result.Code);
            Assert.Null(result.Data);
            Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task Oversized_failure_body_stays_an_explicit_size_error(HttpStatusCode status, bool declaredLength)
    {
        var body = new string('x', RelayForwarder.MaxResponseBytes + 1);
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(status)
        {
            Content = declaredLength ? new StringContent(body) : new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
        }));
        var result = await new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret")).ForwardAsync("ack", JsonSerializer.Deserialize<JsonElement>("{}"));
        Assert.False(result.Ok);
        Assert.Equal("response_too_large", result.Code);
        Assert.Null(result.Data);
    }

    private static string Envelope(bool ok, string code, string dataJson, long version) =>
        $"{{\"ok\":{(ok ? "true" : "false")},\"code\":\"{code}\",\"dataJson\":\"{dataJson}\",\"version\":{version}}}";

    private static async Task<AdapterReply> Forward(HttpStatusCode status, string body)
    {
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        return await new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret")).ForwardAsync("ack", JsonSerializer.Deserialize<JsonElement>("{\"repositoryId\":\"repo\"}"));
    }

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
}

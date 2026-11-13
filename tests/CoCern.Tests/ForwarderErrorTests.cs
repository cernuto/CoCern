using System.Net;
using System.Text;
using System.Text.Json;
using CoCern.Adapter;
using Xunit;

namespace CoCern.Tests;

/// <summary>The adapter passes the hub's own domain failure through on every status the hub answers failures with, and nothing else.</summary>
public sealed class ForwarderErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "hub_error", "uncertain")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", "not_applied")]
    public async Task Unknown_code_success_flag_or_non_envelope_never_leaks(HttpStatusCode status, string expected, string delivery)
    {
        foreach (var body in new[] { Envelope(false, "private_server_detail", "{\\\"secret\\\":\\\"sensitive\\\"}", 3), Envelope(true, "Ok", "{\\\"secret\\\":\\\"sensitive\\\"}", 3), "sensitive-server-detail" })
        {
            var result = await Forward(status, body);
            Assert.False(result.Ok);
            Assert.Equal(expected, result.Code);
            // ack is a write, so the adapter's own failure says only whether it reached the hub.
            var data = result.Data!.Value;
            Assert.Equal(new[] { "delivery", "hint" },data.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(delivery, data.GetProperty("delivery").GetString());
            Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
        }
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

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Every adapter-originated hub call says who made it, so measurement can tell a model's calls from background ones. One test per caller,
/// each asserting what the hub receives; the tag grants nothing, so a call without one, an exact native envelope and what the model sees stay as they were.
/// </summary>
public sealed class CallOriginAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-call-origin-adapter-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ---- One test per caller ----

    [Fact]
    public async Task A_model_tool_call_reports_mcp_on_every_request_including_the_polls_of_one_that_waits()
    {
        var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(), null, null, "origin-scope");
        Assert.True((await calls.CallAsync(RelayTools.SessionOpen, Element(new { repositoryId = "repo", requestId = "open-1", label = "Chat" }), CancellationToken.None)).Ok);
        Assert.True((await calls.CallAsync(RelayTools.TaskGet, Element(new { repositoryId = "repo" }), CancellationToken.None)).Ok);
        Assert.True((await calls.CallAsync(RelayTools.CheckInbox, Element(new { repositoryId = "repo", sessionId = "s-1", sessionToken = "synthetic-token", waitSeconds = 1 }), CancellationToken.None)).Ok);

        Assert.Equal(new[] { RelayTools.SessionOpen, RelayTools.TaskGet, RelayTools.CheckInbox }, hub.Tools.Select(call => call.Tool).Distinct());
        Assert.All(hub.Tools, call => Assert.Equal(CallOrigins.Mcp, Origin(call.Body)));
    }

    // ---- Fixtures ----

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static string? Origin(JsonObject body) => body[CallOrigins.Field]?.GetValue<string>();

    /// <summary>Answers tool calls as a hub with one waiting message would, and records exactly what each request sent. It holds nothing to dispose, so it can be handed out again.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        public const string Origin = "http://127.0.0.1:5271";
        private readonly ConcurrentQueue<(string Tool, string Raw)> _tools = new();
        private readonly ConcurrentQueue<string> _discovery = new();

        /// <summary>The one route an announcement is answered with.</summary>
        public JsonObject? Routes { get; init; }
        public (string Tool, JsonObject Body)[] Tools => _tools.Select(call => (call.Tool, JsonNode.Parse(call.Raw)!.AsObject())).ToArray();
        public string[] Raw => _tools.Select(call => call.Raw).ToArray();
        public JsonObject[] Discovery => _discovery.Select(raw => JsonNode.Parse(raw)!.AsObject()).ToArray();

        public RelayForwarder Forwarder(string? origin = null) => new(new HttpClient(this, disposeHandler: false), new AdapterOptions(Origin, "synthetic-credential"), origin);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/discovery/", StringComparison.Ordinal))
            {
                _discovery.Enqueue(body);
                var routes = new JsonObject { ["routes"] = Routes is null ? new JsonArray() : new JsonArray(Routes.DeepClone()) };
                return new(HttpStatusCode.OK) { Content = new StringContent(routes.ToJsonString(), Encoding.UTF8, "application/json") };
            }
            var tool = request.RequestUri.Segments[^1];
            _tools.Enqueue((tool, body));
            var reply = JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = Data(tool, body), version = 1 });
            return new(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }

        private static string Data(string tool, string body)
        {
            using var document = JsonDocument.Parse(body);
            var args = document.RootElement;
            if (args.TryGetProperty("nativePreflight", out _)) return "{\"repositoryId\":\"repo\"}";
            if (args.TryGetProperty("nativeJoin", out _)) return "{\"sessionId\":\"s-native\",\"sessionToken\":\"synthetic-token\"}";
            return tool switch
            {
                RelayTools.SessionOpen => "{\"sessionId\":\"s-1\",\"sessionToken\":\"synthetic-token\"}",
                RelayTools.GetStatus => new JsonObject { ["sessionId"] = ChatLocators.Text(args, "sessionId"), ["control"] = new JsonObject { ["paused"] = false, ["requests"] = new JsonArray() } }.ToJsonString(),
                RelayTools.CheckInbox => "{\"items\":[{\"messageId\":\"m1\",\"repositoryId\":\"repo\",\"text\":\"synthetic\"}],\"total\":1,\"nextCursor\":null,\"truncated\":false}",
                _ => "{}"
            };
        }
    }
}

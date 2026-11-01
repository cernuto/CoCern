using System.Net;
using System.Text;
using System.Text.Json;
using PkRelay.Adapter;
using Xunit;

namespace PkRelay.Tests;

public sealed class AdapterTests
{
    [Fact]
    public async Task PreservesRecognizedHttpConflictWithoutLeakingUnknownFailureBodies()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"ok\":false,\"code\":\"VersionConflict\",\"dataJson\":\"{\\\"currentVersion\\\":8}\",\"version\":8}", Encoding.UTF8, "application/json") }));
        using var client = new HttpClient(handler);
        var result = await new RelayForwarder(client, Options()).ForwardAsync("task_update", Parse("{\"repositoryId\":\"repo\"}"));
        Assert.False(result.Ok);
        Assert.Equal("VersionConflict", result.Code);
        Assert.Equal(8, result.Version);
        Assert.Equal(8, result.Data!.Value.GetProperty("currentVersion").GetInt32());
    }

    [Theory]
    [InlineData("sensitive-server-detail")]
    [InlineData("{\"ok\":false,\"code\":\"private_server_detail\",\"dataJson\":\"{\\\"secret\\\":\\\"sensitive\\\"}\",\"version\":8}")]
    public async Task HttpConflictNeverExposesUnrecognizedBody(string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(body) }));
        using var client = new HttpClient(handler);
        var result = await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"));
        Assert.False(result.Ok);
        Assert.Null(result.Data);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private_server_detail", result.Code);
    }

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
    [InlineData("build_solution")]
    [InlineData("../task_get")]
    [InlineData("TASK_GET")]
    [InlineData("")]
    public async Task RejectsUnknownToolsWithoutNetwork(string name)
    {
        using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network request"));
        using var client = new HttpClient(handler);
        Assert.Equal("unknown_tool", (await new RelayForwarder(client, Options()).ForwardAsync(name, Parse("{}"))).Code);
    }

    [Fact]
    public async Task RejectsNonObjectAndOversizeInputBeforeSending()
    {
        using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network request"));
        using var client = new HttpClient(handler);
        var forwarder = new RelayForwarder(client, Options());
        Assert.Equal("invalid_arguments", (await forwarder.ForwardAsync("task_get", Parse("[]"))).Code);
        Assert.Equal("request_too_large", (await forwarder.ForwardAsync("task_get", JsonSerializer.SerializeToElement(new { text = new string('x', RelayForwarder.MaxRequestBytes) }))).Code);
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        using var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var client = new HttpClient(handler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"), cancel.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "hub_error")]
    [InlineData(HttpStatusCode.Redirect, "redirect_rejected")]
    public async Task ShapesHttpFailuresWithoutLeakingBody(HttpStatusCode status, string code)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("sensitive-server-detail") }));
        using var client = new HttpClient(handler);
        var result = await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"));
        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"ok\":true,\"code\":\"ok\",\"dataJson\":\"not-json\",\"version\":1}")]
    [InlineData("{\"ok\":true,\"code\":\"ok\"}")]
    public async Task RejectsMalformedHubReplies(string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(body)));
        using var client = new HttpClient(handler);
        Assert.Equal("invalid_hub_reply", (await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"))).Code);
    }

    [Fact]
    public async Task BoundsResponseEvenWithoutContentLength()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', RelayForwarder.MaxResponseBytes + 1)))) }));
        using var client = new HttpClient(handler);
        Assert.Equal("response_too_large", (await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"))).Code);
    }

    [Fact]
    public async Task ShapesTransportFailuresWithoutLeakingException()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("client-secret"));
        using var client = new HttpClient(handler);
        Assert.Equal("hub_unavailable", (await new RelayForwarder(client, Options()).ForwardAsync("task_get", Parse("{}"))).Code);
    }

    [Fact]
    public async Task PreservesDomainConflictAndVersionWithoutTurningItIntoSuccess()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json("{\"ok\":false,\"code\":\"version_conflict\",\"dataJson\":\"{\\\"currentVersion\\\":8}\",\"version\":8}")));
        using var client = new HttpClient(handler);
        var result = await new RelayForwarder(client, Options()).ForwardAsync("task_update", Parse("{}"));
        Assert.False(result.Ok);
        Assert.Equal("version_conflict", result.Code);
        Assert.Equal(8, result.Version);
        Assert.Equal(8, result.Data!.Value.GetProperty("currentVersion").GetInt32());
    }

    [Theory]
    [InlineData("http://example.com/hubs/coordination/negotiate")]
    [InlineData("https://127.0.0.1:5271/hubs/coordination/negotiate")]
    [InlineData("http://127.0.0.1:5272/hubs/coordination/negotiate")]
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

    [Fact]
    public void SdkToolDefinitionsUseDirectObjectArgumentsAndOmitIdeTools()
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected network request")));
        var tools = CoordinationTools.Create(new RelayForwarder(client, Options()), false).ToArray();
        // 18 before standing_propose, 19 before search, 20 before attention_respond; the administrator-only standing_put stays out of a worker's list.
        Assert.Equal(21, tools.Length);
        Assert.All(tools, tool => Assert.Equal("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString()));
        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "check_inbox");
        Assert.DoesNotContain(tools, tool => tool.ProtocolTool.Name == "control_request");
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("ftp://localhost")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("https://example.com/path")]
    public void RejectsUnsafeBaseUrls(string url) => Assert.Throws<AdapterConfigurationException>(() => new AdapterOptions(url, "token"));

    [Theory]
    [InlineData("http://localhost:5271")]
    [InlineData("http://127.0.0.1:5271")]
    [InlineData("http://[::1]:5271")]
    [InlineData("https://example.com")]
    public void AcceptsLoopbackOrHttps(string url) => Assert.NotNull(new AdapterOptions(url, "token").BaseUri);

    [Fact]
    public void MissingTokenFailsWithoutPrintingTokenOrPath()
    {
        var error = Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(_ => null));
        Assert.Contains("PKRELAY_TOKEN", error.Message);
        Assert.Throws<AdapterConfigurationException>(() => new AdapterOptions("http://localhost", " \r\n"));
        Assert.Throws<AdapterConfigurationException>(() => new AdapterOptions("http://localhost", "unsafe\r\nvalue"));
    }

    [Fact]
    public void TokenFileHasPrecedenceAndFileErrorDoesNotExposePath()
    {
        var variables = new Dictionary<string, string?> { ["PKRELAY_TOKEN_FILE"] = "private-path", ["PKRELAY_TOKEN"] = "wrong-token" };
        var options = AdapterOptions.FromEnvironment(key => variables.GetValueOrDefault(key), _ => "file-token\n");
        Assert.Equal("file-token", options.Token);
        var error = Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(key => variables.GetValueOrDefault(key), _ => throw new IOException("private-path")));
        Assert.DoesNotContain("private-path", error.Message);
    }

    [Fact]
    public void WorkerSurfaceOmitsAdminAndIdeCapabilities()
    {
        Assert.Contains("task_claim", CoordinationTools.NamesForRole(false));
        Assert.DoesNotContain("control_request", CoordinationTools.NamesForRole(false));
        Assert.Contains("control_request", CoordinationTools.NamesForRole(true));
        Assert.DoesNotContain("build_solution", CoordinationTools.NamesForRole(true));
        using var handler = RelayForwarder.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    private static AdapterOptions Options() => new("http://127.0.0.1:5271", "client-secret");
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>An enrolled loopback node folder with one session handle for <paramref name="chatId"/>.</summary>
    private static string HookNode(string chatId)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pkrelay-hook-" + Guid.NewGuid().ToString("N"));
        using (var seed = PkRelay.Adapter.Node.NodeState.Open(folder)) { seed.SetHub("http://127.0.0.1:5271/", null); seed.SaveCredential(new string('c', 64)); }
        PkRelay.Adapter.Node.NodeState.WriteSessionHandle(folder, new PkRelay.Adapter.Node.SessionHandle("s-hook", "synthetic-token-hook", "repo", "Hook chat", DateTimeOffset.UtcNow.AddMinutes(-1), Chat: new PkRelay.Adapter.Node.ChatLocation("claude", chatId)));
        return folder;
    }

    private static HttpResponseMessage StatusReply(string attention) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = "{\"sessionId\":\"s-hook\",\"attention\":" + attention + "}", version = 0 }), Encoding.UTF8, "application/json")
    };

    /// <summary>Hook input that never arrives, like a client that keeps standard input open.</summary>
    private sealed class NeverEnding : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override Task<int> ReadAsync(char[] buffer, int index, int count) => ReadAsync(buffer.AsMemory(index, count)).AsTask();
    }

    private sealed class HookClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Prompt_hook_prints_one_line_and_reuses_it_for_thirty_seconds()
    {
        var chatId = Guid.NewGuid().ToString();
        var folder = HookNode(chatId);
        try
        {
            var calls = 0;
            string? sent = null;
            using var hub = new StubHandler(async (request, ct) =>
            {
                calls++;
                sent = await request.Content!.ReadAsStringAsync(ct);
                Assert.EndsWith("/get_status", request.RequestUri!.AbsolutePath);
                return StatusReply("{\"actionable\":3,\"urgent\":1,\"inbox\":2,\"standing\":4,\"top\":{\"id\":\"a-91\",\"k\":\"blocked\",\"t\":\"T-12\"},\"wake\":\"a-91\"}");
            });
            var clock = new HookClock();
            var input = JsonSerializer.Serialize(new { session_id = chatId, hook_event_name = "UserPromptSubmit", prompt = "UNTRUSTED PROMPT" });
            var line = await PkRelay.Adapter.Node.PromptHook.LineAsync(input, folder, TimeSpan.FromSeconds(5), hub, clock);
            Assert.Equal("PkRelay: 3 attention items (1 urgent), 2 inbox, standing r4; top a-91 blocked \"T-12\".", line);
            Assert.DoesNotContain('\n', line);
            // It asks as the chat's own session for the detail, and never sends the prompt.
            using (var body = JsonDocument.Parse(sent!))
            {
                Assert.Equal(("s-hook", "synthetic-token-hook", true), (body.RootElement.GetProperty("sessionId").GetString(), body.RootElement.GetProperty("sessionToken").GetString(), body.RootElement.GetProperty("attentionDetail").GetBoolean()));
                Assert.DoesNotContain("UNTRUSTED", sent);
            }
            // Within 30 seconds the cached line is printed without calling the hub; after that the hub is asked again.
            clock.Now += TimeSpan.FromSeconds(29);
            Assert.Equal(line, await PkRelay.Adapter.Node.PromptHook.LineAsync(input, folder, TimeSpan.FromSeconds(5), hub, clock));
            Assert.Equal(1, calls);
            clock.Now += TimeSpan.FromSeconds(2);
            Assert.Equal(line, await PkRelay.Adapter.Node.PromptHook.LineAsync(input, folder, TimeSpan.FromSeconds(5), hub, clock));
            Assert.Equal(2, calls);
            // The whole command prints exactly one line and exits 0.
            using var output = new StringWriter();
            Assert.Equal(0, await PkRelay.Adapter.Node.PromptHook.RunAsync(new StringReader(input), output, folder, hub, clock));
            Assert.Equal(line + Environment.NewLine, output.ToString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Prompt_hook_prints_nothing_on_hub_error_timeout_or_unknown_chat()
    {
        var chatId = Guid.NewGuid().ToString();
        var folder = HookNode(chatId);
        try
        {
            var input = JsonSerializer.Serialize(new { session_id = chatId });
            using var failing = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
            Assert.Equal("", await PkRelay.Adapter.Node.PromptHook.LineAsync(input, folder, TimeSpan.FromSeconds(5), failing));
            // A hub that does not answer within the timeout: nothing is printed, and quickly.
            using var hanging = new StubHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return StatusReply("{}"); });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal("", await PkRelay.Adapter.Node.PromptHook.LineAsync(input, folder, TimeSpan.FromMilliseconds(200), hanging));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            // An older hub without attention, a chat with no session here, and input that is not hook JSON print nothing.
            using var older = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"code\":\"ok\",\"dataJson\":\"{}\",\"version\":0}", Encoding.UTF8, "application/json") }));
            Assert.Equal("", await PkRelay.Adapter.Node.PromptHook.LineAsync(input, folder, TimeSpan.FromSeconds(5), older));
            Assert.Equal("", await PkRelay.Adapter.Node.PromptHook.LineAsync(JsonSerializer.Serialize(new { session_id = Guid.NewGuid().ToString() }), folder, TimeSpan.FromSeconds(5), older));
            Assert.Equal("", await PkRelay.Adapter.Node.PromptHook.LineAsync("not json", folder, TimeSpan.FromSeconds(5), older));
            // Errors are not cached, so the next message asks again.
            Assert.False(Directory.Exists(Path.Combine(folder, "hooks")) && Directory.EnumerateFiles(Path.Combine(folder, "hooks")).Any());
            // Input that never ends shares the one deadline with the hub call: nothing is printed, within about the timeout.
            using (var silent = new StringWriter())
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                Assert.Equal(0, await PkRelay.Adapter.Node.PromptHook.RunAsync(new NeverEnding(), silent, folder, older, null));
                Assert.Equal("", silent.ToString());
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
            }
            Assert.Equal(TimeSpan.FromMilliseconds(1000), PkRelay.Adapter.Node.PromptHook.Timeout(null));
            Assert.Equal(TimeSpan.FromMilliseconds(250), PkRelay.Adapter.Node.PromptHook.Timeout("250"));
            Assert.Equal(TimeSpan.FromMilliseconds(1000), PkRelay.Adapter.Node.PromptHook.Timeout("999999"));
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}

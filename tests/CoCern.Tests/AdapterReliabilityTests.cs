using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using Xunit;

namespace CoCern.Tests;

/// <summary>Regression tests for the adapter's reliability fixes: hint reconnects, bounded body reads and waits, hook input, announce isolation and waiter races.</summary>
public sealed class AdapterReliabilityTests : IDisposable
{
    private const string Relay = "http://127.0.0.1:5271/";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-reliability-" + Guid.NewGuid().ToString("N"));

    public AdapterReliabilityTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public async Task Session_hints_start_after_a_failed_first_attempt_and_restart_after_the_hub_closes()
    {
        var hub = new FakeHub { Available = false };
        var written = new ConcurrentQueue<string>();
        await using (var hints = new SessionHints(new AdapterOptions(Relay, "client-secret", signalR: true), written.Enqueue, hub.Handler) { FirstRetry = TimeSpan.FromMilliseconds(50), MaxRetry = TimeSpan.FromMilliseconds(200) })
        {
            await hints.ObserveSessionAsync(Json("{\"repositoryId\":\"repo\"}"), new AdapterReply(true, "ok", Json("{\"sessionId\":\"s\",\"sessionToken\":\"t\"}")), CancellationToken.None);
            Assert.Contains(written, line => line.Contains("unavailable"));

            // The hub comes back: the hint channel starts and joins the session without another session_open.
            hub.Available = true;
            await WaitFor(() => hub.Watches >= 1);
            // The hub ends the connection for good (as after an outage automatic reconnect gave up on): it is started and joined again.
            var negotiated = hub.Negotiations;
            hub.CloseAll();
            await WaitFor(() => hub.Watches >= 2);
            Assert.True(hub.Negotiations > negotiated);
        }
    }

    [Fact]
    public async Task Session_hints_retry_a_failed_watch_on_a_connection_that_stayed_up()
    {
        var hub = new FakeHub { FailWatches = 1 };
        await using var hints = new SessionHints(new AdapterOptions(Relay, "client-secret", signalR: true), _ => { }, hub.Handler) { FirstRetry = TimeSpan.FromMilliseconds(50) };
        await hints.ObserveSessionAsync(Json("{\"repositoryId\":\"repo\"}"), new AdapterReply(true, "ok", Json("{\"sessionId\":\"s\",\"sessionToken\":\"t\"}")), CancellationToken.None);
        await WaitFor(() => hub.Watches >= 2);
        Assert.Equal(1, hub.Negotiations);
    }

    [Fact]
    public async Task Wake_watcher_retries_a_failed_watch_on_the_next_pass()
    {
        var hub = new FakeHub { FailWatches = 1 };
        var sessionFile = Path.Combine(root, "session.json");
        File.WriteAllText(sessionFile, "{\"sessionId\":\"session\",\"sessionToken\":\"session-secret\"}");
        var stop = Path.Combine(root, "stop");
        var runtime = new WakeRuntime(new AdapterOptions(Relay, "client-secret", signalR: true), Path.Combine(root, "state.db"), stop, Relay) { HubHandler = hub.Handler, Interval = TimeSpan.FromMilliseconds(100) };
        WakeTarget[] targets = [new(new WakeRoute("repo", "board", sessionFile, "00000000-0000-0000-0000-000000000001", Environment.MachineName), new WakeSession("session", "session-secret"), new InboxWaitDispatcher(_ => { }))];
        var run = WakeWatcher.RunAsync(runtime, _ => Task.FromResult<IReadOnlyList<WakeTarget>>(targets), exitWhenQueued: false, TimeSpan.FromSeconds(30), CancellationToken.None, new EmptyInbox());
        await WaitFor(() => hub.Watches >= 2 || run.IsCompleted);
        File.WriteAllText(stop, "");
        Assert.Equal("stopped", await run);
        Assert.True(hub.Watches >= 2);
        Assert.Equal(1, hub.Negotiations);
    }

    [Fact]
    public async Task Forwarder_bounds_a_body_that_stalls_after_the_headers()
    {
        using var client = new HttpClient(new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) })))
        { Timeout = TimeSpan.FromMilliseconds(300) };
        var watch = Stopwatch.StartNew();
        var reply = await new RelayForwarder(client, new AdapterOptions(Relay, "client-secret")).ForwardAsync("check_inbox", Json("{\"repositoryId\":\"repo\"}"));
        Assert.Equal("hub_timeout", reply.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Waiting_check_inbox_returns_the_last_empty_inbox_when_a_poll_hangs()
    {
        var calls = 0;
        using var client = new HttpClient(new Stub(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1) await Task.Delay(Timeout.Infinite, ct);
            return Envelope("{\"items\":[],\"nextCursor\":null,\"truncated\":false,\"total\":0}");
        })) { Timeout = TimeSpan.FromSeconds(60) };
        var forwarder = new RelayForwarder(client, new AdapterOptions(Relay, "client-secret"));
        var watch = Stopwatch.StartNew();
        var reply = await InboxWait.CheckAsync(forwarder, Json("{\"repositoryId\":\"repo\",\"waitSeconds\":1}"), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.True(reply.Ok);
        Assert.Equal(0, reply.Data!.Value.GetProperty("total").GetInt32());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1) + InboxWait.PollMargin + TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prompt_hook_reads_the_session_of_a_long_prompt_and_drains_its_input(bool sessionFirst)
    {
        var chatId = Guid.NewGuid().ToString();
        var folder = Path.Combine(root, "hook");
        using (var seed = NodeState.Open(folder)) { seed.SetHub(Relay, null); seed.SaveCredential(new string('c', 64)); }
        NodeState.WriteSessionHandle(folder, new SessionHandle("s-hook", "synthetic-token-hook", "repo", "Hook chat", DateTimeOffset.UtcNow.AddMinutes(-1), Chat: new ChatLocation("claude", chatId)));
        var prompt = new string('x', 300_000) + "é\"{}";
        var input = sessionFirst
            ? JsonSerializer.Serialize(new { session_id = chatId, hook_event_name = "UserPromptSubmit", prompt })
            : JsonSerializer.Serialize(new { prompt, nested = new { session_id = "not-this" }, session_id = chatId });
        using var hub = new Stub((_, _) => Task.FromResult(Envelope("{\"sessionId\":\"s-hook\",\"attention\":{\"actionable\":1,\"inbox\":0}}")));
        var reader = new StringReader(input);
        using var output = new StringWriter();
        Assert.Equal(0, await PromptHook.RunAsync(reader, output, folder, hub, null));
        Assert.Equal("CoCern: 1 attention item, 0 inbox." + Environment.NewLine, output.ToString());
        Assert.Equal(-1, reader.Peek());
        // Truncated input still names the session when its ID came first.
        Assert.Equal(sessionFirst ? chatId : null, await PromptHook.ReadSessionIdAsync(new StringReader(input[..1000])));
    }

    [Fact]
    public async Task Announce_retries_the_full_list_when_isolating_a_denied_repository_meets_another_failure()
    {
        var folder = Path.Combine(root, "node");
        using var state = NodeState.Open(folder);
        state.SetHub(Relay, null);
        state.SaveCredential(new string('c', 64));
        var announced = new ConcurrentQueue<int>();
        using var hub = new Stub(async (request, ct) =>
        {
            var chats = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["chats"]!.AsArray().Count;
            announced.Enqueue(chats);
            return announced.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"code\":\"RepositoryDenied\"}", Encoding.UTF8, "application/json") },
                2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }
            };
        });
        var transport = new NodeTransport
        {
            CreateHandler = (_, _) => hub, HubHandler = () => new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))), MachineName = "Node PC", Clients = () => [],
            Chats = _ => Task.FromResult(new JsonArray(new JsonObject { ["repositoryId"] = "a", ["relaySessionId"] = "s-a" }, new JsonObject { ["repositoryId"] = "b", ["relaySessionId"] = "s-b" })),
            FirstRetry = TimeSpan.FromMilliseconds(10), CodexExecutable = () => null, SingleCycle = true
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await NodeRunner.RunAsync(state, null, transport, new StringWriter(), timeout.Token));
        // The trial's 503 is the cycle's failure: the next announcement is the whole list again, never the reduced one.
        Assert.Equal(new[] { 2, 1, 2 }, announced.Take(3).ToArray());
    }

    [Fact]
    public async Task Second_claude_waiter_never_touches_the_live_waiters_pid_file()
    {
        var (folder, chatId) = WaiterNode();
        var waiters = Path.Combine(folder, ClaudeWaiter.WaitersFolder);
        Directory.CreateDirectory(waiters);
        // The live waiter holds its exclusive state; its pid file is whatever it wrote.
        using var live = new WakeState(Path.Combine(waiters, chatId + ".db"));
        var pid = Path.Combine(waiters, chatId + ".pid");
        File.WriteAllText(pid, "live waiter");
        await Assert.ThrowsAsync<AdapterConfigurationException>(() => ClaudeWaiter.RunAsync(folder, chatId, _ => { }, TimeSpan.FromSeconds(1), CancellationToken.None, new EmptyInbox(), () => new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));
        Assert.Equal("live waiter", File.ReadAllText(pid));
    }

    [Fact]
    public async Task Claude_waiter_with_ack_rejects_a_chat_that_is_not_a_uuid_with_the_usage_error()
    {
        var (folder, _) = WaiterNode();
        var error = await Assert.ThrowsAsync<AdapterConfigurationException>(() => ClaudeWaiter.RunAsync(folder, "not-a-uuid", _ => { }, TimeSpan.FromSeconds(1), CancellationToken.None, acknowledge: ["m1"]));
        Assert.Contains("UUID", error.Message);
    }

    private (string Folder, string ChatId) WaiterNode()
    {
        var chatId = Guid.NewGuid().ToString();
        var folder = Path.Combine(root, "waiter");
        using (var seed = NodeState.Open(folder)) { seed.SetHub(Relay, null); seed.SaveCredential(new string('c', 64)); }
        NodeState.WriteSessionHandle(folder, new SessionHandle("session", "session-secret", "repo", "Waiter chat", DateTimeOffset.UtcNow.AddMinutes(-1), Chat: new ChatLocation("claude", chatId)));
        return (folder, chatId);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static HttpResponseMessage Envelope(string data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = data, version = 0 }), Encoding.UTF8, "application/json")
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition());
    }

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    /// <summary>A relay with an empty inbox and nothing paused.</summary>
    private sealed class EmptyInbox : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Envelope(request.RequestUri!.AbsolutePath.EndsWith("get_status")
            ? "{\"sessionId\":\"session\",\"control\":{\"paused\":false,\"requests\":[]}}"
            : "{\"items\":[],\"nextCursor\":null,\"truncated\":false,\"total\":0}"));
    }

    /// <summary>A body whose headers arrived but whose bytes never do, like a half-open connection.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A minimal SignalR hub over long polling (JSON protocol): negotiate, handshake, invocation completions and a server close.
    /// It counts negotiations and <c>WatchSession</c> invocations, and fails the first <see cref="FailWatches"/> of them.
    /// </summary>
    private sealed class FakeHub
    {
        private const char Separator = '\u001e';
        private readonly ConcurrentDictionary<string, Connection> _connections = new();
        private int _negotiations, _watches;
        public volatile bool Available = true;
        public int FailWatches;
        public int Negotiations => Volatile.Read(ref _negotiations);
        public int Watches => Volatile.Read(ref _watches);

        public HttpMessageHandler Handler() => new Endpoint(this);

        /// <summary>Ends every connection with a close message that does not allow reconnecting.</summary>
        public void CloseAll() { foreach (var connection in _connections.Values) connection.Outgoing.Writer.TryWrite("{\"type\":7}" + Separator); }

        private sealed class Connection
        {
            public readonly Channel<string> Outgoing = Channel.CreateUnbounded<string>();
            public int Polls;
        }

        private sealed class Endpoint(FakeHub hub) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (!hub.Available) return new(HttpStatusCode.ServiceUnavailable);
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/negotiate"))
                {
                    Interlocked.Increment(ref hub._negotiations);
                    var id = Guid.NewGuid().ToString("N");
                    hub._connections[id] = new Connection();
                    return Text(JsonSerializer.Serialize(new { negotiateVersion = 1, connectionId = id, connectionToken = id, availableTransports = new[] { new { transport = "LongPolling", transferFormats = new[] { "Text", "Binary" } } } }));
                }
                var token = System.Web.HttpUtility.ParseQueryString(uri.Query)["id"];
                if (token is null || !hub._connections.TryGetValue(token, out var connection)) return new(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Delete)
                {
                    connection.Outgoing.Writer.TryComplete();
                    hub._connections.TryRemove(token, out _);
                    return new(HttpStatusCode.Accepted);
                }
                if (request.Method == HttpMethod.Get)
                {
                    // The first poll only confirms the connection.
                    if (Interlocked.Increment(ref connection.Polls) == 1) return Text("");
                    try
                    {
                        var text = new StringBuilder(await connection.Outgoing.Reader.ReadAsync(cancellationToken));
                        while (connection.Outgoing.Reader.TryRead(out var more)) text.Append(more);
                        return Text(text.ToString());
                    }
                    catch (ChannelClosedException) { return new(HttpStatusCode.NoContent); }
                }
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                foreach (var message in body.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
                {
                    using var document = JsonDocument.Parse(message);
                    var root = document.RootElement;
                    if (root.TryGetProperty("protocol", out _)) { connection.Outgoing.Writer.TryWrite("{}" + Separator); continue; }
                    if (!root.TryGetProperty("type", out var type) || type.GetInt32() != 1 || !root.TryGetProperty("invocationId", out var invocation)) continue;
                    var failed = root.GetProperty("target").GetString() == "WatchSession" && Interlocked.Increment(ref hub._watches) <= hub.FailWatches;
                    connection.Outgoing.Writer.TryWrite(JsonSerializer.Serialize(failed
                        ? (object)new { type = 3, invocationId = invocation.GetString(), error = "watch refused" }
                        : new { type = 3, invocationId = invocation.GetString(), result = (object?)null }) + Separator);
                }
                return new(HttpStatusCode.OK);
            }

            private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}

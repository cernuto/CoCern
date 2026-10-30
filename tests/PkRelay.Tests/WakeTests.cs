using System.Net;
using System.Text;
using System.Text.Json;
using PkRelay.Adapter;
using Xunit;

namespace PkRelay.Tests;

public sealed class WakeTests
{
    [Fact]
    public async Task Distinct_messages_wake_once_and_unacknowledged_messages_stay_suppressed()
    {
        using var fixture = new Fixture();
        fixture.Messages = ["first", "second"];
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Single(fixture.Dispatcher.Prompts);
        Assert.DoesNotContain("UNTRUSTED MESSAGE BODY", fixture.Dispatcher.Prompts[0]);
        Assert.DoesNotContain("session-secret", fixture.Dispatcher.Prompts[0]);
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Messages = ["first", "second", "third"];
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Equal(2, fixture.Dispatcher.Prompts.Count);
    }

    [Fact]
    public async Task Empty_inbox_never_wakes()
    {
        using var fixture = new Fixture();
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("missing-pause")]
    [InlineData("cancelled")]
    [InlineData("wrong-session")]
    [InlineData("unauthorized")]
    public async Task Unverified_or_stopped_session_cannot_dispatch(string status)
    {
        using var fixture = new Fixture { Status = status, Messages = ["first"] };
        Assert.NotEqual("queued", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
    }

    [Fact]
    public async Task Cancellation_for_another_session_does_not_block_enrolled_route()
    {
        using var fixture = new Fixture { Status = "other-cancelled", Messages = ["first"] };
        Assert.Equal("queued", await fixture.Reconcile());
    }

    [Fact]
    public async Task Restart_preserves_deduplication_and_uncertain_dispatch_blocks_route()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        Assert.Equal("queued", await fixture.Reconcile());
        fixture.Restart();
        Assert.Equal("empty", await fixture.Reconcile());
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Messages = ["second"];
        fixture.Dispatcher.Accept = false;
        Assert.Equal("uncertain", await fixture.Reconcile());
        fixture.Restart();
        fixture.Clock.Now += TimeSpan.FromMinutes(11);
        fixture.Messages = ["third"];
        Assert.Equal("uncertain", await fixture.Reconcile());
        Assert.Equal(2, fixture.Dispatcher.Prompts.Count);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("uncertain")]
    [InlineData("reserved")]
    public async Task Equivalent_origin_and_changed_route_metadata_cannot_bypass_durable_inbox_state(string status)
    {
        using var fixture = new Fixture { Messages = ["first"] };
        if (status == "reserved")
        {
            var inbox = WakeState.Key("http://127.0.0.1:5271/", "repo", "session");
            Assert.Equal("reserved", fixture.State.Reserve(inbox, ["first"], fixture.Clock.Now).Status);
        }
        else
        {
            fixture.Dispatcher.Accept = status == "queued";
            Assert.Equal(status, await fixture.Reconcile());
        }
        var dispatches = fixture.Dispatcher.Prompts.Count;
        fixture.Restart();
        fixture.Route = fixture.Route with { BoardId = "another-board", MachineId = Environment.MachineName.ToLowerInvariant(), ThreadId = "00000000-0000-0000-0000-000000000002" };
        fixture.Configuration = fixture.Configuration with { RelayUrl = "http://127.0.0.1:5271/", Routes = [fixture.Route] };
        fixture.Clock.Now += TimeSpan.FromMinutes(11);
        fixture.Dispatcher.Accept = true;
        Assert.Equal(status == "queued" ? "empty" : "uncertain", await fixture.Reconcile());
        Assert.Equal(dispatches, fixture.Dispatcher.Prompts.Count);
    }

    [Fact]
    public void State_has_exclusive_process_ownership()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>(() => new WakeState(fixture.Configuration.StatePath));
    }

    [Fact]
    public async Task Pagination_reaches_new_messages_behind_already_notified_pages()
    {
        using var fixture = new Fixture { Messages = ["old"], PageSize = 1 };
        Assert.Equal("queued", await fixture.Reconcile());
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Messages = ["old", "new"];
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Contains(1, fixture.Cursors);
    }

    [Fact]
    public async Task Incomplete_or_oversize_coverage_never_dispatches_partial_batch()
    {
        using var fixture = new Fixture { Messages = Enumerable.Range(0, 21).Select(i => i.ToString()).ToArray(), PageSize = 1 };
        Assert.Equal("coverage-limit", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
        fixture.Messages = ["safe"];
        Assert.Equal("queued", await fixture.Reconcile());
    }

    [Fact]
    public async Task Both_rate_limits_survive_restart_without_reserving_suppressed_messages()
    {
        using var fixture = new Fixture { Messages = ["0"] };
        Assert.Equal("queued", await fixture.Reconcile());
        fixture.Messages = ["early"];
        Assert.Equal("rate-limited", await fixture.Reconcile());
        for (var i = 1; i < 6; i++)
        {
            fixture.Clock.Now += TimeSpan.FromSeconds(11);
            fixture.Messages = [i.ToString()];
            Assert.Equal("queued", await fixture.Reconcile());
        }
        fixture.Restart();
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Messages = ["seventh"];
        Assert.Equal("rate-limited", await fixture.Reconcile());
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        Assert.Equal("queued", await fixture.Reconcile());
    }

    [Fact]
    public async Task Stop_file_and_cancelled_token_prevent_dispatch()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        File.WriteAllText(fixture.Configuration.StopFile, "stop");
        Assert.Equal("stopped", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Reconcile(cancellation.Token));
    }

    [Fact]
    public void Configuration_rejects_nonlocal_routes_relative_paths_and_duplicate_threads()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Configuration.TokenFile, "client-secret");
        File.WriteAllText(fixture.Route.SessionFile, "{\"sessionId\":\"session\",\"sessionToken\":\"session-secret\"}");
        var path = Path.Combine(Path.GetDirectoryName(fixture.Configuration.StatePath)!, "config.json");
        void Write(WakeConfiguration configuration) => File.WriteAllText(path, JsonSerializer.Serialize(configuration, JsonSerializerOptions.Web));
        Write(fixture.Configuration);
        Assert.Single(WakeConfiguration.Load(path).Routes);
        foreach (var route in new[] { fixture.Route with { MachineId = "remote" }, fixture.Route with { ThreadId = "guess-by-title" }, fixture.Route with { SessionFile = "relative.json" } })
        {
            Write(fixture.Configuration with { Routes = [route] });
            Assert.Throws<AdapterConfigurationException>(() => WakeConfiguration.Load(path));
        }
        Write(fixture.Configuration with { Routes = [fixture.Route, fixture.Route] });
        Assert.Throws<AdapterConfigurationException>(() => WakeConfiguration.Load(path));
    }

    [Theory]
    [InlineData(0, "Queued message 11111111-1111-1111-1111-111111111111 for thread 00000000-0000-0000-0000-000000000001.", true)]
    [InlineData(1, "Queued message 11111111-1111-1111-1111-111111111111 for thread 00000000-0000-0000-0000-000000000001.", false)]
    [InlineData(0, "success", false)]
    [InlineData(0, "Queued message 11111111-1111-1111-1111-111111111111 for thread 00000000-0000-0000-0000-000000000002.", false)]
    public void Only_exact_success_receipt_confirms_queued_notification(int exitCode, string output, bool expected)
    {
        Assert.Equal(expected, CodexWakeDispatcher.HasQueuedReceipt(exitCode, output, "00000000-0000-0000-0000-000000000001"));
    }

    [Fact]
    public async Task Crash_after_reservation_blocks_route_on_restart()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        var key = WakeState.Key("http://127.0.0.1:5271/", "repo", "session");
        Assert.Equal("reserved", fixture.State.Reserve(key, ["first"], fixture.Clock.Now).Status);
        fixture.Restart();
        Assert.Equal("uncertain", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
    }

    [Fact]
    public async Task Inbox_waiter_returns_on_a_new_message_and_rearming_does_not_wake_again_for_it()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        fixture.PrepareEnrollment();
        var prompts = new List<string>();
        Assert.Equal("queued", await fixture.Await(prompts, TimeSpan.FromSeconds(30)));
        var prompt = Assert.Single(prompts);
        Assert.Contains("inbox", prompt);
        Assert.DoesNotContain("UNTRUSTED MESSAGE BODY", prompt);
        Assert.DoesNotContain("session-secret", prompt);

        // The session re-arms the waiter after handling; an unacknowledged message already surfaced stays quiet.
        Assert.Equal("timeout", await fixture.Await(prompts, TimeSpan.FromSeconds(2)));
        Assert.Single(prompts);
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Messages = ["first", "second"];
        Assert.Equal("queued", await fixture.Await(prompts, TimeSpan.FromSeconds(30)));
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    public async Task Inbox_waiter_honors_the_stop_file()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        fixture.PrepareEnrollment();
        File.WriteAllText(fixture.Configuration.StopFile, "stop");
        var prompts = new List<string>();
        Assert.Equal("stopped", await fixture.Await(prompts, TimeSpan.FromSeconds(30)));
        Assert.Empty(prompts);
    }

    [Fact]
    public void Inbox_waiter_configuration_needs_no_codex_and_exactly_one_route()
    {
        using var fixture = new Fixture();
        fixture.PrepareEnrollment();
        var path = Path.Combine(Path.GetDirectoryName(fixture.Configuration.StatePath)!, "await.json");
        void Write(WakeConfiguration configuration) => File.WriteAllText(path, JsonSerializer.Serialize(configuration, JsonSerializerOptions.Web));
        Write(fixture.Configuration with { CodexPath = "" });
        Assert.Single(WakeConfiguration.LoadForAwait(path).Routes);
        Assert.Throws<AdapterConfigurationException>(() => WakeConfiguration.Load(path));

        var otherSession = Path.Combine(Path.GetDirectoryName(path)!, "other-session.json");
        File.WriteAllText(otherSession, "{\"sessionId\":\"other\",\"sessionToken\":\"other-secret\"}");
        var second = fixture.Route with { SessionFile = otherSession, ThreadId = "00000000-0000-0000-0000-000000000002" };
        Write(fixture.Configuration with { CodexPath = "", Routes = [fixture.Route, second] });
        Assert.Throws<AdapterConfigurationException>(() => WakeConfiguration.LoadForAwait(path));
    }

    [Fact]
    public async Task Routes_added_while_running_are_reconciled_without_restart()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        fixture.PrepareEnrollment();
        var current = Array.Empty<WakeTarget>();
        var reads = 0;
        var nudge = new WakeNudge();
        var run = WakeWatcher.RunAsync(fixture.Runtime(nudge), _ => { Interlocked.Increment(ref reads); return Task.FromResult<IReadOnlyList<WakeTarget>>(Volatile.Read(ref current)); },
            exitWhenQueued: true, TimeSpan.FromSeconds(30), CancellationToken.None, fixture.CreateHandler(), fixture.Clock);
        await WaitFor(() => Volatile.Read(ref reads) >= 1, run);
        Assert.Empty(fixture.Dispatcher.Prompts);

        // A route discovered after start is picked up on the next pass; nothing restarts.
        Volatile.Write(ref current, [new WakeTarget(fixture.Route, new("session", "session-secret"), fixture.Dispatcher)]);
        nudge.Pulse();
        Assert.Equal("queued", await run);
        Assert.Single(fixture.Dispatcher.Prompts);
    }

    [Fact]
    public async Task Removed_route_is_no_longer_woken()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        fixture.PrepareEnrollment();
        WakeTarget[] current = [new WakeTarget(fixture.Route, new("session", "session-secret"), fixture.Dispatcher)];
        var reads = 0;
        var run = WakeWatcher.RunAsync(fixture.Runtime(new WakeNudge()) with { Interval = TimeSpan.FromMilliseconds(100) }, _ => { Interlocked.Increment(ref reads); return Task.FromResult<IReadOnlyList<WakeTarget>>(Volatile.Read(ref current)); },
            exitWhenQueued: false, TimeSpan.FromSeconds(60), CancellationToken.None, fixture.CreateHandler(), fixture.Clock);
        await WaitFor(() => fixture.Dispatcher.Prompts.Count == 1, run);

        Volatile.Write(ref current, []);
        var seen = Volatile.Read(ref reads);
        await WaitFor(() => Volatile.Read(ref reads) > seen, run);
        var checks = fixture.Cursors.Count;
        // A new message that would wake a live route, outside both rate limits.
        fixture.Clock.Now += TimeSpan.FromMinutes(11);
        fixture.Messages = ["first", "second"];
        seen = Volatile.Read(ref reads);
        await WaitFor(() => Volatile.Read(ref reads) >= seen + 3, run);
        Assert.Equal(checks, fixture.Cursors.Count);
        Assert.Single(fixture.Dispatcher.Prompts);

        File.WriteAllText(fixture.Configuration.StopFile, "stop");
        Assert.Equal("stopped", await run);
    }

    private static async Task WaitFor(Func<bool> condition, Task run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            if (run.IsCompleted) await run;
            await Task.Delay(25);
        }
        Assert.True(condition());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pkrelay-wake-" + Guid.NewGuid().ToString("N"));
        public readonly Clock Clock = new();
        public readonly Dispatch Dispatcher = new();
        public WakeRoute Route;
        public WakeConfiguration Configuration;
        public WakeState State;
        private readonly HttpClient _client;
        private readonly RelayForwarder _forwarder;
        public string[] Messages = [];
        public string Status = "ok";
        public int PageSize = 50;
        public readonly List<int> Cursors = [];
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Route = new("repo", "board", Path.Combine(_directory, "session.json"), "00000000-0000-0000-0000-000000000001", Environment.MachineName);
            Configuration = new("http://127.0.0.1:5271", Path.Combine(_directory, "token"), Environment.ProcessPath!, Path.Combine(_directory, "state.db"), Path.Combine(_directory, "stop"), [Route]);
            State = new(Configuration.StatePath);
            _client = new(new Handler(this));
            _forwarder = new(_client, new AdapterOptions(Configuration.RelayUrl, "client-secret"));
        }
        public Task<string> Reconcile(CancellationToken cancellationToken = default) => new WakeReconciler(Configuration, State, _forwarder, Dispatcher, Clock).ReconcileAsync(Route, new("session", "session-secret"), cancellationToken);
        public void Restart() { State.Dispose(); State = new(Configuration.StatePath); }

        /// <summary>Writes the token and session files a real enrollment has, and releases the state lock so a waiter can own it.</summary>
        public void PrepareEnrollment()
        {
            File.WriteAllText(Configuration.TokenFile, "client-secret");
            File.WriteAllText(Route.SessionFile, "{\"sessionId\":\"session\",\"sessionToken\":\"session-secret\"}");
            State.Dispose();
        }

        public HttpMessageHandler CreateHandler() => new Handler(this);

        /// <summary>The same enrollment as a runtime; the hint channel answers 404 so tests never reach a real hub.</summary>
        public WakeRuntime Runtime(WakeNudge nudge) => new(new AdapterOptions(Configuration.RelayUrl, "client-secret", signalR: true), Configuration.StatePath, Configuration.StopFile, Configuration.RelayUrl)
        {
            Nudge = nudge, HubHandler = () => new NoHub()
        };

        public Task<string> Await(List<string> prompts, TimeSpan timeout) =>
            WakeWatcher.RunAsync(Configuration, new InboxWaitDispatcher(prompts.Add), exitWhenQueued: true, timeout, CancellationToken.None, new Handler(this), Clock);
        public void Dispose() { State.Dispose(); _client.Dispose(); Directory.Delete(_directory, true); }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("client-secret", request.Headers.Authorization?.Parameter);
                using var args = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("session-secret", args.RootElement.GetProperty("sessionToken").GetString());
                if (fixture.Status == "unauthorized") return new(HttpStatusCode.Unauthorized);
                var cursor = args.RootElement.TryGetProperty("cursor", out var value) ? value.GetInt32() : 0;
                var items = fixture.Messages.Skip(cursor).Take(fixture.PageSize).Select(id => new { messageId = id, repositoryId = "repo", text = "UNTRUSTED MESSAGE BODY" }).ToArray();
                var more = cursor + items.Length < fixture.Messages.Length;
                if (request.RequestUri!.AbsolutePath.EndsWith("check_inbox")) fixture.Cursors.Add(cursor);
                var data = request.RequestUri.AbsolutePath.EndsWith("get_status")
                    ? fixture.Status == "missing-pause" ? "{\"sessionId\":\"session\",\"control\":{}}" : JsonSerializer.Serialize(new { sessionId = fixture.Status == "wrong-session" ? "other" : "session", control = new { paused = fixture.Status == "paused", requests = fixture.Status.EndsWith("cancelled") ? new object[] { new { target = fixture.Status == "other-cancelled" ? "other:" : "session:", request = new { status = "requested_unknown" } } } : [] } })
                    : JsonSerializer.Serialize(new { items, nextCursor = more ? (int?)(cursor + items.Length) : null, truncated = more, total = fixture.Messages.Length });
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = data, version = 0 }), Encoding.UTF8, "application/json") };
            }
        }
    }
    private sealed class NoHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Dispatch : IWakeDispatcher
    {
        public readonly List<string> Prompts = [];
        public bool Accept = true;
        public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken cancellationToken) { Prompts.Add(prompt); return Task.FromResult(Accept); }
    }
}

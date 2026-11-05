using System.Net;
using System.Text;
using System.Text.Json;
using CoCern.Adapter;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

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
    public async Task An_unchanged_status_answer_keeps_the_pause_its_tag_was_read_with()
    {
        using var fixture = new Fixture { StatusTags = true, Status = "paused", Messages = ["first"] };
        Assert.Equal("control-blocked", await fixture.Reconcile());
        Assert.Equal("control-blocked", await fixture.Reconcile());
        Assert.Contains(fixture.CallBodies, body => body.Contains("\"statusTag\":\"") && !body.Contains("\"statusTag\":\"\""));
        Assert.Empty(fixture.Dispatcher.Prompts);

        // The unpaused status has a different tag, so the decision is taken again.
        fixture.Status = "ok";
        Assert.Equal("queued", await fixture.Reconcile());
    }

    [Theory]
    [InlineData("wrong-session")]
    [InlineData("unauthorized")]
    public async Task Unverified_or_stopped_session_cannot_dispatch(string status)
    {
        using var fixture = new Fixture { Status = status, Messages = ["first"] };
        Assert.NotEqual("queued", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
    }

    [Fact]
    public async Task Restart_preserves_deduplication_and_uncertain_dispatch_holds_the_route_then_never_resends()
    {
        using var fixture = new Fixture { Messages = ["first"] };
        Assert.Equal("queued", await fixture.Reconcile());
        fixture.Restart();
        Assert.Equal("empty", await fixture.Reconcile());
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Messages = ["second"];
        fixture.Dispatcher.Accept = false;
        Assert.Equal("uncertain", await fixture.Reconcile());
        // Within the hold, even after a restart, the route stays held and nothing is dispatched.
        fixture.Restart();
        fixture.Clock.Now += TimeSpan.FromMinutes(5);
        fixture.Messages = ["second", "third"];
        fixture.Dispatcher.Accept = true;
        Assert.Equal("uncertain", await fixture.Reconcile());
        Assert.Equal(2, fixture.Dispatcher.Prompts.Count);
        // After it, new mail wakes the chat again (the uncertain wake's own message stays seen: see the test below).
        fixture.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Equal(3, fixture.Dispatcher.Prompts.Count);
    }

    [Theory]
    [InlineData("queued")]
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
        fixture.Dispatcher.Accept = true;
        // Within an uncertain wake's hold the route stays held; after it, the same message is still never sent again.
        fixture.Clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(status == "queued" ? "empty" : "uncertain", await fixture.Reconcile());
        fixture.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Equal(dispatches, fixture.Dispatcher.Prompts.Count);
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
    [InlineData(0, "success", false)]
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
    public void Printed_messages_are_remembered_per_route_across_a_restart()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "state.db");
        using (var state = new WakeState(path))
        {
            Assert.Empty(state.Printed("route", ["m1", "m2"]));
            state.MarkPrinted("route", ["m1"]);
            state.MarkPrinted("route", ["m1"]);
            Assert.Equal(["m1"], state.Printed("route", ["m1", "m2"]));
            Assert.Empty(state.Printed("other", ["m1"]));
        }
        using var reopened = new WakeState(path);
        Assert.Equal(["m1"], reopened.Printed("route", ["m1", "m2"]));
    }

    [Fact]
    public void A_hook_reads_and_records_the_ledger_while_a_waiter_holds_the_state_for_its_whole_wait()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "state.db");
        using var waiter = new WakeState(path);
        // The trap: the waiter's ownership still shuts out anyone who opens the state.
        Assert.Throws<IOException>(() => new WakeState(path));
        waiter.MarkPrinted("route", ["m1"]);
        Assert.Equal(["m1"], WakeState.ReadPrinted(path, "route", ["m1", "m2"]));
        Assert.Empty(WakeState.ReadPrinted(path, "other", ["m1"]));
        // What a hook records, the holding waiter sees, and neither repeats what the other printed.
        WakeState.RecordPrinted(path, "route", ["m2", "m2"]);
        Assert.Equal(["m1", "m2"], waiter.Printed("route", ["m1", "m2"]).Order(StringComparer.Ordinal));
        Assert.Equal(["m1", "m2"], WakeState.ReadPrinted(path, "route", ["m1", "m2", "m3"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_database_from_before_printed_messages_upgrades_in_place_and_keeps_its_wakes()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "state.db");
        var start = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var seen = WakeState.Key("m1");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = $"CREATE TABLE batches (route TEXT NOT NULL, batch TEXT PRIMARY KEY, status TEXT NOT NULL, at INTEGER NOT NULL); CREATE TABLE messages (route TEXT NOT NULL, message TEXT NOT NULL, batch TEXT NOT NULL, PRIMARY KEY(route,message)); INSERT INTO batches VALUES('route','b1','queued',{start.ToUnixTimeMilliseconds()}); INSERT INTO messages VALUES('route','{seen}','b1');";
            command.ExecuteNonQuery();
        }
        using var state = new WakeState(path);
        // A message that woke the route before the upgrade still does not wake it again, and has not counted as printed.
        Assert.Equal("empty", state.Reserve("route", ["m1"], start + TimeSpan.FromMinutes(1)).Status);
        Assert.Empty(state.Printed("route", ["m1"]));
        state.MarkPrinted("route", ["m1"]);
        Assert.Equal(["m1"], state.Printed("route", ["m1"]));
    }

    private sealed class TempDirectory : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocern-wake-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cocern-wake-" + Guid.NewGuid().ToString("N"));
        /// <summary>The limits the rate-limit tests were written against: one wake per 10 s and six per 10 minutes.</summary>
        public static readonly WakeLimits Strict = new(TimeSpan.FromSeconds(10), 6, TimeSpan.FromMinutes(10));
        public readonly Clock Clock = new();
        public readonly Dispatch Dispatcher = new();
        public WakeRoute Route;
        public WakeConfiguration Configuration;
        public WakeState State;
        private readonly HttpClient _client;
        private readonly RelayForwarder _forwarder;
        public string[] Messages = [];
        /// <summary>Messages sent with wake=false.</summary>
        public readonly HashSet<string> Quiet = [];
        public string Status = "ok";
        /// <summary>The hub's attention block on get_status, as JSON, or null for a hub with nothing waiting.</summary>
        public string? Attention;
        /// <summary>Pauses the session on the status read that asks for no attention detail, the one just before a reservation.</summary>
        public bool PauseSecondRead;
        /// <summary>Answers like a hub with status tags: a matching tag gets only {unchanged, statusTag}, and the tag covers what the reply would say.</summary>
        public bool StatusTags;
        public int PageSize = 50;
        public readonly List<int> Cursors = [];
        /// <summary>The body of every check_inbox request.</summary>
        public readonly List<string> InboxBodies = [];
        public readonly List<string> CallBodies = [];
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Route = new("repo", "board", Path.Combine(_directory, "session.json"), "00000000-0000-0000-0000-000000000001", Environment.MachineName);
            Configuration = new("http://127.0.0.1:5271", Path.Combine(_directory, "token"), Environment.ProcessPath!, Path.Combine(_directory, "state.db"), Path.Combine(_directory, "stop"), [Route], new WakeLimitSettings(10, 6, 10));
            State = new(Configuration.StatePath, Strict);
            _client = new(new Handler(this));
            _forwarder = new(_client, new AdapterOptions(Configuration.RelayUrl, "client-secret"));
        }
        /// <summary>What the watcher keeps between passes; every reconcile here shares it, as the watcher's loop does.</summary>
        public readonly InboxCoverage Coverage = new();
        /// <summary>The inbox etag the fake hub gives header reads: it changes with the messages and their wake flags.</summary>
        public string Etag => RelayEtag.Of(string.Join(",", Messages.Select(id => id + (Quiet.Contains(id) ? ":quiet" : ""))));
        public Task<string> Reconcile(CancellationToken cancellationToken = default) => new WakeReconciler(Configuration, State, _forwarder, Dispatcher, Clock, Coverage).ReconcileAsync(Route, new("session", "session-secret"), cancellationToken);
        public int StatusReads => CallBodies.Count(body => !InboxBodies.Contains(body));
        public void Restart() { State.Dispose(); State = new(Configuration.StatePath, Strict); }

        /// <summary>Writes the token and session files a real enrollment has, and releases the state lock so a waiter can own it.</summary>
        public void PrepareEnrollment()
        {
            File.WriteAllText(Configuration.TokenFile, "client-secret");
            File.WriteAllText(Route.SessionFile, "{\"sessionId\":\"session\",\"sessionToken\":\"session-secret\"}");
            State.Dispose();
        }

        /// <summary>The same enrollment as a runtime; the hint channel answers 404 so tests never reach a real hub.</summary>
        public WakeRuntime Runtime(WakeNudge nudge) => new(new AdapterOptions(Configuration.RelayUrl, "client-secret", signalR: true), Configuration.StatePath, Configuration.StopFile, Configuration.RelayUrl)
        {
            Nudge = nudge, HubHandler = () => new NoHub(), Limits = Strict
        };

        public Task<string> Await(List<string> prompts, TimeSpan timeout)
        {
            var target = new WakeTarget(Route, WakeConfiguration.ReadSession(Route), new InboxWaitDispatcher(prompts.Add));
            return WakeWatcher.RunAsync(Runtime(new WakeNudge()), _ => Task.FromResult<IReadOnlyList<WakeTarget>>([target]),
                exitWhenQueued: true, timeout, CancellationToken.None, new Handler(this), Clock);
        }
        public void Dispose() { State.Dispose(); _client.Dispose(); Directory.Delete(_directory, true); }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("client-secret", request.Headers.Authorization?.Parameter);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                fixture.CallBodies.Add(body);
                if (request.RequestUri!.AbsolutePath.EndsWith("check_inbox")) fixture.InboxBodies.Add(body);
                using var args = JsonDocument.Parse(body);
                Assert.Equal("session-secret", args.RootElement.GetProperty("sessionToken").GetString());
                if (fixture.Status == "unauthorized") return new(HttpStatusCode.Unauthorized);
                var cursor = args.RootElement.TryGetProperty("cursor", out var value) ? value.GetInt32() : 0;
                var items = fixture.Messages.Skip(cursor).Take(fixture.PageSize).Select(id => new { messageId = id, repositoryId = "repo", text = "UNTRUSTED MESSAGE BODY", wake = fixture.Quiet.Contains(id) ? false : (bool?)null }).ToArray();
                var more = cursor + items.Length < fixture.Messages.Length;
                if (request.RequestUri!.AbsolutePath.EndsWith("check_inbox")) fixture.Cursors.Add(cursor);
                var headersOnly = args.RootElement.TryGetProperty("headersOnly", out var headers) && headers.GetBoolean();
                var known = args.RootElement.TryGetProperty("knownInboxEtag", out var knownEtag) ? knownEtag.GetString() : null;
                var data = request.RequestUri.AbsolutePath.EndsWith("get_status")
                    ? fixture.Status == "missing-pause" ? "{\"sessionId\":\"session\",\"control\":{}}" : JsonSerializer.Serialize(new { sessionId = fixture.Status == "wrong-session" ? "other" : "session", control = new { paused = fixture.Status == "paused" || (fixture.PauseSecondRead && !args.RootElement.TryGetProperty("attentionDetail", out _)), requests = fixture.Status.EndsWith("cancelled") ? new object[] { new { target = fixture.Status == "other-cancelled" ? "other:" : "session:", request = new { status = "requested_unknown" } } } : [] } })
                    : headersOnly && known == fixture.Etag ? JsonSerializer.Serialize(new { unchanged = true, etag = fixture.Etag, total = fixture.Messages.Length })
                    : headersOnly ? JsonSerializer.Serialize(new { items = items.Select(item => new { item.messageId, item.repositoryId, item.wake }), nextCursor = more ? (int?)(cursor + items.Length) : null, truncated = more, total = fixture.Messages.Length, etag = fixture.Etag })
                    : JsonSerializer.Serialize(new { items, nextCursor = more ? (int?)(cursor + items.Length) : null, truncated = more, total = fixture.Messages.Length });
                // The hub gives its attention block only to a reader that asks for the detail.
                if (request.RequestUri.AbsolutePath.EndsWith("get_status") && fixture.Attention is { } attention && args.RootElement.TryGetProperty("attentionDetail", out var detail) && detail.GetBoolean())
                {
                    var withAttention = System.Text.Json.Nodes.JsonNode.Parse(data)!.AsObject(); withAttention["attention"] = System.Text.Json.Nodes.JsonNode.Parse(attention);
                    data = withAttention.ToJsonString();
                }
                if (fixture.StatusTags && request.RequestUri.AbsolutePath.EndsWith("get_status") && args.RootElement.TryGetProperty("statusTag", out var sent))
                {
                    var tag = RelayEtag.Of(data);
                    var tagged = System.Text.Json.Nodes.JsonNode.Parse(data)!.AsObject(); tagged["statusTag"] = tag;
                    data = sent.GetString() == tag ? JsonSerializer.Serialize(new { unchanged = true, statusTag = tag }) : tagged.ToJsonString();
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = data, version = 0 }), Encoding.UTF8, "application/json") };
            }
        }
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
        public bool Busy;
        public int BusyChecks;
        public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken cancellationToken) { Prompts.Add(prompt); return Task.FromResult(Accept); }
        public bool IsBusy(string threadId) { BusyChecks++; return Busy; }
    }

    [Fact]
    public async Task Top_tier_attention_wakes_once_per_item_and_lower_tiers_wait()
    {
        using var fixture = new Fixture();
        // Lower-tier items only: the hub names no wake candidate, so nothing wakes.
        fixture.Attention = "{\"actionable\":2,\"urgent\":0,\"top\":{\"id\":\"a-1\",\"k\":\"review_ready\",\"t\":\"T-1\"}}";
        Assert.Equal("empty", await fixture.Reconcile());
        // A top-tier item wakes the chat once, with a line naming it; the fixed prompt is otherwise unchanged.
        fixture.Attention = "{\"actionable\":3,\"urgent\":1,\"top\":{\"id\":\"a-2\",\"k\":\"blocked\",\"t\":\"T-1\"},\"wake\":\"a-2\"}";
        Assert.Equal("queued", await fixture.Reconcile());
        var prompt = Assert.Single(fixture.Dispatcher.Prompts);
        Assert.Contains("CoCern attention: 3 items wait; top a-2 blocked on task \"T-1\".", prompt);
        Assert.StartsWith("CoCern inbox wake", prompt);
        Assert.Equal("empty", await fixture.Reconcile());
        // A new candidate wakes again; a malformed one never does.
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Attention = "{\"actionable\":1,\"wake\":\"a-3\"}";
        Assert.Equal("queued", await fixture.Reconcile());
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Attention = "{\"actionable\":1,\"wake\":\"../../etc\"}";
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Equal(2, fixture.Dispatcher.Prompts.Count);
    }
}

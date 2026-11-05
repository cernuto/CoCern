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
    public async Task Empty_inbox_never_wakes()
    {
        using var fixture = new Fixture();
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
        // One status read and one inbox read: with nothing that may wake, the controls are not read a second time.
        Assert.Equal(2, fixture.CallBodies.Count);
        Assert.All(fixture.CallBodies, body => { using var json = JsonDocument.Parse(body); Assert.True(json.RootElement.GetProperty("backgroundCall").GetBoolean()); });
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
        fixture.Dispatcher.Accept = true;
        // Within an uncertain wake's hold the route stays held; after it, the same message is still never sent again.
        fixture.Clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(status == "queued" ? "empty" : "uncertain", await fixture.Reconcile());
        fixture.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal("empty", await fixture.Reconcile());
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

    [Fact]
    public async Task A_delivered_wake_candidate_costs_one_status_read_and_a_new_one_still_gets_both()
    {
        using var fixture = new Fixture();
        fixture.Attention = "{\"actionable\":1,\"top\":{\"id\":\"a-1\",\"k\":\"blocked\"},\"wake\":\"a-1\"}";
        Assert.Equal("queued", await fixture.Reconcile());
        var before = fixture.StatusReads;
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Equal(before + 1, fixture.StatusReads);
        // Anything that would dispatch still pays for the hub's controls a second time.
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        fixture.Attention = "{\"actionable\":1,\"top\":{\"id\":\"a-2\",\"k\":\"blocked\"},\"wake\":\"a-2\"}";
        before = fixture.StatusReads;
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Equal(before + 2, fixture.StatusReads);
        Assert.Equal(2, fixture.Dispatcher.Prompts.Count);
    }

    [Fact]
    public async Task A_delivered_message_costs_one_status_read_with_attention_off()
    {
        using var fixture = new Fixture();
        fixture.Messages = ["m1"];
        Assert.Equal("queued", await fixture.Reconcile());
        var before = fixture.StatusReads;
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Equal(before + 1, fixture.StatusReads);
    }

    [Fact]
    public async Task A_new_candidate_is_held_by_a_pause_only_the_second_status_read_sees()
    {
        using var fixture = new Fixture();
        fixture.Attention = "{\"actionable\":1,\"top\":{\"id\":\"a-1\",\"k\":\"blocked\"},\"wake\":\"a-1\"}";
        fixture.PauseSecondRead = true;
        Assert.Equal("control-blocked", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
        // Nothing was reserved, so it wakes the chat once the pause lifts.
        fixture.PauseSecondRead = false;
        Assert.Equal("queued", await fixture.Reconcile());
    }

    [Fact]
    public void Checking_for_unseen_messages_reserves_nothing()
    {
        using var directory = new TempDirectory();
        using var state = new WakeState(Path.Combine(directory.Path, "state.db"));
        var start = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        Assert.True(state.HasUnseen("route", ["a"]));
        // The check left no trace: the message still reserves.
        Assert.Equal("reserved", Wake(state, "a", start));
        Assert.False(state.HasUnseen("route", ["a"]));
        Assert.True(state.HasUnseen("route", ["a", "b"]));
        Assert.True(state.HasUnseen("other", ["a"]));
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
    public void A_ledger_that_does_not_exist_yet_has_printed_nothing_and_a_hook_can_start_it()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "new", "state.db");
        Assert.Empty(WakeState.ReadPrinted(path, "route", ["m1"]));
        Assert.False(File.Exists(path));
        WakeState.RecordPrinted(path, "route", ["m1"]);
        Assert.Equal(["m1"], WakeState.ReadPrinted(path, "route", ["m1"]));
        // A waiter that starts afterwards finds what the hook recorded.
        using var waiter = new WakeState(path);
        Assert.Equal(["m1"], waiter.Printed("route", ["m1"]));
    }

    [Fact]
    public void A_database_from_before_the_ledger_has_printed_nothing_and_takes_a_hooks_record()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "state.db");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = "CREATE TABLE batches (route TEXT NOT NULL, batch TEXT PRIMARY KEY, status TEXT NOT NULL, at INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }
        Assert.Empty(WakeState.ReadPrinted(path, "route", ["m1"]));
        WakeState.RecordPrinted(path, "route", ["m1"]);
        Assert.Equal(["m1"], WakeState.ReadPrinted(path, "route", ["m1"]));
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

    [Fact]
    public void Default_limits_allow_a_wake_three_seconds_after_the_previous_one_and_refuse_two()
    {
        using var directory = new TempDirectory();
        using var state = new WakeState(Path.Combine(directory.Path, "state.db"));
        var start = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("reserved", Wake(state, "a", start));
        Assert.Equal("rate-limited", Wake(state, "b", start + TimeSpan.FromSeconds(2)));
        Assert.Equal("reserved", Wake(state, "b", start + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Default_limits_refuse_the_twenty_first_wake_in_ten_minutes_and_allow_it_after_the_window()
    {
        using var directory = new TempDirectory();
        using var state = new WakeState(Path.Combine(directory.Path, "state.db"));
        var start = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 20; i++) Assert.Equal("reserved", Wake(state, i.ToString(), start + TimeSpan.FromSeconds(4 * i)));
        Assert.Equal("rate-limited", Wake(state, "extra", start + TimeSpan.FromSeconds(80)));
        Assert.Equal("reserved", Wake(state, "extra", start + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Wake_limits_in_the_configuration_are_optional_per_field_and_default_when_absent()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Configuration.TokenFile, "client-secret");
        File.WriteAllText(fixture.Route.SessionFile, "{\"sessionId\":\"session\",\"sessionToken\":\"session-secret\"}");
        var path = Path.Combine(Path.GetDirectoryName(fixture.Configuration.StatePath)!, "limits.json");
        void Write(WakeLimitSettings? limits) => File.WriteAllText(path, JsonSerializer.Serialize(fixture.Configuration with { Limits = limits }, JsonSerializerOptions.Web));

        Write(null);
        Assert.Equal(WakeLimits.Default, WakeConfiguration.Load(path).ResolvedLimits());
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("wakeLimits");
        File.WriteAllText(path, node.ToJsonString());
        Assert.Equal(WakeLimits.Default, WakeConfiguration.Load(path).ResolvedLimits());

        Write(new WakeLimitSettings(1, 50, 5));
        Assert.Equal(new WakeLimits(TimeSpan.FromSeconds(1), 50, TimeSpan.FromMinutes(5)), WakeConfiguration.Load(path).ResolvedLimits());
        Write(new WakeLimitSettings(null, 7, null));
        Assert.Equal(new WakeLimits(WakeLimits.Default.MinInterval, 7, WakeLimits.Default.Window), WakeConfiguration.Load(path).ResolvedLimits());
        Write(new WakeLimitSettings(0, 1, 1));
        Assert.Equal(new WakeLimits(TimeSpan.Zero, 1, TimeSpan.FromMinutes(1)), WakeConfiguration.Load(path).ResolvedLimits());
        Write(new WakeLimitSettings(3600, 1000, 1440));
        Assert.Equal(new WakeLimits(TimeSpan.FromSeconds(3600), 1000, TimeSpan.FromMinutes(1440)), WakeConfiguration.Load(path).ResolvedLimits());

        foreach (var invalid in new[] { new WakeLimitSettings(-1, null, null), new(3601, null, null), new(null, 0, null), new(null, 1001, null), new(null, null, 0), new(null, null, 1441) })
        {
            Write(invalid);
            Assert.Throws<AdapterConfigurationException>(() => WakeConfiguration.Load(path));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(fixture.Configuration, JsonSerializerOptions.Web).Replace("\"wakeLimits\":{", "\"wakeLimits\":{\"bogus\":1,"));
        Assert.Throws<AdapterConfigurationException>(() => WakeConfiguration.Load(path));
    }

    private static string Wake(WakeState state, string id, DateTimeOffset at)
    {
        var (status, batch) = state.Reserve("route", [id], at);
        if (batch is not null) state.Complete(batch, queued: true);
        return status;
    }

    private sealed class TempDirectory : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocern-wake-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
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

        public HttpMessageHandler CreateHandler() => new Handler(this);

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

    [Fact]
    public async Task With_attention_on_messages_wait_and_only_the_hubs_wake_candidate_wakes()
    {
        using var fixture = new Fixture();
        // A hub with attention on answers with an attention block; a message alone no longer wakes the chat.
        fixture.Attention = "{\"actionable\":2}";
        fixture.Messages = ["first", "second"];
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
        // The idle-hold wake has one ID per idle period, so several waiting messages and items wake the chat once.
        fixture.Attention = "{\"actionable\":3,\"top\":{\"id\":\"a-4\",\"k\":\"info\"},\"wake\":\"hold:638000000000000000\"}";
        Assert.Equal("queued", await fixture.Reconcile());
        fixture.Messages = ["first", "second", "third"];
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Contains("CoCern attention: 3 items wait; top a-4 info.", Assert.Single(fixture.Dispatcher.Prompts));
        // After the chat's next boundary a new idle period may wake it once more.
        fixture.Attention = "{\"actionable\":3,\"wake\":\"hold:638000000000000001\"}";
        Assert.Equal("queued", await fixture.Reconcile());
        // With attention on, only the hub's wake candidate can wake the chat, so the watcher never pages the inbox at all.
        Assert.Empty(fixture.InboxBodies);
    }

    [Fact]
    public async Task With_attention_on_a_pass_is_one_status_read_however_many_messages_wait()
    {
        using var fixture = new Fixture();
        fixture.Attention = "{\"actionable\":150}";
        fixture.Messages = Enumerable.Range(1, 150).Select(n => $"m{n}").ToArray();
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Empty(fixture.InboxBodies);
        Assert.Equal(1, fixture.StatusReads);
    }

    [Fact]
    public async Task With_attention_off_inbox_reads_are_headers_only_and_a_pass_with_nothing_to_wake_skips_the_second_status_read()
    {
        using var fixture = new Fixture();
        fixture.Messages = ["quiet-1", "quiet-2"];
        fixture.Quiet.UnionWith(fixture.Messages);
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.NotEmpty(fixture.InboxBodies);
        Assert.All(fixture.InboxBodies, body => { Assert.Contains("\"headersOnly\":true", body); Assert.Contains("\"attention\":false", body); });
        Assert.Equal(1, fixture.StatusReads);
        Assert.Empty(fixture.Dispatcher.Prompts);
    }

    [Fact]
    public async Task With_attention_off_an_unchanged_inbox_costs_one_tiny_read_and_still_wakes_for_a_new_message()
    {
        using var fixture = new Fixture();
        fixture.PageSize = 2;
        fixture.Messages = ["m1", "m2", "m3"];
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Equal([0, 2], fixture.Cursors);
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        // Unchanged: the first page's etag answers alone, no page is read and nothing wakes again.
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Equal([0, 2, 0], fixture.Cursors);
        Assert.Contains($"\"knownInboxEtag\":\"{fixture.Etag}\"", fixture.InboxBodies[^1]);
        // A new message changes the etag: the inbox is read again and the new message wakes the chat.
        fixture.Messages = ["m1", "m2", "m3", "m4"];
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Equal(2, fixture.Dispatcher.Prompts.Count);
    }

    [Fact]
    public void Hub_names_top_tier_items_at_once_and_an_idle_chat_gets_one_wake_after_the_hold()
    {
        var session = new CoCern.Domain.StateEnvelope<CoCern.Domain.CoordinationDocument>();
        var caller = new CoCern.Domain.RelayCaller("alice", false, ["repo"], true);
        var start = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        System.Text.Json.Nodes.JsonObject Apply(string operation, System.Text.Json.Nodes.JsonObject args, TimeSpan at)
        {
            args["repositoryId"] = "repo"; args["sessionId"] = "s1"; args["sessionToken"] = "secret";
            var reply = CoCern.Domain.CoordinationStateMachine.Apply(session, "session", "s1", caller, operation, args, start + at);
            Assert.True(reply.Ok, reply.Code);
            return System.Text.Json.Nodes.JsonNode.Parse(reply.DataJson)!.AsObject();
        }
        string? Wake(TimeSpan at) => Apply("get_status", new() { ["attentionStatus"] = "detail", ["maxHoldMinutes"] = 10 }, at)["attention"]!["wake"]?.GetValue<string>();
        Apply("__open", new() { ["principalId"] = "alice" }, TimeSpan.Zero);
        var review = Apply("__attention_raise", new() { ["kind"] = "review_ready", ["taskId"] = "T-1" }, TimeSpan.Zero)["itemId"]!.GetValue<string>();
        // A lower tier waits for a boundary reply.
        Assert.Null(Wake(TimeSpan.FromMinutes(1)));
        // A top-tier item is the wake candidate at once.
        var blocked = Apply("__attention_raise", new() { ["kind"] = "failed", ["taskId"] = "T-2" }, TimeSpan.FromMinutes(2))["itemId"]!.GetValue<string>();
        Assert.Equal(blocked, Wake(TimeSpan.FromMinutes(2)));
        Apply("attention_respond", new() { ["itemId"] = blocked, ["action"] = "dismiss" }, TimeSpan.FromMinutes(3));
        Assert.Null(Wake(TimeSpan.FromMinutes(9)));
        // A message is an info item, so it waits like any lower tier.
        Apply("__deliver", new() { ["messageId"] = "m-1", ["text"] = "please look", ["attentionExpiryDays"] = 7 }, TimeSpan.FromMinutes(4));
        Assert.Null(Wake(TimeSpan.FromMinutes(9)));
        // With no boundary for the 10-minute hold, the idle chat may be woken once: one ID for the whole idle period, however many
        // items wait, and the adapter wakes once per ID.
        Assert.Equal("hold:0", Wake(TimeSpan.FromMinutes(10)));
        Assert.Equal("hold:0", Wake(TimeSpan.FromMinutes(20)));
        // A boundary shows the chat is active, so the hold starts again from it with a new ID.
        Apply("__attention_offer", new(), TimeSpan.FromMinutes(11));
        Apply("attention_respond", new() { ["itemId"] = review, ["action"] = "later", ["snoozeBoundaries"] = 1 }, TimeSpan.FromMinutes(11));
        Apply("__attention_offer", new(), TimeSpan.FromMinutes(12));
        Assert.Null(Wake(TimeSpan.FromMinutes(21)));
        Assert.Equal("hold:" + (start + TimeSpan.FromMinutes(12)).UtcTicks, Wake(TimeSpan.FromMinutes(22)));
        // Acknowledging the message closes its item.
        Assert.Equal(2, Apply("get_status", new() { ["attentionStatus"] = "count" }, TimeSpan.FromMinutes(23))["attention"]!["actionable"]!.GetValue<int>());
        Apply("ack", new() { ["requestId"] = "ack-1", ["messageId"] = "m-1" }, TimeSpan.FromMinutes(23));
        Assert.Equal(1, Apply("get_status", new() { ["attentionStatus"] = "count" }, TimeSpan.FromMinutes(23))["attention"]!["actionable"]!.GetValue<int>());
    }

    [Fact]
    public async Task Busy_chat_is_not_woken_and_its_messages_wake_once_when_idle()
    {
        using var fixture = new Fixture();
        // Nothing new: the chat's activity is not even looked at.
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Equal(0, fixture.Dispatcher.BusyChecks);

        // New mail while the chat is mid-turn stacks no wake and reserves nothing.
        fixture.Dispatcher.Busy = true;
        fixture.Messages = ["first"];
        Assert.Equal("busy", await fixture.Reconcile());
        fixture.Messages = ["first", "second"];
        Assert.Equal("busy", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);

        // Once idle, everything still unacknowledged wakes it once.
        fixture.Dispatcher.Busy = false;
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Single(fixture.Dispatcher.Prompts);
        Assert.Equal("empty", await fixture.Reconcile());
    }

    [Fact]
    public async Task Quiet_messages_never_wake_but_ride_along_with_the_next_wake()
    {
        using var fixture = new Fixture();
        fixture.Quiet.Add("status-1");
        fixture.Quiet.Add("status-2");
        fixture.Messages = ["status-1", "status-2"];
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);

        // A message that wants attention wakes the chat once; the quiet ones are simply read with it.
        fixture.Messages = ["status-1", "status-2", "question"];
        Assert.Equal("queued", await fixture.Reconcile());
        Assert.Single(fixture.Dispatcher.Prompts);
        Assert.Equal("empty", await fixture.Reconcile());
    }

    [Fact]
    public async Task Messages_handled_during_a_busy_turn_never_wake()
    {
        using var fixture = new Fixture();
        fixture.Dispatcher.Busy = true;
        fixture.Messages = ["first"];
        Assert.Equal("busy", await fixture.Reconcile());
        // The chat read and acknowledged it in that same turn.
        fixture.Messages = [];
        fixture.Dispatcher.Busy = false;
        Assert.Equal("empty", await fixture.Reconcile());
        Assert.Empty(fixture.Dispatcher.Prompts);
    }

    [Fact]
    public void A_wake_still_pending_in_Codex_queue_holds_the_next_one()
    {
        var home = Path.Combine(Path.GetTempPath(), "cocern-codex-home-" + Guid.NewGuid().ToString("N"));
        const string thread = "00000000-0000-0000-0000-0000000000dd";
        Directory.CreateDirectory(home);
        try
        {
            var queue = new CodexQueue(home);
            // No queue database at all never blocks a wake.
            Assert.False(queue.HasPendingWake(thread));

            using (var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(home, "queue_1.sqlite")};Pooling=False"))
            {
                database.Open();
                using var create = database.CreateCommand();
                create.CommandText = "CREATE TABLE queued_items (id TEXT PRIMARY KEY NOT NULL, thread_id TEXT NOT NULL, payload_json TEXT NOT NULL, queue_order INTEGER NOT NULL, created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL);" +
                    "INSERT INTO queued_items VALUES ('a', 'other-thread', '{\"text\":\"CoCern inbox wake for repository\"}', 1, 0, 0);" +
                    $"INSERT INTO queued_items VALUES ('b', '{thread}', '{{\"text\":\"an ordinary queued request\"}}', 2, 0, 0);";
                create.ExecuteNonQuery();
                // Another thread's wake and this thread's ordinary queued message do not hold it.
                Assert.False(queue.HasPendingWake(thread));

                using var add = database.CreateCommand();
                add.CommandText = $"INSERT INTO queued_items VALUES ('c', '{thread}', '{{\"text\":\"CoCern inbox wake for repository \\\"cocern\\\"\"}}', 3, 0, 0);";
                add.ExecuteNonQuery();
                Assert.True(queue.HasPendingWake(thread));
                Assert.True(new CodexWakeDispatcher(Environment.ProcessPath!, queue: queue).IsBusy(thread));

                // Codex delivering the prompt removes it from its queue: that is the acknowledgment.
                using var delivered = database.CreateCommand();
                delivered.CommandText = "DELETE FROM queued_items WHERE id = 'c'";
                delivered.ExecuteNonQuery();
                Assert.False(queue.HasPendingWake(thread));
            }

            // A queue database in an unknown shape never blocks waking either.
            File.WriteAllText(Path.Combine(home, "queue_2.sqlite"), "not a database");
            Assert.False(queue.HasPendingWake(thread));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(home, true); }
    }

    [Fact]
    public void Codex_turn_stays_busy_however_much_it_writes_and_ends_when_it_completes()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-rollouts-" + Guid.NewGuid().ToString("N"));
        const string thread = "00000000-0000-0000-0000-0000000000cc";
        try
        {
            var day = Path.Combine(root, "2026", "10", "30");
            Directory.CreateDirectory(day);
            var path = Path.Combine(day, $"rollout-2026-10-30T13-00-00-{thread}.jsonl");
            string Event(string type) => JsonSerializer.Serialize(new { timestamp = "2026-10-30T20:00:00Z", type = "event_msg", payload = new { type } });
            var output = JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "function_call_output", output = new string('o', 64 * 1024) } });
            var activity = new CodexActivity(root);
            File.WriteAllLines(path, [Event("task_complete"), Event("task_started")]);
            Assert.True(activity.IsBusy(thread, DateTimeOffset.UtcNow));

            // A long turn writes far more than any fixed tail; its start must not be lost.
            for (var i = 0; i < 300; i++) File.AppendAllLines(path, [output]);
            Assert.True(activity.IsBusy(thread, DateTimeOffset.UtcNow));
            Assert.True(new CodexActivity(root).IsBusy(thread, DateTimeOffset.UtcNow), "A fresh reader must find the turn start too.");

            File.AppendAllLines(path, [Event("task_complete")]);
            Assert.False(activity.IsBusy(thread, DateTimeOffset.UtcNow));
            File.AppendAllText(path, Event("task_started")[..20]);
            Assert.False(activity.IsBusy(thread, DateTimeOffset.UtcNow));
            File.AppendAllText(path, Event("task_started")[20..] + "\n");
            Assert.True(activity.IsBusy(thread, DateTimeOffset.UtcNow));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("task_started", 0, true)]
    [InlineData("task_complete", 0, false)]
    [InlineData("turn_aborted", 0, false)]
    [InlineData("task_started", 20, false)]
    public void Codex_activity_comes_from_the_rollout_tail(string lastEvent, int minutesSinceWrite, bool busy)
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-rollouts-" + Guid.NewGuid().ToString("N"));
        const string thread = "00000000-0000-0000-0000-0000000000aa";
        try
        {
            var day = Path.Combine(root, "2026", "10", "30");
            Directory.CreateDirectory(day);
            var path = Path.Combine(day, $"rollout-2026-10-30T13-00-00-{thread}.jsonl");
            string Event(string type) => JsonSerializer.Serialize(new { timestamp = "2026-10-30T20:00:00Z", type = "event_msg", payload = new { type } });
            File.WriteAllLines(path, [Event("task_started"), Event("token_count"), Event("task_complete"), Event(lastEvent), Event("token_count"), "{not json"]);
            var now = DateTimeOffset.UtcNow;
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-minutesSinceWrite).UtcDateTime);
            Assert.Equal(busy, new CodexActivity(root).IsBusy(thread, now));
            Assert.False(new CodexActivity(root).IsBusy("00000000-0000-0000-0000-0000000000bb", now));
        }
        finally { Directory.Delete(root, true); }
    }
}

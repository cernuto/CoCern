using System.Text.Json;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public sealed class LocalWakeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-local-wake-" + Guid.NewGuid().ToString("N"));
    private const string Secret = "synthetic-local-token-not-a-real-credential";

    [Fact]
    public void Explicit_token_file_connection_is_cached_without_copying_the_credential()
    {
        var tokenFile = TokenFile();
        var connection = LocalWakeConnection.Create(new AdapterOptions("http://127.0.0.1:5271/", Secret, admin: true), tokenFile, Path.Combine(root, "cache"));
        Assert.NotNull(connection);
        var text = File.ReadAllText(Path.Combine(connection.Folder, "connection.json"));
        Assert.DoesNotContain(Secret, text);
        Assert.Contains("tokenFile", text);
        Assert.Equal(Secret, LocalWakeConnection.Read(connection.Folder)!.Options.Token);
        Assert.Equal(Secret, ClaudeWaiter.ConnectionOptions(connection.Folder)!.Token);
        AssertPrivate(Path.Combine(connection.Folder, "connection.json"));
        Assert.Empty(NodeState.ReadSessionHandles(Path.Combine(root, "node")));
    }

    [Fact]
    public void Raw_token_only_configuration_does_not_create_a_durable_wake_connection()
    {
        var connection = LocalWakeConnection.Create(new AdapterOptions("http://127.0.0.1:5271/", Secret), null, Path.Combine(root, "cache"));
        Assert.Null(connection);
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    }

    [Fact]
    public async Task Successful_authenticated_call_restores_the_original_handle_and_a_denied_call_does_not()
    {
        await using var local = new LocalChatWake(Connection());
        using var handler = new ReplyHandler();
        using var http = new HttpClient(handler);
        var calls = new ChatCalls(new RelayForwarder(http, local.Connection.Options), null, null, "test", localWake: local);
        Assert.True((await calls.CallAsync(RelayTools.GetStatus, Arguments(), CancellationToken.None)).Ok);
        var path = NodeState.SessionHandlePath(local.Connection.Folder, "original-session");
        var handle = Assert.IsType<SessionHandle>(NodeState.ReadSessionHandle(path));
        Assert.Equal(("original-session", "private-session-token", "repo"), (handle.SessionId, handle.SessionToken, handle.RepositoryId));
        AssertPrivate(path);
        Assert.DoesNotContain(Secret, File.ReadAllText(path));

        var original = handle with { Chat = new("codex", Guid.NewGuid().ToString()), OpenedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        NodeState.WriteSessionHandle(local.Connection.Folder, original);
        await calls.CallAsync(RelayTools.GetStatus, Arguments(), CancellationToken.None);
        Assert.Equal(original, NodeState.ReadSessionHandle(path));

        handler.Ok = false;
        Assert.False((await calls.CallAsync(RelayTools.GetStatus, Arguments("denied-session"), CancellationToken.None)).Ok);
        Assert.Null(NodeState.ReadSessionHandle(NodeState.SessionHandlePath(local.Connection.Folder, "denied-session")));
        handler.Ok = true;
        await calls.CallAsync(RelayTools.DocSearch, Arguments("not-authenticated-by-session"), CancellationToken.None);
        Assert.Null(NodeState.ReadSessionHandle(NodeState.SessionHandlePath(local.Connection.Folder, "not-authenticated-by-session")));
    }

    [Fact]
    public async Task Cached_handle_cannot_be_reassigned_to_a_different_token_repository_or_native_chat()
    {
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var options = new AdapterOptions("http://127.0.0.1:5271/", Secret) { ExportedChat = chat };
        await using var local = new LocalChatWake(Connection(options));
        var previous = new SessionHandle("original-session", "private-session-token", "repo", "Original", DateTimeOffset.UtcNow.AddDays(-1), Chat: new("claude", Guid.NewGuid().ToString()));
        NodeState.WriteSessionHandle(local.Connection.Folder, previous);
        var reply = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "original-session" }), 0);
        local.Observe(RelayTools.GetStatus, Arguments(), reply);
        local.Observe(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "other", sessionId = previous.SessionId, sessionToken = "changed-token" }), reply);
        Assert.Equal(previous, NodeState.ReadSessionHandle(NodeState.SessionHandlePath(local.Connection.Folder, previous.SessionId)));
    }

    [Fact]
    public void A_captured_native_id_fills_an_unmapped_restored_handle_without_replacing_its_identity()
    {
        var connection = Connection();
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var previous = new SessionHandle("original-session", "private-session-token", "repo", "Existing", DateTimeOffset.UtcNow.AddDays(-1), Unmapped: true);
        NodeState.WriteSessionHandle(connection.Folder, previous);
        SessionHandoff.ForFolder(connection.Folder, chat).Restore(RelayTools.CheckInbox, Arguments(), new(true, "Ok"));
        Assert.Equal(previous with { Client = chat, Unmapped = false }, NodeState.ReadSessionHandle(NodeState.SessionHandlePath(connection.Folder, previous.SessionId)));
    }

    [Fact]
    public void Stop_hook_finds_one_local_hub_and_refuses_ambiguous_hubs_or_tampered_metadata()
    {
        var first = Connection();
        var chat = Guid.NewGuid().ToString();
        var handle = new SessionHandle("claude-session", "session-token", "repo", "Claude", DateTimeOffset.UtcNow, Client: new("claude", chat));
        NodeState.WriteSessionHandle(first.Folder, handle);
        Assert.Equal(first.Folder, LocalWakeConnection.FindForClaude(chat, Path.Combine(root, "cache")));
        var second = Connection(new AdapterOptions("http://127.0.0.1:5272/", Secret));
        NodeState.WriteSessionHandle(second.Folder, handle);
        Assert.Null(LocalWakeConnection.FindForClaude(chat, Path.Combine(root, "cache")));
        var path = Path.Combine(second.Folder, "connection.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("5272", "5273"));
        Assert.Null(LocalWakeConnection.Read(second.Folder));
        Assert.Equal(first.Folder, LocalWakeConnection.FindForClaude(chat, Path.Combine(root, "cache")));
    }

    [Fact]
    public async Task Conditional_first_status_after_reconnect_restores_from_the_full_authenticated_reply()
    {
        var connection = Connection();
        using var http = new HttpClient(new ReplyHandler());
        var forwarder = new RelayForwarder(http, connection.Options);
        var prior = await new ChatCalls(forwarder, null, null, "before").CallAsync(RelayTools.GetStatus, Arguments(), CancellationToken.None);
        var etag = prior.Data!.Value.GetProperty("etag").GetString();
        await using var local = new LocalChatWake(connection);
        var calls = new ChatCalls(forwarder, null, null, "after", localWake: local);
        var reply = await calls.CallAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "original-session", sessionToken = "private-session-token", ifNoneMatch = etag }), CancellationToken.None);
        Assert.True(reply.Data!.Value.GetProperty("unchanged").GetBoolean());
        Assert.Equal("original-session", Assert.IsType<SessionHandle>(NodeState.ReadSessionHandle(NodeState.SessionHandlePath(connection.Folder, "original-session"))).SessionId);
    }

    [Fact]
    public async Task Concurrent_restores_keep_one_native_owner_and_cannot_dispatch_to_the_competing_chat()
    {
        var firstChat = new ChatLocation("codex", Guid.NewGuid().ToString());
        var secondChat = new ChatLocation("codex", Guid.NewGuid().ToString());
        LocalWakeConnection For(ChatLocation chat) => Connection(new AdapterOptions("http://127.0.0.1:5271/", Secret) { ExportedChat = chat });
        var firstConnection = For(firstChat);
        var secondConnection = For(secondChat);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var wrongTarget = 0;
        async Task Watch(ChatLocation own, WakeTarget target, CancellationToken ct)
        {
            if (target.Route.ThreadId != own.ThreadId) Interlocked.Increment(ref wrongTarget);
            Interlocked.Increment(ref startedCount);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => "synthetic-codex", CodexDispatcher = _ => new NoDispatch() };
        await using var first = new LocalChatWake(firstConnection, transport, (_, target, ct) => Watch(firstChat, target, ct));
        await using var second = new LocalChatWake(secondConnection, transport, (_, target, ct) => Watch(secondChat, target, ct));
        var ok = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "original-session" }), 0);
        var path = NodeState.SessionHandlePath(firstConnection.Folder, "original-session");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Both simultaneous restore attempts must fail closed while another writer holds the session, rather than overwrite each other.
        using (NodeState.CreatePrivateFile(Path.Combine(Path.GetDirectoryName(path)!, "original-session.restore.lock")))
        {
            await Task.WhenAll(Task.Run(() => first.Observe(RelayTools.GetStatus, Arguments(), ok)), Task.Run(() => second.Observe(RelayTools.GetStatus, Arguments(), ok)));
            Assert.False(File.Exists(path));
        }
        using var gate = new ManualResetEventSlim(false);
        var left = Task.Run(() => { gate.Wait(); first.Observe(RelayTools.GetStatus, Arguments(), ok); });
        var right = Task.Run(() => { gate.Wait(); second.Observe(RelayTools.GetStatus, Arguments(), ok); });
        gate.Set();
        await Task.WhenAll(left, right);
        var owner = Assert.IsType<SessionHandle>(NodeState.ReadSessionHandle(path));
        Assert.Contains(owner.Client, new[] { firstChat, secondChat });
        first.Observe(RelayTools.GetStatus, Arguments(), ok);
        second.Observe(RelayTools.GetStatus, Arguments(), ok);
        Assert.Equal(owner, NodeState.ReadSessionHandle(path));
        NodeState.WriteSessionHandle(firstConnection.Folder, owner with { Chat = owner.Client });
        var firstRun = first.RunAsync(CancellationToken.None);
        var secondRun = second.RunAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref startedCount));
        Assert.Equal(0, Volatile.Read(ref wrongTarget));
    }

    [Fact]
    public async Task Duplicate_adapter_waiters_take_one_native_chat_lock_and_release_it_on_disconnect()
    {
        var connection = Connection();
        var thread = Guid.NewGuid().ToString();
        NodeState.WriteSessionHandle(connection.Folder, new("original-session", "private-session-token", "repo", "Codex", DateTimeOffset.UtcNow, Chat: new("codex", thread)));
        var count = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Watch(WakeRuntime runtime, WakeTarget target, CancellationToken ct)
        {
            Assert.Equal(Secret, runtime.Options.Token);
            Assert.Equal(thread, target.Route.ThreadId);
            if (Interlocked.Increment(ref count) == 1) firstStarted.TrySetResult(); else secondStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => "synthetic-codex", CodexDispatcher = _ => new NoDispatch() };
        var first = new LocalChatWake(connection, transport, Watch);
        await using var second = new LocalChatWake(connection, transport, Watch);
        var ok = new AdapterReply(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "original-session" }), 0);
        first.Observe(RelayTools.GetStatus, Arguments(), ok);
        second.Observe(RelayTools.GetStatus, Arguments(), ok);
        var firstRun = first.RunAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRun = second.RunAsync(CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref count));
        await first.DisposeAsync();
        Assert.True(firstRun.IsCompletedSuccessfully);
        second.Observe(RelayTools.GetStatus, Arguments(), ok);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref count));
    }

    private string StatusRoot => Path.Combine(root, "status-root");
    private string OwnStatusFile => Path.Combine(StatusRoot, "status", Environment.ProcessId + ".json");

    [Fact]
    public void Report_writes_this_process_status_privately_with_only_fixed_fields()
    {
        LocalWakeStatus.Report("connection_ready", StatusRoot);
        AssertPrivate(OwnStatusFile);
        using var document = JsonDocument.Parse(File.ReadAllText(OwnStatusFile));
        var data = document.RootElement;
        foreach (var field in new[] { "pid", "processStartedAt", "adapterVersion", "code", "codeAt", "observed", "watchers" }) Assert.True(data.TryGetProperty(field, out _), field);
        Assert.Equal(Environment.ProcessId, data.GetProperty("pid").GetInt32());
        Assert.Equal("connection_ready", data.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("adapterVersion").GetString()));
        Assert.True(data.GetProperty("processStartedAt").TryGetDateTimeOffset(out _));
        Assert.True(data.GetProperty("codeAt").TryGetDateTimeOffset(out var at));
        Assert.True(DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(1));
        Assert.Equal((0, 0), (data.GetProperty("observed").GetInt32(), data.GetProperty("watchers").GetInt32()));

        LocalWakeStatus.Report("setup_io_error", StatusRoot);
        using var replaced = JsonDocument.Parse(File.ReadAllText(OwnStatusFile));
        Assert.Equal("setup_io_error", replaced.RootElement.GetProperty("code").GetString());
        AssertPrivate(OwnStatusFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper_Case")]
    [InlineData("has space")]
    [InlineData("digit1")]
    [InlineData("trailing_newline\n")]
    [InlineData(@"C:\Users\someone\token")]
    [InlineData("synthetic-local-token-not-a-real-credential")]
    [InlineData("the file could not be read: access denied")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(null)]
    public void A_code_that_is_not_a_fixed_code_is_stored_as_invalid(string? code)
    {
        LocalWakeStatus.Report(code!, StatusRoot);
        var text = File.ReadAllText(OwnStatusFile);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("invalid_code", document.RootElement.GetProperty("code").GetString());
        if (!string.IsNullOrEmpty(code)) Assert.DoesNotContain(code, text);
    }

    [Fact]
    public void A_longest_allowed_code_is_kept()
    {
        var code = new string('a', 64);
        LocalWakeStatus.Report(code, StatusRoot);
        Assert.Contains(code, File.ReadAllText(OwnStatusFile));
    }

    [Fact]
    public void Report_and_counts_write_only_when_something_changed()
    {
        LocalWakeStatus.Report("connection_ready", StatusRoot);
        File.Delete(OwnStatusFile);
        LocalWakeStatus.Report("connection_ready", StatusRoot);
        Assert.False(File.Exists(OwnStatusFile));

        LocalWakeStatus.Counts(2, 1, StatusRoot);
        using (var document = JsonDocument.Parse(File.ReadAllText(OwnStatusFile)))
        {
            Assert.Equal((2, 1), (document.RootElement.GetProperty("observed").GetInt32(), document.RootElement.GetProperty("watchers").GetInt32()));
            Assert.Equal("connection_ready", document.RootElement.GetProperty("code").GetString());
        }
        File.Delete(OwnStatusFile);
        LocalWakeStatus.Counts(2, 1, StatusRoot);
        Assert.False(File.Exists(OwnStatusFile));
    }

    [Fact]
    public void Counts_alone_writes_a_status_file_and_negative_counts_are_held_at_zero()
    {
        LocalWakeStatus.Counts(-1, -5, StatusRoot);
        using var document = JsonDocument.Parse(File.ReadAllText(OwnStatusFile));
        Assert.Equal((0, 0), (document.RootElement.GetProperty("observed").GetInt32(), document.RootElement.GetProperty("watchers").GetInt32()));
    }

    [Fact]
    public void Status_writes_never_throw_and_a_relative_root_writes_nothing()
    {
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "a-file-not-a-folder");
        File.WriteAllText(blocked, "");
        LocalWakeStatus.Report("connection_ready", blocked);
        LocalWakeStatus.Counts(1, 1, blocked);
        Assert.Equal("", File.ReadAllText(blocked));

        var relative = "cocern-status-" + Guid.NewGuid().ToString("N");
        LocalWakeStatus.Report("connection_ready", relative);
        LocalWakeStatus.Counts(1, 1, relative);
        Assert.False(Directory.Exists(relative));

        LocalWakeStatus.Report("connection_ready", "");
        LocalWakeStatus.Report("connection_ready", "bad\0root");
        LocalWakeStatus.Counts(1, 1, "bad\0root");
    }

    [Fact]
    public void Starting_prunes_files_of_ended_processes_and_files_older_than_seven_days()
    {
        var folder = Directory.CreateDirectory(Path.Combine(StatusRoot, "status")).FullName;
        using var alive = System.Diagnostics.Process.GetProcesses().First(x => x.Id != Environment.ProcessId && x.Id > 0);
        var ended = Path.Combine(folder, int.MaxValue + ".json");
        var running = Path.Combine(folder, alive.Id + ".json");
        var old = Path.Combine(folder, "old-leftover.json.tmp");
        var other = Path.Combine(folder, "recent-leftover.json.tmp");
        foreach (var path in new[] { ended, running, old, other }) File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));

        LocalWakeStatus.Report("connection_ready", StatusRoot);
        Assert.False(File.Exists(ended));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(running));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(OwnStatusFile));
    }

    [Fact]
    public void A_running_process_file_older_than_seven_days_is_pruned()
    {
        var folder = Directory.CreateDirectory(Path.Combine(StatusRoot, "status")).FullName;
        using var alive = System.Diagnostics.Process.GetProcesses().First(x => x.Id != Environment.ProcessId && x.Id > 0);
        var running = Path.Combine(folder, alive.Id + ".json");
        File.WriteAllText(running, "{}");
        File.SetLastWriteTimeUtc(running, DateTime.UtcNow.AddDays(-8));
        LocalWakeStatus.Report("connection_ready", StatusRoot);
        Assert.False(File.Exists(running));
    }

    private JsonElement Status(string? statusRoot = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(statusRoot ?? StatusRoot, "status", Environment.ProcessId + ".json")));
        return document.RootElement.Clone();
    }

    private static readonly AdapterReply OkReply = new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "original-session" }), 0);
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Status_file_tracks_setup_state_and_when_each_changed()
    {
        LocalWakeStatus.Report(LocalWakeCodes.Starting, StatusRoot);
        var first = Status();
        Assert.Equal("starting", first.GetProperty("state").GetString());
        Assert.False(first.GetProperty("setupReached").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("observedAt").ValueKind);

        LocalWakeStatus.Report(LocalWakeCodes.SetupReached, StatusRoot);
        LocalWakeStatus.Counts(2, 1, StatusRoot);
        var after = Status();
        Assert.True(after.GetProperty("setupReached").GetBoolean());
        Assert.True(after.GetProperty("observedAt").TryGetDateTimeOffset(out _));
        Assert.True(after.GetProperty("watchersAt").TryGetDateTimeOffset(out _));
        Assert.True(after.GetProperty("stateAt").TryGetDateTimeOffset(out _));
    }

    [Theory]
    [InlineData("token_file_missing", "off")]
    [InlineData("credential_mismatch", "off")]
    [InlineData("setup_io_error", "off")]
    [InlineData("setup_returned_null", "off")]
    [InlineData("no_codex_executable", "degraded")]
    [InlineData("observed_unmapped", "degraded")]
    public void Unhealthy_codes_are_a_problem_with_that_state_and_reason(string code, string state)
    {
        LocalWakeStatus.Report(code, StatusRoot);
        Assert.Equal((state, code), LocalWakeStatus.Problem(StatusRoot));
        Assert.Equal(state, Status().GetProperty("state").GetString());
        Assert.Equal(code, Status().GetProperty("stateReason").GetString());
        // Setup was reached for any setup reason, and the code stays fixed text.
        Assert.True(Status().GetProperty("setupReached").GetBoolean());
    }

    [Theory]
    [InlineData("starting", "starting")]
    [InlineData("connection_ready", "starting")]
    [InlineData("setup_complete", "starting")]
    [InlineData("node_connection", "not_applicable")]
    [InlineData("native_registration", "not_applicable")]
    [InlineData("watching", "healthy")]
    [InlineData("no_observed_sessions", "healthy")]
    [InlineData("no_codex_targets", "healthy")]
    public void Healthy_or_inapplicable_codes_are_not_a_problem(string code, string state)
    {
        LocalWakeStatus.Report(code, StatusRoot);
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));
        Assert.Equal(state, Status().GetProperty("state").GetString());
    }

    [Fact]
    public void State_changes_write_one_stderr_line_and_repeats_write_none()
    {
        var error = new StringWriter();
        LocalWakeStatus.UseError(StatusRoot, error);
        LocalWakeStatus.Report(LocalWakeCodes.Starting, StatusRoot);
        Assert.Equal("", error.ToString());
        LocalWakeStatus.Report(LocalWakeCodes.NoCodexExecutable, StatusRoot);
        LocalWakeStatus.Report(LocalWakeCodes.NoCodexExecutable, StatusRoot);
        LocalWakeStatus.Report(LocalWakeCodes.Watching, StatusRoot);
        var lines = error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("degraded", lines[0]); Assert.Contains("no_codex_executable", lines[0]);
        Assert.Contains("healthy", lines[1]); Assert.Contains("watching", lines[1]);
    }

    [Fact]
    public void A_routine_exit_is_recorded_without_changing_the_state_and_a_repeated_pass_keeps_it_visible()
    {
        var error = new StringWriter();
        LocalWakeStatus.UseError(StatusRoot, error);
        LocalWakeStatus.Report(LocalWakeCodes.NoCodexExecutable, StatusRoot);
        LocalWakeStatus.Report(LocalWakeCodes.ObserveNotSessionReply, StatusRoot);
        LocalWakeStatus.Report(LocalWakeCodes.ObserveNotSessionReply, StatusRoot);
        Assert.Equal(("degraded", "no_codex_executable"), LocalWakeStatus.Problem(StatusRoot));

        // The loop repeats its own reason every pass; that neither hides the last exit nor writes again.
        LocalWakeStatus.Report(LocalWakeCodes.NoCodexExecutable, StatusRoot);
        Assert.Equal(LocalWakeCodes.ObserveNotSessionReply, LocalWakeStatus.LastCode(StatusRoot));

        LocalWakeStatus.Counts(1, 0, StatusRoot);
        var data = Status();
        Assert.Equal("observe_not_session_reply", data.GetProperty("code").GetString());
        Assert.Equal("no_codex_executable", data.GetProperty("stateReason").GetString());
        Assert.Equal(2, data.GetProperty("codeCounts").GetProperty("observe_not_session_reply").GetInt32());
        Assert.Equal(1, data.GetProperty("codeCounts").GetProperty("no_codex_executable").GetInt32());
        Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_status_write_failure_is_reported_once_and_the_state_is_still_known()
    {
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "a-file-not-a-folder");
        File.WriteAllText(blocked, "");
        var error = new StringWriter();
        LocalWakeStatus.UseError(blocked, error);
        LocalWakeStatus.Report(LocalWakeCodes.NoCodexExecutable, blocked);
        LocalWakeStatus.Report(LocalWakeCodes.LoopFailed, blocked);
        Assert.Equal(1, error.ToString().Split('\n').Count(line => line.Contains("could not be written", StringComparison.Ordinal)));
        Assert.Equal(("degraded", "loop_failed"), LocalWakeStatus.Problem(blocked));

        var relative = "cocern-status-" + Guid.NewGuid().ToString("N");
        LocalWakeStatus.Report(LocalWakeCodes.NoCodexExecutable, relative);
        Assert.False(Directory.Exists(relative));
        Assert.Equal(("degraded", "no_codex_executable"), LocalWakeStatus.Problem(relative));
    }

    [Fact]
    public async Task Every_early_return_in_observe_leaves_its_reason_and_the_file_holds_no_token()
    {
        var claude = new ChatLocation("claude", Guid.NewGuid().ToString());
        await using var local = new LocalChatWake(Connection(new AdapterOptions("http://127.0.0.1:5271/", Secret) { ExportedChat = claude }), statusRoot: StatusRoot);
        var folder = local.Connection.Folder;
        var open = JsonSerializer.SerializeToElement(new { repositoryId = "repo", label = "x" });
        string? Last() => LocalWakeStatus.LastCode(StatusRoot);

        local.Observe(RelayTools.GetStatus, Arguments(), new(false, "SessionDenied"));
        Assert.Equal(LocalWakeCodes.ObserveReplyNotOk, Last());
        local.Observe(RelayTools.SessionOpen, open, new(true, "Ok"));
        Assert.Equal(LocalWakeCodes.ObserveNoReplyData, Last());
        local.Observe(RelayTools.SessionOpen, open, new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "bad/id", sessionToken = "private-session-token" }), 0));
        Assert.Equal(LocalWakeCodes.ObserveBadSessionId, Last());
        local.Observe(RelayTools.DocSearch, Arguments(), OkReply);
        Assert.Equal(LocalWakeCodes.ObserveNotSessionReply, Last());
        local.Observe(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "bad/id", sessionToken = "private-session-token" }), new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "bad/id" }), 0));
        Assert.Equal(LocalWakeCodes.ObserveBadSessionId, Last());

        // A different token for a cached session: the handle stays as it was and the call says why.
        var foreign = new SessionHandle("foreign-session", "other-token", "repo", "Foreign", DateTimeOffset.UtcNow, Chat: new("codex", Guid.NewGuid().ToString()));
        NodeState.WriteSessionHandle(folder, foreign);
        local.Observe(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "foreign-session", sessionToken = "wrong-token" }), new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "foreign-session" }), 0));
        Assert.Equal(LocalWakeCodes.ObserveHandleMismatch, Last());
        // The right token, but the handle belongs to another native chat.
        local.Observe(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "foreign-session", sessionToken = "other-token" }), new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "foreign-session" }), 0));
        Assert.Equal(LocalWakeCodes.ObserveNotOwned, Last());

        var text = File.ReadAllText(OwnStatusFile);
        foreach (var secret in new[] { Secret, "private-session-token", "other-token", "wrong-token", "foreign-session" }) Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task Restore_and_record_failing_to_take_the_lock_leave_their_reasons()
    {
        await using var local = new LocalChatWake(Connection(), statusRoot: StatusRoot);
        var sessions = Directory.CreateDirectory(Path.Combine(local.Connection.Folder, "sessions")).FullName;
        using (NodeState.CreatePrivateFile(Path.Combine(sessions, "original-session.restore.lock")))
        {
            local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
            local.Observe(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", label = "x" }),
                new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "original-session", sessionToken = "private-session-token" }), 0));
        }
        var counts = Status().GetProperty("codeCounts");
        Assert.Equal(1, counts.GetProperty("restore_lock_busy").GetInt32());
        Assert.Equal(1, counts.GetProperty("record_lock_busy").GetInt32());
        Assert.NotNull(LocalWakeStatus.Problem(StatusRoot));
    }

    [Fact]
    public async Task A_lock_failure_with_no_session_stays_a_problem_across_passes_until_a_call_succeeds()
    {
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => "synthetic-codex", CodexDispatcher = _ => new NoDispatch() };
        await using var local = new LocalChatWake(Connection(), transport, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), StatusRoot);
        var sessions = Directory.CreateDirectory(Path.Combine(local.Connection.Folder, "sessions")).FullName;
        using (NodeState.CreatePrivateFile(Path.Combine(sessions, "original-session.restore.lock")))
            local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        _ = local.RunAsync(CancellationToken.None);
        await local.FirstPass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(LocalWakeStatus.Problem(StatusRoot));

        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));
    }

    [Fact]
    public async Task The_loop_records_no_sessions_then_an_unlocated_then_an_unmapped_handle()
    {
        var connection = Connection();
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => "synthetic-codex", CodexDispatcher = _ => new NoDispatch() };
        await using var local = new LocalChatWake(connection, transport, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), StatusRoot);
        _ = local.RunAsync(CancellationToken.None);
        await local.FirstPass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LocalWakeCodes.NoObservedSessions, LocalWakeStatus.LastCode(StatusRoot));
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));

        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        await WaitFor(() => LocalWakeStatus.LastCode(StatusRoot) == LocalWakeCodes.ObservedNotLocated);
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));

        var handle = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(connection.Folder, "original-session"))!;
        NodeState.WriteSessionHandle(connection.Folder, handle with { Unmapped = true });
        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        await WaitFor(() => LocalWakeStatus.LastCode(StatusRoot) == LocalWakeCodes.ObservedUnmapped);
        Assert.Equal(("degraded", "observed_unmapped"), LocalWakeStatus.Problem(StatusRoot));
    }

    [Fact]
    public async Task The_loop_records_a_missing_codex_executable_then_recovers_and_counts_its_watcher()
    {
        var connection = Connection();
        NodeState.WriteSessionHandle(connection.Folder, new("original-session", "private-session-token", "repo", "Codex", DateTimeOffset.UtcNow, Chat: new("codex", Guid.NewGuid().ToString())));
        string? codex = null;
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => codex, CodexDispatcher = _ => new NoDispatch() };
        await using var local = new LocalChatWake(connection, transport, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), StatusRoot);
        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        _ = local.RunAsync(CancellationToken.None);
        await local.FirstPass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("degraded", "no_codex_executable"), LocalWakeStatus.Problem(StatusRoot));

        codex = "synthetic-codex";
        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        await WaitFor(() => LocalWakeStatus.LastCode(StatusRoot) == LocalWakeCodes.Watching);
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));
        var data = Status();
        Assert.Equal((1, 1), (data.GetProperty("observed").GetInt32(), data.GetProperty("watchers").GetInt32()));
    }

    [Fact]
    public async Task A_failing_pass_or_watcher_is_recorded_and_does_not_end_the_loop()
    {
        var connection = Connection();
        NodeState.WriteSessionHandle(connection.Folder, new("original-session", "private-session-token", "repo", "Codex", DateTimeOffset.UtcNow, Chat: new("codex", Guid.NewGuid().ToString())));
        var mode = 0;
        var transport = new NodeTransport
        {
            Locators = [], CodexDispatcher = _ => new NoDispatch(),
            CodexExecutable = () => Volatile.Read(ref mode) == 0 ? throw new InvalidOperationException("synthetic") : "synthetic-codex"
        };
        await using var local = new LocalChatWake(connection, transport, async (_, _, ct) =>
        {
            await Task.Yield();
            if (Volatile.Read(ref mode) == 1) throw new InvalidOperationException("synthetic");
            await Task.Delay(Timeout.Infinite, ct);
        }, StatusRoot);
        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        var run = local.RunAsync(CancellationToken.None);
        await local.FirstPass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("degraded", "loop_failed"), LocalWakeStatus.Problem(StatusRoot));
        Assert.False(run.IsCompleted);

        Volatile.Write(ref mode, 1);
        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        await WaitFor(() => LocalWakeStatus.Problem(StatusRoot) == ("degraded", "watcher_failed"));
        Assert.False(run.IsCompleted);

        Volatile.Write(ref mode, 2);
        await WaitFor(() => LocalWakeStatus.LastCode(StatusRoot) == LocalWakeCodes.Watching);
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));
    }

    [Fact]
    public async Task A_second_adapter_that_loses_the_chat_lock_records_it_and_stays_healthy()
    {
        var connection = Connection();
        NodeState.WriteSessionHandle(connection.Folder, new("original-session", "private-session-token", "repo", "Codex", DateTimeOffset.UtcNow, Chat: new("codex", Guid.NewGuid().ToString())));
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => "synthetic-codex", CodexDispatcher = _ => new NoDispatch() };
        var (firstRoot, secondRoot) = (Path.Combine(root, "first-status"), Path.Combine(root, "second-status"));
        await using var first = new LocalChatWake(connection, transport, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), firstRoot);
        await using var second = new LocalChatWake(connection, transport, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), secondRoot);
        first.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        second.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        _ = first.RunAsync(CancellationToken.None);
        await first.FirstPass.WaitAsync(TimeSpan.FromSeconds(10));
        _ = second.RunAsync(CancellationToken.None);
        await second.FirstPass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LocalWakeCodes.Watching, LocalWakeStatus.LastCode(firstRoot));
        var data = Status(secondRoot);
        Assert.True(data.GetProperty("codeCounts").GetProperty("watcher_lock_held").GetInt32() >= 1);
        Assert.Equal("no_live_watcher", data.GetProperty("stateReason").GetString());
        Assert.Equal(0, data.GetProperty("watchers").GetInt32());
        Assert.Null(LocalWakeStatus.Problem(secondRoot));
    }

    [Fact]
    public async Task A_claude_adapter_with_nothing_for_codex_to_do_is_healthy()
    {
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var connection = Connection(new AdapterOptions("http://127.0.0.1:5271/", Secret) { ExportedChat = chat });
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => null, CodexDispatcher = _ => new NoDispatch() };
        await using var local = new LocalChatWake(connection, transport, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), StatusRoot);
        local.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        _ = local.RunAsync(CancellationToken.None);
        await local.FirstPass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LocalWakeCodes.NoCodexTargets, LocalWakeStatus.LastCode(StatusRoot));
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));
    }

    [Fact]
    public async Task Open_records_why_there_is_no_local_wake_and_marks_setup_reached()
    {
        var cache = Path.Combine(root, "open-native");
        Assert.Null(LocalChatWake.Open(new AdapterOptions("http://127.0.0.1:5271/", Secret), TokenFile(), registered: true, cache));
        Assert.Equal(LocalWakeCodes.NativeRegistration, LocalWakeStatus.LastCode(cache));
        Assert.Null(LocalWakeStatus.Problem(cache));

        cache = Path.Combine(root, "open-raw");
        Assert.Null(LocalChatWake.Open(new AdapterOptions("http://127.0.0.1:5271/", Secret), null, registered: false, cache));
        Assert.Equal(("off", "setup_returned_null"), LocalWakeStatus.Problem(cache));
        Assert.True(Status(cache).GetProperty("setupReached").GetBoolean());

        cache = Path.Combine(root, "open-node");
        var node = new AdapterOptions("http://127.0.0.1:5271/", Secret) { NodeFolder = Path.Combine(root, "node") };
        Assert.Null(LocalChatWake.Open(node, TokenFile(), registered: false, cache));
        Assert.Equal(LocalWakeCodes.NodeConnection, LocalWakeStatus.LastCode(cache));
        Assert.Null(LocalWakeStatus.Problem(cache));

        cache = Path.Combine(root, "open-working");
        await using var wake = LocalChatWake.Open(new AdapterOptions("http://127.0.0.1:5271/", Secret), TokenFile(), registered: false, cache);
        Assert.NotNull(wake);
        Assert.Equal(LocalWakeCodes.SetupComplete, LocalWakeStatus.LastCode(cache));
        Assert.Null(LocalWakeStatus.Problem(cache));
        wake.Observe(RelayTools.DocSearch, Arguments(), new(true, "Ok"));
        Assert.Equal(LocalWakeCodes.ObserveNotSessionReply, LocalWakeStatus.LastCode(cache));
    }

    [Fact]
    public async Task A_more_specific_reason_from_setup_is_not_overwritten()
    {
        var cache = Path.Combine(root, "open-specific");
        // Open adds its own reason only when setup left none.
        Assert.Null(LocalChatWake.SetupOutcome(null, "credential_mismatch"));
        Assert.Equal(LocalWakeCodes.SetupReturnedNull, LocalChatWake.SetupOutcome(null, LocalWakeCodes.SetupReached));
        await using var wake = new LocalChatWake(Connection(), statusRoot: cache);
        Assert.Null(LocalChatWake.SetupOutcome(wake, "connection_ready"));
        Assert.Equal(LocalWakeCodes.SetupComplete, LocalChatWake.SetupOutcome(wake, LocalWakeCodes.SetupReached));
    }

    [Fact]
    public async Task Session_open_and_get_status_carry_the_field_only_while_unhealthy()
    {
        using var handler = new ReplyHandler();
        using var http = new HttpClient(handler);
        var connection = Connection();
        var calls = new ChatCalls(new RelayForwarder(http, connection.Options), null, null, "test", wakeStatusRoot: StatusRoot);
        LocalWakeStatus.Report(LocalWakeCodes.Watching, StatusRoot);
        var healthy = await calls.CallAsync(RelayTools.GetStatus, Arguments(), CancellationToken.None);
        Assert.False(healthy.Data!.Value.TryGetProperty("localWake", out _));

        LocalWakeStatus.Report(LocalWakeCodes.NoCodexExecutable, StatusRoot);
        foreach (var tool in new[] { RelayTools.GetStatus, RelayTools.SessionOpen })
        {
            var reply = await calls.CallAsync(tool, Arguments(), CancellationToken.None);
            var field = reply.Data!.Value.GetProperty("localWake");
            Assert.Equal("degraded", field.GetProperty("state").GetString());
            Assert.Equal("no_codex_executable", field.GetProperty("reason").GetString());
            Assert.Equal(2, field.EnumerateObject().Count());
        }
        var other = await calls.CallAsync(RelayTools.Ack, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "original-session", sessionToken = "private-session-token", messageId = "m", requestId = "r" }), CancellationToken.None);
        Assert.False(other.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("localWake", out _));

        // An unchanged conditional read still says so while unhealthy, and the field does not move the etag.
        var etag = (await calls.CallAsync(RelayTools.GetStatus, Arguments(), CancellationToken.None)).Data!.Value.GetProperty("etag").GetString();
        var conditional = JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "original-session", sessionToken = "private-session-token", ifNoneMatch = etag });
        var unhealthy = await calls.CallAsync(RelayTools.GetStatus, conditional, CancellationToken.None);
        Assert.True(unhealthy.Data!.Value.GetProperty("unchanged").GetBoolean());
        Assert.Equal("degraded", unhealthy.Data!.Value.GetProperty("localWake").GetProperty("state").GetString());
        LocalWakeStatus.Report(LocalWakeCodes.Watching, StatusRoot);
        var recovered = await calls.CallAsync(RelayTools.GetStatus, conditional, CancellationToken.None);
        Assert.True(recovered.Data!.Value.GetProperty("unchanged").GetBoolean());
        Assert.False(recovered.Data!.Value.TryGetProperty("localWake", out _));
    }

    [Fact]
    public async Task Without_a_status_report_for_the_default_root_no_field_is_added_and_nothing_is_read()
    {
        using var handler = new ReplyHandler();
        using var http = new HttpClient(handler);
        var calls = new ChatCalls(new RelayForwarder(http, Connection().Options), null, null, "test");
        var reply = await calls.CallAsync(RelayTools.GetStatus, Arguments(), CancellationToken.None);
        Assert.False(reply.Data!.Value.TryGetProperty("localWake", out _));
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public string Mode = "ok";
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Tool, bool Background, string? Session, string? Token)> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Volatile.Read(ref Mode) == "unreachable") { Requests.Enqueue((request.RequestUri!.Segments[^1], false, null, null)); throw new HttpRequestException("synthetic"); }
            using var args = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var data = args.RootElement;
            var session = data.GetProperty("sessionId").GetString();
            Requests.Enqueue((request.RequestUri!.Segments[^1], data.TryGetProperty("backgroundCall", out var background) && background.GetBoolean(), session, data.GetProperty("sessionToken").GetString()));
            var ok = Mode != "denied";
            var said = Mode == "other-session" ? "another-session" : session;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok, code = ok ? "Ok" : "SessionDenied", dataJson = JsonSerializer.Serialize(new { sessionId = said }), version = 0 })) };
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }

    private static readonly ProcessFacts Launcher = new(4242, 1, "dotnet", new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
    private static readonly ProcessFacts OtherLauncher = new(5353, 1, "dotnet", new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc));
    private static ArmedChats ArmedBy(LocalWakeConnection connection, ProcessFacts? owner) => new(connection.Folder, owner, _ => true);

    /// <summary>A backend just started: nothing observed in memory, only what is on disk.</summary>
    private sealed class Restarted : IAsyncDisposable
    {
        public required LocalChatWake Wake { get; init; }
        public required System.Collections.Concurrent.ConcurrentQueue<WakeTarget> Started { get; init; }
        public required string StatusRoot { get; init; }
        public Task Pass => Wake.FirstPass.WaitAsync(TimeSpan.FromSeconds(15));
        public async ValueTask DisposeAsync() => await Wake.DisposeAsync();
    }

    private Restarted Restart(LocalWakeConnection connection, ProbeHandler hub, string? statusRoot = null, TimeProvider? clock = null, string? codex = "synthetic-codex", bool run = true, ProcessFacts? owner = null, bool noOwner = false)
    {
        var started = new System.Collections.Concurrent.ConcurrentQueue<WakeTarget>();
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => codex, CodexDispatcher = _ => new NoDispatch(), Clock = clock ?? TimeProvider.System };
        var wake = new LocalChatWake(connection, transport, async (_, target, ct) => { started.Enqueue(target); await target.Dispatcher.QueueAsync(target.Route.ThreadId, "synthetic", ct); await Task.Delay(Timeout.Infinite, ct); },
            statusRoot ?? StatusRoot, hub, ArmedBy(connection, noOwner ? null : owner ?? Launcher));
        if (run) _ = wake.RunAsync(CancellationToken.None);
        return new() { Wake = wake, Started = started, StatusRoot = statusRoot ?? StatusRoot };
    }

    /// <summary>A located Codex handle on disk, with a record of <see cref="Launcher"/> arming it unless <paramref name="armed"/> says otherwise.</summary>
    private static SessionHandle CodexHandle(LocalWakeConnection connection, string session = "original-session", string? thread = null, DateTimeOffset? opened = null, bool armed = true)
    {
        var handle = new SessionHandle(session, "private-session-token", "repo", "Codex", opened ?? DateTimeOffset.UtcNow, Chat: new("codex", thread ?? Guid.NewGuid().ToString()));
        NodeState.WriteSessionHandle(connection.Folder, handle);
        if (armed) Assert.True(ArmedBy(connection, Launcher).Record(session));
        return handle;
    }

    private int SkipCount(string code) => Status().GetProperty("codeCounts").TryGetProperty(code, out var count) ? count.GetInt32() : 0;

    [Fact]
    public async Task A_backend_swap_under_the_same_owner_rearms_its_own_chat_after_one_authenticated_read()
    {
        var connection = Connection();
        var handle = CodexHandle(connection, armed: false);
        using var hub = new ProbeHandler();
        await using (var before = Restart(connection, hub, run: false))
            before.Wake.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        Assert.Empty(hub.Requests);
        var path = NodeState.SessionHandlePath(connection.Folder, handle.SessionId);
        var touched = DateTime.UtcNow.AddHours(-23);
        File.SetLastWriteTimeUtc(path, touched);
        var text = File.ReadAllText(path);

        await using var after = Restart(connection, hub);
        await after.Pass;

        var target = Assert.Single(after.Started);
        Assert.Equal((handle.Chat!.ThreadId, handle.SessionId), (target.Route.ThreadId, target.Session.SessionId));
        Assert.Equal((RelayTools.GetStatus, true, handle.SessionId, handle.SessionToken), Assert.Single(hub.Requests));
        // Re-arming is not chat activity: a handle that keeps its age can still fall out of the limit.
        Assert.Equal(touched, File.GetLastWriteTimeUtc(path));
        Assert.Equal(text, File.ReadAllText(path));
        var status = Status();
        Assert.Equal((1, 1), (status.GetProperty("observed").GetInt32(), status.GetProperty("watchers").GetInt32()));
        Assert.Equal(LocalWakeCodes.Watching, LocalWakeStatus.LastCode(StatusRoot));
        Assert.DoesNotContain(handle.SessionToken, File.ReadAllText(OwnStatusFile));
    }

    [Fact]
    public async Task A_located_handle_armed_by_another_owner_is_refused_with_its_reason()
    {
        var connection = Connection();
        CodexHandle(connection, armed: false);
        Assert.True(ArmedBy(connection, OtherLauncher).Record("original-session"));
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(1, SkipCount("rearm_skip_not_armed_here"));
    }

    [Fact]
    public async Task A_located_recent_handle_with_no_record_is_refused_with_its_reason()
    {
        var connection = Connection();
        CodexHandle(connection, armed: false);
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(1, SkipCount("rearm_skip_not_armed_here"));
    }

    [Fact]
    public async Task A_reused_process_id_with_a_different_start_time_does_not_match()
    {
        var connection = Connection();
        CodexHandle(connection);
        var reused = Launcher with { StartedUtc = Launcher.StartedUtc.AddHours(1) };
        Assert.False(ArmedBy(connection, reused).IsArmedHere("original-session"));
        Assert.True(ArmedBy(connection, Launcher).IsArmedHere("original-session"));
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub, owner: reused);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(1, SkipCount("rearm_skip_not_armed_here"));
    }

    [Fact]
    public async Task A_record_holds_only_its_owner_and_session_and_never_a_token()
    {
        var connection = Connection();
        CodexHandle(connection, armed: false);
        using var hub = new ProbeHandler();
        await using (var adapter = Restart(connection, hub, run: false))
            adapter.Wake.Observe(RelayTools.GetStatus, Arguments(), OkReply);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(connection.Folder, "armed")));
        AssertPrivate(path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("private-session-token", text);
        Assert.DoesNotContain(Secret, text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(["ownerPid", "ownerStartedUtc", "sessionId"], document.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(Launcher.Id, document.RootElement.GetProperty("ownerPid").GetInt32());
        Assert.Equal("original-session", document.RootElement.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("other-session")]
    public async Task A_failed_or_unproven_read_does_not_rearm_and_is_not_repeated(string mode)
    {
        var connection = Connection();
        CodexHandle(connection);
        using var hub = new ProbeHandler { Mode = mode };
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Empty(restarted.Started);
        Assert.Single(hub.Requests);
        Assert.Equal(0, Status().GetProperty("observed").GetInt32());
        Assert.Equal(1, SkipCount("rearm_skip_refused"));
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));
    }

    [Fact]
    public async Task A_stale_handle_is_not_read_or_rearmed_even_when_it_was_armed_here()
    {
        var connection = Connection();
        var handle = CodexHandle(connection, opened: DateTimeOffset.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(NodeState.SessionHandlePath(connection.Folder, handle.SessionId), DateTime.UtcNow - LocalChatWake.RearmWindow - TimeSpan.FromMinutes(1));
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(1, SkipCount("rearm_skip_stale"));
    }

    [Fact]
    public async Task An_unlocated_or_unmapped_handle_is_not_read_or_rearmed_even_when_it_was_armed_here()
    {
        var connection = Connection();
        NodeState.WriteSessionHandle(connection.Folder, new("not-located", "private-session-token", "repo", "Codex", DateTimeOffset.UtcNow));
        NodeState.WriteSessionHandle(connection.Folder, new("unmapped-session", "private-session-token", "repo", "Codex", DateTimeOffset.UtcNow, Unmapped: true));
        // The writer never stores both, so a file holding a chat and unmapped:true can only come from elsewhere. It still counts as unmapped.
        var both = CodexHandle(connection, "both-session");
        var path = NodeState.SessionHandlePath(connection.Folder, both.SessionId);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["unmapped"] = true;
        File.WriteAllText(path, node.ToJsonString());
        Assert.True(NodeState.ReadSessionHandle(path)!.Unmapped);
        Assert.True(ArmedBy(connection, Launcher).Record("not-located"));
        Assert.True(ArmedBy(connection, Launcher).Record("unmapped-session"));
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(3, SkipCount("rearm_skip_unlocated"));
    }

    [Fact]
    public async Task A_claude_handle_is_not_read_or_rearmed_with_its_reason()
    {
        var connection = Connection();
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        NodeState.WriteSessionHandle(connection.Folder, new("claude-session", "private-session-token", "repo", "Claude", DateTimeOffset.UtcNow, Client: chat, Chat: chat));
        Assert.True(ArmedBy(connection, Launcher).Record("claude-session"));
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(1, SkipCount("rearm_skip_not_codex"));
    }

    [Fact]
    public async Task A_claude_adapter_never_rearms_a_codex_handle_and_keeps_no_records()
    {
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var connection = Connection(new AdapterOptions("http://127.0.0.1:5271/", Secret) { ExportedChat = chat });
        CodexHandle(connection);
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(0, Status().GetProperty("observed").GetInt32());
        Assert.Equal(1, SkipCount("rearm_skip_not_owned"));

        restarted.Wake.Observe(RelayTools.GetStatus, Arguments("claude-own-session"), new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = "claude-own-session" }), 0));
        Assert.False(ArmedBy(connection, Launcher).IsArmedHere("claude-own-session"));
    }

    [Fact]
    public async Task An_adapter_with_no_exported_chat_rearms_only_the_handle_it_armed_of_several_recent_ones()
    {
        var connection = Connection();
        var mine = CodexHandle(connection, "first-session");
        CodexHandle(connection, "second-session", armed: false);
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Equal(mine.SessionId, Assert.Single(restarted.Started).Session.SessionId);
        Assert.Equal(mine.SessionId, Assert.Single(hub.Requests).Session);
        Assert.Equal(1, SkipCount("rearm_skip_not_armed_here"));
    }

    [Fact]
    public async Task An_adapter_whose_owner_cannot_be_read_rearms_nothing_and_says_so()
    {
        var connection = Connection();
        CodexHandle(connection);
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub, noOwner: true);
        await restarted.Pass;
        Assert.Empty(hub.Requests);
        Assert.Empty(restarted.Started);
        Assert.Equal(1, SkipCount("rearm_skip_no_owner"));
    }

    [Fact]
    public async Task Only_the_newest_session_of_a_chat_is_rearmed()
    {
        var connection = Connection();
        var thread = Guid.NewGuid().ToString();
        CodexHandle(connection, "older-session", thread, DateTimeOffset.UtcNow.AddHours(-2));
        var newer = CodexHandle(connection, "newer-session", thread, DateTimeOffset.UtcNow.AddHours(-1));
        using var hub = new ProbeHandler();
        await using var restarted = Restart(connection, hub);
        await restarted.Pass;
        Assert.Equal(newer.SessionId, Assert.Single(restarted.Started).Session.SessionId);
        Assert.Equal(newer.SessionId, Assert.Single(hub.Requests).Session);
    }

    [Fact]
    public async Task An_unreachable_hub_stops_after_one_read_and_retries_on_the_timer()
    {
        var connection = Connection();
        CodexHandle(connection, "first-session");
        CodexHandle(connection, "second-session");
        var clock = new ManualClock();
        using var hub = new ProbeHandler { Mode = "unreachable" };
        await using var restarted = Restart(connection, hub, clock: clock);
        await restarted.Pass;
        Assert.Empty(restarted.Started);
        Assert.Single(hub.Requests);
        Assert.Equal(1, SkipCount("rearm_hub_unavailable"));

        Volatile.Write(ref hub.Mode, "ok");
        clock.Advance(LocalChatWake.RearmRetry + TimeSpan.FromSeconds(1));
        await WaitFor(() => restarted.Started.Count == 2);
    }

    [Fact]
    public async Task Overlapping_backends_of_one_owner_dispatch_once_and_the_survivor_takes_over()
    {
        var connection = Connection();
        var handle = CodexHandle(connection);
        using var hub = new ProbeHandler();
        var firstAdapter = Restart(connection, hub, Path.Combine(root, "first-status"));
        await firstAdapter.Pass;
        await using var secondAdapter = Restart(connection, hub, Path.Combine(root, "second-status"));
        await secondAdapter.Pass;
        Assert.Equal(handle.Chat!.ThreadId, Assert.Single(firstAdapter.Started).Route.ThreadId);
        Assert.Empty(secondAdapter.Started);
        Assert.Null(LocalWakeStatus.Problem(secondAdapter.StatusRoot));

        await firstAdapter.DisposeAsync();
        await WaitFor(() => secondAdapter.Started.Count == 1);
    }

    [Fact]
    public void Records_of_ended_owners_gone_sessions_and_unreadable_files_are_pruned()
    {
        var connection = Connection();
        var live = new ArmedChats(connection.Folder, Launcher, owner => owner.Id == Launcher.Id);
        var ended = new ArmedChats(connection.Folder, OtherLauncher, owner => owner.Id == Launcher.Id);
        Assert.True(live.Record("kept-session"));
        Assert.True(live.Record("gone-session"));
        Assert.True(ended.Record("ended-session"));
        var folder = Path.Combine(connection.Folder, "armed");
        File.WriteAllText(Path.Combine(folder, "junk.1.json"), "not json");

        live.Prune(session => session != "gone-session");

        Assert.True(live.IsArmedHere("kept-session"));
        Assert.False(live.IsArmedHere("gone-session"));
        Assert.False(ended.IsArmedHere("ended-session"));
        Assert.Single(Directory.GetFiles(folder));
    }

    private static readonly DateTime OwnerT0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private static string? Marked(string name) => name == "COCERN_DEVELOPMENT_GENERATION" ? "generation" : null;
    private static string? Unmarked(string name) => null;
    private static Func<int, ProcessFacts?> Tree(params ProcessFacts[] processes)
    {
        var known = processes.ToDictionary(x => x.Id);
        return id => known.GetValueOrDefault(id);
    }

    [Fact]
    public void A_current_launcher_is_the_direct_parent_and_stays_the_owner_across_a_swap()
    {
        var launcher = new ProcessFacts(10, 1, "dotnet", OwnerT0);
        var read = Tree(launcher, new(20, 10, "dotnet", OwnerT0.AddMinutes(1)), new(30, 10, "dotnet", OwnerT0.AddMinutes(9)));
        Assert.Equal(launcher, ArmedChats.OwnerOf(20, Marked, read, out var first));
        Assert.Equal(launcher, ArmedChats.OwnerOf(30, Marked, read, out var second));
        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public void An_older_launcher_behind_one_cmd_stays_the_owner_though_the_cmd_changes_on_a_swap()
    {
        var launcher = new ProcessFacts(10, 1, "dotnet", OwnerT0);
        var read = Tree(launcher,
            new(11, 10, "cmd", OwnerT0.AddMinutes(1)), new(20, 11, "dotnet", OwnerT0.AddMinutes(1)),
            new(12, 10, "CMD.EXE", OwnerT0.AddMinutes(9)), new(30, 12, "dotnet", OwnerT0.AddMinutes(9)));
        Assert.Equal(launcher, ArmedChats.OwnerOf(20, Marked, read, out var first));
        Assert.Equal(launcher, ArmedChats.OwnerOf(30, Marked, read, out var second));
        Assert.Null(first);
        Assert.Null(second);

        var connection = Connection();
        Assert.True(ArmedBy(connection, ArmedChats.OwnerOf(20, Marked, read, out _)).Record("original-session"));
        Assert.True(ArmedBy(connection, ArmedChats.OwnerOf(30, Marked, read, out _)).IsArmedHere("original-session"));
    }

    private static readonly Dictionary<string, ProcessFacts[]> NoLauncherShapes = new()
    {
        ["parent is not dotnet"] = [new(10, 1, "node", OwnerT0)],
        ["parent is gone"] = [],
        ["direct parent is younger"] = [new(10, 1, "dotnet", OwnerT0.AddHours(1))],
        ["cmd has no parent"] = [new(10, 9, "cmd", OwnerT0)],
        ["cmd's parent is not dotnet"] = [new(10, 9, "cmd", OwnerT0), new(9, 1, "node", OwnerT0)],
        ["cmd's parent is younger than the cmd"] = [new(10, 9, "cmd", OwnerT0), new(9, 1, "dotnet", OwnerT0.AddSeconds(30))],
        ["the cmd is younger than this process"] = [new(10, 9, "cmd", OwnerT0.AddHours(1)), new(9, 1, "dotnet", OwnerT0)],
        ["two cmds are more than the one step"] = [new(10, 9, "cmd", OwnerT0), new(9, 8, "cmd", OwnerT0), new(8, 1, "dotnet", OwnerT0)]
    };

    [Theory]
    [InlineData("parent is not dotnet")]
    [InlineData("parent is gone")]
    [InlineData("direct parent is younger")]
    [InlineData("cmd has no parent")]
    [InlineData("cmd's parent is not dotnet")]
    [InlineData("cmd's parent is younger than the cmd")]
    [InlineData("the cmd is younger than this process")]
    [InlineData("two cmds are more than the one step")]
    public void A_marked_backend_without_a_verified_launcher_owns_itself_and_says_why(string shape)
    {
        var self = new ProcessFacts(20, 10, "dotnet", OwnerT0.AddMinutes(1));
        Assert.Equal(self, ArmedChats.OwnerOf(20, Marked, Tree([self, .. NoLauncherShapes[shape]]), out var fallback));
        Assert.Equal(LocalWakeCodes.RearmOwnerNotLauncher, fallback);
    }

    [Fact]
    public void An_unmarked_process_owns_itself_with_no_reason_and_an_unreadable_one_has_no_owner()
    {
        var launcher = new ProcessFacts(10, 1, "dotnet", OwnerT0);
        var self = new ProcessFacts(20, 10, "dotnet", OwnerT0.AddMinutes(1));
        Assert.Equal(self, ArmedChats.OwnerOf(20, Unmarked, Tree(launcher, self), out var unmarked));
        Assert.Null(unmarked);
        Assert.Null(ArmedChats.OwnerOf(99, Marked, Tree(launcher, self), out var unreadable));
        Assert.Null(unreadable);
    }

    [Fact]
    public async Task The_loop_records_why_a_marked_backend_owns_itself()
    {
        var connection = Connection();
        using var hub = new ProbeHandler();
        var transport = new NodeTransport { Locators = [], CodexExecutable = () => "synthetic-codex", CodexDispatcher = _ => new NoDispatch() };
        await using var wake = new LocalChatWake(connection, transport, (_, _, ct) => Task.Delay(Timeout.Infinite, ct), StatusRoot, hub,
            new ArmedChats(connection.Folder, Launcher, _ => true, LocalWakeCodes.RearmOwnerNotLauncher));
        _ = wake.RunAsync(CancellationToken.None);
        await wake.FirstPass.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, SkipCount("rearm_owner_not_launcher"));
        Assert.Null(LocalWakeStatus.Problem(StatusRoot));
    }

    private LocalWakeConnection Connection(AdapterOptions? options = null) => LocalWakeConnection.Create(options ?? new("http://127.0.0.1:5271/", Secret, admin: true), TokenFile(), Path.Combine(root, "cache"))!;
    private static JsonElement Arguments(string session = "original-session") => JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = session, sessionToken = "private-session-token" });

    private sealed class ReplyHandler : HttpMessageHandler
    {
        public bool Ok { get; set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var args = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var sessionId = args.RootElement.GetProperty("sessionId").GetString();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = Ok, code = Ok ? "Ok" : "SessionDenied", dataJson = JsonSerializer.Serialize(new { sessionId }), version = 0 })) };
        }
    }

    private sealed class NoDispatch : IWakeDispatcher
    {
        public bool IsBusy(string threadId) => false;
        public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken ct) => Task.FromResult(true);
    }

    private static void AssertPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.False(rule.IsInherited);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private string TokenFile()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "token");
        File.WriteAllText(path, Secret);
        return path;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

using System.Text.Json;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;
using static CoCern.Tests.AdapterTestKit;

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
        var cache = Path.Combine(root, "cache");
        Assert.Empty(Directory.Exists(cache) ? Directory.EnumerateFiles(cache, LocalWakeConnection.FileName, SearchOption.AllDirectories) : Array.Empty<string>());
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
    public void Stop_hook_follows_the_latest_record_of_one_session_and_refuses_tied_different_sessions_or_tampered_metadata()
    {
        var cache = Path.Combine(root, "cache");
        var first = Connection();
        var chat = Guid.NewGuid().ToString();
        var opened = DateTimeOffset.UtcNow;
        var handle = new SessionHandle("claude-session", "session-token", "repo", "Claude", opened, Client: new("claude", chat));
        NodeState.WriteSessionHandle(first.Folder, handle);
        Assert.Equal(first.Folder, LocalWakeConnection.FindForClaude(chat, cache));
        // The same hub session re-recorded through a second connection (another address or pin) is one session: the latest record is the live connection.
        var second = Connection(new AdapterOptions("http://127.0.0.1:5272/", Secret));
        NodeState.WriteSessionHandle(second.Folder, handle with { OpenedAt = opened.AddSeconds(1) });
        Assert.Equal(second.Folder, LocalWakeConnection.FindForClaude(chat, cache));
        // Duplicate records may sort before an equally recent different session: inspect every tied candidate, not just the next one.
        var third = Connection(new AdapterOptions("http://127.0.0.1:5274/", Secret));
        var fourth = Connection(new AdapterOptions("http://127.0.0.1:5275/", Secret));
        File.Delete(NodeState.SessionHandlePath(second.Folder, "claude-session"));
        var tiedFolders = new[] { second.Folder, third.Folder, fourth.Folder }.Order(StringComparer.Ordinal).ToArray();
        var stamp = DateTime.UtcNow.AddMinutes(-1);
        for (var i = 0; i < tiedFolders.Length; i++)
        {
            var session = i < 2 ? "claude-session" : "other-session";
            NodeState.WriteSessionHandle(tiedFolders[i], handle with { SessionId = session, OpenedAt = opened.AddSeconds(1) });
            File.SetLastWriteTimeUtc(NodeState.SessionHandlePath(tiedFolders[i], session), stamp);
        }
        Assert.Null(LocalWakeConnection.FindForClaude(chat, cache));
        // The chat moving on to a newer session is followed there.
        File.SetLastWriteTimeUtc(NodeState.SessionHandlePath(tiedFolders[2], "other-session"), stamp.AddSeconds(5));
        Assert.Equal(tiedFolders[2], LocalWakeConnection.FindForClaude(chat, cache));
        // Tampered connection metadata takes that folder out of the running.
        foreach (var folder in tiedFolders)
        {
            var path = Path.Combine(folder, "connection.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("127.0.0.1", "127.0.0.2"));
            Assert.Null(LocalWakeConnection.Read(folder));
        }
        Assert.Equal(first.Folder, LocalWakeConnection.FindForClaude(chat, cache));
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

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public string Mode = "ok";
        public TaskCompletionSource? Hold;
        public volatile string? Origin;
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Tool, bool Background, string? Session, string? Token)> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Volatile.Read(ref Mode) == "unreachable") { Requests.Enqueue((request.RequestUri!.Segments[^1], false, null, null)); throw new HttpRequestException("synthetic"); }
            using var args = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var data = args.RootElement;
            var session = data.GetProperty("sessionId").GetString();
            Origin = data.TryGetProperty("callOrigin", out var origin) ? origin.GetString() : null;
            Requests.Enqueue((request.RequestUri!.Segments[^1], data.TryGetProperty("backgroundCall", out var background) && background.GetBoolean(), session, data.GetProperty("sessionToken").GetString()));
            if (Volatile.Read(ref Hold) is { } hold) await hold.Task.WaitAsync(ct);
            var mode = Volatile.Read(ref Mode);
            var ok = mode is not ("denied" or "busy");
            var said = mode == "other-session" ? "another-session" : session;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok, code = ok ? "Ok" : mode == "busy" ? "Busy" : "SessionDenied", dataJson = JsonSerializer.Serialize(new { sessionId = said }), version = 0 })) };
        }
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
        Assert.Equal(CallOrigins.WakeWatcher, hub.Origin);
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

    private string TokenFile()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "token");
        File.WriteAllText(path, Secret);
        return path;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

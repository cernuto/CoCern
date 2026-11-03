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

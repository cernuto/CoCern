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
/// What one chat's adapter adds to its calls and takes from their replies: request IDs that stay the chat's own across adapter restarts, the
/// chat's own session on <c>close_sessions</c>, a rename passed to the node's handle, and git started only by its full path from PATH.
/// </summary>
public sealed class AdapterChatCallsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-chat-calls-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ---- Request IDs ----

    [Fact]
    public void A_chat_with_a_known_id_keeps_its_request_scope_across_adapter_processes_and_two_chats_never_share_one()
    {
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var other = new ChatLocation("claude", Guid.NewGuid().ToString());
        var scope = CoordinationTools.RequestScope(chat);
        // A restarted adapter computes it from the same chat ID, with nothing kept from the process before.
        Assert.Equal(scope, CoordinationTools.RequestScope(new ChatLocation("claude", chat.ThreadId.ToUpperInvariant())));
        Assert.NotEqual(scope, CoordinationTools.RequestScope(other));
        Assert.NotEqual(scope, CoordinationTools.RequestScope(new ChatLocation("codex", chat.ThreadId)));
        // The chat ID itself never reaches the hub.
        Assert.DoesNotContain(chat.ThreadId, scope, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(32, scope.Length);

        // Without a chat ID, the scope is this process's own: stable within the process, different from any chat's.
        var fallback = CoordinationTools.RequestScope(null);
        Assert.Equal(fallback, CoordinationTools.RequestScope(null));
        Assert.NotEqual(fallback, scope);
        var arguments = JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1" });
        Assert.Equal(fallback + ":open-1", CoordinationTools.OwnRequestId(arguments, null).GetProperty("requestId").GetString());
        Assert.Equal(scope + ":open-1", CoordinationTools.OwnRequestId(arguments, null, scope).GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task A_session_open_retried_by_a_restarted_adapter_in_the_same_chat_returns_the_same_session()
    {
        await using var host = Hub();
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        async Task<string> Open(ChatLocation? who)
        {
            // A new ChatCalls is what a new adapter process builds: nothing carries over but what it computes from the chat ID.
            var calls = new ChatCalls(Forwarder(host), null, null, CoordinationTools.RequestScope(who), () => null);
            var reply = await calls.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1", label = "Chat" }), CancellationToken.None);
            Assert.True(reply.Ok, reply.Code);
            return reply.Data!.Value.GetProperty("sessionId").GetString()!;
        }

        var first = await Open(chat);
        Assert.Equal(first, await Open(chat));
        // Another chat on the same PC (the same principal) reusing "open-1" gets a session of its own.
        Assert.NotEqual(first, await Open(new ChatLocation("claude", Guid.NewGuid().ToString())));
    }

    // ---- close_sessions spares this chat ----

    [Fact]
    public void Close_sessions_gains_the_chats_own_session_only_when_the_call_names_none()
    {
        var close = JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "c-1", command = "close_sessions", labelPrefix = "Smoke" });
        Assert.Equal("mine", CoordinationTools.WithOwnSession(close, "mine").GetProperty("sessionId").GetString());
        // Without a known session, or for another command, or when the caller named one (even blank), nothing is added.
        Assert.False(CoordinationTools.WithOwnSession(close, null).TryGetProperty("sessionId", out _));
        var pause = JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "p-1", command = "pause" });
        Assert.False(CoordinationTools.WithOwnSession(pause, "mine").TryGetProperty("sessionId", out _));
        var named = JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "c-2", command = "close_sessions", labelPrefix = "Smoke", sessionId = "" });
        Assert.Equal("", CoordinationTools.WithOwnSession(named, "mine").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task A_label_prefix_close_from_a_chat_never_closes_that_chat()
    {
        await using var host = Hub(admin: true);
        var mine = new ChatCalls(Forwarder(host, admin: true), null, null, CoordinationTools.RequestScope(new ChatLocation("claude", Guid.NewGuid().ToString())), () => null);
        var other = new ChatCalls(Forwarder(host, admin: true), null, null, CoordinationTools.RequestScope(new ChatLocation("claude", Guid.NewGuid().ToString())), () => null);
        var opened = await mine.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1", label = "Smoke mine" }), CancellationToken.None);
        var theirs = await other.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1", label = "Smoke other" }), CancellationToken.None);
        Assert.True(opened.Ok && theirs.Ok, opened.Code + " " + theirs.Code);
        var (id, token) = (opened.Data!.Value.GetProperty("sessionId").GetString()!, opened.Data!.Value.GetProperty("sessionToken").GetString()!);
        Assert.Equal(id, mine.CurrentSession);

        var closed = await mine.CallAsync(RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "close-1", command = "close_sessions", labelPrefix = "smoke" }), CancellationToken.None);

        Assert.True(closed.Ok, closed.Code);
        Assert.Equal(1, closed.Data!.Value.GetProperty("closed").GetInt32());
        Assert.Equal([theirs.Data!.Value.GetProperty("sessionId").GetString()], closed.Data!.Value.GetProperty("ids").EnumerateArray().Select(x => x.GetString()));
        Assert.True((await mine.CallAsync(RelayTools.CheckInbox, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = id, sessionToken = token }), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task The_current_session_is_learned_from_session_open_and_from_calls_made_with_its_token()
    {
        using var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(admin: true), null, null, "scope", () => null);
        Assert.Null(calls.CurrentSession);
        // An adapter restarted mid-chat learns it from the chat's next call with its handle.
        Assert.True((await calls.CallAsync(RelayTools.CheckInbox, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "s-old", sessionToken = "t" }), CancellationToken.None)).Ok);
        Assert.Equal("s-old", calls.CurrentSession);
        // A call naming a session without its token proves nothing.
        Assert.True((await calls.CallAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "s-other" }), CancellationToken.None)).Ok);
        Assert.Equal("s-old", calls.CurrentSession);
        Assert.True((await calls.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1" }), CancellationToken.None)).Ok);
        Assert.Equal("s-1", calls.CurrentSession);

        Assert.True((await calls.CallAsync(RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "close-1", command = "close_sessions", idleMinutes = 60 }), CancellationToken.None)).Ok);
        Assert.Equal("s-1", hub.Sent(RelayTools.ControlRequest)["sessionId"]!.GetValue<string>());
    }

    // ---- A rename reaches the node ----

    [Fact]
    public async Task A_rename_through_session_bind_reaches_the_nodes_handle_and_keeps_the_rest_of_it()
    {
        using var hub = new FakeHub();
        var folder = Path.Combine(root, "node");
        var calls = new ChatCalls(hub.Forwarder(), null, SessionHandoff.ForFolder(folder), "scope", () => null);
        Assert.True((await calls.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1", label = "First name" }), CancellationToken.None)).Ok);
        var path = NodeState.SessionHandlePath(folder, "s-1");
        var located = NodeState.ReadSessionHandle(path)! with { Chat = new ChatLocation("codex", Guid.NewGuid().ToString()) };
        NodeState.WriteSessionHandle(folder, located);

        async Task Bind(object arguments, bool ok = true)
        {
            hub.Ok = ok;
            await calls.CallAsync(RelayTools.SessionBind, JsonSerializer.SerializeToElement(arguments), CancellationToken.None);
            hub.Ok = true;
        }

        await Bind(new { repositoryId = "repo", requestId = "bind-1", sessionId = "s-1", sessionToken = "t-1", expectedVersion = 0, binding = new { }, label = "  Run a smoke test " });
        var renamed = NodeState.ReadSessionHandle(path)!;
        Assert.Equal("Run a smoke test", renamed.Label);
        Assert.Equal(located with { Label = "Run a smoke test" }, renamed);

        // A bind without a label, or one the hub refused, leaves the name as it is.
        await Bind(new { repositoryId = "repo", requestId = "bind-2", sessionId = "s-1", sessionToken = "t-1", expectedVersion = 1, binding = new { } });
        await Bind(new { repositoryId = "repo", requestId = "bind-3", sessionId = "s-1", sessionToken = "t-1", expectedVersion = 2, binding = new { }, label = "Refused" }, ok: false);
        Assert.Equal("Run a smoke test", NodeState.ReadSessionHandle(path)!.Label);

        // A session this PC has no handle for gets none.
        await Bind(new { repositoryId = "repo", requestId = "bind-4", sessionId = "s-elsewhere", sessionToken = "t", expectedVersion = 0, binding = new { }, label = "Elsewhere" });
        Assert.False(File.Exists(NodeState.SessionHandlePath(folder, "s-elsewhere")));
    }

    // ---- git by its full path ----

    [Fact]
    public void Git_is_found_only_in_absolute_path_folders()
    {
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        var programs = new HashSet<string>(StringComparer.Ordinal) { Path.Combine(b, "git"), Path.Combine("repo", "git"), Path.Combine(".", "git") };
        var path = string.Join(':', ".", "", "repo", a, b);
        Assert.Equal(Path.Combine(b, "git"), ExecutableSearch.Find("git", path, null, windows: false, programs.Contains));
        // A PATH of nothing but the current or relative folders finds nothing, so nothing runs.
        Assert.Null(ExecutableSearch.Find("git", ".:repo:", null, windows: false, programs.Contains));
        Assert.Null(ExecutableSearch.Find("git", null, null, windows: false, programs.Contains));
        Assert.Null(CoordinationTools.GitTopLevel(root, git: null));
    }

    [Fact]
    public void On_windows_each_folder_is_tried_with_each_startable_extension_in_pathext_order()
    {
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        var tried = new List<string>();
        var programs = new HashSet<string>(StringComparer.Ordinal) { Path.Combine(a, "git.cmd"), Path.Combine(a, "git.exe"), Path.Combine(b, "git.exe") };
        bool IsProgram(string candidate) { tried.Add(candidate); return programs.Contains(candidate); }

        Assert.Equal(Path.Combine(a, "git.exe"), ExecutableSearch.Find("git", $"\"{a}\";.;{b}", ".JS;.EXE;.CMD", windows: true, IsProgram));
        Assert.Equal([Path.Combine(a, "git.exe")], tried);
        tried.Clear();
        Assert.Equal(Path.Combine(a, "git.cmd"), ExecutableSearch.Find("git", $"{a};{b}", ".CMD;.EXE", windows: true, IsProgram));
        // The folder order comes first: a later folder's git.exe never beats an earlier folder's git.cmd.
        programs.Remove(Path.Combine(a, "git.exe"));
        Assert.Equal(Path.Combine(a, "git.cmd"), ExecutableSearch.Find("git", $"{a};{b}", null, windows: true, IsProgram));
        tried.Clear();
        Assert.Null(ExecutableSearch.Find("git", $"{Path.Combine(root, "none")}", ".VBS", windows: true, IsProgram));
        // A PATHEXT naming nothing a process starts from falls back to the default order.
        Assert.Equal(new[] { ".com", ".exe", ".bat", ".cmd" }.Select(x => Path.Combine(root, "none", "git" + x)), tried);
    }

    [Fact]
    public void The_real_search_finds_an_installed_git_by_its_full_path()
    {
        if (ExecutableSearch.Find("git") is not { } git) return;
        Assert.True(Path.IsPathFullyQualified(git));
        Assert.True(File.Exists(git));
    }

    // ---- Helpers ----

    private static RelayHostFixture Hub(bool admin = false) => new(remoteAddress: IPAddress.Loopback, settings: admin ? new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true" } : null);

    private static RelayForwarder Forwarder(RelayHostFixture host, bool admin = false) =>
        new(new HttpClient(host.Server.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) }, new AdapterOptions("http://127.0.0.1:5271", RelayHostFixture.Token, admin));

    /// <summary>A hub that answers every tool: <c>session_open</c> with session <c>s-1</c>, anything else with empty data; or a refusal while <see cref="Ok"/> is false.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        private readonly Dictionary<string, JsonObject> _sent = new(StringComparer.Ordinal);
        public bool Ok { get; set; } = true;

        public JsonObject Sent(string tool) { lock (_sent) return _sent[tool]; }

        public RelayForwarder Forwarder(bool admin = false) => new(new HttpClient(this, disposeHandler: false), new AdapterOptions("http://127.0.0.1:5271", "client-secret", admin));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tool = request.RequestUri!.Segments[^1];
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            lock (_sent) _sent[tool] = body;
            var data = tool == RelayTools.SessionOpen ? "{\"sessionId\":\"s-1\",\"sessionToken\":\"t-1\"}" : "{}";
            var envelope = Ok ? new { ok = true, code = "ok", dataJson = data, version = 1 } : new { ok = false, code = RelayErrorCodes.InvalidArguments, dataJson = "{}", version = 1 };
            return new(Ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json") };
        }
    }
}

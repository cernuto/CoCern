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
        // Both chats just called, so the close by label keeps them as working right now. Only the other chat counts as a match: the adapter
        // named this chat's session as the caller's own, which a close never takes or counts.
        Assert.Equal((0, 1), (closed.Data!.Value.GetProperty("closed").GetInt32(), closed.Data!.Value.GetProperty("active").GetInt32()));
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

    // ---- A chat need not carry its own session ----

    private static JsonElement Repo(string repository = "repo") => JsonSerializer.SerializeToElement(new { repositoryId = repository });

    private static (string? Id, string? Token) Handle(JsonObject sent) => (sent["sessionId"]?.GetValue<string>(), sent["sessionToken"]?.GetValue<string>());

    [Fact]
    public async Task A_call_that_leaves_out_its_session_gets_the_one_this_chat_opened()
    {
        using var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(), null, null, "scope", () => null);
        Assert.True((await calls.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1" }), CancellationToken.None)).Ok);

        Assert.True((await calls.CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None)).Ok);

        Assert.Equal(("s-1", "t-1"), Handle(hub.Sent(RelayTools.CheckInbox)));
    }

    [Fact]
    public async Task A_session_used_with_its_token_is_kept_for_the_calls_after_it()
    {
        using var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(), null, null, "scope", () => null);
        await calls.CallAsync(RelayTools.CheckInbox, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "s-old", sessionToken = "t-old" }), CancellationToken.None);

        await calls.CallAsync(RelayTools.Ack, Repo(), CancellationToken.None);

        Assert.Equal(("s-old", "t-old"), Handle(hub.Sent(RelayTools.Ack)));
    }

    [Fact]
    public async Task A_restarted_adapter_finds_the_chats_session_in_its_handle_and_never_another_chats()
    {
        using var hub = new FakeHub();
        var folder = Path.Combine(root, "node");
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        Assert.True((await new ChatCalls(hub.Forwarder(), null, SessionHandoff.ForFolder(folder, chat), "scope", () => null)
            .CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1" }), CancellationToken.None)).Ok);

        await new ChatCalls(hub.Forwarder(), null, SessionHandoff.ForFolder(folder, chat), "scope", () => null).CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None);
        Assert.Equal(("s-1", "t-1"), Handle(hub.Sent(RelayTools.CheckInbox)));

        // Another chat on this PC, and an adapter that was told no chat ID and so can't say whose handle is whose, add nothing.
        await new ChatCalls(hub.Forwarder(), null, SessionHandoff.ForFolder(folder, new ChatLocation("claude", Guid.NewGuid().ToString())), "scope", () => null).CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None);
        Assert.Equal((null, null), Handle(hub.Sent(RelayTools.CheckInbox)));
        await new ChatCalls(hub.Forwarder(), null, SessionHandoff.ForFolder(folder), "scope", () => null).CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None);
        Assert.Equal((null, null), Handle(hub.Sent(RelayTools.CheckInbox)));
    }

    [Fact]
    public async Task The_handle_found_is_the_chats_newest_in_that_repository_and_not_one_located_on_another_chat()
    {
        using var hub = new FakeHub();
        var folder = Path.Combine(root, "node");
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var now = DateTimeOffset.UtcNow;
        NodeState.WriteSessionHandle(folder, new SessionHandle("older", "t-older", "repo", "Chat", now.AddHours(-1), chat));
        NodeState.WriteSessionHandle(folder, new SessionHandle("newest", "t-newest", "repo", "Chat", now, chat));
        NodeState.WriteSessionHandle(folder, new SessionHandle("elsewhere", "t-elsewhere", "other-repo", "Chat", now.AddHours(1), chat));
        NodeState.WriteSessionHandle(folder, new SessionHandle("moved", "t-moved", "repo", "Chat", now.AddHours(2), chat, new ChatLocation("claude", Guid.NewGuid().ToString())));
        var calls = new ChatCalls(hub.Forwarder(), null, SessionHandoff.ForFolder(folder, chat), "scope", () => null);

        await calls.CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None);

        Assert.Equal(("newest", "t-newest"), Handle(hub.Sent(RelayTools.CheckInbox)));
    }

    /// <summary>Every way of finding a chat's session agrees: its waiter, its prompt hook and its adapter's fill-in.</summary>
    private static void AssertChosen(string folder, ChatLocation chat, string expected)
    {
        Assert.Equal(expected, ClaudeWaiter.FindHandle(folder, chat.ThreadId)?.SessionId);
        Assert.Equal(expected, PromptHook.FindHandle(folder, chat.ThreadId)?.SessionId);
        Assert.Equal(expected, SessionHandoff.ForFolder(folder, chat).FindOwn("repo")?.SessionId);
    }

    [Fact]
    public void A_newer_session_that_only_carries_the_chats_id_never_replaces_the_one_found_in_its_transcript()
    {
        var folder = Path.Combine(root, "node");
        var chat = new ChatLocation(ChatProviders.Claude, Guid.NewGuid().ToString());
        var now = DateTimeOffset.UtcNow;
        // Anything started from the chat inherits its ID, as a test script's own sessions did.
        NodeState.WriteSessionHandle(folder, new SessionHandle("inherited", "t", "repo", "Script", now.AddHours(-1), chat, Unmapped: true));
        AssertChosen(folder, chat, "inherited");
        NodeState.WriteSessionHandle(folder, new SessionHandle("pending", "t", "repo", "Just opened", now.AddHours(-2), chat));
        AssertChosen(folder, chat, "pending");
        NodeState.WriteSessionHandle(folder, new SessionHandle("real", "t", "repo", "Chat", now.AddHours(-3), chat, chat));
        AssertChosen(folder, chat, "real");
        NodeState.WriteSessionHandle(folder, new SessionHandle("still-pending", "t", "repo", "Script", now, chat));
        AssertChosen(folder, chat, "real");
        NodeState.WriteSessionHandle(folder, new SessionHandle("gave-up", "t", "repo", "Script", now.AddHours(1), chat, Unmapped: true));
        AssertChosen(folder, chat, "real");

        // A session the chat really moved to is found in its transcript too, and then the newest of those wins.
        NodeState.WriteSessionHandle(folder, new SessionHandle("replacement", "t", "repo", "Chat", now.AddMinutes(-1), chat, chat));
        AssertChosen(folder, chat, "replacement");
    }

    [Fact]
    public async Task Values_the_caller_gave_stand_and_only_calls_that_need_a_session_in_its_repository_are_filled()
    {
        using var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(), null, null, "scope", () => null);
        await calls.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1" }), CancellationToken.None);

        async Task<(string? Id, string? Token)> Check(object arguments)
        {
            await calls.CallAsync(RelayTools.CheckInbox, JsonSerializer.SerializeToElement(arguments), CancellationToken.None);
            return Handle(hub.Sent(RelayTools.CheckInbox));
        }

        // Its own session, named without the token, gains the token; another session never gains this chat's token.
        Assert.Equal(("s-1", "t-1"), await Check(new { repositoryId = "repo", sessionId = "s-1" }));
        Assert.Equal(("s-other", null), await Check(new { repositoryId = "repo", sessionId = "s-other" }));
        // A token the caller gave stands, even blank.
        Assert.Equal((null, ""), await Check(new { repositoryId = "repo", sessionToken = "" }));
        Assert.Equal(("s-mine", "t-mine"), await Check(new { repositoryId = "repo", sessionId = "s-mine", sessionToken = "t-mine" }));
        // An explicitly present credential is never corrected, even when unusable or missing its other half.
        foreach (var fragment in new[] { """{"sessionId":null}""", """{"sessionId":7}""", """{"sessionId":""}""", """{"sessionToken":null}""", """{"sessionToken":7}""", """{"sessionToken":"partial"}""" })
        {
            var input = JsonNode.Parse(fragment)!.AsObject();
            input["repositoryId"] = "repo";
            input["requestId"] = "same-request";
            await calls.CallAsync(RelayTools.Ack, JsonSerializer.SerializeToElement(input), CancellationToken.None);
            var sent = hub.Sent(RelayTools.Ack);
            foreach (var field in new[] { "sessionId", "sessionToken" })
            {
                Assert.Equal(input.ContainsKey(field), sent.ContainsKey(field));
                Assert.True(JsonNode.DeepEquals(input[field], sent[field]));
            }
        }
        // A session belongs to its repository.
        Assert.Equal((null, null), await Check(new { repositoryId = "other-repo" }));

        // Calls that take no session are sent as they were.
        await calls.CallAsync(RelayTools.GetStatus, Repo(), CancellationToken.None);
        Assert.Equal((null, null), Handle(hub.Sent(RelayTools.GetStatus)));
        await calls.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-2" }), CancellationToken.None);
        Assert.Equal((null, null), Handle(hub.Sent(RelayTools.SessionOpen)));
    }

    [Fact]
    public async Task A_local_connection_finds_the_chats_handle_after_a_restart()
    {
        using var hub = new FakeHub();
        var cache = Path.Combine(root, "local");
        var tokenFile = Path.Combine(root, "token.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(tokenFile, "client-secret");
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var options = new AdapterOptions("http://127.0.0.1:5271", "client-secret", false) { ExportedChat = chat };
        await using (var first = LocalChatWake.TryCreate(options, tokenFile, cache))
        {
            Assert.NotNull(first);
            await new ChatCalls(hub.Forwarder(), null, null, "scope", () => null, first).CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1" }), CancellationToken.None);
        }

        await using var restarted = LocalChatWake.TryCreate(options, tokenFile, cache);
        await new ChatCalls(hub.Forwarder(), null, null, "scope", () => null, restarted).CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None);

        Assert.Equal(("s-1", "t-1"), Handle(hub.Sent(RelayTools.CheckInbox)));
    }

    [Fact]
    public async Task An_implicit_send_with_a_lost_reply_retries_in_its_original_session_after_the_default_changes()
    {
        await using var host = Hub();
        var loss = new LoseFirstSendReply(host.Server.CreateHandler());
        using var client = new HttpClient(loss);
        var calls = new ChatCalls(new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", RelayHostFixture.Token, false)), null, null, "scope", () => null);
        var other = new ChatCalls(Forwarder(host), null, null, "other", () => null);
        async Task<AdapterReply> Call(string tool, object args) => await calls.CallAsync(tool, JsonSerializer.SerializeToElement(args), CancellationToken.None);
        var a = await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-a", label = "A" });
        var b = await other.CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-b", label = "B" }), CancellationToken.None);
        Assert.True(a.Ok && b.Ok, a.Code + " " + b.Code);
        var (idB, tokenB) = (b.Data!.Value.GetProperty("sessionId").GetString(), b.Data.Value.GetProperty("sessionToken").GetString());
        var send = new { repositoryId = "repo", requestId = "one-send", recipientSessionId = idB, text = "original" };

        var lost = await Call(RelayTools.SendMessage, send);
        Assert.Equal("hub_unavailable", lost.Code);
        Assert.NotNull(loss.CommittedMessageId);
        var learned = await Call(RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = idB, sessionToken = tokenB });
        Assert.True(learned.Ok, learned.Code);
        Assert.Equal(idB, calls.CurrentSession);

        var retries = await Task.WhenAll(Call(RelayTools.SendMessage, send), Call(RelayTools.SendMessage, send));
        foreach (var retry in retries)
        {
            Assert.True(retry.Ok, retry.Code);
            Assert.Equal(loss.CommittedMessageId, retry.Data!.Value.GetProperty("messageId").GetString());
        }
        Assert.Equal(idB, calls.CurrentSession);
        var changed = await Call(RelayTools.SendMessage, send with { text = "changed" });
        Assert.Equal(RelayErrorCodes.RequestConflict, changed.Code);

        // An old binding cannot supply credentials for a different explicitly named session.
        var partial = await Call(RelayTools.SendMessage, new { send.repositoryId, send.requestId, send.recipientSessionId, send.text, sessionId = idB });
        Assert.False(partial.Ok);
        var invalid = await Call(RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = "stranger", sessionToken = "invalid" });
        Assert.False(invalid.Ok);
        Assert.Equal(idB, calls.CurrentSession);
        foreach (var ordinary in new[] { lost, retries[0], changed, partial, invalid })
        {
            var json = JsonSerializer.Serialize(ordinary);
            Assert.DoesNotContain(a.Data!.Value.GetProperty("sessionToken").GetString()!, json);
            Assert.DoesNotContain(tokenB!, json);
        }

        // Fully explicit calls still use the hub's per-session replay scope.
        var explicitB = await Call(RelayTools.SendMessage, new { send.repositoryId, send.requestId, send.recipientSessionId, send.text, sessionId = idB, sessionToken = tokenB });
        Assert.True(explicitB.Ok, explicitB.Code);
        Assert.NotEqual(loss.CommittedMessageId, explicitB.Data!.Value.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task A_full_implicit_request_table_keeps_early_retries_and_leaves_explicit_writes_available()
    {
        using var hub = new FakeHub();
        var calls = new ChatCalls(hub.Forwarder(), null, null, "scope", () => null);
        Task<AdapterReply> Call(string tool, object args) => calls.CallAsync(tool, JsonSerializer.SerializeToElement(args), CancellationToken.None);
        Task<AdapterReply> Ack(int i) => Call(RelayTools.Ack, new { repositoryId = "repo", requestId = i.ToString(), messageIds = new[] { "m" } });
        Assert.True((await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open" })).Ok);
        for (var i = 0; i < 4096; i++) Assert.True((await Ack(i)).Ok);
        Assert.True((await Call(RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = "s-b", sessionToken = "t-b" })).Ok);

        var refused = await Ack(4096);
        Assert.False(refused.Ok);
        Assert.Equal("implicit_request_capacity", refused.Code);
        Assert.Equal("4095", hub.Sent(RelayTools.Ack)["requestId"]!.GetValue<string>());
        Assert.True((await Ack(0)).Ok);
        Assert.Equal(("s-1", "t-1"), Handle(hub.Sent(RelayTools.Ack)));
        Assert.True((await Call(RelayTools.Ack, new { repositoryId = "repo", requestId = "4096", sessionId = "s-b", sessionToken = "t-b", messageIds = new[] { "m" } })).Ok);
        Assert.Equal(("s-b", "t-b"), Handle(hub.Sent(RelayTools.Ack)));
    }

    private sealed class LoseFirstSendReply(HttpMessageHandler hub) : DelegatingHandler(hub)
    {
        public string? CommittedMessageId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.Segments[^1] == RelayTools.SendMessage && CommittedMessageId is null && response.IsSuccessStatusCode)
            {
                using (response)
                using (var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
                using (var data = JsonDocument.Parse(envelope.RootElement.GetProperty("dataJson").GetString()!))
                    CommittedMessageId = data.RootElement.GetProperty("messageId").GetString();
                throw new HttpRequestException("Reply lost after commit");
            }
            return response;
        }
    }

    [Fact]
    public async Task On_a_real_hub_a_chat_that_lost_its_handle_still_reads_its_inbox_but_a_stranger_is_denied()
    {
        await using var host = Hub();
        var folder = Path.Combine(root, "node");
        var chat = new ChatLocation("claude", Guid.NewGuid().ToString());
        var opened = await new ChatCalls(Forwarder(host), null, SessionHandoff.ForFolder(folder, chat), "scope", () => null)
            .CallAsync(RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1", label = "Chat" }), CancellationToken.None);
        Assert.True(opened.Ok, opened.Code);

        // What compaction leaves: the same chat, and a call that names only the repository.
        var restarted = new ChatCalls(Forwarder(host), null, SessionHandoff.ForFolder(folder, chat), "scope", () => null);
        var inbox = await restarted.CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None);
        Assert.True(inbox.Ok, inbox.Code);
        Assert.Equal(opened.Data!.Value.GetProperty("sessionId").GetString(), restarted.CurrentSession);

        var stranger = new ChatCalls(Forwarder(host), null, SessionHandoff.ForFolder(folder, new ChatLocation("claude", Guid.NewGuid().ToString())), "stranger", () => null);
        var denied = await stranger.CallAsync(RelayTools.CheckInbox, Repo(), CancellationToken.None);
        Assert.False(denied.Ok);
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
        // A synthetic UNC root is fully qualified on both platforms, without a drive colon in the Unix PATH.
        var a = Path.Combine("//server/share", "a");
        var b = Path.Combine("//server/share", "b");
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

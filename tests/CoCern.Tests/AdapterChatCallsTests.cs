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

    // ---- A chat need not carry its own session ----

    private static JsonElement Repo(string repository = "repo") => JsonSerializer.SerializeToElement(new { repositoryId = repository });

    private static (string? Id, string? Token) Handle(JsonObject sent) => (sent["sessionId"]?.GetValue<string>(), sent["sessionToken"]?.GetValue<string>());

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

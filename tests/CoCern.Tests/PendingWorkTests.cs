using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

public sealed class PendingWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inbox_wait_returns_a_current_complete_offer_after_mail_or_expiry(bool mail)
    {
        using var handler = new InboxHandler(mail);
        using var http = new HttpClient(handler);
        var result = await InboxWait.CheckAsync(new RelayForwarder(http, new AdapterOptions("http://127.0.0.1:5271", "synthetic-token")),
            JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "own", sessionToken = "synthetic-session", waitSeconds = 0 }),
            TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("T-1", result.Data!.Value.GetProperty("attention").GetProperty("scope").GetProperty("taskId").GetString());
        Assert.Equal(2, handler.Calls.Count);
        Assert.False(handler.Calls[0].GetProperty("attention").GetBoolean());
        Assert.False(handler.Calls[0].GetProperty("seen").GetBoolean());
        Assert.False(handler.Calls[1].TryGetProperty("attention", out _));
        Assert.False(handler.Calls[1].TryGetProperty("backgroundCall", out _));
        Assert.True(handler.Calls[1].GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task Failed_final_attention_read_preserves_mail_and_reports_unavailable()
    {
        using var handler = new InboxHandler(mail: true, failFinal: true);
        using var http = new HttpClient(handler);
        var result = await InboxWait.CheckAsync(new RelayForwarder(http, new AdapterOptions("http://127.0.0.1:5271", "synthetic-token")),
            JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = "own", sessionToken = "synthetic-session", waitSeconds = 0 }),
            TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.True(result.Ok);
        var data = result.Data!.Value;
        Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("unavailable", data.GetProperty("pendingWork").GetProperty("reason").GetString());
        Assert.Equal("hub_unavailable", data.GetProperty("pendingWork").GetProperty("code").GetString());
        Assert.False(handler.Calls[0].GetProperty("seen").GetBoolean());
    }

    [Fact]
    public void Read_only_offer_preview_keeps_full_scope_and_explains_focus_snooze_and_stale_ids()
    {
        var hub = new AggregateHub();
        var now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var chat = hub.Open("pending-work", now);
        var raised = new AttentionRaise(AggregateHub.Repository, new AttentionContent
        {
            Kind = AttentionKinds.NextWork, TaskId = "T-1", Subject = "review-request",
            Note = "Review this assigned task before deciding whether to claim it.", Refs = ["packet", "evidence"]
        });
        Assert.True(hub.Apply(AggregateKinds.Session, chat.Id, AttentionRaise.Operation, raised.ToJson(), AggregateHub.Alice with { IsInternal = true }, now).Ok);
        var state = hub.State(AggregateKinds.Session, chat.Id);
        var before = state.ToJsonString();
        var preview = SessionAttention.Status(new SessionState(state), new JsonObject(), now, detail: true);
        Assert.Equal(before, state.ToJsonString());
        Assert.Equal("review-request", preview["offer"]!["scope"]!["subject"]!.GetValue<string>());
        Assert.Equal("evidence", preview["offer"]!["scope"]!["refs"]![1]!.GetValue<string>());

        JsonObject Offer(string? focus = null) => AggregateHub.Data(hub.Apply(AggregateKinds.Session, chat.Id, AttentionOffer.Operation,
            new AttentionOffer(AggregateHub.Repository, chat.Id, chat.Token) { FocusTaskId = focus }.ToJson(), AggregateHub.Alice with { IsInternal = true }, now));
        var focused = Offer("T-1");
        Assert.Equal("focused_task", focused["pendingWork"]!["reason"]!.GetValue<string>());
        Assert.Equal(1, focused["pendingWork"]!["actionable"]!.GetValue<int>());
        Assert.Null(focused["attention"]);
        var offered = Offer();
        var id = offered["attention"]!["id"]!.GetValue<string>();
        Assert.Equal(preview["offer"]!["id"]!.GetValue<string>(), id);
        Assert.True(JsonNode.DeepEquals(preview["offer"]!["scope"], offered["attention"]!["scope"]));
        Assert.True(hub.Session(chat, RelayTools.AttentionRespond, new JsonObject { ["itemId"] = id, ["action"] = "later", ["snoozeBoundaries"] = 2 }, now).Ok);
        var snoozed = Offer();
        Assert.Equal("snoozed", snoozed["pendingWork"]!["reason"]!.GetValue<string>());
        var stale = hub.Session(chat, RelayTools.AttentionRespond, new JsonObject { ["itemId"] = "a-no-longer-current", ["action"] = "later" }, now);
        Assert.Equal(RelayErrorCodes.NotFound, stale.Code);
        Assert.Equal("expired_or_resolved", JsonNode.Parse(stale.DataJson)!["pendingWork"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Model_visible_inbox_has_offer_scope_or_reason_and_opt_out_is_explicit()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var opened = (await host.Call(client, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "pending-work-open" })).GetProperty("data");
        var id = opened.GetProperty("sessionId").GetString();
        var token = opened.GetProperty("sessionToken").GetString();
        var visible = (await host.Call(client, RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = id, sessionToken = token })).GetProperty("data");
        if (visible.TryGetProperty("attention", out var offered)) Assert.True(offered.TryGetProperty("scope", out _));
        else Assert.True(visible.GetProperty("pendingWork").TryGetProperty("reason", out _));
        var disabled = (await host.Call(client, RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = id, sessionToken = token, attention = false })).GetProperty("data");
        Assert.Equal("disabled", disabled.GetProperty("pendingWork").GetProperty("reason").GetString());
        Assert.False(disabled.TryGetProperty("attention", out _));
    }

    private sealed class InboxHandler(bool mail, bool failFinal = false) : HttpMessageHandler
    {
        internal List<JsonElement> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var args = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            Calls.Add(args);
            var poll = args.TryGetProperty("backgroundCall", out var background) && background.GetBoolean();
            if (!poll && failFinal) throw new HttpRequestException("Synthetic unavailable final read");
            var data = new JsonObject { ["items"] = mail ? new JsonArray(new JsonObject { ["messageId"] = "m-1", ["text"] = "Unread fixture mail" }) : new JsonArray() };
            if (!poll) data["attention"] = new JsonObject { ["id"] = "a-1", ["k"] = "next_work", ["scope"] = new JsonObject { ["taskId"] = "T-1" } };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["ok"] = true, ["code"] = "Ok", ["dataJson"] = data.ToJsonString(), ["version"] = 1 }.ToJsonString(), Encoding.UTF8, "application/json")
            };
        }
    }
}

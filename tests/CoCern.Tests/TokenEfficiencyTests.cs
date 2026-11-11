using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public sealed class TokenEfficiencyTests
{
    [Fact]
    public void Status_summary_overhead_is_not_an_offer_but_genuinely_large_offers_are_still_counted()
    {
        var counter = new O200kTokenCounter();
        ActivityEvent[] Measure(string tool, JsonObject data)
        {
            var text = data.ToJsonString();
            Assert.True(TokenTextBudget.Shared.TryHold(null, text));
            return TokenMeter.Measure(new ActivityEvent { Kind = ActivityEvent.Call, Tool = tool, Ok = true, ReplyText = text }, counter).ToArray();
        }
        var small = new JsonObject { ["id"] = "a-1", ["k"] = "info" };
        var summary = new JsonObject { ["actionable"] = 1, ["pendingWork"] = new JsonObject { ["reason"] = string.Concat(Enumerable.Repeat("summary metadata ", 100)) }, ["offer"] = small.DeepClone() };
        Assert.True(counter.Count(summary.ToJsonString()) > RelayLimits.AttentionOfferTokens);
        Assert.DoesNotContain(Measure(RelayTools.GetStatus, new() { ["attention"] = summary.DeepClone() }), row => row.Code == "over:attention");
        summary.Remove("offer");
        Assert.DoesNotContain(Measure(RelayTools.GetStatus, new() { ["attention"] = summary.DeepClone() }), row => row.Code == "over:attention");

        var large = new JsonObject { ["id"] = "a-2", ["text"] = string.Concat(Enumerable.Repeat("real offer text ", 100)) };
        Assert.True(counter.Count(large.ToJsonString()) > RelayLimits.AttentionOfferTokens);
        summary["offer"] = large.DeepClone();
        foreach (var (tool, attention) in new[] { (RelayTools.GetStatus, summary), (RelayTools.CheckInbox, large) })
        {
            var over = Assert.Single(Measure(tool, new() { ["attention"] = attention.DeepClone() }), row => row.Code == "over:attention");
            Assert.Equal(counter.Count(large.ToJsonString()), over.ReplyTokens);
        }
    }

    [Fact]
    public async Task Oversized_pieces_of_dense_text_stay_within_the_inbox_token_budget_and_rejoin_exactly()
    {
        await using var host = new RelayHostFixture();
        var client = host.AuthorizedClient();
        var reader = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "dense-reader" }));
        var (sessionId, sessionToken) = (reader.GetProperty("sessionId").GetString()!, reader.GetProperty("sessionToken").GetString()!);
        // CJK, emoji (surrogate pairs) and accents: each escapes to several bytes and tokens. Stored as before sends were measured.
        var text = string.Concat(Enumerable.Repeat("中文字符测试😀é ", 450));
        var grains = host.Services.GetRequiredService<Orleans.IGrainFactory>();
        var delivered = await grains.GetGrain<ISessionGrain>(sessionId).ExecuteAsync(new RelayCaller("relay-dispatcher", false, ["repo"], true), Operations.Deliver,
            new JsonObject { ["repositoryId"] = "repo", ["messageId"] = "dense", ["text"] = text }.ToJsonString());
        Assert.True(delivered.Ok, delivered.Code);

        var counter = new O200kTokenCounter();
        var json = new StringBuilder();
        long? partOffset = 0;
        var pieces = 0;
        while (partOffset is { } at)
        {
            var reply = await host.Call(client, "check_inbox", at == 0
                ? new { repositoryId = "repo", sessionId, sessionToken, attention = false, limit = 1, cursor = 0 } as object
                : new { repositoryId = "repo", sessionId, sessionToken, attention = false, limit = 1, cursor = 0, partOffset = at });
            var dataJson = reply.GetProperty("dataJson").GetString()!;
            Assert.True(counter.Count(dataJson) <= RelayLimits.InboxReplyTokens, counter.Count(dataJson).ToString());
            var piece = Data(reply).GetProperty("items")[0];
            Assert.False(Data(reply).TryGetProperty("compacted", out _));
            Assert.Equal("dense", piece.GetProperty("messageId").GetString());
            Assert.Equal(at, piece.GetProperty("partOffset").GetInt64());
            json.Append(piece.GetProperty("part").GetString());
            partOffset = piece.GetProperty("nextPartOffset").ValueKind == JsonValueKind.Null ? null : piece.GetProperty("nextPartOffset").GetInt64();
            pieces++;
        }
        Assert.True(pieces > 1, pieces.ToString());
        Assert.Equal(text, JsonNode.Parse(json.ToString())!["text"]!.GetValue<string>());
    }

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        using var document = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!);
        return document.RootElement.Clone();
    }
}

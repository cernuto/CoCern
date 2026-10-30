using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PkRelay.Core;
using PkRelay.Domain;
using Xunit;

namespace PkRelay.Tests;

public sealed class SelfDeliveryTests
{
    [Fact]
    public async Task Self_message_dispatch_commits_inbox_and_effect_removal_and_survives_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "pkrelay-self-" + Guid.NewGuid().ToString("N"), "relay.db");
        JsonElement session;
        string messageId;
        await using (var initial = new RelayHostFixture(path, disableDispatcher: true))
        {
            using var client = initial.AuthorizedClient();
            session = Data(await initial.Call(client, "session_open", new { repositoryId = "repo", requestId = "open-self" }));
            var sessionId = session.GetProperty("sessionId").GetString()!;
            var sent = Data(await initial.Call(client, "send_message", new
            {
                repositoryId = "repo", requestId = "send-self", sessionId,
                sessionToken = session.GetProperty("sessionToken").GetString(), recipientSessionId = sessionId, text = "durable self message"
            }));
            messageId = sent.GetProperty("messageId").GetString()!;
            Assert.Empty(Data(await initial.Call(client, "check_inbox", SessionArgs(session))).GetProperty("items").EnumerateArray());
            var due = initial.Services.GetRequiredService<IDueWorkSource>();
            Assert.Contains(await due.ReadDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 64), work => work.GrainType == "pkrelay.session" && work.GrainKey == sessionId);
            var grain = initial.Services.GetRequiredService<IGrainFactory>().GetGrain<ISessionGrain>(sessionId);
            var caller = new RelayCaller("relay-dispatcher", false, [], true);
            var dispatched = await grain.ExecuteAsync(caller, "__dispatch", "{}").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(dispatched.Ok, dispatched.Code);
            Assert.True((await grain.ExecuteAsync(caller, "__dispatch", "{}")).Ok);
            var item = Assert.Single(Data(await initial.Call(client, "check_inbox", SessionArgs(session))).GetProperty("items").EnumerateArray());
            Assert.Equal(messageId, item.GetProperty("messageId").GetString());
            Assert.Equal("durable self message", item.GetProperty("text").GetString());
            Assert.DoesNotContain(await due.ReadDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 64), work => work.GrainType == "pkrelay.session" && work.GrainKey == sessionId);
        }
        await using (var restarted = new RelayHostFixture(path, disableDispatcher: true))
        {
            using var client = restarted.AuthorizedClient();
            var item = Assert.Single(Data(await restarted.Call(client, "check_inbox", SessionArgs(session))).GetProperty("items").EnumerateArray());
            Assert.Equal(messageId, item.GetProperty("messageId").GetString());
            Assert.DoesNotContain(await restarted.Services.GetRequiredService<IDueWorkSource>().ReadDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 64), work => work.GrainType == "pkrelay.session" && work.GrainKey == session.GetProperty("sessionId").GetString());
            Data(await restarted.Call(client, "ack", new
            {
                repositoryId = "repo", requestId = "ack-self", sessionId = session.GetProperty("sessionId").GetString(),
                sessionToken = session.GetProperty("sessionToken").GetString(), messageId
            }));
            var grain = restarted.Services.GetRequiredService<IGrainFactory>().GetGrain<ISessionGrain>(session.GetProperty("sessionId").GetString()!);
            Assert.True((await grain.ExecuteAsync(new RelayCaller("relay-dispatcher", false, [], true), "__dispatch", "{}")).Ok);
            Assert.Empty(Data(await restarted.Call(client, "check_inbox", SessionArgs(session))).GetProperty("items").EnumerateArray());
        }
    }

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.GetProperty("code").GetString());
        using var document = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!);
        return document.RootElement.Clone();
    }

    private static object SessionArgs(JsonElement session) => new
    {
        repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString()
    };
}

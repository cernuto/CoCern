using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

// Producer tests use real authenticated send/delivery and private primary wait
// registration; no fabricated incoming signal is substituted for a peer reply.
public sealed class EventWaitPeerReplyTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_the_recorded_recipient_replying_to_the_owners_original_message_fires_once()
    {
        var hub = new AggregateHub(); var owner = hub.Open("owner", Epoch); var peer = hub.Open("peer", Epoch); var other = hub.Open("other", Epoch);
        hub.DeliverAll(Epoch);
        var original = MessageId(hub.Send(owner, peer.Id, "original", Epoch)); hub.DeliverAll(Epoch);
        Assert.True(Register(hub, owner, peer.Id, original, Epoch.AddSeconds(1)).Ok);
        Assert.True(Reply(hub, other, owner.Id, original, Epoch.AddSeconds(2)).Ok); hub.DeliverAll(Epoch.AddSeconds(2));
        Assert.Null(Entry(hub, owner)["terminal"]);
        Assert.True(Reply(hub, peer, owner.Id, "another-message", Epoch.AddSeconds(3)).Ok); hub.DeliverAll(Epoch.AddSeconds(3));
        Assert.Null(Entry(hub, owner)["terminal"]);
        var reply = Reply(hub, peer, owner.Id, original, Epoch.AddSeconds(4)); Assert.True(reply.Ok);
        var replyId = MessageId(reply);
        var delivery = Assert.Single(hub.Queued, effect => effect.Operation == "__deliver" && JsonNode.Parse(effect.PayloadJson)?["messageId"]?.GetValue<string>() == replyId);
        hub.DeliverAll(Epoch.AddSeconds(4));
        var terminal = Assert.IsType<JsonObject>(Entry(hub, owner)["terminal"]);
        Assert.Equal("satisfied", terminal["outcome"]!.GetValue<string>());
        Assert.True(hub.Deliver(delivery, Epoch.AddSeconds(5)).Ok); hub.DeliverAll(Epoch.AddSeconds(5));
        Assert.Single(hub.Inbox(owner, Epoch.AddSeconds(5)), message => message["kind"]?.GetValue<string>() == "event_wait_terminal");
    }

    [Fact]
    public void Registration_cannot_claim_another_chats_message_or_change_its_recorded_recipient()
    {
        foreach (var wrongRecipient in new[] { false, true })
        {
            var hub = new AggregateHub(); var owner = hub.Open("owner", Epoch); var peer = hub.Open("peer", Epoch); var other = hub.Open("other", Epoch);
            hub.DeliverAll(Epoch);
            var original = MessageId(hub.Send(wrongRecipient ? owner : other, peer.Id, "original", Epoch)); hub.DeliverAll(Epoch);
            var denied = Register(hub, owner, wrongRecipient ? other.Id : peer.Id, original, Epoch.AddSeconds(1));
            Assert.False(denied.Ok);
            Assert.Equal("ReplyNotOwned", denied.Code);
            Assert.Null(hub.State("session", owner.Id)["eventWaits"]?["reply-wait"]);
        }
    }



    [Fact]
    public void A_genuine_post_arm_reply_qualifies_even_when_the_server_clock_ties_or_moves_back()
    {
        foreach (var replySeconds in new[] { 0, -1 })
        {
            var hub = new AggregateHub(); var owner = hub.Open("owner", Epoch); var peer = hub.Open("peer", Epoch); hub.DeliverAll(Epoch);
            var original = MessageId(hub.Send(owner, peer.Id, "original", Epoch)); hub.DeliverAll(Epoch);
            Assert.True(Register(hub, owner, peer.Id, original, Epoch).Ok);
            hub.DeliverAll(Epoch); // Finish the real source arming handshake before the send.
            Assert.True(Reply(hub, peer, owner.Id, original, Epoch.AddSeconds(replySeconds)).Ok);
            hub.DeliverAll(Epoch);
            var terminal = Assert.IsType<JsonObject>(Entry(hub, owner)["terminal"]);
            Assert.Equal("satisfied", terminal["outcome"]!.GetValue<string>());
        }
    }

    [Fact]
    public void A_same_clock_pre_arm_reply_delivered_late_does_not_complete_the_wait()
    {
        var hub = new AggregateHub(); var owner = hub.Open("owner", Epoch); var peer = hub.Open("peer", Epoch); hub.DeliverAll(Epoch);
        var original = MessageId(hub.Send(owner, peer.Id, "original", Epoch)); hub.DeliverAll(Epoch);
        // Forging the private sequence cannot turn delayed pre-arm mail into
        // a fresh completion, even when both accepted operations share a time.
        Assert.True(hub.Session(peer, RelayTools.SendMessage, new() { ["recipientSessionId"] = owner.Id, ["replyTo"] = original,
            ["text"] = "reply", ["peerReplySequence"] = long.MaxValue }, Epoch).Ok);
        Assert.True(Register(hub, owner, peer.Id, original, Epoch).Ok);
        hub.DeliverAll(Epoch);
        Assert.Null(Entry(hub, owner)["terminal"]);
        Assert.DoesNotContain(hub.Inbox(owner, Epoch), message => message["kind"]?.GetValue<string>() == "event_wait_terminal");
    }


    [Fact]
    public void Accepted_reply_before_the_arming_ack_is_buffered_and_duplicate_arm_keeps_its_original_baseline()
    {
        var hub = new AggregateHub(); var owner = hub.Open("owner", Epoch); var peer = hub.Open("peer", Epoch); hub.DeliverAll(Epoch);
        var original = MessageId(hub.Send(owner, peer.Id, "original", Epoch)); hub.DeliverAll(Epoch);
        Assert.True(Register(hub, owner, peer.Id, original, Epoch).Ok);
        var arm = Assert.Single(hub.Queued, effect => effect.Operation == PeerReplyWaits.ArmOperation);
        Assert.True(hub.Deliver(arm, Epoch).Ok); // Captures sender sequence before its next send; ACK remains queued.
        var replyId = MessageId(Reply(hub, peer, owner.Id, original, Epoch));
        var delivery = Assert.Single(hub.Queued, effect => effect.Operation == "__deliver" && JsonNode.Parse(effect.PayloadJson)?["messageId"]?.GetValue<string>() == replyId);
        Assert.True(hub.Deliver(delivery, Epoch).Ok);
        Assert.Null(Entry(hub, owner)["terminal"]);
        Assert.True(hub.Deliver(arm, Epoch).Ok); // Must not recapture the now-increased sender sequence.
        hub.DeliverAll(Epoch);
        Assert.Equal("satisfied", Assert.IsType<JsonObject>(Entry(hub, owner)["terminal"])["outcome"]!.GetValue<string>());
        var inbox = hub.Inbox(owner, Epoch);
        Assert.Single(inbox, message => message["kind"]?.GetValue<string>() == "event_wait_terminal");
        Assert.All(inbox, message => Assert.False(message.ContainsKey("peerReplySequence")));
    }



    private static RelayReply Register(AggregateHub hub, Chat owner, string peer, string original, DateTimeOffset at)
    {
        var args = new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = owner.Id, ["sessionToken"] = owner.Token,
            ["requestId"] = "register-reply", ["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest("reply-wait", "peer_reply", original, "",
                at.AddMinutes(10), PeerSessionId: peer, ReplyTo: original), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        return hub.Apply("session", owner.Id, EventWaitAggregate.RegisterOperation, args, AggregateHub.Alice with { IsInternal = true }, at);
    }
    private static RelayReply Reply(AggregateHub hub, Chat peer, string owner, string original, DateTimeOffset at) =>
        hub.Session(peer, RelayTools.SendMessage, new() { ["recipientSessionId"] = owner, ["replyTo"] = original, ["text"] = "reply" }, at);
    private static string MessageId(RelayReply reply) { Assert.True(reply.Ok); return AggregateHub.Data(reply)["messageId"]!.GetValue<string>(); }
    private static JsonObject Entry(AggregateHub hub, Chat owner) => hub.State("session", owner.Id)["eventWaits"]!["reply-wait"]!.AsObject();
}

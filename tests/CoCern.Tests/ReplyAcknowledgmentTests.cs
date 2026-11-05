using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>A successful <c>send_message</c> with <c>replyTo</c> acknowledges that message, in the sender's own inbox only, as if it were in <c>ackMessageIds</c>.</summary>
public sealed class ReplyAcknowledgmentTests
{
    private const string AckDone = """{"ok":true,"code":"Ok"}""";
    private const string AckUnavailable = """{"ok":false,"code":"Unavailable"}""";

    [Fact]
    public async Task A_reply_acknowledges_the_message_it_answers_and_only_that_one()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");
        await hub.Deliver(chat, "other");

        var sent = await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "answered"));

        Assert.True(sent.Ok, sent.Code);
        Assert.NotNull(Data(sent)["messageId"]);
        Assert.Equal(AckDone, AckOutcome(sent));
        Assert.Equal(["other"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_reply_leaves_the_same_message_in_another_inbox()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "shared");
        await hub.Deliver(peer, "shared");

        Assert.True((await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "shared"))).Ok);

        Assert.Empty(await hub.Inbox(chat));
        Assert.Equal(["shared"], await hub.Inbox(peer));
    }

    [Fact]
    public async Task AckMessageIds_still_works_alone_and_beside_replyTo()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        foreach (var id in new[] { "listed-alone", "listed", "answered", "kept" }) await hub.Deliver(chat, id);

        var alone = hub.Reply(chat, peer); alone["ackMessageIds"] = new JsonArray("listed-alone");
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, alone)));
        Assert.Equal(["listed", "answered", "kept"], await hub.Inbox(chat));

        var both = hub.Reply(chat, peer, "answered"); both["ackMessageIds"] = new JsonArray("listed");
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, both)));
        Assert.Equal(["kept"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task The_same_ID_in_both_acknowledges_once()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "both");

        var send = hub.Reply(chat, peer, "both"); send["ackMessageIds"] = new JsonArray("both");
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));

        var ack = Assert.Single(hub.Acks);
        Assert.Equal(["both"], ack["messageIds"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Null(ack["heldOnly"]);
        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task Listed_IDs_keep_todays_behaviour_and_only_replyTo_is_held_only()
    {
        // A listed ID is recorded even if not in the inbox, so its later delivery is dropped. That pre-emption is today's behaviour, not this feature's.
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());

        var send = hub.Reply(chat, peer, "future-answered"); send["ackMessageIds"] = new JsonArray("future-listed");
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));
        await hub.Deliver(chat, "future-listed");
        await hub.Deliver(chat, "future-answered");

        Assert.Equal(["future-answered"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task The_same_future_ID_in_both_behaves_as_listed()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());

        var send = hub.Reply(chat, peer, "future"); send["ackMessageIds"] = new JsonArray("future");
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));
        await hub.Deliver(chat, "future");

        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_hook_report_flag_survives_the_router_so_the_hub_can_keep_the_models_report()
    {
        var hub = new Hub();
        var chat = await hub.Open();
        var model = hub.Handle(chat); model["waitingFor"] = "Waiting on a rig";
        Assert.True((await hub.Call(RelayTools.Ack, model)).Ok);
        var hook = hub.Handle(chat); hook["waitingFor"] = ""; hook[RelayLimits.HookWaitingField] = true;
        var kept = await hub.Call(RelayTools.Ack, hook);
        Assert.True(kept.Ok, kept.Code);
        Assert.True(Data(kept)["waitingForKept"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_client_heldOnly_flag_changes_nothing()
    {
        // Needs "heldOnly" stripped from the ack tool's client arguments (ServerArguments), since every routed call reaches the aggregate as internal.
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());

        var ack = hub.Handle(chat); ack["messageIds"] = new JsonArray("future-acked"); ack["heldOnly"] = true;
        Assert.True((await hub.Call(RelayTools.Ack, ack)).Ok);
        var send = hub.Reply(chat, peer); send["ackMessageIds"] = new JsonArray("future-listed"); send["heldOnly"] = true;
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));
        await hub.Deliver(chat, "future-acked");
        await hub.Deliver(chat, "future-listed");

        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_full_ackMessageIds_keeps_the_limit_and_still_acknowledges_the_answered_message()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        var listed = Enumerable.Range(0, RelayLimits.MaxAcknowledgedMessages).Select(i => "listed-" + i).ToArray();
        foreach (var id in listed) await hub.Deliver(chat, id);
        await hub.Deliver(chat, "answered");

        var send = hub.Reply(chat, peer, "answered"); send["ackMessageIds"] = new JsonArray(listed.Select(id => (JsonNode)id).ToArray());
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));

        var acks = hub.Acks.ToArray();
        Assert.Equal(2, acks.Length);
        Assert.Equal(RelayLimits.MaxAcknowledgedMessages, acks[0]["messageIds"]!.AsArray().Count);
        Assert.Equal("answered", Assert.Single(acks[1]["messageIds"]!.AsArray())!.GetValue<string>());
        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_clients_own_ack_under_a_similar_request_ID_does_not_block_the_reply()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");
        await hub.Deliver(chat, "other");

        var ack = hub.Handle(chat); ack["requestId"] = "r/reply-ack"; ack["messageIds"] = new JsonArray("other");
        Assert.True((await hub.Call(RelayTools.Ack, ack)).Ok);
        var send = hub.Reply(chat, peer, "answered"); send["requestId"] = "r";

        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));
        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_client_cannot_send_the_held_acks_request_ID()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        var send = hub.Reply(chat, peer, "absent"); send["requestId"] = "r";
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));
        var held = hub.Acks.Single(call => call["heldOnly"] is not null);

        var ack = hub.Handle(chat); ack["requestId"] = held["requestId"]!.DeepClone(); ack["messageIds"] = new JsonArray("absent");

        Assert.Equal(RelayErrorCodes.InvalidArguments, (await hub.Call(RelayTools.Ack, ack)).Code);
    }

    [Fact]
    public async Task A_reply_under_the_longest_request_ID_acknowledges_and_replays()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");
        var send = hub.Reply(chat, peer, "answered"); send["requestId"] = new string('r', RelayLimits.MaxIdentifierLength);

        var first = await hub.Call(RelayTools.SendMessage, send);
        Assert.Equal(AckDone, AckOutcome(first));
        Assert.Empty(await hub.Inbox(chat));
        Assert.Equal(first.DataJson, (await hub.Call(RelayTools.SendMessage, send)).DataJson);
    }

    [Fact]
    public async Task HeldOnly_is_not_part_of_a_requests_identity()
    {
        // The flag is hub-set, so it stays out of the replay fingerprint: the retry replays rather than conflicts, and does not act on the inbox again.
        var hub = new Hub();
        var chat = await hub.Open();
        JsonObject Ack() { var ack = hub.Handle(chat); ack["repositoryId"] = "repo"; ack["requestId"] = "same"; ack["messageIds"] = new JsonArray("later"); return ack; }
        var held = Ack(); held["heldOnly"] = true;

        var first = await hub.Session(chat, RelayTools.Ack, held);
        Assert.True(first.Ok, first.Code);
        var again = await hub.Session(chat, RelayTools.Ack, Ack());

        Assert.True(again.Ok, again.Code);
        Assert.Equal(first.DataJson, again.DataJson);
        await hub.Deliver(chat, "later");
        Assert.Equal(["later"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_message_delivered_between_the_send_and_the_held_ack_is_acknowledged()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        hub.Recorded.Before = (operation, args) => operation == RelayTools.Ack && args["heldOnly"] is not null ? hub.Deliver(chat, "racing") : Task.CompletedTask;

        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "racing"))));

        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_client_heldOnly_flag_changes_nothing_on_any_tool_that_carries_ackMessageIds()
    {
        // A listed ID may be acknowledged before it arrives, so each future ID below is gone on delivery only if the client's flag was dropped.
        var hub = new Hub();
        var chat = await hub.Open();

        var create = hub.Handle(chat); create["taskId"] = "t"; create["expectedVersion"] = 0;
        create["ackMessageIds"] = new JsonArray("future-write"); create["heldOnly"] = true;
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.TaskUpdate, create)));
        var read = hub.Handle(chat); read["taskId"] = "t";
        read["ackMessageIds"] = new JsonArray("future-read"); read["heldOnly"] = true;
        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.TaskGet, read)));

        await hub.Deliver(chat, "future-write");
        await hub.Deliver(chat, "future-read");
        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task Too_many_ackMessageIds_still_refuses_the_send_and_acknowledges_nothing()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");

        var send = hub.Reply(chat, peer, "answered");
        send["ackMessageIds"] = new JsonArray(Enumerable.Range(0, RelayLimits.MaxAcknowledgedMessages + 1).Select(i => (JsonNode)("listed-" + i)).ToArray());
        Assert.Equal(RelayErrorCodes.PayloadTooLarge, (await hub.Call(RelayTools.SendMessage, send)).Code);

        Assert.Equal(["answered"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_reply_finds_its_message_past_the_first_inbox_page_without_reading_the_inbox()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        for (var i = 0; i < RelayLimits.MaxPageSize + 5; i++) await hub.Deliver(chat, "old-" + i);
        await hub.Deliver(chat, "answered");
        hub.Recorded.Calls.Clear();

        Assert.True((await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "answered"))).Ok);

        // Atomic: the check happens inside the ack, with no read of the inbox beside it.
        Assert.DoesNotContain(hub.Recorded.Calls, call => call.Operation == RelayTools.CheckInbox);
        var inbox = await hub.Inbox(chat);
        Assert.Equal(RelayLimits.MaxPageSize + 5, inbox.Count);
        Assert.DoesNotContain("answered", inbox);
    }

    [Fact]
    public async Task A_reply_to_a_message_it_does_not_hold_does_nothing_and_answers_the_same()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "acknowledged");
        await hub.Deliver(chat, "kept");
        await hub.Deliver(peer, "theirs");
        var ack = hub.Handle(chat); ack["messageId"] = "acknowledged";
        Assert.True((await hub.Call(RelayTools.Ack, ack)).Ok);

        foreach (var replyTo in new[] { "no-such-message", "acknowledged", "theirs" })
        {
            var sent = await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, replyTo));
            Assert.True(sent.Ok, sent.Code);
            Assert.NotNull(Data(sent)["messageId"]);
            // The same outcome as a reply to a held message, so it cannot tell which of these an ID is.
            Assert.Equal(AckDone, AckOutcome(sent));
        }

        Assert.Equal(["kept"], await hub.Inbox(chat));
        Assert.Equal(["theirs"], await hub.Inbox(peer));
        // Nothing was recorded for them either: each can still be delivered here, while the one acknowledged earlier stays dropped.
        foreach (var id in new[] { "no-such-message", "acknowledged", "theirs" }) await hub.Deliver(chat, id);
        Assert.Equal(["kept", "no-such-message", "theirs"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task HeldOnly_acts_only_on_what_the_inbox_holds_in_one_call()
    {
        var hub = new Hub();
        var chat = await hub.Open();
        await hub.Deliver(chat, "held", attentionExpiryDays: 7);
        await hub.Deliver(chat, "kept");

        var ack = hub.Handle(chat); ack["repositoryId"] = "repo"; ack["heldOnly"] = true; ack["messageIds"] = new JsonArray("held", "absent");
        var reply = await hub.Session(chat, RelayTools.Ack, ack);

        Assert.Equal(1, Data(reply)["acknowledged"]!.GetValue<int>());
        Assert.Null(await hub.Wake(chat));
        await hub.Deliver(chat, "absent");
        Assert.Equal(["kept", "absent"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_reply_to_a_message_not_yet_delivered_does_not_drop_it_when_it_arrives()
    {
        // An acknowledgment records its IDs and a later delivery of a recorded ID is ignored, so a bare ack here would lose the message.
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());

        Assert.True((await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "in-flight"))).Ok);
        await hub.Deliver(chat, "in-flight");

        Assert.Equal(["in-flight"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_failed_send_acknowledges_nothing()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");
        await hub.Deliver(chat, "listed");

        var nowhere = hub.Reply(chat, peer, "answered"); nowhere.Remove("recipientSessionId"); nowhere["ackMessageIds"] = new JsonArray("listed");
        Assert.False((await hub.Call(RelayTools.SendMessage, nowhere)).Ok);
        var tooLong = hub.Reply(chat, peer, "answered"); tooLong["text"] = new string('x', RelayLimits.MaxReportBytes + 1);
        Assert.False((await hub.Call(RelayTools.SendMessage, tooLong)).Ok);

        Assert.Empty(hub.Acks);
        Assert.Equal(["answered", "listed"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_request_conflict_acknowledges_nothing()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "first");
        await hub.Deliver(chat, "second");

        var send = hub.Reply(chat, peer, "first"); send["requestId"] = "same";
        Assert.True((await hub.Call(RelayTools.SendMessage, send)).Ok);
        var changed = hub.Reply(chat, peer, "second"); changed["requestId"] = "same";
        Assert.Equal(RelayErrorCodes.RequestConflict, (await hub.Call(RelayTools.SendMessage, changed)).Code);

        Assert.Equal(["second"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_retry_replays_the_first_reply_without_acknowledging_again()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");
        await hub.Deliver(chat, "kept");

        var send = hub.Reply(chat, peer, "answered"); send["requestId"] = "retried";
        var first = await hub.Call(RelayTools.SendMessage, send);
        var second = await hub.Call(RelayTools.SendMessage, send);

        Assert.Equal(first.DataJson, second.DataJson);
        Assert.Equal(first.Version, second.Version);
        // Both attempts name one request, so the second replays the stored answer instead of acting again.
        Assert.Equal(2, hub.Acks.Count());
        Assert.Single(hub.Acks.Select(ack => ack["requestId"]!.GetValue<string>()).Distinct());
        Assert.Equal(["kept"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_retry_of_ackMessageIds_runs_the_acknowledgment_again_and_replays_the_same_reply()
    {
        // Pins the behaviour replyTo matches: add-ons run on every attempt, so a retry repeats the ack, which finds nothing left to remove.
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "listed");

        var send = hub.Reply(chat, peer); send["requestId"] = "retried"; send["ackMessageIds"] = new JsonArray("listed");
        var first = await hub.Call(RelayTools.SendMessage, send);
        var second = await hub.Call(RelayTools.SendMessage, send);

        Assert.Equal(first.DataJson, second.DataJson);
        Assert.Equal(2, hub.Acks.Count());
        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_retry_after_the_answered_message_arrives_replays_the_first_answer_and_leaves_it()
    {
        // The held ack has its own request ID, so the retry replays what the first attempt found instead of looking at the inbox again.
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());

        var send = hub.Reply(chat, peer, "late"); send["requestId"] = "retried";
        var first = await hub.Call(RelayTools.SendMessage, send);
        await hub.Deliver(chat, "late");
        var second = await hub.Call(RelayTools.SendMessage, send);

        Assert.Equal(first.DataJson, second.DataJson);
        Assert.Equal(["late"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_retry_after_a_lost_acknowledgment_acknowledges()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");

        var send = hub.Reply(chat, peer, "answered"); send["requestId"] = "retried";
        hub.Recorded.FailOn = RelayTools.Ack;
        Assert.Equal(AckUnavailable, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));
        hub.Recorded.FailOn = null;
        Assert.Equal(["answered"], await hub.Inbox(chat));

        Assert.Equal(AckDone, AckOutcome(await hub.Call(RelayTools.SendMessage, send)));
        Assert.Empty(await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_failing_acknowledgment_still_reports_the_send()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");

        hub.Recorded.FailOn = RelayTools.Ack;
        var sent = await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "answered"));
        hub.Recorded.FailOn = null;

        Assert.True(sent.Ok, sent.Code);
        Assert.NotNull(Data(sent)["messageId"]);
        Assert.Equal(AckUnavailable, AckOutcome(sent));
        Assert.Equal(["answered"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_lost_session_check_still_reports_the_send()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");

        // The first verification is the guard's own; the add-on's second one fails.
        hub.Recorded.FailVerifyAfter = 1;
        var sent = await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "answered"));
        hub.Recorded.FailVerifyAfter = null;

        Assert.True(sent.Ok, sent.Code);
        Assert.Equal(AckUnavailable, AckOutcome(sent));
        Assert.Equal(["answered"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task A_refused_acknowledgment_reports_its_code_and_leaves_the_send()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered");

        // A refused list stops there, so a failed outcome means nothing was acknowledged. One malformed entry refuses the whole list, as it always has.
        var send = hub.Reply(chat, peer, "answered"); send["ackMessageIds"] = new JsonArray(1);
        var sent = await hub.Call(RelayTools.SendMessage, send);

        Assert.True(sent.Ok, sent.Code);
        Assert.Equal("""{"ok":false,"code":"InvalidArguments"}""", AckOutcome(sent));
        Assert.Equal(["answered"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task Acknowledging_by_reply_closes_the_messages_info_item()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "answered", attentionExpiryDays: 7);
        Assert.NotNull(await hub.Wake(chat));

        Assert.True((await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "answered"))).Ok);

        Assert.Null(await hub.Wake(chat));
    }

    [Fact]
    public async Task A_task_channel_reply_acknowledges_the_senders_notice_and_keeps_the_conversation()
    {
        // An ordinary ack treats a task notice like any inbox message and never touches the board, so a reply in the task channel does the same.
        var hub = new Hub();
        var chat = await hub.Open();
        var create = hub.Handle(chat); create["taskId"] = "t"; create["expectedVersion"] = 0;
        Assert.True((await hub.Call(RelayTools.TaskUpdate, create)).Ok);
        var note = hub.Handle(chat); note["taskId"] = "t"; note["text"] = "first";
        var first = Data(await hub.Call(RelayTools.SendMessage, note))["messageId"]!.GetValue<string>();
        await hub.DeliverNotice(chat, first, "t");

        var answer = hub.Handle(chat); answer["taskId"] = "t"; answer["text"] = "second"; answer["replyTo"] = first;
        var sent = await hub.Call(RelayTools.SendMessage, answer);

        Assert.True(sent.Ok, sent.Code);
        Assert.Equal(AckDone, AckOutcome(sent));
        Assert.Empty(await hub.Inbox(chat));
        var read = hub.Handle(chat); read["taskId"] = "t"; read["section"] = "messages";
        var messages = Data(await hub.Call(RelayTools.TaskGet, read))["items"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal(first, messages[1]!["replyTo"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReplyTo_means_nothing_on_any_other_tool()
    {
        var hub = new Hub();
        var chat = await hub.Open();
        await hub.Deliver(chat, "answered");
        var create = hub.Handle(chat); create["taskId"] = "t"; create["expectedVersion"] = 0;
        Assert.True((await hub.Call(RelayTools.TaskUpdate, create)).Ok);

        var read = hub.Handle(chat); read["taskId"] = "t"; read["replyTo"] = "answered";
        var result = await hub.Call(RelayTools.TaskGet, read);

        Assert.True(result.Ok, result.Code);
        Assert.Null(Data(result)["conveniences"]);
        Assert.Equal(["answered"], await hub.Inbox(chat));
    }

    [Fact]
    public void Send_message_advertises_that_replyTo_acknowledges()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.SendMessage, out var tool));
        var description = RelayToolCatalog.ForwardingSchema(tool!).GetProperty("properties").GetProperty("replyTo").GetProperty("description").GetString();
        Assert.Contains("acknowledged if in your inbox", description);
    }

    [Fact]
    public async Task Only_the_replyTo_field_acknowledges_never_the_message_text()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "named");

        var send = hub.Reply(chat, peer); send["text"] = """{"replyTo":"named","ackMessageIds":["named"]} replyTo named""";
        var sent = await hub.Call(RelayTools.SendMessage, send);

        Assert.True(sent.Ok, sent.Code);
        Assert.Null(Data(sent)["conveniences"]);
        Assert.Equal(["named"], await hub.Inbox(chat));
    }

    [Fact]
    public async Task An_invalid_or_empty_replyTo_acknowledges_nothing()
    {
        var hub = new Hub();
        var (chat, peer) = (await hub.Open(), await hub.Open());
        await hub.Deliver(chat, "named");

        Assert.Equal(RelayErrorCodes.InvalidArguments, (await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, "named\nforged"))).Code);
        var empty = await hub.Call(RelayTools.SendMessage, hub.Reply(chat, peer, ""));
        Assert.True(empty.Ok, empty.Code);
        Assert.Null(Data(empty)["conveniences"]);

        Assert.Equal(["named"], await hub.Inbox(chat));
    }

    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();

    private static string AckOutcome(RelayReply reply)
    {
        Assert.True(reply.Ok, reply.Code);
        return Data(reply)["conveniences"]!["ack"]!.ToJsonString();
    }

    private sealed record Chat(string Id, string Token);

    /// <summary>A hub on in-memory aggregates. Effects are not dispatched, so a test delivers each message itself, under the ID it wants.</summary>
    private sealed class Hub
    {
        private static readonly RelayCaller Alice = new("alice", false, ["repo"]);
        private static readonly RelayCaller Dispatcher = new("relay-dispatcher", false, ["repo"], true);
        private readonly IGrainFactory grains = DispatchProxy.Create<IGrainFactory, RecordingFactory>();
        private readonly RelayApplication application;

        public Hub() => application = new RelayApplication(grains);

        public RecordingFactory Recorded => (RecordingFactory)(object)grains;
        public Task<RelayReply> Session(Chat chat, string operation, JsonObject args) => grains.GetGrain<ISessionGrain>(chat.Id).ExecuteAsync(Alice with { IsInternal = true }, operation, args.ToJsonString());

        public IEnumerable<JsonObject> Acks => Recorded.Calls.Where(call => call.Operation == RelayTools.Ack).Select(call => call.Args);

        public Task<RelayReply> Call(string tool, JsonObject args)
        {
            var sent = args.DeepClone().AsObject();
            sent["repositoryId"] = "repo";
            if (!sent.ContainsKey("requestId")) sent["requestId"] = Guid.NewGuid().ToString("N");
            return application.ExecuteAsync(Alice, tool, JsonSerializer.SerializeToElement(sent));
        }

        public async Task<Chat> Open()
        {
            var opened = await Call(RelayTools.SessionOpen, new JsonObject());
            Assert.True(opened.Ok, opened.Code);
            var session = Data(opened);
            return new Chat(session["sessionId"]!.GetValue<string>(), session["sessionToken"]!.GetValue<string>());
        }

        public JsonObject Handle(Chat chat) => new() { ["sessionId"] = chat.Id, ["sessionToken"] = chat.Token };

        public JsonObject Reply(Chat from, Chat to, string? replyTo = null)
        {
            var send = Handle(from);
            send["recipientSessionId"] = to.Id; send["text"] = "thanks";
            if (replyTo is not null) send["replyTo"] = replyTo;
            return send;
        }

        public async Task Deliver(Chat chat, string messageId, long? attentionExpiryDays = null)
        {
            var message = new JsonObject { ["repositoryId"] = "repo", ["messageId"] = messageId, ["senderSessionId"] = "someone", ["text"] = "hello" };
            if (attentionExpiryDays is { } days) message[MessageDelivery.AttentionExpiryDaysKey] = days;
            await Dispatch(chat, message);
        }

        public Task DeliverNotice(Chat chat, string messageId, string taskId) =>
            Dispatch(chat, new MessageDelivery(InboxMessage.TaskChanged("repo", messageId, AggregateKinds.DefaultBoard, taskId, 2)).ToJson());

        private async Task Dispatch(Chat chat, JsonObject message)
        {
            var delivered = await grains.GetGrain<ISessionGrain>(chat.Id).ExecuteAsync(Dispatcher, Operations.Deliver, message.ToJsonString());
            Assert.True(delivered.Ok, delivered.Code);
        }

        /// <summary>Every message the session's inbox holds, oldest first.</summary>
        public async Task<List<string>> Inbox(Chat chat)
        {
            var ids = new List<string>();
            var read = Handle(chat); read["attention"] = false; read["limit"] = RelayLimits.MaxPageSize;
            while (true)
            {
                var reply = await Call(RelayTools.CheckInbox, read);
                Assert.True(reply.Ok, reply.Code);
                var page = Data(reply);
                ids.AddRange(page["items"]!.AsArray().Select(item => item!["messageId"]!.GetValue<string>()));
                if (page["nextCursor"] is not JsonValue next) return ids;
                read["cursor"] = next.DeepClone();
            }
        }

        /// <summary>The attention item that would wake the chat now, read as the status check does, or null.</summary>
        public async Task<string?> Wake(Chat chat)
        {
            var read = Handle(chat); read["repositoryId"] = "repo"; read["attentionStatus"] = "detail"; read["maxHoldMinutes"] = 10;
            var status = await grains.GetGrain<ISessionGrain>(chat.Id).ExecuteAsync(Alice, RelayTools.GetStatus, read.ToJsonString());
            Assert.True(status.Ok, status.Code);
            return Data(status)["attention"]?["wake"]?.GetValue<string>();
        }
    }

    /// <summary>In-memory aggregates that record each session call, and can make one operation throw as an unreachable grain would.</summary>
    public class RecordingFactory : CoordinationTests.TestGrainFactory
    {
        public List<(string Operation, JsonObject Args)> Calls { get; } = [];
        public string? FailOn { get; set; }
        public int? FailVerifyAfter { get; set; }

        /// <summary>Runs just before a session call reaches its aggregate, for a test to act in the gap before it.</summary>
        public Func<string, JsonObject, Task>? Before { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var grain = base.Invoke(targetMethod, args);
            return targetMethod!.GetGenericArguments()[0] == typeof(ISessionGrain) ? new RecordedSession(this, (ISessionGrain)grain!) : grain;
        }

        internal bool ShouldFail(string operation)
        {
            if (operation == FailOn) return true;
            if (operation != Operations.Verify || FailVerifyAfter is not { } remaining) return false;
            FailVerifyAfter = remaining - 1;
            return remaining == 0;
        }
    }

    private sealed class RecordedSession(RecordingFactory factory, ISessionGrain inner) : ISessionGrain
    {
        public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
        {
            if (factory.ShouldFail(operation)) throw new TimeoutException("Session grain unavailable");
            var args = JsonNode.Parse(argumentsJson)!.AsObject();
            factory.Calls.Add((operation, args));
            if (factory.Before is { } before) await before(operation, args);
            return await inner.ExecuteAsync(caller, operation, argumentsJson);
        }
    }
}

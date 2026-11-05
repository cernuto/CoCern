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

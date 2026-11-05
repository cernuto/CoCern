using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using CoCern.Storage;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

public sealed class NativeJoinLifecycleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
    private static readonly RelayCaller Alice = AggregateHub.Alice;
    private static string Nonce() => Guid.NewGuid().ToString("N");

    private static JsonObject Attempt() => new()
    {
        ["repositoryId"] = AggregateHub.Repository, ["machineId"] = "machine", ["requestId"] = Nonce(),
        ["nativeJoin"] = new JsonObject
        {
            ["provider"] = "codex", ["nativeId"] = Guid.NewGuid().ToString("D"), ["workspace"] = "/workspace/repo",
            ["bindingSecret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ["generation"] = Nonce()
        }
    };

    // Use the real admission boundary, but exercise the durable actors with a controlled clock and delivery order.
    private static RelayReply Open(AggregateHub hub, JsonObject args, RelayCaller? caller = null, DateTimeOffset? at = null)
    {
        var guard = new CallGuard(null!, "hub");
        if (!guard.TryAdmit(caller ?? Alice, RelayTools.SessionOpen, JsonSerializer.SerializeToElement(args), null, out var call, out var refusal))
            return refusal!;
        return hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.SessionOpen, call!.Args, call.Caller, at ?? Now);
    }

    private sealed class ActivityLog : IActivitySink, ITokenCounter
    {
        internal List<ActivityEvent> Events { get; } = [];
        public void Record(ActivityEvent activity) => Events.Add(activity);
        public string Encoding => "test";
        public int Count(string text) => text.Length;
        public int PrefixLength(string text, int maxTokens) => Math.Min(text.Length, maxTokens);
    }

    [Theory]
    [InlineData("valid")]
    public void Token_meter_never_queues_a_native_request_or_its_open_reply(string form)
    {
        var attempt = Attempt();
        if (form == "denied") attempt["repositoryId"] = "other";
        var tool = form == "wrong-tool" ? RelayTools.WorkerList : RelayTools.SessionOpen;
        if (form == "legacy") attempt.Remove("nativeJoin");
        var raw = attempt.ToJsonString();
        if (form == "array") raw = "[" + raw + "]";
        if (form == "duplicate") raw = raw.Insert(1, "\"nativeJoin\":null,");
        if (form == "escaped") raw = raw.Replace("nativeJoin", @"native\u004aoin", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var trace = new CallTrace { At = Now };
        var guard = new CallGuard(null!, "hub");
        guard.TryAdmit(Alice, tool, document.RootElement, trace, out _, out var refusal);
        if (form is "wrong-tool" or "array") Assert.Equal(RelayErrorCodes.InvalidArguments, refusal?.Code);
        var log = new ActivityLog();
        new CallTelemetry(log, log).RecordCall(tool, document.RootElement, trace,
            refusal ?? Replies.Ok(new { sessionToken = "private-reply" }), TimeSpan.Zero, false);
        var queued = Assert.Single(log.Events);
        if (form == "legacy")
        {
            Assert.Null(refusal);
            Assert.Equal(raw, queued.RequestText);
            Assert.Contains("private-reply", queued.ReplyText);
            Assert.True(queued.RequestBytes > 0);
            Assert.Single(TokenMeter.Measure(queued, log));
            return;
        }
        Assert.Null(queued.RequestText);
        Assert.Null(queued.ReplyText);
        Assert.True(queued.RequestBytes > 0);
        Assert.DoesNotContain("private-reply", JsonSerializer.Serialize(queued));
        Assert.DoesNotContain(attempt["nativeJoin"]!["bindingSecret"]!.ToString(), JsonSerializer.Serialize(queued));
    }

    [Fact]
    public void Identity_claims_cannot_open_a_peer()
    {
        var hub = new AggregateHub();
        var attempt = Attempt();
        var original = AggregateHub.Data(Open(hub, attempt));
        var missingProof = attempt.DeepClone().AsObject();
        missingProof["nativeJoin"]!.AsObject().Remove("bindingSecret");
        missingProof["requestId"] = Nonce();
        Assert.Equal(RelayErrorCodes.InvalidArguments, Open(hub, missingProof).Code);
        missingProof["repositoryId"] = "inaccessible";
        Assert.Equal(RelayErrorCodes.RepositoryDenied, Open(hub, missingProof).Code);

        var other = attempt.DeepClone().AsObject();
        other["nativeJoin"]!["bindingSecret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.NotEqual(original["sessionToken"]!.ToString(), AggregateHub.Data(Open(hub, other))["sessionToken"]!.ToString());
        Assert.NotEqual(original["sessionToken"]!.ToString(), AggregateHub.Data(Open(hub, attempt, new RelayCaller("other", false, [AggregateHub.Repository])))["sessionToken"]!.ToString());
        var scoped = new RelayCaller(Alice.PrincipalId, false, ["repo", "other"]);
        foreach (var (field, changed) in new[] { ("repositoryId", "other"), ("machineId", "second"), ("provider", "claude"),
            ("workspace", "/other/repo"), ("nativeId", Guid.NewGuid().ToString("D")) })
        {
            var isolated = attempt.DeepClone().AsObject();
            var target = isolated.ContainsKey(field) ? isolated : isolated["nativeJoin"]!.AsObject();
            target[field] = changed;
            Assert.NotEqual(original["sessionToken"]!.ToString(), AggregateHub.Data(Open(hub, isolated, scoped))["sessionToken"]!.ToString());
        }
        var elevated = attempt.DeepClone().AsObject();
        elevated["role"] = "manager";
        elevated["isAdministrator"] = true;
        Assert.Equal(RelayErrorCodes.InvalidArguments, Open(hub, elevated).Code);
    }

    private static Chat Handle(JsonObject data) => new(data["sessionId"]!.ToString(), data["sessionToken"]!.ToString());

    // Import a genuinely initialized private row and session from an older, independent directory snapshot.
    private static JsonObject NextAttempt(JsonObject attempt)
    {
        var next = attempt.DeepClone().AsObject();
        next["nativeJoin"]!["generation"] = Nonce();
        next["requestId"] = Nonce();
        return next;
    }

    private static Chat ImportDuplicate(AggregateHub hub, JsonObject other)
    {
        var old = new AggregateHub();
        var chat = Handle(AggregateHub.Data(Open(old, other)));
        old.DeliverAll(Now);
        var control = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey);
        control["sessions"]![chat.Id] = old.Directory()[chat.Id]!.DeepClone();
        hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Value = new() { Json = control.ToJsonString() };
        hub.Of(AggregateKinds.Session, chat.Id).Value = new() { Json = old.State(AggregateKinds.Session, chat.Id).ToJsonString() };
        return chat;
    }

    [Theory]
    [InlineData(false, false)]
    public void Observed_duplicate_cannot_mint_a_handle_after_current_expiry(bool sameAttempt, bool administrativeClose)
    {
        var hub = new AggregateHub();
        var attempt = Attempt();
        var current = Handle(AggregateHub.Data(Open(hub, attempt)));
        hub.DeliverAll(Now);
        var saved = sameAttempt ? attempt : NextAttempt(attempt);
        var older = ImportDuplicate(hub, saved);
        if (administrativeClose) Assert.True(hub.Close(new() { ["targetSessionId"] = older.Id }, Now).Ok);
        else _ = Open(hub, attempt);
        hub.DeliverAll(Now);
        Assert.False(hub.Directory().ContainsKey(older.Id));
        Assert.Equal(current, Handle(AggregateHub.Data(Open(hub, attempt))));
        Assert.True(hub.Close(new() { ["targetSessionId"] = current.Id, ["force"] = true }, Now).Ok);
        hub.DeliverAll(Now);
        hub = hub.Clone();
        var stale = Open(hub, saved);
        Assert.Equal(RelayErrorCodes.SessionUnknown, stale.Code);
        Assert.DoesNotContain("sessionToken", stale.DataJson);
        var reusedRequest = NextAttempt(attempt);
        reusedRequest["requestId"] = saved["requestId"]!.DeepClone();
        Assert.Equal(RelayErrorCodes.RequestConflict, Open(hub, reusedRequest).Code);
        Assert.Empty(hub.Directory());
        var fresh = Handle(AggregateHub.Data(Open(hub, NextAttempt(attempt))));
        Assert.NotEqual(older, fresh);
        Assert.NotEqual(current, fresh);
    }

    [Fact]
    public void Duplicate_cleanup_respects_unread_action_and_restore_fences()
    {
        var hub = new AggregateHub();
        var attempt = Attempt();
        var current = Handle(AggregateHub.Data(Open(hub, attempt)));
        hub.DeliverAll(Now);
        var idle = ImportDuplicate(hub, NextAttempt(attempt));
        var unread = ImportDuplicate(hub, NextAttempt(attempt));
        var owner = ImportDuplicate(hub, NextAttempt(attempt));
        var unrelated = hub.Open("unrelated", Now);
        var participant = hub.Directory()[current.Id]![NativeParticipant.Id]!.ToString();
        var at = Now.AddMinutes(5);
        Assert.True(hub.Send(unrelated, unread.Id, "keep with original recipient", at).Ok);
        hub.DeliverAll(at);
        JsonObject Report(bool open, DateTimeOffset time) => new ActionWorkReport("repo", owner.Id, "default", "t", "a", 1, open, time).ToJson();
        Assert.True(hub.Apply(AggregateKinds.Session, owner.Id, Operations.SessionWork, Report(true, at), AggregateHub.BoardCaller, at).Ok);
        var lateOwner = Assert.Single(hub.Discard(Operations.SessionOwns));
        ControlState Directory() => new(hub.State(AggregateKinds.Control, AggregateKinds.ControlKey));
        Assert.Null(Directory().CurrentNativeSession(participant, "repo"));
        _ = Open(hub, attempt, at: at);
        Assert.False(hub.Directory().ContainsKey(idle.Id));
        Assert.True(hub.Directory().ContainsKey(unrelated.Id));
        Assert.True(hub.Directory().ContainsKey(current.Id));
        Assert.Null(Directory().CurrentNativeSession(participant, "repo")); // Expiry and refusal are still in flight.
        var pending = hub.Discard(Operations.SessionExpire);
        var firstRefusal = Assert.Single(pending, effect => effect.TargetKey == unread.Id);
        Assert.True(hub.Deliver(firstRefusal, at).Ok);
        var restore = Assert.Single(hub.Discard(Operations.SessionRestore));
        at = at.AddMinutes(RelayLimits.LostCloseAnswerMinutes + 1);
        Assert.True(hub.Verify(unread, at).Ok);
        hub.DeliverAll(at); // A late activity report cannot supersede the original idle close with a non-idle one.
        Assert.Null(Directory().CurrentNativeSession(participant, "repo"));
        Assert.True(hub.Deliver(restore, at).Ok);
        foreach (var expire in pending.Where(effect => effect.TargetKey != unread.Id)) Assert.True(hub.Deliver(expire, at).Ok);
        hub.DeliverAll(at);
        Assert.True(hub.Deliver(lateOwner, at).Ok);
        Assert.Equal(RelayErrorCodes.SessionUnknown, hub.Verify(idle, at).Code);
        Assert.Single(hub.Inbox(unread, at));
        Assert.True(hub.Verify(owner, at).Ok);
        Assert.Empty(hub.Inbox(current, at));
        Assert.Null(Directory().CurrentNativeSession(participant, "repo"));
        _ = Open(hub, attempt, at: at);
        Assert.True(hub.Directory().ContainsKey(unread.Id));
        Assert.True(hub.Directory().ContainsKey(owner.Id));
        // The session reports work settled; the unread recipient acknowledges its own mail.
        var message = Assert.Single(hub.Inbox(unread, at));
        Assert.True(hub.Session(unread, RelayTools.Ack, new() { ["messageIds"] = new JsonArray(message["messageId"]!.DeepClone()) }, at).Ok);
        at = at.AddDays(RelayLimits.SessionMailHoldDays + 1);
        Assert.True(hub.Apply(AggregateKinds.Session, owner.Id, Operations.SessionWork, Report(false, at), AggregateHub.BoardCaller, at).Ok);
        hub.DeliverAll(at);
        _ = Open(hub, attempt, at: at);
        // Its activity arrived while the original restore was in flight, so the restored directory row predates it.
        // The session refuses once for that activity and supplies its updated observation. The next join can close it.
        var recheck = Assert.Single(hub.Queued, effect => effect.Operation == Operations.SessionExpire && effect.TargetKey == unread.Id);
        Assert.Equal("active", AggregateHub.Data(hub.Deliver(recheck, at))["keptFor"]!.ToString());
        hub.DeliverAll(at);
        Assert.True(hub.Directory().ContainsKey(unread.Id));
        Assert.False(hub.Directory().ContainsKey(owner.Id));
        Assert.Null(Directory().CurrentNativeSession(participant, "repo"));
        _ = Open(hub, attempt, at: at);
        hub.DeliverAll(at);
        Assert.Equal(current.Id, Directory().CurrentNativeSession(participant, "repo")?.SessionId);
        Assert.Equal(current.Token, Handle(AggregateHub.Data(Open(hub, attempt, at: at))).Token);
        Assert.True(hub.Directory().ContainsKey(unrelated.Id));
    }

    [Fact]
    public void Concurrent_join_lost_reply_restart_and_expiry_preserve_one_current_participant()
    {
        var hub = new AggregateHub();
        var attempt = Attempt();
        var first = AggregateHub.Data(Open(hub, attempt));
        var originalId = first["sessionId"]!.ToString();
        hub.DeliverAll(Now);
        // Rehydrate the durable stores after losing the first reply and dropping the generic request replay.
        hub = hub.Clone();
        var control = hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey);
        var state = JsonNode.Parse(control.Value.Json)!.AsObject();
        state.Remove(ReadModels.RequestsKey);
        control.Value = new() { Json = state.ToJsonString() };

        var retry = AggregateHub.Data(Open(hub, attempt));
        Assert.Equal(originalId, retry["sessionId"]!.ToString());
        Assert.Equal(first["sessionToken"]!.ToString(), retry["sessionToken"]!.ToString());
        Assert.Single(hub.Directory());
        Assert.DoesNotContain(attempt["nativeJoin"]!["bindingSecret"]!.ToString(), control.Value.Json);
        Assert.DoesNotContain(attempt["nativeJoin"]!["nativeId"]!.ToString(), control.Value.Json);

        var replacement = attempt.DeepClone().AsObject();
        replacement["nativeJoin"]!["generation"] = Nonce();
        replacement["requestId"] = Nonce();
        Assert.False(Open(hub, replacement).Ok);
        Assert.True(hub.Close(new() { ["targetSessionId"] = originalId }, Now.AddHours(1)).Ok);
        Assert.False(Open(hub, replacement).Ok); // The private session has not answered the close.
        hub.DeliverAll(Now.AddHours(1));
        Assert.Equal(RelayErrorCodes.SessionUnknown, Open(hub, attempt).Code);
        var fresh = AggregateHub.Data(Open(hub, replacement));
        Assert.NotEqual(originalId, fresh["sessionId"]!.ToString());
        Assert.NotEqual(first["sessionToken"]!.ToString(), fresh["sessionToken"]!.ToString());
        Assert.False(Open(hub, attempt).Ok);
        Assert.Single(hub.Directory());
    }
}

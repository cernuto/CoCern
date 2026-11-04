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
    [InlineData("denied")]
    [InlineData("duplicate")]
    [InlineData("escaped")]
    public void Token_meter_never_queues_a_native_request_or_its_open_reply(string form)
    {
        var attempt = Attempt();
        if (form == "denied") attempt["repositoryId"] = "other";
        var raw = attempt.ToJsonString();
        if (form == "duplicate") raw = raw.Insert(1, "\"nativeJoin\":null,");
        if (form == "escaped") raw = raw.Replace("nativeJoin", @"native\u004aoin", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var trace = new CallTrace { At = Now };
        var guard = new CallGuard(null!, "hub");
        guard.TryAdmit(Alice, RelayTools.SessionOpen, document.RootElement, trace, out _, out var refusal);
        var log = new ActivityLog();
        new CallTelemetry(log, log).RecordCall(RelayTools.SessionOpen, document.RootElement, trace,
            refusal ?? Replies.Ok(new { sessionToken = "private-reply" }), TimeSpan.Zero, false);
        var queued = Assert.Single(log.Events);
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

    [Fact]
    public void Generation_history_exhaustion_refuses_a_new_session_without_reusing_a_spent_attempt()
    {
        var hub = new AggregateHub();
        var first = Attempt();
        var attempt = first.DeepClone().AsObject();
        for (var i = 0; i < 64; i++)
        {
            var chat = Handle(AggregateHub.Data(Open(hub, attempt)));
            hub.DeliverAll(Now);
            Assert.True(hub.Close(new() { ["targetSessionId"] = chat.Id, ["force"] = true }, Now).Ok);
            hub.DeliverAll(Now);
            attempt["requestId"] = Nonce();
            attempt["nativeJoin"]!["generation"] = Nonce();
        }
        Assert.Equal(RelayErrorCodes.CapacityExceeded, Open(hub, attempt).Code);
        Assert.Equal(RelayErrorCodes.SessionUnknown, Open(hub, first).Code);
        Assert.Empty(hub.Directory());
    }

    private static Chat Handle(JsonObject data) => new(data["sessionId"]!.ToString(), data["sessionToken"]!.ToString());

    // Import a genuinely initialized private row and session from an older, independent directory snapshot.
    private static Chat ImportDuplicate(AggregateHub hub, JsonObject attempt)
    {
        var old = new AggregateHub();
        var other = attempt.DeepClone().AsObject();
        other["nativeJoin"]!["generation"] = Nonce();
        other["requestId"] = Nonce();
        var chat = Handle(AggregateHub.Data(Open(old, other)));
        old.DeliverAll(Now);
        var control = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey);
        control["sessions"]![chat.Id] = old.Directory()[chat.Id]!.DeepClone();
        hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Value = new() { Json = control.ToJsonString() };
        hub.Of(AggregateKinds.Session, chat.Id).Value = new() { Json = old.State(AggregateKinds.Session, chat.Id).ToJsonString() };
        return chat;
    }

    [Fact]
    public async Task Concurrent_public_opens_converge_and_only_verified_handles_carry_native_identity()
    {
        var attempt = Attempt();
        Chat replacement;
        string database;
        await using (var host = new RelayHostFixture(disableDispatcher: true, configureServices: services => services.AddSingleton<ITokenCounter>(new ActivityLog())))
        {
            var app = host.Services.GetRequiredService<RelayApplication>();
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            var guard = new CallGuard(new Aggregates(factory, null), "test");
            Task<RelayReply> Call(string tool, object args) => app.ExecuteAsync(Alice, tool, JsonSerializer.SerializeToElement(args));
            async Task<(VerifiedNativeParticipant? Participant, RelayReply? Refusal)> Verify(Chat handle, RelayCaller caller)
            {
                var args = JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = handle.Id, sessionToken = handle.Token });
                Assert.True(guard.TryAdmit(caller, RelayTools.CheckInbox, args, null, out var admitted, out _));
                var (call, refusal) = await guard.VerifyAsync(admitted!, null);
                return (call.NativeParticipant, refusal);
            }
            database = host.DatabasePath;
            var opens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Call(RelayTools.SessionOpen, attempt)));
            var handles = opens.Select(reply => Handle(AggregateHub.Data(reply))).ToArray();
            var first = handles[0];
            Assert.All(handles, chat => Assert.Equal(first, chat));
            var verified = await Verify(first, Alice);
            Assert.Null(verified.Refusal);
            var identity = Assert.IsType<VerifiedNativeParticipant>(verified.Participant);
            Assert.Equal(first.Id, identity.SessionId);
            Assert.Equal("repo", identity.RepositoryId);
            Assert.DoesNotContain(first.Token, JsonSerializer.Serialize(identity));
            var denied = await Verify(first with { Token = "wrong" }, Alice);
            Assert.Null(denied.Participant);
            Assert.Equal(RelayErrorCodes.SessionDenied, denied.Refusal?.Code);
            denied = await Verify(first, new RelayCaller("other", false, ["repo"]));
            Assert.Null(denied.Participant);
            Assert.Equal(RelayErrorCodes.SessionDenied, denied.Refusal?.Code);

            var raw = Handle(AggregateHub.Data(await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = Nonce(), nativeParticipantId = identity.ParticipantId })));
            Assert.NotEqual(first.Id, raw.Id);
            Assert.True((await Call(RelayTools.SessionBind, new { repositoryId = "repo", sessionId = raw.Id, sessionToken = raw.Token, requestId = Nonce(),
                expectedVersion = 0, nativeParticipantId = identity.ParticipantId, role = "manager", binding = new { participantId = identity.ParticipantId } })).Ok);
            Assert.Null((await Verify(raw, Alice)).Participant);
            var privileged = await Call(RelayTools.ControlRequest, new { repositoryId = "repo", requestId = Nonce(), sessionId = raw.Id, sessionToken = raw.Token,
                command = "close_sessions", targetSessionId = first.Id, isAdministrator = true });
            Assert.Equal(RelayErrorCodes.AdministratorRequired, privileged.Code);
            Assert.Equal(identity, (await Verify(first, Alice)).Participant);

            var status = await Call(RelayTools.GetStatus, new { repositoryId = "repo", sessionId = first.Id, sessionToken = first.Token });
            Assert.True(status.Ok);
            Assert.DoesNotContain(identity.ParticipantId, status.DataJson);
            Assert.DoesNotContain(first.Token, status.DataJson);
            var workers = AggregateHub.Data(await Call(RelayTools.WorkerList, new { repositoryId = "repo" }));
            var row = Assert.Single(workers["items"]!.AsArray(), item => item!["sessionId"]!.ToString() == first.Id);
            Assert.DoesNotContain(identity.ParticipantId, row!.ToJsonString());
            Assert.DoesNotContain(first.Token, workers.ToJsonString());

            Assert.True((await app.ExecuteAsync(AggregateHub.Admin, RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new {repositoryId = "repo",
                requestId = Nonce(), command = "close_sessions", targetSessionId = first.Id, force = true}))).Ok);
            Assert.True((await factory.GetGrain<IControlGrain>(AggregateKinds.ControlKey).ExecuteAsync(Alice with { IsInternal = true }, Operations.Dispatch, "{}")).Ok);
            Assert.Equal(RelayErrorCodes.SessionUnknown, (await Call(RelayTools.SessionOpen, attempt)).Code);
            attempt["requestId"] = Nonce();
            attempt["nativeJoin"]!["generation"] = Nonce();
            var fresh = Handle(AggregateHub.Data(await Call(RelayTools.SessionOpen, attempt)));
            Assert.NotEqual(first, fresh);
            Assert.Equal(identity.ParticipantId, (await Verify(fresh, Alice)).Participant?.ParticipantId);
            replacement = fresh;

            var deniedAttempt = attempt.DeepClone().AsObject();
            deniedAttempt["repositoryId"] = "out-of-scope";
            Assert.Equal(RelayErrorCodes.RepositoryDenied, (await Call(RelayTools.SessionOpen, deniedAttempt)).Code);
            deniedAttempt["repositoryId"] = "repo";
            deniedAttempt["nativeJoin"]!.AsObject().Remove("generation");
            Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(RelayTools.SessionOpen, deniedAttempt)).Code);
            await host.Services.GetRequiredService<SqliteStore>().FlushActivityAsync();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var sql = connection.CreateCommand();
            sql.CommandText = "SELECT count(*) FROM grain_state WHERE instr(payload, $proof) > 0 OR instr(payload, $native) > 0";
            sql.Parameters.AddWithValue("$proof", attempt["nativeJoin"]!["bindingSecret"]!.ToString());
            sql.Parameters.AddWithValue("$native", attempt["nativeJoin"]!["nativeId"]!.ToString());
            Assert.Equal(0L, (long)sql.ExecuteScalar()!);
            sql.CommandText = "SELECT count(*) FROM activity_log WHERE tool='session_open' AND request_bytes>0 AND request_tokens IS NULL AND code IN ('InvalidArguments','RepositoryDenied')";
            Assert.Equal(2L, (long)sql.ExecuteScalar()!);
        }
        await using var restarted = new RelayHostFixture(database, disableDispatcher: true);
        var replay = await restarted.Services.GetRequiredService<RelayApplication>().ExecuteAsync(Alice, RelayTools.SessionOpen, JsonSerializer.SerializeToElement(attempt));
        Assert.Equal(replacement, Handle(AggregateHub.Data(replay)));
    }

    [Fact]
    public void Duplicate_cleanup_respects_unread_action_and_restore_fences()
    {
        var hub = new AggregateHub();
        var attempt = Attempt();
        var current = Handle(AggregateHub.Data(Open(hub, attempt)));
        hub.DeliverAll(Now);
        var idle = ImportDuplicate(hub, attempt);
        var unread = ImportDuplicate(hub, attempt);
        var owner = ImportDuplicate(hub, attempt);
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

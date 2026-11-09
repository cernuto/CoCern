using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// The pipeline's second stage: maps an admitted call to what runs it. Most tools are one aggregate call; the rest go to the handler that
/// composes them (<see cref="ClaimComposer"/>, <see cref="SessionTools"/>, <see cref="StatusTools"/>, <see cref="MessageTools"/>,
/// <see cref="SearchTools"/>). A turned-off feature answers for itself, so no route checks for one.
/// </summary>
internal sealed class ToolRouter(Aggregates aggregates, ClaimComposer claims, SessionTools sessions, StatusTools status, MessageTools messages, PeerTools peers, SearchTools searches, IDocCacheTools docs, IAdminTools admin)
{
    /// <summary>The availability a chat's own waiter registers: idle, and woken by the waiter's next read.</summary>
    internal const string WaitingAvailability = "waiting";
    /// <summary>How long one waiter poll keeps the chat reachable; the Claude waiter reads about once a minute, so two missed reads end it.</summary>
    internal static readonly TimeSpan WaiterFreshness = TimeSpan.FromMinutes(3);

    public async Task<RelayReply> RouteAsync(RelayCall call, CallTrace? trace, CancellationToken cancellationToken)
    {
        var reply = await DispatchAsync(call, trace, cancellationToken);
        if (reply.Ok && call.Session is not null && (call.CallOrigin is "claude-stop" or "claude-waiter" ||
            call.CallOrigin == "wake-watcher" && call.Tool == RelayTools.CheckInbox && call.NativeParticipant is not null && call.Controls.BackgroundCall == true)) RegisterWaiter(call);
        return reply;
    }

    /// <summary>Fresh authenticated metadata is added after replay, never stored as a historical permission or accepted from tool input.</summary>
    internal async Task<RelayReply> WithToolSettingsAsync(RelayCall call, RelayReply reply)
    {
        if (!reply.Ok) return reply;
        if (call.ActorSession is null && call.Tool != RelayTools.SessionOpen) return reply;
        if (JsonNode.Parse(reply.DataJson) is not JsonObject data) return reply;
        var own = call.ActorSession;
        var sessionId = own is not null ? call.ActingSessionId ?? "" : "";
        if (own is not null && sessionId.Length > 0)
        {
            var proof = call.Controls.ActingSessionId == sessionId ? call.Controls.ActingSessionToken ?? "" : Text(call.Args, "sessionToken");
            var fresh = await aggregates.Session(call.Caller, SessionVerify.Operation,
                new SessionVerify(Text(call.Args, "repositoryId"), sessionId, proof, Text(call.Args, "hubIncarnation")).ToJson());
            if (!fresh.Ok) return reply;
            own = Aggregates.Parse(fresh);
        }
        if (call.Tool == RelayTools.SessionOpen && Text(data, "sessionId") is { Length: > 0 } opened && Text(data, "sessionToken") is { Length: > 0 } token)
        {
            var verified = await aggregates.Session(call.Caller, SessionVerify.Operation,
                new SessionVerify(Text(call.Args, "repositoryId"), opened, token, Text(call.Args, "hubIncarnation")).ToJson());
            if (!verified.Ok) return reply;
            own = Aggregates.Parse(verified);
            sessionId = opened;
        }
        if (own?["toolSettings"] is not JsonObject settings || sessionId.Length == 0) return reply;
        data["toolSettings"] = settings.DeepClone();
        data["toolSettingsSessionId"] = sessionId;
        data["toolSettingsAdministrator"] = call.Caller.IsAdministrator;
        var enriched = reply with { DataJson = data.ToJsonString() };
        // Metadata is advisory. If even normal wire trimming cannot fit it, preserve the
        // original result; never let a profile refresh invalidate an already committed reply.
        return FitsWire(BoundWire(enriched)) ? enriched : reply;
    }

    /// <summary>
    /// A chat's waiter, or a verified native Codex watcher's background read, announces its direct wake path. The directory hears that as
    /// expiring presence, <c>waiting</c>. This is advisory reachability, not proof the model is idle or that it ran. A watcher that stops
    /// goes stale. A reported origin grants no identity or permission; the normal authenticated session checks still apply.
    /// </summary>
    private void RegisterWaiter(RelayCall call)
    {
        var now = DateTimeOffset.UtcNow;
        var presence = new SessionPresence { HubIncarnation = Text(call.Args, "hubIncarnation"), ObservedAt = now, FreshUntil = now + WaiterFreshness, Availability = WaitingAvailability };
        var registration = new SessionRegistration(Text(call.Args, "repositoryId"), call.ActingSessionId ?? "", presence).ToJson();
        Advisory.Run(() => aggregates.Control(call.Caller, Operations.Register, registration));
    }

    private Task<RelayReply> DispatchAsync(RelayCall call, CallTrace? trace, CancellationToken cancellationToken)
    {
        var (caller, tool, args, session) = (call.Caller, call.Tool, call.Args, call.Session);
        return tool switch
        {
            RelayTools.SessionOpen => sessions.OpenAsync(caller, args, trace),
            RelayTools.SessionToolSettingsSet => aggregates.Call(AggregateKinds.Session, Text(args, "targetSessionId"), caller, SessionToolSettings.SetOperation, args),
            RelayTools.RecordDecision when Text(args, "decision") == "approveGoal" => new GoalTools(aggregates, peers).ApproveAsync(call),
            RelayTools.EventWait => new EventWaitTools(aggregates).RunAsync(caller, args),
            RelayTools.GetStatus => status.StatusAsync(caller, args, call.Controls.StatusDetail),
            RelayTools.TaskClaim => claims.ClaimTaskAsync(caller, args, session),
            RelayTools.ClaimRenew or RelayTools.ClaimRelease when Text(args, "taskId").Length > 0 && Text(args, "resourceId").Length == 0 => aggregates.Board(caller, tool, args),
            RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.ClaimRelease => claims.ResourceClaimAsync(caller, tool, args, session),
            RelayTools.ActionBegin => claims.BeginActionAsync(caller, args, session),
            RelayTools.SendMessage => messages.SendMessageAsync(caller, args),
            RelayTools.ChatDirectory => messages.DirectoryAsync(args),
            RelayTools.Peers => peers.RunAsync(caller, args, call.NativeParticipant, cancellationToken),
            RelayTools.ControlRequest when Text(args, "command") == "revoke_machine" => messages.RevokeMachineAsync(caller, args),
            RelayTools.Search => searches.SearchAsync(args, cancellationToken),
            RelayTools.AnalyticsQuery => searches.AnalyticsAsync(caller, args, cancellationToken),
            RelayTools.CheckInbox => aggregates.Session(caller, tool, InboxArguments(args, call.Controls)),
            RelayTools.SessionBind or RelayTools.Ack => aggregates.Session(caller, tool, args),
            RelayTools.AttentionRespond => messages.RespondToAttentionAsync(caller, args),
            RelayTools.OrderPut or RelayTools.OrderRemove => status.OrderAsync(caller, tool, args),
            RelayTools.DocSearch => docs.SearchAsync(args),
            RelayTools.DocSection => docs.SectionAsync(args),
            // Administrator only, checked first, so a worker learns nothing about the hub's doc cache settings.
            RelayTools.DocSync => !caller.IsAdministrator ? Task.FromResult(Error(AdministratorRequired)) : docs.SyncAsync(args, cancellationToken),
            // The same, and only the caller's token decides: a session's role or being the recorded manager grants nothing here.
            RelayTools.HubDiagnose or RelayTools.AddPc or RelayTools.ConnectApp => !caller.IsAdministrator ? Task.FromResult(Error(AdministratorRequired)) : admin.RunAsync(caller, tool, args, cancellationToken),
            RelayTools.WorkerList or RelayTools.ControlRequest => aggregates.Control(caller, tool, args),
            RelayTools.TaskGet => aggregates.ReadTaskAsync(caller, args, cancellationToken),
            _ => aggregates.Board(caller, tool, args)
        };
    }

    /// <summary>The header-read controls, which the guard took from the call, given back to the session's inbox read as plain arguments.</summary>
    private static JsonObject InboxArguments(JsonObject args, CallControls controls)
    {
        if (!controls.HeadersOnly) return args;
        var read = (JsonObject)args.DeepClone();
        read["headersOnly"] = true;
        if (controls.KnownInboxEtag is { } known) read["knownInboxEtag"] = known;
        return read;
    }
}

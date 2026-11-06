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
        if (reply.Ok && call.Session is not null && call.CallOrigin is "claude-stop" or "claude-waiter") RegisterWaiter(call);
        return reply;
    }

    /// <summary>
    /// A Claude chat's waiter (its Stop hook, or the background <c>--await-inbox</c> command) reads the hub as the chat, so each read is the chat
    /// saying it is idle and can be woken. The directory hears that as presence, <c>waiting</c> until shortly after the next read is due. On the
    /// hub's own PC no node announces the chat, so this is the only thing that makes it reachable in <c>chat_directory</c>; a waiter that stops
    /// goes stale and the chat reads unreachable again. Advisory: a refused or failed registration changes nothing about the read.
    /// </summary>
    private void RegisterWaiter(RelayCall call)
    {
        var now = DateTimeOffset.UtcNow;
        var presence = new SessionPresence { HubIncarnation = Text(call.Args, "hubIncarnation"), ObservedAt = now, FreshUntil = now + WaiterFreshness, Availability = WaitingAvailability };
        var registration = new SessionRegistration(Text(call.Args, "repositoryId"), Text(call.Args, "sessionId"), presence).ToJson();
        Advisory.Run(() => aggregates.Control(call.Caller, Operations.Register, registration));
    }

    private Task<RelayReply> DispatchAsync(RelayCall call, CallTrace? trace, CancellationToken cancellationToken)
    {
        var (caller, tool, args, session) = (call.Caller, call.Tool, call.Args, call.Session);
        return tool switch
        {
            RelayTools.SessionOpen => sessions.OpenAsync(caller, args, trace),
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

using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// The pipeline's second stage: maps an admitted call to what runs it. Most tools are one aggregate call; the rest go to the handler that
/// composes them (<see cref="ClaimComposer"/>, <see cref="SessionTools"/>, <see cref="StatusTools"/>, <see cref="MessageTools"/>,
/// <see cref="SearchTools"/>). A turned-off feature answers for itself, so no route checks for one.
/// </summary>
internal sealed class ToolRouter(Aggregates aggregates, ClaimComposer claims, SessionTools sessions, StatusTools status, MessageTools messages, SearchTools searches, IDocCacheTools docs)
{
    public Task<RelayReply> RouteAsync(RelayCall call, CallTrace? trace, CancellationToken cancellationToken)
    {
        var (caller, tool, args, session) = (call.Caller, call.Tool, call.Args, call.Session);
        return tool switch
        {
            RelayTools.SessionOpen => sessions.OpenAsync(caller, args, trace),
            RelayTools.GetStatus => status.StatusAsync(caller, args, call.Controls.StatusDetail),
            RelayTools.TaskClaim => claims.ClaimTaskAsync(caller, args, session),
            RelayTools.ClaimRenew or RelayTools.ClaimRelease when Text(args, "taskId").Length > 0 && Text(args, "resourceId").Length == 0 => aggregates.Board(caller, tool, args),
            RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.ClaimRelease => claims.ResourceClaimAsync(caller, tool, args, session),
            RelayTools.ActionBegin => claims.BeginActionAsync(caller, args, session),
            RelayTools.SendMessage => messages.SendMessageAsync(caller, args),
            RelayTools.ChatDirectory => messages.DirectoryAsync(args),
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

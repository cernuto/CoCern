using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// One admitted call, as the router and the reply add-ons see it: the caller, trusted for internal operations (aggregates still check its
/// repositories and administrator rights), the tool and its definition, the checked arguments with the call controls taken out, and the
/// caller's session record once its handle is verified (null for a call that presents none).
/// </summary>
internal sealed record RelayCall(RelayCaller Caller, string Tool, RelayTool Definition, JsonObject Args, CallControls Controls, JsonObject? Session = null)
{
    internal VerifiedNativeParticipant? NativeParticipant { get; init; }
}

/// <summary>
/// The pipeline's first stage, in two steps. <see cref="TryAdmit"/> checks a call before any aggregate is touched: a known tool, the
/// arguments' shape, size and repository scope, no client copy of a field only the hub sets, and the adapter's call controls taken out.
/// <see cref="VerifyAsync"/> then reads aggregates: it verifies the session handle and holds claims while dispatch is paused. Each answers
/// a refusal, or the call to route. The second step runs inside the caller's argument-error mapping, the first does not.
/// </summary>
internal sealed class CallGuard(Aggregates aggregates, string incarnation)
{
    /// <summary>Admits a call, or refuses it before anything reads it. An admitted call carries this hub's incarnation.</summary>
    public bool TryAdmit(RelayCaller caller, string tool, JsonElement arguments, CallTrace? trace, [NotNullWhen(true)] out RelayCall? call, [NotNullWhen(false)] out RelayReply? refusal)
    {
        (call, refusal) = (null, null);
        if (!RelayToolCatalog.TryGet(tool, out var definition)) { refusal = Error(UnknownTool); return false; }
        if (RelayRequestGuard.Validate(caller, definition, arguments, out var args) is { } rejected) { refusal = rejected; return false; }
        // Fields only the hub sets are dropped from what the client sent, before anything reads them.
        ServerArguments.StripClientValues(tool, args);
        var controls = CallControls.Take(args);
        if (trace is not null)
        {
            trace.Args = args;
            if (tool is RelayTools.TaskUpdate or RelayTools.ActionReport) trace.Usage = controls.Usage;
            (trace.ModelVisible, trace.KnownEtag) = (controls.ModelVisible, controls.KnownEtag);
            trace.BackgroundCall = controls.BackgroundCall;
        }
        args["hubIncarnation"] = incarnation;
        call = new(caller with { IsInternal = true }, tool, definition, args, controls);
        return true;
    }

    /// <summary>
    /// Verifies the session handle of a tool that requires one, and of a <c>get_status</c> that names a session; <c>session_open</c> needs
    /// none. A task claim, resource claim or action begin is then refused while dispatch is paused. Returns the call with its verified
    /// session, or the refusal.
    /// </summary>
    public async Task<(RelayCall Call, RelayReply? Refusal)> VerifyAsync(RelayCall call, CallTrace? trace)
    {
        var (caller, tool, args) = (call.Caller, call.Tool, call.Args);
        if (call.Definition.RequiresSession || tool == RelayTools.GetStatus && Text(args, "sessionId").Length > 0)
        {
            var verified = await VerifySessionAsync(caller, args);
            if (!verified.Ok) return (call, verified);
            var session = Aggregates.Parse(verified);
            var participant = Text(session, NativeParticipant.Id);
            session.Remove(NativeParticipant.Id);
            call = call with { Session = session, NativeParticipant = participant.Length == 0 ? null :
                new VerifiedNativeParticipant(participant, Text(args, "sessionId"), Text(args, "repositoryId")) };
            // A background status read (the prompt hook, a wake watcher) is not the session's own turn, so it carries no role.
            if (trace is not null) { trace.SessionId = Text(args, "sessionId"); trace.Role = call.Controls.StatusDetail && tool == RelayTools.GetStatus ? null : Text(session, "role"); }
        }
        if (tool is RelayTools.TaskClaim or RelayTools.ClaimAcquire or RelayTools.ActionBegin && await PausedAsync(caller, args) is { } paused) return (call, paused);
        return (call, null);
    }

    /// <summary>The session's own check of its handle: its record when the handle is valid, else the refusal (<c>SessionDenied</c> without one).</summary>
    public Task<RelayReply> VerifySessionAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0
            ? Task.FromResult(Error(SessionDenied))
            : aggregates.Session(caller, SessionVerify.Operation, new SessionVerify(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "sessionToken"), Text(args, "hubIncarnation")).ToJson());

    private async Task<RelayReply?> PausedAsync(RelayCaller caller, JsonObject args)
    {
        var status = await aggregates.Control(caller, RelayTools.GetStatus, args);
        if (!status.Ok) return status;
        return Aggregates.Parse(status)["paused"]?.GetValue<bool>() == true ? Error(Paused) : null;
    }
}

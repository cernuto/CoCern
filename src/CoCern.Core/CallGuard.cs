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
    /// <summary>On an administrative call, the chat that proved it is making it; null when it proved nothing and only its token speaks.</summary>
    internal string? ActingSessionId { get; init; }
    /// <summary>The manager generation that chat holds, read when it proved itself; null for any other chat. Attribution, never authority.</summary>
    internal long? ManagerGeneration { get; init; }
    /// <summary>
    /// Who the adapter says made the call (<c>mcp</c>, <c>claude-stop</c>, <c>claude-waiter</c>, <c>wake-watcher</c>, <c>node</c>); null when unreported or
    /// not one of those. A report, never authority: it tells a chat's own waiter apart from the chat, so the directory can hear the waiter as presence.
    /// </summary>
    internal string? CallOrigin { get; init; }
}

/// <summary>
/// The pipeline's first stage, in two steps. <see cref="TryAdmit"/> checks a call before any aggregate is touched: a known tool, the
/// arguments' shape, size and repository scope, no client copy of a field only the hub sets, and the adapter's call controls taken out.
/// <see cref="VerifyAsync"/> then reads aggregates: it verifies the session handle and holds claims while dispatch is paused. Each answers
/// a refusal, or the call to route. The second step runs inside the caller's argument-error mapping, the first does not.
/// </summary>
internal sealed class CallGuard(Aggregates aggregates, string incarnation, Func<string, string?>? canonicalRepository = null)
{
    /// <summary>Admits a call, or refuses it before anything reads it. An admitted call carries this hub's incarnation.</summary>
    public bool TryAdmit(RelayCaller caller, string tool, JsonElement arguments, CallTrace? trace, [NotNullWhen(true)] out RelayCall? call, [NotNullWhen(false)] out RelayReply? refusal)
    {
        (call, refusal) = (null, null);
        if (!RelayToolCatalog.TryGet(tool, out var definition)) { refusal = Error(UnknownTool); return false; }
        if (RelayRequestGuard.Validate(caller, definition, arguments, out var args, canonicalRepository) is { } rejected) { refusal = rejected; return false; }
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
        // Read from the raw arguments: the guard above already dropped the adapter's report from what the tool sees.
        var origin = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("callOrigin", out var reported) && reported.ValueKind == JsonValueKind.String ? reported.GetString() : null;
        call = new(caller with { IsInternal = true }, tool, definition, args, controls) { CallOrigin = origin is "mcp" or "claude-stop" or "claude-waiter" or "wake-watcher" or "node" ? origin : null };
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
        return call.Definition.Audited && caller.IsAdministrator && call.Controls.ActingSessionId is { Length: > 0 } acting ? await ActingAsync(call, acting) : (call, null);
    }

    /// <summary>
    /// An administrative call that names the chat making it. The proof is checked like any session handle, and a wrong one refuses. A former manager is
    /// then refused, delayed or not: appointing someone else ended what it may present as itself. A chat whose session has closed goes on as its token
    /// alone unless it was that manager, since an adapter can outlive its handle and a closed handle must not stop a pause. This never adds authority: the token already decided, and the aggregates check it again.
    /// </summary>
    private async Task<(RelayCall Call, RelayReply? Refusal)> ActingAsync(RelayCall call, string acting)
    {
        var args = call.Args;
        var repository = Text(args, "repositoryId");
        var proof = new JsonObject { ["repositoryId"] = repository, ["sessionId"] = acting, ["sessionToken"] = call.Controls.ActingSessionToken ?? "", ["hubIncarnation"] = Text(args, "hubIncarnation") };
        var verified = await VerifySessionAsync(call.Caller, proof);
        if (!verified.Ok && verified.Code != SessionUnknown) return (call, verified);
        var proven = verified.Ok ? call with { ActingSessionId = acting } : call;
        var standing = await aggregates.Request(AggregateKinds.ControlKey, call.Caller, new ManagerStandingQuery(repository, acting));
        if (!standing.Ok) return (proven, standing);
        var seat = Aggregates.Parse(standing);
        if (IsTrue(seat, "revoked")) return (proven, Error(ManagerRevoked, new { currentGeneration = Number(seat, "generation") }));
        if (!verified.Ok) return (call, null);
        if (call.Tool == RelayTools.ControlRequest) args[ServerArguments.ActingSession] = acting;
        return (proven with { ManagerGeneration = IsTrue(seat, "manager") ? Number(seat, "generation") : null }, null);
    }

    /// <summary>The session's own check of its handle: its record when the handle is valid, else the refusal (<c>SessionDenied</c> without one).</summary>
    public Task<RelayReply> VerifySessionAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0
            ? Task.FromResult(Error(SessionDenied))
            : aggregates.Session(caller, SessionVerify.Operation, new SessionVerify(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "sessionToken"), Text(args, "hubIncarnation")).ToJson());

    /// <summary>
    /// Opt-in protected-file check. Reauthenticates this handle, then reads its current native generation and closing fence.
    /// Never derives authority from labels, bindings or supplied claim arguments; ordinary relay calls do not pay for this read.
    /// The returned checkout proof is point-in-time: the file consumer must revalidate at use or hold physical handles.
    /// </summary>
    public async Task<VerifiedNativeFileScope?> ResolveNativeFileScopeAsync(RelayCall call)
    {
        if (call.Caller.AuthenticatedMachineId is not { Length: > 0 } machine) return null;
        var verified = await VerifySessionAsync(call.Caller, call.Args);
        if (!verified.Ok) return null;
        var participant = Text(Aggregates.Parse(verified), NativeParticipant.Id);
        if (participant.Length == 0) return null;
        var session = Text(call.Args, "sessionId");
        var repository = Text(call.Args, "repositoryId");
        var reply = await aggregates.Request(AggregateKinds.ControlKey, call.Caller with { IsInternal = true },
            new NativeFileScopeQuery(repository, session, Text(call.Args, "sessionToken"), participant));
        if (!reply.Ok) return null;
        var scope = Aggregates.Parse(reply);
        var root = Text(scope, "canonicalWorkingCopyRoot");
        var generation = Text(scope, "nativeGeneration");
        if (Text(scope, "participantId") != participant || Text(scope, "sessionId") != session || Text(scope, "repositoryId") != repository
            || Text(scope, "machineScope") != machine || root.Length == 0 || generation.Length == 0) return null;
        return new(participant, session, repository, machine, root, generation);
    }

    private async Task<RelayReply?> PausedAsync(RelayCaller caller, JsonObject args)
    {
        var status = await aggregates.Control(caller, RelayTools.GetStatus, args);
        if (!status.Ok) return status;
        return Aggregates.Parse(status)["paused"]?.GetValue<bool>() == true ? Error(Paused) : null;
    }
}

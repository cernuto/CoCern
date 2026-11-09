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
/// <c>session_open</c>: records the session in the control directory, initializes its private aggregate and attaches the requested task
/// packet and the board's standing. After the reply, off its path, a manager's open starts the repository's anomaly monitor and a reported
/// workspace may teach the doc cache. Its reply is bounded here and takes no reply add-ons.
/// </summary>
internal sealed class SessionTools(Aggregates aggregates, StandingDelivery standing, AttentionOptions attention, AnomalyOptions? anomaly, IDocCacheTools docs, bool searchOn, bool analyticsOn)
{
    public async Task<RelayReply> OpenAsync(RelayCaller caller, JsonObject args, CallTrace? trace)
    {
        Task<string?> Resolve(JsonObject evidence) => docs.ResolveNativeAsync(caller, (string?)evidence["checkout"], (string?)evidence["common"], (string?)evidence["origin"], (string?)evidence["repositoryId"]);
        if (args["nativePreflight"] is JsonObject evidence)
        {
            var repo = await Resolve(evidence);
            return repo is not null && caller.MayAccess(repo) ? Ok(new { repositoryId = repo }) :
                Error(RepositoryDenied, new { guidance = "Select an authorized repository for this connection. If the hub has its clone, its Git origin must match; otherwise configure the repository ID on the adapter." });
        }
        // Fresh authorization and mapping on this allocating request, after the caller obtained the exclusive lease.
        if (args.Remove(RelayRequestGuard.CheckedNativeResolution, out var checkedEvidence) &&
            (checkedEvidence is not JsonObject mapping || await Resolve(mapping) != Text(args, "repositoryId")))
            return Error(RepositoryDenied);
        var opened = BoundWire(await OpenSessionAsync(caller, args));
        if (trace is not null && opened.Ok) { trace.SessionId = Text(Aggregates.Parse(opened), "sessionId"); trace.Role = Text(args, "role"); }
        if (opened.Ok && string.Equals(Text(args, "role"), SessionRoles.Manager, StringComparison.OrdinalIgnoreCase)) StartAnomalyMonitor(caller, Text(args, "repositoryId"));
        if (opened.Ok) LearnWorkspace(args);
        return opened;
    }

    /// <summary>The one line a chat gets about reporting that it waits on Paul.</summary>
    internal const string WaitingHint = "When you stop for Paul's answer, report it with ack waitingFor; clear it next turn with \"\".";

    /// <summary>Records the session in the control directory, initializes its private aggregate, and attaches the requested task packet.</summary>
    private async Task<RelayReply> OpenSessionAsync(RelayCaller caller, JsonObject args)
    {
        if (Flag(args, NativeParticipant.Adoption)) return await AdoptNativeAsync(caller, args);
        var opened = await aggregates.Control(caller, RelayTools.SessionOpen, args);
        if (opened.Code == Unavailable && Text(args, NativeParticipant.Id) is { Length: > 0 } participant
            && Text(Aggregates.Parse(opened), "nativeCheck") is { Length: > 0 } pending)
        {
            var check = new NativeExpiryCheck(Text(args, "repositoryId"), pending, participant);
            var outcome = await aggregates.Call(AggregateKinds.Session, pending, caller, NativeExpiryCheck.Operation, check.ToJson());
            if (!outcome.Ok) return outcome;
            if (!Flag(Aggregates.Parse(outcome), "expired")) return Error(Unavailable);
            // A second serialized decision compares the exact session that answered; a competing replacement cannot be advanced.
            var confirmed = args.DeepClone().AsObject();
            confirmed[NativeParticipant.ConfirmedExpiry] = pending;
            opened = await aggregates.Control(caller, RelayTools.SessionOpen, confirmed);
        }
        if (!opened.Ok) return opened.Code == Unavailable ? Error(Unavailable) : opened;
        var data = Aggregates.Parse(opened);
        if (Flag(data, "nativePending")) return Error(Unavailable);
        // The same record the directory queued, from the client's fields only: the session takes its freshness from the same defaults.
        var record = SessionRecord.Opened(Text(data, "sessionId"), Text(data, "sessionToken"), caller.PrincipalId, Text(args, "repositoryId"), args);
        var initialized = await aggregates.Call(AggregateKinds.Session, record.SessionId, caller, SessionOpen.Operation, new SessionOpen(record).ToJson());
        if (!initialized.Ok) return initialized;
        // The session closed before this open reached it: a close overtook it, or this retry replays the handle of a session closed since.
        // The chat opens a new one, as after any SessionUnknown.
        if (Aggregates.Parse(initialized)["expired"]?.GetValue<bool>() == true) return Error(SessionUnknown, new { expired = true, reopen = RelayTools.SessionOpen });
        var settingsArgs = new JsonObject { ["sessionId"] = record.SessionId, ["sessionToken"] = record.SessionToken, ["repositoryId"] = record.RepositoryId };
        var settings = await aggregates.Session(caller, RelayTools.GetStatus, settingsArgs);
        if (!settings.Ok) return settings;
        data["toolSettings"] = Aggregates.Parse(settings)["toolSettings"]?.DeepClone();
        // The queued initialization remains a retryable durable effect and deduplicates at its recipient.
        if (Text(args, "taskId").Length > 0)
        {
            var packet = await aggregates.Board(caller, RelayTools.TaskGet, args);
            data["taskPacket"] = packet.Ok ? JsonNode.Parse(packet.DataJson) : JsonSerializer.SerializeToNode(new { code = packet.Code });
        }
        // In this reply and not the tool's description: it costs once per session, not on every turn, and server instructions have proved not to reach every chat.
        data["waitingHint"] = WaitingHint;
        data[ChatGuidance.StartHintField] = ChatGuidance.StartHint(caller.IsAdministrator, searchOn, analyticsOn, docs: DocCacheTool.IsEnabled(docs));
        await standing.AttachAtOpenAsync(caller, args, data);
        return Ok(data, opened.Version);
    }

    private async Task<RelayReply> AdoptNativeAsync(RelayCaller caller, JsonObject args)
    {
        // The session aggregate checks its exact principal/repository/secret and rejects closed or differently owned sessions before any directory mutation.
        var proof = new NativeSessionAdoption(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "sessionToken"),
            Text(args, NativeParticipant.Id), Text(args, NativeParticipant.Generation), Text(args, NativeParticipant.Request));
        var probe = proof.ToJson();
        probe["nativeAdoptionProbe"] = true;
        var verified = await aggregates.Session(caller, NativeSessionAdoption.Operation, probe);
        if (!verified.Ok) return verified;
        var reserved = await aggregates.Control(caller, RelayTools.SessionOpen, args);
        if (!reserved.Ok) return reserved;
        var confirmed = await aggregates.Session(caller, NativeSessionAdoption.Operation, proof.ToJson());
        if (!confirmed.Ok) return confirmed;
        var data = Aggregates.Parse(confirmed);
        if (!Flag(data, "adopted")) return Error(SessionUnknown);
        data.Remove("adopted");
        data["waitingHint"] = WaitingHint;
        data[ChatGuidance.StartHintField] = ChatGuidance.StartHint(caller.IsAdministrator, searchOn, analyticsOn, docs: DocCacheTool.IsEnabled(docs));
        return Ok(data, confirmed.Version);
    }

    /// <summary>
    /// A manager session starts its repository's anomaly monitor when analytics, attention and the monitor are all on. It runs after the
    /// reply, because the monitor grain may be busy with a check, and it never schedules a second check.
    /// </summary>
    private void StartAnomalyMonitor(RelayCaller caller, string repository)
    {
        if (anomaly is not { Enabled: true } || !attention.Enabled) return;
        Advisory.Run(() => aggregates.Call(AnomalyStart.TargetKind, repository, caller, AnomalyStart.Operation, new AnomalyStart(repository).ToJson())); // else the next manager starts it
    }

    /// <summary>
    /// The adapter reports its git workspace with session_open (never advertised in a schema). The doc cache may index it, after the reply
    /// and off the open's path: a refused or failing workspace never changes the open.
    /// </summary>
    private void LearnWorkspace(JsonObject args)
    {
        if (args["workspace"] is not JsonValue reported || !reported.TryGetValue<string>(out var workspace) || string.IsNullOrWhiteSpace(workspace)) return;
        var repository = Text(args, "repositoryId");
        Advisory.Run(() => docs.LearnAsync(repository, workspace));
    }
}

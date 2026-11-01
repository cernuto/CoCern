using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PkRelay.Contracts;
using PkRelay.Domain;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Core;

/// <summary>
/// Executes public tools. Each tool maps to one aggregate call, or to a use case that composes several
/// (session open, task claim with resources, action begin, convenience renewals/acks, next-work offers).
/// Aggregates enforce the rules; this layer authenticates the session, routes, composes and bounds replies.
/// </summary>
public sealed class RelayApplication(IGrainFactory grains, IMachineDirectory? machines = null, IActivitySink? activity = null) : IRelayApplication
{
    private readonly IMachineDirectory directory = machines ?? new MachineDirectory(grains);
    private const int WorkerProbeLimit = 50;
    private readonly string incarnation = Guid.NewGuid().ToString("N");
    public string IncarnationId => incarnation;

    /// <summary>Runs one public tool call and, when a sink is configured, records it once. Recording never changes or fails the call.</summary>
    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (activity is null || !RelayToolCatalog.TryGet(tool, out _)) return await ExecuteCoreAsync(caller, tool, arguments, null, cancellationToken);
        var trace = new CallTrace { At = DateTimeOffset.UtcNow };
        var started = Stopwatch.GetTimestamp();
        RelayReply? reply = null;
        try { return reply = await ExecuteCoreAsync(caller, tool, arguments, trace, cancellationToken); }
        finally { RecordCall(tool, arguments, trace, reply, Stopwatch.GetElapsedTime(started), cancellationToken.IsCancellationRequested); }
    }

    private async Task<RelayReply> ExecuteCoreAsync(RelayCaller caller, string tool, JsonElement arguments, CallTrace? trace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RelayToolCatalog.TryGet(tool, out var definition)) return Error(UnknownTool);
        if (RelayRequestGuard.Validate(caller, definition, arguments, out var args) is { } rejected) return rejected;
        // Reported usage is telemetry only: it is recorded with the call and never reaches aggregate state.
        if (args.TryGetPropertyValue("usage", out var usage))
        {
            args.Remove("usage");
            if (trace is not null && tool is RelayTools.TaskUpdate or RelayTools.ActionReport) trace.Usage = usage as JsonObject;
        }
        if (trace is not null) trace.Args = args;
        args["hubIncarnation"] = incarnation;
        var trusted = caller with { IsInternal = true };
        try
        {
            if (tool == RelayTools.SessionOpen)
            {
                var opened = BoundWire(await OpenSessionAsync(trusted, args));
                if (trace is not null && opened.Ok) { trace.SessionId = Text(Parse(opened), "sessionId"); trace.Role = Text(args, "role"); }
                return opened;
            }
            JsonObject? session = null;
            if (definition.RequiresSession || tool == RelayTools.GetStatus && Text(args, "sessionId").Length > 0)
            {
                var verified = await VerifySessionAsync(trusted, args);
                if (!verified.Ok) return verified;
                session = Parse(verified);
                if (trace is not null) { trace.SessionId = Text(args, "sessionId"); trace.Role = Text(session, "role"); }
            }
            if (tool is RelayTools.TaskClaim or RelayTools.ClaimAcquire or RelayTools.ActionBegin && await PausedAsync(trusted, args) is { } paused) return paused;
            var result = await DispatchAsync(trusted, tool, args, session);
            if (result.Ok) result = await PiggybackAsync(trusted, args, result);
            if (result.Ok && tool == RelayTools.TaskUpdate && args["includeNextWork"]?.GetValue<bool>() == true) result = await WithNextWorkAsync(trusted, args, result);
            return BoundWire(result);
        }
        catch (ArgumentException) { return Error(InvalidArguments); }
        catch (InvalidOperationException) { return Error(InvalidArguments); }
        catch (JsonException) { return Error(InvalidArguments); }
    }

    /// <summary>Delivers one aggregate's due outbox effects; called by the hub's due-work pump.</summary>
    public async Task DispatchDueAsync(DueWorkReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = new RelayCaller("relay-dispatcher", false, [], true);
        var reply = await Call(reference.GrainType, reference.GrainKey, dispatcher, Operations.Dispatch, new() { ["workId"] = reference.WorkId });
        if (!reply.Ok) throw new InvalidOperationException("Durable dispatch remains pending: " + reply.Code);
    }

    private Task<RelayReply> DispatchAsync(RelayCaller caller, string tool, JsonObject args, JsonObject? session) => tool switch
    {
        RelayTools.TaskClaim => ClaimTaskAsync(caller, args),
        RelayTools.ClaimRenew or RelayTools.ClaimRelease when Text(args, "taskId").Length > 0 && Text(args, "resourceId").Length == 0 => CallBoard(caller, tool, args),
        RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.ClaimRelease => ResourceClaimAsync(caller, tool, args),
        RelayTools.ActionBegin => BeginActionAsync(caller, args, session),
        RelayTools.SendMessage => SendMessageAsync(caller, args),
        RelayTools.GetStatus => StatusAsync(caller, args),
        RelayTools.ChatDirectory => directory.DirectoryAsync([Text(args, "repositoryId")], CancellationToken.None),
        RelayTools.ControlRequest when Text(args, "command") == "revoke_machine" => RevokeMachineAsync(caller, args),
        RelayTools.SessionBind or RelayTools.CheckInbox or RelayTools.Ack => CallSession(caller, tool, args),
        RelayTools.WorkerList or RelayTools.ControlRequest => CallControl(caller, tool, args),
        _ => CallBoard(caller, tool, args)
    };

    /// <summary>Revoking a machine is a discovery concern, not a control-aggregate one: it needs the machine ID and is refused for non-administrators by the discovery grain.</summary>
    private Task<RelayReply> RevokeMachineAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "machineId").Length == 0 ? Task.FromResult(Error(InvalidArguments)) : directory.RevokeAsync(caller, Text(args, "machineId"), CancellationToken.None);

    /// <summary>Records the session in the control directory, initializes its private aggregate, and attaches the requested task packet.</summary>
    private async Task<RelayReply> OpenSessionAsync(RelayCaller caller, JsonObject args)
    {
        var opened = await CallControl(caller, RelayTools.SessionOpen, args);
        if (!opened.Ok) return opened;
        var data = Parse(opened);
        var initialize = args.DeepClone().AsObject();
        initialize["sessionId"] = Text(data, "sessionId"); initialize["sessionToken"] = Text(data, "sessionToken"); initialize["principalId"] = caller.PrincipalId; initialize.Remove("requestId");
        var initialized = await Call(AggregateKinds.Session, Text(data, "sessionId"), caller, Operations.Open, initialize);
        if (!initialized.Ok) return initialized;
        // The queued initialization remains a retryable durable effect and deduplicates at its recipient.
        if (Text(args, "taskId").Length > 0)
        {
            var packet = await CallBoard(caller, RelayTools.TaskGet, args);
            data["taskPacket"] = packet.Ok ? JsonNode.Parse(packet.DataJson) : JsonSerializer.SerializeToNode(new { code = packet.Code });
        }
        return Ok(data, opened.Version);
    }

    private Task<RelayReply> VerifySessionAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0
            ? Task.FromResult(Error(SessionDenied))
            : CallSession(caller, Operations.Verify, args);

    private async Task<RelayReply?> PausedAsync(RelayCaller caller, JsonObject args)
    {
        var status = await CallControl(caller, RelayTools.GetStatus, args);
        if (!status.Ok) return status;
        return Parse(status)["paused"]?.GetValue<bool>() == true ? Error(Paused) : null;
    }

    private async Task<RelayReply> ResourceClaimAsync(RelayCaller caller, string tool, JsonObject args)
    {
        var resolved = await Resolve(caller, args, Text(args, "resourceId"));
        if (!resolved.Ok) return resolved;
        return await Call(AggregateKinds.Lock, Text(Parse(resolved), "resourceId"), caller, tool, args);
    }

    /// <summary>A named recipient is probed in the worker directory first; delivery itself enforces repository identity and unknown recipients stay visibly pending.</summary>
    private async Task<RelayReply> SendMessageAsync(RelayCaller caller, JsonObject args)
    {
        if (Text(args, "recipientSessionId").Length > 0)
        {
            var probe = await CallControl(caller, RelayTools.WorkerList, new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["limit"] = WorkerProbeLimit });
            if (!probe.Ok) return probe;
        }
        return Text(args, "taskId").Length > 0 ? await CallBoard(caller, RelayTools.SendMessage, args) : await CallSession(caller, RelayTools.SendMessage, args);
    }

    private async Task<RelayReply> StatusAsync(RelayCaller caller, JsonObject args)
    {
        if (Text(args, "sessionId").Length == 0) return await CallControl(caller, RelayTools.GetStatus, args);
        var result = await CallSession(caller, RelayTools.GetStatus, args);
        if (!result.Ok) return result;
        var control = await CallControl(caller, RelayTools.GetStatus, args);
        if (!control.Ok) return control;
        var data = Parse(result); data["control"] = JsonNode.Parse(control.DataJson);
        return Ok(data, result.Version);
    }

    /// <summary>
    /// Claims the task, then each canonical resource in a stable order. If any resource is busy, the grants this
    /// request newly acquired are released and a newly acquired task claim is rolled back.
    /// </summary>
    private async Task<RelayReply> ClaimTaskAsync(RelayCaller caller, JsonObject args)
    {
        var canonical = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var resource in Strings(args["resourceIds"]))
        {
            var resolved = await Resolve(caller, args, resource);
            if (!resolved.Ok) return resolved;
            canonical.Add(Text(Parse(resolved), "resourceId"));
        }
        if (!GrantFits(args, canonical)) return Error(PayloadTooLarge);
        var claimArgs = args.DeepClone().AsObject(); claimArgs["resourceIds"] = JsonSerializer.SerializeToNode(canonical);
        var task = await CallBoard(caller, RelayTools.TaskClaim, claimArgs);
        if (!task.Ok) return task;
        var acquired = new List<(string Resource, long Generation)>();
        var grants = new JsonArray();
        foreach (var resource in canonical)
        {
            var resourceArgs = args.DeepClone().AsObject(); resourceArgs["requestId"] = Text(args, "requestId") + ":resource:" + resource;
            var claim = await Call(AggregateKinds.Lock, resource, caller, RelayTools.ClaimAcquire, resourceArgs);
            if (!claim.Ok)
            {
                await RollBackClaimAsync(caller, args, acquired, task);
                return Error(Busy, new { resourceId = resource, detail = JsonNode.Parse(claim.DataJson), rollback = "new_grants_released" });
            }
            var grant = Parse(claim);
            if (grant["newlyAcquired"]?.GetValue<bool>() == true) acquired.Add((resource, Number(grant, "generation")));
            grant["resourceId"] = resource; grants.Add(grant);
        }
        var data = Parse(task); data["grants"] = grants; data["complete"] = true;
        return Ok(data, task.Version);
    }

    /// <summary>
    /// The complete grant response is authorization material and cannot be truncated after acquiring ownership.
    /// Reserve its worst-case metadata before changing any aggregate.
    /// </summary>
    private static bool GrantFits(JsonObject args, SortedSet<string> resources)
    {
        var maxLease = DateTimeOffset.MaxValue.ToString("O");
        var grants = resources.Select(resource => new { resourceId = resource, holder = Text(args, "sessionId"), generation = long.MaxValue, leaseUntil = maxLease, newlyAcquired = false }).ToArray();
        var preview = Ok(new { taskId = Text(args, "taskId"), newlyAcquired = false, claimGeneration = long.MaxValue, leaseUntil = maxLease, resourceIds = resources, grants, complete = true }, long.MaxValue);
        return FitsWire(preview, RelayLimits.AggregateReplyBytes);
    }

    private async Task RollBackClaimAsync(RelayCaller caller, JsonObject args, List<(string Resource, long Generation)> acquired, RelayReply task)
    {
        foreach (var grant in acquired)
        {
            var release = args.DeepClone().AsObject(); release["generation"] = grant.Generation; release.Remove("requestId");
            await Call(AggregateKinds.Lock, grant.Resource, caller, RelayTools.ClaimRelease, release);
        }
        var claimed = Parse(task);
        if (claimed["newlyAcquired"]?.GetValue<bool>() == true)
        {
            var release = args.DeepClone().AsObject(); release["claimGeneration"] = claimed["claimGeneration"]?.DeepClone(); release["claimRequestId"] = Text(args, "requestId"); release.Remove("requestId");
            await CallBoard(caller, Operations.TaskUnclaim, release);
        }
    }

    /// <summary>
    /// Records the action with the session's verified binding. A first (replayable) attempt must also block its
    /// resources before the agent may invoke it; if that cannot be confirmed, the agent is told not to proceed.
    /// </summary>
    private async Task<RelayReply> BeginActionAsync(RelayCaller caller, JsonObject args, JsonObject? session)
    {
        args["bindingVersion"] = session?["bindingVersion"]?.DeepClone(); args["binding"] = session?["binding"]?.DeepClone();
        var result = await CallBoard(caller, RelayTools.ActionBegin, args);
        if (!result.Ok || Parse(result)["replayAllowed"]?.GetValue<bool>() != true) return result;
        var actionId = Text(args, "actionId");
        var attempt = Number(Parse(result)["action"]!.AsObject(), "attempt", 1);
        var delivery = await Call(AggregateKinds.Board, BoardKey(args), caller, Operations.Dispatch, new() { ["operation"] = Operations.Block, ["actionKey"] = CoordinationStateMachine.ActionKey(args, actionId, attempt) });
        if (!delivery.Ok) return Error(ActionPending, new { actionId, replayAllowed = false, reason = delivery.Code });
        var current = await CallBoard(caller, RelayTools.TaskGet, args);
        if (!current.Ok || current.Version != result.Version) return Error(ActionChanged, new { actionId, replayAllowed = false });
        return result;
    }

    /// <summary>Adds an advisory next-work offer to a committed milestone. The milestone stands whatever the offer's outcome.</summary>
    private async Task<RelayReply> WithNextWorkAsync(RelayCaller caller, JsonObject args, RelayReply committed)
    {
        JsonNode next;
        try
        {
            var status = await CallControl(caller, RelayTools.GetStatus, args);
            if (!status.Ok) next = Outcome(false, status.Code);
            else if (Parse(status)["paused"]?.GetValue<bool>() == true) next = Outcome(true, Paused);
            else
            {
                var read = args.DeepClone().AsObject(); read.Remove("requestId"); read.Remove("busyResources"); read["limit"] = RelayLimits.NextWorkCandidates;
                var offered = await CallBoard(caller, Operations.NextWork, read);
                var offer = offered.Ok ? Parse(offered) : null;
                var busy = offer?["candidates"] is JsonArray candidates ? await BusyResourcesAsync(caller, args, candidates) : [];
                if (busy.Count > 0)
                {
                    // Ask again so that, within equal priority, a task whose listed resources are all free is offered first.
                    read.Remove("limit"); read["busyResources"] = JsonSerializer.SerializeToNode(busy);
                    offered = await CallBoard(caller, Operations.NextWork, read);
                    offer = offered.Ok ? Parse(offered) : null;
                }
                offer?.Remove("candidates");
                next = offer ?? Outcome(false, offered.Code);
            }
        }
        catch (Exception) { next = Outcome(false, Unavailable); }
        var data = Parse(committed); data["nextWork"] = next;
        return Ok(data, committed.Version);
    }

    /// <summary>
    /// The candidates' listed resources that another session holds now, in their listed spelling. At most
    /// <see cref="RelayLimits.MaxOfferResourceLookups"/> are looked up; unknown or unchecked resources count as free. Advisory only.
    /// </summary>
    private async Task<List<string>> BusyResourcesAsync(RelayCaller caller, JsonObject args, JsonArray candidates)
    {
        try
        {
            var listed = candidates.SelectMany(candidate => Strings(candidate?["resources"])).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxOfferResourceLookups).ToArray();
            if (listed.Length == 0) return [];
            var lookup = new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["machineId"] = Text(args, "machineId") };
            async Task<bool> IsBusy(string resource)
            {
                var resolved = await Resolve(caller, lookup, resource);
                if (!resolved.Ok) return false;
                var claim = await Call(AggregateKinds.Lock, Text(Parse(resolved), "resourceId"), caller, Operations.ClaimState, lookup);
                return claim.Ok && Parse(claim)["busy"]?.GetValue<bool>() == true;
            }
            var busy = await Task.WhenAll(listed.Select(IsBusy));
            return listed.Where((_, index) => busy[index]).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Optional claim renewals and inbox acknowledgments riding on another call. Their outcomes never change the committed result.</summary>
    private async Task<RelayReply> PiggybackAsync(RelayCaller caller, JsonObject args, RelayReply committed)
    {
        if (args["renewClaims"] is not JsonArray && args["ackMessageIds"] is not JsonArray) return committed;
        var conveniences = new JsonObject();
        var authorization = await VerifySessionAsync(caller, args);
        if (args["renewClaims"] is JsonArray claims)
        {
            var outcomes = new JsonArray();
            foreach (var item in claims.Take(RelayLimits.MaxClaimResources))
            {
                var renew = args.DeepClone().AsObject(); renew.Remove("requestId"); renew["resourceId"] = item!["resourceId"]?.DeepClone(); renew["generation"] = item["generation"]?.DeepClone();
                var resolved = authorization.Ok ? await Resolve(caller, renew, Text(renew, "resourceId")) : authorization;
                var result = resolved.Ok ? await Call(AggregateKinds.Lock, Text(Parse(resolved), "resourceId"), caller, RelayTools.ClaimRenew, renew) : resolved;
                outcomes.Add(JsonSerializer.SerializeToNode(new { ok = result.Ok, code = result.Code, data = JsonNode.Parse(result.DataJson) }));
            }
            conveniences["renewals"] = outcomes;
        }
        if (args["ackMessageIds"] is JsonArray ids)
        {
            var ack = args.DeepClone().AsObject(); ack.Remove("requestId"); ack["messageIds"] = ids.DeepClone();
            var result = authorization.Ok ? await CallSession(caller, RelayTools.Ack, ack) : authorization;
            conveniences["ack"] = Outcome(result.Ok, result.Code);
        }
        var data = Parse(committed); data["conveniences"] = conveniences;
        return Ok(data, committed.Version);
    }

    private Task<RelayReply> Resolve(RelayCaller caller, JsonObject args, string resource)
    {
        var resolve = args.DeepClone().AsObject(); resolve["resourceId"] = resource; resolve.Remove("requestId");
        return CallControl(caller, Operations.Resolve, resolve);
    }

    private Task<RelayReply> CallControl(RelayCaller caller, string operation, JsonObject args) => Call(AggregateKinds.Control, AggregateKinds.ControlKey, caller, operation, args);
    private Task<RelayReply> CallSession(RelayCaller caller, string operation, JsonObject args) => Call(AggregateKinds.Session, Text(args, "sessionId"), caller, operation, args);
    private Task<RelayReply> CallBoard(RelayCaller caller, string operation, JsonObject args) => Call(AggregateKinds.Board, BoardKey(args), caller, operation, args);
    private Task<RelayReply> Call(string kind, string key, RelayCaller caller, string operation, JsonObject args) => CoordinationGrain.Aggregate(grains, kind, key).ExecuteAsync(caller, operation, args.ToJsonString());

    private sealed class CallTrace
    {
        public DateTimeOffset At { get; init; }
        public JsonObject? Args { get; set; }
        public JsonObject? Usage { get; set; }
        public string? SessionId { get; set; }
        public string? Role { get; set; }
    }

    /// <summary>Builds and queues the call's activity event. Any failure here is swallowed: telemetry must never fail a call.</summary>
    private void RecordCall(string tool, JsonElement arguments, CallTrace trace, RelayReply? reply, TimeSpan elapsed, bool canceled)
    {
        try
        {
            var args = trace.Args;
            var ok = reply?.Ok == true;
            var repository = args is null ? "" : Text(args, "repositoryId");
            var (target, hash) = args is null || !ok ? (args is null ? null : Target(tool, args), (string?)null) : (Target(tool, args), ContentHash(tool, args));
            var workflow = ok && tool == RelayTools.TaskUpdate && args?["workflow"] is JsonObject flow ? Text(flow, "state") : "";
            activity!.Record(new ActivityEvent
            {
                Timestamp = trace.At, Kind = ActivityEvent.Call, RepositoryId = repository.Length <= RelayLimits.MaxIdentifierLength ? repository : "",
                SessionId = Bounded(trace.SessionId), Role = Bounded(trace.Role?.ToLowerInvariant(), RelayLimits.MaxActivityRoleLength), Tool = tool, Ok = ok,
                Code = reply?.Code ?? (canceled ? "Canceled" : Unavailable),
                RequestBytes = arguments.ValueKind == JsonValueKind.Undefined ? 0 : Encoding.UTF8.GetByteCount(arguments.GetRawText()),
                ReplyBytes = reply is null ? 0 : Encoding.UTF8.GetByteCount(reply.DataJson), DurationMilliseconds = elapsed.TotalMilliseconds,
                // Usage counts only with a committed call: a rejected milestone is usually resent with the same usage.
                InputTokens = ok ? Tokens(trace.Usage, "inputTokens") : null, OutputTokens = ok ? Tokens(trace.Usage, "outputTokens") : null,
                RequestId = Bounded(args is null ? null : Text(args, "requestId")), Target = target, ContentHash = hash,
                WorkflowState = workflow is "ready" or "blocked" or "completed" ? workflow : null,
                OfferTarget = ok && reply is not null && args is not null ? OfferTarget(args, reply) : null
            });
        }
        catch (Exception) { /* Never fail or slow the call for telemetry. */ }
    }

    /// <summary>The record a call wrote or claimed: a task, a context note, or a private conversation. Status repeats are counted per target.</summary>
    private static string? Target(string tool, JsonObject args)
    {
        var taskId = Text(args, "taskId");
        return tool switch
        {
            RelayTools.TaskUpdate or RelayTools.ActionReport or RelayTools.TaskClaim or RelayTools.ActionBegin when taskId.Length > 0 => TaskTarget(args, taskId),
            RelayTools.SendMessage when taskId.Length > 0 => TaskTarget(args, taskId),
            RelayTools.SendMessage when Text(args, "recipientSessionId").Length > 0 => "message:" + Uri.EscapeDataString(Text(args, "sessionId")) + ">" + Uri.EscapeDataString(Text(args, "recipientSessionId")),
            RelayTools.ContextPut when Text(args, "contextId").Length > 0 => "context:" + BoardKey(args) + "/" + Uri.EscapeDataString(Text(args, "contextId")),
            // Only a first page counts as an inbox read; continuation pages belong to the same read.
            RelayTools.CheckInbox when Number(args, "cursor") == 0 => "inbox:" + Uri.EscapeDataString(Text(args, "sessionId")),
            _ => null
        };
    }

    private static string TaskTarget(JsonObject args, string taskId) => "task:" + BoardKey(args) + "/" + Uri.EscapeDataString(taskId);

    /// <summary>A fingerprint of the status text a call wrote: task facts, notes, message and workflow; a message's text; a note's content.</summary>
    private static string? ContentHash(string tool, JsonObject args)
    {
        JsonNode? content = tool switch
        {
            RelayTools.TaskUpdate when args.ContainsKey("patch") || args.ContainsKey("notes") || args.ContainsKey("message") || args.ContainsKey("workflow") =>
                new JsonObject { ["patch"] = args["patch"]?.DeepClone(), ["notes"] = args["notes"]?.DeepClone(), ["message"] = args["message"]?.DeepClone(), ["workflow"] = args["workflow"]?.DeepClone() },
            RelayTools.SendMessage => args["text"]?.DeepClone(),
            RelayTools.ContextPut => args["content"]?.DeepClone(),
            _ => null
        };
        return content is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToJsonString())), 0, 16);
    }

    private static string? OfferTarget(JsonObject args, RelayReply reply)
    {
        if (args["includeNextWork"] is null) return null;
        var offered = JsonNode.Parse(reply.DataJson)?["nextWork"]?["offer"]?["taskId"];
        return offered is JsonValue value && value.TryGetValue<string>(out var taskId) && taskId.Length is > 0 and <= RelayLimits.MaxIdentifierLength ? TaskTarget(args, taskId) : null;
    }

    /// <summary>A reported count, or null when absent or malformed. A bad usage report is ignored rather than failing the milestone.</summary>
    private static long? Tokens(JsonObject? usage, string key) =>
        usage?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), out var tokens) && tokens is >= 0 and <= RelayLimits.MaxReportedTokens ? tokens : null;
    private static string? Bounded(string? value, int maximum = RelayLimits.MaxIdentifierLength) => string.IsNullOrEmpty(value) || value.Length > maximum ? null : value;

    private static string BoardKey(JsonObject args) => Uri.EscapeDataString(Text(args, "repositoryId")) + "/" + Uri.EscapeDataString(Text(args, "boardId", "default"));
    private static JsonObject Parse(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
    private static JsonNode Outcome(bool ok, string code) => JsonSerializer.SerializeToNode(new { ok, code })!;
}

public static class RelayCoreServiceCollectionExtensions
{
    public static IServiceCollection AddPkRelayCore(this IServiceCollection services) =>
        services.AddSingleton<RelayApplication>().AddSingleton<IRelayApplication>(provider => provider.GetRequiredService<RelayApplication>()).AddSingleton<IMachineDirectory, MachineDirectory>();
}

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
/// Claims and action begins that span aggregates: a task claim takes its board claim and then each resource lock, rolling back what it
/// took when a lock is busy; an action begin records the action only while every lock its packet declares is held. The declared resources
/// are resolved only when the board asks (<c>DeclaredResourcesNeeded</c>).
/// </summary>
internal sealed class ClaimComposer(Aggregates aggregates)
{
    public async Task<RelayReply> ResourceClaimAsync(RelayCaller caller, string tool, JsonObject args, JsonObject? session)
    {
        var resolved = await ResolveForSession(caller, args, session, Text(args, "resourceId"));
        if (!resolved.Ok) return resolved;
        return await aggregates.Call(AggregateKinds.Lock, Text(Aggregates.Parse(resolved), "resourceId"), caller, tool, args);
    }

    /// <summary>Resolves a resource name (or alias) by the session's machine, as every claim path must, so one name never means two locks.</summary>
    public Task<RelayReply> ResolveForSession(RelayCaller caller, JsonObject args, JsonObject? session, string resource)
    {
        var lookup = args.DeepClone().AsObject();
        lookup["machineId"] = SessionMachine(args, session);
        return aggregates.Resolve(caller, lookup, resource);
    }

    /// <summary>
    /// Claims the task, then each canonical resource in a stable order. If any resource is busy, the grants this
    /// request newly acquired are released and a newly acquired task claim is rolled back.
    /// </summary>
    public async Task<RelayReply> ClaimTaskAsync(RelayCaller caller, JsonObject args, JsonObject? session)
    {
        // Fields only the hub sets; a client's own values are dropped.
        var canonical = new SortedSet<string>(StringComparer.Ordinal);
        // The claim's own names resolve by the same machine as the declared ones: the verified session's, else the one the call gives.
        var lookup = args.DeepClone().AsObject(); lookup["machineId"] = SessionMachine(args, session);
        foreach (var resource in Strings(args["resourceIds"]))
        {
            var resolved = await aggregates.Resolve(caller, lookup, resource);
            if (!resolved.Ok) return resolved;
            canonical.Add(Text(Aggregates.Parse(resolved), "resourceId"));
        }
        if (!GrantFits(args, canonical)) return Error(PayloadTooLarge);
        var claimArgs = args.DeepClone().AsObject();
        claimArgs[ServerArguments.RequestedResources] = args["resourceIds"]?.DeepClone() ?? new JsonArray();
        claimArgs["resourceIds"] = JsonSerializer.SerializeToNode(canonical);
        // The board refuses a claim that leaves out a resource the packet declares. Only a task that declares some sends this hub back to
        // resolve them (aliases by the same machine as the claim's own names), so a task without any costs no extra read.
        claimArgs[Operations.ResolveDeclaredField] = true;
        var task = await aggregates.Board(caller, RelayTools.TaskClaim, claimArgs);
        if (task.Code == DeclaredResourcesNeeded)
        {
            claimArgs.Remove(Operations.ResolveDeclaredField);
            if (await ResolveDeclaredAsync(caller, args, session, Strings(Aggregates.Parse(task)["declared"])) is { } declared) claimArgs["declaredResources"] = declared;
            task = await aggregates.Board(caller, RelayTools.TaskClaim, claimArgs);
        }
        if (!task.Ok) return task;
        var acquired = new List<(string Resource, long Generation)>();
        var held = new List<(string Resource, string ClaimFor)>();
        var grants = new JsonArray();
        foreach (var resource in canonical)
        {
            var resourceArgs = args.DeepClone().AsObject(); resourceArgs["requestId"] = Text(args, "requestId") + ":resource:" + resource;
            // The lock records which task claim its grant serves, so a standing order's release cannot drop a later claim.
            resourceArgs["taskClaimGeneration"] = Aggregates.Parse(task)["claimGeneration"]?.DeepClone();
            RelayReply claim;
            // A lock call that throws (a timeout, a storage fault) still undoes what this claim took, so nothing stays held for its lease.
            try { claim = await aggregates.Call(AggregateKinds.Lock, resource, caller, RelayTools.ClaimAcquire, resourceArgs); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The lock may have granted before its reply was lost; that grant is undone with the rest.
                if (await GrantAfterLostReplyAsync(caller, resource, resourceArgs) is { } granted) Record(resource, Aggregates.Parse(granted), acquired, held);
                await RollBackClaimAsync(caller, args, acquired, held, task);
                return Error(Unavailable, new { resourceId = resource, rollback = "new_grants_released" });
            }
            if (!claim.Ok)
            {
                await RollBackClaimAsync(caller, args, acquired, held, task);
                // Busy means another session holds it; any other refusal (a storage fault) keeps its own code so the agent knows to retry.
                return Error(claim.Code, new { resourceId = resource, detail = JsonNode.Parse(claim.DataJson), rollback = "new_grants_released" });
            }
            var grant = Aggregates.Parse(claim);
            Record(resource, grant, acquired, held);
            grant.Remove("previousClaimFor");
            grant["resourceId"] = resource; grants.Add(grant);
        }
        var data = Aggregates.Parse(task); data["grants"] = grants; data["complete"] = true;
        return Ok(data, task.Version);
    }

    /// <summary>A grant this claim took (released on rollback) or one the session already held (given back to the claim it served).</summary>
    private static void Record(string resource, JsonObject grant, List<(string Resource, long Generation)> acquired, List<(string Resource, string ClaimFor)> held)
    {
        if (grant["newlyAcquired"]?.GetValue<bool>() == true) acquired.Add((resource, Number(grant, "generation")));
        else held.Add((resource, Text(grant, "previousClaimFor")));
    }

    /// <summary>
    /// After a lock call threw, the grant it may have made: the same request again replays the stored grant when the first one did land
    /// (a timeout after the lock's write), or takes it now, so either way the rollback knows its generation and undoes it. Null when the
    /// lock refuses or throws again; a grant it made then lapses with its lease.
    /// </summary>
    private async Task<RelayReply?> GrantAfterLostReplyAsync(RelayCaller caller, string resource, JsonObject resourceArgs)
    {
        try { return await aggregates.Call(AggregateKinds.Lock, resource, caller, RelayTools.ClaimAcquire, resourceArgs) is { Ok: true } replayed ? replayed : null; }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
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

    /// <remarks>Each undo runs on its own: one that fails (its lease simply lapses then) never skips the rest, the task's own unclaim included.</remarks>
    private async Task RollBackClaimAsync(RelayCaller caller, JsonObject args, List<(string Resource, long Generation)> acquired, List<(string Resource, string ClaimFor)> held, RelayReply task)
    {
        static async Task Attempt(Func<Task<RelayReply>> undo)
        {
            try { await undo(); }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
        }
        // A grant the session already held keeps it, serving the claim it served before.
        foreach (var (resource, claimFor) in held)
        {
            var restore = args.DeepClone().AsObject(); restore.Remove("requestId"); restore[Operations.RestoreClaimForField] = claimFor;
            restore[Operations.ForgetRequestField] = Text(args, "requestId") + ":resource:" + resource;
            await Attempt(() => aggregates.Call(AggregateKinds.Lock, resource, caller, RelayTools.ClaimAcquire, restore));
        }
        foreach (var grant in acquired)
        {
            var release = args.DeepClone().AsObject(); release["generation"] = grant.Generation; release.Remove("requestId");
            release[Operations.ForgetRequestField] = Text(args, "requestId") + ":resource:" + grant.Resource;
            await Attempt(() => aggregates.Call(AggregateKinds.Lock, grant.Resource, caller, RelayTools.ClaimRelease, release));
        }
        var claimed = Aggregates.Parse(task);
        if (claimed["newlyAcquired"]?.GetValue<bool>() == true)
        {
            var release = args.DeepClone().AsObject(); release["claimGeneration"] = claimed["claimGeneration"]?.DeepClone(); release["claimRequestId"] = Text(args, "requestId"); release.Remove("requestId");
            await Attempt(() => aggregates.Board(caller, Operations.TaskUnclaim, release));
        }
    }

    /// <summary>
    /// Records the action with the session's verified binding. A first (replayable) attempt must also block its
    /// resources before the agent may invoke it; if that cannot be confirmed, the agent is told not to proceed.
    /// </summary>
    public async Task<RelayReply> BeginActionAsync(RelayCaller caller, JsonObject args, JsonObject? session)
    {
        args["bindingVersion"] = session?["bindingVersion"]?.DeepClone(); args["binding"] = session?["binding"]?.DeepClone();
        // The board answers a retry or an existing action at once. Only a new action on a task that declares resources sends this hub back:
        // nothing starts while one of them is held by another session, or not held by this one, and the board records nothing then.
        var beginArgs = args.DeepClone().AsObject(); beginArgs[Operations.ResolveDeclaredField] = true;
        var result = await aggregates.Board(caller, RelayTools.ActionBegin, beginArgs);
        if (result.Code == DeclaredResourcesNeeded)
        {
            beginArgs.Remove(Operations.ResolveDeclaredField);
            // Without the lookup the locks cannot be checked, so nothing is recorded: the agent retries.
            if (await ResolveDeclaredAsync(caller, args, session, Strings(Aggregates.Parse(result)["declared"])) is not { } declared)
                return Error(Unavailable, new { replayAllowed = false });
            {
                beginArgs["declaredResources"] = declared;
                var lookup = new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId") };
                foreach (var resource in declared.Select(entry => entry.Value is JsonValue value && value.TryGetValue<string>(out var id) ? id : "").Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    var state = await aggregates.Call(AggregateKinds.Lock, resource, caller, Operations.ClaimState, lookup);
                    if (!state.Ok) return state;
                    if (Aggregates.Parse(state)["held"]?.GetValue<bool>() == true) continue;
                    beginArgs[Operations.LockRefusalField] = new JsonObject { ["code"] = Aggregates.Parse(state)["busy"]?.GetValue<bool>() == true ? Busy : ClaimRequired, ["resourceId"] = resource };
                    break;
                }
            }
            result = await aggregates.Board(caller, RelayTools.ActionBegin, beginArgs);
        }
        if (!result.Ok || Aggregates.Parse(result)["replayAllowed"]?.GetValue<bool>() != true) return result;
        var actionId = Text(args, "actionId");
        var attempt = Number(Aggregates.Parse(result)["action"]!.AsObject(), "attempt", 1);
        // The hub blocks each lock itself rather than having the board deliver: a board waiting on a lock while that lock's outbox waits on
        // the board would stall both until the call timeout. Blocking is idempotent, so the board's own queued blocks are harmless.
        var actionKey = CoordinationStateMachine.ActionKey(args, actionId, attempt);
        var resources = Strings(Aggregates.Parse(result)["action"]!["resourceIds"]).ToArray();
        // The same messages the board queues (ActionBlock, ActionUnblock), sent directly.
        var (repository, sessionId) = (Text(args, "repositoryId"), Text(args, "sessionId"));
        foreach (var resource in resources)
        {
            RelayReply block;
            // A block call that throws may still have blocked the lock: it is lifted below with the others.
            try { block = await aggregates.Call(ActionBlock.TargetKind, resource, caller, ActionBlock.Operation, new ActionBlock(repository, actionId, actionKey, sessionId).ToJson()); }
            catch (Exception exception) when (exception is not OperationCanceledException) { block = Error(Unavailable); }
            if (block.Ok) continue;
            // The action is recorded but never ran. Each lock remembers it as resolved, which lifts the blocks this call placed and turns the
            // board's own queued blocks into no-ops, so no lock stays blocked by it. The action stays open until reported (canceled).
            foreach (var placed in resources)
                try { await aggregates.Call(ActionUnblock.TargetKind, placed, caller, ActionUnblock.Operation, new ActionUnblock(repository, actionId, actionKey, sessionId).ToJson()); }
                catch (Exception exception) when (exception is not OperationCanceledException) { }
            return Error(ActionPending, new { actionId, replayAllowed = false, reason = block.Code, resourceId = resource, next = "report this action with outcome canceled, then retry" });
        }
        var current = await aggregates.Board(caller, RelayTools.TaskGet, args);
        if (!current.Ok || current.Version != result.Version) return Error(ActionChanged, new { actionId, replayAllowed = false });
        return result;
    }

    /// <summary>
    /// The task's declared <c>packet.resources</c> (as the board named them in <c>DeclaredResourcesNeeded</c>), each resolved to its canonical
    /// resource as a claim resolves its own (aliases by the session's machine), with <c>""</c> for a name no resource answers to; null when
    /// the lookup fails (the board then checks each name as listed, which only refuses more).
    /// </summary>
    private async Task<JsonObject?> ResolveDeclaredAsync(RelayCaller caller, JsonObject args, JsonObject? session, IEnumerable<string> listed)
    {
        var names = listed.Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return null;
        var resolved = await aggregates.Control(caller, Operations.ResolveAll, new JsonObject
        {
            ["repositoryId"] = Text(args, "repositoryId"), ["machineId"] = SessionMachine(args, session),
            ["resourceIds"] = JsonSerializer.SerializeToNode(names)
        });
        // Without the lookup the board checks each declared name as listed, which only refuses more.
        if (!resolved.Ok) return null;
        var map = Aggregates.Parse(resolved)["resolved"] as JsonObject;
        var declared = new JsonObject();
        foreach (var name in names) declared[name] = map?[name] is JsonValue canonical && canonical.TryGetValue<string>(out var id) ? id : "";
        return declared;
    }

    /// <summary>The machine a session's resource names resolve by: the one it gave at <c>session_open</c>, else the one this call gives.</summary>
    private static string SessionMachine(JsonObject args, JsonObject? session) =>
        session is not null && Text(session, "machineId").Length > 0 ? Text(session, "machineId") : Text(args, "machineId");
}

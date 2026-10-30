using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace PkRelay.Core;

public sealed class RelayApplication(IGrainFactory grains) : IRelayApplication
{
    private readonly string incarnation = Guid.NewGuid().ToString("N");
    private static readonly HashSet<string> Tools = ["session_open", "session_bind", "worker_list", "get_status", "task_get", "task_update", "task_claim", "claim_acquire", "claim_renew", "claim_release", "action_begin", "action_report", "send_message", "check_inbox", "ack", "context_get", "context_put", "control_request"];

    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Tools.Contains(tool)) return Error("UnknownTool");
        if (arguments.ValueKind != JsonValueKind.Object) return Error("InvalidArguments");
        if (Encoding.UTF8.GetByteCount(arguments.GetRawText()) > CoordinationStateMachine.MaxPayloadBytes) return Error("PayloadTooLarge");
        var args = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        if (args.ContainsKey("includeNextWork") && (args["includeNextWork"] is not JsonValue include || !include.TryGetValue<bool>(out _))) return Error("InvalidArguments");
        args["hubIncarnation"] = incarnation;
        var repository = Text(args, "repositoryId");
        if (repository.Length == 0 || (!caller.IsAdministrator && !caller.Repositories.Contains(repository, StringComparer.Ordinal))) return Error("RepositoryDenied");
        if (CoordinationStateMachine.IsMutation(tool) && Text(args, "requestId").Length == 0) return Error("RequestIdRequired");
        if (Text(args, "requestId").Length > 256 || repository.Length > 256 || Text(args, "boardId").Length > 256) return Error("InvalidArguments");
        if (Text(args, "machineId").Length > 256 || Text(args, "label").Length > 256) return Error("InvalidArguments");
        if (Text(args, "taskId").Length > 256 || Text(args, "resourceId").Length > 256 || (args["resourceIds"] as JsonArray ?? new()).Any(x => x!.GetValue<string>().Length > 256)) return Error("InvalidArguments");
        if (args["resourceIds"] is JsonArray resources && resources.Count > 16 || args["messageIds"] is JsonArray messages && messages.Count > 50 || args["ackMessageIds"] is JsonArray acknowledgments && acknowledgments.Count > 50) return Error("PayloadTooLarge");
        var trusted = caller with { IsInternal = true };
        try
        {
            if (tool == "session_open")
            {
                var opened = await Call("control", "hub", trusted, tool, args);
                if (!opened.Ok) return opened;
                var data = Parse(opened); var initialize = args.DeepClone().AsObject();
                initialize["sessionId"] = Text(data, "sessionId"); initialize["sessionToken"] = Text(data, "sessionToken"); initialize["principalId"] = caller.PrincipalId; initialize.Remove("requestId");
                var initialized = await Call("session", Text(data, "sessionId"), trusted, "__open", initialize);
                if (!initialized.Ok) return initialized;
                // The queued initialization remains a retryable durable effect and deduplicates at its recipient.
                if (Text(args, "taskId").Length > 0)
                {
                    var packet = await Call("board", BoardKey(args), trusted, "task_get", args);
                    data["taskPacket"] = packet.Ok ? JsonNode.Parse(packet.DataJson) : JsonSerializer.SerializeToNode(new { code = packet.Code });
                }
                return Bound(CoordinationStateMachine.Ok(data, opened.Version));
            }
            var needsSession = tool is not ("worker_list" or "task_get" or "context_get" or "control_request") && !(tool == "get_status" && Text(args, "sessionId").Length == 0);
            JsonObject? sessionStatus = null;
            if (needsSession)
            {
                if (Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0) return Error("SessionDenied");
                var authorized = await Call("session", Text(args, "sessionId"), trusted, "__verify", args);
                if (!authorized.Ok) return authorized;
                sessionStatus = Parse(authorized);
            }
            if (tool is "task_claim" or "claim_acquire" or "action_begin")
            {
                var status = await Call("control", "hub", trusted, "get_status", args);
                if (!status.Ok) return status;
                if (Parse(status)["paused"]?.GetValue<bool>() == true) return Error("Paused");
            }
            RelayReply result;
            if (tool == "task_claim") result = await ClaimTaskAsync(trusted, args);
            else if (tool is "claim_renew" or "claim_release" && Text(args, "taskId").Length > 0 && Text(args, "resourceId").Length == 0) result = await Call("board", BoardKey(args), trusted, tool, args);
            else if (tool.StartsWith("claim_", StringComparison.Ordinal))
            {
                var resolved = await Resolve(trusted, args, Text(args, "resourceId"));
                if (!resolved.Ok) return resolved;
                result = await Call("lock", Text(Parse(resolved), "resourceId"), trusted, tool, args);
            }
            else if (tool == "action_begin")
            {
                args["bindingVersion"] = sessionStatus?["bindingVersion"]?.DeepClone(); args["binding"] = sessionStatus?["binding"]?.DeepClone();
                result = await BeginActionAsync(trusted, args);
            }
            else
            {
                var kind = tool switch
                {
                    "session_bind" or "check_inbox" or "ack" => "session",
                    "send_message" => Text(args, "taskId").Length > 0 ? "board" : "session",
                    "worker_list" or "control_request" => "control",
                    "get_status" => Text(args, "sessionId").Length > 0 ? "session" : "control",
                    _ => "board"
                };
                var key = kind == "session" ? Text(args, "sessionId") : kind == "board" ? BoardKey(args) : "hub";
                if (tool == "send_message" && Text(args, "recipientSessionId").Length > 0)
                {
                    var verify = await Call("control", "hub", trusted, "worker_list", new JsonObject { ["repositoryId"] = repository, ["limit"] = 50 });
                    // Delivery itself enforces repository identity; unknown recipients stay visibly pending.
                    if (!verify.Ok) return verify;
                }
                result = await Call(kind, key, trusted, tool, args);
                if (tool == "get_status" && kind == "session" && result.Ok)
                {
                    var control = await Call("control", "hub", trusted, "get_status", args);
                    if (!control.Ok) return control;
                    var data = Parse(result); data["control"] = JsonNode.Parse(control.DataJson); result = CoordinationStateMachine.Ok(data, result.Version);
                }
            }
            if (result.Ok) result = await PiggybackAsync(trusted, args, result);
            if (result.Ok && tool == "task_update" && args["includeNextWork"]?.GetValue<bool>() == true) result = await WithNextWorkAsync(trusted, args, result);
            return Bound(result);
        }
        catch (ArgumentException) { return Error("InvalidArguments"); }
        catch (InvalidOperationException) { return Error("InvalidArguments"); }
        catch (JsonException) { return Error("InvalidArguments"); }
    }

    public async Task DispatchDueAsync(DueWorkReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await Call(reference.GrainType, reference.GrainKey, new("relay-dispatcher", false, [], true), "__dispatch", new() { ["workId"] = reference.WorkId });
        if (!reply.Ok) throw new InvalidOperationException("Durable dispatch remains pending: " + reply.Code);
    }

    private async Task<RelayReply> ClaimTaskAsync(RelayCaller caller, JsonObject args)
    {
        var canonical = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var resource in (args["resourceIds"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()))
        {
            var resolved = await Resolve(caller, args, resource);
            if (!resolved.Ok) return resolved;
            canonical.Add(Text(Parse(resolved), "resourceId"));
        }
        var claimArgs = args.DeepClone().AsObject(); claimArgs["resourceIds"] = JsonSerializer.SerializeToNode(canonical);
        // The complete grant response is authorization material and cannot be truncated after
        // acquiring ownership. Reserve its worst-case metadata before changing any aggregate.
        var grantPreview = canonical.Select(resource => new { resourceId = resource, holder = Text(args, "sessionId"), generation = long.MaxValue, leaseUntil = DateTimeOffset.MaxValue.ToString("O"), newlyAcquired = false }).ToArray();
        var responsePreview = CoordinationStateMachine.Ok(new { taskId = Text(args, "taskId"), newlyAcquired = false, claimGeneration = long.MaxValue, leaseUntil = DateTimeOffset.MaxValue.ToString("O"), resourceIds = canonical, grants = grantPreview, complete = true }, long.MaxValue);
        if (!CoordinationStateMachine.FitsWire(responsePreview, CoordinationStateMachine.MaxPayloadBytes - 8192)) return Error("PayloadTooLarge");
        var task = await Call("board", BoardKey(args), caller, "task_claim", claimArgs);
        if (!task.Ok) return task;
        var acquired = new List<(string Resource, long Generation)>(); var all = new JsonArray();
        foreach (var resource in canonical)
        {
            var resourceArgs = args.DeepClone().AsObject(); resourceArgs["requestId"] = Text(args, "requestId") + ":resource:" + resource;
            var claim = await Call("lock", resource, caller, "claim_acquire", resourceArgs);
            if (!claim.Ok)
            {
                foreach (var grant in acquired)
                {
                    var release = args.DeepClone().AsObject(); release["generation"] = grant.Generation; release.Remove("requestId");
                    await Call("lock", grant.Resource, caller, "claim_release", release);
                }
                if (Parse(task)["newlyAcquired"]?.GetValue<bool>() == true)
                {
                    var release = args.DeepClone().AsObject(); release["claimGeneration"] = Parse(task)["claimGeneration"]?.DeepClone(); release["claimRequestId"] = Text(args, "requestId"); release.Remove("requestId");
                    await Call("board", BoardKey(args), caller, "__task_unclaim", release);
                }
                return Error("Busy", new { resourceId = resource, detail = JsonNode.Parse(claim.DataJson), rollback = "new_grants_released" });
            }
            var grantData = Parse(claim); if (grantData["newlyAcquired"]?.GetValue<bool>() == true) acquired.Add((resource, CoordinationStateMachine.Number(grantData, "generation")));
            grantData["resourceId"] = resource; all.Add(grantData);
        }
        var data = Parse(task); data["grants"] = all; data["complete"] = true;
        return CoordinationStateMachine.Ok(data, task.Version);
    }

    private async Task<RelayReply> BeginActionAsync(RelayCaller caller, JsonObject args)
    {
        var result = await Call("board", BoardKey(args), caller, "action_begin", args);
        if (result.Ok && Parse(result)["replayAllowed"]?.GetValue<bool>() == true)
        {
            var attempt = CoordinationStateMachine.Number(Parse(result)["action"]!.AsObject(), "attempt", 1);
            var delivery = await Call("board", BoardKey(args), caller, "__dispatch", new() { ["operation"] = "__block", ["actionKey"] = CoordinationStateMachine.ActionKey(args, Text(args, "actionId"), attempt) });
            if (!delivery.Ok) return Error("ActionPending", new { actionId = Text(args, "actionId"), replayAllowed = false, reason = delivery.Code });
            var current = await Call("board", BoardKey(args), caller, "task_get", args);
            if (!current.Ok || current.Version != result.Version) return Error("ActionChanged", new { actionId = Text(args, "actionId"), replayAllowed = false });
        }
        return result;
    }

    private async Task<RelayReply> WithNextWorkAsync(RelayCaller caller, JsonObject args, RelayReply committed)
    {
        JsonNode next;
        try
        {
            var status = await Call("control", "hub", caller, "get_status", args);
            if (!status.Ok) next = JsonSerializer.SerializeToNode(new { ok = false, code = status.Code })!;
            else if (Parse(status)["paused"]?.GetValue<bool>() == true) next = JsonSerializer.SerializeToNode(new { ok = true, code = "Paused" })!;
            else
            {
                var read = args.DeepClone().AsObject(); read.Remove("requestId");
                var offered = await Call("board", BoardKey(args), caller, "__next_work", read);
                next = offered.Ok ? JsonNode.Parse(offered.DataJson)! : JsonSerializer.SerializeToNode(new { ok = false, code = offered.Code })!;
            }
        }
        catch (Exception) { next = JsonSerializer.SerializeToNode(new { ok = false, code = "Unavailable" })!; }
        var data = Parse(committed); data["nextWork"] = next;
        return CoordinationStateMachine.Ok(data, committed.Version);
    }

    private async Task<RelayReply> PiggybackAsync(RelayCaller caller, JsonObject args, RelayReply committed)
    {
        if (args["renewClaims"] is not JsonArray renewals && args["ackMessageIds"] is not JsonArray) return committed;
        var conveniences = new JsonObject();
        var authorization = Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0
            ? Error("SessionDenied")
            : await Call("session", Text(args, "sessionId"), caller, "__verify", args);
        if (args["renewClaims"] is JsonArray claims)
        {
            var outcomes = new JsonArray();
            foreach (var item in claims.Take(16))
            {
                var renew = args.DeepClone().AsObject(); renew.Remove("requestId"); renew["resourceId"] = item!["resourceId"]?.DeepClone(); renew["generation"] = item["generation"]?.DeepClone();
                var resolved = authorization.Ok ? await Resolve(caller, renew, Text(renew, "resourceId")) : authorization;
                var result = resolved.Ok ? await Call("lock", Text(Parse(resolved), "resourceId"), caller, "claim_renew", renew) : resolved;
                outcomes.Add(JsonSerializer.SerializeToNode(new { ok = result.Ok, code = result.Code, data = JsonNode.Parse(result.DataJson) }));
            }
            conveniences["renewals"] = outcomes;
        }
        if (args["ackMessageIds"] is JsonArray ids)
        {
            var ack = args.DeepClone().AsObject(); ack.Remove("requestId"); ack["messageIds"] = ids.DeepClone();
            var result = authorization.Ok ? await Call("session", Text(args, "sessionId"), caller, "ack", ack) : authorization;
            conveniences["ack"] = JsonSerializer.SerializeToNode(new { ok = result.Ok, code = result.Code });
        }
        var data = Parse(committed); data["conveniences"] = conveniences;
        return CoordinationStateMachine.Ok(data, committed.Version);
    }

    private Task<RelayReply> Resolve(RelayCaller caller, JsonObject args, string resource)
    {
        var resolve = args.DeepClone().AsObject(); resolve["resourceId"] = resource; resolve.Remove("requestId");
        return Call("control", "hub", caller, "__resolve", resolve);
    }
    private Task<RelayReply> Call(string kind, string key, RelayCaller caller, string operation, JsonObject args) => CoordinationGrain.Aggregate(grains, kind, key).ExecuteAsync(caller, operation, args.ToJsonString());
    private static string BoardKey(JsonObject args) => Uri.EscapeDataString(Text(args, "repositoryId")) + "/" + Uri.EscapeDataString(Text(args, "boardId", "default"));
    private static JsonObject Parse(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
    private static string Text(JsonObject node, string key, string fallback = "") => CoordinationStateMachine.Text(node, key, fallback);
    private static RelayReply Error(string code, object? data = null) => CoordinationStateMachine.Error(code, data);
    private static RelayReply Bound(RelayReply result) => CoordinationStateMachine.BoundWire(result);
}

public static class RelayCoreServiceCollectionExtensions
{
    public static IServiceCollection AddPkRelayCore(this IServiceCollection services) => services.AddSingleton<RelayApplication>().AddSingleton<IRelayApplication>(provider => provider.GetRequiredService<RelayApplication>());
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PkRelay.Core;

public static class CoordinationStateMachine
{
    public const int MaxPayloadBytes = 32_768;
    public const int MaxPageSize = 50;
    public const int MaxRecords = 2_048;
    public const int MaxTaskBytes = 262_144;
    public const int MaxReportBytes = 8_192;
    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, string kind, string key, RelayCaller caller, string operation, JsonObject arguments, DateTimeOffset now)
    {
        if (Encoding.UTF8.GetByteCount(arguments.ToJsonString()) > MaxPayloadBytes) return Error("PayloadTooLarge");
        var repository = Text(arguments, "repositoryId");
        if (repository.Length == 0 || (!caller.IsAdministrator && !caller.Repositories.Contains(repository, StringComparer.Ordinal))) return Error("RepositoryDenied");
        if (operation.StartsWith("__", StringComparison.Ordinal) && !caller.IsInternal) return Error("InternalOnly");
        var state = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        if (operation is "session_open" or "session_bind" && Encoding.UTF8.GetByteCount(arguments["binding"]?.ToJsonString() ?? "null") > 8_192) return Error("PayloadTooLarge");
        if (kind != "control" && Text(state, "repositoryId") is { Length: > 0 } existing && existing != repository) return Error("RepositoryDenied");
        if (kind != "control") state["repositoryId"] = repository;
        var request = Text(arguments, "requestId");
        var dedupKey = caller.PrincipalId + ":" + operation + ":" + request;
        var requests = Object(state, "requests");
        var fingerprintArgs = arguments.DeepClone().AsObject(); fingerprintArgs.Remove("hubIncarnation");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintArgs.ToJsonString())));
        if (request.Length > 0 && requests[dedupKey] is JsonObject prior)
        {
            if (Text(prior, "fingerprint") != fingerprint) return Error("RequestConflict");
            if (operation == "action_begin" && Object(state, "tasks")[Text(arguments, "taskId")] is JsonObject actionTask && Object(actionTask, "actions")[Text(arguments, "actionId")] is JsonObject currentAction)
                return Ok(new { action = currentAction, replayAllowed = false, existing = true }, Number(actionTask, "version"));
            if (operation is "claim_acquire" or "claim_renew" or "task_claim")
            {
                var grant = JsonNode.Parse(Text(prior, "json"))!.AsObject();
                if (Date(grant, "leaseUntil") <= now) return Error("StaleClaim");
                if (kind == "lock" && (Text(state, "holder") != Text(arguments, "sessionId") || Number(state, "generation") != Number(grant, "generation"))) return Error("StaleClaim");
                if (kind == "board" && Object(state, "tasks")[Text(arguments, "taskId")] is JsonObject task && (Text(task, "owner") != Text(arguments, "sessionId") || Number(task, "claimGeneration") != Number(grant, "claimGeneration"))) return Error("StaleClaim");
            }
            return new(true, Text(prior, "code"), Text(prior, "json"), Number(prior, "version"));
        }
        var effects = new List<DurableEffect>();
        RelayReply result;
        try
        {
            result = kind switch
            {
                "control" => Control(state, caller, operation, arguments, now, effects),
                "session" => Session(state, caller, operation, arguments, now, effects),
                "board" => Board(state, caller, operation, arguments, now, effects),
                "lock" => Lock(state, caller, operation, arguments, now),
                _ => Error("UnknownAggregate")
            };
        }
        catch (ArgumentException exception) { return Error("InvalidArguments", new { error = exception.Message }); }
        catch (InvalidOperationException) { return Error("InvalidArguments"); }
        catch (FormatException) { return Error("InvalidArguments"); }
        if (!result.Ok) return result;
        if (kind == "board" && state["tasks"] is JsonObject taskMap && taskMap.Any(task => Encoding.UTF8.GetByteCount(task.Value!.ToJsonString()) > MaxTaskBytes)) return Error("TaskCapacityExceeded");
        if (kind == "board" && !FitsWire(result, MaxPayloadBytes - 8192) && operation == "task_get" && Object(state, "tasks")[Text(arguments, "taskId")] is JsonObject largeTask && Text(arguments, "section").Length == 0) result = Ok(TaskSummary(largeTask), result.Version);
        // Leave room for application composition (session attachments and convenience outcomes).
        result = BoundWire(result, MaxPayloadBytes - 8192);
        // Reject before committing if essential authorization or fencing metadata alone cannot fit.
        if (!FitsWire(result, MaxPayloadBytes - 8192)) return Error("PayloadTooLarge");
        if (envelope.Effects.Count + effects.Count > MaxRecords) return Error("CapacityExceeded");
        if (request.Length > 0 && IsMutation(operation))
        {
            if (requests.Count >= MaxRecords) return Error("CapacityExceeded");
            requests[dedupKey] = new JsonObject { ["fingerprint"] = fingerprint, ["code"] = result.Code, ["json"] = result.DataJson, ["version"] = result.Version };
        }
        envelope.Value = new() { Json = state.ToJsonString() };
        envelope.Effects.AddRange(effects);
        return result;
    }

    private static RelayReply Control(JsonObject state, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now, List<DurableEffect> effects)
    {
        var repository = Required(args, "repositoryId");
        var sessions = Object(state, "sessions");
        if (operation == "session_open")
        {
            Required(args, "requestId");
            if (sessions.Count >= MaxRecords) return Error("CapacityExceeded");
            var id = Guid.NewGuid().ToString("N");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var session = new JsonObject { ["sessionId"] = id, ["principalId"] = caller.PrincipalId, ["repositoryId"] = repository, ["sessionToken"] = token, ["bindingVersion"] = 0, ["machineId"] = Text(args, "machineId"), ["label"] = Text(args, "label"), ["binding"] = args["binding"]?.DeepClone(), ["observedAt"] = now.ToString("O"), ["freshUntil"] = now.AddMinutes(2).ToString("O"), ["availability"] = "unknown" };
            session["hubIncarnation"] = Text(args, "hubIncarnation");
            sessions[id] = session;
            Effect(effects, "session", id, "__open", session, now);
            return Ok(new { sessionId = id, sessionToken = token, bindingVersion = 0, binding = args["binding"] });
        }
        if (operation == "__register")
        {
            var id = Required(args, "sessionId");
            if (sessions[id] is not JsonObject session || Text(session, "repositoryId") != repository) return Error("SessionDenied");
            foreach (var property in new[] { "binding", "bindingVersion", "observedAt", "freshUntil", "availability", "machineId", "label", "hubIncarnation" })
                if (args.ContainsKey(property)) session[property] = args[property]?.DeepClone();
            return Ok(new { registered = true });
        }
        if (operation == "worker_list")
        {
            var workers = sessions.Select(x => x.Value!.AsObject()).Where(x => Text(x, "repositoryId") == repository).Select(x =>
            {
                var copy = x.DeepClone().AsObject(); copy.Remove("sessionToken"); copy.Remove("principalId");
                var stale = Date(copy, "freshUntil") <= now || Text(copy, "hubIncarnation") != Text(args, "hubIncarnation");
                copy["stale"] = stale; copy.Remove("hubIncarnation");
                if (stale) copy["availability"] = "unknown";
                return (JsonNode)copy;
            }).ToArray();
            var page = Page(workers, args); var data = JsonNode.Parse(page.DataJson)!.AsObject();
            var machines = workers.GroupBy(x => Text(x.AsObject(), "machineId")).Where(x => x.Key.Length > 0).Select(group => new { machineId = group.Key, sessionCount = group.Count(), availability = group.Any(x => Text(x.AsObject(), "availability") == "active") ? "active" : "unknown" }).ToArray();
            data["machines"] = JsonSerializer.SerializeToNode(machines.Take(50)); data["machineSummariesTruncated"] = machines.Length > 50;
            var machinePage = data["machines"]!.AsArray();
            while (Encoding.UTF8.GetByteCount(data.ToJsonString()) > MaxPayloadBytes && machinePage.Count > 0) { machinePage.RemoveAt(machinePage.Count - 1); data["machineSummariesTruncated"] = true; }
            return Ok(data);
        }
        if (operation == "get_status")
        {
            var sessionId = Text(args, "sessionId"); var cancellationRequests = Object(state, "cancellations");
            return Ok(new { paused = Flag(state, "paused"), cancellations = cancellationRequests.Count, requests = sessionId.Length > 0 ? cancellationRequests.Where(x => x.Key.StartsWith(sessionId + ":", StringComparison.Ordinal)).Take(50).Select(x => new { target = x.Key, request = x.Value }).ToArray() : [] });
        }
        if (operation == "__resolve")
        {
            var resources = Object(state, "resources"); var id = Required(args, "resourceId");
            var resolved = resources[id] as JsonObject;
            if (resolved is null && Object(state, "aliases")[Text(args, "machineId").ToUpperInvariant() + ":" + id.ToUpperInvariant()] is JsonValue alias) resolved = resources[alias.GetValue<string>()] as JsonObject;
            return resolved is null || Text(resolved, "repositoryId") != repository ? Error("ResourceUnknown") : Ok(resolved);
        }
        if (operation == "control_request")
        {
            if (!caller.IsAdministrator) return Error("AdministratorRequired");
            var command = Required(args, "command");
            if (command == "register_resource")
            {
                var id = Required(args, "resourceId"); var machine = Required(args, "machineId");
                var resources = Object(state, "resources"); var aliases = Object(state, "aliases");
                var names = (args["aliases"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()).Append(id).ToArray();
                var canonical = id;
                foreach (var name in names)
                    if (aliases[machine.ToUpperInvariant() + ":" + name.ToUpperInvariant()] is JsonValue prior) canonical = prior.GetValue<string>();
                if (resources[canonical] is JsonObject existing && Text(existing, "repositoryId") != repository) return Error("RepositoryDenied");
                foreach (var name in names)
                {
                    var aliasKey = machine.ToUpperInvariant() + ":" + name.ToUpperInvariant();
                    if (aliases[aliasKey] is JsonValue other && other.GetValue<string>() != canonical) return Error("AliasConflict");
                    aliases[aliasKey] = canonical;
                }
                resources[canonical] = new JsonObject { ["resourceId"] = canonical, ["repositoryId"] = repository, ["machineId"] = machine, ["aliases"] = JsonSerializer.SerializeToNode(names) };
                return Ok(new { resourceId = canonical });
            }
            if (command is "pause" or "stop") state["paused"] = true;
            else if (command == "resume") state["paused"] = false;
            else if (command != "cancel") return Error("UnknownCommand");
            if (command is "stop" or "cancel") Object(state, "cancellations")[Text(args, "sessionId") + ":" + Text(args, "actionId")] = JsonSerializer.SerializeToNode(new { requestedBy = caller.PrincipalId, at = now, status = "requested_unknown" });
            return Ok(new { paused = Flag(state, "paused"), cancellationRequested = command is "stop" or "cancel" });
        }
        return Error("UnknownTool");
    }

    private static RelayReply Session(JsonObject state, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now, List<DurableEffect> effects)
    {
        if (operation == "__open")
        {
            if (state.ContainsKey("principalId")) return Text(state, "principalId") == Text(args, "principalId") && Text(state, "sessionToken") == Text(args, "sessionToken") ? Ok(new { initialized = true }) : Error("SessionDenied");
            foreach (var property in args) if (property.Key != "requestId") state[property.Key] = property.Value?.DeepClone();
            return Ok(new { initialized = true });
        }
        if (operation == "__deliver")
        {
            if (!state.ContainsKey("principalId")) return Error("SessionUnknown");
            var id = Required(args, "messageId"); var inbox = Object(state, "inbox");
            if (inbox.ContainsKey(id) || Object(state, "acked").ContainsKey(id)) return Ok(new { duplicate = true });
            if (inbox.Count >= MaxRecords) return Error("CapacityExceeded");
            inbox[id] = args.DeepClone(); return Ok(new { delivered = true });
        }
        if (Text(state, "principalId") != caller.PrincipalId || !SecretEquals(Text(state, "sessionToken"), Text(args, "sessionToken"))) return Error("SessionDenied");
        if (operation is "__verify" or "get_status") return Ok(new { sessionId = Text(state, "sessionId"), bindingVersion = Number(state, "bindingVersion"), binding = state["binding"], availability = Date(state, "freshUntil") > now && Text(state, "hubIncarnation") == Text(args, "hubIncarnation") ? Text(state, "availability") : "unknown", observedAt = Text(state, "observedAt") });
        if (operation == "session_bind")
        {
            var current = Number(state, "bindingVersion");
            if (!args.ContainsKey("expectedVersion") || Number(args, "expectedVersion") != current) return Error("VersionConflict", new { currentVersion = current }, current);
            state["binding"] = args["binding"]?.DeepClone(); state["bindingVersion"] = current + 1;
            state["observedAt"] = now.ToString("O"); state["freshUntil"] = now.AddSeconds(Math.Clamp(Number(args, "freshnessSeconds", 120), 1, 600)).ToString("O");
            state["availability"] = Text(args, "availability", "unknown");
            state["hubIncarnation"] = Text(args, "hubIncarnation");
            var registration = state.DeepClone().AsObject(); registration.Remove("sessionToken"); registration.Remove("requests"); registration.Remove("inbox"); registration.Remove("acked");
            Effect(effects, "control", "hub", "__register", registration, now);
            return Ok(new { bindingVersion = current + 1 }, current + 1);
        }
        if (operation == "check_inbox") return Page(Object(state, "inbox").Select(x => x.Value!), args);
        if (operation == "ack")
        {
            var ids = args["messageIds"] as JsonArray ?? new JsonArray(Text(args, "messageId")); var count = 0;
            foreach (var id in ids.Select(x => x!.GetValue<string>()))
            {
                if (!Object(state, "acked").ContainsKey(id) && Object(state, "acked").Count >= MaxRecords) return Error("CapacityExceeded");
                if (Object(state, "inbox").Remove(id)) count++;
                Object(state, "acked")[id] = true;
            }
            return Ok(new { acknowledged = count });
        }
        if (operation == "send_message")
        {
            var recipient = Required(args, "recipientSessionId"); var id = Guid.NewGuid().ToString("N");
            var message = new JsonObject { ["messageId"] = id, ["repositoryId"] = Text(args, "repositoryId"), ["text"] = MessageText(args), ["author"] = caller.PrincipalId, ["senderSessionId"] = Text(args, "sessionId"), ["sentAt"] = now.ToString("O"), ["replyTo"] = args["replyTo"]?.DeepClone() };
            Effect(effects, "session", recipient, "__deliver", message, now);
            return Ok(new { messageId = id, delivery = "pending" });
        }
        return Error("UnknownTool");
    }

    private static RelayReply Board(JsonObject state, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now, List<DurableEffect> effects)
    {
        var tasks = Object(state, "tasks"); var taskId = Text(args, "taskId");
        if (operation == "task_get" && taskId.Length == 0) return Page(tasks.Select(x => (JsonNode)TaskSummary(x.Value!.AsObject())), args);
        if (operation is "context_put" or "context_get")
        {
            var context = Object(state, "context"); var id = Text(args, "contextId");
            if (operation == "context_get") return id.Length == 0 ? Page(context.Select(x => x.Value!), args) : context[id] is JsonNode note ? Ok(note, Number(note.AsObject(), "version")) : Error("NotFound");
            id = Required(args, "contextId"); var version = context[id] is JsonObject previous ? Number(previous, "version") : 0;
            if (Encoding.UTF8.GetByteCount(args["content"]?.ToJsonString() ?? "null") > 16_384) return Error("PayloadTooLarge");
            if (!args.ContainsKey("expectedVersion") || Number(args, "expectedVersion") != version) return Error("VersionConflict", new { currentVersion = version }, version);
            if (context.Count >= MaxRecords && !context.ContainsKey(id)) return Error("CapacityExceeded");
            var entry = new JsonObject { ["contextId"] = id, ["version"] = version + 1, ["content"] = args["content"]?.DeepClone(), ["provenance"] = args["provenance"]?.DeepClone(), ["author"] = caller.PrincipalId, ["updatedAt"] = now.ToString("O") };
            if (Encoding.UTF8.GetByteCount(entry.ToJsonString()) > 24_576) return Error("PayloadTooLarge");
            context[id] = entry; return Ok(entry, version + 1);
        }
        if (taskId.Length == 0) return Error("InvalidArguments", new { required = "taskId" });
        var task = tasks[taskId] as JsonObject;
        if (task is null)
        {
            if (operation != "task_update" || !args.ContainsKey("expectedVersion") || Number(args, "expectedVersion") != 0) return Error("NotFound");
            if (tasks.Count >= MaxRecords) return Error("CapacityExceeded");
            task = new() { ["taskId"] = taskId, ["version"] = 0, ["actions"] = new JsonObject(), ["messages"] = new JsonArray(), ["reports"] = new JsonArray(), ["facts"] = new JsonObject() };
            tasks[taskId] = task;
        }
        var versionNow = Number(task, "version");
        if (operation == "task_get")
        {
            var section = Text(args, "section");
            if (section is "messages" or "reports" or "attemptHistory") return Page(Array(task, section).Select(x => x!), args) with { Version = versionNow };
            if (section == "actions") return Page(Object(task, "actions").Select(x => x.Value!), args) with { Version = versionNow };
            if (section == "facts")
            {
                var facts = Object(task, "facts"); var factKey = Text(args, "factKey");
                if (factKey.Length > 0) return facts.ContainsKey(factKey) ? Ok(new { key = factKey, value = facts[factKey] }, versionNow) : Error("NotFound");
                return Page(facts.Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["value"] = x.Value?.DeepClone() }), args) with { Version = versionNow };
            }
            if (Encoding.UTF8.GetByteCount(task.ToJsonString()) > MaxPayloadBytes) return Ok(TaskSummary(task), versionNow);
            return Ok(task, versionNow);
        }
        var session = Text(args, "sessionId");
        if (operation is "claim_renew" or "claim_release")
        {
            var generation = Number(task, "claimGeneration");
            if (Text(task, "owner") != session || generation != Number(args, "claimGeneration") || Date(task, "leaseUntil") <= now) return Error("StaleClaim", new { claimGeneration = generation });
            if (operation == "claim_renew") task["leaseUntil"] = now.AddSeconds(Lease(args)).ToString("O");
            else
            {
                if (HasUnresolved(task)) return Error("UnresolvedAction");
                if (!args.ContainsKey("expectedVersion") || Number(args, "expectedVersion") != versionNow) return Error("VersionConflict", new { currentVersion = versionNow }, versionNow);
                task.Remove("owner"); task["leaseUntil"] = now.ToString("O"); task["version"] = ++versionNow;
            }
            return Ok(new { claimGeneration = generation, leaseUntil = Text(task, "leaseUntil"), released = operation == "claim_release" }, versionNow);
        }
        if (operation == "__task_unclaim")
        {
            if (Text(task, "owner") == session && Number(task, "claimGeneration") == Number(args, "claimGeneration") && Text(task, "claimRequestId") == Text(args, "claimRequestId")) { task.Remove("owner"); task["leaseUntil"] = now.ToString("O"); task["version"] = versionNow + 1; }
            return Ok(new { released = true }, Number(task, "version"));
        }
        if (operation == "task_claim")
        {
            var owned = Text(task, "owner") == session && Date(task, "leaseUntil") > now;
            if (owned && !(task["resourceIds"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual((args["resourceIds"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)) return Error("ResourceSetConflict", new { resourceIds = task["resourceIds"] }, versionNow);
            if (!owned && HasUnresolved(task)) return Error("Busy", new { holder = Text(task, "owner"), leaseUntil = Text(task, "leaseUntil"), unresolved = true }, versionNow);
            if (Text(task, "owner").Length > 0 && Text(task, "owner") != session && (Date(task, "leaseUntil") > now || HasUnresolved(task))) return Error("Busy", new { holder = Text(task, "owner"), leaseUntil = Text(task, "leaseUntil"), unresolved = HasUnresolved(task) }, versionNow);
            if (!args.ContainsKey("expectedVersion") || Number(args, "expectedVersion") != versionNow) return Error("VersionConflict", new { currentVersion = versionNow }, versionNow);
            if (!owned)
            {
                task["owner"] = Required(args, "sessionId"); task["claimGeneration"] = Number(task, "claimGeneration") + 1; task["claimRequestId"] = Text(args, "requestId");
                task["leaseUntil"] = now.AddSeconds(Lease(args)).ToString("O"); task["version"] = ++versionNow;
                task["resourceIds"] = args["resourceIds"]?.DeepClone() ?? new JsonArray();
            }
            return Ok(new { taskId, newlyAcquired = !owned, claimGeneration = Number(task, "claimGeneration"), leaseUntil = Text(task, "leaseUntil"), resourceIds = task["resourceIds"] }, versionNow);
        }
        if (operation == "action_begin")
        {
            var id = Required(args, "actionId"); var fingerprint = Required(args, "inputFingerprint"); var actions = Object(task, "actions");
            if (id.Length > 128) return Error("InvalidArguments");
            if (actions[id] is JsonObject prior)
            {
                if (Text(prior, "inputFingerprint") != fingerprint) return Error("ActionConflict");
                if (!Flag(args, "explicitRetry")) return Ok(new { action = prior, replayAllowed = false, existing = true }, versionNow);
                if (Text(prior, "restoration") is "pending" or "blocked") return Error("RestorationRequired");
                if (Text(prior, "status") is not ("completed" or "failed" or "canceled")) return Error("ActionUncertain");
                if (Number(args, "attempt") != Number(prior, "attempt") + 1) return Error("AttemptConflict");
                Array(task, "attemptHistory").Add(prior.DeepClone());
            }
            if (Text(task, "owner") != session || Date(task, "leaseUntil") <= now) return Error("ClaimRequired");
            if (actions.Any(x => x.Key != id && Unresolved(x.Value!.AsObject()))) return Error("ActionUncertain");
            if (!args.ContainsKey("expectedVersion") || Number(args, "expectedVersion") != versionNow) return Error("VersionConflict", new { currentVersion = versionNow }, versionNow);
            var trigger = Text(args, "triggerId");
            if (trigger.Length > 0 && actions.Any(x => x.Key != id && Text(x.Value!.AsObject(), "triggerId") == trigger)) return Error("TriggerConsumed");
            if (actions.Count >= 128 || Array(task, "attemptHistory").Count >= 256) return Error("CapacityExceeded");
            var action = new JsonObject { ["actionId"] = id, ["inputFingerprint"] = fingerprint, ["triggerId"] = trigger, ["attempt"] = Number(args, "attempt", 1), ["status"] = "invoking", ["sessionId"] = session, ["principalId"] = caller.PrincipalId, ["bindingVersion"] = Number(args, "bindingVersion"), ["binding"] = args["binding"]?.DeepClone(), ["startedAt"] = now.ToString("O"), ["restoration"] = Flag(args, "requiresRestoration") ? "pending" : "not_required", ["resourceIds"] = task["resourceIds"]?.DeepClone() };
            action["requiresRestoration"] = Flag(args, "requiresRestoration");
            actions[id] = action; task["version"] = ++versionNow;
            foreach (var resource in (action["resourceIds"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()))
                Effect(effects, "lock", resource, "__block", new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["actionId"] = id, ["actionKey"] = ActionKey(args, id, Number(action, "attempt")), ["sessionId"] = session }, now);
            return Ok(new { action, replayAllowed = true, existing = false }, versionNow);
        }
        if (operation is "action_report" or "task_update")
        {
            if (!args.ContainsKey("expectedVersion") || Number(args, "expectedVersion") != versionNow) return Error("VersionConflict", new { currentVersion = versionNow }, versionNow);
            var reports = args["actionReports"] as JsonArray ?? new JsonArray();
            if (operation == "action_report") reports = new JsonArray(args.DeepClone());
            if (operation == "task_update" && Text(task, "owner").Length > 0 && Text(task, "owner") != session) return Error("OwnerConflict");
            foreach (var report in reports)
            {
                var update = report!.AsObject(); var id = Required(update, "actionId");
                if (Encoding.UTF8.GetByteCount(update.ToJsonString()) > MaxReportBytes) return Error("PayloadTooLarge");
                if (Object(task, "actions")[id] is not JsonObject currentAction) return Error("ActionUnknown");
                if (!update.ContainsKey("attempt") || Number(update, "attempt") < 1) return Error("AttemptRequired");
                var attempt = Number(update, "attempt");
                var historical = attempt != Number(currentAction, "attempt");
                var action = historical ? Array(task, "attemptHistory").OfType<JsonObject>().SingleOrDefault(x => Text(x, "actionId") == id && Number(x, "attempt") == attempt) : currentAction;
                if (action is null) return Error("AttemptConflict");
                var originalWriter = Text(action, "principalId") == caller.PrincipalId && Text(action, "sessionId") == session;
                // Administrative acceptance is only a cleanup decision. It cannot supply an outcome.
                var adminAcceptance = caller.IsAdministrator && operation == "action_report" && Text(update, "restoration") == "accepted" && !update.ContainsKey("outcome") && !args.ContainsKey("patch") && !args.ContainsKey("notes") && !args.ContainsKey("message");
                if (!originalWriter && !adminAcceptance) return Error("ActionDenied");
                var outcome = adminAcceptance ? Text(action, "status") : Required(update, "outcome");
                if (!new[] { "started", "completed", "failed", "canceled", "incomplete", "unknown" }.Contains(outcome)) return Error("InvalidOutcome");
                var restorationValue = update.ContainsKey("restoration") ? Text(update, "restoration") : Text(action, "restoration");
                if (!new[] { "pending", "blocked", "verified", "accepted", "not_required" }.Contains(restorationValue)) return Error("InvalidRestoration");
                if (Text(update, "restoration") == "accepted" && Text(action, "restoration") != "accepted" && !caller.IsAdministrator) return Error("AdministratorRequired");
                var history = Array(task, "reports"); if (history.Count >= 256) return Error("CapacityExceeded");
                if (historical)
                {
                    history.Add(new JsonObject { ["actionId"] = id, ["attempt"] = attempt, ["outcome"] = outcome, ["restoration"] = restorationValue, ["report"] = update["report"]?.DeepClone(), ["at"] = now.ToString("O"), ["author"] = caller.PrincipalId, ["sessionId"] = session, ["historical"] = true });
                    continue;
                }
                var terminal = Text(action, "status") is "completed" or "failed" or "canceled";
                if (terminal && Text(action, "status") != outcome) return Error("ActionConflict");
                var priorRestoration = Text(action, "restoration");
                var requiresRestoration = Flag(action, "requiresRestoration") || priorRestoration is "pending" or "blocked";
                if (requiresRestoration && restorationValue == "not_required") return Error("RestorationRequired");
                if (priorRestoration is "verified" or "accepted" && restorationValue != priorRestoration) return Error("RestorationResolved");
                if (!Unresolved(action) && restorationValue is "pending" or "blocked") return Error("RestorationResolved");
                action["status"] = outcome;
                action["restoration"] = restorationValue;
                action["report"] = update["report"]?.DeepClone(); action["reportedAt"] = now.ToString("O");
                history.Add(new JsonObject { ["actionId"] = id, ["attempt"] = attempt, ["outcome"] = outcome, ["restoration"] = restorationValue, ["report"] = update["report"]?.DeepClone(), ["at"] = now.ToString("O"), ["author"] = caller.PrincipalId, ["sessionId"] = session });
                if (!Unresolved(action))
                    foreach (var resource in (action["resourceIds"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()))
                        Effect(effects, "lock", resource, "__unblock", new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["actionId"] = id, ["actionKey"] = ActionKey(args, id, Number(action, "attempt")), ["sessionId"] = Text(action, "sessionId"), ["administrativeAcceptance"] = adminAcceptance }, now);
            }
            if (args["patch"] is JsonObject patch)
            {
                if (Encoding.UTF8.GetByteCount(patch.ToJsonString()) > 16_384) return Error("PayloadTooLarge");
                foreach (var fact in patch) Object(task, "facts")[fact.Key] = fact.Value?.DeepClone();
            }
            if (args["notes"] is JsonNode notes)
            {
                if (Encoding.UTF8.GetByteCount(notes.ToJsonString()) > 16_384) return Error("PayloadTooLarge");
                Object(task, "facts")["notes"] = notes.DeepClone();
            }
            if (args["message"] is JsonObject message) AddTaskMessage(task, message, caller, session, now, effects, args);
            task["updatedAt"] = now.ToString("O"); task["updatedBy"] = caller.PrincipalId; task["version"] = ++versionNow;
            return Ok(task, versionNow);
        }
        if (operation == "send_message")
        {
            AddTaskMessage(task, args, caller, session, now, effects, args); task["version"] = ++versionNow;
            return Ok(new { messageId = Array(task, "messages").Last()!["messageId"]!.GetValue<string>() }, versionNow);
        }
        return Error("UnknownTool");
    }

    private static RelayReply Lock(JsonObject state, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now)
    {
        var holder = Text(state, "holder"); var session = Text(args, "sessionId"); var generation = Number(state, "generation"); var blockers = Object(state, "blockers");
        if (operation == "__block")
        {
            var actionKey = Text(args, "actionKey", Required(args, "actionId"));
            if (Object(state, "resolvedActions").ContainsKey(actionKey)) return Ok(new { resolved = true });
            if (holder != session || Date(state, "leaseUntil") <= now) return Error("ClaimRequired");
            if (blockers.Count > 0 && !blockers.ContainsKey(actionKey)) return Error("Busy", new { holder, unresolved = true });
            blockers[actionKey] = new JsonObject { ["sessionId"] = session, ["principalId"] = caller.PrincipalId };
            return Ok(new { blocked = true });
        }
        if (operation == "__unblock")
        {
            var id = Text(args, "actionKey", Required(args, "actionId"));
            if (blockers[id] is JsonObject blocker && (Text(blocker, "sessionId") != session || Text(blocker, "principalId") != caller.PrincipalId && !(caller.IsAdministrator && Flag(args, "administrativeAcceptance")))) return Error("ActionDenied");
            var resolved = Object(state, "resolvedActions");
            if (!resolved.ContainsKey(id) && resolved.Count >= MaxRecords) return Error("CapacityExceeded");
            blockers.Remove(id); resolved[id] = true;
            return Ok(new { resolved = true });
        }
        if (operation == "claim_acquire")
        {
            if (holder.Length > 0 && holder != session && (Date(state, "leaseUntil") > now || blockers.Count > 0)) return Error("Busy", new { holder, leaseUntil = Text(state, "leaseUntil"), blockers = blockers.Count, retryAfterSeconds = 5 });
            var owned = holder == session && Date(state, "leaseUntil") > now;
            if (blockers.Count > 0 && !owned) return Error("Busy", new { holder, blockers = blockers.Count });
            if (!owned) { state["holder"] = Required(args, "sessionId"); state["generation"] = ++generation; state["leaseUntil"] = now.AddSeconds(Lease(args)).ToString("O"); }
            return Ok(new { newlyAcquired = !owned, generation, leaseUntil = Text(state, "leaseUntil") }, generation);
        }
        if (operation is "claim_renew" or "claim_release")
        {
            if (holder != session || generation != Number(args, "generation") || Date(state, "leaseUntil") <= now) return Error("StaleClaim", new { generation });
            if (operation == "claim_renew") state["leaseUntil"] = now.AddSeconds(Lease(args)).ToString("O");
            else { if (blockers.Count > 0) return Error("UnresolvedAction", new { blockers = blockers.Count }); state["holder"] = ""; state["leaseUntil"] = now.ToString("O"); }
            return Ok(new { generation, leaseUntil = Text(state, "leaseUntil"), released = operation == "claim_release" }, generation);
        }
        return Error("UnknownTool");
    }

    private static void AddTaskMessage(JsonObject task, JsonObject args, RelayCaller caller, string session, DateTimeOffset now, List<DurableEffect> effects, JsonObject root)
    {
        var messages = Array(task, "messages"); if (messages.Count >= 256) throw new ArgumentException("Task conversation limit reached.");
        var message = new JsonObject { ["messageId"] = Guid.NewGuid().ToString("N"), ["taskId"] = Text(task, "taskId"), ["text"] = MessageText(args), ["kind"] = Text(args, "kind", "message"), ["replyTo"] = args["replyTo"]?.DeepClone(), ["author"] = caller.PrincipalId, ["sessionId"] = session, ["at"] = now.ToString("O"), ["taskVersion"] = Number(task, "version") + 1 };
        messages.Add(message);
        var recipient = Text(args, "recipientSessionId", Text(task, "owner"));
        if (recipient.Length > 0) Effect(effects, "session", recipient, "__deliver", new JsonObject { ["repositoryId"] = Text(root, "repositoryId"), ["messageId"] = Text(message, "messageId"), ["taskId"] = Text(task, "taskId"), ["boardId"] = Text(root, "boardId", "default"), ["kind"] = "task.changed", ["taskVersion"] = Number(task, "version") + 1 }, now);
    }
    private static bool HasUnresolved(JsonObject task) => Object(task, "actions").Any(x => Unresolved(x.Value!.AsObject()));
    private static JsonObject TaskSummary(JsonObject task)
    {
        var summary = new JsonObject { ["truncated"] = true, ["taskId"] = Text(task, "taskId"), ["version"] = Number(task, "version"), ["owner"] = task["owner"]?.DeepClone(), ["claimGeneration"] = Number(task, "claimGeneration"), ["leaseUntil"] = task["leaseUntil"]?.DeepClone(), ["resourceIds"] = task["resourceIds"]?.DeepClone() };
        var unresolved = new JsonArray();
        foreach (var action in Object(task, "actions").Where(x => Unresolved(x.Value!.AsObject()))) unresolved.Add(new JsonObject { ["actionId"] = action.Key, ["attempt"] = action.Value!["attempt"]?.DeepClone(), ["status"] = action.Value["status"]?.DeepClone(), ["restoration"] = action.Value["restoration"]?.DeepClone(), ["resourceIds"] = action.Value["resourceIds"]?.DeepClone() });
        summary["unresolvedActions"] = unresolved; summary["sections"] = new JsonArray("facts", "messages", "reports", "actions", "attemptHistory");
        return summary;
    }
    private static string MessageText(JsonObject args)
    {
        var text = Text(args, "text");
        return text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= MaxReportBytes ? text : throw new ArgumentException("Message text must contain 1 to 8192 UTF-8 bytes.");
    }
    internal static string ActionKey(JsonObject args, string actionId, long attempt) => string.Join("/", new[] { Text(args, "repositoryId"), Text(args, "boardId", "default"), Text(args, "taskId"), actionId, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Select(Uri.EscapeDataString));
    private static bool Unresolved(JsonObject action) => Text(action, "status") is "invoking" or "started" or "unknown" or "incomplete" || Text(action, "restoration") is "pending" or "blocked";
    private static int Lease(JsonObject args) => (int)Math.Clamp(Number(args, "leaseSeconds", 120), 1, 600);
    internal static bool IsMutation(string operation) => operation is not ("worker_list" or "get_status" or "task_get" or "context_get" or "check_inbox" or "__verify" or "__resolve");
    internal static string Text(JsonObject node, string key, string fallback = "") => node[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : fallback;
    internal static long Number(JsonObject node, string key, long fallback = 0) => long.TryParse(node[key]?.ToString(), out var value) ? value : fallback;
    internal static JsonObject Object(JsonObject node, string key) { if (node[key] is not JsonObject result) node[key] = result = new(); return result; }
    private static JsonArray Array(JsonObject node, string key) { if (node[key] is not JsonArray result) node[key] = result = new(); return result; }
    private static string Required(JsonObject node, string key) => Text(node, key) is { Length: > 0 and <= 256 } value ? value : throw new ArgumentException(key + " is required and at most 256 characters.");
    private static bool Flag(JsonObject node, string key) => node[key]?.GetValue<bool>() ?? false;
    private static DateTimeOffset Date(JsonObject node, string key) => DateTimeOffset.TryParse(Text(node, key), out var result) ? result : DateTimeOffset.MinValue;
    private static bool SecretEquals(string stored, string supplied) => stored.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(supplied));
    internal static RelayReply Ok(object value, long version = 0) => new(true, "Ok", JsonSerializer.Serialize(value), version);
    internal static RelayReply Error(string code, object? data = null, long version = 0) => new(false, code, JsonSerializer.Serialize(data ?? new { }), version);
    internal static bool FitsWire(RelayReply reply, int maxBytes = MaxPayloadBytes) => JsonSerializer.SerializeToUtf8Bytes(reply).Length <= maxBytes;

    internal static RelayReply BoundWire(RelayReply reply, int maxBytes = MaxPayloadBytes)
    {
        if (FitsWire(reply, maxBytes)) return reply;
        var data = JsonNode.Parse(reply.DataJson)!;
        // Details remain durable and separately retrievable. Authorization, grants, replay
        // decisions, outcomes, attempts, restoration state and resource blockers are retained.
        void Trim(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "facts", "messages", "reports", "attemptHistory", "binding", "report", "provenance", "content", "notes" })
                    if (obj.Remove(key)) { obj[key + "Truncated"] = true; obj["truncated"] = true; }
                foreach (var child in obj.Select(x => x.Value).OfType<JsonNode>().ToArray()) Trim(child);
            }
            else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) Trim(child);
        }
        Trim(data);
        if (data is JsonObject root) root["truncated"] = true;
        return reply with { DataJson = data.ToJsonString() };
    }

    private static RelayReply Page(IEnumerable<JsonNode> source, JsonObject args)
    {
        var offset = (int)Math.Clamp(Number(args, "cursor"), 0, MaxRecords); var limit = (int)Math.Clamp(Number(args, "limit", 20), 1, MaxPageSize);
        var all = source.ToArray(); var items = all.Skip(offset).Take(limit).Select(x => x.DeepClone()).ToList();
        while (items.Count > 1 && !FitsWire(Ok(new { items, nextCursor = offset + items.Count, truncated = true, total = all.Length }), MaxPayloadBytes - 8192)) items.RemoveAt(items.Count - 1);
        if (items.Count == 0 && offset < all.Length) items.Add(new JsonObject { ["truncated"] = true, ["coverage"] = "Oversized item; retrieve its exact detail separately." });
        var more = offset + items.Count < all.Length;
        return Ok(new { items, nextCursor = more ? (int?)(offset + items.Count) : null, truncated = more, total = all.Length });
    }
    private static void Effect(List<DurableEffect> effects, string target, string key, string operation, JsonObject payload, DateTimeOffset now) => effects.Add(new(Guid.NewGuid().ToString("N"), target, key, operation, payload.ToJsonString(), now));
}

[Orleans.GenerateSerializer]
public sealed class CoordinationDocument
{
    [Orleans.Id(0)] public string Json { get; set; } = "{}";
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>The hub singleton: session directory, worker presence, registered resources and their aliases, pause and cancellation requests.</summary>
internal static class ControlAggregate
{
    private const int MaxMachineSummaries = 50;
    private static readonly TimeSpan InitialFreshness = TimeSpan.FromMinutes(2);

    public static RelayReply Handle(AggregateContext context)
    {
        Required(context.Args, "repositoryId");
        return context.Operation switch
        {
            RelayTools.SessionOpen => OpenSession(context),
            Operations.Register => RegisterSession(context),
            RelayTools.WorkerList => ListWorkers(context),
            RelayTools.GetStatus => Status(context),
            Operations.Resolve => ResolveResource(context),
            Operations.ResolveAll => ResolveAll(context),
            Operations.SessionOwners => OwnedSessions(context),
            RelayTools.ControlRequest => Control(context),
            _ => Error(UnknownTool)
        };
    }

    private static RelayReply OpenSession(AggregateContext context)
    {
        var args = context.Args;
        Required(args, "requestId");
        var sessions = ObjectAt(context.State, "sessions");
        if (sessions.Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
        var id = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new JsonObject
        {
            ["sessionId"] = id, ["principalId"] = context.Caller.PrincipalId, ["repositoryId"] = context.Repository, ["sessionToken"] = token, ["bindingVersion"] = 0,
            ["machineId"] = Text(args, "machineId"), ["label"] = Text(args, "label"), ["binding"] = args["binding"]?.DeepClone(),
            ["observedAt"] = context.Now.ToString("O"), ["freshUntil"] = context.Now.Add(InitialFreshness).ToString("O"), ["availability"] = "unknown"
        };
        session["hubIncarnation"] = Text(args, "hubIncarnation");
        sessions[id] = session;
        context.Emit(AggregateKinds.Session, id, Operations.Open, session);
        return Ok(new { sessionId = id, sessionToken = token, bindingVersion = 0, binding = args["binding"] });
    }

    private static RelayReply RegisterSession(AggregateContext context)
    {
        var id = Required(context.Args, "sessionId");
        if (ObjectAt(context.State, "sessions")[id] is not JsonObject session || Text(session, "repositoryId") != context.Repository) return Error(SessionDenied);
        foreach (var property in new[] { "binding", "bindingVersion", "observedAt", "freshUntil", "availability", "machineId", "label", "hubIncarnation" })
            if (context.Args.ContainsKey(property)) session[property] = context.Args[property]?.DeepClone();
        return Ok(new { registered = true });
    }

    /// <summary>Reported presence only. A record from another hub incarnation or past its freshness is shown as stale.</summary>
    private static RelayReply ListWorkers(AggregateContext context)
    {
        var workers = ObjectAt(context.State, "sessions").Select(x => x.Value!.AsObject()).Where(x => Text(x, "repositoryId") == context.Repository).Select(x =>
        {
            var copy = x.DeepClone().AsObject(); copy.Remove("sessionToken"); copy.Remove("principalId");
            var stale = Date(copy, "freshUntil") <= context.Now || Text(copy, "hubIncarnation") != Text(context.Args, "hubIncarnation");
            copy["stale"] = stale; copy.Remove("hubIncarnation");
            if (stale) copy["availability"] = "unknown";
            return (JsonNode)copy;
        }).ToArray();
        var data = JsonNode.Parse(Page(workers, context.Args).DataJson)!.AsObject();
        var machines = workers.GroupBy(x => Text(x.AsObject(), "machineId")).Where(x => x.Key.Length > 0).Select(group => new
        {
            machineId = group.Key,
            sessionCount = group.Count(),
            availability = group.Any(x => Text(x.AsObject(), "availability") == "active") ? "active" : "unknown"
        }).ToArray();
        data["machines"] = JsonSerializer.SerializeToNode(machines.Take(MaxMachineSummaries)); data["machineSummariesTruncated"] = machines.Length > MaxMachineSummaries;
        var machinePage = data["machines"]!.AsArray();
        while (Encoding.UTF8.GetByteCount(data.ToJsonString()) > RelayLimits.MaxPayloadBytes && machinePage.Count > 0) { machinePage.RemoveAt(machinePage.Count - 1); data["machineSummariesTruncated"] = true; }
        return Ok(data);
    }

    private static RelayReply Status(AggregateContext context)
    {
        var sessionId = context.SessionId;
        var cancellations = ObjectAt(context.State, "cancellations");
        return Ok(new
        {
            paused = Flag(context.State, "paused"),
            cancellations = cancellations.Count,
            requests = sessionId.Length > 0 ? cancellations.Where(x => x.Key.StartsWith(sessionId + ":", StringComparison.Ordinal)).Take(RelayLimits.MaxPageSize).Select(x => new { target = x.Key, request = x.Value }).ToArray() : []
        });
    }

    /// <summary>The given session IDs that the caller's principal opened in this repository. Read-only; tokens and other principals are never returned.</summary>
    private static RelayReply OwnedSessions(AggregateContext context)
    {
        var sessions = ObjectAt(context.State, "sessions");
        var owned = Strings(context.Args["sessionIds"]).Distinct(StringComparer.Ordinal)
            .Where(id => sessions[id] is JsonObject session && Text(session, "principalId") == context.Caller.PrincipalId && Text(session, "repositoryId") == context.Repository).ToArray();
        return Ok(new { owned });
    }

    /// <summary>Maps a resource ID or a machine-scoped alias to its canonical registered resource.</summary>
    private static RelayReply ResolveResource(AggregateContext context) =>
        Find(context, Required(context.Args, "resourceId")) is { } resolved ? Ok(resolved) : Error(ResourceUnknown);

    /// <summary>Read-only: the canonical ID of each named resource this repository registered, in one call. Unknown names are left out.</summary>
    private static RelayReply ResolveAll(AggregateContext context)
    {
        var resolved = new JsonObject();
        foreach (var id in Strings(context.Args["resourceIds"]).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxOfferResourceLookups))
            if (Find(context, id) is { } resource) resolved[id] = Text(resource, "resourceId");
        return Ok(new { resolved });
    }

    private static JsonObject? Find(AggregateContext context, string id)
    {
        var resources = ObjectAt(context.State, "resources");
        var resolved = resources[id] as JsonObject;
        if (resolved is null && ObjectAt(context.State, "aliases")[AliasKey(Text(context.Args, "machineId"), id)] is JsonValue alias) resolved = resources[alias.GetValue<string>()] as JsonObject;
        return resolved is null || Text(resolved, "repositoryId") != context.Repository ? null : resolved;
    }

    private static RelayReply Control(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        var command = Required(context.Args, "command");
        if (command == "register_resource") return RegisterResource(context);
        var state = context.State;
        if (command is "pause" or "stop") state["paused"] = true;
        else if (command == "resume") state["paused"] = false;
        else if (command != "cancel") return Error(UnknownCommand);
        var cancellation = command is "stop" or "cancel";
        if (cancellation)
            ObjectAt(state, "cancellations")[context.SessionId + ":" + Text(context.Args, "actionId")] = JsonSerializer.SerializeToNode(new { requestedBy = context.Caller.PrincipalId, at = context.Now, status = "requested_unknown" });
        return Ok(new { paused = Flag(state, "paused"), cancellationRequested = cancellation });
    }

    private static RelayReply RegisterResource(AggregateContext context)
    {
        var id = Required(context.Args, "resourceId");
        var machine = Required(context.Args, "machineId");
        var resources = ObjectAt(context.State, "resources");
        var aliases = ObjectAt(context.State, "aliases");
        var names = Strings(context.Args["aliases"]).Append(id).ToArray();
        var canonical = id;
        foreach (var name in names)
            if (aliases[AliasKey(machine, name)] is JsonValue prior) canonical = prior.GetValue<string>();
        if (resources[canonical] is JsonObject existing && Text(existing, "repositoryId") != context.Repository) return Error(RepositoryDenied);
        foreach (var name in names)
        {
            var aliasKey = AliasKey(machine, name);
            if (aliases[aliasKey] is JsonValue other && other.GetValue<string>() != canonical) return Error(AliasConflict);
            aliases[aliasKey] = canonical;
        }
        resources[canonical] = new JsonObject { ["resourceId"] = canonical, ["repositoryId"] = context.Repository, ["machineId"] = machine, ["aliases"] = JsonSerializer.SerializeToNode(names) };
        return Ok(new { resourceId = canonical });
    }

    private static string AliasKey(string machine, string name) => machine.ToUpperInvariant() + ":" + name.ToUpperInvariant();
}

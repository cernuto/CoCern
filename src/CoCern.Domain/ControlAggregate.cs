using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

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
            Operations.AttentionRoute => RouteAttention(context),
            RelayTools.ControlRequest => Control(context),
            _ => Error(UnknownTool)
        };
    }

    private static RelayReply OpenSession(AggregateContext context)
    {
        var args = context.Args;
        Required(args, "requestId");
        var sessions = ObjectAt(context.State, "sessions");
        if (sessions.Count >= RelayLimits.MaxRecords) DropStaleSessions(context, sessions);
        // Every session was seen within StaleSessionHours: refused for now, and it clears as they go stale.
        if (sessions.Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded, new { staleAfterHours = RelayLimits.StaleSessionHours });
        var id = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new JsonObject
        {
            ["sessionId"] = id, ["principalId"] = context.Caller.PrincipalId, ["repositoryId"] = context.Repository, ["sessionToken"] = token, ["bindingVersion"] = 0,
            ["machineId"] = Text(args, "machineId"), ["label"] = Text(args, "label"), ["binding"] = args["binding"]?.DeepClone(),
            ["observedAt"] = context.Now.ToString("O"), ["freshUntil"] = context.Now.Add(InitialFreshness).ToString("O"), ["availability"] = "unknown"
        };
        session["hubIncarnation"] = Text(args, "hubIncarnation");
        // The role given at session_open routes manager-bound attention items; it grants nothing.
        if (Text(args, "role") is { Length: > 0 and <= RelayLimits.MaxActivityRoleLength } role) session["role"] = role;
        sessions[id] = session;
        context.Emit(AggregateKinds.Session, id, Operations.Open, session);
        return Ok(new { sessionId = id, sessionToken = token, bindingVersion = 0, binding = args["binding"] });
    }

    /// <summary>
    /// Makes room in a full directory: drops sessions not seen (fresh, active or observed) for <see cref="RelayLimits.StaleSessionHours"/>,
    /// least recently seen first, down to <see cref="RelayLimits.RetainedEntriesAfterPrune"/>, so it runs at most once per 512 opens.
    /// A session seen within that time is never dropped. Each dropped session is sent <see cref="Operations.SessionExpire"/>, under its own
    /// repository, so its chat gets a clean <c>SessionUnknown</c> and opens again.
    /// </summary>
    private static void DropStaleSessions(AggregateContext context, JsonObject sessions)
    {
        var cutoff = context.Now.AddHours(-RelayLimits.StaleSessionHours);
        var stale = sessions.Select(entry => (entry.Key, Session: entry.Value as JsonObject, Seen: entry.Value is JsonObject record ? LastSeen(record) : DateTimeOffset.MinValue))
            .Where(entry => entry.Seen <= cutoff).OrderBy(entry => entry.Seen).ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(sessions.Count - RelayLimits.RetainedEntriesAfterPrune).ToList();
        foreach (var (id, session, _) in stale)
        {
            sessions.Remove(id);
            var repository = session is null ? "" : Text(session, "repositoryId");
            context.Emit(AggregateKinds.Session, id, Operations.SessionExpire, new JsonObject
            {
                ["repositoryId"] = repository, ["sessionId"] = id,
                // Its own caller: the session belongs to its repository, which the caller opening a new session may not reach.
                ["__principalId"] = "relay-directory", ["__administrator"] = false, ["__repositories"] = new JsonArray(repository)
            });
        }
    }

    private static DateTimeOffset LastSeen(JsonObject session) =>
        new[] { Date(session, "freshUntil"), Date(session, "activeAt"), Date(session, "observedAt") }.Max();

    private static RelayReply RegisterSession(AggregateContext context)
    {
        var id = Required(context.Args, "sessionId");
        // A session dropped from the directory may still send a late registration; accept it unchanged so its outbox does not retry forever.
        if (ObjectAt(context.State, "sessions")[id] is not JsonObject session) return Ok(new { registered = false });
        if (Text(session, "repositoryId") != context.Repository) return Error(SessionDenied);
        foreach (var property in new[] { "binding", "bindingVersion", "observedAt", "freshUntil", "availability", "machineId", "label", "hubIncarnation", "activeAt" })
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

    /// <summary>
    /// Passes one attention raise or close to every session in the repository whose role (from <c>session_open</c>, ignoring case) matches,
    /// except the session that caused it and sessions neither fresh nor active (a boundary reply, reported hourly as <c>activeAt</c>) for
    /// more than <see cref="RelayLimits.MaxAttentionRouteIdleHours"/> (closed chats); at most <see cref="RelayLimits.MaxAttentionRouteTargets"/>, most recently seen first.
    /// </summary>
    private static RelayReply RouteAttention(AggregateContext context)
    {
        var args = context.Args;
        var operation = Text(args, "operation");
        if (operation is not (Operations.AttentionRaise or Operations.AttentionClose) || args["item"] is not JsonObject item) return Error(InvalidArguments, new { field = "item" });
        var role = Required(args, "role");
        var except = Text(args, "exceptSessionId");
        var targets = ObjectAt(context.State, "sessions").Select(x => x.Value).OfType<JsonObject>()
            .Where(session => Text(session, "repositoryId") == context.Repository && string.Equals(Text(session, "role"), role, StringComparison.OrdinalIgnoreCase) && Text(session, "sessionId") != except
                && Max(Date(session, "freshUntil"), Date(session, "activeAt")) > context.Now.AddHours(-RelayLimits.MaxAttentionRouteIdleHours))
            .OrderByDescending(session => Date(session, "observedAt")).Take(RelayLimits.MaxAttentionRouteTargets).Select(session => Text(session, "sessionId")).ToArray();
        foreach (var target in targets)
        {
            var copy = item.DeepClone().AsObject(); copy["repositoryId"] = context.Repository;
            context.Emit(AggregateKinds.Session, target, operation, copy);
        }
        return Ok(new { routed = targets.Length });
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

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

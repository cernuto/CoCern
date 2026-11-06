using System.Text.Json.Nodes;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>A session's durable tool selection. It never grants credential authority.</summary>
public static class SessionToolSettings
{
    public const string SetOperation = "__session_tool_settings_set";
    public const string ReadOperation = "__session_tool_settings_read";
    public const int MaxAuditEntries = 1000;
    public const int MaxHumanWordsLength = 1024;

    public static JsonObject Read(JsonObject state) => new()
    {
        ["profile"] = state["toolProfile"]?.DeepClone(),
        ["revision"] = state["toolProfileRevision"]?.DeepClone() ?? JsonValue.Create(0),
        ["explicit"] = state["toolProfile"] is not null
    };

    internal static RelayReply ReadForAdministrator(AggregateContext context)
    {
        var state = new SessionState(context.State);
        var repository = Text(context.Args, "repositoryId");
        if (!context.Caller.IsInternal || !context.Caller.IsAdministrator || !state.Opened || state.Closed ||
            state.SessionId != context.Key || repository.Length == 0 || repository != state.Record.RepositoryId || !context.Caller.MayAccess(repository))
            return Error(SessionDenied);
        return Ok(Read(context.State));
    }

    internal static RelayReply Set(AggregateContext context)
    {
        var state = new SessionState(context.State);
        var repository = Text(context.Args, "repositoryId");
        if (!context.Caller.IsAdministrator || !state.Opened || state.Closed ||
            state.SessionId != context.Key || repository.Length == 0 || repository != state.Record.RepositoryId || !context.Caller.MayAccess(repository))
            return Error(SessionDenied);
        var profile = Text(context.Args, "profile");
        var words = Text(context.Args, "humanWords");
        if (profile is not ("worker" or "chat" or "diagnostics" or "admin") || string.IsNullOrWhiteSpace(words) || words.Length > MaxHumanWordsLength ||
            context.Args["expectedVersion"] is not JsonValue expectedNode || !expectedNode.TryGetValue<long>(out var expected) || expected < 0)
            return Error(InvalidArguments);
        var current = context.State["toolProfileRevision"]?.GetValue<long>() ?? 0;
        if (expected != current) return Error(CoCern.Contracts.RelayErrorCodes.VersionConflict, new { currentVersion = current });
        if (current == long.MaxValue) return Error(CapacityExceeded);
        var audit = context.State["toolProfileAudit"] as JsonArray;
        if (audit is { Count: >= MaxAuditEntries }) return Error(CapacityExceeded);
        audit ??= new JsonArray();
        audit.Add(new JsonObject { ["revision"] = current + 1, ["profile"] = profile, ["humanWords"] = words,
            ["administrator"] = context.Caller.PrincipalId, ["at"] = context.Now.ToString("O") });
        context.State["toolProfile"] = profile;
        context.State["toolProfileRevision"] = current + 1;
        if (context.State["toolProfileAudit"] is null) context.State["toolProfileAudit"] = audit;
        return Ok(Read(context.State), current + 1);
    }
}

using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.State;
using Orleans;
using Orleans.Runtime;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

public interface ISessionContextGrain : IRelayAggregateGrain
{
    Task<RelayReply> SearchAsync(RelayCaller caller, string argumentsJson);
    Task<RelayReply> ReadPartAsync(RelayCaller caller, string argumentsJson);
}

[GrainType("cocern.session_context")]
public sealed class SessionContextGrain([PersistentState("session_context", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> persistent,
    IGrainFactory grains, IServiceProvider services, ITokenCounter tokens) : CoordinationGrain(persistent, grains, services), ISessionContextGrain
{
    protected override string Kind => AggregateKinds.SessionContext;
    private SessionContextIndex? index;

    public override async Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
    {
        var reply = await base.ExecuteAsync(caller, operation, argumentsJson);
        if (reply.Ok && operation == RelayTools.SessionContextPut && index is not null && TryGetCommittedDocument(out var document))
        {
            var state = new SessionContextState(JsonNode.Parse(document.Json)!.AsObject());
            try { if (state.Exists) index.Apply(state); else { index.Dispose(); index = null; } }
            catch { index?.Dispose(); index = null; } // Index failure cannot undo or misreport a committed save.
        }
        return reply;
    }

    protected override RelayReply? ValidateCandidate(StateEnvelope<CoordinationDocument> candidate, string operation)
    {
        if (operation != RelayTools.SessionContextPut) return null;
        var state = new SessionContextState(JsonNode.Parse(candidate.Value.Json)!.AsObject());
        if (Encoding.UTF8.GetByteCount(candidate.Value.Json) > SessionContextLimits.StateBytes) return Error(SessionContextLimits.BudgetExceeded);
        if (!state.Exists) return null;
        var manifest = state.Manifest().ToJsonString();
        if (tokens.Count(Text(state.Json, "summary")) > SessionContextLimits.SummaryTokens
            || tokens.Count(state.Json["nextSteps"]?.ToJsonString() ?? "[]") > SessionContextLimits.NextStepTokens
            || tokens.Count(manifest) > SessionContextLimits.ManifestTokens || !SessionContextIndex.FitsResource(state.Manifest(), tokens))
            return Error(SessionContextLimits.BudgetExceeded, new { maxManifestTokens = SessionContextLimits.ManifestTokens });
        foreach (var item in state.Entries)
        {
            var entry = item.Value!.AsObject();
            var parts = SessionContextIndex.Parts(Text(entry, "text"), tokens).ToArray();
            for (var part = 0; part < parts.Length; part++)
                if (!SessionContextIndex.Fits(PartData(state, entry, parts, part), SessionContextLimits.ReplyTokens - 250, tokens)
                    || !SessionContextIndex.FitsResource(PartData(state, entry, parts, part), tokens))
                    return Error(SessionContextLimits.BudgetExceeded, new { entryId = item.Key, part });
        }
        return null;
    }

    private RelayReply? Read(RelayCaller caller, JsonObject args, out SessionContextState state)
    {
        state = null!;
        if (!caller.IsInternal) return Error(RelayErrorCodes.InternalOnly);
        if (!caller.MayAccess(Text(args, "repositoryId"))) return Error(RelayErrorCodes.RepositoryDenied);
        if (!TryGetCommittedDocument(out var document)) return Error(RelayErrorCodes.StorageFailure);
        state = new(JsonNode.Parse(document.Json)!.AsObject());
        if (this.GetPrimaryKeyString() != SessionContextState.Key(Text(args, "repositoryId"), Text(args, "contextId"))
            || state.RepositoryId.Length > 0 && state.RepositoryId != Text(args, "repositoryId")) return Error(RelayErrorCodes.RepositoryDenied);
        if (state.Exists) return null;
        index?.Dispose(); index = null;
        return Error(RelayErrorCodes.NotFound);
    }

    public Task<RelayReply> SearchAsync(RelayCaller caller, string argumentsJson)
    {
        var args = JsonNode.Parse(argumentsJson)!.AsObject();
        if (Read(caller, args, out var state) is { } failure) return Task.FromResult(failure);
        try
        {
            if (index is null)
            {
                var staged = new SessionContextIndex(tokens);
                try { staged.Apply(state); index = staged; }
                catch { staged.Dispose(); throw; }
            }
            else index.Apply(state);
            return Task.FromResult(Ok(index.Search(state, Text(args, "query"), (int)Math.Clamp(Number(args, "limit", SessionContextLimits.SearchHits), 1, SessionContextLimits.SearchHits)), state.Revision));
        }
        catch (Exception)
        {
            index?.Dispose(); index = null;
            return Task.FromResult(Error(SessionContextLimits.SearchUnavailable));
        }
    }

    public Task<RelayReply> ReadPartAsync(RelayCaller caller, string argumentsJson)
    {
        var args = JsonNode.Parse(argumentsJson)!.AsObject();
        if (Read(caller, args, out var state) is { } failure) return Task.FromResult(failure);
        var entryId = Text(args, "entryId");
        if (state.Entries[entryId] is not JsonObject entry) return Task.FromResult(Error(RelayErrorCodes.NotFound));
        var version = Number(entry, "version");
        if (args["entryVersion"] is not null && (Number(args, "entryVersion") != version || Text(args, "contextGeneration") != state.Generation))
            return Task.FromResult(Error(SessionContextLimits.VersionUnavailable));
        var parts = SessionContextIndex.Parts(Text(entry, "text"), tokens).ToArray();
        var part = Number(args, "part");
        if (part < 0 || part >= parts.Length) return Task.FromResult(Error(RelayErrorCodes.NotFound));
        return Task.FromResult(Ok(PartData(state, entry, parts, (int)part), state.Revision));
    }

    private JsonObject PartData(SessionContextState state, JsonObject entry, string[] parts, int part)
    {
        var entryId = Text(entry, "entryId");
        var version = Number(entry, "version");
        var result = (JsonObject)entry.DeepClone();
        result["text"] = parts[(int)part]; result["contextId"] = state.ContextId; result["revision"] = state.Revision;
        result["part"] = part; result["partCount"] = parts.Length; result["encoding"] = tokens.Encoding;
        result["generation"] = state.Generation;
        result["uri"] = SessionContextResourceUri.PartUri(state.RepositoryId, state.ContextId, entryId, version, (int)part, state.Generation);
        if (part + 1 < parts.Length) result["nextUri"] = SessionContextResourceUri.PartUri(state.RepositoryId, state.ContextId, entryId, version, (int)part + 1, state.Generation);
        return result;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        index?.Dispose(); index = null;
        return Task.CompletedTask;
    }
}

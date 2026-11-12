using System.Text.Json.Nodes;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

public sealed record SessionContextCatalogProjection(string RepositoryId, string ContextId, long Revision, string Title, string Summary, string TaskIdsJson, string UpdatedAt, bool Deleted);

public static class SessionContextReadModels
{
    public static SessionContextCatalogProjection? Catalog(string json)
    {
        var state = new SessionContextState(JsonNode.Parse(json)!.AsObject());
        if (state.Revision == 0) return null;
        return new(state.RepositoryId, state.ContextId, state.Revision, Text(state.Json, "title"), Text(state.Json, "summary"),
            state.Json["taskIds"]?.ToJsonString() ?? "[]", Text(state.Json, "updatedAt"), !state.Exists);
    }
}

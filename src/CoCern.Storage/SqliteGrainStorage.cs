using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using CoCern.Core;
using CoCern.Domain;
using System.Text.Json;

namespace CoCern.Storage;

public sealed class SqliteGrainStorage(SqliteStore store) : IGrainStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => ReadStateAsync(stateName, grainId, grainState, CancellationToken.None);

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        var saved = await store.ReadAsync(stateName, grainId, cancellationToken);
        var state = saved?.Payload is { } payload ? JsonSerializer.Deserialize<T>(payload, JsonOptions) : Activator.CreateInstance<T>();
        grainState.State = state;
        grainState.ETag = saved?.Etag;
        grainState.RecordExists = saved?.Payload is not null;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => WriteStateAsync(stateName, grainId, grainState, CancellationToken.None);

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(grainState.State, JsonOptions);
        var due = grainState.State is IHasDueWork indexed ? indexed.GetDueWork().ToArray() : [];
        var search = grainState.State as IHasSearchDocuments;
        var archive = grainState.State as IHasTaskArchive;
        string etag;
        try { etag = await store.CommitAsync(stateName, grainId, grainState.ETag, payload, due, cancellationToken, search, archive?.PendingArchive() ?? []); }
        finally { search?.ReleaseSearchState(); }
        // Written in that commit's transaction; a failed commit keeps them, and the grain discards the whole candidate state.
        archive?.ArchiveWritten();
        grainState.ETag = etag;
        grainState.RecordExists = true;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => ClearStateAsync(stateName, grainId, grainState, CancellationToken.None);

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        var etag = await store.CommitAsync(stateName, grainId, grainState.ETag, null, [], cancellationToken);
        grainState.ETag = etag;
        grainState.RecordExists = false;
    }
}

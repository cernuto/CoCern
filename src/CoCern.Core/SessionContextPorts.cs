using CoCern.Domain;

namespace CoCern.Core;

public interface ISessionContextCatalog
{
    Task<RelayReply> ReadSessionContextsAsync(string repositoryId, string? taskId, string? cursor, int limit, CancellationToken cancellationToken = default);
}

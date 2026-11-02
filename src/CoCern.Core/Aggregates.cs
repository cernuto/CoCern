using System.Text.Json.Nodes;
using Orleans;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// How the hub reaches its aggregates: grain addressing, arguments on the wire, and the task archive behind board calls (a write to an
/// archived task answers <c>TaskArchived</c>; a read of one returns its archived copy). Tool composition never sees grains or the archive.
/// </summary>
internal sealed class Aggregates(IGrainFactory grains, ITaskArchive? archive)
{
    /// <summary><c>task_get</c>: the board's reply, or for a task the board archived, the archived copy, read-only and marked <c>archived: true</c>.</summary>
    public async Task<RelayReply> ReadTaskAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken)
    {
        var reply = await Board(caller, RelayTools.TaskGet, args);
        // The board answers TaskArchived for an ID it still remembers, and NotFound once it forgot it; the archive table has both.
        if (reply.Code is not (TaskArchived or NotFound) || archive is null || Text(args, "taskId") is not { Length: > 0 } taskId) return reply;
        string? json;
        try { json = await archive.ReadArchivedTaskAsync(Text(args, "repositoryId"), Text(args, "boardId", "default"), taskId, cancellationToken); }
        catch (Exception exception) when (IsStoreFault(exception)) { return Error(Unavailable); }
        return json is null ? reply : ArchivedTasks.Read(args, json);
    }

    /// <summary>The canonical ID of a resource name (or alias, by the args' <c>machineId</c>).</summary>
    public Task<RelayReply> Resolve(RelayCaller caller, JsonObject args, string resource)
    {
        var resolve = args.DeepClone().AsObject(); resolve["resourceId"] = resource; resolve.Remove("requestId");
        return Control(caller, Operations.Resolve, resolve);
    }

    public Task<RelayReply> Control(RelayCaller caller, string operation, JsonObject args) => Call(AggregateKinds.Control, AggregateKinds.ControlKey, caller, operation, args);
    public Task<RelayReply> Session(RelayCaller caller, string operation, JsonObject args) => Call(AggregateKinds.Session, Text(args, "sessionId"), caller, operation, args);
    /// <summary>A board call; a task write is checked against the archive first (see <see cref="CallTaskWriteAsync"/>).</summary>
    public Task<RelayReply> Board(RelayCaller caller, string operation, JsonObject args)
    {
        return archive is not null && operation is RelayTools.TaskUpdate or RelayTools.TaskClaim or RelayTools.ActionBegin or RelayTools.ActionReport or RelayTools.SendMessage or RelayTools.ClaimRenew or RelayTools.ClaimRelease
            ? CallTaskWriteAsync(caller, operation, args)
            : Call(AggregateKinds.Board, BoardKey(args), caller, operation, args);
    }

    /// <summary>
    /// A task write, checked against the archive table, which keeps every archived ID after the board forgets it: a new task under an
    /// archived ID, or a write the board answers <c>NotFound</c> for one, gets <c>TaskArchived</c>; a workflow's <c>dependsOn</c> IDs found
    /// there are passed as <c>archivedDependsOn</c>, so they count as completed. A client cannot supply <c>archivedDependsOn</c> itself.
    /// </summary>
    private async Task<RelayReply> CallTaskWriteAsync(RelayCaller caller, string operation, JsonObject args)
    {
        var (repository, board, taskId) = (Text(args, "repositoryId"), Text(args, "boardId", "default"), Text(args, "taskId"));
        try
        {
            if (operation == RelayTools.TaskUpdate && args["workflow"] is JsonObject workflow && workflow["dependsOn"] is JsonArray dependsOn)
            {
                var ids = dependsOn.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var id) ? id : "").Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxDependencies).ToArray();
                if (ids.Length > 0 && await archive!.ArchivedIdsAsync(repository, board, ids) is { Count: > 0 } found)
                    args["archivedDependsOn"] = new JsonArray(found.Order(StringComparer.Ordinal).Select(id => (JsonNode)id).ToArray());
            }
            if (taskId.Length > 0 && operation == RelayTools.TaskUpdate && ExpectedVersionMatches(args, 0) && (await archive!.ArchivedIdsAsync(repository, board, [taskId])).Count > 0)
                return Error(TaskArchived, new { taskId, archived = true });
        }
        catch (Exception exception) when (IsStoreFault(exception)) { return Error(Unavailable); }
        var reply = await Call(AggregateKinds.Board, BoardKey(args), caller, operation, args);
        if (reply.Code != NotFound || taskId.Length == 0) return reply;
        try { return (await archive!.ArchivedIdsAsync(repository, board, [taskId])).Count > 0 ? Error(TaskArchived, new { taskId, archived = true }) : reply; }
        catch (Exception exception) when (IsStoreFault(exception)) { return reply; }
    }
    /// <summary>The store behind a port is faulted or closing: the call answers Unavailable instead of failing.</summary>
    internal static bool IsStoreFault(Exception exception) => exception is System.Data.Common.DbException or ObjectDisposedException;

    public Task<RelayReply> Call(string kind, string key, RelayCaller caller, string operation, JsonObject args) => CoordinationGrain.Aggregate(grains, kind, key).ExecuteAsync(caller, operation, args.ToJsonString());

    public static string BoardKey(JsonObject args) => CallTelemetry.BoardKey(args);
    public static JsonObject Parse(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
}

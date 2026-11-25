using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Microsoft.AspNetCore.Http;

namespace CoCern.Host.Dashboard;

public static class DashboardManagerManagement
{
    private const int MaximumBodyBytes = 16_384;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 4, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    public static async Task<IResult> ReadAsync(HttpContext context, IRelayApplication application)
    {
        if (Viewer(context) is not { } principal) return Results.StatusCode(403);
        var query = context.Request.Query;
        if (query["repositoryId"].Count != 1 || !Identity(query["repositoryId"][0])) return Invalid();
        var args = new JsonObject { ["repositoryId"] = query["repositoryId"][0], ["managerHistoryOnly"] = true };
        if (query.ContainsKey("managerHistoryBefore"))
        {
            if (query["managerHistoryBefore"].Count != 1 || !long.TryParse(query["managerHistoryBefore"][0], NumberStyles.None, CultureInfo.InvariantCulture, out var before) || before < 0) return Invalid();
            args["managerHistoryBefore"] = before;
        }
        if (query.ContainsKey("managerActionsBefore"))
        {
            if (query["managerActionsBefore"].Count != 1 || !long.TryParse(query["managerActionsBefore"][0], NumberStyles.None, CultureInfo.InvariantCulture, out var before) || before < 0) return Invalid();
            args["managerActionsBefore"] = before;
        }
        var reply = await application.ExecuteAsync(new(principal, true, [query["repositoryId"][0]!]), RelayTools.GetStatus, JsonSerializer.SerializeToElement(args), context.RequestAborted);
        return Project(reply);
    }

    public static async Task<IResult> DecideAsync(HttpContext context, IRelayApplication application)
    {
        if (Viewer(context) is not { } principal || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        var (body, failure) = await BodyAsync<DashboardManagerDecision>(context);
        if (failure is not null) return failure;
        if (body is null || !Identity(body.RepositoryId) || !Identity(body.RequestId) || body.Command is not ("appoint_manager" or "clear_manager")
            || body.ExpectedManagerGeneration is null or < 0 || string.IsNullOrWhiteSpace(body.HumanWords)
            || body.HumanWords.Length > RelayLimits.MaxManagerHumanWordsLength || body.HumanWords.Any(UnsafeCharacter)
            || (body.Command == "appoint_manager" || body.TargetSessionId is not null) && !Identity(body.TargetSessionId)) return Invalid();
        var args = new JsonObject
        {
            ["repositoryId"] = body.RepositoryId, ["requestId"] = body.RequestId, ["command"] = body.Command,
            ["expectedManagerGeneration"] = body.ExpectedManagerGeneration, ["humanWords"] = body.HumanWords
        };
        // clear_manager with a session removes that one manager; without one it removes them all. Either way Paul's words travel with it.
        if (body.TargetSessionId is not null) args["targetSessionId"] = body.TargetSessionId;
        var reply = await application.ExecuteAsync(new(principal, true, [body.RepositoryId]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(args), context.RequestAborted);
        // A stale write is never resubmitted. Return a fresh redacted view with the original domain code.
        if (reply.Code == RelayErrorCodes.VersionConflict)
        {
            var result = new DashboardManagerResult(false, reply.Code, null, null);
            try
            {
                var refreshed = await ReadStatusAsync(context, application, principal, body.RepositoryId);
                if (refreshed.Ok) result = result with { Manager = refreshed.Manager, Paused = refreshed.Paused };
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or TimeoutException
                or Orleans.Runtime.OrleansMessageRejectionException or Orleans.Runtime.GatewayTooBusyException
                or Orleans.Runtime.ClientNotAvailableException or Orleans.Runtime.Messaging.ConnectionFailedException) { }
            return Results.Json(result, statusCode: ToolResults.Status(reply));
        }
        return Project(reply);
    }

    public static async Task<IResult> DispatchAsync(HttpContext context, IRelayApplication application)
    {
        if (Viewer(context) is not { } principal || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        var (body, failure) = await BodyAsync<DashboardDispatchDecision>(context);
        if (failure is not null) return failure;
        if (body is null || !Identity(body.RepositoryId) || !Identity(body.RequestId) || body.Command is not ("pause" or "stop" or "resume")) return Invalid();
        var args = new JsonObject { ["repositoryId"] = body.RepositoryId, ["requestId"] = body.RequestId, ["command"] = body.Command };
        if (body.Command == "stop") args["allSessions"] = true;
        var reply = await application.ExecuteAsync(new(principal, true, [body.RepositoryId]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(args), context.RequestAborted);
        return Project(reply);
    }

    internal static async Task<IResult> AnswerHumanBlockerAsync(HttpContext context, IGrainFactory grains, RepositoryCatalog repositories)
    {
        if (Viewer(context) is not { } principal || !DashboardSecurity.SameOrigin(context)) return Results.StatusCode(403);
        var (body, failure) = await BodyAsync<DashboardHumanAnswer>(context);
        if (failure is not null) return failure;
        if (body is null || !Identity(body.RepositoryId) || !Identity(body.BoardId) || !Identity(body.TaskId) || !Identity(body.BlockerId)
            || !Identity(body.Option) || !Identity(body.SessionId) || body.TaskVersion < 1 || !Guid.TryParseExact(body.RequestId, "N", out _))
            return Results.BadRequest(new DashboardHumanAnswerResult(false, RelayErrorCodes.InvalidArguments));
        if (repositories.Find(body.RepositoryId) is not { } repository) return Results.StatusCode(403);
        var caller = new RelayCaller(principal, true, [repository], IsInternal: true);
        // A human blocker outlives its chat, and the board does not hear of an owner that closed. An answer nobody would receive is refused
        // before it is recorded, since a recorded answer cannot be given again.
        var open = await grains.GetGrain<IControlGrain>(AggregateKinds.ControlKey).ExecuteAsync(caller, SessionsOpenQuery.Operation,
            new SessionsOpenQuery(repository, [body.SessionId]).ToJson().ToJsonString());
        if (!open.Ok) return Results.Json(new DashboardHumanAnswerResult(false, open.Code), statusCode: ToolResults.Status(open));
        using (var sessions = JsonDocument.Parse(open.DataJson))
        {
            if (!sessions.RootElement.TryGetProperty("open", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
                return Results.Json(new DashboardHumanAnswerResult(false, RelayErrorCodes.SessionUnknown), statusCode: StatusCodes.Status409Conflict);
        }
        var args = new JsonObject
        {
            ["repositoryId"] = repository, ["boardId"] = body.BoardId, ["taskId"] = body.TaskId, ["expectedVersion"] = body.TaskVersion,
            ["blockerId"] = body.BlockerId, ["option"] = body.Option, ["requestId"] = body.RequestId, ["expectedOwner"] = body.SessionId
        };
        var board = grains.GetGrain<ITaskBoardGrain>(AggregateKinds.BoardKey(repository, body.BoardId));
        var reply = await board.ExecuteAsync(caller, Operations.AnswerHumanBlocker, args.ToJsonString());
        return Results.Json(new DashboardHumanAnswerResult(reply.Ok, reply.Code, reply.Ok ? reply.Version : null), statusCode: ToolResults.Status(reply));
    }

    private static string? Viewer(HttpContext context) => DashboardSecurity.SecureTransport(context) && context.User.Identity?.IsAuthenticated == true
        && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } principal ? principal : null;
    private static bool UnsafeCharacter(char value) => char.IsControl(value) || value is '\u2028' or '\u2029';
    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(UnsafeCharacter);
    private static IResult Invalid() => Results.BadRequest(new DashboardManagerResult(false, RelayErrorCodes.InvalidArguments, null, null));

    private static async Task<(T? Body, IResult? Failure)> BodyAsync<T>(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumBodyBytes) return (default, Results.StatusCode(413));
        var buffer = new byte[MaximumBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumBodyBytes) return (default, Results.StatusCode(413));
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || DuplicateProperties(document.RootElement)) return (default, Invalid());
            return (document.RootElement.Deserialize<T>(Json), null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return (default, Invalid()); }
    }

    private static bool DuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
                if (!seen.Add(property.Name) || DuplicateProperties(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (DuplicateProperties(item)) return true;
        return false;
    }

    private static IResult Project(RelayReply reply) => Results.Json(Projection(reply), statusCode: ToolResults.Status(reply));
    private static DashboardManagerResult Projection(RelayReply reply)
    {
        using var document = JsonDocument.Parse(reply.DataJson);
        var root = document.RootElement;
        if (root.TryGetProperty("control", out var control)) root = control;
        var manager = root.TryGetProperty("manager", out var selection) ? selection.Deserialize<ManagerSelectionStatus>(Json) : null;
        bool? paused = root.TryGetProperty("paused", out var pausedValue) && pausedValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? pausedValue.GetBoolean() : null;
        var actions = root.TryGetProperty("actions", out var page) ? page.Deserialize<AdminActionPage>(Json) : null;
        return new(reply.Ok, reply.Code, manager, paused, actions);
    }

    private static async Task<DashboardManagerResult> ReadStatusAsync(HttpContext context, IRelayApplication application, string principal, string repository)
    {
        var reply = await application.ExecuteAsync(new(principal, true, [repository]), RelayTools.GetStatus,
            JsonSerializer.SerializeToElement(new { repositoryId = repository, managerHistoryOnly = true }), context.RequestAborted);
        return Projection(reply);
    }
}

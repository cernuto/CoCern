using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoCern.Contracts;
using CoCern.Core;

namespace CoCern.Host;

internal static class ErrorEventIntake
{
    private sealed record ErrorEvent(string RepositoryId, string SessionId, string SessionToken, string Source,
        string EventId, string Summary, string TargetSessionId, string? BoardId);

    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public static async Task<IResult> ReceiveAsync(HttpContext context, IRelayApplication application)
    {
        ErrorEvent? incident;
        try
        {
            incident = await JsonSerializer.DeserializeAsync<ErrorEvent>(context.Request.Body, Input, context.RequestAborted);
        }
        catch (JsonException)
        {
            return InvalidEvent();
        }
        if (incident is null || !Identifier(incident.Source) || !Identifier(incident.EventId)
            || !Identifier(incident.RepositoryId) || !Identifier(incident.SessionId) || !Identifier(incident.TargetSessionId)
            || incident.BoardId is not null && !Identifier(incident.BoardId)
            || string.IsNullOrWhiteSpace(incident.SessionToken) || string.IsNullOrWhiteSpace(incident.Summary)
            || incident.Summary.Length > 1024)
            return InvalidEvent();

        var caller = RelayAuthentication.Caller(context.User);
        var board = incident.BoardId ?? "default";
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { caller.PrincipalId, incident.Source, incident.EventId });
        var taskId = "error-" + Convert.ToHexString(SHA256.HashData(identity));
        var args = JsonSerializer.SerializeToElement(new
        {
            repositoryId = incident.RepositoryId, boardId = board, sessionId = incident.SessionId, sessionToken = incident.SessionToken,
            requestId = taskId, taskId, expectedVersion = 0,
            patch = new { title = "Error reported by " + incident.Source, errorSource = incident.Source, externalEventId = incident.EventId },
            message = new { text = incident.Summary, recipientSessionId = incident.TargetSessionId },
            workflow = new
            {
                state = "ready",
                packet = new
                {
                    goal = "Triage the reported error from " + incident.Source, target = incident.RepositoryId,
                    authorityRef = "Error report for triage", restoration = "Read-only triage",
                    stopCondition = "Obtain separate authority for changes or external actions.", resources = Array.Empty<string>()
                },
                criteria = new[] { new { id = "triage", description = "Record the finding and supporting evidence.", result = "not_run" } },
                blockers = Array.Empty<object>()
            }
        });
        // Creation and the targeted task-change notice commit together through the normal board write.
        var reply = ToolResults.Bound(await application.ExecuteAsync(caller, RelayTools.TaskUpdate, args, context.RequestAborted));
        return Results.Json(reply, statusCode: ToolResults.Status(reply));
    }

    private static bool Identifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= RelayLimits.MaxIdentifierLength && !value.Any(char.IsControl);

    private static IResult InvalidEvent() =>
        Results.Json(ToolResults.Failure(RelayErrorCodes.InvalidArguments), statusCode: StatusCodes.Status400BadRequest);
}

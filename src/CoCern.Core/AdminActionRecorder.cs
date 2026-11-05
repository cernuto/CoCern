using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// Writes each administrative call to the durable action log once its outcome is known, on the one path every surface shares, the dashboard's included.
/// Selection decisions already commit their own history with the decision; this entry attributes the call around them. A log that cannot be written
/// never undoes or blocks what was done, least of all a pause or stop: the reply says <c>actionLogged: false</c> instead.
/// </summary>
internal sealed class AdminActionRecorder(Aggregates aggregates)
{
    public async Task<RelayReply> AfterAsync(RelayCall call, RelayReply reply)
    {
        try
        {
            var report = call.Definition.Audited
                // A worker's refused attempt is never logged: any worker could otherwise push real entries out of a bounded log.
                ? call.Caller.IsAdministrator ? Administrative(call, reply) : null
                : await AssignmentAsync(call, reply);
            if (report is not null && !(await aggregates.Request(AggregateKinds.ControlKey, call.Caller, report)).Ok) return Unlogged(reply);
            return reply;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return call.Definition.Audited ? Unlogged(reply) : reply; }
    }

    private static AdminActionReport Administrative(RelayCall call, RelayReply reply)
    {
        var args = call.Args;
        var command = Text(args, "command");
        return new(Text(args, "repositoryId"), call.Tool, command.Length > 0 ? command : null, Target(call.Tool, args), Bounded(Text(args, "requestId")), reply.Code, reply.Ok,
            call.ActingSessionId, call.ManagerGeneration, reply.Ok && command is "appoint_manager" or "clear_manager" ? SelectionRef(reply) : null);
    }

    /// <summary>Handing work to a chat is not an administrator tool, so it is logged only for the session the hub verified as the seated manager.</summary>
    private async Task<AdminActionReport?> AssignmentAsync(RelayCall call, RelayReply reply)
    {
        var args = call.Args;
        if (!reply.Ok || call.Tool != RelayTools.TaskUpdate || call.Session is null || args["workflow"] is not JsonObject workflow || Text(workflow, "targetSessionId") is not { Length: > 0 } target) return null;
        var session = Text(args, "sessionId");
        var standing = await aggregates.Request(AggregateKinds.ControlKey, call.Caller, new ManagerStandingQuery(Text(args, "repositoryId"), session));
        if (!standing.Ok || !IsTrue(Aggregates.Parse(standing), "manager")) return null;
        return new(Text(args, "repositoryId"), call.Tool, "assign", CallTelemetry.Bounded(Text(args, "taskId") + " to " + target, 2 * RelayLimits.MaxIdentifierLength + 4), Bounded(Text(args, "requestId")), reply.Code, true,
            session, Number(Aggregates.Parse(standing), "generation"), null);
    }

    private static string? Target(string tool, JsonObject args) => tool switch
    {
        RelayTools.ControlRequest => args["allSessions"] is not null ? "all sessions"
            : First(args, "targetSessionId", "machineId", "resourceId", "labelPrefix") ?? (Text(args, "command") is "cancel" or "stop" ? First(args, "sessionId") : null),
        RelayTools.OrderPut or RelayTools.OrderRemove => First(args, "orderId"),
        RelayTools.DocSync => First(args, "branch"),
        RelayTools.ConnectApp => First(args, "client") is { } client
            ? Bounded(client + (args["admin"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var admin)
                ? " admin=" + (admin ? "true" : "false") : " admin=unchanged")) : null,
        _ => null
    };

    private static string? First(JsonObject args, params string[] fields) => fields.Select(field => Bounded(Text(args, field))).FirstOrDefault(value => value is not null);

    private static string? SelectionRef(RelayReply reply) =>
        Aggregates.Parse(reply)["manager"] is JsonObject manager ? "manager generation " + Number(manager, "generation") : null;

    private static string? Bounded(string value) => CallTelemetry.Bounded(value);

    private static RelayReply Unlogged(RelayReply reply)
    {
        var data = Aggregates.Parse(reply);
        data["actionLogged"] = false;
        var marked = reply with { DataJson = data.ToJsonString() };
        return FitsWire(marked) ? marked : reply;
    }
}

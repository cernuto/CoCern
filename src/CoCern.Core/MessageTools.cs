using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// Reaching other chats and answering the hub: <c>send_message</c>, the directory of chats and PCs (<c>chat_directory</c>, and
/// <c>control_request</c>'s <c>revoke_machine</c>), and <c>attention_respond</c>, a chat's answer to an attention offer.
/// </summary>
internal sealed class MessageTools(Aggregates aggregates, IMachineDirectory directory, AttentionOptions attention, CallTelemetry? telemetry)
{
    /// <summary>
    /// A private send first reads one worker entry to check the caller's access to the repository; the entries are never used, so one
    /// keeps the check while a large directory can no longer make the probe's reply too large and refuse every private message.
    /// </summary>
    private const int WorkerProbeLimit = 1;

    /// <summary>A named recipient is probed in the worker directory first; delivery itself enforces repository identity and unknown recipients stay visibly pending.</summary>
    public async Task<RelayReply> SendMessageAsync(RelayCaller caller, JsonObject args)
    {
        if (Text(args, "recipientSessionId").Length > 0)
        {
            var probe = await aggregates.Control(caller, RelayTools.WorkerList, new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["limit"] = WorkerProbeLimit });
            if (!probe.Ok) return probe;
        }
        return Text(args, "taskId").Length > 0 ? await aggregates.Board(caller, RelayTools.SendMessage, args) : await aggregates.Session(caller, RelayTools.SendMessage, args);
    }

    /// <summary>The chats on every PC, for the call's repository.</summary>
    public Task<RelayReply> DirectoryAsync(JsonObject args) => directory.DirectoryAsync([Text(args, "repositoryId")], CancellationToken.None);

    /// <summary>Revoking a machine is a discovery concern, not a control-aggregate one: it needs the machine ID and is refused for non-administrators by the discovery grain.</summary>
    public Task<RelayReply> RevokeMachineAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "machineId").Length == 0 ? Task.FromResult(Error(InvalidArguments)) : directory.RevokeAsync(caller, Text(args, "machineId"), CancellationToken.None);

    /// <summary>The agent's answer to an attention item. "Later" uses the hub's configured snooze; a client cannot set it.</summary>
    public async Task<RelayReply> RespondToAttentionAsync(RelayCaller caller, JsonObject args)
    {
        args["snoozeBoundaries"] = attention.SnoozeBoundaries;
        var reply = await aggregates.Session(caller, RelayTools.AttentionRespond, args);
        if (reply.Ok) { var data = Aggregates.Parse(reply); telemetry?.RecordOutcome(args, Text(data, "kind"), Text(data, "outcome"), Text(data, "itemId")); }
        return reply;
    }
}

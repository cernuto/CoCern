using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
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
    /// The directory first answers whether the caller may reach the repository, as a small read, and whether the session the guard just verified holds
    /// the manager's seat. Only then is the message marked, with the generation, from the hub's state and never from the call's arguments. A former
    /// manager's message simply goes unmarked. Delivery itself enforces repository identity, and unknown recipients stay visibly pending.
    /// </summary>
    public async Task<RelayReply> SendMessageAsync(RelayCaller caller, JsonObject args)
    {
        var standing = await aggregates.Request(AggregateKinds.ControlKey, caller, new ManagerStandingQuery(Text(args, "repositoryId"), Text(args, "sessionId")));
        if (!standing.Ok) return standing;
        var seat = Aggregates.Parse(standing);
        if (IsTrue(seat, "manager")) args[ServerArguments.ManagerOrigin] = Number(seat, "generation");
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
        if (!reply.Ok) return reply;
        var data = Aggregates.Parse(reply);
        // Each answered item counts once, as it would have answered alone. One that was not answered has no outcome.
        IEnumerable<JsonObject> answered = data["results"] is JsonArray results ? results.OfType<JsonObject>().Where(row => Text(row, "outcome").Length > 0) : [data];
        foreach (var one in answered) telemetry?.RecordOutcome(args, Text(one, "kind"), Text(one, "outcome"), Text(one, "itemId"));
        return reply;
    }
}

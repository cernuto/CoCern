using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

/// <summary>
/// <see cref="Operations.Dispatch"/>: a coordination grain runs its due checks and delivers its due outbox effects, removing each only
/// once its target accepted it. The hub's due-work pump sends it for one due item (<see cref="WorkId"/>); without one, all that are due go.
/// <see cref="EffectOperation"/> or <see cref="ActionKey"/> limit delivery to one operation's or one action's effects and skip the due
/// checks; no sender uses either today. Every kind of coordination grain takes it itself, before its aggregate sees it, so it has no one
/// target kind (<see cref="InternalRequests.AnyAggregate"/>) and carries no repository.
/// </summary>
public sealed record OutboxDispatch(string? WorkId = null, string? EffectOperation = null, string? ActionKey = null) : IInternalRequest<OutboxDispatch>
{
    public static string Operation => Operations.Dispatch;
    public static string TargetKind => InternalRequests.AnyAggregate;

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (WorkId is not null) json["workId"] = WorkId;
        if (EffectOperation is not null) json["operation"] = EffectOperation;
        if (ActionKey is not null) json["actionKey"] = ActionKey;
        return json;
    }

    public static Decoding<OutboxDispatch> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new OutboxDispatch(read.OptionalText("workId"), read.OptionalText("operation"), read.OptionalText("actionKey")));
    }
}

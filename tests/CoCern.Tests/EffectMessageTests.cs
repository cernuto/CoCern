using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The typed messages between aggregates: each reads back exactly what it writes, a missing or mistyped field is named (never its value),
/// and a handler given one answers <see cref="RelayErrorCodes.MalformedEffect"/> rather than a silent Ok.
/// </summary>
public sealed class EffectMessageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);

    private static T RoundTrip<T>(T message) where T : class, IEffectMessage<T>
    {
        Assert.True(T.Decode(message.ToJson()).TryGet(out var decoded, out var fields), string.Join(", ", fields));
        return decoded;
    }

    private static IReadOnlyList<string> Malformed<T>(JsonObject payload) where T : class, IEffectMessage<T>
    {
        Assert.False(T.Decode(payload).TryGet(out _, out var fields));
        return fields;
    }

    private static JsonObject Without(JsonObject payload, params string[] fields)
    {
        var copy = payload.DeepClone().AsObject();
        foreach (var field in fields) copy.Remove(field);
        return copy;
    }

    [Fact]
    public void A_board_work_report_reads_back_and_names_its_missing_fields()
    {
        var report = new ActionWorkReport("repo", "s1", "ops board", "t/1", "deploy", 2, true, Now);
        Assert.Equal(report, RoundTrip(report));
        // The action key is built from the fields, escaped as the board and its locks build it.
        Assert.Equal(CoordinationStateMachine.ActionKey(new JsonObject { ["repositoryId"] = "repo", ["boardId"] = "ops board", ["taskId"] = "t/1" }, "deploy", 2), report.ActionKey);
        Assert.Equal(["boardId", "at"], Malformed<ActionWorkReport>(Without(report.ToJson(), "boardId", "at")));
        var mistyped = report.ToJson(); mistyped["attempt"] = 0; mistyped["open"] = "yes";
        Assert.Equal(["attempt", "open"], Malformed<ActionWorkReport>(mistyped));
        Assert.Equal((Operations.SessionWork, AggregateKinds.Session, DeliveryClass.Retry), (ActionWorkReport.Operation, ActionWorkReport.TargetKind, ActionWorkReport.Delivery));
    }

    [Fact]
    public void An_ownership_report_requires_its_time_and_sequence()
    {
        var report = new SessionOwnership("repo", "s1", true, Now, 3);
        Assert.Equal(report, RoundTrip(report));
        Assert.Equal(["at", "seq"], Malformed<SessionOwnership>(Without(report.ToJson(), "at", "seq")));
        Assert.Equal((Operations.SessionOwns, AggregateKinds.Control, DeliveryClass.DropAndCount), (SessionOwnership.Operation, SessionOwnership.TargetKind, SessionOwnership.Delivery));
    }

    [Fact]
    public void Handlers_answer_a_malformed_report_with_its_field_names_and_the_directory_takes_no_board_report()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(CoordinationStateMachine.Apply(session, AggregateKinds.Session, "s1", Hub, Operations.Open, new() { ["repositoryId"] = "repo", ["sessionId"] = "s1", ["principalId"] = "alice", ["sessionToken"] = "secret" }, Now).Ok);
        var work = Without(new ActionWorkReport("repo", "s1", "default", "t", "a", 1, true, Now).ToJson(), "taskId");
        var refused = CoordinationStateMachine.Apply(session, AggregateKinds.Session, "s1", Hub, Operations.SessionWork, work, Now);
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
        Assert.Equal(["taskId"], JsonNode.Parse(refused.DataJson)!["fields"]!.AsArray().Select(field => field!.GetValue<string>()));
        Assert.Empty(session.Effects);

        var control = new StateEnvelope<CoordinationDocument>();
        var unnumbered = Without(new SessionOwnership("repo", "s1", true, Now, 1).ToJson(), "seq");
        Assert.Equal(RelayErrorCodes.MalformedEffect, CoordinationStateMachine.Apply(control, AggregateKinds.Control, AggregateKinds.ControlKey, Hub, Operations.SessionOwns, unnumbered, Now).Code);
        // The board's per-action report is for the session; the directory no longer keeps a ledger of its own.
        Assert.Equal(RelayErrorCodes.UnknownTool, CoordinationStateMachine.Apply(control, AggregateKinds.Control, AggregateKinds.ControlKey, Hub, Operations.SessionWork,
            new ActionWorkReport("repo", "s1", "default", "t", "a", 1, true, Now).ToJson(), Now).Code);
    }
}

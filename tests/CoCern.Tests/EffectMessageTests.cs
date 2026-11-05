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
    public void An_ownership_report_requires_its_time_and_sequence()
    {
        var report = new SessionOwnership("repo", "s1", true, Now, 3);
        Assert.Equal(report, RoundTrip(report));
        Assert.Equal(["at", "seq"], Malformed<SessionOwnership>(Without(report.ToJson(), "at", "seq")));
        Assert.Equal((Operations.SessionOwns, AggregateKinds.Control, DeliveryClass.DropAndCount), (SessionOwnership.Operation, SessionOwnership.TargetKind, SessionOwnership.Delivery));
    }

    private static SessionRecord Record(JsonObject json)
    {
        Assert.True(SessionRecord.Decode(json).TryGet(out var record, out var fields), string.Join(", ", fields));
        return record;
    }

    [Fact]
    public void A_session_and_its_directory_entry_hold_one_record_whichever_copy_of_its_open_lands_first()
    {
        foreach (var role in new[] { "manager", new string('m', RelayLimits.MaxActivityRoleLength + 1) })
        {
            var hub = new AggregateHub { HoldDirectCalls = true };
            var chat = hub.Open("chat", Now, machineId: "pc-1", role: role);
            var drained = hub.Explore(Now, state =>
            {
                var session = Record(state.State(AggregateKinds.Session, chat.Id));
                Assert.Equal(Record(state.Directory()[chat.Id]!.AsObject()).ToJson().ToJsonString(), session.ToJson().ToJsonString());
                Assert.Equal(role.Length <= RelayLimits.MaxActivityRoleLength ? role : null, session.Role);
            });
            Assert.True(drained > 1);
        }
    }

    [Fact]
    public void A_session_closed_before_it_opened_tells_a_board_whose_task_targets_it()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        var directory = new RelayCaller("relay-directory", false, ["repo"], true);
        Assert.True(CoordinationStateMachine.Apply(session, "session", "s1", directory, Operations.SessionExpire, new SessionExpire("repo", "s1", 1).ToJson(), Now).Ok);
        var watch = CoordinationStateMachine.Apply(session, "session", "s1", Hub, Operations.SessionTargetWatch, new SessionTargetWatch("repo", "s1", "repo/ops", "ops").ToJson(), Now);
        Assert.True(JsonNode.Parse(watch.DataJson)!["closed"]!.GetValue<bool>());
        var told = Assert.Single(session.Effects, effect => effect.Operation == SessionClosed.Operation);
        Assert.Equal(("repo/ops", new SessionClosed("repo", "ops", "s1")), (told.TargetKey, RoundTrip(SessionClosed.Decode(JsonNode.Parse(told.PayloadJson)!.AsObject()).Message!)));

        // A session never opened or closed here is still left alone.
        var unknown = new StateEnvelope<CoordinationDocument>();
        var ignored = CoordinationStateMachine.Apply(unknown, "session", "s2", Hub, Operations.SessionTargetWatch, new SessionTargetWatch("repo", "s2", "repo/ops", "ops").ToJson(), Now);
        Assert.False(JsonNode.Parse(ignored.DataJson)!["watching"]!.GetValue<bool>());
        Assert.Empty(unknown.Effects);
    }

}

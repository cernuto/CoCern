using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Each aggregate's state class declares every key its document stores. These tests build each document through
/// <see cref="CoordinationStateMachine.Apply"/>, as the hub does, and check it holds no key its shape leaves out, so a handler that
/// writes a new key must name it in its state class first.
/// </summary>
public sealed class StateShapeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Monitor = new("relay-anomaly-monitor", false, ["repo"], true);

    private static JsonObject Document(StateEnvelope<CoordinationDocument> envelope) => JsonNode.Parse(envelope.Value.Json)!.AsObject();

    [Fact]
    public void A_shape_names_each_key_it_does_not_declare_by_its_path()
    {
        var shape = StateShape.Object(("a", null), ("tasks", StateShape.Map(StateShape.Object(("version", null)))), ("binding", StateShape.Any));
        var json = JsonNode.Parse("""{"a":1,"b":2,"tasks":{"T-1":{"version":1,"stray":true}},"binding":{"anything":{"goes":1}}}""")!;
        Assert.Equal(["b", "tasks.*.stray"], shape.Undeclared(json));
    }

    [Fact]
    public void The_anomaly_monitor_stores_only_the_keys_its_state_declares()
    {
        var envelope = new StateEnvelope<CoordinationDocument>();
        RelayReply Apply<T>(T request, DateTimeOffset at) where T : class, IInternalRequest<T> =>
            CoordinationStateMachine.Apply(envelope, T.TargetKind, "repo", Monitor, T.Operation, request.ToJson(), at);
        Assert.True(Apply(new AnomalyStart("repo"), Now).Ok);
        Assert.True(Apply(new AnomalySchedule("repo", 3600), Now).Ok);
        var raise = new AnomalyFinding(AnalyticsQueryNames.TokensPerTask, "2026-11-03", new AnomalyRow("all", 5400, 2100, 12));
        Assert.True(Apply(new AnomalyCheck("repo", Now, [raise]), Now).Ok);
        var back = new AnomalyFinding(AnalyticsQueryNames.TokensPerTask, "2026-11-04", InRange: true);
        Assert.True(Apply(new AnomalyCheck("repo", Now.AddDays(1), [back]), Now.AddDays(1)).Ok);

        var state = new AnomalyState(Document(envelope));
        Assert.Equal((Now.AddHours(1), Now, Now.AddDays(1)), (state.NextCheckAt, state.CheckedAt, state.FindingsAt));
        Assert.False(state.Check(AnalyticsQueryNames.TokensPerTask).Open);
        Assert.Empty(AnomalyState.Shape.Undeclared(Document(envelope)));
        // The shape is not vacuous: every key the monitor wrote is one it declares.
        Assert.Contains(AnomalyState.ChecksKey, Document(envelope).Select(entry => entry.Key));
    }

    [Fact]
    public void Anomaly_requests_read_back_and_name_what_they_are_missing()
    {
        var check = new AnomalyCheck("repo", Now, [new AnomalyFinding(AnalyticsQueryNames.TokensPerTask, "2026-11-03", new AnomalyRow("all", 5400.5, 2100, 12)), new AnomalyFinding(AnalyticsQueryNames.CallsPerTask, "2026-11-03")]);
        Assert.True(AnomalyCheck.Decode(check.ToJson()).TryGet(out var decoded, out _));
        Assert.Equal(check.ToJson().ToJsonString(), decoded.ToJson().ToJsonString());
        var missing = check.ToJson();
        missing.Remove("checkAt");
        missing["findings"]![0]!["anomaly"]!.AsObject().Remove("samples");
        Assert.False(AnomalyCheck.Decode(missing).TryGet(out _, out var fields));
        Assert.Equal(["findings.anomaly.samples", "checkAt"], fields);
        Assert.False(AnomalySchedule.Decode(new JsonObject { ["repositoryId"] = "repo" }).TryGet(out _, out fields));
        Assert.Equal(["intervalSeconds"], fields);

        // The monitor answers a malformed request by name, and changes nothing.
        var envelope = new StateEnvelope<CoordinationDocument>();
        var refused = CoordinationStateMachine.Apply(envelope, AggregateKinds.Anomaly, "repo", Monitor, Operations.AnomalyCheck, missing, Now);
        Assert.Equal(RelayErrorCodes.MalformedEffect, refused.Code);
    }
}

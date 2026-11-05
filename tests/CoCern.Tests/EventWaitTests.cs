using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public sealed class EventWaitTests
{
    [Fact]
    public void Request_replay_cannot_change_its_target_or_emit_another_notice()
    {
        var at = DateTimeOffset.UtcNow; var hub = new AggregateHub(); var owner = hub.Open("owner", at); hub.DeliverAll(at);
        var request = new EventWaitRequest("wait", "task_completed", "repo/default/task", "", at.AddMinutes(10), BaselineRevision: 0, QualifyingData: "completed");
        var args = new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = owner.Id, ["sessionToken"] = owner.Token,
            ["requestId"] = "register", ["request"] = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        RelayReply Apply(JsonObject input) => hub.Apply("session", owner.Id, EventWaitAggregate.RegisterOperation, input,
            AggregateHub.Alice with { IsInternal = true }, at);
        var first = Apply(args); Assert.True(first.Ok);
        var before = hub.State("session", owner.Id).ToJsonString();
        var effects = hub.Of("session", owner.Id).Effects.Count;
        var changed = (JsonObject)args.DeepClone(); changed["requestId"] = "changed";
        changed["request"] = JsonSerializer.SerializeToNode(request with { Target = "another/task" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("RequestConflict", Apply(changed).Code);
        Assert.Equal(before, hub.State("session", owner.Id).ToJsonString());
        Assert.Equal(first.DataJson, Apply(args).DataJson);
        Assert.Equal(effects, hub.Of("session", owner.Id).Effects.Count);
    }
}

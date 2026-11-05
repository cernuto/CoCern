using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

// Verify stored keys, including nested and detached records, without requiring fixtures to populate every optional field.
internal static class ExhaustiveStateFixture
{
    public static void Verify(StateShape shape, IReadOnlyList<JsonObject> documents,
        IReadOnlyList<(StateShape Shape, JsonObject Document)>? detached = null)
    {
        foreach (var document in documents) Assert.Empty(shape.Undeclared(document));
        foreach (var (recordShape, document) in detached ?? []) Assert.Empty(recordShape.Undeclared(document));
    }

    public static void ExerciseSourceRetirement(Func<int, EventWaitSourceWatch> watch,
        Func<string, JsonObject, RelayReply> apply)
    {
        for (var index = 0; index < 129; index++)
        {
            var payload = JsonSerializer.SerializeToNode(watch(index), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            Assert.True(apply(EventWaitSources.SourceWatchOperation, payload).Ok);
            Assert.True(apply(EventWaitSources.SourceUnwatchOperation, payload).Ok);
        }
    }

}

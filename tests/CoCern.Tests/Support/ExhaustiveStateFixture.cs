using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

// One exhaustive verifier for the session, lock and board fixtures. Each keeps
// its real operation-based setup and typed reads; schema exclusions stay explicit.
internal static class ExhaustiveStateFixture
{
    public static IEnumerable<string> Keys(JsonNode? node) => node is JsonObject json ? json.Select(entry => entry.Key) : [];
    public static IEnumerable<string> EntryKeys(JsonObject state, string map) =>
        state[map] is JsonObject entries ? entries.SelectMany(entry => Keys(entry.Value)) : [];

    public static void Verify(StateShape shape, IReadOnlyList<JsonObject> documents,
        IReadOnlyList<(StateShape Shape, JsonObject Document)>? detached = null, bool recursiveCoverage = false)
    {
        foreach (var document in documents) Assert.Empty(shape.Undeclared(document));
        foreach (var (recordShape, document) in detached ?? []) Assert.Empty(recordShape.Undeclared(document));
        if (!recursiveCoverage)
        {
            Assert.Equal(shape.Fields.Keys.Except([ReadModels.RequestTombstonesKey]).Order(StringComparer.Ordinal),
                documents.SelectMany(Keys).Distinct().Order(StringComparer.Ordinal));
            return;
        }
        var coverage = new Coverage();
        foreach (var document in documents) coverage.Walk(shape, document);
        foreach (var (recordShape, document) in detached ?? []) coverage.Walk(recordShape, document);
        Assert.Equal([ReadModels.RequestTombstonesKey], coverage.Missing(shape));
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

    private sealed class Coverage
    {
        private readonly Dictionary<StateShape, HashSet<string>> seen = new(ReferenceEqualityComparer.Instance);
        private HashSet<string> Seen(StateShape shape) => seen.TryGetValue(shape, out var keys) ? keys : seen[shape] = new(StringComparer.Ordinal);
        public void Walk(StateShape? shape, JsonNode? node)
        {
            if (shape is null || shape.Opaque || node is null) return;
            if (node is JsonArray array) { foreach (var item in array) Walk(shape, item); return; }
            if (node is not JsonObject json) return;
            foreach (var (key, value) in json)
            {
                if (shape.KeyedByIds) { Walk(shape.Entries, value); continue; }
                Seen(shape).Add(key);
                if (shape.Fields.TryGetValue(key, out var child)) Walk(child, value);
            }
        }
        public IReadOnlyList<string> Missing(StateShape root)
        {
            var visited = new HashSet<StateShape>(ReferenceEqualityComparer.Instance);
            var missing = new List<string>();
            void Visit(StateShape? shape, string path)
            {
                if (shape is null || shape.Opaque || !visited.Add(shape)) return;
                if (shape.KeyedByIds) { Visit(shape.Entries, Join(path, "*")); return; }
                foreach (var (key, child) in shape.Fields)
                {
                    if (!Seen(shape).Contains(key)) missing.Add(Join(path, key));
                    Visit(child, Join(path, key));
                }
            }
            Visit(root, ""); return missing;
        }
        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;
    }
}

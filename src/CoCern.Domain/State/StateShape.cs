using System.Text.Json.Nodes;

namespace CoCern.Domain.State;

/// <summary>
/// The keys one stored object may hold, and the shape under each. An aggregate's state class declares its document's shape here, beside
/// the typed accessors its handlers use, so every key the aggregate stores is named once; a test then builds the state through
/// <see cref="CoordinationStateMachine.Apply"/> and checks that it holds no key the shape leaves out (<see cref="Undeclared"/>).
/// </summary>
public sealed class StateShape
{
    private StateShape(IReadOnlyDictionary<string, StateShape?> fields, StateShape? entries, bool opaque, bool keyedByIds)
    {
        Fields = fields;
        Entries = entries;
        Opaque = opaque;
        KeyedByIds = keyedByIds;
    }

    /// <summary>The named keys an object holds, each with the shape of its value: null for a scalar, a list of scalars, or any value the shape does not look into.</summary>
    public IReadOnlyDictionary<string, StateShape?> Fields { get; }

    /// <summary>Whether the object is keyed by IDs (tasks by task ID, sessions by session ID): its keys are data, not names.</summary>
    public bool KeyedByIds { get; }

    /// <summary>For an object keyed by IDs, the shape of each entry: null for a scalar.</summary>
    public StateShape? Entries { get; }

    /// <summary>A value the aggregate stores as it was given (a client's binding, a stored reply), whose keys are not the aggregate's to name.</summary>
    public bool Opaque { get; }

    /// <summary>An object with these named keys.</summary>
    public static StateShape Object(params (string Key, StateShape? Shape)[] fields) =>
        new(fields.ToDictionary(field => field.Key, field => field.Shape, StringComparer.Ordinal), null, false, false);

    /// <summary>An object keyed by IDs, each entry of <paramref name="entry"/>'s shape.</summary>
    public static StateShape Map(StateShape? entry) => new(new Dictionary<string, StateShape?>(), entry, false, true);

    /// <summary>A value whose keys the shape does not check.</summary>
    public static StateShape Any { get; } = new(new Dictionary<string, StateShape?>(), null, true, false);

    /// <summary>The same shape with more named keys, for a document that adds its own keys to the ones every aggregate keeps.</summary>
    public StateShape With(params (string Key, StateShape? Shape)[] fields) =>
        new(Fields.Concat(fields.Select(field => KeyValuePair.Create(field.Key, field.Shape))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), Entries, Opaque, KeyedByIds);

    /// <summary>
    /// The paths (<c>checks.*.open</c>) of every key in <paramref name="json"/> this shape does not declare. Arrays are checked element by
    /// element against the shape given for the array.
    /// </summary>
    public IReadOnlyList<string> Undeclared(JsonNode? json)
    {
        var found = new List<string>();
        Walk(this, json, "", found);
        return found;
    }

    private static void Walk(StateShape? shape, JsonNode? node, string path, List<string> found)
    {
        if (shape is null || shape.Opaque || node is null) return;
        if (node is JsonArray array)
        {
            foreach (var item in array) Walk(shape, item, path, found);
            return;
        }
        if (node is not JsonObject json) return;
        foreach (var (key, value) in json)
        {
            if (shape.KeyedByIds)
            {
                Walk(shape.Entries, value, Join(path, "*"), found);
                continue;
            }
            if (!shape.Fields.TryGetValue(key, out var child)) { found.Add(Join(path, key)); continue; }
            Walk(child, value, Join(path, key), found);
        }
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;
}

/// <summary>Keys every aggregate's document holds beside its own, written by <see cref="CoordinationStateMachine.Apply"/>.</summary>
public static class CommonState
{
    public const string RepositoryIdKey = "repositoryId";

    /// <summary>
    /// The shape every aggregate's document starts from: its repository, and the request replay caches, which hold earlier replies as they
    /// were sent (<see cref="ReadModels.RequestsKey"/>, <see cref="ReadModels.RequestTombstonesKey"/>).
    /// </summary>
    public static StateShape Shape { get; } = StateShape.Object(
        (RepositoryIdKey, null), (ReadModels.RequestsKey, StateShape.Any), (ReadModels.RequestTombstonesKey, StateShape.Any));
}

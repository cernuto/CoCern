using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain;

/// <summary>Tolerant readers over the JSON state documents and tool arguments. Missing or mistyped fields read as the fallback.</summary>
public static class JsonFields
{
    public static string Text(JsonObject node, string key, string fallback = "") => node[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : fallback;
    public static long Number(JsonObject node, string key, long fallback = 0) => long.TryParse(node[key]?.ToString(), out var value) ? value : fallback;
    public static bool Flag(JsonObject node, string key) => node[key]?.GetValue<bool>() ?? false;
    public static DateTimeOffset Date(JsonObject node, string key) => DateTimeOffset.TryParse(Text(node, key), out var result) ? result : DateTimeOffset.MinValue;

    /// <summary>A nonempty identifier within the shared length bound; otherwise the operation fails as invalid arguments.</summary>
    public static string Required(JsonObject node, string key) => Text(node, key) is { Length: > 0 and <= RelayLimits.MaxIdentifierLength } value ? value : throw new ArgumentException(key + " is required and at most 256 characters.");

    /// <summary>The child object at <paramref name="key"/>, created when absent.</summary>
    public static JsonObject ObjectAt(JsonObject node, string key) { if (node[key] is not JsonObject result) node[key] = result = new(); return result; }

    /// <summary>The child array at <paramref name="key"/>, created when absent.</summary>
    public static JsonArray ArrayAt(JsonObject node, string key) { if (node[key] is not JsonArray result) node[key] = result = new(); return result; }

    public static IEnumerable<string> Strings(JsonNode? node) => (node as JsonArray ?? new()).Select(x => x!.GetValue<string>());

    public static bool ExpectedVersionMatches(JsonObject args, long current) => args.ContainsKey("expectedVersion") && Number(args, "expectedVersion") == current;

    public static int ByteSize(JsonNode? node) => Encoding.UTF8.GetByteCount(node?.ToJsonString() ?? "null");

    public static bool SecretEquals(string stored, string supplied) => stored.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(supplied));
}

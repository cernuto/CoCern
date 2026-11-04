using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoCern.Contracts;

/// <summary>
/// <c>local-hub.json</c>, beside the hub's database: where this PC's apps reach the hub over pinned loopback HTTPS (<see cref="Url"/>)
/// and the certificate to pin (<see cref="Fingerprint"/>). The hub writes it when configuring local HTTPS; Connect reads it. It holds no secret.
/// </summary>
public sealed record LocalHubRecord(string Url, string Fingerprint)
{
    /// <summary>Optional configured loopback HTTP origin for same-PC chats.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(OptionalHttpUrlConverter))]
    public string? HttpUrl { get; init; }

    private sealed class OptionalHttpUrlConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    public const string FileName = "local-hub.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The record in <paramref name="folder"/>, or null when there is none or it cannot be read; callers still check its values.</summary>
    public static LocalHubRecord? Read(string folder)
    {
        try { return JsonSerializer.Deserialize<LocalHubRecord>(File.ReadAllText(Path.Combine(folder, FileName)), Options) is { Url: not null, Fingerprint: not null } record ? record : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
}

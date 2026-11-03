using System.Text.Json;

namespace CoCern.Contracts;

/// <summary>
/// <c>local-hub.json</c>, beside the hub's database: where this PC's apps reach the hub over pinned loopback HTTPS (<see cref="Url"/>)
/// and the certificate to pin (<see cref="Fingerprint"/>). The hub rewrites it at every start; Connect reads it. It holds no secret.
/// </summary>
public sealed record LocalHubRecord(string Url, string Fingerprint)
{
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

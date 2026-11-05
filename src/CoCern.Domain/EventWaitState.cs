using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoCern.Domain;

internal sealed class EventWaitState(JsonObject state)
{
    internal const int MaxActive = 64;
    internal const int MaxRetained = 256;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public JsonObject? Entries => state["eventWaits"] switch
    {
        null => null,
        JsonObject entries => entries,
        _ => throw new InvalidOperationException("Invalid event wait state")
    };
    public JsonObject CreateEntries()
    {
        if (state["eventWaits"] is not null && Entries is null) throw new InvalidOperationException("Invalid event wait state");
        return Entries ?? (JsonObject)(state["eventWaits"] = new JsonObject());
    }
    public static EventWaitRequest Request(JsonObject entry) =>
        entry["request"]?.Deserialize<EventWaitRequest>(Json) ?? throw new InvalidOperationException("Invalid event wait record");
}

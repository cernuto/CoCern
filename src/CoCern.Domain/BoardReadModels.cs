using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Domain;

/// <summary>
/// The dashboard's standing panel for a stored board: its standing preferences (<see cref="StandingPreferences"/>) and its standing
/// orders (<see cref="StandingOrders"/>). It reads the keys those write, so storage extracts only the two sub-documents the board keeps
/// them in and asks here, instead of knowing their layout. Every text it returns is <see cref="DashboardText.Safe"/>.
/// </summary>
public static class BoardReadModels
{
    /// <summary>The board's top-level key for its standing preferences: lines, grants, settings, proposals and history.</summary>
    public const string StandingKey = "standing";

    /// <summary>The board's top-level key for its standing orders (<c>items</c>) and their <c>log</c>.</summary>
    public const string OrdersKey = "orders";

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 64 };

    /// <summary>
    /// One board's panel, from its grain key and its stored <see cref="StandingKey"/> and <see cref="OrdersKey"/> sub-documents (JSON text,
    /// null when the board has none): lines, grants and pending proposals with their provenance, whether each proposal would replace a
    /// line or grant, the board's settings and change history; its orders and order log. History and log are newest first, at most
    /// <see cref="RelayLimits.StandingHistoryEntries"/> and <see cref="RelayLimits.OrderLogEntries"/> entries. A value of the wrong type
    /// reads as empty: a line, grant, proposal, order or action that is not an object is left out, and a history or log entry that is not
    /// one shows as an empty entry.
    /// </summary>
    /// <exception cref="JsonException">A sub-document is not JSON, or nests deeper than 64 levels.</exception>
    public static DashboardStanding Standing(string boardKey, string? standing, string? orders)
    {
        var board = ProjectStanding(boardKey, standing ?? "{}");
        return board with { Orders = ProjectOrders(orders ?? "{}", out var log), OrderLog = log };
    }

    private static DashboardStanding ProjectStanding(string key, string json)
    {
        using var document = JsonDocument.Parse(json, Options);
        var standing = document.RootElement;
        var lines = Values(Property(standing, "lines")).Select(line => new DashboardStandingLine
        {
            Id = Text(line, "id"), Text = Text(line, "text"), Author = Text(line, "author"), ApprovedBy = Text(line, "approvedBy"), ApprovedAt = Date(line, "approvedAt")
        }).ToArray();
        var grants = Values(Property(standing, "grants")).Select(grant => new DashboardStandingGrant
        {
            Id = Text(grant, "id"), Scope = Text(grant, "scope"), Targets = Strings(Property(grant, "targets")), ExpiresAt = Date(grant, "expiresAt"),
            Grantor = Text(grant, "grantor"), ProposedBy = Text(grant, "proposedBy"), GrantedAt = Date(grant, "grantedAt")
        }).ToArray();
        var proposals = Values(Property(standing, "proposals")).Select(proposal =>
        {
            var line = Property(proposal, "line");
            var grant = Property(proposal, "grant");
            var item = line.ValueKind == JsonValueKind.Object ? line : grant;
            // Approving a proposal writes the line or grant under its own ID, or under the proposal's when it names none.
            var id = Text(item, "id") is { Length: > 0 } named ? named : Text(proposal, "id");
            return new DashboardStandingProposal
            {
                Id = Text(proposal, "id"), Kind = line.ValueKind == JsonValueKind.Object ? "line" : "grant",
                Summary = line.ValueKind == JsonValueKind.Object ? Text(line, "text") : $"{Text(grant, "scope")}: {string.Join(", ", Strings(Property(grant, "targets")))} until {Text(grant, "expiresAt")}",
                ProposedBy = Text(proposal, "proposedBy"), ProposedAt = Date(proposal, "proposedAt"),
                Replaces = line.ValueKind == JsonValueKind.Object ? lines.Any(x => x.Id == id) : grants.Any(x => x.Id == id)
            };
        }).ToArray();
        var history = Property(standing, "history") is { ValueKind: JsonValueKind.Array } changes
            ? changes.EnumerateArray().Reverse().Take(RelayLimits.StandingHistoryEntries).Select(change => new DashboardStandingChange
            {
                Revision = Long(Property(change, "revision")), By = Text(change, "by"), At = Date(change, "at"), Changes = Strings(Property(change, "changes"))
            }).ToArray()
            : [];
        return new()
        {
            Board = DashboardText.Safe(Uri.UnescapeDataString(key)), Revision = Long(Property(standing, "revision")),
            RequireResources = Property(Property(standing, "settings"), "requireResources").ValueKind == JsonValueKind.True,
            Lines = lines, Grants = grants, Proposals = proposals, History = history
        };
    }

    /// <summary>A board's orders (trigger, actions, approval, expiry, runs, failures) and its order log, newest first.</summary>
    private static DashboardOrder[] ProjectOrders(string json, out DashboardOrderLogEntry[] log)
    {
        using var document = JsonDocument.Parse(json, Options);
        var orders = document.RootElement;
        log = Property(orders, "log") is { ValueKind: JsonValueKind.Array } entries
            ? entries.EnumerateArray().Reverse().Take(RelayLimits.OrderLogEntries).Select(entry => new DashboardOrderLogEntry { At = Date(entry, "at"), OrderId = Text(entry, "orderId"), What = Text(entry, "what") }).ToArray()
            : [];
        return Values(Property(orders, "items")).Select(order =>
        {
            var trigger = Property(order, "trigger");
            var on = Text(trigger, "on");
            var actions = Property(order, "actions") is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Where(action => action.ValueKind == JsonValueKind.Object).Select(action => string.Join(" ", new[] { Text(action, "do"), Text(action, "taskId"), Text(action, "sessionId") is { Length: > 0 } session ? "to " + session : "" }.Where(part => part.Length > 0))).ToArray()
                : [];
            return new DashboardOrder
            {
                Id = Text(order, "id"), Trigger = on, Target = Text(trigger, on == OrderTriggers.ClaimReleased ? "resourceId" : "taskId"), Actions = actions,
                ApprovedBy = Text(order, "approvedBy"), ExpiresAt = Date(order, "expiresAt"), Runs = Long(Property(order, "runs")), Failures = Long(Property(order, "failures")),
                Disabled = Property(order, "disabled").ValueKind == JsonValueKind.True, LastError = Text(order, "lastError")
            };
        }).ToArray();
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;

    private static string Text(JsonElement value, string name) { var item = Property(value, name); return item.ValueKind == JsonValueKind.String ? DashboardText.Safe(item.GetString()!) : ""; }

    private static DateTimeOffset? Date(JsonElement value, string name) { var item = Property(value, name); return item.ValueKind == JsonValueKind.String && item.TryGetDateTimeOffset(out var date) ? date : null; }

    private static long Long(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    /// <summary>The object values of a JSON object (a map keyed by ID); anything else holds none.</summary>
    private static IEnumerable<JsonElement> Values(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(property => property.Value).Where(item => item.ValueKind == JsonValueKind.Object).ToArray() : [];

    private static string[] Strings(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => DashboardText.Safe(item.GetString()!)).ToArray() : [];
}

using System.Text.Json;
using PkRelay.Contracts;
using PkRelay.Contracts.Dashboard;
using PkRelay.Core;

namespace PkRelay.Storage;

/// <summary>The dashboard's read-only view of each board's standing preferences (see docs/collaboration.md).</summary>
public sealed partial class SqliteStore
{
    private const int MaximumStandingBoards = 20;

    public async Task<DashboardStandingObservation?> ReadStandingAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<(string Key, string Json, string Orders)>();
        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // SQLite extracts only the standing and orders sub-documents, so a large board is never copied out whole.
            using var command = Command("""
                SELECT grain_key, standing, orders FROM (
                    SELECT grain_key, json_extract(document, '$.standing') AS standing, json_extract(document, '$.orders') AS orders FROM (
                        SELECT grain_key, json_extract(payload, '$.Value.Json') AS document
                        FROM grain_state WHERE state_name='board' AND grain_type='pkrelay.board' AND json_valid(payload) AND (instr(payload, 'standing') > 0 OR instr(payload, 'orders') > 0)))
                WHERE (standing IS NOT NULL OR orders IS NOT NULL) AND coalesce(length(standing), 0) + coalesce(length(orders), 0) <= $maximum ORDER BY grain_key LIMIT $limit;
                """);
            command.Parameters.AddWithValue("$maximum", MaximumStateBytes);
            command.Parameters.AddWithValue("$limit", MaximumStandingBoards + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "{}" : reader.GetString(1), reader.IsDBNull(2) ? "{}" : reader.GetString(2)));
        }
        finally { _gate.Release(); }

        var boards = new List<DashboardStanding>();
        foreach (var (key, json, orders) in rows.Take(MaximumStandingBoards))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (ProjectStanding(key, json) is { } board) boards.Add(board with { Orders = ProjectOrders(orders, out var log), OrderLog = log }); }
            catch (JsonException) { }
        }
        return new() { Boards = boards.ToArray(), Truncated = rows.Count > MaximumStandingBoards };
    }

    private static DashboardStanding? ProjectStanding(string key, string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
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
            Board = Safe(Uri.UnescapeDataString(key)), Revision = Long(Property(standing, "revision")),
            RequireResources = Flag(Property(standing, "settings"), "requireResources"), Lines = lines, Grants = grants, Proposals = proposals, History = history
        };
    }

    /// <summary>A board's orders (trigger, actions, approval, expiry, runs, failures) and its order log, newest first.</summary>
    private static DashboardOrder[] ProjectOrders(string json, out DashboardOrderLogEntry[] log)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
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

    private static long Long(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static IEnumerable<JsonElement> Values(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(property => property.Value).Where(item => item.ValueKind == JsonValueKind.Object).ToArray() : [];

    private static string[] Strings(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => Safe(item.GetString()!)).ToArray() : [];
}

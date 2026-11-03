using System.Text.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Storage;

/// <summary>The dashboard's read-only view of each board's standing preferences (see docs/reference.md#standing).</summary>
public sealed partial class SqliteStore
{
    private const int MaximumStandingBoards = 20;

    /// <summary>
    /// Each board's standing and orders sub-documents, under the keys the Domain names. SQLite extracts only those, so a large board is
    /// never copied out whole, and the Domain projects them (<see cref="BoardReadModels.Standing"/>). A test checks each JSON path here
    /// against a board the Domain built.
    /// </summary>
    internal const string StandingQuery = $"""
        SELECT grain_key, standing, orders FROM (
            SELECT grain_key, json_extract(document, '$.{BoardReadModels.StandingKey}') AS standing, json_extract(document, '$.{BoardReadModels.OrdersKey}') AS orders FROM (
                SELECT grain_key, json_extract(payload, '$.Value.Json') AS document
                FROM grain_state WHERE state_name='{AggregateKinds.Board}' AND grain_type='cocern.board' AND json_valid(payload) AND (instr(payload, '{BoardReadModels.StandingKey}') > 0 OR instr(payload, '{BoardReadModels.OrdersKey}') > 0)))
        WHERE (standing IS NOT NULL OR orders IS NOT NULL) AND coalesce(length(standing), 0) + coalesce(length(orders), 0) <= $maximum ORDER BY grain_key LIMIT $limit;
        """;

    public async Task<DashboardStandingObservation?> ReadStandingAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<(string Key, string? Standing, string? Orders)>();
        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = Command(StandingQuery);
            command.Parameters.AddWithValue("$maximum", MaximumStateBytes);
            command.Parameters.AddWithValue("$limit", MaximumStandingBoards + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        finally { _gate.Release(); }

        var boards = new List<DashboardStanding>();
        foreach (var (key, standing, orders) in rows.Take(MaximumStandingBoards))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A board whose sub-documents are not JSON is left out; the others still show.
            try { boards.Add(BoardReadModels.Standing(key, standing, orders)); }
            catch (JsonException) { }
        }
        return new() { Boards = boards.ToArray(), Truncated = rows.Count > MaximumStandingBoards };
    }
}

using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Storage;

/// <summary>The dashboard's read-only view of each board's standing preferences (see docs/reference.md#standing).</summary>
public sealed partial class SqliteStore
{
    private const int MaximumStandingBoards = 20;
    private const int PendingStandingPageSize = 8;

    // Empty and unchanged-reserved boards are excluded before the limit. Revision changes can be unrelated to a shown
    // effect; page through those candidates until the domain finds eligible terms, so they cannot hide a later board.
    private const string PendingStandingQuery = """
        WITH boards AS (
            SELECT grain_key, json_extract(json_extract(payload, '$.Value.Json'), '$.standing') AS standing
            FROM grain_state
            WHERE state_name='board' AND grain_type='cocern.board' AND json_valid(payload)
                AND substr(grain_key, 1, length($prefix)) = $prefix
        ), pending AS (
            SELECT grain_key, standing, proposal.key AS proposal_id, json_extract(proposal.value, '$.proposedAt') AS proposed_at
            FROM boards, json_each(CASE WHEN json_valid(standing) THEN standing ELSE '{}' END, '$.proposals') AS proposal
            WHERE length(standing) <= $maximum AND proposal.type = 'object'
                AND json_extract(proposal.value, '$.goalTaskId') IS NULL
                AND (coalesce(json_extract(proposal.value, '$.presentedBy'), '') = ''
                    OR (coalesce(json_extract(proposal.value, '$.presentedRevision'), 0) BETWEEN 0 AND coalesce(json_extract(standing, '$.revision'), 0)
                        AND (json_extract(proposal.value, '$.presentedRevision') != coalesce(json_extract(standing, '$.revision'), 0)
                            OR ($request != '' AND json_extract(proposal.value, '$.presentedBy') = $session
                                AND json_extract(proposal.value, '$.presentedPrincipal') = $principal
                                AND json_extract(proposal.value, '$.presentedRequest') = $request))))
        )
        SELECT grain_key, standing, proposal_id FROM pending
        ORDER BY proposed_at, grain_key, proposal_id LIMIT $limit OFFSET $offset;
        """;

    public async Task<IReadOnlyList<StandingPresentation>?> ReadPendingStandingAsync(string repositoryId, string principalId, string sessionId, string requestCorrelation, CancellationToken cancellationToken = default)
    {
        // A human-active boundary must serialize with ordinary session writes, not lose its one opportunity to a busy snapshot.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<StandingPresentation>();
            var prefix = Uri.EscapeDataString(repositoryId) + "/";
            for (var offset = 0; ; offset += PendingStandingPageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = Command(PendingStandingQuery);
                command.Parameters.AddWithValue("$prefix", prefix);
                command.Parameters.AddWithValue("$maximum", MaximumStateBytes);
                command.Parameters.AddWithValue("$principal", principalId);
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$request", requestCorrelation);
                command.Parameters.AddWithValue("$limit", PendingStandingPageSize);
                command.Parameters.AddWithValue("$offset", offset);
                using var reader = command.ExecuteReader();
                var read = 0;
                while (reader.Read())
                {
                    read++;
                    try
                    {
                        var board = Uri.UnescapeDataString(reader.GetString(0)[prefix.Length..]);
                        var standing = JsonNode.Parse(reader.GetString(1))!.AsObject();
                        var candidate = StandingPresentation.ReadPending(standing, board, reader.GetString(2), principalId, sessionId, requestCorrelation);
                        if (candidate is not null && Encoding.UTF8.GetByteCount(candidate.SummaryJson) <= RelayLimits.MaxAttentionOfferBytes) result.Add(candidate);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { }
                    if (result.Count == PendingStandingPageSize) return result;
                }
                if (read < PendingStandingPageSize) return result;
            }
        }
        finally { _gate.Release(); }
    }

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

using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.State;

namespace CoCern.Storage;

/// <summary>
/// The stored rows behind "Waiting on you" (<see cref="WaitingList"/>). SQLite extracts only the named keys of each document, so no session
/// secret, inbox or whole board is ever copied out, and each query prefilters on the raw payload so a document without the word is not parsed.
/// </summary>
public sealed partial class SqliteStore
{
    /// <summary>Blocked tasks read at once. Human blockers sort first, so old holds cannot use up the read.</summary>
    private const int MaximumWaitingBlockers = 512;

    /// <summary>A tool read may wait this long for a busy writer before it answers busy; the dashboard's read gives up at once.</summary>
    private static readonly TimeSpan WaitingToolWait = TimeSpan.FromMilliseconds(250);

    private const string BoardDocument = "CASE WHEN json_valid(json_extract(payload, '$.Value.Json')) THEN json_extract(payload, '$.Value.Json') ELSE '{}' END";

    /// <summary>
    /// The tasks in <c>blocked</c> that may count, from any board: it does not use <see cref="StandingQuery"/>'s filter, which skips a board
    /// with no standing or orders. A task's kinds are checked in the Domain; the text test here only saves the read.
    /// </summary>
    internal const string WaitingBlockersQuery = $"""
        WITH boards AS (
            SELECT grain_key, {BoardDocument} AS document
            FROM grain_state
            WHERE state_name='{AggregateKinds.Board}' AND grain_type='cocern.board' AND json_valid(payload)
                AND substr(grain_key, 1, length($prefix)) = $prefix
                AND (instr(payload, '{BlockerKinds.HumanDecision}') > 0 OR instr(payload, '{BlockerKinds.CoordinatorHold}') > 0)
        ), tasks AS (
            SELECT grain_key, task.key AS task_id, task.value AS task, json_extract(task.value, '$.{BoardTask.WorkflowKey}.{WorkflowRecord.BlockersKey}') AS blockers
            FROM boards, json_each(boards.document, '$.{BoardState.TasksKey}') AS task
            WHERE task.type = 'object' AND json_extract(task.value, '$.{BoardTask.WorkflowKey}.{WorkflowRecord.StateKey}') = '{WorkflowStates.Blocked}'
        )
        SELECT grain_key, task_id, json_extract(task, '$.{BoardTask.OwnerKey}'), json_extract(task, '$.{BoardTask.HumanSinceKey}'), json_extract(task, '$.{BoardTask.HoldSinceKey}'), json_extract(task, '$.{BoardTask.UpdatedAtKey}'), blockers
        FROM tasks
        WHERE instr(blockers, '{BlockerKinds.HumanDecision}') > 0 OR instr(blockers, '{BlockerKinds.CoordinatorHold}') > 0
        ORDER BY instr(blockers, '{BlockerKinds.HumanDecision}') = 0, coalesce(json_extract(task, '$.{BoardTask.HumanSinceKey}'), json_extract(task, '$.{BoardTask.HoldSinceKey}'), json_extract(task, '$.{BoardTask.UpdatedAtKey}')), grain_key, task_id
        LIMIT $limit;
        """;

    /// <summary>One repository's boards with a pending proposal, their standing sub-documents only; one too large to read comes back null, so the list can say so.</summary>
    internal const string WaitingProposalsQuery = $"""
        SELECT grain_key, CASE WHEN length(standing) <= $maximum THEN standing END FROM (
            SELECT grain_key, json_extract(document, '$.{BoardReadModels.StandingKey}') AS standing FROM (
                SELECT grain_key, {BoardDocument} AS document
                FROM grain_state
                WHERE state_name='{AggregateKinds.Board}' AND grain_type='cocern.board' AND json_valid(payload)
                    AND substr(grain_key, 1, length($prefix)) = $prefix AND instr(payload, '{StandingState.ProposalsKey}') > 0))
        WHERE standing IS NOT NULL AND coalesce(length(json_extract(standing, '$.{StandingState.ProposalsKey}')), 0) > 2
        ORDER BY grain_key LIMIT $limit;
        """;

    /// <summary>One repository's open sessions with a report: its ID, label and the report, and nothing else of the session.</summary>
    internal const string WaitingReportsQuery = $"""
        SELECT json_extract(document, '$.{SessionState.SessionIdKey}'), json_extract(document, '$.{SessionState.LabelKey}'), json_extract(document, '$.{SessionState.WaitingForKey}') FROM (
            SELECT {BoardDocument} AS document
            FROM grain_state
            WHERE state_name='session' AND grain_type='cocern.session'{OpenSessionsOnly} AND json_valid(payload) AND instr(payload, '{SessionState.WaitingForKey}') > 0)
        WHERE json_extract(document, '$.{CommonState.RepositoryIdKey}') = $repository AND json_extract(document, '$.{SessionState.WaitingForKey}') IS NOT NULL
        ORDER BY json_extract(document, '$.{SessionState.WaitingForKey}.{WaitingMark.SinceKey}') LIMIT $limit;
        """;

    public async Task<WaitingSources?> ReadWaitingBlockersAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<BlockerRow>();
        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CopyWaitingBlockers("", rows);
        }
        finally { _gate.Release(); }
        return new() { Blockers = ProjectBlockers(rows, out var skipped), Truncated = rows.Count > MaximumWaitingBlockers || skipped };
    }

    public async Task<WaitingSources?> ReadWaitingAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var prefix = Uri.EscapeDataString(repositoryId) + "/";
        var blockerRows = new List<BlockerRow>();
        var standingRows = new List<(string Key, string? Standing)>();
        var reportRows = new List<(string? SessionId, string? Label, string Mark)>();
        if (!await _gate.WaitAsync(WaitingToolWait, cancellationToken)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CopyWaitingBlockers(prefix, blockerRows);
            using (var command = Command(WaitingProposalsQuery))
            {
                command.Parameters.AddWithValue("$prefix", prefix);
                command.Parameters.AddWithValue("$maximum", MaximumStateBytes);
                command.Parameters.AddWithValue("$limit", MaximumStandingBoards + 1);
                using var reader = command.ExecuteReader();
                while (reader.Read()) standingRows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
            using (var command = Command(WaitingReportsQuery))
            {
                command.Parameters.AddWithValue("$repository", repositoryId);
                command.Parameters.AddWithValue("$limit", MaximumSessions + 1);
                using var reader = command.ExecuteReader();
                while (reader.Read()) if (!reader.IsDBNull(2)) reportRows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
            }
        }
        finally { _gate.Release(); }

        // Only the bounded SQL holds the shared writer gate, as for the observation.
        var truncated = blockerRows.Count > MaximumWaitingBlockers || standingRows.Count > MaximumStandingBoards || reportRows.Count > MaximumSessions;
        var blockers = ProjectBlockers(blockerRows, out var skipped);
        truncated |= skipped;
        var boards = new List<DashboardStanding>();
        foreach (var (key, standing) in standingRows.Take(MaximumStandingBoards))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (standing is null) { truncated = true; continue; }
            try { boards.Add(BoardReadModels.Standing(key, standing, null)); }
            catch (JsonException) { truncated = true; }
        }
        var reports = new List<WaitingReport>();
        foreach (var (sessionId, label, mark) in reportRows.Take(MaximumSessions))
        {
            if (string.IsNullOrEmpty(sessionId)) continue;
            try
            {
                using var document = JsonDocument.Parse(mark, new JsonDocumentOptions { MaxDepth = 64 });
                if (WaitingList.Report(sessionId, repositoryId, document.RootElement) is { } report) reports.Add(report with { Label = label ?? "" });
            }
            catch (JsonException) { truncated = true; }
        }
        var owners = blockers.Select(blocker => blocker.OwnerSessionId).Concat(boards.SelectMany(board => board.Proposals).Select(proposal => proposal.SessionId))
            .Where(session => session.Length > 0).Distinct(StringComparer.Ordinal).Take(MaximumSessions).ToArray();
        return new() { Boards = boards, Blockers = blockers, Reports = reports, Labels = await ReadWaitingLabelsAsync(repositoryId, owners, cancellationToken), Truncated = truncated };
    }

    /// <summary>The labels of the open sessions named, in that repository. They only decorate a row, so a busy writer leaves them out rather than delay the read.</summary>
    private async Task<Dictionary<string, string>> ReadWaitingLabelsAsync(string repositoryId, string[] sessions, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sessions.Length == 0 || !await _gate.WaitAsync(0, cancellationToken)) return labels;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var parameters = string.Join(", ", Enumerable.Range(0, sessions.Length).Select(index => "$key" + index));
            using var command = Command($"""
                SELECT grain_key, json_extract(document, '$.{SessionState.LabelKey}') FROM (
                    SELECT grain_key, {BoardDocument} AS document
                    FROM grain_state
                    WHERE state_name='session' AND grain_type='cocern.session'{OpenSessionsOnly} AND json_valid(payload) AND grain_key IN ({parameters}))
                WHERE json_extract(document, '$.{CommonState.RepositoryIdKey}') = $repository;
                """);
            command.Parameters.AddWithValue("$repository", repositoryId);
            for (var index = 0; index < sessions.Length; index++) command.Parameters.AddWithValue("$key" + index, sessions[index]);
            using var reader = command.ExecuteReader();
            while (reader.Read()) if (!reader.IsDBNull(1)) labels[reader.GetString(0)] = reader.GetString(1);
        }
        finally { _gate.Release(); }
        return labels;
    }

    private sealed record BlockerRow(string BoardKey, string TaskId, string? Owner, string? HumanSince, string? HoldSince, string? UpdatedAt, string Blockers);

    /// <summary>Copies the blocked tasks of the boards under <paramref name="prefix"/>, empty for every repository, up to one past the read's limit.</summary>
    private void CopyWaitingBlockers(string prefix, List<BlockerRow> rows)
    {
        using var command = Command(WaitingBlockersQuery);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$limit", MaximumWaitingBlockers + 1);
        using var reader = command.ExecuteReader();
        string? Optional(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), Optional(2), Optional(3), Optional(4), Optional(5), reader.GetString(6)));
    }

    /// <summary>The rows that count; a row whose blockers cannot be read is left out, and <paramref name="skipped"/> says so.</summary>
    private static WaitingBlocker[] ProjectBlockers(List<BlockerRow> rows, out bool skipped)
    {
        var blockers = new List<WaitingBlocker>();
        skipped = false;
        foreach (var row in rows.Take(MaximumWaitingBlockers))
        {
            try
            {
                if (WaitingList.Blocker(row.BoardKey, row.TaskId, row.Owner, row.HumanSince, row.HoldSince, row.UpdatedAt, row.Blockers) is { } blocker) blockers.Add(blocker);
            }
            catch (JsonException) { skipped = true; }
        }
        return blockers.ToArray();
    }
}

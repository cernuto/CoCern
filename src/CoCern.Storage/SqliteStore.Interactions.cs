using System.Diagnostics;
using CoCern.Contracts.Dashboard;

namespace CoCern.Storage;

public sealed partial class SqliteStore
{
    private const int MaximumInteractionRows = 4000;
    private const int MaximumInteractionEdges = 300;

    private async Task<DashboardInteractions?> ReadInteractionsAsync(Dictionary<string, DashboardSession> sessions, DateTimeOffset now, long started, CancellationToken cancellationToken)
    {
        var from = now.AddHours(-24);
        var rows = new List<(string Repository, string Sender, string Target, long At, double Duration)>();
        var truncated = false;
        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A partial index bounds the scan to message calls. Copy only safe metadata, never queued token-counting texts.
            using var command = Command("""
                SELECT CASE WHEN length(repository_id) <= 256 THEN repository_id ELSE NULL END,
                    CASE WHEN length(session_id) <= 256 THEN session_id ELSE NULL END,
                    CASE WHEN length(target) <= 8192 THEN target ELSE NULL END, at_ticks, duration_ms
                FROM activity_log
                WHERE kind='call' AND ok=1 AND tool='send_message' AND target GLOB 'message:*'
                    AND at_ticks >= $from AND at_ticks <= $now
                ORDER BY at_ticks DESC, id DESC LIMIT $limit;
                """);
            command.Parameters.AddWithValue("$from", from.UtcTicks);
            command.Parameters.AddWithValue("$now", now.UtcTicks);
            command.Parameters.AddWithValue("$limit", MaximumInteractionRows + 1);
            using var reader = command.ExecuteReader();
            var read = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (read++ == MaximumInteractionRows || Stopwatch.GetElapsedTime(started) > ObservationBudget) { truncated = true; break; }
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2))
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetDouble(4)));
            }
        }
        finally { _gate.Release(); }

        // URI decoding, validation and grouping happen after releasing the shared writer gate.
        var edges = new Dictionary<(string Sender, string Recipient), DashboardInteractionEdge>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMessageRecipient(row.Sender, row.Target, out var recipient)
                || !sessions.TryGetValue(row.Sender, out var source) || source.RepositoryId != row.Repository
                || !sessions.TryGetValue(recipient, out var target) || target.RepositoryId != row.Repository) continue;
            var key = (row.Sender, recipient);
            if (!edges.TryGetValue(key, out var edge))
            {
                if (edges.Count >= MaximumInteractionEdges) { truncated = true; continue; }
                edge = new() { SourceSessionId = row.Sender, TargetSessionId = recipient, LastInteractionAt = new(row.At, TimeSpan.Zero) };
            }
            var measured = double.IsFinite(row.Duration) && row.Duration >= 0;
            var count = edge.MeasuredCallCount + (measured ? 1 : 0);
            // Weighted mean avoids an overflowing duration sum for malformed but finite historical values.
            var mean = measured ? (edge.MeanHubDurationMilliseconds ?? 0) * ((double)edge.MeasuredCallCount / count) + row.Duration / count : edge.MeanHubDurationMilliseconds;
            edges[key] = edge with { CallCount = edge.CallCount + 1, MeasuredCallCount = count, MeanHubDurationMilliseconds = mean };
        }
        return new() { ObservedAt = now, WindowStart = from, Edges = edges.Values.ToArray(), Truncated = truncated };
    }

    private static bool TryMessageRecipient(string sender, string target, out string recipient)
    {
        recipient = "";
        var prefix = "message:" + Uri.EscapeDataString(sender) + ">";
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var encoded = target[prefix.Length..];
        try { recipient = Uri.UnescapeDataString(encoded); }
        catch (UriFormatException) { return false; }
        return recipient.Length is > 0 and <= 256 && recipient == Safe(recipient)
            && Uri.EscapeDataString(recipient) == encoded;
    }
}

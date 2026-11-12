using CoCern.Contracts.Dashboard;

namespace CoCern.Storage;

public sealed partial class SqliteStore
{
    private const int MaximumEfficiencyRows = 100_000;

    /// <summary>Indexed raw activity from one bounded live hour. Historical rollups are deliberately excluded.</summary>
    public async Task<DashboardEfficiencySnapshot?> ReadDashboardEfficiencyAsync(string? repositoryId, DateTimeOffset from,
        DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (to <= from || to - from > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(from));
        if (!await _activityGate.WaitAsync(0, cancellationToken)) return null;
        var rows = new List<EfficiencyRow>();
        var rowsRead = 0;
        var truncated = false;
        DateTimeOffset? coverageStart = null;
        try
        {
            if (_activityConnection is null) return null;
            // The existing time index gives a conservative history bound without scanning the retained rows.
            using (var coverage = ActivityCommand("SELECT min(at_ticks) FROM activity_log;"))
            {
                coverage.CommandTimeout = 2;
                if (coverage.ExecuteScalar() is long ticks) coverageStart = new DateTimeOffset(ticks, TimeSpan.Zero);
            }
            // Each branch starts at an existing time index and stops at its row bound. Nothing reads retained history outside the hour.
            var scope = repositoryId is null ? "" : "repository_id = $repository AND ";
            using var query = ActivityCommand($"""
                SELECT at_ticks, repository_id, session_id, request_bytes, reply_bytes, request_tokens, reply_tokens,
                    background_call, call_origin, tool, kind
                FROM activity_log
                WHERE {scope}at_ticks >= $from AND at_ticks < $to
                ORDER BY at_ticks DESC, id DESC LIMIT $limit;
                """);
            query.CommandTimeout = 2;
            if (repositoryId is not null) query.Parameters.AddWithValue("$repository", repositoryId);
            query.Parameters.AddWithValue("$from", from.UtcTicks);
            query.Parameters.AddWithValue("$to", to.UtcTicks);
            query.Parameters.AddWithValue("$limit", MaximumEfficiencyRows + 1);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++rowsRead > MaximumEfficiencyRows) { truncated = true; break; }
                if (reader.GetString(10) != "call") continue;
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6), !reader.IsDBNull(7) && reader.GetInt64(7) == 1,
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }
        finally { _activityGate.Release(); }

        var requestTokens = rows.Where(row => row.RequestTokens.HasValue).ToArray();
        var replyTokens = rows.Where(row => row.ReplyTokens.HasValue).ToArray();
        return new()
        {
            CollectedAt = DateTimeOffset.UtcNow, WindowStart = from, WindowEnd = to,
            Scope = repositoryId is null ? "hub-wide" : "repository", RepositoryId = repositoryId,
            Truncated = truncated, ActivitySamples = rows.Count, ActivityCoverageStart = coverageStart, Calls = rows.Count,
            BackgroundCalls = rows.LongCount(row => row.Background),
            RequestBytes = rows.Sum(row => row.RequestBytes), ReplyBytes = rows.Sum(row => row.ReplyBytes),
            EstimatedRequestTokens = requestTokens.Length == 0 ? null : requestTokens.Sum(row => row.RequestTokens!.Value),
            EstimatedReplyTokens = replyTokens.Length == 0 ? null : replyTokens.Sum(row => row.ReplyTokens!.Value),
            ReplyTokenSamples = replyTokens.Length,
            Watcher = Polling(rows.Where(row => row.Background && row.Tool == "get_status" && row.Origin == "wake-watcher").ToArray(), from, to),
            NodeWatcher = Polling(rows.Where(row => row.Background && row.Tool == "get_status" && row.Origin == "node").ToArray(), from, to),
            OtherBackground = Polling(rows.Where(row => row.Background && (row.Tool != "get_status" || row.Origin is not ("wake-watcher" or "node"))).ToArray(), from, to),
            DroppedActivityEvents = DroppedActivityEvents
        };
    }

    private static DashboardPollingMetrics Polling(EfficiencyRow[] rows, DateTimeOffset from, DateTimeOffset to)
    {
        var sessionRows = rows.Where(row => !string.IsNullOrEmpty(row.SessionId))
            .GroupBy(row => (row.RepositoryId, row.SessionId)).ToArray();
        var hours = sessionRows.Length * (to - from).TotalHours;
        var gaps = new List<double>();
        foreach (var session in sessionRows)
        {
            long? previous = null;
            foreach (var row in session.OrderBy(row => row.At))
            {
                if (previous is { } at) gaps.Add(TimeSpan.FromTicks(row.At - at).TotalSeconds);
                previous = row.At;
            }
        }
        gaps.Sort();
        var sizes = rows.Select(row => (double)row.ReplyBytes).Order().ToArray();
        var tokenRows = rows.Where(row => row.ReplyTokens.HasValue).ToArray();
        return new()
        {
            Calls = rows.LongLength, Sessions = sessionRows.Length, ObservedSessionHours = hours,
            CallsPerSessionHour = hours == 0 ? null : rows.LongLength / hours,
            RequestBytes = rows.Sum(row => row.RequestBytes), ReplyBytes = rows.Sum(row => row.ReplyBytes),
            EstimatedReplyTokens = tokenRows.Length == 0 ? null : tokenRows.Sum(row => row.ReplyTokens!.Value),
            ReplyTokenSamples = tokenRows.LongLength,
            MeanReplyBytes = sizes.Length == 0 ? null : sizes.Average(), MedianReplyBytes = Median(sizes),
            GapSamples = gaps.Count, MedianGapSeconds = Median(gaps),
            P95GapSeconds = gaps.Count == 0 ? null : gaps[(int)Math.Ceiling(gaps.Count * .95) - 1],
            FirstCallAt = rows.Length == 0 ? null : new DateTimeOffset(rows.Min(row => row.At), TimeSpan.Zero),
            LastCallAt = rows.Length == 0 ? null : new DateTimeOffset(rows.Max(row => row.At), TimeSpan.Zero)
        };
    }

    private static double? Median(IReadOnlyList<double> values) => values.Count == 0 ? null
        : values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;

    private sealed record EfficiencyRow(long At, string RepositoryId, string? SessionId, long RequestBytes,
        long ReplyBytes, long? RequestTokens, long? ReplyTokens, bool Background, string? Origin, string? Tool);
}

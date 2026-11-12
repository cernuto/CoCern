using CoCern.Contracts.Dashboard;

namespace CoCern.Core;

/// <summary>
/// Bounded process-local measurements made while a read has its exact source version in hand.
/// It retains only sizes, relay token estimates, repository scope and timestamps; never text or document/task/session identifiers.
/// No work runs without a read or measurement. A restart intentionally leaves historical savings unknown.
/// </summary>
public sealed class ReadEfficiencyMeasurements
{
    public const string TaskBrief = "task-brief";
    public const string FocusedDocument = "focused-document";
    private const int MaximumSamples = 100_000;
    private readonly object gate = new();
    private readonly Queue<Sample> samples = new();
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? evictedThrough;

    public void Record(string repositoryId, string kind, long sourceBytes, long replyBytes,
        long? sourceTokens, long? replyTokens, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || kind is not (TaskBrief or FocusedDocument)
            || sourceBytes < 0 || replyBytes < 0 || sourceTokens < 0 || replyTokens < 0) return;
        lock (gate)
        {
            Prune(at.AddHours(-1));
            while (samples.Count >= MaximumSamples)
            {
                var evicted = samples.Dequeue().At;
                if (evictedThrough is null || evicted > evictedThrough) evictedThrough = evicted;
            }
            samples.Enqueue(new(repositoryId, kind, sourceBytes, replyBytes, sourceTokens, replyTokens, at));
        }
    }

    public (DashboardProjectionSavings TaskBrief, DashboardProjectionSavings FocusedDocument, bool Truncated) Read(
        string? repositoryId, DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            Prune(from);
            var selected = samples.Where(sample => sample.At >= from && sample.At < to
                && (repositoryId is null || sample.RepositoryId == repositoryId)).ToArray();
            return (Project(TaskBrief), Project(FocusedDocument), evictedThrough >= from);

            DashboardProjectionSavings Project(string kind)
            {
                var group = selected.Where(sample => sample.Kind == kind).ToArray();
                if (group.Length == 0) return new() { CollectionStartedAt = startedAt };
                var tokens = group.Where(sample => sample.SourceTokens.HasValue && sample.ReplyTokens.HasValue).ToArray();
                var sourceBytes = group.Sum(sample => sample.SourceBytes);
                var replyBytes = group.Sum(sample => sample.ReplyBytes);
                long? sourceTokens = tokens.Length == 0 ? null : tokens.Sum(sample => sample.SourceTokens!.Value);
                long? replyTokens = tokens.Length == 0 ? null : tokens.Sum(sample => sample.ReplyTokens!.Value);
                return new()
                {
                    CollectionStartedAt = startedAt, FirstSampleAt = group.Min(sample => sample.At), LastSampleAt = group.Max(sample => sample.At),
                    Samples = group.LongLength, SourceBytes = sourceBytes, ReplyBytes = replyBytes, SavedBytes = sourceBytes - replyBytes,
                    EstimatedSourceTokens = sourceTokens, EstimatedReplyTokens = replyTokens, EstimatedSavedTokens = sourceTokens - replyTokens,
                    TokenSamples = tokens.LongLength
                };
            }
        }
    }

    private void Prune(DateTimeOffset before)
    {
        // Call timestamps come from the hub clock. A backwards clock adjustment is harmless: selection still checks both boundaries.
        while (samples.TryPeek(out var sample) && sample.At < before) samples.Dequeue();
    }

    private sealed record Sample(string RepositoryId, string Kind, long SourceBytes, long ReplyBytes,
        long? SourceTokens, long? ReplyTokens, DateTimeOffset At);
}

using CoCern.Core;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The dashboard's observation is a bounded, best-effort sample: the store answers null when its writer gate is busy, and stops early,
/// marking the sample truncated, when its time budget runs out. Under a full test run's load either can happen to any one sample, so a
/// test takes the first available (or, for a fixture too small to truncate, complete) sample, as the dashboard's next poll would, rather
/// than fail a content assertion on a sample the store said was partial.
/// </summary>
internal static class ObservationSamples
{
    private const int Attempts = 40;
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// A sample from <paramref name="source"/>. With <paramref name="complete"/>, a sample marked truncated is retried too; pass false for
    /// a fixture that is meant to truncate.
    /// </summary>
    public static async Task<RelayObservation> ObserveAsync(this IRelayObservationSource source, DateTimeOffset now, string incarnationId, bool complete = true)
    {
        RelayObservation? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            last = await source.ReadObservationAsync(now, incarnationId);
            if (last is not null && (!complete || !last.SessionsTruncated)) return last;
            await Task.Delay(Pause);
        }
        Assert.Fail(last is null
            ? "The store gave no observation in " + Attempts + " samples."
            : "The store gave only truncated observations in " + Attempts + " samples; warnings: " + string.Join(" | ", last.Warnings));
        return last!;
    }
}

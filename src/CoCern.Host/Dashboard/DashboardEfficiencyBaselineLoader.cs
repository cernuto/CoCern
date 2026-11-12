using System.Text.Json;
using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

/// <summary>Loads only the reviewed, body-free baseline packaged with this host.</summary>
internal static class DashboardEfficiencyBaselineLoader
{
    private const string ResourceName = "CoCern.Host.Dashboard.Baselines.database-efficiency-20261110.json";
    private static readonly Lazy<DashboardEfficiencyBaseline?> Baseline = new(Read);

    public static DashboardEfficiencyBaseline? Load() => Baseline.Value;

    private static DashboardEfficiencyBaseline? Read()
    {
        try
        {
            using var stream = typeof(DashboardEfficiencyBaselineLoader).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return null;
            var baseline = JsonSerializer.Deserialize<DashboardEfficiencyBaseline>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return baseline is { DurationSeconds: 3600, Scope: "hub-wide", BackgroundCalls: >= 0, BackgroundWireBytes: >= 0 }
                && baseline.CapturedAt != default && baseline.EvidenceSha256.Length == 64
                && baseline.EvidenceSha256.All(Uri.IsHexDigit)
                && Valid(baseline.Watcher) && Valid(baseline.NodeWatcher) ? baseline : null;
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private static bool Valid(DashboardPollingMetrics metrics) => metrics is { Calls: >= 0, Sessions: > 0, RequestBytes: >= 0, ReplyBytes: >= 0 }
        && metrics.ObservedSessionHours == metrics.Sessions
        && metrics.CallsPerSessionHour is { } rate && double.IsFinite(rate) && rate >= 0
        && Math.Abs(rate - (double)metrics.Calls / metrics.Sessions) < 0.0001;
}

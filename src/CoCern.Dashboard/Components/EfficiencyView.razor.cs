using CoCern.Contracts.Dashboard;
using Microsoft.AspNetCore.Components;

namespace CoCern.Dashboard.Components;

public partial class EfficiencyView : LiveDashboardView
{
    [Parameter] public DashboardSnapshot Snapshot { get; set; } = new();
    private string _repository = "";
    private DashboardEfficiencySnapshot? _data;
    private IEnumerable<string> Repositories => RepositoryInventory.Append(_repository)
        .Where(repository => !string.IsNullOrWhiteSpace(repository)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);

    protected override async Task ReadAsync(CancellationToken cancellationToken, long generation)
    {
        var inventoryRefreshed = await ReadRepositoryInventoryAsync(cancellationToken, generation);
        if (!IsCurrent(generation)) return;
        if (inventoryRefreshed && !string.IsNullOrEmpty(_repository) && !RepositoryInventoryTruncated && !RepositoryInventory.Contains(_repository, StringComparer.OrdinalIgnoreCase))
        {
            _repository = "";
            _data = null;
            ResetSample();
        }
        var repository = _repository;
        var path = "api/efficiency" + (string.IsNullOrEmpty(repository) ? "" : $"?repositoryId={Uri.EscapeDataString(repository)}");
        var data = await ReadJsonAsync<DashboardEfficiencySnapshot>(path, cancellationToken);
        if (!IsCurrent(generation)) return;
        if (data is null || !string.Equals(data.RepositoryId ?? "", repository, StringComparison.OrdinalIgnoreCase))
        {
            Error = "The efficiency response did not match the selected scope.";
            return;
        }
        if (data.Unavailable is { } unavailable)
        {
            Error = unavailable;
            return;
        }
        _data = data;
    }

    private async Task RepositoryChangedAsync(string? repository)
    {
        repository ??= "";
        if (repository == _repository || !string.IsNullOrEmpty(repository) && !Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase)) return;
        _repository = repository;
        _data = null;
        ResetSample();
        await RefreshAsync(true);
    }

    private static (string Name, DashboardPollingMetrics Metrics)[] PollingRows(DashboardEfficiencySnapshot data) =>
        [("Session watcher", data.Watcher), ("Node watcher", data.NodeWatcher), ("Other background", data.OtherBackground)];
    private static (string Name, DashboardProjectionSavings Savings)[] SavingsRows(DashboardEfficiencySnapshot data) =>
        [("Task brief", data.TaskBrief), ("Focused document excerpt", data.FocusedDocument)];
    private static (string Name, DashboardPollingMetrics Current, DashboardPollingMetrics Baseline)[] ComparisonRows(DashboardEfficiencySnapshot data, DashboardEfficiencyBaseline baseline) =>
        [("Session watcher", data.Watcher, baseline.Watcher), ("Node watcher", data.NodeWatcher, baseline.NodeWatcher)];

    private static string Stamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";
    private static string Number(long? value) => value?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";
    private static string Rate(double? value) => value is { } number && double.IsFinite(number) ? number.ToString("N2", System.Globalization.CultureInfo.InvariantCulture) : "Unknown";
    private static string Bytes(long? value) => value is null ? "Unknown" : $"{Number(value)} B";
    private static string Seconds(double? value) => value is null ? "Unknown" : $"{Rate(value)} s";
    private static string Reduction(DashboardProjectionSavings savings) => savings.SourceBytes is > 0 && savings.SavedBytes is { } saved
        ? $"{(100d * saved / savings.SourceBytes.Value).ToString("N1", System.Globalization.CultureInfo.InvariantCulture)}% of reference bytes"
        : "Percentage unknown";
    private static string RateChange(double? current, double? baseline) => current is { } now && baseline is > 0 && double.IsFinite(now) && double.IsFinite(baseline.Value)
        ? $"{((now / baseline.Value - 1) * 100).ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)}%"
        : "Unknown";
}

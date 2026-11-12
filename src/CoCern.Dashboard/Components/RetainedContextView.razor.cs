using System.Net.Http.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using Microsoft.AspNetCore.Components;

namespace CoCern.Dashboard.Components;

public partial class RetainedContextView : LiveDashboardView
{
    [Parameter] public DashboardSnapshot Snapshot { get; set; } = new();
    private string _repository = "";
    private string _taskId = "";
    private string _taskFilter = "";
    private string _searchText = "";
    private string _query = "";
    private string? _cursor;
    private string? _contextId;
    private string? _requestedUri;
    private string? _partProblem;
    private DateTimeOffset? _catalogAt;
    private DateTimeOffset? _manifestAt;
    private DateTimeOffset? _searchAt;
    private DateTimeOffset? _partAt;
    private DashboardRetainedContextList? _catalog;
    private DashboardRetainedContextManifest? _manifest;
    private DashboardRetainedContextSearch? _search;
    private DashboardRetainedContextPart? _part;

    private IEnumerable<string> Repositories => RepositoryInventory.Append(_repository)
        .Where(repository => !string.IsNullOrWhiteSpace(repository)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);

    protected override async Task ReadAsync(CancellationToken cancellationToken, long generation)
    {
        var inventoryRefreshed = await ReadRepositoryInventoryAsync(cancellationToken, generation);
        if (!IsCurrent(generation)) return;
        if (inventoryRefreshed && (string.IsNullOrEmpty(_repository) || !RepositoryInventoryTruncated && !RepositoryInventory.Contains(_repository, StringComparer.OrdinalIgnoreCase)))
        {
            _repository = RepositoryInventory.FirstOrDefault(repository => repository.Equals("cocern", StringComparison.OrdinalIgnoreCase)) ?? RepositoryInventory.FirstOrDefault() ?? "";
            _taskId = _taskFilter = "";
            ClearSelection();
        }
        if (string.IsNullOrEmpty(_repository)) return;
        var repository = _repository;
        if (_requestedUri is { } uri)
        {
            _requestedUri = null;
            var reply = await ReadReplyAsync<DashboardRetainedContextPart>($"api/retained-context/part?uri={Uri.EscapeDataString(uri)}", cancellationToken);
            if (!IsCurrent(generation)) return;
            if (Valid(reply, repository) && reply!.Data is { } part)
            {
                _part = part;
                _partAt = reply.SampledAt;
                _partProblem = null;
            }
            else _partProblem = Problem(reply?.Code);
            return;
        }

        var path = $"api/retained-context?repositoryId={Uri.EscapeDataString(repository)}&limit=10";
        if (!string.IsNullOrWhiteSpace(_taskFilter)) path += $"&taskId={Uri.EscapeDataString(_taskFilter)}";
        if (_cursor is not null) path += $"&cursor={Uri.EscapeDataString(_cursor)}";
        var catalog = await ReadReplyAsync<DashboardRetainedContextList>(path, cancellationToken);
        if (!IsCurrent(generation)) return;
        if (!Valid(catalog, repository)) { Error = Problem(catalog?.Code); return; }
        _catalog = catalog!.Data;
        _catalogAt = catalog.SampledAt;

        if (_contextId is not { } contextId) return;
        var manifest = await ReadReplyAsync<DashboardRetainedContextManifest>($"api/retained-context/manifest?repositoryId={Uri.EscapeDataString(repository)}&contextId={Uri.EscapeDataString(contextId)}", cancellationToken);
        if (!IsCurrent(generation)) return;
        if (!Valid(manifest, repository)) { Error = Problem(manifest?.Code); return; }
        _manifest = manifest!.Data;
        _manifestAt = manifest.SampledAt;

        if (string.IsNullOrWhiteSpace(_query)) return;
        var search = await ReadReplyAsync<DashboardRetainedContextSearch>($"api/retained-context/search?repositoryId={Uri.EscapeDataString(repository)}&contextId={Uri.EscapeDataString(contextId)}&query={Uri.EscapeDataString(_query)}&limit=5", cancellationToken);
        if (!IsCurrent(generation)) return;
        if (!Valid(search, repository)) { Error = Problem(search?.Code); return; }
        _search = search!.Data;
        _searchAt = search.SampledAt;
    }

    private async Task<DashboardRetainedContextReply<T>?> ReadReplyAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRead) throw new OperationCanceledException();
        using var response = await Http.GetAsync(path, cancellationToken);
        // Retained-context failures carry the same bounded envelope, including the exact-version error code.
        return await response.Content.ReadFromJsonAsync<DashboardRetainedContextReply<T>>(cancellationToken);
    }

    private static bool Valid<T>(DashboardRetainedContextReply<T>? reply, string repository) where T : class => reply is { Ok: true, Data: not null } && string.Equals(reply.RepositoryId, repository, StringComparison.OrdinalIgnoreCase);

    private async Task RepositoryChangedAsync(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository == _repository || !Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase)) return;
        _repository = repository;
        _taskId = _taskFilter = "";
        ClearSelection();
        await RefreshAsync(true);
    }

    private async Task FilterAsync()
    {
        _taskFilter = _taskId.Trim();
        ClearSelection();
        await RefreshAsync(true);
    }

    private void ClearSelection()
    {
        _cursor = _contextId = _requestedUri = null;
        _catalog = null;
        _catalogAt = _manifestAt = _searchAt = _partAt = null;
        _manifest = null;
        _search = null;
        _part = null;
        _partProblem = null;
        _query = _searchText = "";
        ResetSample();
    }

    private async Task OpenContextAsync(string contextId)
    {
        _contextId = contextId;
        _manifest = null;
        _search = null;
        _part = null;
        _requestedUri = null;
        _partProblem = null;
        _manifestAt = _searchAt = _partAt = null;
        _query = _searchText = "";
        await RefreshAsync(true);
    }

    private async Task SearchAsync()
    {
        _query = _searchText.Trim();
        _search = null;
        _searchAt = null;
        await RefreshAsync(true);
    }

    private async Task OpenPartAsync(string uri)
    {
        _requestedUri = uri;
        _partProblem = null;
        await RefreshAsync(true);
    }

    private async Task PageAsync(string? cursor)
    {
        _cursor = cursor;
        _catalog = null;
        await RefreshAsync(true);
    }

    private static string Stamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";
    private static string Problem(string? code) => code switch
    {
        "RepositoryDenied" => "Access to this repository is no longer available.",
        "ContextVersionUnavailable" => "This exact retained version is unavailable. The selected text has not been replaced with another version.",
        "ContextListRestartRequired" => "The collection list changed. Choose First collections to read a fresh page.",
        "NotFound" => "The selected retained context could not be found.",
        "ContextSearchUnavailable" => "Search is temporarily unavailable. Collection details and exact reads remain available.",
        _ => $"Retained context is unavailable ({code ?? "unknown response"}). A fresh hub update will retry."
    };
}

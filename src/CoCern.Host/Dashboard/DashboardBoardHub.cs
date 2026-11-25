using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using Microsoft.AspNetCore.SignalR;

namespace CoCern.Host.Dashboard;

internal sealed class DashboardBoardHub(DashboardBoardNotifier boards, IRelayApplication application) : Hub
{
    public Task<string> WatchBoard(string repositoryId, string boardId) => boards.WatchAsync(Context, repositoryId, boardId);

    public async Task<DashboardBoardPage> ReadChanges(long sinceRevision, long? snapshotRevision, int cursor)
    {
        var (repository, board, caller) = boards.AuthorizedScope(Context);
        var args = new JsonObject
        {
            ["repositoryId"] = repository, ["boardId"] = board, ["sinceRevision"] = sinceRevision,
            ["cursor"] = cursor, ["limit"] = 24, ["dashboardRows"] = true
        };
        if (snapshotRevision.HasValue) args["snapshotRevision"] = snapshotRevision.Value;
        var reply = await application.ExecuteAsync(caller, RelayTools.TaskGet, JsonSerializer.SerializeToElement(args), Context.ConnectionAborted);
        if (reply.Code == RelayErrorCodes.DeltaRestartRequired || reply.Code == RelayErrorCodes.InvalidArguments && sinceRevision > 0)
            return new(0, null, [], Restart: true);
        if (!reply.Ok) throw new HubException("Board changes are unavailable.");
        using var data = JsonDocument.Parse(reply.DataJson);
        var page = data.RootElement;
        var now = DateTimeOffset.UtcNow;
        var rows = page.GetProperty("items").EnumerateArray().Select(item =>
        {
            DateTimeOffset? leaseUntil = item.TryGetProperty("leaseUntil", out var lease) && lease.ValueKind == JsonValueKind.String &&
                lease.TryGetDateTimeOffset(out var until) ? until : null;
            return new DashboardBoardRow(
                item.GetProperty("taskId").GetString()!, item.TryGetProperty("version", out var version) ? version.GetInt64() : 0,
                item.TryGetProperty("owner", out var owner) ? owner.GetString() : null,
                leaseUntil > now,
                item.TryGetProperty("pendingActions", out var pending) ? pending.GetBoolean() : null,
                Scalar(item, "state"), item.TryGetProperty("archived", out var archived) && archived.GetBoolean(), leaseUntil,
                Scalar(item, "title"), Scalar(item, "milestone"), Scalar(item, "updatedBy"),
                DateTimeOffset.TryParse(Scalar(item, "updatedAt"), out var updated) ? updated : null,
                Scalar(item, "blockerKind"), Scalar(item, "blocker"), Scalar(item, "target"), Scalar(item, "parentTaskId"),
                item.TryGetProperty("review", out var review) && review.ValueKind == JsonValueKind.Object
                    ? new(Scalar(review, "commit")!, review.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : null,
                        Scalar(review, "branch")!, Scalar(review, "reviewer")!, Scalar(review, "conclusion")!) : null);
        }).ToArray();
        boards.AuthorizedScope(Context);
        return new(page.GetProperty("snapshotRevision").GetInt64(),
            page.GetProperty("nextCursor") is { ValueKind: JsonValueKind.Number } next ? next.GetInt32() : null, rows, SampledAt: now);
    }

    private static string? Scalar(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    public async Task<DashboardReportPage> ReadReports(string taskId, long version, int cursor)
    {
        var (repository, board, caller) = boards.AuthorizedScope(Context);
        var args = JsonSerializer.SerializeToElement(new { repositoryId = repository, boardId = board, taskId, section = "dashboardEvidence", cursor, limit = 4 });
        var reply = await application.ExecuteAsync(caller, RelayTools.TaskGet, args, Context.ConnectionAborted);
        if (!reply.Ok || reply.Version != version) throw new HubException("Task changed. Reopen its reports.");
        using var json = JsonDocument.Parse(reply.DataJson);
        var page = json.RootElement;
        var reports = page.GetProperty("items").EnumerateArray().Select(entry =>
        {
            var by = Scalar(entry, "author");
            if (Scalar(entry, "text") is { } message) return $"{by} · {Scalar(entry, "at")}\n{message}";
            var title = $"{Scalar(entry, "actionId")} · {Scalar(entry, "outcome")} · {by} · {Scalar(entry, "at")}";
            var body = entry.TryGetProperty("report", out var report) ? report.ToString() : "";
            if (entry.TryGetProperty("review", out var review) && review.ValueKind == JsonValueKind.Object)
                title += $"\n{Scalar(review, "branch")} @ {Scalar(review, "commit")} · {Scalar(review, "conclusion")}";
            return body.Length > 0 ? title + "\n" + body : title;
        }).ToArray();
        boards.AuthorizedScope(Context);
        return new(version, reports, page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null);
    }

    public async Task<DashboardRevisionComparison> CompareReview(string commit, string branch, int? pullRequest,
        [Microsoft.AspNetCore.Mvc.FromServices] DocRepositoryRegistry registry, [Microsoft.AspNetCore.Mvc.FromServices] GitCli git)
    {
        var (repository, _, _) = boards.AuthorizedScope(Context);
        if (registry.Find(repository) is not { } repo || !DocGit.IsCommit(commit) || !DocGlob.SafeRef(branch))
            return new(null, null);
        var comparison = await DashboardReviewComparison.ReadAsync(repo, git, commit, branch, pullRequest, Context.ConnectionAborted);
        boards.AuthorizedScope(Context);
        return comparison;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        boards.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

internal sealed class DashboardBoardNotifier(
    IHubContext<DashboardBoardHub> hub, DashboardSecurity security, RepositoryCatalog repositories)
{
    private sealed record Viewer(HubCallerContext Context, string Repository, string Board, string Group);
    private readonly ConcurrentDictionary<string, Viewer> viewers = new(StringComparer.Ordinal);
    private readonly object admission = new();
    private readonly string instance = Guid.NewGuid().ToString("N");

    internal async Task<string> WatchAsync(HubCallerContext context, string repositoryId, string boardId)
    {
        if (!ValidId(repositoryId) || !ValidId(boardId) || repositories.Find(repositoryId) is not { } repository
            || security.Caller(context.User, DateTimeOffset.UtcNow)?.MayAccess(repository) != true)
            throw new HubException("Board access is unavailable.");
        var viewer = new Viewer(context, repository, boardId, AggregateKinds.BoardKey(repository, boardId));
        lock (admission)
        {
            if (viewers.Count >= 64 || !viewers.TryAdd(context.ConnectionId, viewer))
                throw new HubException("A board subscription is unavailable.");
        }
        try
        {
            await hub.Groups.AddToGroupAsync(context.ConnectionId, viewer.Group, context.ConnectionAborted);
            return instance;
        }
        catch
        {
            Disconnected(context.ConnectionId);
            throw;
        }
    }

    internal (string Repository, string Board, RelayCaller Caller) AuthorizedScope(HubCallerContext context)
    {
        if (viewers.TryGetValue(context.ConnectionId, out var viewer)
            && CurrentCaller(viewer) is { } caller)
            return (viewer.Repository, viewer.Board, caller);
        context.Abort();
        throw new HubException("Board access has expired.");
    }

    internal void Disconnected(string id) => viewers.TryRemove(id, out _);

    public async Task BoardChangedAsync(string repositoryId, string boardId, long revision, CancellationToken cancellationToken)
    {
        var group = AggregateKinds.BoardKey(repositoryId, boardId);
        List<string> excluded = [];
        var hasViewer = false;
        foreach (var viewer in viewers.Values)
        {
            if (viewer.Group != group) continue;
            if (CurrentCaller(viewer) is not null) hasViewer = true;
            else
            {
                excluded.Add(viewer.Context.ConnectionId);
                viewer.Context.Abort();
            }
        }
        if (hasViewer) await hub.Clients.GroupExcept(group, excluded).SendAsync("BoardChanged", revision, cancellationToken);
    }

    private RelayCaller? CurrentCaller(Viewer viewer)
    {
        var caller = security.Caller(viewer.Context.User, DateTimeOffset.UtcNow);
        return caller?.MayAccess(viewer.Repository) == true && repositories.Find(viewer.Repository) is not null ? caller : null;
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id)
        && id.Length <= RelayLimits.MaxIdentifierLength && !id.Any(char.IsControl);
}

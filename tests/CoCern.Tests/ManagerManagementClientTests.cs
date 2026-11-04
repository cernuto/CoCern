using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Services;
using Xunit;
namespace CoCern.Tests;

public class ManagerManagementClientTests
{
    [Fact]
    public async Task Lost_response_reuses_the_same_request_and_words()
    {
        var requests = new List<string>();
        var handler = new Handler(async request =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync());
            if (requests.Count == 1) throw new HttpRequestException("response lost");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", null, true)) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://localhost/dashboard/") };
        var decision = new DashboardManagerDecision("repo", "stable", "appoint_manager", "exact-id", 7, "  Exact human words.  ");
        await Assert.ThrowsAsync<HttpRequestException>(() => ManagerManagementClient.DecideAsync(http, decision));
        Assert.True((await ManagerManagementClient.DecideAsync(http, decision)).Ok);
        Assert.Equal(requests[0], requests[1]);
        using var json = JsonDocument.Parse(requests[1]);
        Assert.Equal("stable", json.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("  Exact human words.  ", json.RootElement.GetProperty("humanWords").GetString());
    }

    [Fact]
    public async Task Stale_form_reads_new_selection_without_automatically_resubmitting()
    {
        var methods = new List<HttpMethod>(); var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            methods.Add(request.Method); paths.Add(request.RequestUri!.PathAndQuery);
            var response = request.Method == HttpMethod.Post ? new DashboardManagerResult(false, "VersionConflict", null, null)
                : new DashboardManagerResult(true, "Ok", new(8, new("new", "repo", 8, true), [], null, false), true);
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Conflict : HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var result = await ManagerManagementClient.DecideAsync(http, new("repo", "stale", "clear_manager", null, 7, "Clear."));
        Assert.False(result.Ok); Assert.Equal("VersionConflict", result.Code); Assert.Equal(8, result.Manager!.Generation);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, methods);
        Assert.Equal("/dashboard/api/manager?repositoryId=repo", paths[1]);
    }

    [Fact]
    public async Task Read_encodes_repository_and_history_cursor_and_dispatch_has_its_own_route()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", null, false)) });
        })) { BaseAddress = new("https://localhost/dashboard/") };
        Assert.True((await ManagerManagementClient.ReadAsync(http, "repo & other", 9)).Ok);
        Assert.Equal("/dashboard/api/manager?repositoryId=repo%20%26%20other&managerHistoryBefore=9", paths[0]);
        Assert.True((await ManagerManagementClient.DispatchAsync(http, new("repo", "dispatch", "resume"))).Ok);
        Assert.Equal("/dashboard/api/dispatch", paths[1]);
    }

    [Fact]
    public async Task Form_preserves_uncertain_decision_and_explicit_new_decision_gets_a_new_id()
    {
        var writes = new List<DashboardManagerDecision>(); var lose = true;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", new(7, null, [], null, false), false)) };
            writes.Add((await request.Content!.ReadFromJsonAsync<DashboardManagerDecision>())!);
            if (lose) { lose = false; throw new HttpRequestException("response lost"); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", new(8, null, [], null, false), false)) };
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo", TargetSessionId = "exact", HumanWords = "  Yes, choose this chat.  " };
        await form.RefreshAsync();
        await form.DecideAsync("clear_manager");
        Assert.NotNull(form.UnconfirmedDecision);
        var saved = form.UnconfirmedDecision;
        await form.DecideAsync("clear_manager"); // No second decision while its predecessor is unresolved.
        Assert.Single(writes);
        await form.RetryDecisionAsync();
        Assert.Equal(saved, writes[1]); Assert.Null(form.UnconfirmedDecision);
        form.NewDecision(); form.HumanWords = "Clear the selection.";
        await form.DecideAsync("clear_manager");
        Assert.NotEqual(saved.RequestId, writes[2].RequestId);
        Assert.Equal(8, writes[2].ExpectedManagerGeneration);
        Assert.Equal("  Yes, choose this chat.  ", writes[0].HumanWords);
    }

    [Fact]
    public async Task Form_rejects_201_characters_without_trimming_or_sending()
    {
        var writes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) writes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", new(0, null, [], null, false), false)) });
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo", HumanWords = new string('x', 201) };
        await form.RefreshAsync(); await form.DecideAsync("clear_manager");
        Assert.Equal(0, writes); Assert.Equal(201, form.HumanWords.Length); Assert.Contains("200", form.Message);
    }

    [Fact]
    public async Task Form_shows_refreshed_selection_on_conflict_without_automatic_write()
    {
        var writes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", new(1, null, [], null, false), false)) });
            writes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new DashboardManagerResult(false, "VersionConflict", new(2, new("new-manager", "repo", 2, true), [], null, false), false)) });
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo", HumanWords = "Clear." };
        await form.RefreshAsync(); await form.DecideAsync("clear_manager");
        Assert.Equal(1, writes); Assert.Null(form.UnconfirmedDecision); Assert.Equal("new-manager", form.Selection!.Current!.SessionId);
        Assert.Contains("review", form.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_click_during_transport_does_not_submit_a_second_decision()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post) { writes++; entered.SetResult(); await release.Task; }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", new(1, null, [], null, false), false)) };
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo", HumanWords = "Clear." };
        await form.RefreshAsync();
        var pending = form.DecideAsync("clear_manager");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(form.DecisionBusy);
        await form.DecideAsync("clear_manager"); await form.RetryDecisionAsync();
        Assert.Equal(1, writes);
        release.SetResult(); await pending;
        Assert.False(form.DecisionBusy);
    }

    [Fact]
    public async Task Forbidden_domain_result_keeps_its_existing_code()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new DashboardManagerResult(false, "RepositoryDenied", null, null))
        }))) { BaseAddress = new("https://localhost/dashboard/") };
        var result = await ManagerManagementClient.DecideAsync(http, new("repo", "denied", "appoint_manager", "other-repo-target", 0, "Choose."));
        Assert.False(result.Ok); Assert.Equal("RepositoryDenied", result.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Empty_boundary_refusal_asks_for_sign_in(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status)))) { BaseAddress = new("https://localhost/dashboard/") };
        Assert.Contains("Sign in", (await ManagerManagementClient.ReadAsync(http, "repo", null)).Code);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}

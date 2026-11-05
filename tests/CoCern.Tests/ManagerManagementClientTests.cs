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
    public async Task Form_preserves_uncertain_decision_and_explicit_new_decision_gets_a_new_id()
    {
        var writes = new List<DashboardManagerDecision>(); var lose = true;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardManagerResult(true, "Ok", new(writes.Count == 0 ? 7 : 8, null, [], null, false), false)) };
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

    [Theory]
    [InlineData("clear_manager", false)]
    [InlineData("appoint_manager", true)]
    public async Task A_completed_appointment_requires_fresh_words_for_clear_or_another_target(string nextCommand, bool changeTarget)
    {
        var writes = new List<DashboardManagerDecision>();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post) writes.Add((await request.Content!.ReadFromJsonAsync<DashboardManagerDecision>())!);
            return Reply(new(true, "Ok", new(writes.Count, null, [], null, false), false));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo", TargetSessionId = "A" };
        Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?> { ["Sessions"] = new DashboardSession[] { new() { SessionId = "A", RepositoryId = "repo" }, new() { SessionId = "B", RepositoryId = "repo" } } }).SetParameterProperties(form);
        await form.RefreshAsync(); form.HumanWords = "Choose A."; await form.DecideAsync("appoint_manager");
        Assert.Single(writes);
        if (changeTarget) form.TargetSessionId = "B";
        await form.DecideAsync(nextCommand);
        Assert.Single(writes); Assert.Empty(form.HumanWords);
        form.HumanWords = changeTarget ? "Choose B." : "Clear.";
        await form.DecideAsync(nextCommand);
        Assert.Equal(2, writes.Count);
        Assert.Equal(changeTarget ? "Choose B." : "Clear.", writes[1].HumanWords); Assert.Empty(form.HumanWords);
    }

    [Fact]
    public async Task Changing_context_or_abandoning_uncertainty_requires_fresh_words_but_retry_keeps_original()
    {
        var writes = new List<DashboardManagerDecision>(); var lose = true;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                writes.Add((await request.Content!.ReadFromJsonAsync<DashboardManagerDecision>())!);
                if (lose) throw new HttpRequestException("lost response");
            }
            return Reply(new(true, "Ok", new(0, null, [], null, false), false));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo", TargetSessionId = "A" };
        await form.RefreshAsync();
        form.HumanWords = "Choose A."; form.TargetSessionId = "B";
        Assert.Empty(form.HumanWords);
        form.HumanWords = "Clear here."; form.RepositoryId = "other";
        Assert.Empty(form.HumanWords);
        form.RepositoryId = "repo"; await form.RefreshAsync();
        form.HumanWords = "  Clear this exact selection.  "; await form.DecideAsync("clear_manager");
        var saved = form.UnconfirmedDecision;
        Assert.NotNull(saved); lose = false; await form.RetryDecisionAsync();
        Assert.Equal(saved, writes[1]);
        form.HumanWords = "Another clear."; lose = true; await form.DecideAsync("clear_manager");
        Assert.NotNull(form.UnconfirmedDecision);
        form.NewDecision();
        Assert.Empty(form.HumanWords); Assert.Null(form.UnconfirmedDecision);
        await form.DecideAsync("clear_manager");
        Assert.Equal(3, writes.Count);
    }

    private sealed class ParameterForm : CoCern.Dashboard.Components.ManagerControl
    {
        public Task Observe(string? repository)
        {
            DefaultRepositoryId = repository;
            return OnParametersSetAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Confirmed_write_survives_unavailable_current_read(bool selection)
    {
        var reads = 0; var writes = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) writes++;
            else if (++reads > 1) throw new HttpRequestException("status read unavailable");
            return Task.FromResult(Reply(new(true, "Ok", new(writes, null, [], null, false), false)));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo" };
        await form.RefreshAsync(); form.HumanWords = "Clear.";
        if (selection) await form.DecideAsync("clear_manager"); else await form.DispatchAsync("resume");
        Assert.Equal(1, writes); Assert.Equal(2, reads); Assert.Null(form.Paused); Assert.Null(form.Selection);
        Assert.Null(form.UnconfirmedDecision); Assert.Null(form.UnconfirmedDispatch);
        Assert.Contains("recorded", selection ? form.Message : form.DispatchMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Reply(DashboardManagerResult result) => new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}

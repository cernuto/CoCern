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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_refresh_does_not_replace_a_confirmed_stop_or_selection(bool selection)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var read = ++reads;
                if (read == 2) { entered.SetResult(); await release.Task; }
                return Reply(new(true, "Ok", new(read > 2 && selection ? 2 : 1, null, [], null, false), read > 2));
            }
            return Reply(new(true, "Ok", selection ? new(2, null, [], null, false) : null, true));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo" };
        await form.RefreshAsync();
        var delayed = form.RefreshAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (selection) { form.HumanWords = "Clear."; await form.DecideAsync("clear_manager"); }
        else await form.DispatchAsync("stop");
        Assert.True(form.ReadBusy); // Independent write controls remain usable.
        var feedback = form.Message; var dispatchFeedback = form.DispatchMessage;
        release.SetResult(); await delayed;
        Assert.True(form.Paused);
        if (selection) { Assert.Equal(2, form.Selection!.Generation); Assert.Equal(feedback, form.Message); }
        Assert.Equal(dispatchFeedback, form.DispatchMessage);
    }

    [Fact]
    public async Task Delayed_history_cannot_append_to_a_new_selection_or_replace_feedback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CoCern.Contracts.ManagerSelectionHistory History(long generation) => new(generation, "clear_manager", null, null, "repo", "viewer", "r-" + generation, DateTimeOffset.UnixEpoch, "Clear " + generation);
        var generation = 2;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                generation = 3;
                return Reply(new(true, "Ok", new(3, null, [History(3)], null, false), false));
            }
            if (request.RequestUri!.Query.Contains("managerHistoryBefore"))
            {
                entered.SetResult(); await release.Task;
                return Reply(new(true, "Ok", new(2, null, [History(1)], 1, true), false));
            }
            return Reply(new(true, "Ok", new(generation, null, [History(generation)], generation, true), false));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo" };
        await form.RefreshAsync();
        var delayed = form.LoadEarlierAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        form.HumanWords = "Clear."; await form.DecideAsync("clear_manager");
        var feedback = form.Message;
        release.SetResult(); await delayed;
        Assert.Equal(3, form.Selection!.Generation);
        Assert.Equal(3, Assert.Single(form.RecentSelections).Generation);
        Assert.Equal(feedback, form.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definitive_conflict_with_failed_refresh_requires_only_read_recovery(bool transportFailure)
    {
        var writes = 0; var reads = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                writes++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new DashboardManagerResult(false, "VersionConflict", null, null)) });
            }
            reads++;
            if (reads == 2)
            {
                if (transportFailure) throw new HttpRequestException("refresh unavailable");
                return Task.FromResult(Reply(new(false, "Unavailable", null, null)));
            }
            return Task.FromResult(Reply(new(true, "Ok", new(reads == 1 ? 1 : 2, null, [], null, false), false)));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo" };
        await form.RefreshAsync(); form.HumanWords = "Clear."; await form.DecideAsync("clear_manager");
        Assert.Null(form.UnconfirmedDecision);
        Assert.Null(form.Selection); Assert.Empty(form.RecentSelections);
        Assert.Contains("Selection changed", form.Message);
        Assert.Contains("Refresh selection", form.Message);
        await form.RetryDecisionAsync(); await form.DecideAsync("clear_manager");
        Assert.Equal(1, writes);
        await form.RefreshAsync();
        Assert.Equal(2, form.Selection!.Generation);
        Assert.Equal(1, writes);
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

    [Fact]
    public async Task Dispatch_readback_superseded_by_refresh_requires_fresh_words_for_the_successor()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new List<DashboardManagerDecision>();
        int reads = 0, generation = 1;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/dispatch"))
                return Reply(new(true, "Ok", null, true));
            if (request.Method == HttpMethod.Post)
                decisions.Add((await request.Content!.ReadFromJsonAsync<DashboardManagerDecision>())!);
            var snapshot = generation;
            if (request.Method == HttpMethod.Get && ++reads == 2)
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return Reply(new(true, "Ok", new(snapshot, new(snapshot == 1 ? "A" : "B", "repo", snapshot, true), [], null, false), true));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo" };
        await form.RefreshAsync();
        Assert.Equal("A", form.Selection!.Current!.SessionId);
        form.HumanWords = "Remove A.";
        var stop = form.DispatchAsync("stop");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        generation = 2; // Another viewer appointed B while the first readback was in flight.
        await form.RefreshAsync();
        release.SetResult();
        await stop;
        Assert.Equal("B", form.Selection!.Current!.SessionId);
        await form.DecideAsync("clear_manager");
        Assert.Empty(decisions);
        form.HumanWords = "Remove B.";
        await form.DecideAsync("clear_manager");
        var clear = Assert.Single(decisions);
        Assert.Equal(2, clear.ExpectedManagerGeneration);
        Assert.Equal("Remove B.", clear.HumanWords);
    }

    [Fact]
    public async Task Newer_manual_refresh_supersedes_a_delayed_post_receipt_read()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post) return Reply(new(true, "Ok", null, true));
            var read = ++reads;
            if (read == 1) { entered.SetResult(); await release.Task; }
            return Reply(new(true, "Ok", new(read, null, [], null, false), read == 1));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo" };
        var stop = form.DispatchAsync("stop"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await form.RefreshAsync(); release.SetResult(); await stop;
        Assert.False(form.Paused); Assert.Equal(2, form.Selection!.Generation);
        Assert.Contains("Stop recorded", form.DispatchMessage);
    }

    [Fact]
    public async Task Multiline_answer_requires_explicit_correction_and_preserves_spacing()
    {
        var writes = new List<DashboardManagerDecision>();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post) writes.Add((await request.Content!.ReadFromJsonAsync<DashboardManagerDecision>())!);
            return Reply(new(true, "Ok", new(writes.Count, null, [], null, false), false));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo" };
        await form.RefreshAsync();
        form.HumanWords = "Line one\nLine two"; await form.DecideAsync("clear_manager");
        Assert.Empty(writes); Assert.Equal("Line one\nLine two", form.HumanWords);
        form.HumanWords = "Line one  Line two"; await form.DecideAsync("clear_manager");
        Assert.Equal("Line one  Line two", Assert.Single(writes).HumanWords);
    }

    private sealed class ParameterForm : CoCern.Dashboard.Components.ManagerControl
    {
        public Task Observe(string? repository)
        {
            DefaultRepositoryId = repository;
            return OnParametersSetAsync();
        }
    }

    [Fact]
    public async Task Late_repository_is_adopted_once_until_operator_edits_or_clears()
    {
        var reads = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            reads++;
            return Task.FromResult(Reply(new(true, "Ok", new(0, null, [], null, false), false)));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new ParameterForm { ManagerHttp = http };
        await form.Observe(null);
        await form.DispatchAsync("stop"); Assert.Contains("repository", form.DispatchMessage, StringComparison.OrdinalIgnoreCase);
        await form.Observe("first"); await form.Observe("second");
        Assert.Equal("first", form.RepositoryId); Assert.Equal(1, reads);
        form.RepositoryId = ""; await form.Observe("third");
        Assert.Empty(form.RepositoryId); Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_gateway_receipt_and_concurrent_change_refresh_current_state(bool selection)
    {
        var writes = new List<string>();
        var generation = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Reply(new(true, "Ok", new(generation, generation == 0 ? null : new("B", "repo", generation, true), [], null, false), false));
            writes.Add(await request.Content!.ReadAsStringAsync());
            if (writes.Count == 1)
            {
                generation = 1; // Write committed; the proxy lost the original response.
                return new(HttpStatusCode.BadGateway) { Content = JsonContent.Create(new { title = "upstream response missing" }) };
            }
            return Reply(new(true, "Ok", new(1, new("A", "repo", 1, true), [], null, false), true));
        })) { BaseAddress = new("https://localhost/dashboard/") };
        var form = new CoCern.Dashboard.Components.ManagerControl { ManagerHttp = http, RepositoryId = "repo", TargetSessionId = "A" };
        await form.RefreshAsync(); form.HumanWords = "Select  A.";
        if (selection)
        {
            await form.DecideAsync("appoint_manager"); // Exact A is absent from observations.
            Assert.NotNull(form.UnconfirmedDecision); Assert.Equal("Select  A.", form.HumanWords);
        }
        else { await form.DispatchAsync("stop"); Assert.NotNull(form.UnconfirmedDispatch); }
        generation = 2; // Second viewer handed off to B and resumed.
        await form.RefreshAsync();
        if (selection) await form.RetryDecisionAsync(); else await form.RetryDispatchAsync();
        Assert.Equal(2, writes.Count); Assert.Equal(writes[0], writes[1]);
        Assert.Null(form.UnconfirmedDecision); Assert.Null(form.UnconfirmedDispatch);
        Assert.Equal(2, form.Selection!.Generation); Assert.Equal("B", form.Selection.Current!.SessionId); Assert.False(form.Paused);
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

    [Theory]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"code\":\"Ok\"}")]
    [InlineData(200, "{\"ok\":false,\"code\":\"InvalidArguments\"}")]
    [InlineData(400, "{\"ok\":true,\"code\":\"Ok\"}")]
    [InlineData(400, "{\"ok\":false,\"code\":\"Ok\"}")]
    [InlineData(502, "{\"ok\":false,\"code\":\"InvalidArguments\"}")]
    [InlineData(400, "{\"ok\":false,\"code\":\"VersionConflict\"}")]
    [InlineData(200, "{\"ok\":true,\"code\":\"Ok\"}")]
    public async Task Inconsistent_manager_envelopes_are_uncertain(int status, string json)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }))) { BaseAddress = new("https://localhost/dashboard/") };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ManagerManagementClient.DispatchAsync(http, new("repo", "id", "stop")));
    }

    private static HttpResponseMessage Reply(DashboardManagerResult result) => new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}

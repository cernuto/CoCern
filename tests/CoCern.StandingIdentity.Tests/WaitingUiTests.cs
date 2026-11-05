using System.Net;
using System.Text;
using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CoCern.Tests;

public sealed class WaitingUiTests
{
    [Fact]
    public async Task Waiting_text_and_labels_render_escaped_without_automatic_links()
    {
        var waiting = new DashboardWaiting
        {
            Count = 1,
            Items = [new() { Kind = DashboardWaitingKinds.SelfReport, SessionId = "own-session", Label = "<img src=x onerror=bad()>", Text = "<script>bad()</script> https://untrusted.invalid", Since = DateTimeOffset.UtcNow }]
        };
        var html = await Render<WaitingOnYou>(new() { ["Waiting"] = waiting });
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img", html);
        Assert.Contains("Reported by the chat", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("href=", html);
    }

    [Fact]
    public async Task Unknown_kinds_remain_readable_and_coordinator_holds_stay_separate()
    {
        var waiting = new DashboardWaiting
        {
            Count = 51, Truncated = true,
            Items = [new() { Kind = "future_kind", Text = "No hub call for 10 minutes", Label = "Worker" }],
            MaybeCount = 1,
            Maybe = [new() { Kind = DashboardWaitingKinds.CoordinatorHold, Text = "Coordinator hold" }]
        };
        var html = await Render<WaitingOnYou>(new() { ["Waiting"] = waiting });
        Assert.Contains("No hub call for 10 minutes", html);
        Assert.Contains("may be incomplete", html);
        Assert.Contains("Coordinator holds", html);
        Assert.Contains("may not need your answer", html);
        Assert.DoesNotContain("future_kind", html);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(51, true)]
    public async Task Badge_uses_the_true_count_and_is_quiet_at_zero(int count, bool accented)
    {
        var html = await Render<WaitingBadge>(new() { ["Waiting"] = new DashboardWaiting { Count = count, MaybeCount = 4 } });
        Assert.Contains($">{count}</span>", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Equal(accented, html.Contains("has-waiting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_waiting_list_explains_that_nothing_needs_an_answer()
    {
        var html = await Render<WaitingOnYou>(new() { ["Waiting"] = new DashboardWaiting() });
        Assert.Contains("Nothing is waiting on you", html);
    }

    [Fact]
    public async Task Slim_dispatch_keeps_controls_visible_without_the_manager_form()
    {
        var html = await Render<ManagerControl>(new() { ["Compact"] = true, ["DefaultRepositoryId"] = "repo" });
        Assert.Contains("Dispatch:", html);
        Assert.Contains("Running", html);
        Assert.Contains("Pause dispatch", html);
        Assert.Contains("Request stop for all chats", html);
        Assert.Contains("Resume dispatch", html);
        Assert.Contains("href=\"manager\"", html);
        Assert.DoesNotContain("id=\"manager-target\"", html);
        Assert.DoesNotContain("id=\"manager-human-words\"", html);
    }

    [Fact]
    public async Task Dedicated_manager_view_retains_exact_human_answer_and_selection_history()
    {
        var html = await Render<ManagerControl>(new() { ["DefaultRepositoryId"] = "repo" });
        Assert.Contains("id=\"manager-target\"", html);
        Assert.Contains("id=\"manager-human-words\"", html);
        Assert.Contains("Recent manager selections", html);
        Assert.Contains("grants no new powers", html);
    }

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var handler = new ReadHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/dashboard/") };
        using var services = new ServiceCollection().AddLogging().AddSingleton(http).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return rendered.ToHtmlString();
        });
    }

    private sealed class ReadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"code\":\"Ok\",\"paused\":false}", Encoding.UTF8, "application/json") });
        }
    }
}

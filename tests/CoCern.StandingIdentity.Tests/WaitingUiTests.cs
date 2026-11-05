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

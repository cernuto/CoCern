using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Components;
using CoCern.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CoCern.Tests;

public sealed class StandingIdentityTests
{
    [Theory]
    [InlineData("long")]
    [InlineData("format")]
    [InlineData("slashes")]
    [InlineData("percent")]
    public async Task Decisions_use_exact_identity_even_when_display_is_lossy_or_ambiguous(string scenario)
    {
        var (repository, boardId) = scenario switch
        {
            "long" => (new string('r', 200), new string('b', 100)),
            "format" => ("repo\u200E", "board\u202E"),
            "slashes" => ("repo/part", "board/part"),
            _ => ("repo%2Fpart", "board%25part")
        };
        var board = BoardReadModels.Standing(AggregateKinds.BoardKey(repository, boardId), "{}", null);
        var wire = JsonSerializer.SerializeToElement(board);
        Assert.True(wire.TryGetProperty("RepositoryId", out var exactRepository), "Projection must carry exact RepositoryId separately from display text.");
        Assert.True(wire.TryGetProperty("BoardId", out var exactBoard), "Projection must carry exact BoardId separately from display text.");
        Assert.Equal(repository, exactRepository.GetString());
        Assert.Equal(boardId, exactBoard.GetString());
        if (scenario is "long" or "format") Assert.NotEqual(repository + "/" + boardId, board.Board);

        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/dashboard/") };
        var view = new StandingView();
        typeof(StandingView).GetProperty("StandingHttp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, http);
        await Decide(view, board);
        var sent = Assert.Single(handler.Decisions);
        Assert.Equal((repository, boardId), (sent.RepositoryId, sent.BoardId));
        Assert.Equal("remove", sent.Action);
        Assert.Equal("rule", sent.Id);
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("/board")]
    [InlineData("repo/")]
    [InlineData("repo/board/extra")]
    [InlineData("repo/board%ZZ")]
    [InlineData("repo/board%")]
    [InlineData("repo/board%C3%28")]
    [InlineData("repo/board%0A")]
    [InlineData("repo%00/board")]
    [InlineData("repo/%20")]
    public async Task Malformed_or_control_containing_keys_never_fall_back_to_display(string key)
    {
        var board = BoardReadModels.Standing(key, "{}", null);
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/dashboard/") };
        var view = new StandingView();
        typeof(StandingView).GetProperty("StandingHttp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, http);
        await Decide(view, board with { Board = "repo/default" });
        Assert.Empty(handler.Decisions);
    }

    [Fact]
    public async Task Display_only_snapshots_cannot_authorize_decisions()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/dashboard/") };
        var view = new StandingView();
        typeof(StandingView).GetProperty("StandingHttp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, http);
        await Decide(view, new DashboardStanding { Board = "repo/default" });
        Assert.Empty(handler.Decisions);
    }

    [Theory]
    [InlineData("", "board")]
    [InlineData("repo", "")]
    [InlineData("repo\n", "board")]
    [InlineData("repo", "board\0")]
    [InlineData(" ", "board")]
    public async Task Invalid_exact_ids_cannot_use_a_valid_display_address(string repository, string boardId)
    {
        var board = JsonSerializer.Deserialize<DashboardStanding>(JsonSerializer.Serialize(new { Board = "repo/default", RepositoryId = repository, BoardId = boardId }))!;
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/dashboard/") };
        var view = new StandingView();
        typeof(StandingView).GetProperty("StandingHttp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, http);
        await Decide(view, board);
        Assert.Empty(handler.Decisions);
    }

    [Theory]
    [InlineData("repo", "board", "repo\u200E", "board")]
    [InlineData("repo/part", "board", "repo", "part/board")]
    public async Task Colliding_display_addresses_have_distinct_render_and_decision_keys(string firstRepository, string firstBoard, string secondRepository, string secondBoard)
    {
        var first = BoardReadModels.Standing(AggregateKinds.BoardKey(firstRepository, firstBoard), "{}", null);
        var second = BoardReadModels.Standing(AggregateKinds.BoardKey(secondRepository, secondBoard), "{}", null);
        Assert.Equal(first.Board, second.Board);
        using var services = new ServiceCollection().AddLogging().AddSingleton(new HttpClient()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<StandingView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Boards"] = new[] { first, second } }));
            Assert.Contains("Standing by board", rendered.ToHtmlString());
        });
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/dashboard/") };
        var view = new StandingView();
        typeof(StandingView).GetProperty("StandingHttp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, http);
        await Decide(view, first);
        await Decide(view, second);
        Assert.Equal(2, handler.Decisions.Count);
        Assert.NotEqual(handler.Decisions[0].RepositoryId, handler.Decisions[1].RepositoryId);
    }

    private static Task Decide(StandingView view, DashboardStanding board) =>
        (Task)typeof(StandingView).GetMethod("DecideStandingAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [board, "rule", "remove", "line"])!;

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<DashboardStandingDecision> Decisions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Decisions.Add((await request.Content!.ReadFromJsonAsync<DashboardStandingDecision>(cancellationToken))!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new DashboardStandingDecisionResult(true, "Ok", 0)) };
        }
    }
}

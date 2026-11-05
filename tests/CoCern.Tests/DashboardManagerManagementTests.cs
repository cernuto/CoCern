using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public class DashboardManagerManagementTests
{
    [Fact]
    public async Task Manager_routes_require_viewer_secure_transport_and_same_origin()
    {
        await using var host = Host();
        using var browser = Browser(host);
        foreach (var route in new[] { "/dashboard/api/manager", "/dashboard/api/manager/decide", "/dashboard/api/dispatch" })
        {
            using var anonymous = route.EndsWith("manager") ? await browser.GetAsync(route + "?repositoryId=repo") : await browser.PostAsJsonAsync(route, new { });
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var bearer = host.AuthorizedClient();
            using var noCookie = route.EndsWith("manager") ? await bearer.GetAsync(route + "?repositoryId=repo") : await bearer.PostAsJsonAsync(route, new { });
            Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        }
        await Login(browser);
        foreach (var route in new[] { "/dashboard/api/manager/decide", "/dashboard/api/dispatch" })
        {
            foreach (var origin in new[] { "", "https://foreign.example", "https://localhost, https://localhost" })
            {
                browser.DefaultRequestHeaders.Remove("Origin");
                if (origin.Length > 0) browser.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
                using var response = await browser.PostAsJsonAsync(route, new { repositoryId = "repo", requestId = "x", command = "pause" });
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        var app = new RecordingApplication();
        var remote = Context("{}");
        remote.Request.Scheme = "http"; remote.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.2");
        Assert.Equal(403, Status(await DashboardManagerManagement.ReadAsync(remote, app)));
        Assert.Equal(403, Status(await DashboardManagerManagement.DecideAsync(remote, app)));
        Assert.Equal(403, Status(await DashboardManagerManagement.DispatchAsync(remote, app)));
        var nonViewer = Context("{}"); nonViewer.User = new ClaimsPrincipal();
        Assert.Equal(403, Status(await DashboardManagerManagement.ReadAsync(nonViewer, app)));
        Assert.Empty(app.Calls);
    }

    [Fact]
    public async Task Dashboard_decisions_forward_only_allowlisted_fields_as_the_viewer()
    {
        var app = new RecordingApplication();
        var result = await DashboardManagerManagement.DecideAsync(Context("""
            {"repositoryId":"repo","requestId":"choose","command":"appoint_manager","targetSessionId":"exact","expectedManagerGeneration":0,"humanWords":"  Choose this chat.  ","IsAdministrator":true,"actor":"forged","at":"forged","sessionToken":"SECRET","role":"manager"}
            """), app);
        Assert.Equal(200, Status(result));
        var call = Assert.Single(app.Calls);
        Assert.Equal(("viewer", true), (call.Caller.PrincipalId, call.Caller.IsAdministrator));
        Assert.Equal(["repo"], call.Caller.Repositories);
        Assert.Equal(RelayTools.ControlRequest, call.Tool);
        Assert.Equal(new[] { "command", "expectedManagerGeneration", "humanWords", "repositoryId", "requestId", "targetSessionId" }, call.Args.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("  Choose this chat.  ", call.Args.GetProperty("humanWords").GetString());
        app.Calls.Clear();
        await DashboardManagerManagement.DispatchAsync(Context("""{"repositoryId":"repo","requestId":"stop","command":"stop","sessionId":"bad","actionId":"bad","sessionToken":"SECRET","allSessions":false}"""), app);
        call = Assert.Single(app.Calls);
        Assert.Equal(new[] { "allSessions", "command", "repositoryId", "requestId" }, call.Args.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(call.Args.GetProperty("allSessions").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_authorized_appointments_commit_one_generation_and_one_history_entry()
    {
        await using var host = Host();
        var first = await Open(host, "repo"); var second = await Open(host, "repo");
        using var viewer = Browser(host); await Login(viewer);
        var results = await Task.WhenAll(
            viewer.PostAsJsonAsync("/dashboard/api/manager/decide", Decision(first.Id, "one", 0)),
            viewer.PostAsJsonAsync("/dashboard/api/manager/decide", Decision(second.Id, "two", 0)));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, results.Select(r => r.StatusCode).Order().ToArray());
        var data = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<DashboardManagerResult>()));
        Assert.Single(data, r => r!.Ok);
        Assert.Equal(RelayErrorCodes.VersionConflict, Assert.Single(data, r => !r!.Ok)!.Code);
        var status = (await viewer.GetFromJsonAsync<DashboardManagerResult>("/dashboard/api/manager?repositoryId=repo"))!;
        Assert.Equal(1, status.Manager!.Generation); Assert.Single(status.Manager.History);
        Assert.DoesNotContain(first.Token, JsonSerializer.Serialize(status));
        Assert.DoesNotContain(second.Token, JsonSerializer.Serialize(status));
        foreach (var response in results) response.Dispose();
    }

    [Fact]
    public async Task Lost_decision_reply_replays_without_a_second_history_entry_or_changed_words()
    {
        await using var host = Host(); var session = await Open(host, "repo");
        using var viewer = Browser(host); await Login(viewer);
        var decision = Decision(session.Id, "lost-response", 0) with { HumanWords = "  Keep these exact words.  " };
        var first = await Post(viewer, decision);
        Assert.True(first.Ok);
        var retry = await Post(viewer, decision);
        Assert.True(retry.Ok); Assert.Equal(1, retry.Manager!.Generation);
        var current = (await viewer.GetFromJsonAsync<DashboardManagerResult>("/dashboard/api/manager?repositoryId=repo"))!;
        Assert.Single(current.Manager!.History); Assert.Equal(decision.HumanWords, current.Manager.History[0].HumanWords);
        var changed = await Post(viewer, decision with { HumanWords = "Changed words." });
        Assert.Equal("RequestConflict", changed.Code);
    }

    [Fact]
    public async Task Revoked_viewer_cookie_cannot_read_or_write_manager_or_dispatch()
    {
        await using var host = Host(); using var viewer = Browser(host); await Login(viewer);
        host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Relay:Dashboard:ViewerPrincipalIds:0"] = "revoked";
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/dashboard/api/manager?repositoryId=repo")).StatusCode);
        foreach (var route in new[] { "/dashboard/api/manager/decide", "/dashboard/api/dispatch" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync(route, new { repositoryId = "repo", requestId = "revoked", command = "resume" })).StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("appoint_manager")]
    [InlineData("")]
    public async Task Dispatch_rejects_commands_outside_its_allowlist(string command)
    {
        var app = new RecordingApplication();
        var context = Context(JsonSerializer.Serialize(new { repositoryId = "repo", requestId = "dispatch", command }));
        Assert.Equal(400, Status(await DashboardManagerManagement.DispatchAsync(context, app)));
        Assert.Empty(app.Calls);
    }

    private static Exception GrainFailure(string failure) => failure switch
    {
        "timeout" => new TimeoutException("fixture grain read timeout"),
        "silo" => new Orleans.Runtime.SiloUnavailableException("fixture silo unavailable"),
        "gateway" => new Orleans.Runtime.GatewayTooBusyException("fixture gateway busy"),
        "connection" => new Orleans.Runtime.Messaging.ConnectionFailedException("fixture connection unavailable"),
        _ => throw new ArgumentException("Unknown fixture failure", nameof(failure))
    };

    private sealed class ConflictRefreshApplication(Exception? failure = null, bool failInitial = false) : IRelayApplication
    {
        public List<string> Tools { get; } = [];
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Tools.Add(tool);
            if (failInitial || tool == RelayTools.GetStatus) throw failure ?? new HttpRequestException("status unavailable");
            return Task.FromResult(new RelayReply(false, RelayErrorCodes.VersionConflict, "{}", 0));
        }
    }

    internal static RelayHostFixture Host() => new(settings: new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true", ["Relay:Clients:0:Repositories:1"] = "other", ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user" });
    internal static HttpClient Browser(RelayHostFixture host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost"); return client;
    }
    internal static async Task Login(HttpClient browser) => (await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
    internal static async Task<(string Id, string Token)> Open(RelayHostFixture host, string repository)
    {
        var reply = await host.Services.GetRequiredService<RelayApplication>().ExecuteAsync(new RelayCaller("worker", false, [repository]), RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = repository, requestId = Guid.NewGuid().ToString("N"), label = "Same label" }));
        Assert.True(reply.Ok, reply.Code);
        var data = JsonNode.Parse(reply.DataJson)!; return (data["sessionId"]!.GetValue<string>(), data["sessionToken"]!.GetValue<string>());
    }
    private static DashboardManagerDecision Decision(string id, string request, long generation) => new("repo", request, "appoint_manager", id, generation, "Choose this exact chat.");
    private static async Task<DashboardManagerResult> Post(HttpClient browser, DashboardManagerDecision decision)
    {
        using var response = await browser.PostAsJsonAsync("/dashboard/api/manager/decide", decision);
        return (await response.Content.ReadFromJsonAsync<DashboardManagerResult>())!;
    }
    internal static DefaultHttpContext Context(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https"; context.Request.Host = new("localhost"); context.Request.Headers.Origin = "https://localhost";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "viewer")], "test"));
        return context;
    }
    private static int? Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode;
    internal sealed class RecordingApplication : IRelayApplication
    {
        public List<(RelayCaller Caller, string Tool, JsonElement Args)> Calls { get; } = [];
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add((caller, tool, arguments.Clone()));
            return Task.FromResult(new RelayReply(true, "Ok", """{"paused":true,"manager":{"generation":0,"current":null,"history":[],"nextHistoryBefore":null,"historyTruncated":false}}""", 0));
        }
    }
}

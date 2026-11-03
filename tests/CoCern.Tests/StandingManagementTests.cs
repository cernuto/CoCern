using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CoCern.Tests;

public sealed class StandingManagementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");

    [Theory]
    [InlineData(null)]
    [InlineData("https://other.example")]
    public async Task Missing_or_foreign_origin_never_calls_domain(string? origin)
    {
        var app = new LocalApplication();
        var context = Context(Decision("p", "approve"));
        context.Request.Headers.Remove("Origin");
        if (origin is not null) context.Request.Headers.Origin = origin;
        Assert.Equal(403, Status(await DashboardStandingManagement.DecideAsync(context, app)));
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task Remote_plaintext_and_missing_viewer_are_refused()
    {
        var app = new LocalApplication();
        var context = Context(Decision("p", "approve"));
        context.Request.Scheme = "http";
        context.Request.Headers.Origin = "http://localhost";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.Equal(403, Status(await DashboardStandingManagement.DecideAsync(context, app)));
        context = Context(Decision("p", "approve"));
        context.User = new ClaimsPrincipal();
        Assert.Equal(403, Status(await DashboardStandingManagement.DecideAsync(context, app)));
        Assert.Equal(0, app.Calls);
    }

    [Theory]
    [InlineData("expectedRevision")]
    [InlineData("requestId")]
    [InlineData("repositoryId")]
    [InlineData("boardId")]
    [InlineData("id")]
    public async Task Required_fields_are_not_inferred(string field)
    {
        var app = new LocalApplication();
        var body = JsonSerializer.SerializeToNode(Decision("p", "approve"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        body.Remove(field);
        Assert.Equal(400, Status(await DashboardStandingManagement.DecideAsync(Context(body), app)));
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task Arbitrary_mutations_and_oversized_bodies_are_refused()
    {
        var app = new LocalApplication();
        Assert.Equal(400, Status(await DashboardStandingManagement.DecideAsync(Context(Decision("p", "grants")), app)));
        var context = Context(Decision("p", "approve"));
        context.Request.Body = new MemoryStream(new byte[16385]);
        Assert.Equal(413, Status(await DashboardStandingManagement.DecideAsync(context, app)));
        Assert.Equal(0, app.Calls);
    }

    [Fact]
    public async Task Dismissal_keeps_revision_and_history_and_competing_decisions_find_missing_proposal()
    {
        var app = new LocalApplication();
        var id = app.ProposeLine();
        var decision = Decision(id, "remove");
        Assert.Equal(200, Status(await DashboardStandingManagement.DecideAsync(Context(decision), app)));
        Assert.Equal(0, app.LastReply!.Version);
        var committed = app.State.Value.Json;
        Assert.Contains("-proposal:" + id, committed);
        // A lost response retries exactly the same request; no second history entry.
        Assert.Equal(200, Status(await DashboardStandingManagement.DecideAsync(Context(decision), app)));
        Assert.Equal(committed, app.State.Value.Json);
        Assert.Equal(404, Status(await DashboardStandingManagement.DecideAsync(Context(Decision(id, "approve")), app)));
        Assert.Equal("NotFound", app.LastReply!.Code);
        Assert.Equal(404, Status(await DashboardStandingManagement.DecideAsync(Context(Decision(id, "remove")), app)));
        Assert.Equal(committed, app.State.Value.Json);
        Assert.Equal("viewer", app.LastCaller!.PrincipalId);
        Assert.True(app.LastCaller.IsAdministrator);
        Assert.False(app.LastCaller.IsInternal);
    }

    [Fact]
    public async Task Approval_changes_revision_and_competing_old_decision_is_stale()
    {
        var app = new LocalApplication();
        var id = app.ProposeLine();
        Assert.Equal(200, Status(await DashboardStandingManagement.DecideAsync(Context(Decision(id, "approve")), app)));
        Assert.Equal(1, app.LastReply!.Version);
        var committed = app.State.Value.Json;
        Assert.Equal(409, Status(await DashboardStandingManagement.DecideAsync(Context(Decision(id, "remove")), app)));
        Assert.Equal("VersionConflict", app.LastReply!.Code);
        Assert.Equal(committed, app.State.Value.Json);
        Assert.Equal(404, Status(await DashboardStandingManagement.DecideAsync(Context(Decision(id, "approve") with { ExpectedRevision = 1 }), app)));
        Assert.Equal(200, Status(await DashboardStandingManagement.DecideAsync(Context(Decision("rule", "remove") with { ExpectedRevision = 1 }), app)));
        Assert.Equal(2, app.LastReply!.Version);
        Assert.Contains("-line:rule", app.State.Value.Json);
    }

    [Fact]
    public async Task Expired_pending_grant_requires_explicit_dismissal_and_cannot_be_approved()
    {
        var app = new LocalApplication();
        var proposed = app.Apply("standing_propose", new JsonObject
        {
            ["sessionId"] = "worker-session", ["grant"] = new JsonObject
            {
                ["id"] = "brief", ["scope"] = "repository", ["expiresAt"] = Now.AddSeconds(30).ToString("O")
            }
        });
        Assert.True(proposed.Ok, proposed.Code);
        var id = JsonNode.Parse(proposed.DataJson)!["proposalId"]!.GetValue<string>();
        app.Clock = Now.AddMinutes(1);
        var before = app.State.Value.Json;
        Assert.Equal(400, Status(await DashboardStandingManagement.DecideAsync(Context(Decision(id, "approve")), app)));
        Assert.Equal("GrantInvalid", app.LastReply!.Code);
        Assert.Equal(before, app.State.Value.Json);
        Assert.Equal(200, Status(await DashboardStandingManagement.DecideAsync(Context(Decision(id, "remove")), app)));
        Assert.Equal(0, app.LastReply!.Version);
    }

    [Fact]
    public async Task Approved_expired_grant_is_retained_until_explicit_removal_with_history()
    {
        var app = new LocalApplication();
        var put = app.Apply("standing_put", new JsonObject
        {
            ["expectedRevision"] = 0, ["grants"] = new JsonArray(new JsonObject
            {
                ["id"] = "brief", ["scope"] = "repository", ["expiresAt"] = Now.AddSeconds(30).ToString("O")
            })
        }, new RelayCaller("admin", true, ["repo"]));
        Assert.True(put.Ok, put.Code);
        app.Clock = Now.AddMinutes(1);
        Assert.Contains("brief", app.State.Value.Json);
        Assert.Equal(200, Status(await DashboardStandingManagement.DecideAsync(Context(Decision("brief", "remove") with { ExpectedRevision = 1 }), app)));
        Assert.Equal(2, app.LastReply!.Version);
        Assert.Contains("-grant:brief", app.State.Value.Json);
    }

    [Fact]
    public async Task Registered_route_requires_viewer_cookie_and_checks_origin_after_login()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
        });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/dashboard/api/standing/decide", Decision("missing", "remove"))).StatusCode);
        (await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/dashboard/api/standing/decide", Decision("missing", "remove"))).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/dashboard/api/standing/decide", Decision("missing", "remove"))).StatusCode);
    }

    private static DashboardStandingDecision Decision(string id, string action) => new()
    {
        RepositoryId = "repo", BoardId = "default", Id = id, Action = action, ExpectedRevision = 0, RequestId = Guid.NewGuid().ToString("N")
    };

    private static DefaultHttpContext Context(object body)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = "https://localhost";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "viewer")], "test"));
        return context;
    }

    private static int? Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode;

    private sealed class LocalApplication : IRelayApplication
    {
        public StateEnvelope<CoordinationDocument> State { get; } = new();
        public DateTimeOffset Clock { get; set; } = Now;
        public int Calls { get; private set; }
        public RelayReply? LastReply { get; private set; }
        public RelayCaller? LastCaller { get; private set; }

        public RelayReply Apply(string tool, JsonObject args, RelayCaller? caller = null)
        {
            args["repositoryId"] = "repo";
            args["requestId"] ??= Guid.NewGuid().ToString("N");
            return CoordinationStateMachine.Apply(State, "board", "repo/default", caller ?? new RelayCaller("worker", false, ["repo"], true), tool, args, Clock);
        }

        public string ProposeLine()
        {
            var reply = Apply("standing_propose", new JsonObject { ["sessionId"] = "worker-session", ["line"] = new JsonObject { ["id"] = "rule", ["text"] = "Run smoke tests." } });
            Assert.True(reply.Ok, reply.Code);
            return JsonNode.Parse(reply.DataJson)!["proposalId"]!.GetValue<string>();
        }

        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCaller = caller;
            Assert.Equal("standing_put", tool);
            LastReply = Apply(tool, JsonNode.Parse(arguments.GetRawText())!.AsObject(), caller);
            return Task.FromResult(LastReply);
        }
    }
}

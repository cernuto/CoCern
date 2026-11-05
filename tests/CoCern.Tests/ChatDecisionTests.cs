using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

/// <summary>Protocol/aggregate evidence only: these fixtures never start native chats or record a human answer.</summary>
public sealed class ChatDecisionTests
{
    private static readonly RelayCaller Worker = new("test-user", false, ["repo", "other"]);
    private static readonly RelayCaller Admin = new("fixture-admin", true, ["repo", "other"]);

    [Fact]
    public async Task Racing_verified_sessions_present_once_and_reopening_does_not_repeat_or_authorize()
    {
        var database = Path.Combine(Path.GetTempPath(), "cocern-chat-decisions-" + Guid.NewGuid().ToString("N"), "relay.db");
        string first, second, tokenFirst, tokenSecond, proposal;
        await using (var fixture = new Fixture(database))
        {
            var a = await fixture.Open(); var b = await fixture.Open();
            (first, tokenFirst, second, tokenSecond) = (a.Id, a.Token, b.Id, b.Token);
            proposal = await fixture.Propose(a, new() { ["line"] = new JsonObject { ["text"] = "Ask before changing external credentials." } });
            var results = await Task.WhenAll(fixture.Boundary(a, present: true), fixture.Boundary(b, present: true));
            var winner = Assert.Single(results.Select((reply, index) => (reply, index)), item => Question(item.reply) is not null);
            var pending = await fixture.Pending(proposal);
            Assert.Equal(winner.index == 0 ? a.Id : b.Id, (string?)pending["presentedBy"]);
            Assert.Equal("test-user", (string?)pending["presentedPrincipal"]);
            Assert.NotNull(pending["presentedAt"]);
            Assert.Equal(0, (long?)pending["presentedRevision"]);
            await fixture.AssertNoAuthority();
            Assert.Null(Question(await fixture.Boundary(a, present: true)));
            Assert.Null(Question(await fixture.Boundary(b, present: true)));
        }
        await using (var reopened = new Fixture(database))
        {
            Assert.Null(Question(await reopened.Boundary(new(first, tokenFirst), present: true)));
            Assert.Null(Question(await reopened.Boundary(new(second, tokenSecond), present: true)));
            Assert.NotNull((await reopened.Pending(proposal))["presentedBy"]);
            await reopened.AssertNoAuthority();
        }
    }

    [Fact]
    public async Task Grant_summary_preserves_scope_targets_absolute_expiry_and_replacement_effect()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var oldExpiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O");
        var expiry = DateTimeOffset.UtcNow.AddHours(4).ToString("O");
        await fixture.Put(new() { ["grants"] = new JsonArray(Grant("resource", ["old-resource"], oldExpiry, "grant")) });
        await fixture.Propose(session, new() { ["grant"] = Grant("resource", ["first-resource", "second-resource"], expiry, "grant") });
        var offered = Question(await fixture.Boundary(session, present: true));
        Assert.NotNull(offered);
        Assert.Equal("resource", (string?)offered["grant"]?["scope"]);
        Assert.Equal(new[] { "first-resource", "second-resource" }, offered["grant"]!["targets"]!.AsArray().Select(value => (string)value!).ToArray());
        Assert.Equal(expiry, (string?)offered["grant"]?["expiresAt"]);
        Assert.Equal("old-resource", (string?)offered["replaces"]?["grant"]?["targets"]?[0]);
        Assert.Equal(oldExpiry, (string?)offered["replaces"]?["grant"]?["expiresAt"]);
    }

    [Theory]
    [InlineData("unmarked")]
    [InlineData("background-false-only")]
    [InlineData("attention-false")]
    [InlineData("waiting")]
    [InlineData("unsupported")]
    [InlineData("failed")]
    [InlineData("bad-session")]
    public async Task Ineligible_boundaries_leave_the_proposal_unpresented(string scenario)
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var id = await fixture.Propose(session, Line("Still pending"));
        var args = fixture.Args(session);
        if (scenario is not ("unmarked" or "background-false-only")) args["presentStanding"] = true;
        var tool = RelayTools.CheckInbox;
        if (scenario == "background-false-only") args["backgroundCall"] = false;
        if (scenario == "attention-false") args["attention"] = false;
        if (scenario == "waiting") args["waitSeconds"] = 0;
        if (scenario == "unsupported") tool = RelayTools.GetStatus;
        if (scenario == "failed") { tool = RelayTools.TaskUpdate; args["taskId"] = "missing"; args["expectedVersion"] = 999; }
        if (scenario == "bad-session") args["sessionToken"] = "wrong";
        var reply = await fixture.Call(Worker, tool, args);
        Assert.Null(Question(reply));
        Assert.Null((await fixture.Pending(id))["presentedBy"]);
        await fixture.AssertNoAuthority();
    }

    [Fact]
    public async Task Same_request_can_recover_its_immutable_offer_but_a_new_boundary_cannot()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        await fixture.Propose(session, Line("One request, one offer"));
        var first = Question(await fixture.Boundary(session, present: true, request: "stable-read"));
        Assert.NotNull(first);
        var replay = Question(await fixture.Boundary(session, present: true, request: "stable-read"));
        Assert.True(JsonNode.DeepEquals(first, replay));
        Assert.Null(Question(await fixture.Boundary(session, present: true, request: "new-read")));
    }

    [Fact]
    public async Task Changed_replacement_terms_need_a_fresh_presentation_while_unrelated_revision_does_not()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        await fixture.Put(new() { ["lines"] = new JsonArray(new JsonObject { ["id"] = "rule", ["text"] = "Original" }) });
        await fixture.Propose(session, Line("Proposed", "rule"));
        Assert.NotNull(Question(await fixture.Boundary(session, present: true)));
        await fixture.Put(new() { ["lines"] = new JsonArray(new JsonObject { ["id"] = "unrelated", ["text"] = "Other rule" }) });
        Assert.Null(Question(await fixture.Boundary(session, present: true)));
        await fixture.Put(new() { ["lines"] = new JsonArray(new JsonObject { ["id"] = "rule", ["text"] = "Changed by administrator" }) });
        var changed = Question(await fixture.Boundary(session, present: true));
        Assert.NotNull(changed);
        Assert.Equal("Changed by administrator", (string?)changed["replaces"]?["line"]?["text"]);
    }

    [Fact]
    public async Task A_dismissal_racing_a_presentation_never_leaves_an_orphan_or_authority()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var id = await fixture.Propose(session, Line("May be dismissed"));
        await Task.WhenAll(fixture.Boundary(session, present: true), fixture.Put(new() { ["remove"] = new JsonArray(id) }));
        var view = await fixture.View();
        Assert.Empty(view["proposals"]!.AsArray());
        await fixture.AssertNoAuthority();
        Assert.Null(Question(await fixture.Boundary(session, present: true)));
    }

    [Fact]
    public async Task Public_authorized_http_boundary_uses_the_verified_session_without_admin_rights()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var expected = await fixture.Propose(session, Line("Public boundary"));
        using var client = fixture.Host.AuthorizedClient();
        var args = fixture.Args(session); args["presentStanding"] = true;
        var reply = await fixture.Host.Call(client, RelayTools.CheckInbox, args);
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(expected, reply.GetProperty("dataJson").ValueKind == JsonValueKind.String
            ? (string?)JsonNode.Parse(reply.GetProperty("dataJson").GetString()!)?["attention"]?["id"]
            : null);
        var denied = await fixture.Call(Worker, RelayTools.StandingPut, new() { ["repositoryId"] = "repo", ["requestId"] = "worker-put", ["expectedRevision"] = 0, ["approve"] = new JsonArray(expected) });
        Assert.False(denied.Ok);
        await fixture.AssertNoAuthority();
    }

    private static JsonObject Line(string text, string? id = null) => new() { ["line"] = id is null ? new JsonObject { ["text"] = text } : new JsonObject { ["id"] = id, ["text"] = text } };
    private static JsonObject Grant(string scope, string[] targets, string expiry, string? id = null)
    {
        var grant = new JsonObject { ["scope"] = scope, ["targets"] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray()), ["expiresAt"] = expiry };
        if (id is not null) grant["id"] = id;
        return grant;
    }
    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
    private static JsonObject? Question(RelayReply reply) => Data(reply)["attention"] is JsonObject item && (string?)item["k"] == AttentionKinds.StandingProposal ? item : null;
    private sealed record Session(string Id, string Token);

    private sealed class Fixture : IAsyncDisposable
    {
        public RelayHostFixture Host { get; }
        public RelayApplication Application => Host.Services.GetRequiredService<RelayApplication>();
        public ITokenCounter Counter => Host.Services.GetRequiredService<ITokenCounter>();
        public Fixture(string? database = null, bool attention = true, bool enforceTokens = false, bool dispatchMessages = false)
        {
            Host = new RelayHostFixture(databasePath: database, disableDispatcher: !dispatchMessages, settings: new Dictionary<string, string?>
            {
                ["Relay:Clients:0:Token"] = RelayHostFixture.Token,
                ["Relay:Clients:0:TokenFile"] = null,
                ["Relay:Clients:0:PrincipalId"] = "test-user",
                ["Relay:Clients:0:IsAdministrator"] = "false",
                ["Relay:Clients:0:Repositories:0"] = "repo",
                ["Relay:Clients:0:Repositories:1"] = "other",
                ["Relay:Attention:Enabled"] = attention.ToString(),
                ["Relay:Activity:Enabled"] = "false",
                ["Relay:Analytics:Enabled"] = "false",
                ["Relay:Local:HttpsPort"] = "0",
                ["Relay:Network:Enabled"] = "false",
                ["Relay:Tokens:Enabled"] = enforceTokens.ToString(),
                ["Relay:Tokens:EnforceTools:0"] = RelayTools.CheckInbox,
                ["urls"] = "http://127.0.0.1:0"
            });
        }
        public async Task<Session> Open(string repository = "repo")
        {
            var reply = await Call(Worker, RelayTools.SessionOpen, new() { ["repositoryId"] = repository, ["requestId"] = Guid.NewGuid().ToString("N") });
            Assert.True(reply.Ok, reply.Code);
            var data = Data(reply);
            return new((string)data["sessionId"]!, (string)data["sessionToken"]!);
        }
        public JsonObject Args(Session session, string board = "default", string repository = "repo") => new()
        {
            ["repositoryId"] = repository, ["boardId"] = board, ["sessionId"] = session.Id, ["sessionToken"] = session.Token, ["requestId"] = Guid.NewGuid().ToString("N")
        };
        public Task<RelayReply> Call(RelayCaller caller, string tool, JsonObject args) => Application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
        public async Task<string> Propose(Session session, JsonObject fields, string board = "default", string repository = "repo")
        {
            var args = Args(session, board, repository); foreach (var field in fields) args[field.Key] = field.Value?.DeepClone();
            var reply = await Call(Worker, RelayTools.StandingPropose, args); Assert.True(reply.Ok, reply.Code);
            return (string)Data(reply)["proposalId"]!;
        }
        public Task<RelayReply> Boundary(Session session, bool present = false, string? request = null)
        {
            var args = Args(session); if (present) args["presentStanding"] = true; if (request is not null) args["requestId"] = request;
            return Call(Worker, RelayTools.CheckInbox, args);
        }
        public async Task<JsonObject> View(string board = "default", string repository = "repo")
        {
            var reply = await Call(Admin, RelayTools.TaskGet, new() { ["repositoryId"] = repository, ["boardId"] = board, ["section"] = "standing" });
            Assert.True(reply.Ok, reply.Code); return Data(reply);
        }
        public async Task<JsonObject> Pending(string id, string board = "default", string repository = "repo") =>
            (await View(board, repository))["proposals"]!.AsArray().OfType<JsonObject>().Single(item => (string?)item["id"] == id);
        public async Task<RelayReply> Put(JsonObject fields, string board = "default")
        {
            var args = new JsonObject { ["repositoryId"] = "repo", ["boardId"] = board, ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedRevision"] = (await View(board))["revision"]!.DeepClone() };
            foreach (var field in fields) args[field.Key] = field.Value?.DeepClone();
            var reply = await Call(Admin, RelayTools.StandingPut, args); Assert.True(reply.Ok, reply.Code); return reply;
        }
        public async Task Raise(Session session, string kind, string subject)
        {
            var args = Args(session); args["kind"] = kind; args["subject"] = subject; args["taskId"] = subject; args["note"] = "fixture attention";
            var caller = new RelayCaller(Worker.PrincipalId, false, ["repo"], true);
            var reply = await CoordinationGrain.Aggregate(Host.Services.GetRequiredService<Orleans.IGrainFactory>(), AggregateKinds.Session, session.Id)
                .ExecuteAsync(caller, "__attention_raise", args.ToJsonString());
            Assert.True(reply.Ok, reply.Code);
        }
        public async Task AssertNoAuthority()
        {
            var view = await View(); Assert.Equal(0, (long?)view["revision"]); Assert.Empty(view["lines"]!.AsArray()); Assert.Empty(view["grants"]!.AsArray());
        }
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}

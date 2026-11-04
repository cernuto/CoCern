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
    public void Creating_a_proposal_emits_no_manager_session_attention()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var reply = CoordinationStateMachine.Apply(board, AggregateKinds.Board, "repo/default", Worker, RelayTools.StandingPropose,
            new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = "fixture-session", ["requestId"] = "propose", ["line"] = new JsonObject { ["text"] = "No manager fanout" } }, DateTimeOffset.UtcNow);
        Assert.True(reply.Ok, reply.Code);
        Assert.Empty(board.Effects);
    }

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
    public async Task Nondefault_board_pending_proposal_is_not_hidden_behind_other_boards_or_other_repositories()
    {
        await using var fixture = new Fixture();
        var session = await fixture.Open();
        for (var index = 0; index < 36; index++)
            await fixture.Put(new() { ["lines"] = new JsonArray(new JsonObject { ["id"] = "rule", ["text"] = "fixture rule" }) }, board: "a-" + index.ToString("D2"));
        var foreign = await fixture.Open(repository: "other");
        var foreignProposal = await fixture.Propose(foreign, Line("Foreign proposal"), repository: "other");
        var expected = await fixture.Propose(session, Line("Only the current repository"), board: "z-last");
        var offered = Question(await fixture.Boundary(session, present: true));
        Assert.NotNull(offered);
        Assert.Equal(expected, (string?)offered["id"]);
        Assert.Equal("z-last", (string?)offered["b"]);
        Assert.Null((await fixture.Pending(foreignProposal, repository: "other"))["presentedBy"]);
    }

    [Fact]
    public async Task A_replacement_describes_both_the_new_line_and_the_exact_current_line()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        await fixture.Put(new() { ["lines"] = new JsonArray(new JsonObject { ["id"] = "rule", ["text"] = "Current rule" }) });
        var proposal = await fixture.Propose(session, Line("Replacement rule", "rule"));
        var offered = Question(await fixture.Boundary(session, present: true));
        Assert.NotNull(offered);
        Assert.Equal(proposal, (string?)offered["id"]);
        Assert.Equal("Replacement rule", (string?)offered["line"]?["text"]);
        Assert.Equal("Current rule", (string?)offered["replaces"]?["line"]?["text"]);
        Assert.Equal(1, (long?)(await fixture.View())["revision"]);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Background_telemetry_does_not_change_explicit_presentation_admission(bool background)
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var expected = await fixture.Propose(session, Line("Telemetry has no authority"));
        var args = fixture.Args(session); args["presentStanding"] = true; args["backgroundCall"] = background;
        Assert.Equal(expected, (string?)Question(await fixture.Call(Worker, RelayTools.CheckInbox, args))?["id"]);
    }

    [Theory]
    [InlineData("task_update")]
    [InlineData("task_claim")]
    [InlineData("action_report")]
    public async Task Each_successful_verified_mutating_boundary_can_present_without_changing_Standing(string tool)
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var expected = await fixture.Propose(session, Line("Ordinary boundary"));
        var args = fixture.Args(session); args["taskId"] = "fixture-task"; args["expectedVersion"] = 0;
        if (tool != RelayTools.TaskUpdate)
        {
            Assert.True((await fixture.Call(Worker, RelayTools.TaskUpdate, args.DeepClone().AsObject())).Ok);
            args["requestId"] = Guid.NewGuid().ToString("N"); args["expectedVersion"] = 1; args["resourceIds"] = new JsonArray();
        }
        if (tool == RelayTools.ActionReport)
        {
            Assert.True((await fixture.Call(Worker, RelayTools.TaskClaim, args.DeepClone().AsObject())).Ok);
            args["requestId"] = Guid.NewGuid().ToString("N"); args["expectedVersion"] = 2;
            args["actionId"] = "fixture-action"; args["inputFingerprint"] = "fixture-input";
            Assert.True((await fixture.Call(Worker, RelayTools.ActionBegin, args.DeepClone().AsObject())).Ok);
            args["requestId"] = Guid.NewGuid().ToString("N"); args["expectedVersion"] = 3;
            args["attempt"] = 1; args["outcome"] = "completed";
        }
        args["presentStanding"] = true;
        var reply = await fixture.Call(Worker, tool, args);
        Assert.True(reply.Ok, reply.Code);
        Assert.Equal(expected, (string?)Question(reply)?["id"]);
        await fixture.AssertNoAuthority();
    }

    [Fact]
    public async Task Attention_disabled_at_the_host_prevents_presentation()
    {
        await using var fixture = new Fixture(attention: false); var session = await fixture.Open();
        var id = await fixture.Propose(session, Line("Attention is disabled"));
        Assert.Null(Question(await fixture.Boundary(session, present: true)));
        Assert.Null((await fixture.Pending(id))["presentedBy"]);
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
    public async Task Requested_question_outranks_a_tip_without_marking_the_tip_shown()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        await fixture.Raise(session, AttentionKinds.Tip, "tip");
        var expected = await fixture.Propose(session, Line("Question before a tip"));
        Assert.Equal(expected, (string?)Question(await fixture.Boundary(session, present: true))?["id"]);
        var next = await fixture.Boundary(session);
        Assert.Equal(AttentionKinds.Tip, (string?)Data(next)["attention"]?["k"]);
    }

    [Fact]
    public async Task Urgent_attention_defers_the_question_without_claiming_it()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var id = await fixture.Propose(session, Line("Urgent attention first"));
        await fixture.Raise(session, AttentionKinds.Failed, "failed-task");
        var reply = await fixture.Boundary(session, present: true);
        Assert.Equal(AttentionKinds.Failed, (string?)Data(reply)["attention"]?["k"]);
        Assert.Null(Question(reply));
        Assert.Null((await fixture.Pending(id))["presentedBy"]);
    }

    [Fact]
    public async Task A_complete_escaped_summary_survives_actual_reply_compaction()
    {
        await using var fixture = new Fixture(enforceTokens: true, dispatchMessages: true); var a = await fixture.Open(); var b = await fixture.Open();
        var exact = string.Concat(Enumerable.Repeat("\"雪<&>", 35));
        await fixture.Propose(a, Line(exact));
        for (var index = 0; index < 30; index++)
        {
            var args = fixture.Args(b); args["recipientSessionId"] = a.Id; args["text"] = $"Message {index}: " + string.Concat(Enumerable.Repeat("7 3 9 1 4 ", 35));
            Assert.True((await fixture.Call(Worker, RelayTools.SendMessage, args)).Ok);
        }
        async Task<int> Waiting()
        {
            var args = fixture.Args(a); args["attentionDetail"] = true;
            return (int)Data(await fixture.Call(Worker, RelayTools.GetStatus, args))["attention"]!["actionable"]!;
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (await Waiting() < 30 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(30, await Waiting());
        var read = fixture.Args(a); read["presentStanding"] = true; read["limit"] = 50;
        var reply = await fixture.Call(Worker, RelayTools.CheckInbox, read);
        var offered = Question(reply);
        Assert.NotNull(offered);
        Assert.Equal(exact, (string?)offered["line"]?["text"]);
        Assert.True((bool?)Data(reply)["compacted"]);
        Assert.True(Replies.FitsWire(reply));
        Assert.True(fixture.Counter.Count(reply.DataJson) <= RelayToolCatalog.All.Single(tool => tool.Name == RelayTools.CheckInbox).TokenBudget);
    }

    [Fact]
    public async Task An_oversized_complete_summary_is_not_reserved_or_partially_presented()
    {
        await using var fixture = new Fixture(); var session = await fixture.Open();
        var targets = Enumerable.Range(0, 16).Select(index => index.ToString("D2") + new string('x', 250)).ToArray();
        var id = await fixture.Propose(session, new() { ["grant"] = Grant("resource", targets, DateTimeOffset.UtcNow.AddHours(1).ToString("O")) });
        Assert.Null(Question(await fixture.Boundary(session, present: true)));
        Assert.Null((await fixture.Pending(id))["presentedBy"]);
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

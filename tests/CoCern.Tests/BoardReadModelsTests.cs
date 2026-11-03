using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The Domain's standing panel (<see cref="BoardReadModels.Standing"/>) on a board the Domain built (<see cref="DomainBoard"/>): it shows
/// what the operations wrote, under the keys they wrote it. Storage's read of the same board is checked against it in
/// <see cref="StoredKeyPathTests"/>, and the dashboard's end to end in <see cref="DashboardTests"/>.
/// </summary>
public sealed class BoardReadModelsTests
{
    private static readonly DateTimeOffset Now = DomainBoard.Now;

    [Fact]
    public void The_panel_shows_the_standing_and_orders_the_domain_wrote()
    {
        var (built, proposed) = DomainBoard.Build();
        var json = JsonNode.Parse(built.Value.Json)!.AsObject();
        var board = BoardReadModels.Standing(DomainBoard.Key, json[BoardReadModels.StandingKey]!.ToJsonString(), json[BoardReadModels.OrdersKey]!.ToJsonString());

        Assert.Equal(("repo/default", 2L, true), (board.Board, board.Revision, board.RequireResources));
        var line = Assert.Single(board.Lines);
        Assert.Equal(("css", "Never deploy CSS without a restoration receipt.", "bob", "bob", Now.AddSeconds(110)), (line.Id, line.Text, line.Author, line.ApprovedBy, line.ApprovedAt));
        Assert.Collection(board.Grants,
            grant =>
            {
                Assert.Equal(("rigs", "resource", DomainBoard.GrantExpiry, "bob", "", Now.AddSeconds(110)), (grant.Id, grant.Scope, grant.ExpiresAt, grant.Grantor, grant.ProposedBy, grant.GrantedAt));
                Assert.Equal(["rig-1"], grant.Targets);
            },
            // An approved proposal that named no ID becomes a grant under the proposal's, with its proposer.
            grant =>
            {
                Assert.Equal((proposed.ApprovedGrant, "resource", DomainBoard.ProposedGrantExpiry, "bob", "alice", Now.AddSeconds(124)), (grant.Id, grant.Scope, grant.ExpiresAt, grant.Grantor, grant.ProposedBy, grant.GrantedAt));
                Assert.Equal(["rig-3"], grant.Targets);
            });
        // The approved proposal is no longer pending; a proposal replaces a line or grant when the ID it would be written under exists.
        Assert.Equal(
            [
                (proposed.NewLine, "line", "Report before releasing a rig.", "alice", Now.AddSeconds(120), false),
                (proposed.ReplacingLine, "line", "Deploy CSS only with a receipt.", "alice", Now.AddSeconds(121), true),
                (proposed.ReplacingGrant, "grant", $"resource: rig-1, rig-2 until {DomainBoard.ProposedGrantExpiry.ToUniversalTime():O}", "alice", Now.AddSeconds(122), true)
            ],
            board.Proposals.Select(proposal => (proposal.Id, proposal.Kind, proposal.Summary, proposal.ProposedBy, proposal.ProposedAt ?? default, proposal.Replaces)));
        // Newest first.
        Assert.Collection(board.History,
            change =>
            {
                Assert.Equal((2L, "bob", Now.AddSeconds(124)), (change.Revision, change.By, change.At));
                Assert.Equal(["approve:" + proposed.ApprovedGrant, "+grant:" + proposed.ApprovedGrant], change.Changes);
            },
            change =>
            {
                Assert.Equal((1L, "bob", Now.AddSeconds(110)), (change.Revision, change.By, change.At));
                Assert.Equal(["+line:css", "+grant:rigs", "settings:requireResources=true"], change.Changes);
            });

        Assert.Collection(board.Orders,
            order =>
            {
                Assert.Equal(("o-1", OrderTriggers.WorkflowCompleted, "plan", "bob", Now.AddSeconds(130).AddDays(7)), (order.Id, order.Trigger, order.Target, order.ApprovedBy, order.ExpiresAt));
                Assert.Equal((0L, 0L, false, ""), (order.Runs, order.Failures, order.Disabled, order.LastError));
                Assert.Equal([OrderActions.ReleaseClaims], order.Actions);
            },
            // A resource trigger's target is its resource; it ran once, then failed until it was disabled.
            order =>
            {
                Assert.Equal(("o-2", OrderTriggers.ClaimReleased, "rig-1", "bob"), (order.Id, order.Trigger, order.Target, order.ApprovedBy));
                Assert.Equal((1L, (long)RelayLimits.MaxOrderFailures, true, "watch refused " + RelayLimits.MaxOrderFailures), (order.Runs, order.Failures, order.Disabled, order.LastError));
                Assert.Equal(["offer plan to b", "notify to c"], order.Actions);
            });
        Assert.Equal(
            [
                ("o-2", "failed and disabled: watch refused 3"), ("o-2", "failed: watch refused 2"), ("o-2", "failed: watch refused 1"), ("o-2", "ran on claim_released rig-1"),
                ("o-2", "put by bob"), ("o-1", "put by bob")
            ],
            board.OrderLog.Select(entry => (entry.OrderId, entry.What)));
        Assert.Equal(Now.AddSeconds(140 + RelayLimits.MaxOrderFailures), board.OrderLog[0].At);
    }

    [Fact]
    public void A_board_with_orders_and_no_standing_shows_its_orders()
    {
        var board = BoardReadModels.Standing("repo/default", null, """{"items":{"o":{"id":"o","trigger":{"on":"workflow_completed","taskId":"T-1"},"actions":[{"do":"notify","sessionId":"s-1"}]}}}""");
        Assert.Equal(("repo/default", 0L, false), (board.Board, board.Revision, board.RequireResources));
        Assert.Empty(board.Lines);
        Assert.Equal(["notify to s-1"], Assert.Single(board.Orders).Actions);
        Assert.Empty(board.OrderLog);
    }

    [Fact]
    public void History_and_the_order_log_show_their_newest_entries_up_to_their_limits()
    {
        var history = new JsonArray(Enumerable.Range(1, RelayLimits.StandingHistoryEntries + 5).Select(revision => (JsonNode)new JsonObject { ["revision"] = revision }).ToArray());
        var log = new JsonArray(Enumerable.Range(1, RelayLimits.OrderLogEntries + 5).Select(entry => (JsonNode)new JsonObject { ["orderId"] = "o-" + entry }).ToArray());
        var board = BoardReadModels.Standing("k", new JsonObject { ["history"] = history }.ToJsonString(), new JsonObject { ["log"] = log }.ToJsonString());
        Assert.Equal(Enumerable.Range(6, RelayLimits.StandingHistoryEntries).Reverse().Select(revision => (long)revision), board.History.Select(change => change.Revision));
        Assert.Equal(Enumerable.Range(6, RelayLimits.OrderLogEntries).Reverse().Select(entry => "o-" + entry), board.OrderLog.Select(entry => entry.OrderId));
    }

    [Fact]
    public void Stored_text_is_shown_without_control_or_format_characters_and_cut_to_the_dashboard_length()
    {
        const string Hidden = "fine‮enil​\u0007\r\n";
        var longText = new string('x', DashboardText.MaxLength - 1) + "\U0001F600";
        var standing = new JsonObject
        {
            ["lines"] = new JsonObject { ["a"] = new JsonObject { ["id"] = "a", ["text"] = longText, ["author"] = Hidden } },
            ["proposals"] = new JsonObject { ["p"] = new JsonObject { ["id"] = "p", ["grant"] = new JsonObject { ["scope"] = Hidden, ["targets"] = new JsonArray(Hidden, longText), ["expiresAt"] = "soon" } } }
        };
        var orders = new JsonObject { ["items"] = new JsonObject { ["o"] = new JsonObject { ["id"] = "o", ["actions"] = new JsonArray(new JsonObject { ["do"] = Hidden, ["sessionId"] = Hidden }) } } };
        var board = BoardReadModels.Standing(Uri.EscapeDataString("repo‮") + "/default", standing.ToJsonString(), orders.ToJsonString());

        Assert.Equal("repo/default", board.Board);
        var line = Assert.Single(board.Lines);
        // A character that would cross the limit is dropped whole, never split.
        Assert.Equal((new string('x', DashboardText.MaxLength - 1), "fineenil"), (line.Text, line.Author));
        // Each part is cleaned before the summary joins them, so the summary is not cut at the limit.
        Assert.Equal($"fineenil: fineenil, {new string('x', DashboardText.MaxLength - 1)} until soon", Assert.Single(board.Proposals).Summary);
        Assert.Equal(["fineenil to fineenil"], Assert.Single(board.Orders).Actions);
    }

    [Fact]
    public void Values_of_the_wrong_shape_read_as_empty_and_text_that_is_not_json_throws()
    {
        var standing = """
            {"revision":"3","settings":{"requireResources":"true"},
             "lines":{"a":"text","b":[],"c":{"id":"c","approvedAt":"not a date","text":7}},
             "grants":["not","a","map"],
             "proposals":{"p":{"id":"p","line":"not an object"}},
             "history":[{"revision":1.5,"changes":["+line:c",7,null]},"not an object"]}
            """;
        var orders = """{"items":{"o":{"id":"o","trigger":"not an object","actions":["x",{"taskId":"T"}],"runs":"2","disabled":"true"},"p":5},"log":{"not":"an array"}}""";
        var board = BoardReadModels.Standing("k", standing, orders);

        Assert.Equal((0L, false), (board.Revision, board.RequireResources));
        var line = Assert.Single(board.Lines);
        Assert.Equal(("c", "", null), (line.Id, line.Text, line.ApprovedAt));
        Assert.Empty(board.Grants);
        // A proposal without a line object reads as a grant proposal.
        Assert.Equal(("grant", ":  until ", false), (Assert.Single(board.Proposals).Kind, board.Proposals[0].Summary, board.Proposals[0].Replaces));
        // History entries are listed as stored, an entry that is not an object as an empty one.
        Assert.Equal([(0L, 0), (0L, 1)], board.History.Select(change => (change.Revision, change.Changes.Length)));
        var order = Assert.Single(board.Orders);
        Assert.Equal(("", "", 0L, false), (order.Trigger, order.Target, order.Runs, order.Disabled));
        Assert.Equal(["T"], order.Actions);
        Assert.Empty(board.OrderLog);

        Assert.ThrowsAny<JsonException>(() => BoardReadModels.Standing("k", "{", null));
        Assert.ThrowsAny<JsonException>(() => BoardReadModels.Standing("k", null, new string('[', 65) + new string(']', 65)));
    }
}

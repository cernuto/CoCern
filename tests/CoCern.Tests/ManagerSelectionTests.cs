using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

public sealed class ManagerSelectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
    private static readonly RelayCaller Admin = new("selection-admin", true, ["repo"]);
    private static readonly RelayCaller Worker = new("worker", false, ["repo"]);

    [Fact]
    public void Only_administrators_can_appoint_or_clear_even_with_human_words()
    {
        var hub = Seed();
        foreach (var command in new[] { "appoint_manager", "clear_manager" })
        {
            var args = Decision(command, "a", 0, "Paul says select this chat", "denied-" + command);
            args["role"] = "manager"; args["label"] = "CoCern Manager";
            if (command == "clear_manager") args.Remove("targetSessionId");
            var before = Snapshot(hub);
            Assert.Equal("AdministratorRequired", Apply(hub, args, Worker).Code);
            Assert.Equal(before, Snapshot(hub));
        }
    }

    [Fact]
    public void Selection_retry_is_idempotent_and_changed_arguments_are_refused()
    {
        var hub = Seed(); var args = Decision("appoint_manager", "a", 0);
        var first = Apply(hub, args); Assert.True(first.Ok);
        var before = Snapshot(hub);
        Assert.Equal(first, Apply(hub, args));
        Assert.Equal(before, Snapshot(hub));
        foreach (var field in new[] { "humanWords", "targetSessionId" })
        {
            var changed = args.DeepClone().AsObject(); changed[field] = "changed";
            Assert.Equal("RequestConflict", Apply(hub, changed).Code);
            Assert.Equal(before, Snapshot(hub));
        }
        Assert.Single(Manager(hub)["history"]!.AsArray());
    }

    [Fact]
    public void Selection_replay_requires_current_administrator_authority_before_returning_history()
    {
        var hub = Seed(); var args = Decision("appoint_manager", "a", 0, "private replay quotation");
        var accepted = Apply(hub, args); Assert.True(accepted.Ok);
        var before = Snapshot(hub); var effects = hub.Queued.ToArray();
        var downgraded = new RelayCaller(Admin.PrincipalId, false, ["repo"]);
        var refused = Apply(hub, args, downgraded);
        Assert.False(refused.Ok); Assert.Equal("AdministratorRequired", refused.Code);
        foreach (var marker in new[] { "private replay quotation", "principal-a", "selection-admin", "history", "sessionId" }) Assert.DoesNotContain(marker, refused.DataJson);
        Assert.Equal(before, Snapshot(hub)); Assert.Equal(effects, hub.Queued);
        Assert.Equal(accepted, Apply(hub, args, Admin)); Assert.Equal(before, Snapshot(hub));
    }
    [Fact]
    public void Human_words_are_required_bounded_and_preserved()
    {
        foreach (var words in new JsonNode?[] { null, JsonValue.Create("  "), JsonValue.Create(12), JsonValue.Create("line\nbreak"), JsonValue.Create("tab\t"), JsonValue.Create("x\u007fy"), JsonValue.Create(new string('x', 201)) })
        {
            var hub = Seed(); var args = Decision("appoint_manager", "a", 0); args["humanWords"] = words;
            AssertRejectedWithoutMutation(hub, args, "InvalidArguments");
        }
        var accepted = Seed(); var exact = " " + new string('x', 198) + " ";
        Assert.True(Apply(accepted, Decision("appoint_manager", "a", 0, exact)).Ok);
        Assert.Equal(exact, Manager(accepted)["history"]![0]!["humanWords"]!.GetValue<string>());
        Assert.Equal(exact, accepted.State(AggregateKinds.Control, AggregateKinds.ControlKey)[ControlState.ManagerSeatsKey]!["repo"]![ManagerSeat.ManagersKey]![0]!["humanWords"]!.GetValue<string>());
    }

    [Fact]
    public void Target_must_be_an_exact_open_directory_session()
    {
        foreach (var id in new[] { "", " ", "a\n", new string('a', 257), "unknown" })
            AssertRejectedWithoutMutation(Seed(), Decision("appoint_manager", id, 0), id == "unknown" ? "SessionUnknown" : "InvalidArguments");
        AssertRejectedWithoutMutation(Seed(), Decision("appoint_manager", null, 0), "InvalidArguments");
        var wrong = Seed(); Add(wrong, "b", "other"); AssertRejectedWithoutMutation(wrong, Decision("appoint_manager", "b", 0), "RepositoryDenied");
        var closing = Seed(); Mutate(closing, json => new ControlState(json).Fence("a", Now));
        AssertRejectedWithoutMutation(closing, Decision("appoint_manager", "a", 0), "SessionUnknown");
        var stale = Seed(); Add(stale, "a", at: Now.AddHours(-RelayLimits.StaleSessionHours - 1));
        AssertRejectedWithoutMutation(stale, Decision("appoint_manager", "a", 0), "SessionUnknown");
        var sleeping = Seed(); Add(sleeping, "a", at: Now.AddHours(-1));
        Assert.True(Apply(sleeping, Decision("appoint_manager", "a", 0)).Ok);
    }

    [Fact]
    public void Selection_status_never_serializes_secrets_or_other_repository_identities()
    {
        var hub = Seed(); Assert.True(Apply(hub, Decision("appoint_manager", "a", 0, "private quotation")).Ok);
        var admin = Status(hub, Admin);
        Assert.DoesNotContain("TOKEN-MARKER", admin.DataJson); Assert.DoesNotContain("sessionToken", admin.DataJson); Assert.DoesNotContain("principal-a", admin.DataJson);
        var outsider = Status(hub, new("outsider", false, ["other"]), "other");
        Assert.True(outsider.Ok); var manager = AggregateHub.Data(outsider)["manager"]!;
        Assert.Equal(0, manager["generation"]!.GetValue<long>()); Assert.Null(manager["current"]); Assert.Empty(manager["history"]!.AsArray());
        Assert.Null(manager["nextHistoryBefore"]); Assert.False(manager["historyTruncated"]!.GetValue<bool>());
        foreach (var marker in new[] { "principal-a", "selection-admin", "private quotation", "\"repo\"", "\"a\"" }) Assert.DoesNotContain(marker, outsider.DataJson);
        var own = AggregateHub.Data(Status(hub, Worker))["manager"]!; Assert.NotNull(own["current"]); Assert.Empty(own["history"]!.AsArray());

        // Each repository holds its own managers: an appointment in another repository neither replaces nor revokes this one's, and adding
        // a second manager here keeps the first. (Paul's appointment of a cocern chat revoked the m3ts-snx Manager on 2026-11-09.)
        var hubCaller = new RelayCaller("hub", true, ["repo", "other", "m3ts-snx", "cocern"], true);
        JsonNode Standing(AggregateHub on, string repository, string session) =>
            AggregateHub.Data(on.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.ManagerStanding, new ManagerStandingQuery(repository, session).ToJson(), hubCaller, Now));
        Add(hub, "b", "other"); Add(hub, "c");
        var elsewhere = Decision("appoint_manager", "b", 0, "Paul selected b", "decide-other"); elsewhere["repositoryId"] = "other";
        Assert.True(Apply(hub, elsewhere, new RelayCaller("selection-admin", true, ["other"])).Ok);
        Assert.True(Apply(hub, Decision("appoint_manager", "c", 1, "Paul selected c too", "decide-c")).Ok);
        Assert.Equal(new[] { "c", "a" }, Manager(hub)["managers"]!.AsArray().Select(item => item!["sessionId"]!.GetValue<string>()).ToArray());
        Assert.Equal(2, Manager(hub)["generation"]!.GetValue<long>());
        foreach (var (repository, session, generation) in new[] { ("repo", "a", 2L), ("repo", "c", 2L), ("other", "b", 1L) })
        {
            var seat = Standing(hub, repository, session);
            Assert.True(seat["manager"]!.GetValue<bool>()); Assert.False(seat["revoked"]!.GetValue<bool>()); Assert.Equal(generation, seat["generation"]!.GetValue<long>());
        }
        Assert.False(Standing(hub, "other", "a")["manager"]!.GetValue<bool>());

        // A document from the hub-wide seat, as the live hub held it: generation 2's cocern appointment displaced and revoked m3ts-snx's manager.
        var legacy = new AggregateHub(); Add(legacy, "51498ecb", "m3ts-snx"); Add(legacy, "cf5989d6", "cocern");
        Mutate(legacy, json =>
        {
            json[ControlState.ManagerGenerationKey] = 2;
            json[ControlState.ManagerKey] = new JsonObject { ["sessionId"] = "cf5989d6", ["principalId"] = "principal-cf5989d6", ["repositoryId"] = "cocern", ["generation"] = 2, ["assignedBy"] = "paul", ["assignedAt"] = Now.ToString("O"), ["requestId"] = "r2", ["humanWords"] = "Paul named it" };
            json[ControlState.ManagerHistoryKey] = new JsonArray(
                JsonSerializer.SerializeToNode(new ManagerSelectionHistory(1, "appoint_manager", null, "51498ecb", "m3ts-snx", "paul", "r1", Now, "Paul named it")),
                JsonSerializer.SerializeToNode(new ManagerSelectionHistory(2, "appoint_manager", "51498ecb", "cf5989d6", "cocern", "paul", "r2", Now, "Paul named it")));
            json[ControlState.ManagerRevokedKey] = new JsonObject { ["51498ecb"] = 2 };
        });
        var snx = Standing(legacy, "m3ts-snx", "51498ecb");
        Assert.True(snx["manager"]!.GetValue<bool>()); Assert.False(snx["revoked"]!.GetValue<bool>()); Assert.Equal(1, snx["generation"]!.GetValue<long>());
        Assert.True(Standing(legacy, "cocern", "cf5989d6")["manager"]!.GetValue<bool>());
        // The first change stores the migrated seats and touches only its own repository.
        var removal = new JsonObject { ["repositoryId"] = "cocern", ["requestId"] = "remove-cocern", ["command"] = "clear_manager", ["targetSessionId"] = "cf5989d6", ["expectedManagerGeneration"] = 2, ["humanWords"] = "Paul removed it" };
        Assert.True(Apply(legacy, removal, new RelayCaller("selection-admin", true, ["cocern"])).Ok);
        Assert.True(Standing(legacy, "cocern", "cf5989d6")["revoked"]!.GetValue<bool>());
        Assert.True(Standing(legacy, "m3ts-snx", "51498ecb")["manager"]!.GetValue<bool>());
    }

    /// <summary>The log lives in a hub-wide document but reads one selected repository, with that repository's own numbers, cursor and counts.</summary>
    [Fact]
    public void Reading_the_action_log_shows_only_the_selected_repository()
    {
        var hub = new AggregateHub();
        void Log(string repository, string request) => Assert.True(hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.AdminAction,
            new AdminActionReport(repository, RelayTools.ControlRequest, "close_sessions", "target-" + repository, request, "Ok", true, "session-" + repository, null, null).ToJson(),
            new RelayCaller("actor-" + repository, true, [repository], true), Now).Ok);
        RelayReply Read(string repository) => hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.GetStatus, new JsonObject { ["repositoryId"] = repository, ["managerHistoryOnly"] = true }, new RelayCaller("administrator", true, []), Now);
        Log("mine", "request-mine");
        // Enough to evict in the other repository, which must not show in this one's number, count or entries.
        for (var i = 0; i < RelayLimits.AdminActionEntries + 5; i++) Log("other", "request-other-" + i);

        var selected = Read("mine");
        Assert.True(selected.Ok);
        foreach (var marker in new[] { "target-other", "session-other", "actor-other", "request-other" }) Assert.DoesNotContain(marker, selected.DataJson);
        var own = AggregateHub.Data(selected)["actions"]!;
        Assert.Equal(1, Assert.Single(own["items"]!.AsArray())!["seq"]!.GetValue<long>());
        Assert.Equal(0, own["dropped"]!.GetValue<long>());
        Assert.Null(own["nextBefore"]);

        // The same administrator selects the other repository and sees its actions, which it could perform.
        var other = Read("other");
        Assert.True(other.Ok);
        Assert.DoesNotContain("target-mine", other.DataJson);
        var others = AggregateHub.Data(other)["actions"]!;
        Assert.Equal(RelayLimits.AdminActionPageEntries, others["items"]!.AsArray().Count);
        Assert.Equal(5, others["dropped"]!.GetValue<long>());
    }

    private static AggregateHub Seed() { var hub = new AggregateHub(); Add(hub, "a"); return hub; }
    private static void Add(AggregateHub hub, string id, string repository = "repo", DateTimeOffset? at = null, string? principal = null) => Mutate(hub, json => new ControlState(json).Enter(SessionRecord.Opened(id, "TOKEN-MARKER", principal ?? "principal-" + id, repository, new() { ["label"] = "CoCern Manager", ["role"] = "manager" }).WithInitialFreshness(at ?? Now)));
    private static void Mutate(AggregateHub hub, Action<JsonObject> mutate) { var json = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey); mutate(json); hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Value = new() { Json = json.ToJsonString() }; }
    private static string Snapshot(AggregateHub hub) => hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Value.Json;
    private static JsonObject Decision(string command, string? target, long generation, string words = "Paul selected this chat", string request = "decide") { var args = new JsonObject { ["repositoryId"] = "repo", ["requestId"] = request, ["command"] = command, ["expectedManagerGeneration"] = generation, ["humanWords"] = words }; if (target is not null) args["targetSessionId"] = target; return args; }
    private static RelayReply Apply(AggregateHub hub, JsonObject args, RelayCaller? caller = null) => hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, args, caller ?? Admin, Now);
    private static RelayReply Status(AggregateHub hub, RelayCaller caller, string repository = "repo", long? before = null) { var args = new JsonObject { ["repositoryId"] = repository }; if (before is not null) args["managerHistoryBefore"] = before; return hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.GetStatus, args, caller, Now); }
    private static JsonNode Manager(AggregateHub hub) { var reply = Status(hub, Admin); Assert.True(reply.Ok); return Assert.IsType<JsonObject>(AggregateHub.Data(reply)["manager"]); }
    private static void AssertRejectedWithoutMutation(AggregateHub hub, JsonObject args, string code) { var before = Snapshot(hub); Assert.Equal(code, Apply(hub, args).Code); Assert.Equal(before, Snapshot(hub)); }
}

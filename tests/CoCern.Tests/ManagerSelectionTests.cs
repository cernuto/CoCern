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
        Assert.Equal(exact, accepted.State(AggregateKinds.Control, AggregateKinds.ControlKey)["manager"]!["humanWords"]!.GetValue<string>());
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
    }

    /// <summary>The log lives in a hub-wide document, and MayAccess admits any administrator anywhere, so this is the line that keeps one repository's actions from another's reader.</summary>
    [Fact]
    public void An_administrator_limited_to_one_repository_sees_nothing_of_another_in_the_action_log()
    {
        var hub = new AggregateHub();
        void Log(string repository, string request) => Assert.True(hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.AdminAction,
            new AdminActionReport(repository, RelayTools.ControlRequest, "close_sessions", "target-" + repository, request, "Ok", true, "session-" + repository, null, null).ToJson(),
            new RelayCaller("actor-" + repository, true, [repository], true), Now).Ok);
        JsonObject Read(RelayCaller caller, string repository)
        {
            var reply = hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.GetStatus, new JsonObject { ["repositoryId"] = repository, ["managerHistoryOnly"] = true }, caller, Now);
            Assert.True(reply.Ok);
            foreach (var marker in new[] { "target-other", "session-other", "actor-other", "request-other" })
                if (repository != "other") Assert.DoesNotContain(marker, reply.DataJson);
            return AggregateHub.Data(reply);
        }
        Log("mine", "request-mine");
        // Enough to evict in the other repository, which must not show in this one's number, count or entries.
        for (var i = 0; i < RelayLimits.AdminActionEntries + 5; i++) Log("other", "request-other-" + i);

        var limited = new RelayCaller("limited", true, ["mine"]);
        var own = Read(limited, "mine")["actions"]!;
        var entry = Assert.Single(own["items"]!.AsArray());
        Assert.Equal(1, entry!["seq"]!.GetValue<long>());
        Assert.Equal(0, own["dropped"]!.GetValue<long>());
        Assert.Null(own["nextBefore"]);

        // Naming the other repository is admitted, as for any administrator, and reads exactly like one with no history.
        var refused = hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.GetStatus, new JsonObject { ["repositoryId"] = "other", ["managerHistoryOnly"] = true }, limited, Now);
        foreach (var marker in new[] { "target-other", "session-other", "actor-other", "request-other" }) Assert.DoesNotContain(marker, refused.DataJson);
        var nothing = AggregateHub.Data(refused)["actions"]!;
        Assert.Empty(nothing["items"]!.AsArray());
        Assert.Equal(0, nothing["dropped"]!.GetValue<long>());
        Assert.Null(nothing["nextBefore"]);

        var hubWide = Read(new RelayCaller("hub", true, []), "other")["actions"]!;
        Assert.Equal(RelayLimits.AdminActionPageEntries, hubWide["items"]!.AsArray().Count);
        Assert.Equal(5, hubWide["dropped"]!.GetValue<long>());
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

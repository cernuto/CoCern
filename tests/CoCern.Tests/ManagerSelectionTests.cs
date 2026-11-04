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
    public void Assignment_is_global_and_handoff_checks_generation()
    {
        var hub = Seed(); Add(hub, "b", "other");
        Assert.True(Apply(hub, Decision("appoint_manager", "a", 0)).Ok);
        var second = Decision("appoint_manager", "b", 1, request: "handoff"); second["repositoryId"] = "other";
        Assert.True(Apply(hub, second).Ok);
        var manager = Manager(hub);
        Assert.Equal(2, manager["generation"]!.GetValue<long>());
        Assert.Equal("b", manager["current"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("other", manager["current"]!["repositoryId"]!.GetValue<string>());
        var before = Snapshot(hub);
        var conflict = Apply(hub, Decision("appoint_manager", "a", 1, request: "stale"));
        Assert.Equal("VersionConflict", conflict.Code);
        Assert.Equal("{\"currentGeneration\":2}", conflict.DataJson);
        Assert.Equal(before, Snapshot(hub));
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
    public void Generation_is_a_required_nonnegative_whole_number()
    {
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("0"), JsonValue.Create(0.5), JsonValue.Create(-1), JsonValue.Create(true), JsonValue.Create(long.MaxValue) })
        {
            var hub = Seed(); var args = Decision("appoint_manager", "a", 0); args["expectedManagerGeneration"] = value;
            AssertRejectedWithoutMutation(hub, args, "InvalidArguments");
        }
        var missing = Decision("appoint_manager", "a", 0); missing.Remove("expectedManagerGeneration");
        AssertRejectedWithoutMutation(Seed(), missing, "InvalidArguments");
        var overflow = Seed(); Mutate(overflow, json => json["managerGeneration"] = long.MaxValue);
        AssertRejectedWithoutMutation(overflow, Decision("clear_manager", null, long.MaxValue), "InvalidArguments");
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
    public void Identity_fields_and_clear_target_are_validated_without_mutation()
    {
        foreach (var field in new[] { "repositoryId", "requestId" })
        foreach (var value in new[] { "", " ", "bad\r", new string('x', 257) })
        {
            var args = Decision("appoint_manager", "a", 0); args[field] = value;
            AssertRejectedWithoutMutation(Seed(), args, field == "repositoryId" && value == "" ? "RepositoryDenied" : "InvalidArguments");
        }
        foreach (var principal in new[] { "", " ", "bad\r", new string('x', 257) })
        {
            var hub = Seed(); var before = Snapshot(hub);
            Assert.Equal("InvalidArguments", Apply(hub, Decision("appoint_manager", "a", 0), new(principal, true, ["repo"])).Code);
            Assert.Equal(before, Snapshot(hub));
        }
        var clear = Decision("clear_manager", "a", 0); AssertRejectedWithoutMutation(Seed(), clear, "InvalidArguments");
        clear["targetSessionId"] = null; AssertRejectedWithoutMutation(Seed(), clear, "InvalidArguments");
    }

    [Fact]
    public void Closed_manager_requires_explicit_recovery()
    {
        var hub = new AggregateHub(); var a = hub.Open("CoCern Manager", Now); hub.DeliverAll(Now);
        Assert.True(Apply(hub, Decision("appoint_manager", a.Id, 0)).Ok);
        var old = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey)["sessions"]![a.Id]!.DeepClone();
        Assert.True(hub.Close(new() { ["targetSessionId"] = a.Id, ["force"] = true }, Now.AddMinutes(1)).Ok); hub.DeliverAll(Now.AddMinutes(1));
        Assert.False(Manager(hub)["current"]!["available"]!.GetValue<bool>());
        var b = hub.Open("CoCern Manager", Now.AddMinutes(2), role: "manager"); hub.DeliverAll(Now.AddMinutes(2));
        Assert.Equal(a.Id, Manager(hub)["current"]!["sessionId"]!.GetValue<string>());
        Assert.True(Apply(hub, Decision("appoint_manager", b.Id, 1, request: "recover")).Ok);
        Mutate(hub, json => json["sessions"]![a.Id] = old);
        Assert.Equal(b.Id, Manager(hub)["current"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("AdministratorRequired", Apply(hub, Decision("clear_manager", null, 2), Worker).Code);
    }

    [Fact]
    public void Selection_survives_reload_and_history_eviction()
    {
        var hub = Seed();
        var first = Decision("appoint_manager", "a", 0, "  exact human words  "); first["actorPrincipalId"] = "forged"; first["assignedAt"] = "forged";
        Assert.True(Apply(hub, first).Ok); hub = hub.Clone();
        var current = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey)["manager"]!;
        Assert.Equal("selection-admin", current["assignedBy"]!.GetValue<string>());
        Assert.Equal(Now, current["assignedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("decide", current["requestId"]!.GetValue<string>());
        Assert.Equal("principal-a", current["principalId"]!.GetValue<string>());
        Assert.Equal("repo", current["repositoryId"]!.GetValue<string>());
        for (var generation = 1; generation <= 21; generation++) Assert.True(Apply(hub, Decision("clear_manager", null, generation, request: "clear-" + generation)).Ok);
        var json = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey);
        Assert.Equal(20, json["managerHistory"]!.AsArray().Count);
        Assert.Equal(22, json["managerGeneration"]!.GetValue<long>());
        Assert.Null(json["manager"]);
        Assert.True(Apply(hub, Decision("appoint_manager", "a", 22, "retained", "current")).Ok);
        var clone = hub.Clone().State(AggregateKinds.Control, AggregateKinds.ControlKey);
        Assert.Equal("retained", clone["manager"]!["humanWords"]!.GetValue<string>());
        Assert.Equal("current", clone["manager"]!["requestId"]!.GetValue<string>());
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

    [Fact]
    public void History_cursor_is_strict_and_administrator_only()
    {
        foreach (var value in new JsonNode?[] { JsonValue.Create(-1), JsonValue.Create("1"), JsonValue.Create(1.5), JsonValue.Create(true), null })
        {
            var hub = Seed(); var args = new JsonObject { ["repositoryId"] = "repo", ["managerHistoryBefore"] = value };
            Assert.Equal("InvalidArguments", hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.GetStatus, args, Admin, Now).Code);
        }
        var worker = Seed(); Assert.Equal("AdministratorRequired", Status(worker, Worker, before: 0).Code);
    }

    [Fact]
    public void Selection_history_pages_fit_worst_case_wire_budget()
    {
        var hub = new AggregateHub(); var escaped = new string('"', 256); var repository = new string('"', 256);
        Add(hub, escaped, repository, principal: new string('"', 256));
        var actor = new RelayCaller(new string('"', 256), true, [repository]);
        for (var generation = 0; generation < 20; generation++)
        {
            var request = new string('"', 253) + generation.ToString("D3");
            var args = Decision("appoint_manager", escaped, generation, new string('"', 200), request); args["repositoryId"] = repository;
            Assert.True(Apply(hub, args, actor).Ok);
        }
        var seen = new List<long>(); long? cursor = null; var reduced = false;
        do
        {
            var reply = Status(hub, actor, repository, cursor); Assert.True(reply.Ok); Assert.True(Replies.FitsWire(reply, 24576));
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(reply).Length <= 24576);
            var manager = AggregateHub.Data(reply)["manager"]!; var page = manager["history"]!.AsArray();
            Assert.InRange(page.Count, 1, 2); reduced |= page.Count == 1;
            seen.AddRange(page.Select(x => x!["generation"]!.GetValue<long>()));
            cursor = manager["nextHistoryBefore"]?.GetValue<long>();
            if (cursor is not null) Assert.Equal(seen[^1], cursor.Value);
        } while (cursor is not null);
        Assert.True(reduced); Assert.Equal(Enumerable.Range(1, 20).Reverse().Select(x => (long)x), seen);
    }

    [Fact]
    public void Clear_of_empty_assignment_is_an_explicit_recorded_decision_and_old_state_defaults_are_safe()
    {
        var hub = Seed(); var initial = Manager(hub); Assert.Equal(0, initial["generation"]!.GetValue<long>()); Assert.Null(initial["current"]);
        Assert.True(Apply(hub, Decision("clear_manager", null, 0)).Ok);
        var cleared = Manager(hub); Assert.Equal(1, cleared["generation"]!.GetValue<long>()); Assert.Null(cleared["current"]);
        Assert.Equal("clear_manager", Assert.Single(cleared["history"]!.AsArray())!["command"]!.GetValue<string>());
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

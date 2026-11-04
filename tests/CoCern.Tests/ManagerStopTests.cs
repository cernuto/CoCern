using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public class ManagerStopTests
{
    [Fact]
    public async Task Stop_all_records_each_session_and_preserves_action_evidence()
    {
        await using var host = DashboardManagerManagementTests.Host();
        var one = await DashboardManagerManagementTests.Open(host, "repo");
        var two = await DashboardManagerManagementTests.Open(host, "other");
        var app = host.Services.GetRequiredService<RelayApplication>();
        var worker = new RelayCaller("worker", false, ["repo", "other"]);
        Task<RelayReply> Work(string tool, object args) => app.ExecuteAsync(worker, tool, JsonSerializer.SerializeToElement(args));
        Assert.True((await Work(RelayTools.TaskUpdate, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = "create", expectedVersion = 0 })).Ok);
        Assert.True((await Work(RelayTools.TaskClaim, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = "claim", expectedVersion = 1 })).Ok);
        var begun = await Work(RelayTools.ActionBegin, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = "begin", expectedVersion = 2, actionId = "a", inputFingerprint = "fingerprint", requiresRestoration = true });
        Assert.True(begun.Ok, begun.Code);
        var before = await Work(RelayTools.TaskGet, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", section = "actions" });
        var stopped = await app.ExecuteAsync(new("viewer", true, ["repo"]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "stop-all", command = "stop", allSessions = true }));
        Assert.True(stopped.Ok, stopped.Code);
        var data = JsonNode.Parse(stopped.DataJson)!;
        Assert.True(data["paused"]!.GetValue<bool>()); Assert.True(data["cancellationRequested"]!.GetValue<bool>());
        Assert.NotNull(data["cancellationSessions"]); Assert.Equal(2, data["cancellationSessions"]!.GetValue<int>());
        foreach (var (session, repository) in new[] { (one, "repo"), (two, "other") })
        {
            var status = await Work(RelayTools.GetStatus, new { repositoryId = repository, sessionId = session.Id, sessionToken = session.Token });
            Assert.True(status.Ok, status.Code);
            var control = JsonNode.Parse(status.DataJson)!["control"]!;
            var request = Assert.Single(control["requests"]!.AsArray())!;
            Assert.Equal(session.Id + ":", request["target"]!.GetValue<string>());
            Assert.Equal("requested_unknown", request["request"]!["status"]!.GetValue<string>());
        }
        var after = await Work(RelayTools.TaskGet, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", section = "actions" });
        Assert.Equal(before.DataJson, after.DataJson);
        Assert.Contains("invoking", after.DataJson); Assert.Contains("pending", after.DataJson);
        foreach (var tool in new[] { RelayTools.TaskClaim, RelayTools.ClaimAcquire, RelayTools.ActionBegin })
        {
            var refused = await Work(tool, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = Guid.NewGuid().ToString("N"), resourceId = "resource", actionId = "next" });
            Assert.Equal(RelayErrorCodes.Paused, refused.Code);
        }
        Assert.True((await Work(RelayTools.SendMessage, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, recipientSessionId = one.Id, requestId = "message", text = "Recovery stays available." })).Ok);
        Assert.True((await Work(RelayTools.CheckInbox, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token })).Ok);
        Assert.True((await Work(RelayTools.ActionReport, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = "report", expectedVersion = 3, actionId = "a", attempt = 1, outcome = "unknown", restoration = "pending", report = "Tool outcome is still unresolved." })).Ok);
        var refusedRelease = await Work(RelayTools.ClaimRelease, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = "release-unresolved", expectedVersion = 4, claimGeneration = 1 });
        Assert.Equal("UnresolvedAction", refusedRelease.Code); // Pause permits cleanup, but never erases outstanding work.
        var settled = await Work(RelayTools.ActionReport, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = "settle", expectedVersion = 4, actionId = "a", attempt = 1, outcome = "completed", restoration = "verified", report = "Fixture completion and restoration were verified." });
        Assert.True(settled.Ok, settled.Code);
        var released = await Work(RelayTools.ClaimRelease, new { repositoryId = "repo", sessionId = one.Id, sessionToken = one.Token, taskId = "t", requestId = "release", expectedVersion = 5, claimGeneration = 1 });
        Assert.True(released.Ok, released.Code);
        var selection = await app.ExecuteAsync(new("viewer", true, ["repo"]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "choose-paused", command = "appoint_manager", targetSessionId = one.Id, expectedManagerGeneration = 0, humanWords = "Choose this chat." }));
        Assert.True(selection.Ok, selection.Code);
        Assert.Equal(RelayErrorCodes.AdministratorRequired, (await Work(RelayTools.ControlRequest, new { repositoryId = "repo", requestId = "self-elevate", command = "resume" })).Code);
        Assert.Equal(RelayErrorCodes.AdministratorRequired, (await Work(RelayTools.StandingPut, new { repositoryId = "repo", requestId = "self-standing", expectedRevision = 0 })).Code);
        var resumed = await app.ExecuteAsync(new("viewer", true, ["repo"]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "resume", command = "resume" }));
        Assert.True(resumed.Ok);
    }

    [Fact]
    public async Task Running_hub_allows_claim_and_action_when_ordinary_status_defers_manager()
    {
        await using var host = DashboardManagerManagementTests.Host();
        var repositoryId = new string('"', 256);
        var chat = await DashboardManagerManagementTests.Open(host, repositoryId);
        var app = host.Services.GetRequiredService<RelayApplication>();
        var worker = new RelayCaller("worker", false, [repositoryId]);
        var admin = new RelayCaller(new string('b', 175), true, [repositoryId]);
        Task<RelayReply> Work(string tool, object args) => app.ExecuteAsync(worker, tool, JsonSerializer.SerializeToElement(args));
        Task<RelayReply> Control(object args) => app.ExecuteAsync(admin, RelayTools.ControlRequest, JsonSerializer.SerializeToElement(args));
        var selected = await Control(new { repositoryId, command = "appoint_manager", expectedManagerGeneration = 0, targetSessionId = chat.Id, humanWords = "Choose this chat.", requestId = "appoint" });
        Assert.True(selected.Ok, selected.Code);
        var sessionId = chat.Id; var sessionToken = chat.Token;
        for (var i = 0; i < 50; i++)
        {
            var cancelled = await Control(new { repositoryId, sessionId, command = "cancel", actionId = new string('a', 230) + i, requestId = "cancel" + i });
            Assert.True(cancelled.Ok, cancelled.Code);
            var status = await Work(RelayTools.GetStatus, new { repositoryId, sessionId, sessionToken });
            Assert.True(status.Ok, status.Code);
            var control = JsonNode.Parse(status.DataJson)!["control"]!.AsObject();
            if (control.ContainsKey("manager")) continue;
            Assert.Equal(i + 1, control["requests"]!.AsArray().Count);
            var created = await Work(RelayTools.TaskUpdate, new { repositoryId, sessionId, sessionToken, taskId = "t", requestId = "create", expectedVersion = 0 });
            Assert.True(created.Ok, created.Code);
            var claimed = await Work(RelayTools.TaskClaim, new { repositoryId, sessionId, sessionToken, taskId = "t", requestId = "claim", expectedVersion = 1 });
            Assert.True(claimed.Ok, claimed.Code);
            var begun = await Work(RelayTools.ActionBegin, new { repositoryId, sessionId, sessionToken, taskId = "t", requestId = "begin", expectedVersion = 2, actionId = "test", inputFingerprint = "fixture", requiresRestoration = true });
            Assert.True(begun.Ok, begun.Code);
            return;
        }
        Assert.Fail("Fixture never reached the manager omission boundary.");
    }

    [Fact]
    public async Task Revoke_with_all_sessions_leaves_machine_enrolled()
    {
        await using var host = DashboardManagerManagementTests.Host();
        var machines = host.Services.GetRequiredService<IMachineDirectory>();
        await machines.EnrollAsync("pc", "Test PC", "secret", CancellationToken.None);
        var app = host.Services.GetRequiredService<RelayApplication>();
        var admin = new RelayCaller("admin", true, ["repo"]);
        var args = new JsonObject { ["repositoryId"] = "repo", ["requestId"] = "bad", ["command"] = "revoke_machine", ["machineId"] = "pc", ["allSessions"] = true };
        var rejected = await app.ExecuteAsync(admin, RelayTools.ControlRequest, JsonSerializer.SerializeToElement(args));
        Assert.Equal(RelayErrorCodes.InvalidArguments, rejected.Code);
        Assert.NotNull(await machines.ValidateAsync("secret", CancellationToken.None));
        args.Remove("allSessions"); args["requestId"] = "good";
        Assert.True((await app.ExecuteAsync(admin, RelayTools.ControlRequest, JsonSerializer.SerializeToElement(args))).Ok);
        Assert.Null(await machines.ValidateAsync("secret", CancellationToken.None));
    }

    [Fact]
    public async Task Authenticated_status_includes_global_stop_after_fifty_action_requests()
    {
        await using var host = DashboardManagerManagementTests.Host();
        var chat = await DashboardManagerManagementTests.Open(host, "repo");
        var app = host.Services.GetRequiredService<RelayApplication>();
        Task<RelayReply> Control(object args) => app.ExecuteAsync(new("admin", true, ["repo"]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(args));
        for (var i = 0; i < 50; i++)
        {
            var targeted = await Control(new { repositoryId = "repo", sessionId = chat.Id, command = "cancel", actionId = "a" + i, requestId = "c" + i });
            Assert.True(targeted.Ok, targeted.Code);
        }
        for (var i = 0; i < 2; i++)
        {
            Assert.True((await Control(new { repositoryId = "repo", command = "stop", allSessions = true, requestId = "stop" + i })).Ok);
            var status = await app.ExecuteAsync(new("worker", false, ["repo"]), RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = chat.Id, sessionToken = chat.Token }));
            Assert.True(status.Ok, status.Code);
            var control = JsonNode.Parse(status.DataJson)!["control"]!;
            Assert.Equal(51, control["cancellations"]!.GetValue<int>());
            var requests = control["requests"]!.AsArray();
            Assert.Equal(50, requests.Count);
            Assert.Contains(requests, request => (string?)request!["target"] == chat.Id + ":");
        }
    }

    [Fact]
    public void Manager_selection_still_confers_no_powers_or_alert_role_changes()
    {
        var now = DateTimeOffset.UtcNow; var hub = new AggregateHub(); var chat = hub.Open("CoCern Manager", now, role: "worker");
        var beforeRole = AggregateHub.Data(hub.Verify(chat, now))["role"]?.ToJsonString();
        var sessionBefore = hub.Of(AggregateKinds.Session, chat.Id).Value.Json;
        var selection = hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest,
            new() { ["requestId"] = "select", ["command"] = "appoint_manager", ["targetSessionId"] = chat.Id, ["expectedManagerGeneration"] = 0, ["humanWords"] = "Choose this chat." }, AggregateHub.Admin, now);
        Assert.True(selection.Ok);
        Assert.Equal(sessionBefore, hub.Of(AggregateKinds.Session, chat.Id).Value.Json);
        Assert.Equal(beforeRole, AggregateHub.Data(hub.Verify(chat, now))["role"]?.ToJsonString());
        Assert.Equal(RelayErrorCodes.AdministratorRequired, hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, new() { ["requestId"] = "elevate", ["command"] = "resume" }, AggregateHub.Alice, now).Code);
        Assert.Equal(RelayErrorCodes.AdministratorRequired, hub.Board(RelayTools.StandingPut, new() { ["requestId"] = "standing", ["expectedRevision"] = 0 }, now).Code);
    }

    [Fact]
    public void Empty_stop_all_pauses_and_nonadministrator_cannot_use_it()
    {
        var hub = new AggregateHub(); var now = DateTimeOffset.UtcNow;
        var args = new JsonObject { ["requestId"] = "global", ["command"] = "stop", ["allSessions"] = true };
        Assert.Equal(RelayErrorCodes.AdministratorRequired, hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, args, AggregateHub.Alice, now).Code);
        var result = hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, args, AggregateHub.Admin, now);
        Assert.True(result.Ok); Assert.Equal(0, AggregateHub.Data(result)["cancellationSessions"]!.GetValue<int>());
        Assert.True(AggregateHub.Data(result)["paused"]!.GetValue<bool>());
    }
    [Theory]
    [InlineData("pause", null)]
    [InlineData("resume", null)]
    [InlineData("appoint_manager", null)]
    [InlineData("stop", "sessionId")]
    [InlineData("stop", "actionId")]
    [InlineData("stop", "targetSessionId")]
    public void All_sessions_rejects_other_commands_or_simultaneous_targets_without_mutation(string command, string? field)
    {
        var hub = new AggregateHub();
        var before = hub.State(AggregateKinds.Control, AggregateKinds.ControlKey).ToJsonString();
        var args = new JsonObject { ["requestId"] = "invalid", ["command"] = command, ["allSessions"] = true };
        if (field is not null) args[field] = "target";
        var result = hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, args, AggregateHub.Admin, DateTimeOffset.UtcNow);
        Assert.Equal(RelayErrorCodes.InvalidArguments, result.Code);
        Assert.Equal(before, hub.State(AggregateKinds.Control, AggregateKinds.ControlKey).ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void All_sessions_is_a_boolean(string json)
    {
        var result = new AggregateHub().Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest,
            new() { ["requestId"] = "bad", ["command"] = "stop", ["allSessions"] = JsonNode.Parse(json) }, AggregateHub.Admin, DateTimeOffset.UtcNow);
        Assert.Equal(RelayErrorCodes.InvalidArguments, result.Code);
    }

    [Fact]
    public void Targeted_stop_still_targets_only_its_given_session()
    {
        var hub = new AggregateHub(); var now = DateTimeOffset.UtcNow; var chat = hub.Open("worker", now);
        var result = hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, new() { ["requestId"] = "stop-one", ["command"] = "stop", ["sessionId"] = chat.Id, ["actionId"] = "a" }, AggregateHub.Admin, now);
        Assert.True(result.Ok);
        var state = new ControlState(hub.State(AggregateKinds.Control, AggregateKinds.ControlKey));
        Assert.Equal(chat.Id + ":a", Assert.Single(state.CancellationsOf(chat.Id)).Target);
    }
}

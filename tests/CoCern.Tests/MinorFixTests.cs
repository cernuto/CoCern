using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Orleans;
using Xunit;

namespace CoCern.Tests;

/// <summary>Regression tests for the review's Minor findings and the pinned loopback connection.</summary>
public sealed class MinorFixTests : IDisposable
{
    private static readonly RelayCaller Hub = new("alice", true, ["repo", "other"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
    private const string Fingerprint = "AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-minor-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { System.IO.Directory.Delete(root, true); } catch (IOException) { } }

    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string kind, string operation, JsonObject args, string repository = "repo", int seconds = 0)
    {
        args["repositoryId"] = repository;
        return CoordinationStateMachine.Apply(state, kind, "key", Hub, operation, args, Now.AddSeconds(seconds));
    }

    private static JsonObject State(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!.AsObject();
    private static JsonObject Payload(DurableEffect effect) => JsonNode.Parse(effect.PayloadJson)!.AsObject();

    private static StateEnvelope<CoordinationDocument> Directory(params (string Id, DateTimeOffset Seen)[] managers) => new()
    {
        Value = new()
        {
            Json = new JsonObject
            {
                ["sessions"] = new JsonObject(managers.Select(manager => KeyValuePair.Create(manager.Id, (JsonNode?)new JsonObject
                {
                    ["sessionId"] = manager.Id, ["repositoryId"] = "repo", ["role"] = "manager", ["bindingVersion"] = 2, ["binding"] = new JsonObject { ["v"] = 2 },
                    ["observedAt"] = manager.Seen.ToString("O"), ["freshUntil"] = manager.Seen.ToString("O")
                })))
            }.ToJsonString()
        }
    };

    [Fact]
    public void A_close_reaches_idle_managers_too_while_a_raise_goes_only_to_active_ones()
    {
        var control = Directory(("active", Now), ("idle", Now.AddDays(-3)));
        JsonObject Route(string operation) => new() { ["role"] = "manager", ["operation"] = operation, ["item"] = new JsonObject { ["kind"] = AttentionKinds.Blocked, ["taskId"] = "t" } };
        Assert.True(Apply(control, "control", Operations.AttentionRoute, Route(Operations.AttentionRaise)).Ok);
        Assert.Equal(["active"], control.Effects.Select(effect => effect.TargetKey));
        control.Effects.Clear();
        Assert.True(Apply(control, "control", Operations.AttentionRoute, Route(Operations.AttentionClose)).Ok);
        Assert.Equal(["active", "idle"], control.Effects.Select(effect => effect.TargetKey).Order());
    }

    [Fact]
    public void A_reordered_older_registration_never_replaces_a_newer_binding_or_moves_times_back()
    {
        var control = Directory(("s1", Now));
        var reply = Apply(control, "control", Operations.Register, new()
        {
            ["sessionId"] = "s1", ["bindingVersion"] = 1, ["binding"] = new JsonObject { ["v"] = 1 }, ["observedAt"] = Now.AddHours(-1).ToString("O"), ["freshUntil"] = Now.AddHours(-1).ToString("O")
        });
        Assert.True(reply.Ok);
        Assert.True(Apply(control, "control", Operations.SessionActive, new() { ["sessionId"] = "s1", ["activeAt"] = Now.AddMinutes(5).ToString("O") }).Ok);
        // An older activity report, delivered later, never moves it back.
        Assert.True(Apply(control, "control", Operations.SessionActive, new() { ["sessionId"] = "s1", ["activeAt"] = Now.AddMinutes(1).ToString("O") }).Ok);
        var session = State(control)["sessions"]!["s1"]!;
        Assert.Equal(2, session["binding"]!["v"]!.GetValue<int>());
        Assert.Equal(Now, DateTimeOffset.Parse(session["freshUntil"]!.GetValue<string>()));
        Assert.Equal(Now.AddMinutes(5), DateTimeOffset.Parse(session["activeAt"]!.GetValue<string>()));
    }

    [Fact]
    public void Retrying_a_failed_action_restates_or_closes_the_managers_failed_item()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var workflow = new JsonObject
        {
            ["state"] = "ready", ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "x:y", ["restoration"] = "r", ["stopCondition"] = "s" },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }), ["blockers"] = new JsonArray()
        };
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["requestId"] = "c", ["expectedVersion"] = 0, ["workflow"] = workflow }).Ok);
        Assert.True(Apply(board, "board", RelayTools.TaskClaim, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "k", ["expectedVersion"] = 1 }).Ok);
        Assert.True(Apply(board, "board", RelayTools.ActionBegin, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "b1", ["expectedVersion"] = 2, ["actionId"] = "a", ["inputFingerprint"] = "f" }).Ok);
        Assert.True(Apply(board, "board", RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "r1", ["expectedVersion"] = 3, ["actionId"] = "a", ["attempt"] = 1, ["outcome"] = "failed" }).Ok);
        board.Effects.Clear();
        Assert.True(Apply(board, "board", RelayTools.ActionBegin, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "b2", ["expectedVersion"] = 4, ["actionId"] = "a", ["inputFingerprint"] = "f", ["attempt"] = 2, ["explicitRetry"] = true }).Ok);
        var close = Assert.Single(board.Effects, effect => effect.Operation == Operations.AttentionRoute && JsonFields.Text(Payload(effect), "operation") == Operations.AttentionClose);
        Assert.Contains(AttentionKinds.Failed, Payload(close)["item"]!.ToJsonString());
    }

    [Fact]
    public void A_null_in_an_array_of_ids_is_invalid_input_not_a_crash()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var reply = Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["requestId"] = "c", ["expectedVersion"] = 0, ["actionReports"] = new JsonArray((JsonNode?)null) });
        Assert.Equal(RelayErrorCodes.InvalidArguments, reply.Code);
        Assert.Throws<ArgumentException>(() => JsonFields.Strings(new JsonArray((JsonNode?)null, "x")).ToArray());
    }

    [Fact]
    public void Deliveries_that_can_never_succeed_are_dropped_instead_of_retried_forever()
    {
        var inbox = new JsonObject(Enumerable.Range(0, RelayLimits.MaxRecords).Select(i => KeyValuePair.Create("m" + i, (JsonNode?)new JsonObject { ["messageId"] = "m" + i, ["text"] = "x" })));
        var full = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["principalId"] = "alice", ["sessionId"] = "s", ["repositoryId"] = "repo", ["inbox"] = inbox }.ToJsonString() } };
        var dropped = Apply(full, "session", Operations.Deliver, new() { ["messageId"] = "new", ["text"] = "hi" });
        Assert.True(dropped.Ok);
        Assert.False(JsonNode.Parse(dropped.DataJson)!["delivered"]!.GetValue<bool>());

        var elsewhere = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["principalId"] = "alice", ["sessionId"] = "s", ["repositoryId"] = "repo" }.ToJsonString() } };
        var other = Apply(elsewhere, "session", Operations.Deliver, new() { ["messageId"] = "x", ["text"] = "hi" }, repository: "other");
        Assert.True(other.Ok);
        Assert.False(JsonNode.Parse(other.DataJson)!["delivered"]!.GetValue<bool>());
        // Anything else from another repository is still refused.
        Assert.Equal(RelayErrorCodes.RepositoryDenied, Apply(elsewhere, "session", RelayTools.CheckInbox, new() { ["sessionId"] = "s" }, repository: "other").Code);
    }

    [Fact]
    public async Task Every_claim_path_resolves_an_alias_by_the_sessions_machine()
    {
        var app = new RelayApplication(DispatchProxy.Create<IGrainFactory, CoordinationTests.TestGrainFactory>());
        var admin = new RelayCaller("alice", true, ["repo"]);
        async Task<RelayReply> Call(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] ??= Guid.NewGuid().ToString("N");
            return await app.ExecuteAsync(admin, tool, JsonSerializer.SerializeToElement(args));
        }
        Assert.True((await Call("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "db-prod-1", ["machineId"] = "M1", ["aliases"] = new JsonArray("db") })).Ok);
        var opened = JsonNode.Parse((await Call("session_open", new() { ["machineId"] = "M1" })).DataJson)!;
        JsonObject Session() => new() { ["sessionId"] = opened["sessionId"]!.DeepClone(), ["sessionToken"] = opened["sessionToken"]!.DeepClone() };
        // None of these calls names a machine: the alias resolves by the session's.
        var acquire = Session(); acquire["resourceId"] = "db";
        var acquired = await Call(RelayTools.ClaimAcquire, acquire);
        Assert.True(acquired.Ok, acquired.Code + " " + acquired.DataJson);
        var release = Session(); release["resourceId"] = "db"; release["generation"] = JsonNode.Parse(acquired.DataJson)!["generation"]!.DeepClone();
        var released = await Call(RelayTools.ClaimRelease, release);
        Assert.True(released.Ok, released.Code + " " + released.DataJson);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("\"30\"", 30)]
    [InlineData("\"soon\"", 0)]
    [InlineData("null", 0)]
    public void A_quoted_wait_works_and_nonsense_waits_for_nothing(string json, int seconds) =>
        Assert.Equal(seconds, InboxWait.WaitSeconds(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void The_adapter_pins_the_certificate_COCERN_FINGERPRINT_names()
    {
        string? Env(string name) => name switch { "COCERN_URL" => "https://127.0.0.1:5272", "COCERN_TOKEN" => "secret", "COCERN_FINGERPRINT" => Fingerprint.ToLowerInvariant(), _ => null };
        Assert.Equal(Fingerprint, AdapterOptions.FromEnvironment(Env).PinnedFingerprint);
        string? Plain(string name) => name == "COCERN_URL" ? "http://127.0.0.1:5271" : Env(name);
        Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(Plain));
        string? Bad(string name) => name == "COCERN_FINGERPRINT" ? "nope" : Env(name);
        Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(Bad));
    }

    [Fact]
    public void Connect_uses_the_hubs_pinned_loopback_https_when_the_hub_recorded_it()
    {
        var data = Path.Combine(root, "repo", "data");
        System.IO.Directory.CreateDirectory(data);
        var token = Path.Combine(data, "local-client-token");
        File.WriteAllText(token, "secret");
        File.WriteAllText(Path.Combine(data, "local-hub.json"), JsonSerializer.Serialize(new { url = "https://127.0.0.1:5272/", fingerprint = Fingerprint }));
        var profile = Path.Combine(root, "profile");
        using var output = new StringWriter();
        Assert.Equal(0, ConnectCommand.Run(["--token-file", token, "--only", "claude-code"], profile, null, "dotnet", Path.Combine(root, "CoCern.Adapter.dll"), output, TextWriter.Null));
        var env = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, ".claude.json")))!["mcpServers"]!["cocern"]!["env"]!;
        Assert.Equal("https://127.0.0.1:5272/", env["COCERN_URL"]!.GetValue<string>());
        Assert.Equal(Fingerprint, env["COCERN_FINGERPRINT"]!.GetValue<string>());
    }
}

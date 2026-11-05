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
    public void The_adapter_pins_the_certificate_COCERN_FINGERPRINT_names()
    {
        string? Env(string name) => name switch { "COCERN_URL" => "https://127.0.0.1:5272", "COCERN_TOKEN" => "secret", "COCERN_FINGERPRINT" => Fingerprint.ToLowerInvariant(), _ => null };
        Assert.Equal(Fingerprint, AdapterOptions.FromEnvironment(Env).PinnedFingerprint);
        string? Plain(string name) => name == "COCERN_URL" ? "http://127.0.0.1:5271" : Env(name);
        Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(Plain));
        string? Bad(string name) => name == "COCERN_FINGERPRINT" ? "nope" : Env(name);
        Assert.Throws<AdapterConfigurationException>(() => AdapterOptions.FromEnvironment(Bad));
    }

}

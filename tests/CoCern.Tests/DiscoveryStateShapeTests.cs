using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Discovery stores only the keys <see cref="DiscoveryState"/> declares. The document is built through
/// <see cref="DiscoveryStateMachine.Apply"/>, as the hub builds it: a full machine table that expires the machine unseen longest, a
/// machine that announced chats of each provider, and a revoked one.
/// </summary>
public sealed class DiscoveryStateShapeTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
    private static readonly RelayCaller Hub = new("hub", true, [], true);

    private static RelayReply Call(StateEnvelope<CoordinationDocument> envelope, string operation, JsonObject args, DateTimeOffset at) =>
        DiscoveryStateMachine.Apply(envelope, Hub, operation, args, at);

    private static RelayReply Enroll(StateEnvelope<CoordinationDocument> envelope, string id, DateTimeOffset at) =>
        Call(envelope, DiscoveryOperations.Enroll, new() { ["machineId"] = id, ["machineName"] = "PC " + id, ["credentialHash"] = "hash-" + id }, at);

    private static JsonObject Chat(string provider, string thread, bool wakeable = false, bool waiter = false) => new()
    {
        ["provider"] = provider, ["threadId"] = thread, ["relaySessionId"] = "rs-" + thread, ["repositoryId"] = "repo", ["label"] = "chat " + thread,
        ["wakeable"] = wakeable, ["waiterRunning"] = waiter, ["openedAt"] = T0.ToString("O")
    };

    [Fact]
    public void Discovery_stores_only_the_keys_its_state_declares()
    {
        var envelope = new StateEnvelope<CoordinationDocument>();
        for (var index = 0; index < DiscoveryLimits.MaxMachines; index++) Assert.True(Enroll(envelope, "m" + index, T0.AddSeconds(index)).Ok);
        Assert.True(Call(envelope, DiscoveryOperations.Announce, new()
        {
            ["machineId"] = "m1", ["machineName"] = "PC A", ["clients"] = new JsonArray("codex", "claude"),
            ["chats"] = new JsonArray(Chat(ChatProviders.Codex, "t1", wakeable: true), Chat(ChatProviders.Claude, "t2", waiter: true), Chat(ChatProviders.Unknown, "unmapped"))
        }, T0.AddMinutes(10)).Ok);
        Assert.True(Call(envelope, DiscoveryOperations.Revoke, new() { ["machineId"] = "m2" }, T0.AddMinutes(11)).Ok);
        // The revoked machine's slot is taken again; then, with the table full, each new machine past the expiry takes the slot of the
        // machine unseen longest, whose ID is kept.
        var later = T0.AddDays(RelayLimits.MachineExpiryDays + 1);
        Assert.Equal(RelayErrorCodes.MachineRevoked, Enroll(envelope, "m2", later).Code);
        foreach (var id in new[] { "late", "later", "latest" }) Assert.True(Enroll(envelope, id, later).Ok);

        var json = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        Assert.Empty(DiscoveryState.Shape.Undeclared(json));
        // The shape is not vacuous: the document holds every key it declares, and so do its machines and their chats.
        Assert.Empty(DiscoveryState.Shape.Fields.Keys.Except(json.Select(entry => entry.Key)));
        var machine = json[DiscoveryState.MachinesKey]!["m1"]!.AsObject();
        Assert.Empty(EnrolledMachine.Shape.Fields.Keys.Except(machine.Select(entry => entry.Key)));
        Assert.Empty(AnnouncedChat.Shape.Fields.Keys.Except(machine[EnrolledMachine.ChatsKey]![0]!.AsObject().Select(entry => entry.Key)));
        // An announcement's own fields beyond those (its openedAt) are never stored.
        Assert.DoesNotContain("openedAt", machine.ToJsonString());

        // Its typed members read what the handlers wrote.
        var state = new DiscoveryState(json);
        var announced = state.Machine("m1")!;
        Assert.Equal(("PC A", T0.AddMinutes(10), "hash-m1"), (announced.Name, announced.LastContact, announced.CredentialHash));
        Assert.Equal(["codex", "claude"], announced.Clients);
        Assert.Equal([(ChatProviders.Codex, true, false), (ChatProviders.Claude, false, true), (ChatProviders.Unknown, false, false)],
            announced.Chats.Select(chat => (chat.Provider, chat.Wakeable, chat.WaiterRunning)));
        Assert.Equal("rs-t1", announced.Chats.First().RelaySessionId);
        // A machine that never announced counts from its enrollment.
        Assert.Null(state.Machine("m4")!.LastSeen);
        Assert.Equal(T0.AddSeconds(4), state.Machine("m4")!.LastContact);
        Assert.True(state.IsDenied("m2"));
        Assert.False(state.HasMachine("m2"));
        // m0 then m3 were unseen longest (m1 announced, m2 was revoked); both IDs are kept so neither is reissued.
        Assert.True(state.IsExpired("m0") && state.IsExpired("m3"));
        Assert.Equal("m0", state.OldestExpired());
        Assert.Equal(DiscoveryLimits.MaxMachines, state.MachineCount);
    }
}

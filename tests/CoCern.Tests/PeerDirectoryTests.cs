using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The security boundary of named peers: a label nominates and never grants, the repository filter hides other repositories' chats, and no public
/// reply carries a participant ID.
/// </summary>
public sealed class PeerDirectoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
    private static readonly RelayCaller Internal = AggregateHub.Alice with { IsInternal = true };
    private static readonly IReadOnlySet<string> NoRoutes = new HashSet<string>();

    private static string Nonce() => Guid.NewGuid().ToString("N");

    private static JsonObject Attempt(string? label, string repository = AggregateHub.Repository)
    {
        var attempt = new JsonObject
        {
            ["repositoryId"] = repository, ["machineId"] = "machine", ["requestId"] = Nonce(),
            ["nativeJoin"] = new JsonObject
            {
                ["provider"] = ChatProviders.Claude, ["nativeId"] = Guid.NewGuid().ToString("D"), ["workspace"] = "/workspace/repo",
                ["bindingSecret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ["generation"] = Nonce()
            }
        };
        if (label is not null) attempt["label"] = label;
        return attempt;
    }

    /// <summary>Through the real admission boundary, then the directory with a controlled clock.</summary>
    private static Chat Admitted(AggregateHub hub, JsonObject args, RelayCaller? caller = null, DateTimeOffset? at = null)
    {
        var admitted = new CallGuard(null!, "hub").TryAdmit(caller ?? AggregateHub.Alice, RelayTools.SessionOpen, JsonSerializer.SerializeToElement(args), null, out var call, out var refusal);
        Assert.True(admitted, refusal?.Code);
        var opened = AggregateHub.Data(hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.SessionOpen, call!.Args, call.Caller, at ?? Now));
        return new(opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>());
    }

    private static string Participant(AggregateHub hub, Chat chat) => hub.Directory()[chat.Id]![NativeParticipant.Id]!.GetValue<string>();

    private static PeerResolution Resolve(AggregateHub hub, Chat from, string name = "", string reference = "")
    {
        var request = new PeerResolve(AggregateHub.Repository, Participant(hub, from), from.Id, name, reference);
        var reply = hub.Apply(PeerResolve.TargetKind, AggregateKinds.ControlKey, PeerResolve.Operation, request.ToJson(), Internal, Now);
        return PeerResolution.Parse(AggregateHub.Data(reply), AggregateHub.Repository);
    }

    [Fact]
    public void Label_collision_grants_nothing()
    {
        var hub = new AggregateHub();
        var asker = Admitted(hub, Attempt("Asker"));
        var real = Admitted(hub, Attempt("Real name"));

        // The label is outside the identity: the same proof names the same participant with or without one.
        var bare = Attempt(null);
        var labelled = bare.DeepClone().AsObject();
        labelled["label"] = "Anything at all";
        Assert.True(NativeParticipant.Admit(AggregateHub.Alice, bare));
        Assert.True(NativeParticipant.Admit(AggregateHub.Alice, labelled));
        Assert.Equal(bare[NativeParticipant.Id]!.GetValue<string>(), labelled[NativeParticipant.Id]!.GetValue<string>());

        // A raw session named and roled as the manager is nobody's verified peer.
        hub.Open("CoCern Manager", Now, role: SessionRoles.Manager);
        var alone = Resolve(hub, asker, "cocern manager");
        Assert.Equal((PeerOutcome.None, PeerRefusals.Unverified), (alone.Outcome, alone.Reason));
        Assert.Null(alone.Participant);

        // A raw session that takes a verified chat's name, and even sends its participant ID, costs availability and gains no identity.
        var forged = Admitted(hub, new JsonObject
        {
            ["repositoryId"] = AggregateHub.Repository, ["requestId"] = Nonce(), ["label"] = "Real name", [NativeParticipant.Id] = Participant(hub, real),
            [NativeParticipant.Generation] = Nonce(), [NativeParticipant.Request] = Nonce(), [NativeParticipant.Client] = ChatProviders.Claude
        });
        Assert.False(hub.Directory()[forged.Id]!.AsObject().ContainsKey(NativeParticipant.Id));
        var collided = Resolve(hub, asker, "Real name");
        Assert.Equal(PeerOutcome.Ambiguous, collided.Outcome);
        Assert.Null(collided.Participant);
        var legacy = Assert.Single(collided.Choices!, choice => !choice.Verified);
        Assert.Equal(forged.Id, legacy.SessionId);
        Assert.Equal("", legacy.Client);

        // Its ref is refused too: only the verified row resolves, and to its own participant.
        Assert.Equal(PeerRefusals.Unverified, Resolve(hub, asker, reference: forged.Id).Reason);
        Assert.Equal(Participant(hub, real), Resolve(hub, asker, reference: real.Id).Participant?.ParticipantId);
    }

    [Fact]
    public void Another_repositorys_chat_is_invisible_with_a_reply_identical_to_an_unknown_name()
    {
        var hub = new AggregateHub();
        var hidden = Admitted(hub, Attempt("Secret", "other"), new RelayCaller("alice", false, [AggregateHub.Repository, "other"]));
        var asker = Admitted(hub, Attempt("Asker"));

        var unknown = PeerTools.PublicResolution(Resolve(hub, asker, "Nobody"), NoRoutes);
        Assert.Equal(RelayErrorCodes.NotFound, unknown.Code);
        foreach (var other in new[] { Resolve(hub, asker, "Secret"), Resolve(hub, asker, reference: hidden.Id) })
        {
            var reply = PeerTools.PublicResolution(other, NoRoutes);
            Assert.Equal((unknown.Ok, unknown.Code, unknown.DataJson), (reply.Ok, reply.Code, reply.DataJson));
        }

        var request = new PeerList(AggregateHub.Repository, Participant(hub, asker), asker.Id);
        var listed = PeerListing.Parse(AggregateHub.Data(hub.Apply(PeerList.TargetKind, AggregateKinds.ControlKey, PeerList.Operation, request.ToJson(), Internal, Now)));
        var row = Assert.Single(listed.Rows);
        Assert.Equal(("Asker", ChatProviders.Claude, true), (row.Name, row.Client, row.Self));
    }

    [Fact]
    public void Ambiguous_name_refuses_and_lists_choices()
    {
        var hub = new AggregateHub();
        var asker = Admitted(hub, Attempt("Asker"));
        var older = Admitted(hub, Attempt("Builder"));
        var newer = Admitted(hub, Attempt("BUILDER "), at: Now.AddHours(1));

        // Whichever is newer, and whatever the case or spacing: a refusal with both, and no participant.
        var ambiguous = Resolve(hub, asker, " builder");
        Assert.Equal(PeerOutcome.Ambiguous, ambiguous.Outcome);
        Assert.Null(ambiguous.Participant);
        Assert.Equal(new[] { older.Id, newer.Id }.Order(StringComparer.Ordinal), ambiguous.Choices!.Select(choice => choice.SessionId).Order(StringComparer.Ordinal));

        var reply = PeerTools.PublicResolution(ambiguous, NoRoutes);
        Assert.Equal(RelayErrorCodes.PeerAmbiguous, reply.Code);
        var choices = JsonNode.Parse(reply.DataJson)!["choices"]!.AsArray();
        Assert.Equal(2, choices.Count);
        Assert.All(choices, choice => Assert.Equal(new[] { "client", "machine", "name", "ref", "self", "state", "verified" }, choice!.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal)));

        // A prefix is not a name, and a choice's ref answers for that one chat.
        Assert.Equal(PeerRefusals.Unknown, Resolve(hub, asker, "Build").Reason);
        var chosen = Resolve(hub, asker, reference: choices[0]!["ref"]!.GetValue<string>());
        Assert.Equal(PeerOutcome.Resolved, chosen.Outcome);
        Assert.Equal(Participant(hub, chosen.Peer!.SessionId == older.Id ? older : newer), chosen.Participant?.ParticipantId);
    }

    [Fact]
    public async Task No_public_reply_contains_a_participant_id()
    {
        using var host = await StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var caller = new RelayCaller("alice", false, [AggregateHub.Repository]);
            Task<RelayReply> Call(string tool, object args) => application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
            var participants = new List<string>();
            async Task<Chat> Join(string label)
            {
                var attempt = Attempt(label);
                var proof = attempt.DeepClone().AsObject();
                Assert.True(NativeParticipant.Admit(caller, proof));
                participants.Add(proof[NativeParticipant.Id]!.GetValue<string>());
                var opened = AggregateHub.Data(await Call(RelayTools.SessionOpen, attempt));
                return new(opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>());
            }
            var chat = await Join("Builder");
            await Join("Builder");
            await Join("Reviewer");

            void Check(RelayReply reply)
            {
                var wire = JsonSerializer.Serialize(reply);
                foreach (var key in new[] { NativeParticipant.Id, NativeParticipant.Generation, NativeParticipant.Request, NativeParticipant.Client })
                    Assert.DoesNotContain(key, wire, StringComparison.OrdinalIgnoreCase);
                foreach (var participant in participants) Assert.DoesNotContain(participant, wire, StringComparison.OrdinalIgnoreCase);
            }

            // Every tool in the catalog, so a new one is covered by being added. This worker's administrator tools refuse before they touch anything.
            foreach (var tool in RelayToolCatalog.All)
                Check(await Call(tool.Name, new { repositoryId = AggregateHub.Repository, requestId = "sentinel-" + tool.Name, sessionId = chat.Id, sessionToken = chat.Token }));

            object Peers(object? extra = null)
            {
                var args = new JsonObject { ["repositoryId"] = AggregateHub.Repository, ["sessionId"] = chat.Id, ["sessionToken"] = chat.Token };
                if (extra is not null) foreach (var (key, value) in JsonSerializer.SerializeToNode(extra)!.AsObject()) args[key] = value?.DeepClone();
                return args;
            }
            var listed = await Call(RelayTools.Peers, Peers());
            Assert.True(listed.Ok, listed.Code + " " + listed.DataJson);
            Check(listed);
            var resolved = await Call(RelayTools.Peers, Peers(new { name = "Reviewer" }));
            Assert.True(resolved.Ok, resolved.Code + " " + resolved.DataJson);
            Check(resolved);
            var ambiguous = await Call(RelayTools.Peers, Peers(new { name = "Builder" }));
            Assert.Equal(RelayErrorCodes.PeerAmbiguous, ambiguous.Code);
            Check(ambiguous);
            Check(await Call(RelayTools.Peers, Peers(new { name = "Nobody" })));
            Check(await Call(RelayTools.WorkerList, new { repositoryId = AggregateHub.Repository, includeStale = true }));
            Check(await Call(RelayTools.ChatDirectory, new { repositoryId = AggregateHub.Repository }));

            // A client-sent participant field changes nothing: this chat stays itself.
            var claimed = await Call(RelayTools.Peers, Peers(new Dictionary<string, string> { [NativeParticipant.Id] = participants[^1] }));
            Assert.True(claimed.Ok, claimed.Code);
            Check(claimed);
            var self = JsonNode.Parse(claimed.DataJson)!["items"]!.AsArray().Single(row => row!["self"]!.GetValue<bool>());
            Assert.Equal("Builder", self!["name"]!.GetValue<string>());

            // A raw session is refused and keeps its own tools.
            var raw = AggregateHub.Data(await Call(RelayTools.SessionOpen, new { repositoryId = AggregateHub.Repository, requestId = "raw-open", label = "Raw" }));
            Assert.Equal(RelayErrorCodes.SessionDenied, (await Call(RelayTools.Peers, new { repositoryId = AggregateHub.Repository, sessionId = raw["sessionId"]!.GetValue<string>(), sessionToken = raw["sessionToken"]!.GetValue<string>() })).Code);
        }
        finally { await host.StopAsync(); }
    }

    private static async Task<IHost> StartAsync()
    {
        var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "peer-directory-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        return host;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

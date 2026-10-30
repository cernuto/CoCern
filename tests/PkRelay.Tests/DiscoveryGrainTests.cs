using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using PkRelay.Core;
using PkRelay.Domain;
using Xunit;

namespace PkRelay.Tests;

public sealed class DiscoveryGrainTests
{
    private static readonly RelayCaller Administrator = new("admin", true, []);
    private static readonly RelayCaller Ordinary = new("machine:pc-a", false, ["pkrelay"]);

    [Fact]
    public async Task Enrollment_survives_grain_deactivation()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            var enrolled = await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None);
            Assert.Equal(new MachineIdentity("pc-a", "machine:pc-a"), enrolled);
            var factory = host.Services.GetRequiredService<IGrainFactory>();
            await factory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);
            // A directory without the cache must resolve the credential from the persisted state of a fresh activation.
            var uncached = new MachineDirectory(factory);
            Assert.Equal(enrolled, await uncached.ValidateAsync("secret-a", CancellationToken.None));
            Assert.Null(await uncached.ValidateAsync("wrong", CancellationToken.None));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Revocation_takes_effect_immediately_despite_cache()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None);
            Assert.NotNull(await directory.ValidateAsync("secret-a", CancellationToken.None));
            Assert.NotNull(await directory.ValidateAsync("secret-a", CancellationToken.None));
            Assert.True((await directory.RevokeAsync(Administrator, "pc-a", CancellationToken.None)).Ok);
            Assert.Null(await directory.ValidateAsync("secret-a", CancellationToken.None));
            Assert.Null(await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Revocation_by_a_non_administrator_is_refused_and_leaves_the_machine_valid()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None);
            Assert.NotNull(await directory.ValidateAsync("secret-a", CancellationToken.None));
            var refused = await directory.RevokeAsync(Ordinary, "pc-a", CancellationToken.None);
            Assert.False(refused.Ok);
            Assert.Equal("AdministratorRequired", refused.Code);
            Assert.NotNull(await directory.ValidateAsync("secret-a", CancellationToken.None));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Failed_validation_is_not_cached()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            Assert.Null(await directory.ValidateAsync("secret-a", CancellationToken.None));
            Assert.Null(await directory.ValidateAsync("", CancellationToken.None));
            await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None);
            Assert.Equal("pc-a", (await directory.ValidateAsync("secret-a", CancellationToken.None))?.MachineId);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Enrolling_an_existing_machine_with_another_credential_is_refused()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            Assert.NotNull(await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None));
            Assert.Null(await directory.EnrollAsync("pc-a", "PC A", "other", CancellationToken.None));
            Assert.NotNull(await directory.EnrollAsync("pc-a", "PC A renamed", "secret-a", CancellationToken.None));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Announce_ignores_a_client_supplied_machine_id_for_another_machine()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            var a = (await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None))!;
            var b = (await directory.EnrollAsync("pc-b", "PC B", "secret-b", CancellationToken.None))!;
            Assert.True((await directory.AnnounceAsync(b, Announcement("pc-b", "PC B", "chat-b"), CancellationToken.None)).Ok);
            // Machine A claims to be machine B: the hub must apply it to A and leave B untouched.
            var hijack = Announcement("pc-b", "PC B hijacked", "chat-a");
            Assert.True((await directory.AnnounceAsync(a, hijack, CancellationToken.None)).Ok);
            Assert.Equal("pc-b", hijack["machineId"]!.GetValue<string>());
            var items = Items(await directory.DirectoryAsync(["pkrelay"], CancellationToken.None));
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-b" && x["machineName"]!.GetValue<string>() == "PC B");
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-a" && x["machineName"]!.GetValue<string>() == "PC B hijacked");
            Assert.Equal(2, items.Count);
            // Announcing under a machine ID that is not enrolled cannot create or touch anything either.
            var ghost = Announcement("ghost", "Ghost", "chat-g");
            Assert.True((await directory.AnnounceAsync(a, ghost, CancellationToken.None)).Ok);
            items = Items(await directory.DirectoryAsync(["pkrelay"], CancellationToken.None));
            Assert.Equal(2, items.Count);
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-b" && x["machineName"]!.GetValue<string>() == "PC B");
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-g" && x["machineName"]!.GetValue<string>() == "Ghost");
        }
        finally { await host.StopAsync(); }
    }

    private static JsonObject Announcement(string machineId, string machineName, string label) => new()
    {
        ["machineId"] = machineId, ["machineName"] = machineName, ["clients"] = new JsonArray("codex"),
        ["chats"] = new JsonArray(new JsonObject
        {
            ["provider"] = "codex", ["threadId"] = "thread-" + label, ["relaySessionId"] = "session-" + label, ["repositoryId"] = "pkrelay", ["label"] = label, ["wakeable"] = true, ["waiterRunning"] = false
        })
    };

    private static List<JsonObject> Items(RelayReply reply)
    {
        Assert.True(reply.Ok, reply.Code);
        return JsonNode.Parse(reply.DataJson)!["items"]!.AsArray().Select(x => x!.AsObject()).ToList();
    }

    private static async Task<IHost> StartAsync()
    {
        var siloPort = FreePort(); var gatewayPort = FreePort();
        var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, serviceId: "discovery-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay")
            // Memory storage round-trips state through Newtonsoft with a type allow-list; production uses SqliteGrainStorage, which does not.
            .Configure<TypeManifestOptions>(options => options.AddAllowedAssembly(typeof(StateEnvelope<>).Assembly)))
            .ConfigureServices(services => services.AddPkRelayCore()).Build();
        await host.StartAsync();
        return host;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}

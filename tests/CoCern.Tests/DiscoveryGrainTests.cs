using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public sealed class DiscoveryGrainTests
{
    private static readonly RelayCaller Administrator = new("admin", true, []);
    private static readonly RelayCaller Ordinary = new("machine:pc-a", false, ["cocern"]);

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
    public async Task Chat_directory_returns_only_callers_repositories()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            var application = host.Services.GetRequiredService<RelayApplication>();
            var a = (await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None))!;
            var machine = new RelayCaller(a.PrincipalId, false, ["cocern", "secret-repo"]);
            var announcement = Announcement("pc-a", "PC A", "chat-open"); var secret = Announcement("pc-a", "PC A", "chat-secret");
            announcement["chats"]![0]!["relaySessionId"] = await OpenAsync(application, machine, "cocern", "chat-open");
            secret["chats"]![0]!["repositoryId"] = "secret-repo";
            secret["chats"]![0]!["relaySessionId"] = await OpenAsync(application, machine, "secret-repo", "chat-secret");
            var chats = announcement["chats"]!.AsArray(); chats.Add(secret["chats"]![0]!.DeepClone());
            Assert.True((await directory.AnnounceAsync(a, announcement, CancellationToken.None)).Ok);
            // A caller who may use both repositories still sees only the one it asked for.
            var both = new RelayCaller("alice", false, ["cocern", "secret-repo"]);
            var listed = await application.ExecuteAsync(both, RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new { repositoryId = "cocern" }));
            Assert.Equal(["chat-open"], Items(listed).Select(x => x["label"]!.GetValue<string>()));
            Assert.DoesNotContain("thread-", listed.DataJson);
            // A caller without access to a repository is refused rather than shown an empty or filtered list.
            var limited = new RelayCaller("bob", false, ["cocern"]);
            var denied = await application.ExecuteAsync(limited, RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new { repositoryId = "secret-repo" }));
            Assert.Equal(RelayErrorCodes.RepositoryDenied, denied.Code);
            Assert.Equal(["chat-open"], Items(await application.ExecuteAsync(limited, RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new { repositoryId = "cocern" }))).Select(x => x["label"]!.GetValue<string>()));
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
            var application = host.Services.GetRequiredService<RelayApplication>();
            var a = (await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None))!;
            var b = (await directory.EnrollAsync("pc-b", "PC B", "secret-b", CancellationToken.None))!;
            // Real open sessions, so the directory (which leaves out closed ones) lists every announced chat.
            Task<string> Open(MachineIdentity machine, string label) => OpenAsync(application, new RelayCaller(machine.PrincipalId, false, ["cocern"]), "cocern", label);
            JsonObject Announced(MachineIdentity machine, string machineId, string machineName, string label, string sessionId)
            {
                var announcement = Announcement(machineId, machineName, label);
                announcement["chats"]![0]!["relaySessionId"] = sessionId;
                return announcement;
            }
            Assert.True((await directory.AnnounceAsync(b, Announced(b, "pc-b", "PC B", "chat-b", await Open(b, "chat-b")), CancellationToken.None)).Ok);
            // Machine A claims to be machine B: the hub must apply it to A and leave B untouched.
            var hijack = Announced(a, "pc-b", "PC B hijacked", "chat-a", await Open(a, "chat-a"));
            Assert.True((await directory.AnnounceAsync(a, hijack, CancellationToken.None)).Ok);
            Assert.Equal("pc-b", hijack["machineId"]!.GetValue<string>());
            var items = Items(await directory.DirectoryAsync(["cocern"], CancellationToken.None));
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-b" && x["machineName"]!.GetValue<string>() == "PC B");
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-a" && x["machineName"]!.GetValue<string>() == "PC B hijacked");
            Assert.Equal(2, items.Count);
            // Announcing under a machine ID that is not enrolled cannot create or touch anything either.
            var ghost = Announced(a, "ghost", "Ghost", "chat-g", await Open(a, "chat-g"));
            Assert.True((await directory.AnnounceAsync(a, ghost, CancellationToken.None)).Ok);
            items = Items(await directory.DirectoryAsync(["cocern"], CancellationToken.None));
            Assert.Equal(3, items.Count);
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-b" && x["machineName"]!.GetValue<string>() == "PC B");
            Assert.Contains(items, x => x["label"]!.GetValue<string>() == "chat-g" && x["machineName"]!.GetValue<string>() == "Ghost");
            var noLongerAnnounced = Assert.Single(items, x => x["label"]!.GetValue<string>() == "chat-a");
            Assert.False(noLongerAnnounced["reachable"]!.GetValue<bool>());
            Assert.Null(noLongerAnnounced["provider"]);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_revocation_completing_during_a_validation_leaves_no_stale_cache_entry()
    {
        var grain = new GatedDiscoveryGrain();
        var factory = DispatchProxy.Create<IGrainFactory, FakeGrainFactory>();
        ((FakeGrainFactory)(object)factory).Grain = grain;
        var directory = new MachineDirectory(factory);
        // The revoke is in flight, the validation starts and the grain answers it as valid, then the revoke completes before the validation resumes.
        var revoke = directory.RevokeAsync(Administrator, "pc-a", CancellationToken.None);
        await grain.RevokeReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var validate = directory.ValidateAsync("secret-a", CancellationToken.None);
        await grain.ValidateAnswered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        grain.ReleaseRevoke.SetResult();
        Assert.True((await revoke.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        grain.ReleaseValidate.SetResult();
        Assert.Equal("pc-a", (await validate.WaitAsync(TimeSpan.FromSeconds(10)))?.MachineId);
        // The racing call returned what the grain said, but nothing stale may remain cached.
        Assert.Null(await directory.ValidateAsync("secret-a", CancellationToken.None));
    }

    /// <summary>Answers validation from the state before the revoke, then holds the answer back until released.</summary>
    private sealed class GatedDiscoveryGrain : IDiscoveryGrain
    {
        private bool revoked;
        public TaskCompletionSource RevokeReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRevoke { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ValidateAnswered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseValidate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
        {
            if (operation == DiscoveryOperations.Revoke)
            {
                RevokeReached.SetResult(); await ReleaseRevoke.Task; revoked = true;
                return Replies.Ok(new { revoked = true });
            }
            Assert.Equal(DiscoveryOperations.Validate, operation);
            var answer = revoked ? Replies.Error("NotFound") : Replies.Ok(new { machineId = "pc-a", principalId = "machine:pc-a" });
            if (revoked) return answer;
            ValidateAnswered.SetResult(); await ReleaseValidate.Task;
            return answer;
        }
    }

    private class FakeGrainFactory : DispatchProxy
    {
        public IDiscoveryGrain? Grain { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "GetGrain" && targetMethod.ReturnType == typeof(IDiscoveryGrain) ? Grain : throw new NotSupportedException(targetMethod.Name);
    }

    private static JsonObject Announcement(string machineId, string machineName, string label) => new()
    {
        ["machineId"] = machineId, ["machineName"] = machineName, ["clients"] = new JsonArray("codex"),
        ["chats"] = new JsonArray(new JsonObject
        {
            ["provider"] = "codex", ["threadId"] = "thread-" + label, ["relaySessionId"] = "session-" + label, ["repositoryId"] = "cocern", ["label"] = label, ["wakeable"] = true, ["waiterRunning"] = false
        })
    };

    /// <summary>Opens a real session, labelled <paramref name="label"/>, and returns its ID.</summary>
    private static async Task<string> OpenAsync(RelayApplication application, RelayCaller caller, string repository, string label)
    {
        var reply = await application.ExecuteAsync(caller, RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = repository, requestId = "open-" + label, label }));
        Assert.True(reply.Ok, reply.Code);
        return JsonNode.Parse(reply.DataJson)!["sessionId"]!.GetValue<string>();
    }

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
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        return host;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}

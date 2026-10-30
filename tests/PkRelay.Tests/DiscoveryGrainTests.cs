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
using PkRelay.Contracts;
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
    public async Task Chat_directory_returns_only_callers_repositories()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            var a = (await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None))!;
            var announcement = Announcement("pc-a", "PC A", "chat-open"); var secret = Announcement("pc-a", "PC A", "chat-secret");
            secret["chats"]![0]!["repositoryId"] = "secret-repo";
            var chats = announcement["chats"]!.AsArray(); chats.Add(secret["chats"]![0]!.DeepClone());
            Assert.True((await directory.AnnounceAsync(a, announcement, CancellationToken.None)).Ok);
            var application = host.Services.GetRequiredService<RelayApplication>();
            // A caller who may use both repositories still sees only the one it asked for.
            var both = new RelayCaller("alice", false, ["pkrelay", "secret-repo"]);
            var listed = await application.ExecuteAsync(both, RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new { repositoryId = "pkrelay" }));
            Assert.Equal(["chat-open"], Items(listed).Select(x => x["label"]!.GetValue<string>()));
            Assert.DoesNotContain("thread-", listed.DataJson);
            // A caller without access to a repository is refused rather than shown an empty or filtered list.
            var limited = new RelayCaller("bob", false, ["pkrelay"]);
            var denied = await application.ExecuteAsync(limited, RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new { repositoryId = "secret-repo" }));
            Assert.Equal(RelayErrorCodes.RepositoryDenied, denied.Code);
            Assert.Equal(["chat-open"], Items(await application.ExecuteAsync(limited, RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new { repositoryId = "pkrelay" }))).Select(x => x["label"]!.GetValue<string>()));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Revoke_machine_command_is_administrator_only()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None);
            Assert.NotNull(await directory.ValidateAsync("secret-a", CancellationToken.None));
            var application = host.Services.GetRequiredService<RelayApplication>();
            var arguments = JsonSerializer.SerializeToElement(new { repositoryId = "pkrelay", requestId = "r-1", command = "revoke_machine", machineId = "pc-a" });
            var refused = await application.ExecuteAsync(new RelayCaller("machine:pc-a", false, ["pkrelay"]), RelayTools.ControlRequest, arguments);
            Assert.Equal(RelayErrorCodes.AdministratorRequired, refused.Code);
            Assert.NotNull(await directory.ValidateAsync("secret-a", CancellationToken.None));
            var missing = await application.ExecuteAsync(new RelayCaller("admin", true, ["pkrelay"]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "pkrelay", requestId = "r-2", command = "revoke_machine" }));
            Assert.Equal(RelayErrorCodes.InvalidArguments, missing.Code);
            var revoked = await application.ExecuteAsync(new RelayCaller("admin", true, ["pkrelay"]), RelayTools.ControlRequest, arguments);
            Assert.True(revoked.Ok, revoked.Code);
            Assert.Null(await directory.ValidateAsync("secret-a", CancellationToken.None));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_failed_validation_never_outlives_the_enrollment_of_that_credential()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            Assert.Null(await directory.ValidateAsync("secret-a", CancellationToken.None));
            Assert.Null(await directory.ValidateAsync("secret-a", CancellationToken.None));
            Assert.Null(await directory.ValidateAsync("", CancellationToken.None));
            await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None);
            Assert.Equal("pc-a", (await directory.ValidateAsync("secret-a", CancellationToken.None))?.MachineId);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Failed_validations_are_cached_by_hash_for_a_minute_and_bounded()
    {
        var grain = new CountingDiscoveryGrain();
        var factory = DispatchProxy.Create<IGrainFactory, FakeGrainFactory>();
        ((FakeGrainFactory)(object)factory).Grain = grain;
        var clock = new SteppedClock();
        var directory = new MachineDirectory(factory, clock);
        for (var i = 0; i < 3; i++) Assert.Equal(CredentialStatus.Unknown, (await directory.CheckAsync("junk", null, CancellationToken.None)).Status);
        Assert.Equal(1, grain.Validations);
        clock.Advance(TimeSpan.FromSeconds(61));
        await directory.CheckAsync("junk", null, CancellationToken.None);
        Assert.Equal(2, grain.Validations);
        // The cache holds at most 4096 hashes and drops the oldest first.
        for (var i = 0; i < MachineDirectory.NegativeCacheSize; i++) await directory.CheckAsync("filler-" + i, null, CancellationToken.None);
        var before = grain.Validations;
        await directory.CheckAsync("junk", null, CancellationToken.None);
        await directory.CheckAsync("filler-" + (MachineDirectory.NegativeCacheSize - 1), null, CancellationToken.None);
        Assert.Equal(before + 1, grain.Validations);
        // A caller over its failure budget is refused without asking the grain, and that refusal is not cached.
        Assert.Equal(CredentialStatus.Throttled, (await directory.CheckAsync("limited", () => false, CancellationToken.None)).Status);
        Assert.Equal(before + 1, grain.Validations);
        await directory.CheckAsync("limited", null, CancellationToken.None);
        Assert.Equal(before + 2, grain.Validations);
    }

    private sealed class CountingDiscoveryGrain : IDiscoveryGrain
    {
        public int Validations;
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
        {
            if (operation == DiscoveryOperations.Validate) Interlocked.Increment(ref Validations);
            return Task.FromResult(Replies.Error("NotFound"));
        }
    }

    private sealed class SteppedClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => now += by;
        public override DateTimeOffset GetUtcNow() => now;
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

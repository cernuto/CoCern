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
    public async Task Chat_directory_includes_local_sessions_and_enriches_each_only_once_when_announced()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            var application = host.Services.GetRequiredService<RelayApplication>();
            var machine = (await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None))!;
            var caller = new RelayCaller(machine.PrincipalId, false, ["cocern", "private"]);
            var id = await OpenAsync(application, caller, "cocern", "local chat");
            await OpenAsync(application, caller, "private", "private chat");
            var closing = await OpenAsync(application, caller, "cocern", "closed local chat");
            Assert.True((await application.ExecuteAsync(Administrator, RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "cocern", requestId = "close-local", command = "close_sessions", targetSessionId = closing }))).Ok);

            var local = Assert.Single(Items(await directory.DirectoryAsync(["cocern"], CancellationToken.None)));
            Assert.Equal(id, local["relaySessionId"]!.GetValue<string>());
            Assert.Equal("local chat", local["label"]!.GetValue<string>());
            Assert.False(local["reachable"]!.GetValue<bool>());
            Assert.Null(local["provider"]);
            Assert.Null(local["binding"]);
            Assert.Null(local["sessionToken"]);

            var announced = Announcement("pc-a", "PC A", "announced label");
            announced["chats"]![0]!["relaySessionId"] = id;
            Assert.True((await directory.AnnounceAsync(machine, announced, CancellationToken.None)).Ok);
            var enriched = Assert.Single(Items(await directory.DirectoryAsync(["cocern"], CancellationToken.None)));
            Assert.Equal(id, enriched["relaySessionId"]!.GetValue<string>());
            Assert.Equal("announced label", enriched["label"]!.GetValue<string>());
            Assert.Equal("codex", enriched["provider"]!.GetValue<string>());
            Assert.Equal("PC A", enriched["machineName"]!.GetValue<string>());
            Assert.True(enriched["reachable"]!.GetValue<bool>());

            Assert.True((await directory.RevokeAsync(Administrator, machine.MachineId, CancellationToken.None)).Ok);
            var revoked = Assert.Single(Items(await directory.DirectoryAsync(["cocern"], CancellationToken.None)));
            Assert.Equal(id, revoked["relaySessionId"]!.GetValue<string>());
            Assert.False(revoked["reachable"]!.GetValue<bool>());
            Assert.Null(revoked["provider"]);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chat_directory_uses_local_identity_when_announcements_are_unavailable(bool throws)
    {
        var source = new DirectorySources { DiscoveryUnavailable = true, Throw = throws };
        source.Workers.Add(new JsonObject { ["sessionId"] = "local", ["repositoryId"] = "cocern", ["machineId"] = "pc-known", ["label"] = "local chat", ["binding"] = new JsonObject { ["secret"] = "private binding" } });
        var reply = await new MachineDirectory(DirectoryFactory.For(source)).DirectoryAsync(["cocern"], CancellationToken.None);
        var item = Assert.Single(Items(reply));
        Assert.Equal("local", item["relaySessionId"]!.GetValue<string>());
        Assert.Equal("pc-known", item["machineId"]!.GetValue<string>());
        Assert.False(item["reachable"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(reply.DataJson)!["truncated"]!.GetValue<bool>());
        Assert.DoesNotContain("private binding", reply.DataJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chat_directory_retains_announced_reachability_when_workers_are_unavailable(bool throws)
    {
        var source = new DirectorySources { ControlUnavailable = true, Throw = throws };
        source.Announced.Add(new JsonObject { ["relaySessionId"] = "routed", ["repositoryId"] = "cocern", ["label"] = "routed chat", ["provider"] = "codex", ["reachable"] = false });
        var reply = await new MachineDirectory(DirectoryFactory.For(source)).DirectoryAsync(["cocern"], CancellationToken.None);
        var item = Assert.Single(Items(reply));
        Assert.Equal("routed", item["relaySessionId"]!.GetValue<string>());
        Assert.False(item["reachable"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(reply.DataJson)!["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Chat_directory_pages_local_sessions_and_bounds_the_merged_reply()
    {
        var source = new DirectorySources();
        for (var i = 0; i < 120; i++) source.Workers.Add(new JsonObject { ["sessionId"] = "local-" + i, ["repositoryId"] = "cocern", ["label"] = new string('x', 80) + i });
        // A route beyond the first worker page must survive; a closed route must disappear.
        source.Announced.Add(new JsonObject { ["relaySessionId"] = "local-119", ["repositoryId"] = "cocern", ["label"] = "last routed", ["provider"] = "codex", ["reachable"] = true });
        source.Announced.Add(new JsonObject { ["relaySessionId"] = "closed", ["repositoryId"] = "cocern", ["label"] = "closed route", ["reachable"] = true });
        var reply = await new MachineDirectory(DirectoryFactory.For(source)).DirectoryAsync(["cocern"], CancellationToken.None);
        var items = Items(reply);
        Assert.Contains(items, x => x["relaySessionId"]!.GetValue<string>() == "local-119" && x["reachable"]!.GetValue<bool>());
        Assert.DoesNotContain(items, x => x["relaySessionId"]!.GetValue<string>() == "closed");
        Assert.True(items.Count > RelayLimits.MaxPageSize);
        Assert.True(items.Count < 120);
        Assert.Equal(items.Count, items.Select(x => x["relaySessionId"]!.GetValue<string>()).Distinct().Count());
        Assert.True(Replies.FitsWire(reply, RelayLimits.AggregateReplyBytes));
        Assert.True(JsonNode.Parse(reply.DataJson)!["truncated"]!.GetValue<bool>());
    }

    private sealed class DirectorySources : IDiscoveryGrain, IControlGrain
    {
        public bool DiscoveryUnavailable, ControlUnavailable, Throw;
        public JsonArray Workers { get; } = [];
        public JsonArray Announced { get; } = [];
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
        {
            var discovery = operation == DiscoveryOperations.Directory;
            if (discovery ? DiscoveryUnavailable : ControlUnavailable)
            {
                if (Throw) throw new InvalidOperationException("source unavailable");
                return Task.FromResult(Replies.Error(RelayErrorCodes.StorageFailure));
            }
            if (discovery) return Task.FromResult(Replies.Ok(new { items = Announced, truncated = false }));
            Assert.Equal(RelayTools.WorkerList, operation);
            var args = JsonNode.Parse(argumentsJson)!.AsObject();
            Assert.True(caller.IsInternal);
            Assert.Equal(new[] { "cocern" }, caller.Repositories);
            return Task.FromResult(Replies.Page(Workers.Where(x => x!["repositoryId"]!.GetValue<string>() == args["repositoryId"]!.GetValue<string>())!, args));
        }
    }

    private class DirectoryFactory : DispatchProxy
    {
        private DirectorySources? sources;
        public static IGrainFactory For(DirectorySources sources)
        {
            var factory = Create<IGrainFactory, DirectoryFactory>();
            ((DirectoryFactory)(object)factory).sources = sources;
            return factory;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name == "GetGrain" ? sources : throw new NotSupportedException(targetMethod.Name);
    }

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
    public async Task Chat_directory_leaves_out_an_announced_chat_whose_session_closed()
    {
        using var host = await StartAsync();
        try
        {
            var directory = host.Services.GetRequiredService<IMachineDirectory>();
            var application = host.Services.GetRequiredService<RelayApplication>();
            var a = (await directory.EnrollAsync("pc-a", "PC A", "secret-a", CancellationToken.None))!;
            var machine = new RelayCaller(a.PrincipalId, false, ["cocern"]);
            var announcement = Announcement("pc-a", "PC A", "kept-chat");
            announcement["chats"]![0]!["relaySessionId"] = await OpenAsync(application, machine, "cocern", "kept-chat");
            var closing = Announcement("pc-a", "PC A", "closing-chat")["chats"]![0]!.DeepClone();
            closing["relaySessionId"] = await OpenAsync(application, machine, "cocern", "closing-chat");
            announcement["chats"]!.AsArray().Add(closing);
            Assert.True((await directory.AnnounceAsync(a, announcement, CancellationToken.None)).Ok);
            var reader = new RelayCaller("alice", false, ["cocern"]);
            async Task<string[]> Labels() => Items(await application.ExecuteAsync(reader, RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new { repositoryId = "cocern" })))
                .Select(x => x["label"]!.GetValue<string>()).ToArray();
            Assert.Equal(["kept-chat", "closing-chat"], await Labels());

            var closed = await application.ExecuteAsync(Administrator, RelayTools.ControlRequest,
                JsonSerializer.SerializeToElement(new { repositoryId = "cocern", requestId = "close-closing", command = "close_sessions", targetSessionId = closing["relaySessionId"]!.GetValue<string>() }));
            Assert.True(closed.Ok, closed.Code);
            Assert.Equal(1, JsonNode.Parse(closed.DataJson)!["closed"]!.GetValue<int>());
            // The PC is off and never announces again, so only the session directory can tell that this chat is gone.
            Assert.Equal(["kept-chat"], await Labels());
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
            var arguments = JsonSerializer.SerializeToElement(new { repositoryId = "cocern", requestId = "r-1", command = "revoke_machine", machineId = "pc-a" });
            var refused = await application.ExecuteAsync(new RelayCaller("machine:pc-a", false, ["cocern"]), RelayTools.ControlRequest, arguments);
            Assert.Equal(RelayErrorCodes.AdministratorRequired, refused.Code);
            Assert.NotNull(await directory.ValidateAsync("secret-a", CancellationToken.None));
            var missing = await application.ExecuteAsync(new RelayCaller("admin", true, ["cocern"]), RelayTools.ControlRequest, JsonSerializer.SerializeToElement(new { repositoryId = "cocern", requestId = "r-2", command = "revoke_machine" }));
            Assert.Equal(RelayErrorCodes.InvalidArguments, missing.Code);
            var revoked = await application.ExecuteAsync(new RelayCaller("admin", true, ["cocern"]), RelayTools.ControlRequest, arguments);
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

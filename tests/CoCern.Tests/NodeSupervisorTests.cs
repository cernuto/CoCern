using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using CoCern.Adapter.Development;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

public sealed class NodeSupervisorTests(ITestOutputHelper output) : IDisposable
{
    private static NodeHostCapabilities FixHost => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");

    [Fact]
    public async Task Fix3_old_starting_refusal_preserves_newer_failure_through_C_commit_and_original_expiry()
    {
        PrepareRoot();
        var current = Image("fix3-current");
        var a = Image("fix3-A") with { ReleaseId = new string('A', 64) };
        var b = Image("fix3-B") with { ReleaseId = new string('B', 64) };
        var c = Image("fix3-C") with { ReleaseId = new string('C', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        var clock = new NodeTests.UpdateClock(); var origin = clock.GetUtcNow(); var bFailedAt = origin.AddMinutes(5);
        FileStream? blocked = new(Path.Combine(Updates, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var stages = 0; var announces = 0; var launches = 0;
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new NodeRelease(1, b.ReleaseId!, b.Package, 1, new string('D', 64), b.AdapterDllSha256);
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            Clock = clock, AnnounceInterval = TimeSpan.FromMilliseconds(20), UpdateHost = FixHost,
            CreateUpdater = _ => new UpdateScheduleCoordinator(() => { Interlocked.Increment(ref stages); staged.TrySetResult(); }),
            Trace = value => { if (value == "announced") Interlocked.Increment(ref announces); },
            HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            CreateHandler = (_, _) => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { routes = Array.Empty<object>(), nodeRelease = release })) })
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task CommittedImage(string id, CancellationToken ct)
        {
            while (NodeUpdateState.ReadPrimary(Updates) is not { Phase: NodeUpdatePhase.Committed } journal || journal.Current.ContentId != id)
                await Task.Delay(10, ct);
        }
        async Task Announced(int target)
        {
            while (Volatile.Read(ref announces) < target) await Task.Delay(10, stop.Token);
        }
        var supervisor = NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, launch =>
        {
            switch (++launches)
            {
                case 1:
                    NodeUpdateState.WriteRequest(Updates, new(a)); return new UpdateExitedChild(75);
                case 2:
                    Assert.Equal(a.ReleaseId, launch.StartupContext!.FailedReleaseId);
                    blocked.Dispose(); blocked = null; clock.Advance(TimeSpan.FromMinutes(5));
                    NodeUpdateState.WriteRequest(Updates, new(b)); return new UpdateExitedChild(75);
                case 3:
                    NodeUpdateState.WriteReady(Updates, new(Guid.NewGuid(), b.ContentId, 5678, launch.StartupContext!.LaunchId));
                    return new UpdateReadyChild(Updates);
                case 4:
                    var rollback = NodeUpdateState.ReadPrimary(Updates)!;
                    Assert.Equal(b.ReleaseId, rollback.LastFailedReleaseId); Assert.Equal(bFailedAt, rollback.LastFailedAtUtc);
                    NodeUpdateState.WriteReady(Updates, new(rollback.ActivationNonce!.Value, current.ContentId, Environment.ProcessId, launch.StartupContext!.LaunchId));
                    return new UpdateWorkerChild(async ct =>
                    {
                        await CommittedImage(current.ContentId, ct); clock.Advance(TimeSpan.FromMinutes(5));
                        NodeUpdateState.WriteRequest(Updates, new(c)); return 75;
                    }, stop.Token);
                case 5:
                    Assert.Equal(a.ReleaseId, launch.StartupContext!.FailedReleaseId);
                    var starting = NodeUpdateState.ReadPrimary(Updates)!;
                    return new UpdateWorkerChild(ct => NodeRunner.RunWorkerAsync(NodeFolder, starting.ActivationNonce,
                        Path.Combine(Updates, "versions", c.ContentId), transport, new StringWriter(), ct, launch.StartupContext), stop.Token);
                default: throw new InvalidOperationException("An unexpired failed handoff must not churn the serving worker.");
            }
        }, clock, stop.Token);
        try
        {
            await CommittedImage(c.ContentId, stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await Announced(4).WaitAsync(TimeSpan.FromSeconds(5));
            var committed = NodeUpdateState.ReadPrimary(Updates)!;
            Assert.Equal(b.ReleaseId, committed.LastFailedReleaseId); Assert.Equal(bFailedAt, committed.LastFailedAtUtc);
            Assert.Equal(0, Volatile.Read(ref stages)); Assert.False(supervisor.IsCompleted);
            clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
            await Announced(Volatile.Read(ref announces) + 3).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref stages)); Assert.False(supervisor.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, Volatile.Read(ref stages)); Assert.Equal(5, launches);
            Assert.Equal(bFailedAt, NodeUpdateState.ReadPrimary(Updates)!.LastFailedAtUtc);
        }
        finally { stop.Cancel(); blocked?.Dispose(); Assert.Equal(0, await supervisor); }
    }

    [Fact]
    public async Task Fix2_serving_retained_worker_defers_transient_primary_read_loss()
    {
        var candidateStartup = false; var checkDispatchFirst = false;
        PrepareRoot(); var current = Image("fix-admission-A"); var candidate = Image("fix-admission-B") with { ReleaseId = new string('B', 64) }; var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current)); NodeUpdateState.Write(Updates, new(1, current, null, candidate, nonce, NodeUpdatePhase.Starting, null, null));
        var thread = Guid.NewGuid().ToString();
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        NodeState.WriteSessionHandle(NodeFolder, new("session", "session-token", "repo", "candidate", DateTimeOffset.UtcNow, Chat: new("codex", thread)));
        var dispatcher = new FixServingDispatcher(); var stages = 0; var announces = 0; var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => "inert-never-run", CodexDispatcher = _ => dispatcher,
            AnnounceInterval = TimeSpan.FromMilliseconds(20), WakeInterval = TimeSpan.FromMilliseconds(20), UpdateHost = FixHost,
            CreateUpdater = _ => new UpdateScheduleCoordinator(() => Interlocked.Increment(ref stages)), HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            Trace = value => { if (value == "announced" && ++announces == 4) cycled.TrySetResult(); },
            CreateHandler = (_, _) => new UpdateWorkerHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("announce")) return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { routes = new[] { new { provider = "codex", threadId = thread, relaySessionId = "session", repositoryId = "repo" } } })) };
                object data = request.RequestUri.AbsolutePath.EndsWith("get_status") ? new { sessionId = "session", control = new { paused = false, requests = Array.Empty<object>() } } : new { items = new[] { new { messageId = "candidate-mail", repositoryId = "repo" } }, total = 1, nextCursor = (int?)null, truncated = false };
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = JsonSerializer.Serialize(data), version = 1 })) };
            })
        };
        FileStream? readyLock = null;
        if (!candidateStartup) { NodeUpdateState.WriteReady(Updates, new(Guid.NewGuid(), current.ContentId, 999)); readyLock = new(Path.Combine(Updates, "ready.json"), FileMode.Open, FileAccess.Read, FileShare.Read); }
        var worker = NodeRunner.RunWorkerAsync(NodeFolder, nonce, Path.Combine(Updates, "versions", candidateStartup ? candidate.ContentId : current.ContentId), transport, new StringWriter(), stop.Token);
        try
        {
            await Task.WhenAny(cycled.Task, worker).WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(cycled.Task.IsCompleted);
            if (candidateStartup)
            {
                if (checkDispatchFirst) { Assert.Equal(0, dispatcher.Calls); Assert.Equal(0, stages); }
                else { Assert.Equal(0, stages); Assert.Equal(0, dispatcher.Calls); }
                Assert.NotNull(NodeUpdateState.ReadReady(Updates));
                NodeUpdateState.Write(Updates, Committed(candidate) with { Previous = current });
                await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            else
            {
                Assert.Equal(1, dispatcher.Calls); Assert.Equal(0, stages);
                Assert.False(dispatcher.Completed.Task.IsCompleted);
                using (var denied = new FileStream(Path.Combine(Updates, "state.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await Task.Delay(150);
                    Assert.False(worker.IsCompleted);
                    Assert.Equal(0, stages);
                }
                readyLock!.Dispose(); readyLock = null;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
                while (NodeUpdateState.ReadReady(Updates)?.ActivationNonce != nonce && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                Assert.Equal(nonce, NodeUpdateState.ReadReady(Updates)!.ActivationNonce);
                NodeUpdateState.Write(Updates, Committed(current));
                await Task.Delay(100);
                Assert.False(worker.IsCompleted);
            }
        }
        finally { stop.Cancel(); readyLock?.Dispose(); await worker; }
    }

    [Fact]
    public async Task Fix2_retained_startup_failure_seed_prevents_same_release_churn()
    {
        var candidateStartup = false; var checkDispatchFirst = false;
        PrepareRoot(); var current = Image("fix-admission-A"); var candidate = Image("fix-admission-B") with { ReleaseId = new string('B', 64) }; var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current));
        var release = new NodeRelease(1, candidate.ReleaseId!, candidate.Package, 1, new string('C', 64), candidate.AdapterDllSha256);
        var thread = Guid.NewGuid().ToString();
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        NodeState.WriteSessionHandle(NodeFolder, new("session", "session-token", "repo", "candidate", DateTimeOffset.UtcNow, Chat: new("codex", thread)));
        var dispatcher = new FixDispatcher(); var stages = 0; var announces = 0; var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => "inert-never-run", CodexDispatcher = _ => dispatcher,
            AnnounceInterval = TimeSpan.FromMilliseconds(20), WakeInterval = TimeSpan.FromMilliseconds(20), UpdateHost = FixHost,
            CreateUpdater = _ => new UpdateScheduleCoordinator(() => Interlocked.Increment(ref stages)), HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            Trace = value => { if (value == "announced" && ++announces == 4) cycled.TrySetResult(); },
            CreateHandler = (_, _) => new UpdateWorkerHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("announce")) return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { nodeRelease = release, routes = new[] { new { provider = "codex", threadId = thread, relaySessionId = "session", repositoryId = "repo" } } })) };
                object data = request.RequestUri.AbsolutePath.EndsWith("get_status") ? new { sessionId = "session", control = new { paused = false, requests = Array.Empty<object>() } } : new { items = new[] { new { messageId = "candidate-mail", repositoryId = "repo" } }, total = 1, nextCursor = (int?)null, truncated = false };
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = JsonSerializer.Serialize(data), version = 1 })) };
            })
        };
        FileStream? readyLock = null;

        var worker = NodeRunner.RunWorkerAsync(NodeFolder, null, Path.Combine(Updates, "versions", candidateStartup ? candidate.ContentId : current.ContentId), transport, new StringWriter(), stop.Token, new(null, Guid.NewGuid(), candidate.ReleaseId, DateTimeOffset.UtcNow));
        try
        {
            await Task.WhenAny(cycled.Task, worker).WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(cycled.Task.IsCompleted);
            if (candidateStartup)
            {
                if (checkDispatchFirst) { Assert.Equal(0, dispatcher.Calls); Assert.Equal(0, stages); }
                else { Assert.Equal(0, stages); Assert.Equal(0, dispatcher.Calls); }
                Assert.NotNull(NodeUpdateState.ReadReady(Updates));
                NodeUpdateState.Write(Updates, Committed(candidate) with { Previous = current });
                await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            else { Assert.Equal(0, stages); Assert.False(worker.IsCompleted); }
        }
        finally { stop.Cancel(); readyLock?.Dispose(); var exit = await worker; if (cycled.Task.IsCompleted) Assert.Equal(0, exit); }
    }

    [Fact]
    public void Fix2_private_startup_context_is_bounded_and_preserves_legacy_nonce()
    {
        var nonce = Guid.NewGuid(); var launch = Guid.NewGuid(); var release = new string('B', 64); var failed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(NodeWorkerStartupContext.TryParse($"v1;{nonce:D};{launch:D};{release};{failed}", out var context));
        Assert.Equal(nonce, context!.ActivationNonce); Assert.Equal(launch, context.LaunchId); Assert.Equal(release, context.FailedReleaseId);
        Assert.Equal(failed, context.FailedAtUtc!.Value.ToUnixTimeSeconds());
        Assert.True(NodeWorkerStartupContext.TryParse($"v1;-;{launch:D};-;-", out var retained)); Assert.Null(retained!.ActivationNonce);
        Assert.True(NodeWorkerStartupContext.TryParse(nonce.ToString("D"), out var legacy)); Assert.Equal(nonce, legacy!.ActivationNonce);
        foreach (var invalid in new[] { new string('x', 193), $"v2;-;{launch:D};-;-", $"v1;-;{Guid.Empty:D};-;-", $"v1;-;{launch:D};{release};-", $"v1;-;{launch:D};-;-;extra", $"v1;-;{launch:D};{release};9223372036854775807" })
            Assert.False(NodeWorkerStartupContext.TryParse(invalid, out _));
    }

    [Fact]
    public void Fix2_actual_link_validation_handle_allows_atomic_journal_replacement()
    {
        PrepareRoot(); var current = Image("fix2-sharing"); var record = Committed(current);
        NodeUpdateState.Write(Updates, record);
        using var validation = NodePackage.OpenPrivateReadHandle(Path.Combine(Updates, "state.json"));
        NodeUpdateState.Write(Updates, record with { LastFailedReleaseId = new string('B', 64), LastFailedAtUtc = DateTimeOffset.UtcNow });
        Assert.Equal(new string('B', 64), NodeUpdateState.ReadPrimary(Updates)!.LastFailedReleaseId);
    }

    [Fact]
    public async Task Fix2_recycled_retained_pid_requires_fresh_launch_publication()
    {
        PrepareRoot(); var current = Image("fix2-fresh-A"); var candidate = Image("fix2-fresh-B") with { ReleaseId = new string('B', 64) }; var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current));
        NodeUpdateState.Write(Updates, new(1, current, null, candidate, nonce, NodeUpdatePhase.Rollback, candidate.ReleaseId, DateTimeOffset.UtcNow));
        NodeUpdateState.WriteReady(Updates, new(nonce, current.ContentId, 12345, Guid.NewGuid()));
        FileStream? held = new(Path.Combine(Updates, "ready.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        NodeLaunchSpec? launched = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, launch => { launched = launch; return new WaitingChild(); }, TimeProvider.System, stop.Token);
        try
        {
            await Task.Delay(150);
            Assert.Equal(NodeUpdatePhase.Rollback, NodeUpdateState.ReadPrimary(Updates)!.Phase);
            Assert.False(run.IsCompleted);
            Assert.NotNull(launched!.StartupContext); Assert.NotEqual(Guid.Empty, launched.StartupContext.LaunchId);
            held.Dispose(); held = null;
            NodeUpdateState.WriteReady(Updates, new(nonce, current.ContentId, 12345, launched.StartupContext.LaunchId));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (ReadPrimaryPhase(Updates) != NodeUpdatePhase.Committed && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
            Assert.Equal(NodeUpdatePhase.Committed, NodeUpdateState.ReadPrimary(Updates)!.Phase);
        }
        finally { held?.Dispose(); stop.Cancel(); await run; }
    }

    [Fact]
    public async Task Fix2_starting_refusal_carries_bounded_failure_into_retained_launch()
    {
        PrepareRoot(); var current = Image("fix2-refusal-A"); var candidate = Image("fix2-refusal-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        using var held = new FileStream(Path.Combine(Updates, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var launches = 0; NodeWorkerStartupContext? recovery = null;
        Assert.Equal(0, await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, launch =>
        {
            if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            recovery = launch.StartupContext; return new UpdateExitedChild(0);
        }, TimeProvider.System, default));
        Assert.Equal(2, launches); Assert.NotNull(recovery);
        Assert.Equal(candidate.ReleaseId, recovery.FailedReleaseId); Assert.NotNull(recovery.FailedAtUtc);
        Assert.InRange(DateTimeOffset.UtcNow - recovery.FailedAtUtc.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fix1_retained_current_survives_late_ready_io_or_stale_evidence_then_commits(bool locked)
    {
        PrepareRoot(); var current = Image("fix-late-A"); var candidate = Image("fix-late-B") with { ReleaseId = new string('B', 64) }; var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current)); NodeUpdateState.Write(Updates, new(1, current, null, candidate, nonce, NodeUpdatePhase.Rollback, candidate.ReleaseId, DateTimeOffset.UtcNow));
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); FileStream? held = null; Guid launchId = Guid.Empty;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var supervisor = NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, launch =>
        {
            launchId = launch.StartupContext!.LaunchId;
            NodeUpdateState.WriteReady(Updates, new(Guid.NewGuid(), current.ContentId, 999));
            if (locked) held = new(Path.Combine(Updates, "ready.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            launched.TrySetResult(); return new WaitingChild();
        }, new FixExpiredClock(), stop.Token);
        try
        {
            await launched.Task; Assert.False(supervisor.IsCompleted);
            var pending = NodeUpdateState.Read(Updates).Record!; Assert.Equal(candidate.ContentId, pending.Pending!.ContentId); Assert.Equal(nonce, pending.ActivationNonce);
            held?.Dispose(); held = null;
            NodeUpdateState.WriteReady(Updates, new(nonce, current.ContentId, 12345, launchId));
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            while (ReadPrimaryPhase(Updates) != NodeUpdatePhase.Committed && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
            var committed = NodeUpdateState.Read(Updates).Record!;
            Assert.Equal(NodeUpdatePhase.Committed, committed.Phase); Assert.Null(committed.Pending); Assert.Null(committed.ActivationNonce); Assert.Equal(current.ContentId, committed.Current.ContentId);
            Assert.False(supervisor.IsCompleted);
        }
        finally { held?.Dispose(); stop.Cancel(); await supervisor; }
    }

    private sealed class FixExpiredClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Add(ref timestamp, 31);
    }

    [SkippableFact]
    public async Task Fix1_nonprivate_request_is_not_consumed_or_deleted_and_current_remains_available()
    {
        if (!OperatingSystem.IsWindows()) throw new Xunit.SkipException("This refusal fixture requires Windows file ACLs.");
        PrepareRoot(); var current = Image("fix-untrusted-request"); NodeUpdateState.Write(Updates, Committed(current));
        var path = Path.Combine(Updates, "request.json"); NodeUpdateState.WriteRequest(Updates, new(current with { ReleaseId = new string('B', 64) }));
        var bytes = File.ReadAllBytes(path); var file = new FileInfo(path); var acl = FileSystemAclExtensions.GetAccessControl(file);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Allow)); FileSystemAclExtensions.SetAccessControl(file, acl);
        var launches = 0;
        Assert.Equal(0, await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, _ => { launches++; return new UpdateExitedChild(0); }, TimeProvider.System, default));
        Assert.Equal(1, launches); Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(current.ContentId, NodeUpdateState.Read(Updates).Record!.Current.ContentId);
    }

    [Fact]
    public async Task Fix1_rollback_write_refusal_keeps_retained_current_available_and_pending_evidence()
    {
        PrepareRoot(); var current = Image("fix-rollback-A"); var candidate = Image("fix-rollback-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current)); FileStream? blocked = null; var launches = 0; var retained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var supervisor = NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, launch =>
        {
            if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            var journal = NodeUpdateState.Read(Updates).Record!;
            if (launches == 2)
            {
                blocked = new FileStream(Path.Combine(Updates, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                NodeUpdateState.WriteReady(Updates, new(Guid.NewGuid(), candidate.ContentId, 5678));
                return new UpdateReadyChild(Updates);
            }
            Assert.Equal(Path.Combine(Updates, "versions", current.ContentId, "CoCern.Adapter.dll"), launch.Arguments[0]);
            NodeUpdateState.WriteReady(Updates, new(journal.ActivationNonce!.Value, current.ContentId, 12345, launch.StartupContext!.LaunchId)); retained.TrySetResult(); return new WaitingChild();
        }, TimeProvider.System, stop.Token);
        try
        {
            await Task.WhenAny(retained.Task, supervisor).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(retained.Task.IsCompleted);
            var journal = NodeUpdateState.Read(Updates).Record!;
            Assert.NotNull(journal.Pending); Assert.NotNull(journal.ActivationNonce); Assert.Equal(current.ContentId, journal.Current.ContentId);
            Assert.False(supervisor.IsCompleted);
        }
        finally { stop.Cancel(); blocked?.Dispose(); await supervisor; }
    }

    [Fact]
    public async Task Fix1_journal_observers_do_not_deny_atomic_replacement_during_image_validation()
    {
        PrepareRoot(); var current = Image(new string('x', 2 * 1024 * 1024)); NodeUpdateState.Write(Updates, Committed(current));
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () => { while (!stop.IsCancellationRequested) { NodeUpdateState.Read(Updates); await Task.Yield(); } })).ToArray();
        try { for (var i = 0; i < 8; i++) await Task.Run(() => NodeUpdateState.Write(Updates, Committed(current))); }
        finally { stop.Cancel(); await Task.WhenAll(readers); }
        Assert.Equal(current.ContentId, NodeUpdateState.Read(Updates).Record!.Current.ContentId);
    }

    [Fact]
    public async Task Fix1_starting_replace_refusal_restarts_authoritative_committed_current()
    {
        PrepareRoot();
        var current = Image("fix-start-A"); var candidate = Image("fix-start-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var before = File.ReadAllBytes(Path.Combine(Updates, "state.json"));
        using var blocked = new FileStream(Path.Combine(Updates, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var launches = 0;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, launch =>
        {
            Assert.Equal(Path.Combine(Updates, "versions", current.ContentId, "CoCern.Adapter.dll"), launch.Arguments[0]);
            if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            return new UpdateExitedChild(0);
        }, TimeProvider.System, default);
        Assert.Equal(0, exit); Assert.Equal(2, launches);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Updates, "state.json")));
    }

    [Theory]
    [InlineData("request.json")]
    [InlineData("ready.json")]
    public async Task Fix1_locked_handoff_debris_does_not_prevent_verified_current(string name)
    {
        PrepareRoot(); var current = Image("fix-debris-A");
        NodeUpdateState.Write(Updates, Committed(current));
        if (name == "request.json") NodeUpdateState.WriteRequest(Updates, new(current with { ReleaseId = new string('B', 64) }));
        else NodeUpdateState.WriteReady(Updates, new(Guid.NewGuid(), current.ContentId, 999));
        var bytes = File.ReadAllBytes(Path.Combine(Updates, name));
        using var held = new FileStream(Path.Combine(Updates, name), FileMode.Open, FileAccess.Read, FileShare.Read);
        var launches = 0;
        Assert.Equal(0, await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, _ => { launches++; return new UpdateExitedChild(0); }, TimeProvider.System, default));
        Assert.Equal(1, launches); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Updates, name)));
        Assert.Equal(NodeUpdatePhase.Committed, NodeUpdateState.Read(Updates).Record!.Phase);
    }

    [Fact]
    public async Task Fix1_signaled_owned_child_is_rejected_before_delayed_wait_task_completes()
    {
        PrepareRoot(); var current = Image("fix-signaled-A"); var candidate = Image("fix-signaled-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current)); var launches = 0;
        Assert.Equal(0, await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", FixHost, launch =>
        {
            if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            var journal = NodeUpdateState.Read(Updates).Record!;
            NodeUpdateState.WriteReady(Updates, new(journal.ActivationNonce!.Value, launches == 2 ? candidate.ContentId : current.ContentId, 5678, launch.StartupContext!.LaunchId));
            return new UpdateReadyChild(Updates, signaled: launches == 2);
        }, TimeProvider.System, default));
        Assert.Equal(3, launches); Assert.Equal(current.ContentId, NodeUpdateState.Read(Updates).Record!.Current.ContentId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fix1_private_worker_refusal_never_creates_or_adopts_identity(bool missing)
    {
        PrepareRoot(); var current = Image("fix-identity"); NodeUpdateState.Write(Updates, Committed(current));
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        var path = Path.Combine(NodeFolder, "node.json");
        if (missing) File.Delete(path);
        else { var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; json["enrolled"] = false; File.WriteAllText(path, json.ToJsonString()); }
        var before = missing ? null : File.ReadAllBytes(path); var requests = 0;
        var transport = new NodeTransport { Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            CreateHandler = (_, _) => new UpdateWorkerHandler(_ => { requests++; return new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }; }) };
        try { Assert.Equal(1, await NodeRunner.RunWorkerAsync(NodeFolder, null, Path.Combine(Updates, "versions", current.ContentId), transport, new StringWriter(), default)); }
        catch (CoCern.Adapter.AdapterConfigurationException) { }
        Assert.Equal(0, requests);
        if (missing) Assert.False(File.Exists(path)); else Assert.Equal(before, File.ReadAllBytes(path));
        using var released = new FileStream(Path.Combine(NodeFolder, "node.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Fix1_nonce_worker_admission_preserves_candidate_commit_fence_and_retained_availability(bool candidateStartup, bool checkDispatchFirst)
    {
        PrepareRoot(); var current = Image("fix-admission-A"); var candidate = Image("fix-admission-B") with { ReleaseId = new string('B', 64) }; var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current)); NodeUpdateState.Write(Updates, new(1, current, null, candidate, nonce, NodeUpdatePhase.Starting, null, null));
        var thread = Guid.NewGuid().ToString();
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        NodeState.WriteSessionHandle(NodeFolder, new("session", "session-token", "repo", "candidate", DateTimeOffset.UtcNow, Chat: new("codex", thread)));
        var dispatcher = new FixDispatcher(); var stages = 0; var announces = 0; var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => "inert-never-run", CodexDispatcher = _ => dispatcher,
            AnnounceInterval = TimeSpan.FromMilliseconds(20), WakeInterval = TimeSpan.FromMilliseconds(20), UpdateHost = FixHost,
            CreateUpdater = _ => new UpdateScheduleCoordinator(() => Interlocked.Increment(ref stages)), HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            Trace = value => { if (value == "announced" && ++announces == 4) cycled.TrySetResult(); },
            CreateHandler = (_, _) => new UpdateWorkerHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("announce")) return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { routes = new[] { new { provider = "codex", threadId = thread, relaySessionId = "session", repositoryId = "repo" } } })) };
                object data = request.RequestUri.AbsolutePath.EndsWith("get_status") ? new { sessionId = "session", control = new { paused = false, requests = Array.Empty<object>() } } : new { items = new[] { new { messageId = "candidate-mail", repositoryId = "repo" } }, total = 1, nextCursor = (int?)null, truncated = false };
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = JsonSerializer.Serialize(data), version = 1 })) };
            })
        };
        FileStream? readyLock = null;
        if (!candidateStartup) { NodeUpdateState.WriteReady(Updates, new(Guid.NewGuid(), current.ContentId, 999)); readyLock = new(Path.Combine(Updates, "ready.json"), FileMode.Open, FileAccess.Read, FileShare.Read); }
        var worker = NodeRunner.RunWorkerAsync(NodeFolder, nonce, Path.Combine(Updates, "versions", candidateStartup ? candidate.ContentId : current.ContentId), transport, new StringWriter(), stop.Token);
        try
        {
            await Task.WhenAny(cycled.Task, worker).WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(cycled.Task.IsCompleted);
            if (candidateStartup)
            {
                if (checkDispatchFirst) { Assert.Equal(0, dispatcher.Calls); Assert.Equal(0, stages); }
                else { Assert.Equal(0, stages); Assert.Equal(0, dispatcher.Calls); }
                Assert.NotNull(NodeUpdateState.ReadReady(Updates));
                NodeUpdateState.Write(Updates, Committed(candidate) with { Previous = current });
                await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            else { Assert.Equal(1, dispatcher.Calls); Assert.Equal(0, stages); Assert.Equal(candidate.ContentId, NodeUpdateState.Read(Updates).Record!.Pending!.ContentId); }
        }
        finally { stop.Cancel(); readyLock?.Dispose(); var exit = await worker; if (cycled.Task.IsCompleted) Assert.Equal(0, exit); }
    }

    private sealed class FixServingDispatcher : CoCern.Adapter.IWakeDispatcher
    {
        public int Calls;
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            return Completed.Task.WaitAsync(ct);
        }
    }

    private sealed class FixDispatcher : CoCern.Adapter.IWakeDispatcher
    {
        public int Calls; public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> QueueAsync(string threadId, string prompt, CancellationToken ct) { Interlocked.Increment(ref Calls); Entered.TrySetResult(); return Task.FromResult(false); }
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("metadata")]
    [InlineData("dll")]
    public async Task Update_prepare_requires_complete_manifest_image_agreement_and_keeps_caller_http_alive(string defect)
    {
        PrepareRoot();
        var current = Image("prepare-identity-A");
        var candidate = Image("prepare-identity-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var before = File.ReadAllBytes(Path.Combine(Updates, "state.json"));
        var release = new NodeRelease(1, candidate.ReleaseId!, defect == "metadata" ? candidate.Package with { ProductVersion = "9.9.9" } : candidate.Package, 1, candidate.ReleaseId!, defect == "dll" ? new string('C', 64) : candidate.AdapterDllSha256);
        using var http = new HttpClient(new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var coordinator = new NodeUpdateCoordinator(new(NodeFolder, new("http://localhost/"), http, new string('a', 64), host));
        var staged = new StagedNode(release, candidate, Path.Combine(Updates, "versions", candidate.ContentId));
        if (defect == "valid")
        {
            await coordinator.PrepareAsync(staged, default);
            AssertImage(candidate, NodeUpdateState.ReadRequest(Updates)!.Candidate);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.PrepareAsync(staged, default));
            Assert.Null(NodeUpdateState.ReadRequest(Updates));
        }
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Updates, "state.json")));
        using var alive = await http.GetAsync("http://localhost/fixture-alive");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, alive.StatusCode);
    }

    [Fact]
    public async Task Update_byte_identical_bootstrap_offer_is_downloaded_validated_and_not_activated()
    {
        PrepareRoot();
        var current = Image("identical-bootstrap");
        Assert.Null(current.ReleaseId);
        NodeUpdateState.Write(Updates, Committed(current));
        var archive = Path.Combine(fixture, "identical.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(Updates, "versions", current.ContentId), archive);
        var bytes = File.ReadAllBytes(archive);
        var releaseId = Convert.ToHexString(SHA256.HashData(bytes));
        var release = new NodeRelease(1, releaseId, current.Package, bytes.Length, releaseId, current.AdapterDllSha256);
        var requests = 0;
        using var http = new HttpClient(new UpdateWorkerHandler(request =>
        {
            requests++;
            Assert.Equal(new string('a', 64), request.Headers.Authorization?.Parameter);
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var coordinator = new NodeUpdateCoordinator(new(NodeFolder, new("http://localhost/"), http, new string('a', 64), host));
        Assert.Null(await coordinator.TryStageAsync(release, default));
        Assert.Equal(1, requests);
        Assert.Equal(current.ContentId, NodeUpdateState.Read(Updates).Record!.Current.ContentId);
        Assert.Null(NodeUpdateState.ReadRequest(Updates));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_invalid_or_same_image_request_after_exit_restarts_verified_current_without_journal_change(bool sameImage)
    {
        PrepareRoot();
        var current = Image("request-refusal-A");
        NodeUpdateState.Write(Updates, Committed(current));
        var before = File.ReadAllBytes(Path.Combine(Updates, "state.json"));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var launches = 0;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
        {
            Assert.Equal(Path.Combine(Updates, "versions", current.ContentId, "CoCern.Adapter.dll"), launch.Arguments[0]);
            if (++launches == 1)
            {
                if (sameImage) NodeUpdateState.WriteRequest(Updates, new(current with { ReleaseId = new string('B', 64) }));
                else WriteRaw("request.json", "invalid-request");
                return new UpdateExitedChild(75);
            }
            return new UpdateExitedChild(0);
        }, TimeProvider.System, default);
        Assert.Equal(0, exit);
        Assert.Equal(2, launches);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Updates, "state.json")));
    }

    [Fact]
    public async Task Update_private_worker_without_nonce_refuses_first_401_without_reenrollment()
    {
        PrepareRoot();
        var current = Image("no-nonce-auth");
        NodeUpdateState.Write(Updates, Committed(current));
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        var before = File.ReadAllBytes(Path.Combine(NodeFolder, "node.json"));
        var paths = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            CreateHandler = (_, _) => new UpdateWorkerHandler(request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                if (paths.Count == 2) stop.Cancel();
                return new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            }),
            FirstRetry = TimeSpan.FromMilliseconds(10)
        };
        Assert.Equal(1, await NodeRunner.RunWorkerAsync(NodeFolder, null, Path.Combine(Updates, "versions", current.ContentId), transport, new StringWriter(), stop.Token));
        Assert.Equal(["/api/discovery/announce"], paths);
        using var reopened = NodeState.Open(NodeFolder);
        Assert.Equal(new string('a', 64), reopened.ReadCredential());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(NodeFolder, "node.json")));
    }

    [Fact]
    public async Task Update_mail_after_old_exit_and_before_candidate_authentication_survives_commit()
    {
        PrepareRoot();
        var current = Image("mail-gap-A");
        var candidate = Image("mail-gap-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var database = Path.Combine(fixture, "mail-gap-hub.db");
        (string SessionId, string Token) receiver = default;
        string credential;
        var settings = new Dictionary<string, string?> { ["Relay:Discovery:Enabled"] = "true", ["Relay:Discovery:Repositories:0"] = "repo" };
        await using (var hub = new RelayHostFixture(database, remoteAddress: System.Net.IPAddress.Loopback, settings: settings))
        {
            using var machine = hub.CreateClient();
            using (var state = NodeState.Open(NodeFolder))
            {
                state.SetHub("http://localhost/", null);
                using var enrolled = await machine.PostAsJsonAsync("api/discovery/enroll", new { machineId = state.MachineId, machineName = "fixture worker", joinCode = RelayHostFixture.JoinCode });
                enrolled.EnsureSuccessStatusCode();
                var json = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
                credential = json.GetProperty("credential").GetString()!;
                state.SaveCredential(credential);
            }
            machine.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            async Task<(string SessionId, string Token)> Open(string request)
            {
                var reply = await hub.Call(machine, "session_open", new { repositoryId = "repo", requestId = request });
                using var data = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!);
                return (data.RootElement.GetProperty("sessionId").GetString()!, data.RootElement.GetProperty("sessionToken").GetString()!);
            }
            var sender = await Open("gap-sender");
            receiver = await Open("gap-receiver");
            var announceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var authenticate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new NodeTransport
            {
                Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
                CreateHandler = (_, _) => new UpdateAnnounceBarrier(hub.Server.CreateHandler(), announceEntered, authenticate), HubHandler = () => hub.Server.CreateHandler()
            };
            var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var launches = 0;
            var supervisor = NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
            {
                if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
                var starting = NodeUpdateState.Read(Updates).Record!;
                Assert.NotNull(launch.StartupContext); Assert.NotEqual(Guid.Empty, launch.StartupContext.LaunchId);
                return new UpdateWorkerChild(ct => NodeRunner.RunWorkerAsync(NodeFolder, starting.ActivationNonce, Path.Combine(Updates, "versions", candidate.ContentId), transport, new StringWriter(), ct, launch.StartupContext), stop.Token);
            }, TimeProvider.System, stop.Token);
            try
            {
                await announceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, launches); // Old child exit was observed before candidate startup.
                Assert.Null(NodeUpdateState.ReadReady(Updates));
                Assert.Equal(NodeUpdatePhase.Starting, NodeUpdateState.Read(Updates).Record!.Phase);
                await hub.Call(machine, "send_message", new { repositoryId = "repo", sessionId = sender.SessionId, sessionToken = sender.Token, recipientSessionId = receiver.SessionId, requestId = "gap-mail", text = "mail after old exit before candidate ready" });
                await GapInboxCount(hub, machine, receiver, 1);
                Assert.Null(NodeUpdateState.ReadReady(Updates));
                authenticate.TrySetResult();
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                while (ReadPrimaryPhase(Updates) != NodeUpdatePhase.Committed && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
                var committed = NodeUpdateState.Read(Updates).Record!;
                Assert.Equal(NodeUpdatePhase.Committed, committed.Phase);
                Assert.Equal(candidate.ContentId, committed.Current.ContentId);
                Assert.Equal(current.ContentId, committed.Previous!.ContentId);
                Assert.Equal(Environment.ProcessId, NodeUpdateState.ReadReady(Updates)!.ProcessId);
                await GapInboxCount(hub, machine, receiver, 1);
            }
            finally { authenticate.TrySetResult(); stop.Cancel(); Assert.Equal(0, await supervisor); }
        }
        await using var reopened = new RelayHostFixture(database, remoteAddress: System.Net.IPAddress.Loopback, settings: settings);
        using var reader = reopened.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        await GapInboxCount(reopened, reader, receiver, 1);
    }

    private static async Task GapInboxCount(RelayHostFixture hub, HttpClient machine, (string SessionId, string Token) receiver, int expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        var count = 0;
        do
        {
            var reply = await hub.Call(machine, "check_inbox", new { repositoryId = "repo", sessionId = receiver.SessionId, sessionToken = receiver.Token, attention = false, headersOnly = true });
            using var data = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!);
            count = data.RootElement.GetProperty("items").GetArrayLength();
            if (count == expected) break;
            await Task.Delay(20);
        } while (DateTimeOffset.UtcNow < deadline);
        Assert.Equal(expected, count);
    }

    private sealed class UpdateAnnounceBarrier(HttpMessageHandler inner, TaskCompletionSource entered, TaskCompletionSource authenticate) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/discovery/announce", StringComparison.Ordinal)) { entered.TrySetResult(); await authenticate.Task.WaitAsync(ct); }
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class UpdateWorkerChild(Func<CancellationToken, Task<int>> run, CancellationToken ct) : INodeChild
    {
        public bool HasExited => ended?.IsCompleted == true;
        private readonly CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        private Task<int>? ended;
        public int ProcessId => Environment.ProcessId;
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => ended ??= run(stopping.Token);
        public async ValueTask DisposeAsync() { stopping.Cancel(); if (ended is not null) { try { await ended; } catch (OperationCanceledException) { } } stopping.Dispose(); }
    }

    [Fact]
    public async Task Update_scheduler_honors_supervisor_failed_release_after_worker_restart()
    {
        PrepareRoot();
        var current = Image("persisted-backoff-A");
        var clock = new NodeTests.UpdateClock();
        var began = clock.GetUtcNow();
        var failedRelease = new string('B', 64);
        var release = new NodeRelease(1, failedRelease, current.Package, 1, failedRelease, current.AdapterDllSha256);
        NodeUpdateState.Write(Updates, Committed(current) with { LastFailedReleaseId = failedRelease, LastFailedAtUtc = began });
        using var state = NodeState.Open(NodeFolder);
        state.SetHub("http://localhost/", null);
        state.SaveCredential(new string('a', 64));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = 0;
        var announcements = 0;
        var early = false;
        var transport = new NodeTransport
        {
            Clock = clock, Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            AnnounceInterval = TimeSpan.FromMilliseconds(10), UpdateHost = new(null, [], "win-x64"),
            CreateHandler = (_, _) => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { routes = Array.Empty<object>(), nodeRelease = release }, new JsonSerializerOptions(JsonSerializerDefaults.Web))) }),
            HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            CreateUpdater = _ => new UpdateScheduleCoordinator(() => { calls++; early = clock.GetUtcNow() - began < TimeSpan.FromMinutes(15); stop.Cancel(); }),
            Trace = value => { if (value == "announced" && ++announcements == 3) clock.Advance(TimeSpan.FromMinutes(16)); }
        };
        Assert.Equal(0, await NodeRunner.RunAsync(state, null, transport, new StringWriter(), stop.Token));
        Assert.False(early);
        Assert.Equal(1, calls);
    }

    private sealed class UpdateScheduleCoordinator(Action stage) : INodeUpdateCoordinator
    {
        public Task<StagedNode?> TryStageAsync(NodeRelease? offered, CancellationToken ct) { stage(); return Task.FromResult<StagedNode?>(null); }
        public Task PrepareAsync(StagedNode staged, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Update_matching_ready_from_an_already_exited_candidate_rolls_back()
    {
        PrepareRoot();
        var current = Image("dead-ready-A");
        var candidate = Image("dead-ready-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var launches = 0;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
        {
            if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            var journal = NodeUpdateState.Read(Updates).Record!;
            NodeUpdateState.WriteReady(Updates, new(journal.ActivationNonce!.Value, launches == 2 ? candidate.ContentId : current.ContentId, 5678, launch.StartupContext!.LaunchId));
            return launches == 2 ? new UpdateExitedChild(0) : new UpdateReadyChild(Updates);
        }, TimeProvider.System, default);
        Assert.Equal(0, exit);
        Assert.Equal(3, launches);
        var retained = NodeUpdateState.Read(Updates).Record!;
        Assert.Equal(current.ContentId, retained.Current.ContentId);
        Assert.Equal(candidate.ReleaseId, retained.LastFailedReleaseId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_supervisor_rereads_the_authoritative_commit_after_replace_failure(bool primaryCommitted)
    {
        PrepareRoot();
        var current = Image("commit-fault-A");
        var candidate = Image("commit-fault-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var launches = 0;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
        {
            if (++launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            var journal = NodeUpdateState.Read(Updates).Record!;
            NodeUpdateState.WriteReady(Updates, new(journal.ActivationNonce!.Value, launches == 2 ? candidate.ContentId : current.ContentId, 5678, launch.StartupContext!.LaunchId));
            var blocked = launches == 2 ? new FileStream(Path.Combine(Updates, primaryCommitted ? "committed.json" : "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            return new UpdateReadyChild(Updates, blocked);
        }, TimeProvider.System, default);
        Assert.Equal(0, exit);
        Assert.Equal(primaryCommitted ? 2 : 3, launches);
        var journal = NodeUpdateState.Read(Updates).Record!;
        Assert.Equal(NodeUpdatePhase.Committed, journal.Phase);
        Assert.Equal(primaryCommitted ? candidate.ContentId : current.ContentId, journal.Current.ContentId);
        Assert.Equal(primaryCommitted ? current.ContentId : null, journal.Previous?.ContentId);
    }

    [Theory]
    [InlineData(NodeUpdatePhase.Starting, false)]
    [InlineData(NodeUpdatePhase.Starting, true)]
    [InlineData(NodeUpdatePhase.Rollback, false)]
    [InlineData(NodeUpdatePhase.Rollback, true)]
    public async Task Update_damaged_pending_remains_recovery_evidence_until_authenticated_retained_start(NodeUpdatePhase phase, bool deleted)
    {
        PrepareRoot();
        var current = Image("retained-damage-A");
        var pending = Image("retained-damage-B") with { ReleaseId = new string('B', 64) };
        var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current));
        NodeUpdateState.Write(Updates, new(1, current, null, pending, nonce, phase, phase == NodeUpdatePhase.Rollback ? pending.ReleaseId : null, phase == NodeUpdatePhase.Rollback ? DateTimeOffset.UtcNow : null));
        var candidateDll = Path.Combine(Updates, "versions", pending.ContentId, "CoCern.Adapter.dll");
        if (deleted) File.Delete(candidateDll); else File.AppendAllText(candidateDll, "changed-after-starting");
        var recovered = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, recovered.Status);
        Assert.Equal(phase, recovered.Record!.Phase);
        Assert.Equal(pending.ContentId, recovered.Record.Pending!.ContentId);
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var launches = 0;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
        {
            launches++;
            Assert.Equal(Path.Combine(Updates, "versions", current.ContentId, "CoCern.Adapter.dll"), launch.Arguments[0]);
            var journal = NodeUpdateState.Read(Updates).Record!;
            Assert.Equal(NodeUpdatePhase.Rollback, journal.Phase);
            Assert.Equal(pending.ContentId, journal.Pending!.ContentId);
            Assert.Equal(nonce, journal.ActivationNonce);
            NodeUpdateState.WriteReady(Updates, new(nonce, current.ContentId, 5678, launch.StartupContext!.LaunchId));
            return new UpdateReadyChild(Updates);
        }, TimeProvider.System, default);
        Assert.Equal(0, exit);
        Assert.Equal(1, launches);
        var committed = NodeUpdateState.Read(Updates).Record!;
        Assert.Equal(NodeUpdatePhase.Committed, committed.Phase);
        Assert.Equal(current.ContentId, committed.Current.ContentId);
        Assert.Null(committed.Pending);
        Assert.Null(committed.ActivationNonce);
        Assert.Equal(pending.ReleaseId, committed.LastFailedReleaseId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_new_starting_still_refuses_pending_that_does_not_rehash(bool deleted)
    {
        PrepareRoot();
        var current = Image("new-start-A");
        var pending = Image("new-start-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var candidateDll = Path.Combine(Updates, "versions", pending.ContentId, "CoCern.Adapter.dll");
        if (deleted) File.Delete(candidateDll); else File.AppendAllText(candidateDll, "changed-before-starting");
        Assert.Throws<InvalidDataException>(() => NodeUpdateState.Write(Updates, new(1, current, null, pending, Guid.NewGuid(), NodeUpdatePhase.Starting, null, null)));
        Assert.Equal(NodeUpdatePhase.Committed, NodeUpdateState.Read(Updates).Record!.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_worker_lifecycle_writes_ready_only_after_accepted_authentication_without_reenrollment(bool accepted)
    {
        PrepareRoot();
        var current = Image("worker-auth-A");
        var candidate = Image("worker-auth-B") with { ReleaseId = new string('B', 64) };
        var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current));
        NodeUpdateState.Write(Updates, new(1, current, null, candidate, nonce, NodeUpdatePhase.Starting, null, null));
        using (var state = NodeState.Open(NodeFolder)) { state.SetHub("http://localhost/", null); state.SaveCredential(new string('a', 64)); }
        var identityBefore = File.ReadAllBytes(Path.Combine(NodeFolder, "node.json"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var paths = new List<string>();
        var announces = 0;
        var transport = new NodeTransport
        {
            Clients = () => [], Locators = [], Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()), CodexExecutable = () => null,
            CreateHandler = (_, _) => new UpdateWorkerHandler(request =>
            {
                Assert.Null(NodeUpdateState.ReadReady(Updates));
                paths.Add(request.RequestUri!.AbsolutePath);
                Assert.Equal(new string('a', 64), request.Headers.Authorization?.Parameter);
                return new(accepted ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{\"routes\":[]}") };
            }),
            HubHandler = () => new UpdateWorkerHandler(_ => new(System.Net.HttpStatusCode.ServiceUnavailable)),
            Trace = value => { if (value == "announced" && ++announces == 1) stop.Cancel(); },
            AnnounceInterval = TimeSpan.FromMilliseconds(10)
        };
        var exit = await NodeRunner.RunWorkerAsync(NodeFolder, nonce, Path.Combine(Updates, "versions", candidate.ContentId), transport, new StringWriter(), stop.Token);
        Assert.Single(paths);
        Assert.Equal("/api/discovery/announce", paths[0]);
        Assert.Equal(accepted ? 0 : 1, exit);
        var ready = NodeUpdateState.ReadReady(Updates);
        if (accepted) Assert.Equal(new NodeReady(nonce, candidate.ContentId, Environment.ProcessId), ready);
        else Assert.Null(ready);
        using var reopened = NodeState.Open(NodeFolder);
        Assert.Equal(new string('a', 64), reopened.ReadCredential());
        Assert.Equal(identityBefore, File.ReadAllBytes(Path.Combine(NodeFolder, "node.json")));
    }

    private sealed class UpdateWorkerHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_worker_waits_for_the_original_singleton_before_configuration_refusal(bool activation)
    {
        Directory.CreateDirectory(fixture);
        var target = Path.Combine(fixture, "takeover-node");
        Directory.CreateDirectory(target);
        var singleton = new FileStream(Path.Combine(target, "node.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var arguments = new List<string> { "--node-worker", "--data-dir", target };
        if (activation) arguments.AddRange(["--activation-nonce", Guid.NewGuid().ToString("D")]);
        using var process = IsolatedAdapterProcess(Path.Combine(fixture, "must-not-open-default"), arguments.ToArray());
        Assert.True(process.Start());
        output.WriteLine($"worker-takeover:start:pid={process.Id},activation={activation},utc={DateTimeOffset.UtcNow:O}");
        var stdout = ReadBounded(process.StandardOutput);
        process.StandardInput.Close();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Equal("node-update:singleton-wait", await process.StandardError.ReadLineAsync(timeout.Token));
            Assert.False(process.HasExited);
            singleton.Dispose();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(await stdout);
            using var released = new FileStream(Path.Combine(target, "node.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            singleton.Dispose();
            if (!process.HasExited) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); }
            output.WriteLine($"worker-takeover:end:pid={process.Id},exited={process.HasExited},exit={process.ExitCode},utc={DateTimeOffset.UtcNow:O}");
        }
    }

    [Theory]
    [InlineData(NodeUpdatePhase.Starting)]
    [InlineData(NodeUpdatePhase.Rollback)]
    public void Update_worker_validates_nonce_and_its_exact_immutable_image_before_authentication(NodeUpdatePhase phase)
    {
        PrepareRoot();
        var current = Image("worker-A");
        var candidate = Image("worker-B") with { ReleaseId = new string('B', 64) };
        var nonce = Guid.NewGuid();
        NodeUpdateState.Write(Updates, Committed(current));
        NodeUpdateState.Write(Updates, new(1, current, null, candidate, nonce, phase, null, null));
        var expected = phase == NodeUpdatePhase.Starting ? candidate : current;
        var path = Path.Combine(Updates, "versions", expected.ContentId);
        var validated = NodeUpdateState.ValidateWorkerStartup(NodeFolder, nonce, path);
        Assert.NotNull(validated);
        AssertImage(expected, validated!);
        Assert.Null(NodeUpdateState.ValidateWorkerStartup(NodeFolder, Guid.NewGuid(), path));
        Assert.Null(NodeUpdateState.ValidateWorkerStartup(NodeFolder, nonce, fixture));
        File.AppendAllText(Path.Combine(path, "CoCern.Adapter.dll"), "changed");
        Assert.Null(NodeUpdateState.ValidateWorkerStartup(NodeFolder, nonce, path));
    }

    [Fact]
    public void Update_handoff_records_are_private_fixed_and_do_not_mutate_the_journal()
    {
        PrepareRoot();
        var current = Image("record-A");
        var candidate = Image("record-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var before = File.ReadAllBytes(Path.Combine(Updates, "state.json"));
        var request = new NodeUpdateRequest(candidate);
        var ready = new NodeReady(Guid.NewGuid(), candidate.ContentId, 12345);
        NodeUpdateState.WriteRequest(Updates, request);
        NodeUpdateState.WriteReady(Updates, ready);
        AssertImage(request.Candidate, NodeUpdateState.ReadRequest(Updates)!.Candidate);
        Assert.Equal(ready, NodeUpdateState.ReadReady(Updates));
        AssertPrivate(Path.Combine(Updates, "request.json"));
        AssertPrivate(Path.Combine(Updates, "ready.json"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Updates, "state.json")));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("nonce")]
    [InlineData("content")]
    [InlineData("pid")]
    public async Task Update_supervisor_commits_only_exact_owned_authenticated_readiness(string identity)
    {
        PrepareRoot();
        var current = Image("handoff-A");
        var candidate = Image("handoff-B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var launches = 0;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/unused/CoCern.Adapter.dll", host, launch =>
        {
            launches++;
            if (launches == 1) { NodeUpdateState.WriteRequest(Updates, new(candidate)); return new UpdateExitedChild(75); }
            var journal = NodeUpdateState.Read(Updates).Record!;
            if (launches == 2)
            {
                Assert.Equal(NodeUpdatePhase.Starting, journal.Phase);
                NodeUpdateState.WriteReady(Updates, new(identity == "nonce" ? Guid.NewGuid() : journal.ActivationNonce!.Value, identity == "content" ? current.ContentId : candidate.ContentId, identity == "pid" ? 5679 : 5678, launch.StartupContext!.LaunchId));
            }
            else
            {
                Assert.Equal(NodeUpdatePhase.Rollback, journal.Phase);
                AssertImage(candidate, journal.Pending!);
                NodeUpdateState.WriteReady(Updates, new(journal.ActivationNonce!.Value, current.ContentId, 5678, launch.StartupContext!.LaunchId));
            }
            return new UpdateReadyChild(Updates);
        }, TimeProvider.System, default);
        Assert.Equal(0, exit);
        Assert.Equal(identity == "valid" ? 2 : 3, launches);
        var committed = NodeUpdateState.Read(Updates).Record!;
        Assert.Equal(NodeUpdatePhase.Committed, committed.Phase);
        Assert.Null(committed.Pending);
        Assert.Null(committed.ActivationNonce);
        AssertImage(identity == "valid" ? candidate : current, committed.Current);
        if (identity == "valid") AssertImage(current, committed.Previous!);
        else Assert.Null(committed.Previous);
        Assert.Equal(identity == "valid" ? null : candidate.ReleaseId, committed.LastFailedReleaseId);
    }

    private sealed class UpdateExitedChild(int code) : INodeChild
    {
        public bool HasExited => true;
        public int ProcessId => 5678;
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(code);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UpdateReadyChild(string updates, IDisposable? blockedReplace = null, bool signaled = false) : INodeChild
    {
        private readonly CancellationTokenSource stopped = new();
        private Task<int>? ended;
        public int ProcessId => 5678;
        public bool HasExited => signaled || ended?.IsCompleted == true;
        public Task<int> WaitForExitAsync(CancellationToken ct) => ended ??= WaitAsync(ct);
        private async Task<int> WaitAsync(CancellationToken ct)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stopped.Token);
            try
            {
                while (ReadPrimaryPhase(updates) != NodeUpdatePhase.Committed)
                    await Task.Delay(10, lifetime.Token);
                return 0;
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { return 1; }
        }
        public async ValueTask DisposeAsync()
        {
            blockedReplace?.Dispose();
            stopped.Cancel();
            if (ended is not null) await ended;
            stopped.Dispose();
        }
    }

    private static NodeUpdatePhase? ReadPrimaryPhase(string updates)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(updates, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<NodeUpdateRecord>(stream)?.Phase;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static void AssertImage(NodeImage expected, NodeImage actual)
    {
        Assert.Equal(expected.ContentId, actual.ContentId);
        Assert.Equal(expected.AdapterDllSha256, actual.AdapterDllSha256);
        Assert.Equal(expected.ReleaseId, actual.ReleaseId);
        Assert.Equal((expected.Package.Schema, expected.Package.ProductVersion, expected.Package.TargetFramework, expected.Package.Rid, expected.Package.SelfContained, expected.Package.SupervisorProtocol), (actual.Package.Schema, actual.Package.ProductVersion, actual.Package.TargetFramework, actual.Package.Rid, actual.Package.SelfContained, actual.Package.SupervisorProtocol));
        Assert.Equal(expected.Package.Frameworks.Select(f => (f.Name, f.Version)).OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal), actual.Package.Frameworks.Select(f => (f.Name, f.Version)).OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal));
    }

    private readonly string fixture = Path.Combine(Path.GetTempPath(), "cocern-node-supervisor-" + Guid.NewGuid().ToString("N"));
    private string NodeFolder => Path.Combine(fixture, "original node");
    private string Updates => Path.Combine(NodeFolder, "updates");

    [Fact]
    public void Missing_requires_absence_of_both_journal_records()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        Assert.Equal(NodeUpdateReadStatus.Missing, NodeUpdateState.Read(Updates).Status);
        WriteRaw("state.json", "{");
        var result = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Corrupt, result.Status);
        Assert.Null(result.Record);
        Assert.Equal("{", File.ReadAllText(Path.Combine(Updates, "state.json")));
    }

    [Fact]
    public void Committed_record_round_trips_and_is_private_without_touching_original_state()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var record = Committed(current);
        var original = File.ReadAllBytes(Path.Combine(NodeFolder, "original-state.bin"));
        NodeUpdateState.Write(Updates, record);
        var result = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, result.Status);
        Assert.Equal(JsonSerializer.Serialize(record), JsonSerializer.Serialize(result.Record));
        AssertPrivate(Path.Combine(Updates, "state.json"));
        AssertPrivate(Path.Combine(Updates, "committed.json"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(NodeFolder, "original-state.bin")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("unknown-field")]
    [InlineData("oversized")]
    public void Invalid_primary_falls_back_only_to_valid_last_committed(string damage)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var retained = Committed(Image("A"));
        WriteRaw("committed.json", JsonSerializer.Serialize(retained));
        if (damage == "truncated") WriteRaw("state.json", "{\"Schema\":1,");
        else if (damage == "unknown-field") WriteRaw("state.json", JsonSerializer.Serialize(Committed(Image("B")))[..^1] + ",\"Executable\":\"C:/foreign.exe\"}");
        else if (damage == "oversized") WriteRaw("state.json", new string(' ', 65537));
        var result = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, result.Status);
        Assert.Equal(retained.Current.ContentId, result.Record!.Current.ContentId);
        Assert.Equal(NodeUpdatePhase.Committed, result.Record.Phase);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("phase")]
    [InlineData("content-id")]
    [InlineData("content-case")]
    [InlineData("dll-case")]
    [InlineData("release-case")]
    [InlineData("metadata")]
    [InlineData("pending-without-nonce")]
    [InlineData("nonce-without-pending")]
    [InlineData("committed-pending")]
    [InlineData("failed-without-time")]
    [InlineData("time-without-failed")]
    public void Invalid_record_cannot_replace_committed_evidence(string defect)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var pending = Image("B");
        var retained = Committed(current);
        WriteRaw("state.json", JsonSerializer.Serialize(retained));
        WriteRaw("committed.json", JsonSerializer.Serialize(retained));
        var before = File.ReadAllBytes(Path.Combine(Updates, "state.json"));
        var invalid = defect switch
        {
            "schema" => retained with { Schema = 2 },
            "phase" => retained with { Phase = (NodeUpdatePhase)99 },
            "content-id" => retained with { Current = current with { ContentId = "../outside" } },
            "content-case" => retained with { Current = current with { ContentId = current.ContentId.ToUpperInvariant() } },
            "dll-case" => retained with { Current = current with { AdapterDllSha256 = current.AdapterDllSha256.ToLowerInvariant() } },
            "release-case" => retained with { Current = current with { ReleaseId = new string('a', 64) } },
            "metadata" => retained with { Current = current with { Package = current.Package with { ProductVersion = new string('x', 257) } } },
            "pending-without-nonce" => retained with { Phase = NodeUpdatePhase.Starting, Pending = pending },
            "nonce-without-pending" => retained with { Phase = NodeUpdatePhase.Starting, ActivationNonce = Guid.Parse("4add542d-1e4a-47eb-a812-0fb0071907ab") },
            "committed-pending" => retained with { Pending = pending, ActivationNonce = Guid.NewGuid() },
            "failed-without-time" => retained with { LastFailedReleaseId = new string('A', 64) },
            "time-without-failed" => retained with { LastFailedAtUtc = DateTimeOffset.Parse("2026-11-03T00:00:00Z") },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() => NodeUpdateState.Write(Updates, invalid));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Updates, "state.json")));
    }

    [Fact]
    public void Starting_preserves_last_committed_and_does_not_promote_pending_on_read()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var pending = Image("B");
        NodeUpdateState.Write(Updates, Committed(current));
        var starting = new NodeUpdateRecord(1, current, null, pending, Guid.Parse("4add542d-1e4a-47eb-a812-0fb0071907ab"), NodeUpdatePhase.Starting, null, null);
        NodeUpdateState.Write(Updates, starting);
        var result = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, result.Status);
        Assert.Equal(current.ContentId, result.Record!.Current.ContentId);
        Assert.Equal(pending.ContentId, result.Record.Pending!.ContentId);
        Assert.Equal(NodeUpdatePhase.Starting, result.Record.Phase);
        var backup = JsonSerializer.Deserialize<NodeUpdateRecord>(File.ReadAllText(Path.Combine(Updates, "committed.json")))!;
        Assert.Equal(NodeUpdatePhase.Committed, backup.Phase);
        Assert.Equal(current.ContentId, backup.Current.ContentId);
        Assert.Null(backup.Pending);
        File.WriteAllText(Path.Combine(Updates, "state.json"), "{");
        result = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, result.Status);
        Assert.Equal(current.ContentId, result.Record!.Current.ContentId);
        Assert.Null(result.Record.Pending);
    }

    [Fact]
    public void Named_version_directory_without_matching_bytes_is_corrupt()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        WriteRaw("state.json", JsonSerializer.Serialize(Committed(current)));
        File.AppendAllText(Path.Combine(Updates, "versions", current.ContentId, "payload.txt"), "changed");
        var result = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Corrupt, result.Status);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task Bootstrap_import_uses_only_supplied_installed_package_and_preserves_source_and_original_data()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var source = Package("installed-A");
        var unrelated = Package("unrelated-newer-B");
        File.SetLastWriteTimeUtc(Path.Combine(unrelated, "CoCern.Adapter.dll"), DateTime.UtcNow.AddDays(1));
        var sourceHash = AdapterDeployment.ContentHash(source);
        var original = File.ReadAllBytes(Path.Combine(NodeFolder, "original-state.bin"));
        var image = await NodePackage.ImportBootstrapAsync(Path.Combine(source, "CoCern.Adapter.dll"), Updates, default);
        Assert.NotNull(image);
        Assert.Null(image.ReleaseId);
        Assert.Equal(sourceHash, image.ContentId);
        Assert.Equal(sourceHash, AdapterDeployment.ContentHash(source));
        var imported = Path.Combine(Updates, "versions", image.ContentId);
        Assert.Equal("installed-A", File.ReadAllText(Path.Combine(imported, "payload.txt")));
        Assert.Equal(sourceHash, AdapterDeployment.ContentHash(imported));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "CoCern.Adapter.dll")))), image.AdapterDllSha256);
        AssertPrivate(Path.Combine(imported, "CoCern.Adapter.dll"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(NodeFolder, "original-state.bin")));
    }

    [Theory]
    [InlineData("wrong-name")]
    [InlineData("relative")]
    [InlineData("protocol")]
    [InlineData("missing-metadata")]
    public async Task Bootstrap_refuses_invalid_explicit_source_without_committing_any_image(string defect)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var source = Package("installed-A", protocol: defect == "protocol" ? 0 : 1);
        var dll = Path.Combine(source, "CoCern.Adapter.dll");
        if (defect == "wrong-name") { File.Copy(dll, Path.Combine(source, "foreign.dll")); dll = Path.Combine(source, "foreign.dll"); }
        if (defect == "relative") dll = Path.GetRelativePath(Environment.CurrentDirectory, dll);
        if (defect == "missing-metadata") File.Delete(Path.Combine(source, "CoCern.Adapter.runtimeconfig.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => NodePackage.ImportBootstrapAsync(dll, Updates, default));
        Assert.False(Directory.Exists(Path.Combine(Updates, "versions")) && Directory.EnumerateDirectories(Path.Combine(Updates, "versions")).Any());
        Assert.False(File.Exists(Path.Combine(Updates, "state.json")));
    }

    [Fact]
    public async Task Bootstrap_cancellation_propagates_without_selecting_or_copying_a_package()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var source = Package("installed-A");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodePackage.ImportBootstrapAsync(Path.Combine(source, "CoCern.Adapter.dll"), Updates, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(Updates, "versions")));
    }

    [Fact]
    public async Task Corrupt_journal_never_reseeds_from_bootstrap_or_changes_evidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var source = Package("installed-A");
        WriteRaw("state.json", "broken-primary-evidence");
        WriteRaw("committed.json", "broken-committed-evidence");
        var exit = await NodeSupervisor.RunAsync(NodeFolder, Path.Combine(source, "CoCern.Adapter.dll"), null, default);
        Assert.NotEqual(0, exit);
        Assert.Equal("broken-primary-evidence", File.ReadAllText(Path.Combine(Updates, "state.json")));
        Assert.Equal("broken-committed-evidence", File.ReadAllText(Path.Combine(Updates, "committed.json")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "versions")));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("case")]
    [InlineData("dot")]
    public async Task Supervisor_holds_one_owner_until_its_child_has_exited(string alias)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var source = Package("installed-A");
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var launches = 0;
        var first = NodeSupervisor.RunAsync(NodeFolder, Path.Combine(source, "CoCern.Adapter.dll"), host, _ =>
        {
            Interlocked.Increment(ref launches);
            started.TrySetResult();
            return new WaitingChild();
        }, TimeProvider.System, cancellation.Token);
        try
        {
            await Task.WhenAny(started.Task, first);
            Assert.True(started.Task.IsCompleted, "The retained child must start before checking ownership.");
            var duplicateFolder = alias switch { "case" => NodeFolder.ToUpperInvariant(), "dot" => Path.Combine(NodeFolder, "."), _ => NodeFolder };
            var duplicate = await NodeSupervisor.RunAsync(duplicateFolder, Path.Combine(source, "CoCern.Adapter.dll"), host, _ => { Interlocked.Increment(ref launches); return new ExitedChild(); }, TimeProvider.System, default);
            Assert.NotEqual(0, duplicate);
            Assert.Equal(1, launches);
            AssertPrivate(Path.Combine(Updates, "owner.lock"));
            using var singleton = new FileStream(Path.Combine(NodeFolder, "node.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        finally { cancellation.Cancel(); await first; }
        using var released = new FileStream(Path.Combine(Updates, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_start_launches_retained_current_and_keeps_rollback_evidence_until_authenticated_readiness(bool corruptBackup)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var pending = Image("B") with { ReleaseId = new string('B', 64) };
        var nonce = Guid.Parse("4add542d-1e4a-47eb-a812-0fb0071907ab");
        WriteRaw("state.json", JsonSerializer.Serialize(new NodeUpdateRecord(1, current, null, pending, nonce, NodeUpdatePhase.Starting, null, null)));
        WriteRaw("committed.json", corruptBackup ? "corrupt-backup" : JsonSerializer.Serialize(Committed(current)));
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        NodeLaunchSpec? launched = null;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, "C:/absent-explicit-bootstrap/CoCern.Adapter.dll", host, launch => { launched = launch; return new ExitedChild(); }, TimeProvider.System, default);
        Assert.NotNull(launched);
        Assert.Equal(1, exit);
        Assert.Equal(Path.Combine(Updates, "versions", current.ContentId, "CoCern.Adapter.dll"), launched.Arguments[0]);
        Assert.True(NodeWorkerStartupContext.TryParse(launched.Arguments[^1], out var startup));
        Assert.Equal(nonce, startup!.ActivationNonce);
        Assert.NotEqual(Guid.Empty, startup.LaunchId);
        Assert.Equal(launched.StartupContext!.LaunchId, startup.LaunchId);
        var recovered = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, recovered.Status);
        Assert.Equal(NodeUpdatePhase.Rollback, recovered.Record!.Phase);
        Assert.Equal(current.ContentId, recovered.Record.Current.ContentId);
        Assert.Equal(pending.ContentId, recovered.Record.Pending!.ContentId);
        Assert.Equal(nonce, recovered.Record.ActivationNonce);
        Assert.Equal(pending.ReleaseId, recovered.Record.LastFailedReleaseId);
        Assert.NotNull(recovered.Record.LastFailedAtUtc);
    }

    private sealed class WaitingChild : INodeChild
    {
        public bool HasExited => false;
        public int ProcessId => 12345;
        public async Task<int> WaitForExitAsync(CancellationToken ct) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Missing_journal_ignores_newer_unrecorded_version_and_imports_only_explicit_installed_bootstrap()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var installed = Package("installed-A");
        var abandoned = Image("unrecorded-B");
        Directory.SetLastWriteTimeUtc(Path.Combine(Updates, "versions", abandoned.ContentId), DateTime.UtcNow.AddDays(1));
        var expected = AdapterDeployment.ContentHash(installed);
        var host = new NodeHostCapabilities(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), [new("Microsoft.NETCore.App", "10.0.14")], "win-x64");
        NodeLaunchSpec? launched = null;
        var exit = await NodeSupervisor.RunAsync(NodeFolder, Path.Combine(installed, "CoCern.Adapter.dll"), host, launch => { launched = launch; return new ExitedChild(); }, TimeProvider.System, default);
        Assert.Equal(0, exit);
        Assert.NotNull(launched);
        Assert.Equal(Path.Combine(Updates, "versions", expected, "CoCern.Adapter.dll"), launched.Arguments[0]);
        Assert.NotEqual(abandoned.ContentId, NodeUpdateState.Read(Updates).Record!.Current.ContentId);
        Assert.True(Directory.Exists(Path.Combine(Updates, "versions", abandoned.ContentId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Journal_replace_failure_recovers_the_actual_durable_commit_boundary(bool failBackupAfterCommit)
    {
        if (!OperatingSystem.IsWindows()) return;
        PrepareRoot();
        var current = Image("A");
        var candidate = Image("B") with { ReleaseId = new string('B', 64) };
        NodeUpdateState.Write(Updates, Committed(current));
        var path = Path.Combine(Updates, failBackupAfterCommit ? "committed.json" : "state.json");
        var next = failBackupAfterCommit
            ? Committed(candidate) with { Previous = current }
            : new NodeUpdateRecord(1, current, null, candidate, Guid.Parse("4add542d-1e4a-47eb-a812-0fb0071907ab"), NodeUpdatePhase.Starting, null, null);
        using (var blockedReplace = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<InvalidDataException>(() => NodeUpdateState.Write(Updates, next));
        var recovered = NodeUpdateState.Read(Updates);
        Assert.Equal(NodeUpdateReadStatus.Valid, recovered.Status);
        Assert.Equal(NodeUpdatePhase.Committed, recovered.Record!.Phase);
        Assert.Equal(failBackupAfterCommit ? candidate.ContentId : current.ContentId, recovered.Record.Current.ContentId);
        Assert.Null(recovered.Record.Pending);
        var backup = JsonSerializer.Deserialize<NodeUpdateRecord>(File.ReadAllText(Path.Combine(Updates, "committed.json")))!;
        Assert.Equal(current.ContentId, backup.Current.ContentId);
        output.WriteLine($"journal-fault:writeThrew=True,primaryNewCommit={failBackupAfterCommit},rereadCurrentMatchesDurableBoundary=True");
    }

    private sealed class ExitedChild : INodeChild
    {
        public bool HasExited => true;
        public int ProcessId => 12346;
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Worker_entry_opens_only_explicit_isolated_node_and_releases_its_lock_on_configuration_refusal()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(fixture);
        var target = Path.Combine(fixture, "worker target");
        var sentinel = Path.Combine(fixture, "must-not-open-default");
        using (NodeState.Open(target)) { }
        var identityBefore = File.ReadAllBytes(Path.Combine(target, "node.json"));
        using var process = IsolatedAdapterProcess(sentinel, ["--node-worker", "--data-dir", target]);
        Assert.True(process.Start());
        var stdout = ReadBounded(process.StandardOutput);
        var error = ReadBounded(process.StandardError);
        process.StandardInput.Close();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            output.WriteLine($"worker-fixture:pid={process.Id},exited={process.HasExited},exit={process.ExitCode},sentinelExists={Directory.Exists(sentinel)}");
            Assert.False(Directory.Exists(sentinel));
            Assert.Equal(1, process.ExitCode);
            Assert.True(File.Exists(Path.Combine(target, "node.lock")), "The private worker entry must acquire its explicit isolated singleton before refusing a missing hub.");
            Assert.Empty(await stdout);
            Assert.Contains("no hub yet", await error, StringComparison.Ordinal);
            Assert.Equal(identityBefore, File.ReadAllBytes(Path.Combine(target, "node.json")));
            using var released = new FileStream(Path.Combine(target, "node.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); } }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("nonce")]
    public async Task Worker_entry_rejects_missing_or_nonabsolute_folder_and_malformed_nonce_before_state_access(string defect)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(fixture);
        var target = Path.Combine(fixture, "worker target");
        var sentinel = Path.Combine(fixture, "must-not-open-default");
        var arguments = defect switch
        {
            "missing" => new[] { "--node-worker" },
            "relative" => new[] { "--node-worker", "--data-dir", "relative-node" },
            _ => ["--node-worker", "--data-dir", target, "--activation-nonce", "not-a-guid"]
        };
        using var process = IsolatedAdapterProcess(sentinel, arguments);
        Assert.True(process.Start());
        var stdout = ReadBounded(process.StandardOutput);
        var error = ReadBounded(process.StandardError);
        process.StandardInput.Close();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            output.WriteLine($"worker-fixture:pid={process.Id},exited={process.HasExited},exit={process.ExitCode},sentinelExists={Directory.Exists(sentinel)}");
            Assert.False(Directory.Exists(target));
            Assert.False(Directory.Exists(sentinel));
            Assert.False(Directory.Exists(Path.Combine(fixture, "relative-node")));
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("Use --node-worker", await error, StringComparison.Ordinal);
            Assert.Empty(await stdout);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); } }
    }

    private Process IsolatedAdapterProcess(string sentinel, string[] arguments)
        => IsolatedProcess(typeof(NodePackage).Assembly.Location, sentinel, arguments);

    private Process IsolatedProcess(string executableDll, string sentinel, string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = fixture,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(executableDll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot");
        start.Environment["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR");
        start.Environment["TEMP"] = fixture;
        start.Environment["TMP"] = fixture;
        start.Environment["USERPROFILE"] = fixture;
        start.Environment["HOME"] = fixture;
        start.Environment["APPDATA"] = fixture;
        start.Environment["LOCALAPPDATA"] = fixture;
        start.Environment["PATH"] = Path.GetDirectoryName(start.FileName);
        start.Environment["COCERN_URL"] = "fixture-invalid-origin";
        start.Environment["COCERN_TOKEN"] = "fixture-only";
        start.Environment["COCERN_NODE_DIR"] = sentinel;
        return new Process { StartInfo = start };
    }

    [Fact]
    public async Task Native_child_preserves_argument_boundaries_through_actual_windows_process_creation()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(fixture);
        var helper = LifetimeFixture();
        var received = Path.Combine(helper.Folder, "received.json");
        string[] arguments = ["", "white space", "embedded\"quote", "trailing\\", "two\\\\\"q", "tab\tvalue", "雪"];
        await using var child = NodeSupervisor.StartOwnedChild(new(helper.Dotnet, [helper.Dll, "args", received, .. arguments]));
        if (child is null) output.WriteLine("native-fixture:launcherUnavailable=True,noWorkerCreated=True");
        Assert.NotNull(child);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await child.WaitForExitAsync(timeout.Token));
        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(File.ReadAllText(received)));
        output.WriteLine($"native-fixture:argumentRoundtrip=True,pid={child.ProcessId},exited=True");
    }

    [Fact]
    public async Task Native_job_close_stops_exact_worker_and_releases_its_original_singleton()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(fixture);
        var helper = LifetimeFixture();
        var node = Path.Combine(helper.Folder, "node");
        Directory.CreateDirectory(node);
        var ready = Path.Combine(helper.Folder, "ready.json");
        var child = NodeSupervisor.StartOwnedChild(new(helper.Dotnet, [helper.Dll, "lock", node, ready]));
        if (child is null) output.WriteLine("native-fixture:launcherUnavailable=True,noWorkerCreated=True");
        Assert.NotNull(child);
        try
        {
            await AwaitFile(ready);
            using var owned = Process.GetProcessById(child.ProcessId);
            _ = owned.Handle;
            Assert.Throws<IOException>(() => { using var forbidden = new FileStream(Path.Combine(node, "node.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            await child.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await owned.WaitForExitAsync(timeout.Token);
            Assert.True(owned.HasExited);
            using var released = new FileStream(Path.Combine(node, "node.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            output.WriteLine($"native-fixture:jobClosed=True,pid={child.ProcessId},exited=True,nodeLockReleased=True");
        }
        finally { await child.DisposeAsync(); }
    }

    [Fact]
    public async Task Native_children_cannot_hold_the_same_original_node_singleton()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(fixture);
        var helper = LifetimeFixture();
        var node = Path.Combine(helper.Folder, "node");
        Directory.CreateDirectory(node);
        var firstReady = Path.Combine(helper.Folder, "first.json");
        var secondReady = Path.Combine(helper.Folder, "second.json");
        var first = NodeSupervisor.StartOwnedChild(new(helper.Dotnet, [helper.Dll, "lock", node, firstReady]));
        if (first is null) output.WriteLine("native-fixture:launcherUnavailable=True,noWorkerCreated=True");
        Assert.NotNull(first);
        try
        {
            await AwaitFile(firstReady);
            await using var second = NodeSupervisor.StartOwnedChild(new(helper.Dotnet, [helper.Dll, "lock", node, secondReady]));
            Assert.NotNull(second);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(7, await second.WaitForExitAsync(timeout.Token));
            Assert.Equal("busy", File.ReadAllText(secondReady));
            using var live = Process.GetProcessById(first.ProcessId);
            Assert.False(live.HasExited);
            output.WriteLine($"native-fixture:firstPid={first.ProcessId},secondPid={second.ProcessId},secondExited=True,secondLockRefused=True");
        }
        finally { await first.DisposeAsync(); output.WriteLine($"native-fixture:firstPid={first.ProcessId},firstCleanupExited=True"); }
    }

    [Fact]
    public async Task Native_abrupt_supervisor_death_ends_worker_releases_lock_and_preserves_separate_descendant()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(fixture);
        var helper = LifetimeFixture();
        var node = Path.Combine(helper.Folder, "node");
        Directory.CreateDirectory(node);
        var workerReady = Path.Combine(helper.Folder, "worker.json");
        var parentReady = Path.Combine(helper.Folder, "parent.json");
        var descendantReady = Path.Combine(helper.Folder, "descendant.txt");
        using var parent = IsolatedProcess(helper.Dll, Path.Combine(helper.Folder, "unused-default"), ["parent", typeof(NodePackage).Assembly.Location, helper.Dotnet, helper.Dll, node, workerReady, parentReady, descendantReady]);
        // A deliberately killed process must not make deletion of this test's separate temp profile its CWD barrier.
        parent.StartInfo.WorkingDirectory = helper.Folder;
        Process? worker = null;
        Process? descendant = null;
        Assert.True(parent.Start());
        output.WriteLine($"native-fixture:parentPid={parent.Id},parentStarted=True");
        parent.StandardInput.Close();
        var stdout = ReadBounded(parent.StandardOutput);
        var stderr = ReadBounded(parent.StandardError);
        try
        {
            await AwaitFile(parentReady);
            using var status = JsonDocument.Parse(File.ReadAllText(parentReady));
            Assert.Equal("owned", status.RootElement.GetProperty("status").GetString());
            worker = Process.GetProcessById(status.RootElement.GetProperty("pid").GetInt32());
            _ = worker.Handle;
            await AwaitFile(workerReady);
            await AwaitFile(descendantReady);
            descendant = Process.GetProcessById(int.Parse(File.ReadAllText(descendantReady)));
            _ = descendant.Handle;
            Assert.False(worker.HasExited);
            Assert.False(descendant.HasExited);
            parent.Kill(entireProcessTree: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await parent.WaitForExitAsync(timeout.Token);
            await worker.WaitForExitAsync(timeout.Token);
            Assert.True(worker.HasExited);
            Assert.False(descendant.HasExited);
            using var released = new FileStream(Path.Combine(node, "node.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            output.WriteLine($"native-fixture:parentPid={parent.Id},parentExited=True,workerPid={worker.Id},workerExited=True,nodeLockReleased=True,descendantPid={descendant.Id},descendantSurvived=True");
        }
        finally
        {
            await StopExact(parent);
            output.WriteLine($"native-fixture:parentPid={parent.Id},parentCleanupExited={parent.HasExited},workerCreated={worker is not null},descendantObserved={descendant is not null}");
            if (worker is not null) { await StopExact(worker); output.WriteLine($"native-fixture:workerPid={worker.Id},workerCleanupExited={worker.HasExited}"); worker.Dispose(); }
            if (descendant is not null) { await StopExact(descendant); output.WriteLine($"native-fixture:descendantPid={descendant.Id},descendantCleanupExited={descendant.HasExited}"); descendant.Dispose(); }
            Assert.Empty(await stdout);
            Assert.Empty(await stderr);
        }
    }

    private sealed record LifetimeFixtureFiles(string Folder, string Dll, string Dotnet);

    private LifetimeFixtureFiles LifetimeFixture()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "node-lifetime-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        const string source = """
            using System;
            using System.IO;
            using System.Linq;
            using System.Diagnostics;
            using System.Reflection;
            using System.Text.Json;
            using System.Threading;
            public static class LifetimeFixture
            {
                private static void Write(string path, string content) { File.WriteAllText(path + ".tmp", content); File.Move(path + ".tmp", path); }
                public static int Main(string[] args)
                {
                    if (args[0] == "args") { Write(args[1], JsonSerializer.Serialize(args.Skip(2).ToArray())); return 0; }
                    if (args[0] == "descendant") { Write(args[1], Environment.ProcessId.ToString()); Thread.Sleep(45000); return 0; }
                    if (args[0] == "parent")
                    {
                        var assembly = Assembly.LoadFrom(args[1]);
                        var spec = Activator.CreateInstance(assembly.GetType("CoCern.Adapter.Node.NodeLaunchSpec")!, new object[] { args[2], new[] { args[3], "lock", args[4], args[5], args[2], args[3], args[7] } });
                        var start = assembly.GetType("CoCern.Adapter.Node.NodeSupervisor")!.GetMethod("StartOwnedChild", BindingFlags.Static | BindingFlags.NonPublic)!;
                        var child = start.Invoke(null, new[] { spec });
                        if (child is null) { Write(args[6], JsonSerializer.Serialize(new { status = "unavailable", pid = 0 })); return 0; }
                        var pid = (int)child.GetType().GetProperty("ProcessId")!.GetValue(child)!;
                        Write(args[6], JsonSerializer.Serialize(new { status = "owned", pid }));
                        Thread.Sleep(45000);
                        ((IAsyncDisposable)child).DisposeAsync().AsTask().GetAwaiter().GetResult();
                        return 0;
                    }
                    if (args[0] != "lock") return 9;
                    FileStream singleton;
                    try { singleton = new FileStream(Path.Combine(args[1], "node.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { Write(args[2], "busy"); return 7; }
                    using (singleton)
                    {
                        if (args.Length == 6)
                        {
                            var info = new ProcessStartInfo(args[3]) { UseShellExecute = false, CreateNoWindow = true };
                            foreach (var argument in new[] { args[4], "descendant", args[5] }) info.ArgumentList.Add(argument);
                            using var descendant = Process.Start(info);
                        }
                        Write(args[2], JsonSerializer.Serialize(new { status = "locked", pid = Environment.ProcessId }));
                        Thread.Sleep(45000);
                        return 0;
                    }
                }
            }
            """;
        var sourcePath = Path.Combine(folder, "LifetimeFixture.cs");
        var dll = Path.Combine(folder, "LifetimeFixture.dll");
        File.WriteAllText(sourcePath, source);
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create("CoCern.NodeLifetimeFixture", [CSharpSyntaxTree.ParseText(source)], trusted.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var result = compilation.Emit(dll);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        File.WriteAllText(Path.Combine(folder, "LifetimeFixture.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        output.WriteLine($"native-fixture:folder={folder},sourceSha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)))},dllSha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)))}");
        return new(folder, dll, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
    }

    private static async Task AwaitFile(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(20, timeout.Token);
    }

    private static async Task StopExact(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static async Task<string> ReadBounded(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            if (text.Length + count > 32768) throw new InvalidDataException("fixture-output-limit");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static NodeUpdateRecord Committed(NodeImage image) => new(1, image, null, null, null, NodeUpdatePhase.Committed, null, null);

    private void PrepareRoot()
    {
        Directory.CreateDirectory(NodeFolder);
        File.WriteAllBytes(Path.Combine(NodeFolder, "original-state.bin"), [3, 1, 4, 1, 5, 9]);
        PrivateDirectory(Updates);
    }

    private NodeImage Image(string payload)
    {
        var source = Package(payload);
        var metadata = NodePackageMetadataReader.Read(source);
        Assert.NotNull(metadata);
        var contentId = AdapterDeployment.ContentHash(source);
        var path = Path.Combine(Updates, "versions", contentId);
        PrivateDirectory(Path.GetDirectoryName(path)!);
        PrivateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
        return new(contentId, metadata, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "CoCern.Adapter.dll")))), null);
    }

    private string Package(string payload, int protocol = 1)
    {
        var path = Path.Combine(fixture, "installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var code = "using System.Reflection; [assembly: AssemblyInformationalVersion(\"7.8.9+installed\")] [assembly: AssemblyMetadata(\"CoCernNodeSupervisorProtocol\", \"" + protocol + "\")] public static class InertPackage { }";
        var compilation = CSharpCompilation.Create("CoCern.Adapter", [CSharpSyntaxTree.ParseText(code)], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(Path.Combine(path, "CoCern.Adapter.dll"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        File.WriteAllText(Path.Combine(path, "CoCern.Adapter.deps.json"), "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0\"}}");
        File.WriteAllText(Path.Combine(path, "CoCern.Adapter.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        File.WriteAllText(Path.Combine(path, "payload.txt"), payload);
        return path;
    }

    private void WriteRaw(string name, string json) => File.WriteAllText(Path.Combine(Updates, name), json);

    private static void PrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    private static void AssertPrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new FileInfo(path).GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow) Assert.Equal(user, rule.IdentityReference);
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(fixture);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("cocern-node-supervisor-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}

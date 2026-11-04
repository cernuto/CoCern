using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Diagnostics;
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
        Assert.Equal(0, exit);
        Assert.Equal(Path.Combine(Updates, "versions", current.ContentId, "CoCern.Adapter.dll"), launched.Arguments[0]);
        Assert.Contains(nonce.ToString("D"), launched.Arguments);
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

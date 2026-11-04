using System.Collections.Concurrent;
using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CoCern.Adapter;
using CoCern.Adapter.Development;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CoCern.Tests;

public sealed class DevelopmentLauncherTests
{
    [Fact]
    public async Task The_real_launcher_reconnects_to_a_changed_adapter_without_restarting_its_client()
    {
        var checkout = new DirectoryInfo(AppContext.BaseDirectory);
        while (checkout is not null && !File.Exists(Path.Combine(checkout.FullName, "CoCern.slnx"))) checkout = checkout.Parent;
        Assert.NotNull(checkout);
        var fixtureParent = Path.GetFullPath(Path.Combine(checkout.FullName, "artifacts", "n4d"));
        var root = Path.GetFullPath(Path.Combine(fixtureParent, Guid.NewGuid().ToString("N")[..12]));
        Assert.StartsWith(fixtureParent + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        try
        {
            var configuration = typeof(DevelopmentLauncherTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var build = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "CoCern.Adapter", configuration.ToLowerInvariant()));
            Assert.True(File.Exists(Path.Combine(build, "CoCern.Adapter.dll")), "This process test needs its own dependency-built adapter artifacts.");
            var source = Path.Combine(root, "source");
            foreach (var file in Directory.GetFiles(build, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(source, Path.GetRelativePath(build, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            var deployment = Path.Combine(root, "deployment");
            var first = AdapterDeployment.Publish(source, deployment);
            var logs = new ConcurrentQueue<string>();
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? (OperatingSystem.IsWindows() ? "C:/Program Files/dotnet/dotnet.exe" : "/usr/bin/dotnet");
            Assert.True(Path.IsPathFullyQualified(dotnet) && File.Exists(dotnet));
            await using var endpoint = new FailedHub();
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { AdapterDeployment.AdapterPath(deployment, first), "--dev-launch", deployment }) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PROCESSOR_ARCHITECTURE" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment["PATH"] = Path.GetDirectoryName(dotnet)!; start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
            var home = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
            start.Environment["HOME"] = home; start.Environment["USERPROFILE"] = home;
            foreach (var key in new[] { "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "DOTNET_CLI_HOME" })
                start.Environment[key] = Directory.CreateDirectory(Path.Combine(root, key.ToLowerInvariant())).FullName;
            start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["COCERN_URL"] = endpoint.Origin; start.Environment["COCERN_TOKEN"] = "development-process-test-not-a-production-token";
            using var process = Process.Start(start)!;
            var errors = ReadFixedLogs();
            var transport = new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            McpClient? client = null;
            try
            {
                client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
                var before = await client.ListToolsAsync(new ListToolsRequestParams(), timeout.Token);
                Assert.Contains(before.Tools, tool => tool.Name == "session_open");
                var initialCall = await client.CallToolAsync("worker_list", new Dictionary<string, object?> { ["repositoryId"] = "repo" }, cancellationToken: timeout.Token);
                Assert.True(initialCall.IsError);
                Assert.Contains("hub_unavailable", string.Join("", initialCall.Content.OfType<TextContentBlock>().Select(block => block.Text)));
                // The added file changes content identity without changing or replacing any running binary.
                File.WriteAllText(Path.Combine(source, "development-build-marker.txt"), "changed build");
                var changed = AdapterDeployment.Publish(source, deployment);
                Assert.NotEqual(first.Version, changed.Version);
                while (!logs.Any(line => line.Contains(changed.Generation, StringComparison.Ordinal))) await Task.Delay(25, timeout.Token);
                var after = await client.ListToolsAsync(new ListToolsRequestParams(), timeout.Token);
                Assert.Equal(before.Tools.Select(tool => tool.Name), after.Tools.Select(tool => tool.Name));
                var nextCall = await client.CallToolAsync("worker_list", new Dictionary<string, object?> { ["repositoryId"] = "repo" }, cancellationToken: timeout.Token);
                Assert.True(nextCall.IsError);
                Assert.Contains("hub_unavailable", string.Join("", nextCall.Content.OfType<TextContentBlock>().Select(block => block.Text)));
            }
            finally
            {
                try { if (client is not null) await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                finally
                {
                    try { process.StandardInput.Close(); } catch (ObjectDisposedException) { } catch (IOException) { }
                    if (!process.HasExited)
                    {
                        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch (TimeoutException)
                        {
                            // This test owns the complete synthetic launcher tree; it starts no model/provider/work process.
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        }
                    }
                    await errors.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            Assert.True(process.HasExited);
            // A completed SDK disposal closes the launcher and its private child, releasing every immutable copy.
            Directory.Delete(root, recursive: true);

            async Task ReadFixedLogs()
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    if (line.Length <= 200 && line.StartsWith("Development adapter connected: ", StringComparison.Ordinal)) logs.Enqueue(line);
            }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(fixtureParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its artifact folder.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailedHub : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task watching;
        internal string Origin { get; }
        internal FailedHub()
        {
            listener.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            watching = Watch();
        }
        private async Task Watch()
        {
            try { while (!stopping.IsCancellationRequested) { using var client = await listener.AcceptTcpClientAsync(stopping.Token); } }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stopping.Cancel(); listener.Stop(); await watching; stopping.Dispose(); }
    }
}

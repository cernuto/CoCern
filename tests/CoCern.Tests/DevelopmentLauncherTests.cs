using System.Collections.Concurrent;
using System.Reflection;
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
        var root = Path.Combine(Path.GetTempPath(), "cocern-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var checkout = new DirectoryInfo(AppContext.BaseDirectory);
            while (checkout is not null && !File.Exists(Path.Combine(checkout.FullName, "CoCern.slnx"))) checkout = checkout.Parent;
            Assert.NotNull(checkout);
            var configuration = typeof(DevelopmentLauncherTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var build = Path.Combine(checkout.FullName, "src", "CoCern.Adapter", "bin", configuration, "net10.0");
            Assert.True(File.Exists(Path.Combine(build, "CoCern.Adapter.dll")), "Build the solution through Visual Studio before running this process test.");
            var source = Path.Combine(root, "source");
            foreach (var file in Directory.GetFiles(build, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(source, Path.GetRelativePath(build, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            var deployment = Path.Combine(root, "deployment");
            var first = AdapterDeployment.Publish(source, deployment);
            var logs = new ConcurrentQueue<string>();
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var transport = new StdioClientTransport(new()
            {
                Command = dotnet, Arguments = [AdapterDeployment.AdapterPath(deployment, first), "--dev-launch", deployment],
                Name = "CoCern development test", ShutdownTimeout = TimeSpan.FromSeconds(5), StandardErrorLines = logs.Enqueue,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["COCERN_URL"] = "http://127.0.0.1:1/", ["COCERN_TOKEN"] = "development-process-test-not-a-production-token"
                }
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
            {
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
            // A completed SDK disposal closes the launcher and its private child, releasing every immutable copy.
            Directory.Delete(root, recursive: true);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

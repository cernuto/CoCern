using CoCern.Adapter.Development;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CoCern.Tests;

public sealed class DevelopmentConnectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-dev-connections-" + Guid.NewGuid().ToString("N"));
    private string Deployment => Path.Combine(root, "deployment");
    private AdapterManifest Publish()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        foreach (var file in new[] { "CoCern.Adapter.dll", "CoCern.Adapter.deps.json", "CoCern.Adapter.runtimeconfig.json" }) File.WriteAllText(Path.Combine(source, file), "fixture");
        return AdapterDeployment.Publish(source, Deployment);
    }

    [Fact]
    public async Task Each_debug_start_reconnects_even_when_the_adapter_content_is_unchanged()
    {
        var first = Publish();
        var children = new List<Backend>();
        await using var connections = new DevelopmentConnections(Deployment, (manifest, _) =>
        {
            var child = new Backend(manifest.Generation); children.Add(child); return Task.FromResult<IDevelopmentBackend>(child);
        });
        Assert.True(await connections.RefreshAsync());
        Assert.Equal(first.Generation, await connections.UseAsync((backend, _) => ValueTask.FromResult(backend.Instructions)));
        var second = Publish();
        Assert.Equal(first.Version, second.Version);
        Assert.True(await connections.RefreshAsync());
        Assert.Equal(second.Generation, await connections.UseAsync((backend, _) => ValueTask.FromResult(backend.Instructions)));
        Assert.True(children[0].Disposed);
        Assert.False(children[1].Disposed);
        Assert.False(await connections.RefreshAsync());
    }

    [Fact]
    public async Task A_call_that_refreshes_the_backend_keeps_the_metadata_change_for_the_watcher()
    {
        Publish();
        await using var connections = new DevelopmentConnections(Deployment, (manifest, _) => Task.FromResult<IDevelopmentBackend>(new Backend(manifest.Generation)));
        Assert.True(await connections.RefreshAsync());
        var next = Publish();

        Assert.Equal(next.Generation, await connections.UseAsync((backend, _) => ValueTask.FromResult(backend.Instructions)));

        // A request can see the new generation before the periodic watcher. The watcher must still notify the MCP client once.
        Assert.True(await connections.RefreshAsync());
        Assert.False(await connections.RefreshAsync());
    }

    [Fact]
    public async Task A_restart_drains_the_active_call_without_replaying_it()
    {
        Publish();
        var children = new List<Backend>();
        await using var connections = new DevelopmentConnections(Deployment, (manifest, _) =>
        {
            var child = new Backend(manifest.Generation); children.Add(child); return Task.FromResult<IDevelopmentBackend>(child);
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var request = connections.UseAsync(async (_, _) => { Interlocked.Increment(ref invocations); entered.SetResult(); return await finish.Task; }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        var restart = connections.RefreshAsync();
        Assert.False(restart.IsCompleted);
        Assert.False(children[0].Disposed);
        Assert.Single(children);
        finish.SetResult("original call completed");
        Assert.Equal("original call completed", await request);
        Assert.True(await restart);
        Assert.Equal(1, invocations);
        Assert.Equal(2, children.Count);
        Assert.True(children[0].Disposed);
    }

    [Fact]
    public async Task Independent_calls_can_run_concurrently()
    {
        Publish();
        await using var connections = new DevelopmentConnections(Deployment, (manifest, _) => Task.FromResult<IDevelopmentBackend>(new Backend(manifest.Generation)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = connections.UseAsync(async (_, _) => { entered.SetResult(); await finish.Task; return 1; }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = connections.UseAsync((_, _) => ValueTask.FromResult(2)).AsTask();
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        finish.SetResult();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task A_failed_replacement_keeps_the_previous_backend_usable()
    {
        var first = Publish();
        var child = new Backend(first.Generation);
        var refuse = false;
        await using var connections = new DevelopmentConnections(Deployment, (_, _) => refuse ? Task.FromException<IDevelopmentBackend>(new IOException("replacement failed")) : Task.FromResult<IDevelopmentBackend>(child));
        await connections.RefreshAsync();
        Publish(); refuse = true;
        Assert.False(await connections.RefreshAsync());
        Assert.False(child.Disposed);
        Assert.Equal(first.Generation, await connections.UseAsync((backend, _) => ValueTask.FromResult(backend.Instructions)));
    }

    [Fact]
    public async Task Disposing_the_launcher_disposes_its_backend()
    {
        var manifest = Publish();
        var child = new Backend(manifest.Generation);
        var connections = new DevelopmentConnections(Deployment, (_, _) => Task.FromResult<IDevelopmentBackend>(child));
        await connections.RefreshAsync();
        await connections.DisposeAsync();
        Assert.True(child.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connections.RefreshAsync());
    }

    [Fact]
    public async Task A_failed_call_is_not_replayed_and_the_next_call_reconnects_a_crashed_backend()
    {
        var manifest = Publish();
        var children = new List<Backend>();
        await using var connections = new DevelopmentConnections(Deployment, (_, _) =>
        {
            var child = new Backend(manifest.Generation); children.Add(child); return Task.FromResult<IDevelopmentBackend>(child);
        });
        var invocations = 0;
        await Assert.ThrowsAsync<IOException>(() => connections.UseAsync<int>((backend, _) =>
        {
            invocations++; ((Backend)backend).IsClosed = true; throw new IOException("child exited during call");
        }).AsTask());
        Assert.Equal(1, invocations);
        Assert.True(await connections.RefreshAsync());
        Assert.Equal(2, children.Count);
        Assert.True(children[0].Disposed);
        Assert.False(children[1].Disposed);
        Assert.Equal(manifest.Generation, await connections.UseAsync((backend, _) => ValueTask.FromResult(backend.Instructions)));
    }

    [Fact]
    public void Refreshed_tool_metadata_delivers_new_server_guidance_without_retaining_old_rules()
    {
        var original = new ListToolsResult
        {
            Tools = [new Tool { Name = "session_open", Description = "Open a session.", InputSchema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }) }]
        };
        var first = DevelopmentLauncher.AdvertiseGuidance(original, "First generation guidance.");
        var next = DevelopmentLauncher.AdvertiseGuidance(original, "Second generation guidance.");
        Assert.Contains("First generation guidance.", first.Tools[0].Description);
        Assert.Contains("Second generation guidance.", next.Tools[0].Description);
        Assert.DoesNotContain("First generation guidance.", next.Tools[0].Description);
        Assert.Equal("Open a session.", original.Tools[0].Description);
        Assert.Equal(original.Tools[0].InputSchema.GetRawText(), next.Tools[0].InputSchema.GetRawText());
    }

    private sealed class Backend(string identity) : IDevelopmentBackend
    {
        public bool IsClosed { get; set; }
        public string Instructions => identity;
        public bool Disposed { get; private set; }
        public ValueTask<ListToolsResult> ListToolsAsync(ListToolsRequestParams request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

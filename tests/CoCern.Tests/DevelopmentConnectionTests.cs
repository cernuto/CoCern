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

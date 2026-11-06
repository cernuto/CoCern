using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using CoCern.Adapter.Node;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CoCern.Adapter.Development;

/// <summary>A stable development endpoint whose private adapter MCP connection is replaced at debug startup.</summary>
internal static class DevelopmentLauncher
{
    internal static ListToolsResult AdvertiseGuidance(ListToolsResult result, string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions) || result.Tools.Count == 0) return result;
        // Initialization instructions cannot change on an existing upstream connection. Deliver current guidance with refreshed tool metadata instead.
        var copy = JsonSerializer.Deserialize<ListToolsResult>(JsonSerializer.Serialize(result))!;
        var entry = copy.Tools.FirstOrDefault(tool => tool.Name == "session_open") ?? copy.Tools[0];
        entry.Description = $"{entry.Description}\n\nCurrent CoCern server guidance:\n{instructions}";
        return copy;
    }
    internal static async Task<int> RunNativeAsync(string root, string dotnet, string[] context, CancellationToken cancellationToken = default)
    {
        if (!NativeRegistration.ValidClientArguments(context)) throw new AdapterConfigurationException(NativeRegistration.Refusal);
        try { return await RunCoreAsync(root, dotnet, cancellationToken, context.ToArray()); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or McpException or OperationCanceledException)
        { throw new AdapterConfigurationException(NativeRegistration.Refusal); }
    }

    public static Task<int> RunAsync(string root, string dotnet, CancellationToken cancellationToken = default) => RunCoreAsync(root, dotnet, cancellationToken);

    private static async Task<int> RunCoreAsync(string root, string dotnet, CancellationToken cancellationToken, string[]? nativeContext = null)
    {
        root = Path.GetFullPath(root);
        await using var connections = new DevelopmentConnections(root, async (manifest, token) =>
        {
            var adapter = AdapterDeployment.AdapterPath(root, manifest);
            // Pin and inspect the exact immutable deployment before running any code. A trusted
            // old image may ignore private arguments, so initialization alone is too late.
            using var image = nativeContext is null ? null : File.Open(adapter, FileMode.Open, FileAccess.Read, FileShare.Read);
            var privateVersion = image is null ? null : PrivateImageVersion(image);
            if (nativeContext is not null && NativeRegistration.FromClientArguments(nativeContext, privateVersion) is null)
                throw new IOException(NativeRegistration.Refusal);
            // The SDK's Windows StdioClientTransport inserts cmd.exe /c, which misparses
            // a spaced dotnet executable together with a spaced deployment path. Own exactly
            // this backend process and give the SDK its streams, preserving argument boundaries.
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(adapter);
            // This is untrusted launch context, not authentication. Every replacement backend
            // must perform its own fresh registration exchange before MCP initialization.
            if (nativeContext is not null)
            {
                foreach (var argument in nativeContext) start.ArgumentList.Add(argument);
                NativeRegistration.RestrictPrivateEnvironment(start);
            }
            if (Environment.GetEnvironmentVariable("COCERN_CONNECTION_DIAGNOSTICS_DIR") is { } diagnosticsDirectory)
                start.Environment["COCERN_CONNECTION_DIAGNOSTICS_DIR"] = diagnosticsDirectory;
            start.Environment["COCERN_DEVELOPMENT_GENERATION"] = manifest.Generation;
            var process = Process.Start(start) ?? throw new IOException("The development backend could not start.");
            var stopError = new CancellationTokenSource();
            var error = DrainErrorAsync(process.StandardError, stopError.Token);
            try
            {
                var transport = new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
                var client = await McpClient.CreateAsync(transport, cancellationToken: token);
                if (nativeContext is not null && (client.ServerInfo?.Name != NativeRegistration.PrivateServerName || client.ServerInfo?.Version != privateVersion))
                {
                    await client.DisposeAsync();
                    throw new IOException(NativeRegistration.Refusal);
                }
                return new Backend(client, process, stopError, error);
            }
            catch { await StopBackendAsync(process, stopError, error); throw; }
        }, Console.Error.WriteLine);
        using (var startupTrace = ConnectionTrace.Begin("unknown", "launcher"))
        {
            try { await connections.RefreshAsync(cancellationToken); }
            catch (OperationCanceledException) { startupTrace.Complete(false, "cancelled"); throw; }
            catch { startupTrace.Complete(false, "backend_unavailable"); throw; }
        }
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        McpServer? upstream = null;
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "CoCern development launcher", Version = "1.0" };
            options.ServerInstructions = nativeContext is null
                ? "CoCern development endpoint. Follow the current server guidance included in the session_open tool description; tool metadata refreshes when the development adapter changes."
                : connections.Instructions ?? throw new AdapterConfigurationException(NativeRegistration.Refusal);
            options.Capabilities = new() { Tools = new() { ListChanged = true } };
        }).WithStdioServerTransport()
            .WithListToolsHandler(async (request, token) =>
            {
                upstream = request.Server;
                using var trace = ConnectionTrace.Begin("unknown", "launcher");
                try { return await connections.UseAsync(async (backend, ct) => AdvertiseGuidance(await backend.ListToolsAsync(request.Params ?? new(), ct), backend.Instructions), token); }
                catch (OperationCanceledException) { trace.Complete(false, "cancelled"); throw; }
                catch { trace.Complete(false, "backend_unavailable"); throw; }
            })
            .WithCallToolHandler((request, token) =>
            {
                upstream = request.Server;
                return ForwardMeasuredAsync(connections, request.Params!, token);
            });
        using var host = builder.Build();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        var watching = WatchAsync(connections, () => upstream, stopping.Token);
        try { await host.RunAsync(cancellationToken); }
        finally
        {
            await stopping.CancelAsync();
            await watching;
        }
        return 0;
    }

    private static async ValueTask<CallToolResult> ForwardMeasuredAsync(DevelopmentConnections connections, CallToolRequestParams request, CancellationToken token)
    {
        using var trace = ConnectionTrace.Begin(request.Name, "launcher");
        try
        {
            var result = await connections.UseAsync((backend, ct) => backend.CallToolAsync(new CallToolRequestParams
            { Name = request.Name, Arguments = request.Arguments, Meta = ConnectionTrace.Metadata(request.Meta) }, ct), token);
            trace.Complete(result.IsError != true, result.IsError == true ? "tool_error" : null);
            return result;
        }
        catch (OperationCanceledException) { trace.Complete(false, "cancelled"); throw; }
        catch { trace.Complete(false, "backend_unavailable"); throw; }
    }

    private static string PrivateImageVersion(Stream image)
    {
        if (image.Length is <= 0 or > 64 * 1024 * 1024) throw new IOException(NativeRegistration.Refusal);
        try
        {
            using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) throw new IOException(NativeRegistration.Refusal);
            var metadata = pe.GetMetadataReader();
            var found = false;
            foreach (var attributeHandle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(attributeHandle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
                if (metadata.GetString(type.Namespace) != "System.Reflection" || metadata.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
                var value = metadata.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1 || value.ReadSerializedString() != NativeRegistration.ProtocolMetadata) continue;
                if (found || value.ReadSerializedString() != NativeRegistration.ProtocolVersion || value.ReadUInt16() != 0 || value.RemainingBytes != 0)
                    throw new IOException(NativeRegistration.Refusal);
                found = true;
            }
            var version = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            if (!found || version == Guid.Empty) throw new IOException(NativeRegistration.Refusal);
            return version.ToString("D");
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException or ArgumentException)
        { throw new IOException(NativeRegistration.Refusal); }
    }

    private static async Task WatchAsync(DevelopmentConnections connections, Func<McpServer?> upstream, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                if (await connections.RefreshAsync(cancellationToken) && upstream() is { } server)
                {
                    try { await server.SendNotificationAsync("notifications/tools/list_changed", cancellationToken); }
                    catch (Exception error) when (error is InvalidOperationException or IOException) { }
                }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task DrainErrorAsync(TextReader reader, CancellationToken ct)
    {
        // Diagnostics may contain handles. Discard bounded chunks without retaining lines or payloads.
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer.AsMemory(), ct) != 0) { } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task StopBackendAsync(Process process, CancellationTokenSource stopError, Task error)
    {
        try
        {
            try { process.StandardInput.Close(); } catch (ObjectDisposedException) { } catch (IOException) { }
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException)
                {
                    // Native work descendants have independent lifetimes. Never kill their tree.
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
        finally
        {
            stopError.Cancel(); process.StandardError.Dispose();
            try { await error.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { stopError.Dispose(); process.Dispose(); }
        }
    }

    private sealed class Backend(McpClient client, Process process, CancellationTokenSource stopError, Task error) : IDevelopmentBackend
    {
        private int disposed;
        public bool IsClosed => client.Completion.IsCompleted;
        public string? Instructions => client.ServerInstructions;
        public ValueTask<ListToolsResult> ListToolsAsync(ListToolsRequestParams request, CancellationToken cancellationToken) => client.ListToolsAsync(request, cancellationToken);
        public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken) => client.CallToolAsync(request, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { await StopBackendAsync(process, stopError, error); }
        }
    }
}

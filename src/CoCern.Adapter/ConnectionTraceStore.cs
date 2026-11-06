using System.Text.Json;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter;

/// <summary>A bounded, optional diagnostic sink. Construction and reads never create files.</summary>
public sealed class ConnectionTraceStore(string? directory = null, bool persistAutomatically = true) : IDisposable
{
    public const int MaxEvents = 256;
    private const int MaxFiles = 16;
    private const int MaxFileBytes = 262144;
    private readonly object gate = new();
    private readonly object ioGate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Queue<ConnectionDiagnosticEvent> events = new();
    private int flushing;
    private int revision;
    private int persistedRevision;
    private bool disposed;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoCern", "diagnostics");
    public string? DirectoryPath => directory;

    public void Record(ConnectionDiagnosticEvent item)
    {
        if (Volatile.Read(ref disposed)) return;
        if (!Valid(item)) return;
        lock (gate)
        {
            events.Enqueue(item);
            while (events.Count > MaxEvents) events.Dequeue();
            revision++;
        }
        QueueFlush();
    }

    private void QueueFlush()
    {
        if (directory is null || !persistAutomatically || Interlocked.Exchange(ref flushing, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            var success = false;
            try { await Task.Delay(200, stopping.Token); Flush(); success = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or OperationCanceledException) { }
            finally
            {
                Interlocked.Exchange(ref flushing, 0);
                if (success && !Volatile.Read(ref disposed) && Volatile.Read(ref revision) != Volatile.Read(ref persistedRevision)) QueueFlush();
            }
        });
    }

    public ConnectionDiagnosticsSnapshot Read()
    {
        var collected = new List<ConnectionDiagnosticEvent>();
        lock (gate) collected.AddRange(events);
        if (directory is not null)
        {
            try
            {
                NoLinks(directory);
                if (Directory.Exists(directory))
                    foreach (var file in new DirectoryInfo(directory).EnumerateFiles("connection-*.json").OrderByDescending(x => x.LastWriteTimeUtc).Take(MaxFiles))
                    {
                        if (file.Length > MaxFileBytes || !NodeState.IsPrivateFile(file.FullName)) continue;
                        using var data = JsonDocument.Parse(File.ReadAllBytes(file.FullName), new JsonDocumentOptions { MaxDepth = 8 });
                        if (data.RootElement.ValueKind != JsonValueKind.Array) continue;
                        foreach (var row in data.RootElement.EnumerateArray().Take(MaxEvents))
                            if (row.Deserialize<ConnectionDiagnosticEvent>() is { } item && Valid(item)) collected.Add(item);
                    }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException) { }
        }
        return new() { Events = collected.Distinct().OrderByDescending(x => x.StartedAt).Take(MaxEvents).ToArray() };
    }

    /// <summary>Explicit flush is for orderly shutdown and isolated fixtures, never the request path.</summary>
    public void Flush()
    {
        lock (ioGate) FlushCore();
    }
    private void FlushCore()
    {
        if (directory is null || disposed) return;
        ConnectionDiagnosticEvent[] copy;
        int selectedRevision;
        lock (gate) { copy = events.ToArray(); selectedRevision = revision; }
        if (copy.Length == 0) return;
        NoLinks(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "connection-" + Environment.ProcessId + ".json");
        if (File.Exists(path) && !NodeState.IsPrivateFile(path)) throw new IOException("diagnostics_file_unavailable");
        NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(copy));
        Volatile.Write(ref persistedRevision, selectedRevision);
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("connection-*.json").OrderByDescending(x => x.LastWriteTimeUtc).Skip(MaxFiles))
            if (NodeState.IsPrivateFile(file.FullName)) { try { File.Delete(file.FullName); } catch (IOException) { } }
    }

    public void Dispose()
    {
        stopping.Cancel();
        lock (ioGate)
        {
            if (disposed) return;
            try { FlushCore(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
            disposed = true;
        }
    }

    public static bool ValidId(string? value) => value is { Length: 35 } && value.StartsWith("cc-", StringComparison.Ordinal) &&
        Guid.TryParseExact(value[3..], "N", out _);

    public static bool Valid(ConnectionDiagnosticEvent? item) =>
        item is not null && ValidId(item.CorrelationId) && item.Tool is { Length: > 0 and <= 64 } &&
        (item.Tool == "unknown" || RelayToolCatalog.TryGet(item.Tool, out _)) &&
        item.Source is "launcher" or "adapter" or "hub" &&
        item.Stage is "queue" or "drain" or "backend_start" or "native_registration" or "http_headers" or "http_body" or "hub_to_headers" or "hub_execution" or "transport_total" or "total" &&
        item.Outcome is "ok" or "error" or "cancelled" &&
        item.ErrorCode is null or "hub_timeout" or "hub_unavailable" or "hub_identity_changed" or "unauthorized" or "forbidden" or "rate_limited" or "hub_error" or "redirect_rejected" or "request_too_large" or "response_too_large" or "invalid_arguments" or "invalid_hub_reply" or "cancelled" or "backend_unavailable" or "native_registration_unverified" or "tool_error" &&
        double.IsFinite(item.DurationMs) && item.DurationMs is >= 0 and <= 300000 &&
        item.StartedAt >= DateTimeOffset.UtcNow.AddDays(-1) && item.StartedAt <= DateTimeOffset.UtcNow.AddMinutes(1);

    private static void NoLinks(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("diagnostics_path_unavailable");
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(folder)!).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new IOException("diagnostics_path_unavailable");
        for (DirectoryInfo? item = new(folder); item is not null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("diagnostics_path_unavailable");
    }
}

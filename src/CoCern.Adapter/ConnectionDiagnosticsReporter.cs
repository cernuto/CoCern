using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter;

/// <summary>Best-effort bounded reporting to the already configured hub, never awaited by a tool call.</summary>
internal sealed class ConnectionDiagnosticsReporter(HttpClient client, AdapterOptions options)
{
    private readonly Channel<ConnectionDiagnosticEvent[]> queue = Channel.CreateBounded<ConnectionDiagnosticEvent[]>(new BoundedChannelOptions(8)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private int started;
    private ConnectionDiagnosticEvent[] retained = [];
    internal void Report(IEnumerable<ConnectionDiagnosticEvent> events)
    {
        if (!ConnectionTrace.ReportingEnabled) return;
        var rows = events.Where(ConnectionTraceStore.Valid).Distinct().TakeLast(24).ToArray();
        if (rows.Length == 0) return;
        queue.Writer.TryWrite(rows);
        if (Interlocked.Exchange(ref started, 1) == 0) _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (queue.Reader.TryRead(out var batch))
            {
                retained = retained.Concat(batch).Where(ConnectionTraceStore.Valid).Distinct().TakeLast(24).ToArray();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "api/connection-diagnostics"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
                    request.Content = new StringContent(JsonSerializer.Serialize(new ConnectionDiagnosticsBatch { Events = retained }), Encoding.UTF8, "application/json");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (response.IsSuccessStatusCode) retained = [];
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        finally
        {
            Interlocked.Exchange(ref started, 0);
            if (queue.Reader.TryPeek(out _) && Interlocked.Exchange(ref started, 1) == 0) _ = Task.Run(RunAsync);
        }
    }
}

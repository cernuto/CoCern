using System.Text.Json.Nodes;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host;

/// <summary>
/// Every ten minutes, closes sessions idle longer than <c>Relay:Sessions:IdleHours</c> in every repository, so the session directory
/// rarely fills. A failed sweep is logged and tried again on the next run; it never stops the hub.
/// </summary>
internal sealed class SessionSweepService(Orleans.IGrainFactory grains, SessionCleanupOptions options, ILogger<SessionSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    // The sweep spans every repository: an administrator hub caller passes the directory's repository check for any nonempty scope.
    private static readonly RelayCaller Sweeper = new("relay-sweep", true, [], true);
    private const string HubScope = "*";

    /// <summary>One sweep: sends <see cref="Operations.SweepSessions"/> to the control directory and returns its reply.</summary>
    public Task<RelayReply> SweepAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new JsonObject { ["repositoryId"] = HubScope, ["idleHours"] = options.IdleHours };
        return grains.GetGrain<IControlGrain>(AggregateKinds.ControlKey).ExecuteAsync(Sweeper, Operations.SweepSessions, args.ToJsonString());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = FirstDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            delay = Interval;
            try
            {
                var reply = await SweepAsync(stoppingToken);
                if (!reply.Ok) logger.LogWarning("Session sweep refused: {Code}.", reply.Code);
                else if (JsonNode.Parse(reply.DataJson) is JsonObject data && JsonFields.Number(data, "closed") is var closed and > 0)
                    logger.LogInformation("Session sweep closed {Closed} sessions idle over {IdleHours} hours; kept {Kept} that answer for unresolved actions.", closed, options.IdleHours, JsonFields.Number(data, "kept"));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Session sweep failed; trying again in {Minutes} minutes.", Interval.TotalMinutes); }
        }
    }
}

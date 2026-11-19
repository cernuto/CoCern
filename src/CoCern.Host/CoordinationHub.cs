using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host;

[Authorize]
public sealed class CoordinationHub(IRelayApplication application, CoordinationSubscriptions subscriptions, IMachineDirectory machines) : Hub
{
    internal const string DispatchGroup = "dispatch";

    public override async Task OnConnectedAsync()
    {
        var machine = RelayAuthentication.Machine(Context.User!);
        subscriptions.Connect(Context, machine?.MachineId);
        try
        {
            if (machine is not null)
            {
                // Cover a revocation between HTTP authentication and registering this connection.
                var header = Context.GetHttpContext()?.Request.Headers.Authorization.ToString();
                if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                    (await machines.ValidateAsync(header[7..], Context.ConnectionAborted))?.MachineId != machine.MachineId)
                    throw new HubException("Machine access denied.");
            }
            await base.OnConnectedAsync();
        }
        catch
        {
            subscriptions.Disconnect(Context.ConnectionId);
            Context.Abort();
            throw;
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    [HubMethodName(HubEvents.WatchSession)]
    public async Task WatchSession(string sessionId, string sessionToken, string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(repositoryId) || sessionId.Length > RelayLimits.MaxIdentifierLength || sessionToken.Length > RelayLimits.MaxIdentifierLength || repositoryId.Length > RelayLimits.MaxIdentifierLength) throw new HubException("Session access denied.");
        var caller = RelayAuthentication.Caller(Context.User!);
        // Register before checking the handle so a concurrent committed session closure also aborts a pending join.
        var added = subscriptions.TrackSession(Context.ConnectionId, sessionId);
        try
        {
            var reply = await application.ExecuteAsync(caller, RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId, sessionId, sessionToken }), Context.ConnectionAborted);
            if (!reply.Ok) throw new HubException("Session access denied.");
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(sessionId), Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, DispatchGroup, Context.ConnectionAborted);
        }
        catch
        {
            if (added) subscriptions.ForgetSession(Context.ConnectionId, sessionId);
            throw;
        }
    }

    /// <summary>Subscribes this connection to hints for its own machine. Only a connection authenticated by a machine credential may call it, and only for that machine.</summary>
    [HubMethodName(HubEvents.WatchMachine)]
    public async Task WatchMachine()
    {
        var machine = RelayAuthentication.Machine(Context.User!) ?? throw new HubException("Machine access denied.");
        await Groups.AddToGroupAsync(Context.ConnectionId, MachineGroup(machine.MachineId), Context.ConnectionAborted);
    }

    internal static string MachineGroup(string machineId) => "machine:" + machineId;

    internal static string Group(string sessionId) => "session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
}

internal sealed class CoordinationNotifier(IHubContext<CoordinationHub> hub, Dashboard.DashboardTrafficBuffer traffic,
    Dashboard.DashboardBoardNotifier boards, CoordinationSubscriptions subscriptions) : ICoordinationNotifier
{
    public Task BoardChangedAsync(string repositoryId, string boardId, long revision, CancellationToken cancellationToken) =>
        boards.BoardChangedAsync(repositoryId, boardId, revision, cancellationToken);

    public Task DispatchChangedAsync(CancellationToken cancellationToken) =>
        hub.Clients.Group(CoordinationHub.DispatchGroup).SendAsync(HubEvents.CoordinationChanged, new { kind = "control" }, cancellationToken);

    public Task SessionClosedAsync(string sessionId, CancellationToken cancellationToken)
    {
        subscriptions.CloseSession(sessionId);
        return Task.CompletedTask;
    }

    public async Task NotifyAsync(string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        await hub.Clients.Group(CoordinationHub.Group(sessionId)).SendAsync(HubEvents.CoordinationChanged, new { sessionId, eventId }, cancellationToken);
        traffic.Push(sessionId);
    }
}

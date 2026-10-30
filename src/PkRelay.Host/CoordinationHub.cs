using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PkRelay.Core;
using PkRelay.Domain;

namespace PkRelay.Host;

[Authorize]
public sealed class CoordinationHub(IRelayApplication application) : Hub
{
    public async Task WatchSession(string sessionId, string sessionToken, string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(repositoryId) || sessionId.Length > 256 || sessionToken.Length > 256 || repositoryId.Length > 256) throw new HubException("Session access denied.");
        var caller = RelayAuthentication.Caller(Context.User!);
        var reply = await application.ExecuteAsync(caller, "get_status", JsonSerializer.SerializeToElement(new { repositoryId, sessionId, sessionToken }), Context.ConnectionAborted);
        if (!reply.Ok) throw new HubException("Session access denied.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(sessionId), Context.ConnectionAborted);
    }

    /// <summary>Subscribes this connection to hints for its own machine. Only a connection authenticated by a machine credential may call it, and only for that machine.</summary>
    public async Task WatchMachine()
    {
        var machine = RelayAuthentication.Machine(Context.User!) ?? throw new HubException("Machine access denied.");
        await Groups.AddToGroupAsync(Context.ConnectionId, MachineGroup(machine.MachineId), Context.ConnectionAborted);
    }

    internal static string MachineGroup(string machineId) => "machine:" + machineId;

    internal static string Group(string sessionId) => "session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
}

internal sealed class CoordinationNotifier(IHubContext<CoordinationHub> hub) : ICoordinationNotifier
{
    public Task NotifyAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => hub.Clients.Group(CoordinationHub.Group(sessionId)).SendAsync("coordination.changed", new { sessionId, eventId }, cancellationToken);
}

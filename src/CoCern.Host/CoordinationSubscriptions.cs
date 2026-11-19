using CoCern.Core;
using Microsoft.AspNetCore.SignalR;

namespace CoCern.Host;

/// <summary>Ends live hint subscriptions when their authenticated machine or watched session closes.</summary>
public sealed class CoordinationSubscriptions : IMachineRevocationListener
{
    private sealed record Connection(HubCallerContext Context, string? MachineId, HashSet<string> Sessions);
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly object gate = new();

    internal void Connect(HubCallerContext context, string? machineId)
    {
        lock (gate) connections.Add(context.ConnectionId, new(context, machineId, new(StringComparer.Ordinal)));
    }

    internal void Disconnect(string connectionId)
    {
        lock (gate) connections.Remove(connectionId);
    }

    internal bool TrackSession(string connectionId, string sessionId)
    {
        lock (gate) return connections.TryGetValue(connectionId, out var connection)
            ? connection.Sessions.Add(sessionId) : throw new HubException("Connection closed.");
    }

    internal void ForgetSession(string connectionId, string sessionId)
    {
        lock (gate)
            if (connections.TryGetValue(connectionId, out var connection)) connection.Sessions.Remove(sessionId);
    }

    internal void CloseSession(string sessionId) => AbortMatching(connection => connection.Sessions.Contains(sessionId));

    void IMachineRevocationListener.MachineRevoked(string machineId) => AbortMatching(connection => connection.MachineId == machineId);

    private void AbortMatching(Func<Connection, bool> matches)
    {
        HubCallerContext[] affected;
        lock (gate) affected = connections.Values.Where(matches).Select(connection => connection.Context).ToArray();
        foreach (var context in affected) context.Abort();
    }
}

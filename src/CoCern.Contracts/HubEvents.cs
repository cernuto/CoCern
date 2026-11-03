namespace CoCern.Contracts;

/// <summary>The SignalR hub that adapters and nodes listen on for hints: its path, the methods they call and the events it sends.</summary>
public static class HubEvents
{
    /// <summary>The hub's path, relative to the hub's root address.</summary>
    public const string Path = "hubs/coordination";
    /// <summary>Called by a chat's adapter with its session handle to hear about its own session.</summary>
    public const string WatchSession = nameof(WatchSession);
    /// <summary>Called by a joined PC's node, authenticated by its machine credential, to hear about its own chats.</summary>
    public const string WatchMachine = nameof(WatchMachine);
    /// <summary>Sent to a watched session when its inbox or attention changes; the inbox stays the source of truth.</summary>
    public const string CoordinationChanged = "coordination.changed";
    /// <summary>Sent to a watched machine when a chat of it should be looked at again.</summary>
    public const string DiscoveryChanged = "discovery.changed";
}

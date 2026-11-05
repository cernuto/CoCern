namespace CoCern.Adapter;

/// <summary>
/// Who made a hub call, as the adapter reports it in <c>callOrigin</c>, so measurement can tell a model's calls from background ones. Telemetry
/// only: it grants nothing and changes no call. The hub keeps exactly these values and counts anything else as unknown; <c>dashboard</c> is the host's own.
/// </summary>
internal static class CallOrigins
{
    internal const string Field = "callOrigin";
    /// <summary>A model's tool call through <see cref="CoordinationTools"/>, including the polls of a call that waits.</summary>
    internal const string Mcp = "mcp";
    /// <summary>The Claude Code Stop hook, waiting for the chat after its turn ends.</summary>
    internal const string ClaudeStop = "claude-stop";
    /// <summary>The inbox waiter a Claude chat starts as a background command.</summary>
    internal const string ClaudeWaiter = "claude-waiter";
    /// <summary>The wake watcher and its reconciler, in whichever process they run.</summary>
    internal const string WakeWatcher = "wake-watcher";
    /// <summary>The rest of node mode: the prompt hook and a native binding's own checks.</summary>
    internal const string Node = "node";
}

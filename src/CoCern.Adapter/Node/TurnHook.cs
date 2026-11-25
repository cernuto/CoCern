using System.Text.Json;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

internal static class TurnHook
{
    public static async Task<int> RunAsync(TextReader input, string nodeFolder, string provider, bool local)
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = new CancellationTokenSource(PromptHook.Timeout(Environment.GetEnvironmentVariable("COCERN_HOOK_TIMEOUT_MS")));
            var text = await PostToolHook.ReadBoundedAsync(input).WaitAsync(timeout.Token);
            if (text is null) return 0;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (ChatLocators.Text(root, "agent_id") is { Length: > 0 } ||
                ChatLocators.Text(root, "session_id") is not { } chat || !ChatLocators.IsUuid(chat)) return 0;
            var phase = ChatLocators.Text(root, "hook_event_name") switch
            {
                "UserPromptSubmit" => "working",
                "Stop" => "finished",
                "Interrupt" => "interrupted",
                _ => null
            };
            if (phase is null) return 0;

            SessionHandle? Find(string folder, string thread) => NodeState.ChooseForChat(
                NodeState.ReadSessionHandles(folder).Where(handle => (handle.Chat ?? handle.Client) is { } location &&
                    location.Provider == provider && string.Equals(location.ThreadId, thread, StringComparison.OrdinalIgnoreCase)), thread);
            var folder = local ? LocalWakeConnection.FindForChat(chat, Find) : nodeFolder;
            if (folder is null || Find(folder, chat) is not { } handle || handle.OpenedAt > at || ClaudeWaiter.ConnectionOptions(folder) is not { } options) return 0;
            using var http = new HttpClient(RelayForwarder.CreateHandler(options));
            var args = JsonSerializer.SerializeToElement(new
            {
                repositoryId = handle.RepositoryId, sessionId = handle.SessionId, sessionToken = handle.SessionToken,
                requestId = "turn-hook-" + Guid.NewGuid().ToString("N"), turnPhase = phase, turnObservedAt = at.ToString("O")
            });
            await new RelayForwarder(http, options, CallOrigins.Node).ForwardAsync(RelayTools.Ack,
                RelayForwarder.WithOrigin(args, background: true), timeout.Token);
        }
        catch (Exception) { /* A missing binding, oversized input or unavailable hub leaves the observation to expire. */ }
        return 0;
    }
}

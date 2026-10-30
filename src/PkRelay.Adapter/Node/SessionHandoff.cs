using System.Text.Json;

namespace PkRelay.Adapter.Node;

/// <summary>
/// A chat's relay session as handed to the node in <c>sessions\&lt;sessionId&gt;.json</c>: <c>{ sessionId, sessionToken, repositoryId, label, openedAt }</c>, plus <c>client { provider, chatId }</c> when the client told the adapter its chat ID.
/// The file is current-user-only and never holds the machine credential. <see cref="ToString"/> leaves the token out.
/// </summary>
public sealed record SessionHandle(string SessionId, string SessionToken, string RepositoryId, string Label, DateTimeOffset OpenedAt, ChatLocation? Client = null)
{
    public override string ToString() => $"SessionHandle {{ SessionId = {SessionId}, RepositoryId = {RepositoryId}, Client = {Client} }}";
}

/// <summary>Runs in the stdio adapter on node credentials: after each successful <c>session_open</c> it hands the new session to the node by writing its handle file.</summary>
public sealed class SessionHandoff
{
    private const string DefaultLabel = "Unnamed chat";
    private readonly string _folder;
    private readonly ChatLocation? _client;
    private readonly TextWriter _error;

    private SessionHandoff(string folder, ChatLocation? client, TextWriter error) => (_folder, _client, _error) = (folder, client, error);

    /// <summary>A hand-off only when the adapter runs on the node's credentials; with explicit PKRELAY_* settings nothing is written.</summary>
    public static SessionHandoff? For(AdapterOptions options, TextWriter? error = null) => options.NodeFolder is { } folder ? new(folder, options.ClientChat, error ?? Console.Error) : null;

    /// <summary>Writes the handle for a successful <c>session_open</c> reply. A failure is reported without details and never fails the call that already opened the session.</summary>
    public void Record(JsonElement arguments, AdapterReply reply)
    {
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data ||
            ChatLocators.Text(data, "sessionId") is not { } sessionId || ChatLocators.Text(data, "sessionToken") is not { Length: > 0 } sessionToken ||
            ChatLocators.Text(arguments, "repositoryId") is not { Length: > 0 and <= NodeState.MaxIdentifierLength } repositoryId) return;
        var label = ChatLocators.Text(arguments, "label")?.Trim() is { Length: > 0 } given && !given.Any(char.IsControl) ? given[..Math.Min(given.Length, NodeState.MaxIdentifierLength)] : DefaultLabel;
        try { NodeState.WriteSessionHandle(_folder, new SessionHandle(sessionId, sessionToken, repositoryId, label, DateTimeOffset.UtcNow, _client)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _error.WriteLine("PkRelay could not hand this chat's session to the node, so other PCs will not see it.");
        }
    }
}

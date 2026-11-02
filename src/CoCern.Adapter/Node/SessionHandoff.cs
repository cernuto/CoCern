using System.Text.Json;

namespace CoCern.Adapter.Node;

/// <summary>
/// A chat's relay session as handed to the node in <c>sessions\&lt;sessionId&gt;.json</c>: <c>{ sessionId, sessionToken, repositoryId, label, openedAt }</c>, plus <c>client { provider, chatId }</c> when the client told the adapter its chat ID.
/// The node adds its outcome: <c>chat { provider, threadId }</c> once located, or <c>unmapped: true</c> once it gave up; either is final.
/// The file is current-user-only and never holds the machine credential. <see cref="ToString"/> leaves the token out.
/// </summary>
public sealed record SessionHandle(string SessionId, string SessionToken, string RepositoryId, string Label, DateTimeOffset OpenedAt, ChatLocation? Client = null, ChatLocation? Chat = null, bool Unmapped = false)
{
    public override string ToString() => $"SessionHandle {{ SessionId = {SessionId}, RepositoryId = {RepositoryId}, Client = {Client}, Chat = {Chat}, Unmapped = {Unmapped} }}";
}

/// <summary>Runs in the stdio adapter on node credentials: after each successful <c>session_open</c> it hands the new session to the node by writing its handle file.</summary>
public sealed class SessionHandoff
{
    private const string DefaultLabel = "Unnamed chat";
    private readonly string _folder;
    private readonly ChatLocation? _client;
    private readonly TextWriter _error;
    private readonly Dictionary<string, DateTime> _touched = new(StringComparer.Ordinal);
    /// <summary>How often a chat's activity refreshes its handle file; the node prunes handles idle for <see cref="NodeChats.IdleAfter"/>.</summary>
    internal static readonly TimeSpan TouchEvery = TimeSpan.FromHours(1);

    private SessionHandoff(string folder, ChatLocation? client, TextWriter error) => (_folder, _client, _error) = (folder, client, error);

    /// <summary>A hand-off writing into <paramref name="folder"/> directly (tests).</summary>
    internal static SessionHandoff ForFolder(string folder, ChatLocation? client = null) => new(folder, client, TextWriter.Null);

    /// <summary>A hand-off only when the adapter runs on the node's credentials; with explicit COCERN_* settings nothing is written.</summary>
    public static SessionHandoff? For(AdapterOptions options, TextWriter? error = null) => options.NodeFolder is { } folder ? new(folder, options.ClientChat, error ?? Console.Error) : null;

    /// <summary>
    /// Marks this chat's handle as in use (its file's write time), at most once per <see cref="TouchEvery"/>, so the node keeps announcing a
    /// chat that still calls CoCern and prunes one that stopped. Best effort: a handle that is gone or locked is left alone.
    /// </summary>
    public void Touch(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || ChatLocators.Text(arguments, "sessionId") is not { Length: > 0 } sessionId || !NodeState.IsFileId(sessionId)) return;
        var now = DateTime.UtcNow;
        lock (_touched)
        {
            if (_touched.TryGetValue(sessionId, out var last) && now - last < TouchEvery) return;
            _touched[sessionId] = now;
        }
        try
        {
            var path = NodeState.SessionHandlePath(_folder, sessionId);
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

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
            _error.WriteLine("CoCern could not hand this chat's session to the node, so other PCs will not see it.");
        }
    }
}

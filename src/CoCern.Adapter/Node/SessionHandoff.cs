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

/// <summary>
/// Runs in the stdio adapter on node credentials: after each successful <c>session_open</c> it hands the new session to the node by writing its
/// handle file, and after a rename through <c>session_bind</c> it updates that file's label.
/// </summary>
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

    /// <summary>Restores an established handle only after its authenticated tool call succeeded; never opens a replacement session.</summary>
    internal void Restore(string tool, JsonElement arguments, AdapterReply reply)
    {
        if (!IsSessionReply(tool, arguments, reply) ||
            ChatLocators.Text(arguments, "sessionId") is not { } sessionId || !NodeState.IsFileId(sessionId) ||
            ChatLocators.Text(arguments, "sessionToken") is not { Length: > 0 } token ||
            ChatLocators.Text(arguments, "repositoryId") is not { Length: > 0 and <= NodeState.MaxIdentifierLength } repository) return;
        var path = NodeState.SessionHandlePath(_folder, sessionId);
        using var owner = Acquire(sessionId);
        if (owner is null) return;
        var previous = NodeState.ReadSessionHandle(path);
        if (previous is not null)
        {
            // Neither a reused ID nor a new adapter may move a cached handle onto a different repository or native chat.
            if (previous.RepositoryId != repository || previous.SessionToken != token || !OwnsNative(previous)) return;
            if (previous.Client is null && previous.Chat is null && _client is not null)
                NodeState.WriteSessionHandle(_folder, previous with { Client = _client, Unmapped = false });
            Touch(arguments);
            return;
        }
        NodeState.WriteSessionHandle(_folder, new(sessionId, token, repository, Label(arguments) ?? DefaultLabel, DateTimeOffset.UtcNow, _client));
    }

    internal void RecordLocal(JsonElement arguments, AdapterReply reply)
    {
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data || ChatLocators.Text(data, "sessionId") is not { } session || !NodeState.IsFileId(session)) return;
        using var owner = Acquire(session);
        if (owner is null) return;
        var previous = NodeState.ReadSessionHandle(NodeState.SessionHandlePath(_folder, session));
        if (previous is null) Record(arguments, reply);
        else if (previous.RepositoryId == ChatLocators.Text(arguments, "repositoryId") && previous.SessionToken == ChatLocators.Text(data, "sessionToken") && OwnsNative(previous)) Touch(arguments);
    }

    private bool OwnsNative(SessionHandle handle) => _client is null ||
        ((handle.Client is null || handle.Client == _client) && (handle.Chat is null || handle.Chat == _client));

    private FileStream? Acquire(string sessionId)
    {
        var directory = Path.Combine(_folder, "sessions");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sessionId + ".restore.lock");
        try { return File.Exists(path) ? new(path, FileMode.Open, FileAccess.Write, FileShare.None) : NodeState.CreatePrivateFile(path); }
        catch (IOException) { return null; } // A competing adapter keeps the first native owner; a later successful call may retry.
    }

    internal static bool IsSessionReply(string tool, JsonElement arguments, AdapterReply reply) => reply.Ok &&
        CoCern.Contracts.RelayToolCatalog.TryGet(tool, out var definition) && (definition.RequiresSession ||
        (tool == CoCern.Contracts.RelayTools.GetStatus && reply.Data is { ValueKind: JsonValueKind.Object } data &&
         ChatLocators.Text(arguments, "sessionId") is { Length: > 0 } session && ChatLocators.Text(data, "sessionId") == session));

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
        var label = Label(arguments) ?? DefaultLabel;
        try { NodeState.WriteSessionHandle(_folder, new SessionHandle(sessionId, sessionToken, repositoryId, label, DateTimeOffset.UtcNow, _client)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _error.WriteLine("CoCern could not hand this chat's session to the node, so other PCs will not see it.");
        }
    }

    /// <summary>
    /// After a successful <c>session_bind</c> that gave a <c>label</c>, writes that name into the session's handle, so the node announces the name
    /// the hub now holds instead of the one the session opened with. Everything else in the handle (its token, the node's outcome) is kept as read.
    /// Only a handle this PC already has is renamed. Best effort: a failure is reported without details and never fails the call that renamed the session.
    /// </summary>
    public void Rename(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || ChatLocators.Text(arguments, "sessionId") is not { Length: > 0 } sessionId || !NodeState.IsFileId(sessionId) ||
            Label(arguments) is not { } label) return;
        try
        {
            if (NodeState.ReadSessionHandle(NodeState.SessionHandlePath(_folder, sessionId)) is not { } handle || handle.Label == label) return;
            NodeState.WriteSessionHandle(_folder, handle with { Label = label });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _error.WriteLine("CoCern could not pass this chat's new name to the node, so other PCs may still show the old one.");
        }
    }

    /// <summary>The label a call gave, trimmed and within the hub's bound; null when it gave none that can be a label.</summary>
    private static string? Label(JsonElement arguments) =>
        ChatLocators.Text(arguments, "label")?.Trim() is { Length: > 0 } given && !given.Any(char.IsControl) ? given[..Math.Min(given.Length, NodeState.MaxIdentifierLength)] : null;
}

namespace CoCern.Contracts;

/// <summary>The chat apps a joined PC's node announces chats for, as named in announcements, the directory and the dashboard.</summary>
public static class ChatProviders
{
    public const string Codex = "codex";
    public const string Claude = "claude";
    /// <summary>A session the node could not map to a chat.</summary>
    public const string Unknown = "unknown";
}

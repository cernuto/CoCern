namespace PkRelay.Contracts;

/// <summary>The kinds of search document, shared by the hub's extraction and the search tool's schema. Each hit is opened with an existing tool.</summary>
public static class SearchKinds
{
    /// <summary>A board task; open it with <c>task_get</c>.</summary>
    public const string Task = "task";
    /// <summary>A shared context note; open it with <c>context_get</c>.</summary>
    public const string Context = "context";
    /// <summary>An unacknowledged inbox message, visible only to its session; open it with <c>check_inbox</c>.</summary>
    public const string Message = "message";

    public static readonly IReadOnlyList<string> All = [Task, Context, Message];
}

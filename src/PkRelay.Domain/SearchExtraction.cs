using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Domain;

/// <summary>The kinds of search document. A hit of each kind is opened with an existing tool.</summary>
public static class SearchKinds
{
    /// <summary>A board task; open it with <c>task_get</c>.</summary>
    public const string Task = "task";
    /// <summary>A shared context note; open it with <c>context_get</c>.</summary>
    public const string Context = "context";
    /// <summary>An unacknowledged inbox message, visible only to its session; open it with <c>check_inbox</c>.</summary>
    public const string Message = "message";
    /// <summary>A board's approved standing line or grant; open it with <c>task_get</c> and <c>section: "standing"</c>.</summary>
    public const string Standing = "standing";

    public static readonly IReadOnlyList<string> All = [Task, Context, Message, Standing];
}

/// <summary>
/// Turns an aggregate's state document into the text that search can find. Pure: it reads only the JSON it is given, never
/// throws on a malformed or unexpected document (it returns what it can read), and knows nothing about how documents are stored.
/// The state name decides what to read: a board gives its tasks, context notes and standing; a session gives its inbox; the
/// lock, control and discovery aggregates give nothing.
/// </summary>
public static class SearchExtraction
{
    /// <summary>A field whose name, lowercased without separators, contains one of these is never indexed (a defensive filter).</summary>
    private static readonly string[] CredentialMarkers =
        ["token", "secret", "password", "passwd", "passphrase", "credential", "apikey", "privatekey", "authorization", "cookie", "bearer"];

    public static IReadOnlyList<SearchDocument> Extract(string stateName, string json)
    {
        if (stateName is not (AggregateKinds.Board or AggregateKinds.Session) || string.IsNullOrEmpty(json)) return [];
        try
        {
            // A document without a repository could never pass the repository filter, so it is not indexed at all.
            if (JsonNode.Parse(json) is not JsonObject state || Text(state, "repositoryId") is not { Length: > 0 } repository) return [];
            return stateName == AggregateKinds.Board ? Board(state, repository) : Session(state, repository);
        }
        // The hub writes these documents itself, so this is only a guard: a document it cannot read gives no search text,
        // and never fails the commit that will call this.
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException) { return []; }
    }

    /// <summary>Whether a field name looks like it holds a credential or token.</summary>
    public static bool IsCredentialName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return CredentialMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static List<SearchDocument> Board(JsonObject board, string repository)
    {
        var documents = new List<SearchDocument>();
        if (board["tasks"] is JsonObject tasks)
            foreach (var (id, node) in tasks.OrderBy(x => x.Key, StringComparer.Ordinal))
                if (node is JsonObject task) documents.Add(TaskDocument(id, task, repository));
        if (board["context"] is JsonObject notes)
            foreach (var (id, node) in notes.OrderBy(x => x.Key, StringComparer.Ordinal))
                if (node is JsonObject note) documents.Add(ContextDocument(id, note, repository));
        if (board["standing"] is JsonObject standing) documents.AddRange(StandingDocuments(standing, repository));
        return documents;
    }

    /// <summary>
    /// One task: its title, workflow state, packet goal and target, blockers, dependencies, resources, <c>facts.notes</c>, and the
    /// latest <see cref="RelayLimits.RecentTaskEntries"/> reports and messages (what <c>task_get</c> returns), newest first so the
    /// body cap keeps the most recent.
    /// </summary>
    private static SearchDocument TaskDocument(string id, JsonObject task, string repository)
    {
        var workflow = task["workflow"] as JsonObject;
        var packet = workflow?["packet"] as JsonObject;
        var facts = task["facts"] as JsonObject;
        var goal = packet is null ? "" : Text(packet, "goal");
        var title = Text(facts ?? new JsonObject(), "title") is { Length: > 0 } named ? named : goal.Length > 0 ? goal : id;
        var body = new Body();
        if (goal != title) body.Add(goal);
        if (packet is not null) body.Add(Text(packet, "target"));
        if (workflow?["blockers"] is JsonArray blockers)
            foreach (var blocker in blockers.OfType<JsonObject>())
                body.Add(Join(" ", "blocked", Text(blocker, "kind"), Text(blocker, "summary"), Text(blocker, "recheckCondition")));
        if (workflow is not null)
        {
            var dependencies = TaskWorkflow.Dependencies(workflow).ToArray();
            if (dependencies.Length > 0) body.Add("depends on " + string.Join(' ', dependencies));
            var resources = TaskWorkflow.Resources(workflow).ToArray();
            if (resources.Length > 0) body.Add("resources " + string.Join(' ', resources));
        }
        if (facts?["notes"] is { } notes) body.Add(Flatten(notes));
        var reports = Recent(task["reports"]);
        foreach (var report in reports)
            body.Add(Join(" ", Text(report, "actionId"), Text(report, "outcome"), report["report"] is { } detail ? Flatten(detail) : ""));
        var messages = Recent(task["messages"]);
        foreach (var message in messages) body.Add(Text(message, "text"));
        var updated = new[] { Date(task, "updatedAt") }.Concat(reports.Select(x => Date(x, "at"))).Concat(messages.Select(x => Date(x, "at"))).Max();
        var status = workflow is not null && Text(workflow, "state") is { Length: > 0 } state ? state : "open";
        return new(id, SearchKinds.Task, repository, null, Title(title), body.ToString(), status, updated);
    }

    private static SearchDocument ContextDocument(string id, JsonObject note, string repository)
    {
        var body = new Body();
        if (note["content"] is { } content) body.Add(Flatten(content));
        return new(id, SearchKinds.Context, repository, null, Title(id), body.ToString(), "note", Date(note, "updatedAt"));
    }

    /// <summary>Approved lines and grants only. Pending proposals are not in effect, so they are not searchable.</summary>
    private static IEnumerable<SearchDocument> StandingDocuments(JsonObject standing, string repository)
    {
        if (standing["lines"] is JsonObject lines)
            foreach (var (key, node) in lines.OrderBy(x => x.Key, StringComparer.Ordinal))
                if (node is JsonObject line)
                {
                    var body = new Body();
                    body.Add(Text(line, "text"));
                    yield return new(Text(line, "id", key), SearchKinds.Standing, repository, null, Title(Text(line, "id", key)), body.ToString(), "line", Date(line, "approvedAt"));
                }
        if (standing["grants"] is JsonObject grants)
            foreach (var (key, node) in grants.OrderBy(x => x.Key, StringComparer.Ordinal))
                if (node is JsonObject grant)
                {
                    var body = new Body();
                    body.Add(Join(" ", Text(grant, "scope"), "grant for", string.Join(' ', TextItems(grant["targets"])), "expires", Text(grant, "expiresAt")));
                    yield return new(Text(grant, "id", key), SearchKinds.Standing, repository, null, Title(Text(grant, "id", key)), body.ToString(), "grant", Date(grant, "grantedAt"));
                }
    }

    /// <summary>
    /// One document per unacknowledged inbox message with text, owned by the session so only it can find the message. A board
    /// notification (<c>task.changed</c>) carries no text, only a pointer to a task already indexed on its board, so it is skipped.
    /// </summary>
    private static List<SearchDocument> Session(JsonObject session, string repository)
    {
        var owner = Text(session, "sessionId");
        if (owner.Length == 0 || session["inbox"] is not JsonObject inbox) return [];
        var documents = new List<SearchDocument>();
        foreach (var (id, node) in inbox.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (node is not JsonObject message || Text(message, "text") is not { Length: > 0 } text) continue;
            var body = new Body();
            body.Add(text);
            var firstLine = text.Split('\n', 2)[0].Trim();
            documents.Add(new(Text(message, "messageId", id), SearchKinds.Message, repository, owner, Title(firstLine), body.ToString(), "unread", Date(message, "sentAt")));
        }
        return documents;
    }

    /// <summary>The latest <see cref="RelayLimits.RecentTaskEntries"/> entries of a task list, newest first.</summary>
    private static JsonObject[] Recent(JsonNode? node) =>
        node is JsonArray entries ? entries.OfType<JsonObject>().TakeLast(RelayLimits.RecentTaskEntries).Reverse().ToArray() : [];

    private static IEnumerable<string> TextItems(JsonNode? node) =>
        node is JsonArray items ? items.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>() : [];

    private static string Join(string separator, params string[] parts) => string.Join(separator, parts.Where(part => part.Length > 0));

    private static string Title(string text) => Truncate(text.Trim(), RelayLimits.MaxSearchTitleBytes);

    /// <summary>The text values of a free-form JSON value, one per line, skipping credential-like fields, read only up to the field cap.</summary>
    private static string Flatten(JsonNode node)
    {
        var text = new StringBuilder();
        void Visit(JsonNode? current)
        {
            if (current is null || text.Length >= RelayLimits.MaxSearchFieldBytes) return;
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj) if (!IsCredentialName(name)) Visit(child);
                    break;
                case JsonArray array:
                    foreach (var child in array) Visit(child);
                    break;
                case JsonValue value:
                    var item = value.TryGetValue<string>(out var s) ? s : value.GetValueKind() is JsonValueKind.Number ? value.ToJsonString() : "";
                    if (item.Trim() is { Length: > 0 } trimmed) { if (text.Length > 0) text.Append('\n'); text.Append(trimmed); }
                    break;
            }
        }
        Visit(node);
        return text.ToString();
    }

    /// <summary>The longest prefix of <paramref name="text"/> within <paramref name="maxBytes"/> UTF-8 bytes, never splitting a character.</summary>
    internal static string Truncate(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        return text[..chars];
    }

    /// <summary>A body built from parts in priority order: each part is capped, and parts stop once the body cap is reached.</summary>
    private sealed class Body
    {
        private readonly StringBuilder _text = new();
        private int _bytes;

        public void Add(string part)
        {
            part = part.Trim();
            if (part.Length == 0) return;
            var separator = _text.Length > 0 ? 1 : 0;
            var room = RelayLimits.MaxSearchBodyBytes - _bytes - separator;
            if (room <= 0) return;
            var piece = Truncate(part, Math.Min(room, RelayLimits.MaxSearchFieldBytes));
            if (piece.Length == 0) return;
            if (separator > 0) _text.Append('\n');
            _text.Append(piece);
            _bytes += separator + Encoding.UTF8.GetByteCount(piece);
        }

        public override string ToString() => _text.ToString();
    }
}

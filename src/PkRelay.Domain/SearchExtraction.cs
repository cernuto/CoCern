using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Domain;

/// <summary>
/// Turns an aggregate's state document into the text that search can find. Pure: it reads only the JSON it is given, never changes it,
/// never throws on a malformed or unexpected document (it returns what it can read), and knows nothing about how documents are stored.
/// The state name decides what to read: a board gives its tasks and context notes; a session gives its inbox; the lock, control and
/// discovery aggregates give nothing. <see cref="Changes(string, JsonObject?, JsonObject?)"/> gives only what changed between two
/// states, so a commit rewrites the documents it touched rather than the whole grain.
/// </summary>
public static class SearchExtraction
{
    /// <summary>A field whose name, lowercased without separators, contains one of these is never indexed (a defensive filter).</summary>
    private static readonly string[] CredentialMarkers =
        ["token", "secret", "password", "passwd", "passphrase", "credential", "apikey", "privatekey", "authorization", "cookie", "bearer"];

    /// <summary>Every search document in <paramref name="json"/>.</summary>
    public static IReadOnlyList<SearchDocument> Extract(string stateName, string json) =>
        Indexed(stateName) && Parse(json) is { } state ? Extract(stateName, state) : [];

    /// <summary>Every search document in an already parsed state, so a caller holding the object does not parse it again.</summary>
    public static IReadOnlyList<SearchDocument> Extract(string stateName, JsonObject state) =>
        Guarded(() => Repository(state) is { } repository ? Documents(stateName, state, repository, null) : [], () => []);

    /// <summary>The documents of only the named tasks on a board. Unknown IDs are ignored.</summary>
    public static IReadOnlyList<SearchDocument> ExtractTasks(JsonObject board, IEnumerable<string> taskIds) =>
        Guarded(() => Repository(board) is { } repository && board["tasks"] is JsonObject tasks
            ? taskIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Where(id => tasks[id] is JsonObject).Select(id => TaskDocument(id, tasks[id]!.AsObject(), repository)).ToArray()
            : [], () => []);

    /// <summary>What changed between two serialized states; see <see cref="Changes(string, JsonObject?, JsonObject?)"/>.</summary>
    public static SearchDelta Changes(string stateName, string? previousJson, string? currentJson) =>
        Indexed(stateName) ? Changes(stateName, Parse(previousJson), Parse(currentJson)) : SearchDelta.None;

    /// <summary>
    /// The documents to write and the keys to delete when a grain's state moves from <paramref name="previous"/> to
    /// <paramref name="current"/>. A task is rewritten only when its <c>changeRevision</c> moved, a note only when its <c>version</c>
    /// moved, and an inbox message only when it is new; documents whose source is gone are listed in <see cref="SearchDelta.Removed"/>.
    /// With no readable previous state (first write, backfill), a cleared state, or a changed repository or owner, the delta is
    /// <see cref="SearchDelta.ReplaceAll"/>: delete every row of the grain and insert <see cref="SearchDelta.Upserts"/>.
    /// </summary>
    public static SearchDelta Changes(string stateName, JsonObject? previous, JsonObject? current)
    {
        if (!Indexed(stateName)) return SearchDelta.None;
        if (current is null) return new([], [], ReplaceAll: true);
        return Guarded(() =>
        {
            var repository = Repository(current);
            if (repository is null) return new SearchDelta([], [], ReplaceAll: true);
            if (previous is null || Repository(previous) != repository || stateName == AggregateKinds.Session && Text(previous, "sessionId") != Text(current, "sessionId"))
                return new SearchDelta(Documents(stateName, current, repository, null), [], ReplaceAll: true);
            var upserts = Documents(stateName, current, repository, previous);
            var kept = Keys(stateName, current).ToHashSet();
            var removed = Keys(stateName, previous).Where(key => !kept.Contains(key)).ToArray();
            return new SearchDelta(upserts, removed, ReplaceAll: false);
        }, () => new SearchDelta(Extract(stateName, current), [], ReplaceAll: true));
    }

    /// <summary>Whether a field name looks like it holds a credential or token.</summary>
    public static bool IsCredentialName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return CredentialMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool Indexed(string stateName) => stateName is AggregateKinds.Board or AggregateKinds.Session;

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>A document without a repository could never pass the repository filter, so it is not indexed at all.</summary>
    private static string? Repository(JsonObject state) => Text(state, "repositoryId") is { Length: > 0 } repository ? repository : null;

    // The hub writes these documents itself, so this is only a guard: a document it cannot read gives what it can,
    // and never fails the commit that calls this.
    private static T Guarded<T>(Func<T> read, Func<T> fallback)
    {
        try { return read(); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException) { return fallback(); }
    }

    /// <summary>The documents of <paramref name="state"/>; with <paramref name="previous"/>, only those whose source changed since it.</summary>
    private static List<SearchDocument> Documents(string stateName, JsonObject state, string repository, JsonObject? previous)
    {
        var documents = new List<SearchDocument>();
        if (stateName == AggregateKinds.Board)
        {
            foreach (var (id, task) in Items(state, "tasks"))
                if (previous is null || Changed(previous["tasks"], id, task, "changeRevision")) documents.Add(TaskDocument(id, task, repository));
            foreach (var (id, note) in Items(state, "context"))
                if (previous is null || Changed(previous["context"], id, note, "version")) documents.Add(ContextDocument(id, note, repository));
        }
        else if (stateName == AggregateKinds.Session && Text(state, "sessionId") is { Length: > 0 } owner)
        {
            foreach (var (id, message) in Items(state, "inbox"))
                if (MessageText(message) is { } text && (previous is null || Changed(previous["inbox"], id, message, null)))
                    documents.Add(MessageDocument(id, message, text, repository, owner));
        }
        return documents;
    }

    /// <summary>The object entries of a state section in key order; anything that is not an object is skipped.</summary>
    private static IEnumerable<(string Id, JsonObject Item)> Items(JsonObject state, string section) =>
        state[section] is JsonObject items
            ? items.Where(x => x.Value is JsonObject).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, x.Value!.AsObject()))
            : [];

    /// <summary>
    /// Whether an item differs from the same item in the previous state. The marker (<c>changeRevision</c> on a task, <c>version</c>
    /// on a note) moves on every change; when either side lacks it, the whole item is compared instead.
    /// </summary>
    private static bool Changed(JsonNode? previousSection, string id, JsonObject item, string? marker)
    {
        if ((previousSection as JsonObject)?[id] is not JsonObject prior) return true;
        if (marker is not null && prior[marker] is JsonValue before && item[marker] is JsonValue after) return before.ToJsonString() != after.ToJsonString();
        return !JsonNode.DeepEquals(prior, item);
    }

    /// <summary>The keys of every document <paramref name="state"/> gives, without building their text.</summary>
    private static IEnumerable<SearchDocumentKey> Keys(string stateName, JsonObject state)
    {
        if (Repository(state) is null) return [];
        if (stateName == AggregateKinds.Board)
            return Items(state, "tasks").Select(x => new SearchDocumentKey(SearchKinds.Task, x.Id)).Concat(Items(state, "context").Select(x => new SearchDocumentKey(SearchKinds.Context, x.Id)));
        if (stateName == AggregateKinds.Session && Text(state, "sessionId").Length > 0)
            return Items(state, "inbox").Where(x => MessageText(x.Item) is not null).Select(x => new SearchDocumentKey(SearchKinds.Message, Text(x.Item, "messageId", x.Id)));
        return [];
    }

    /// <summary>
    /// One task: its title, workflow state, packet goal and target, blockers, dependencies, resources, <c>facts.notes</c>, and the
    /// latest <see cref="RelayLimits.RecentTaskEntries"/> reports and messages (what <c>task_get</c> returns). Reports and messages
    /// share the rest of the body, newest first across both, so the cap drops the oldest of either.
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
        var reports = Recent(task["reports"]).Select(report => (At: Date(report, "at"), Text: Join(" ", Text(report, "actionId"), Text(report, "outcome"), report["report"] is { } detail ? Flatten(detail) : "")));
        var messages = Recent(task["messages"]).Select(message => (At: Date(message, "at"), Text: Text(message, "text")));
        // A stable sort keeps each list's own newest-first order on equal times; a message comes before a report at the same time.
        var entries = messages.Concat(reports).OrderByDescending(entry => entry.At).ToArray();
        foreach (var entry in entries) body.Add(entry.Text);
        var updated = entries.Select(entry => entry.At).Append(Date(task, "updatedAt")).Max();
        var status = workflow is not null && Text(workflow, "state") is { Length: > 0 } state ? state : "open";
        return new(id, SearchKinds.Task, repository, null, Title(title), body.ToString(), status, updated);
    }

    private static SearchDocument ContextDocument(string id, JsonObject note, string repository)
    {
        var body = new Body();
        if (note["content"] is { } content) body.Add(Flatten(content));
        return new(id, SearchKinds.Context, repository, null, Title(id), body.ToString(), "note", Date(note, "updatedAt"));
    }

    /// <summary>
    /// The text of an inbox message, or null when it has none. A board notification (<c>task.changed</c>) carries no text, only a
    /// pointer to a task already indexed on its board, so it is not indexed.
    /// </summary>
    private static string? MessageText(JsonObject message) => Text(message, "text") is { Length: > 0 } text ? text : null;

    /// <summary>One unacknowledged inbox message, owned by its session so only that session can find it. Its title is the first line.</summary>
    private static SearchDocument MessageDocument(string id, JsonObject message, string text, string repository, string owner)
    {
        var body = new Body();
        body.Add(text);
        return new(Text(message, "messageId", id), SearchKinds.Message, repository, owner, Title(text.Split('\n', 2)[0]), body.ToString(), "unread", Date(message, "sentAt"));
    }

    /// <summary>The latest <see cref="RelayLimits.RecentTaskEntries"/> entries of a task list, newest first.</summary>
    private static JsonObject[] Recent(JsonNode? node) =>
        node is JsonArray entries ? entries.OfType<JsonObject>().TakeLast(RelayLimits.RecentTaskEntries).Reverse().ToArray() : [];

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

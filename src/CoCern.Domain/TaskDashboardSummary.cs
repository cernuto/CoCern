using System.Text.Json.Nodes;
using CoCern.Domain.State;

namespace CoCern.Domain;

internal static class TaskDashboardSummary
{
    internal static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? Clip(string? text, int limit) =>
        text?.Length > limit ? text[..limit] : text;

    internal static bool ValidParent(BoardState board, string taskId, JsonNode? value)
    {
        if (value is null) return true;
        var id = String(value);
        if (string.IsNullOrWhiteSpace(id) || board.Task(id) is null) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal) { taskId };
        while (id is not null)
        {
            if (!seen.Add(id)) return false;
            id = board.Task(id) is { } ancestor ? ParentOf(board, ancestor) : null;
        }
        return true;
    }

    internal static bool ValidReview(JsonNode? value)
    {
        if (value is not JsonObject review || String(review["commit"]) is not { Length: 40 or 64 } commit ||
            !commit.All(char.IsAsciiHexDigit) || String(review["conclusion"]) is not { Length: > 0 and <= 1024 } conclusion ||
            string.IsNullOrWhiteSpace(conclusion) || String(review["branch"]) is not { Length: > 0 and <= 256 } branch ||
            branch.Any(char.IsControl)) return false;
        return review["pullRequest"] is null || review["pullRequest"] is JsonValue pr && pr.TryGetValue<int>(out var number) && number > 0;
    }

    private static string? ParentOf(BoardState board, BoardTask task)
    {
        if (String(task.Json["parentTaskId"]) is { } parent) return parent;
        if (String(task.Json["goalAuthorityRef"]) is not { } grant || !grant.StartsWith("grant:", StringComparison.Ordinal) ||
            board.Json["goalBindings"] is not JsonObject bindings || bindings[grant[6..]] is not JsonObject binding) return null;
        var root = String(binding["rootTaskId"]);
        return root == task.TaskId ? null : root;
    }

    internal static JsonObject Row(BoardState board, BoardTask task)
    {
        var workflow = task.Workflow;
        var blocker = workflow?.BlockerList?.OfType<JsonObject>().FirstOrDefault(b => String(b["kind"]) == "human_decision")
            ?? workflow?.BlockerList?.OfType<JsonObject>().FirstOrDefault();
        var reports = task.ReportsIfAny?.OfType<JsonObject>();
        var latest = reports?.LastOrDefault();
        var message = task.MessagesIfAny?.OfType<JsonObject>().LastOrDefault();
        if (message is not null && (latest is null || new TaskMessage(message).At > new TaskReport(latest).At)) latest = message;
        var body = latest?["report"];
        var milestone = (latest?["review"] is JsonObject sourceReview ? String(sourceReview["conclusion"]) : null)
            ?? String(latest?["text"]) ?? String(body) ?? (body is JsonObject fields ? String(fields["summary"]) : null);
        if (string.IsNullOrWhiteSpace(milestone) && latest is not null)
            milestone = $"{String(latest["actionId"])} · {String(latest["outcome"])}";
        var reviewReport = reports?.LastOrDefault(report => report["review"] is JsonObject && report["historical"]?.GetValue<bool>() != true);
        var review = reviewReport?["review"]?.DeepClone().AsObject();
        if (review is not null) review["reviewer"] = reviewReport!["sessionId"]?.DeepClone() ?? reviewReport["author"]?.DeepClone();
        return new JsonObject
        {
            ["taskId"] = task.TaskId, ["version"] = task.Version, ["owner"] = task.Json["owner"]?.DeepClone(),
            ["leaseUntil"] = task.Json["leaseUntil"]?.DeepClone(), ["pendingActions"] = TaskActions.HasUnresolved(task),
            ["state"] = workflow?.State, ["title"] = Clip(String(task.FactsIfAny?["title"]) ?? workflow?.Packet?.Goal, 256),
            ["milestone"] = Clip(milestone, 320), ["updatedBy"] = latest?["sessionId"]?.DeepClone() ?? latest?["author"]?.DeepClone() ?? task.Json["updatedBy"]?.DeepClone(),
            ["updatedAt"] = latest?["at"]?.DeepClone() ?? task.Json["updatedAt"]?.DeepClone(), ["blockerKind"] = blocker?["kind"]?.DeepClone(),
            ["blocker"] = Clip(String(blocker?["summary"]), 320), ["target"] = workflow?.TargetSessionId,
            ["parentTaskId"] = ParentOf(board, task), ["review"] = review
        };
    }

    internal static IEnumerable<JsonNode> Evidence(BoardTask task) =>
        (task.ReportsIfAny?.OfType<JsonObject>() ?? []).Concat(task.MessagesIfAny?.OfType<JsonObject>() ?? [])
            .OrderByDescending(entry => DateTimeOffset.TryParse(String(entry["at"]), out var at) ? at : DateTimeOffset.MinValue);
}

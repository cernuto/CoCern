using System.Text.Json.Nodes;
using CoCern.Contracts;
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
        var blockers = workflow?.BlockerList?.OfType<JsonObject>();
        var decisions = blockers?.Where(b => String(b[WorkflowBlocker.KindKey]) == BlockerKinds.HumanDecision)
            .Select(b => WaitingList.HumanDecision(b, task.Json[BoardTask.HumanAnswersKey] as JsonObject)).ToArray() ?? [];
        var unanswered = decisions.FirstOrDefault(decision => decision.Answer is null);
        var human = unanswered ?? decisions.FirstOrDefault();
        var blocker = blockers?.FirstOrDefault();
        var allAnswered = human is not null && unanswered is null;
        string? humanAnswerRecipient = null;
        if (allAnswered)
        {
            var deliveries = task.Json[BoardTask.HumanAnswerDeliveriesKey] as JsonObject;
            humanAnswerRecipient = decisions.Select(decision => deliveries?[decision.BlockerId]).OfType<JsonObject>()
                .Select(attempt => BoardTask.HumanAnswerRecipient(attempt, workflow?.TargetSessionId, task.Owner))
                .FirstOrDefault(recipient => recipient != task.Owner) ?? task.Owner;
        }
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
        // A dashboard answer has no session, and stores an empty one: its author is who made the update.
        static string? Actor(JsonObject? entry) => String(entry?["sessionId"]) is { Length: > 0 } session ? session : String(entry?["author"]) is { Length: > 0 } author ? author : null;
        if (review is not null) review["reviewer"] = Actor(reviewReport);
        return new JsonObject
        {
            ["taskId"] = task.TaskId, ["version"] = task.Version, ["owner"] = task.Json["owner"]?.DeepClone(),
            ["leaseUntil"] = task.Json["leaseUntil"]?.DeepClone(), ["pendingActions"] = TaskActions.HasUnresolved(task),
            ["state"] = workflow?.State, ["title"] = Clip(String(task.FactsIfAny?["title"]) ?? workflow?.Packet?.Goal, 256),
            ["milestone"] = Clip(milestone, 320), ["updatedBy"] = Actor(latest) ?? String(task.Json["updatedBy"]),
            ["updatedAt"] = latest?["at"]?.DeepClone() ?? task.Json["updatedAt"]?.DeepClone(),
            ["blockerKind"] = human is null ? String(blocker?["kind"]) : BlockerKinds.HumanDecision,
            ["blocker"] = Clip(human?.Question ?? String(blocker?["summary"]), 320), ["target"] = workflow?.TargetSessionId,
            ["humanDecisionsAnswered"] = allAnswered, ["humanAnswerRecipient"] = humanAnswerRecipient,
            ["parentTaskId"] = ParentOf(board, task), ["review"] = review
        };
    }

    internal static IEnumerable<JsonNode> Evidence(BoardTask task) =>
        (task.ReportsIfAny?.OfType<JsonObject>() ?? []).Concat(task.MessagesIfAny?.OfType<JsonObject>() ?? [])
            .OrderByDescending(entry => DateTimeOffset.TryParse(String(entry["at"]), out var at) ? at : DateTimeOffset.MinValue);
}

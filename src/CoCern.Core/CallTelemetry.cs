using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;

namespace CoCern.Core;

/// <summary>What one call recorded along the way, for its activity event.</summary>
internal sealed class CallTrace
{
public DateTimeOffset At { get; init; }
public JsonObject? Args { get; set; }
public JsonObject? Usage { get; set; }
public string? SessionId { get; set; }
public string? Role { get; set; }
/// <summary>Existing feedback eligibility heuristic, based on attention opt-out and waiting; not the reported traffic origin.</summary>
public bool ModelVisible { get; set; } = true;
/// <summary>Adapter-reported origin, independent of attention and never used for authorization.</summary>
public bool? BackgroundCall { get; set; }
public string? KnownEtag { get; set; }
/// <summary>The reply's tokens when compaction already counted the reply as sent, and the step it reached.</summary>
public int? ReplyTokens { get; set; }
public int CompactStep { get; set; }
public bool OverBudget { get; set; }
}

/// <summary>
/// Records calls, attention outcomes and hold wakes as activity events, and owns the activity "target" format that storage and analytics
/// read back (<c>task:</c>, <c>message:</c>, <c>context:</c>, <c>resource:</c>, <c>inbox:</c>). Recording never fails or slows a call.
/// </summary>
internal sealed class CallTelemetry(IActivitySink activity, ITokenCounter? counter)
{
    /// <summary>Builds and queues the call's activity event. Any failure here is swallowed: telemetry must never fail a call.</summary>
    public void RecordCall(string tool, JsonElement arguments, CallTrace trace, RelayReply? reply, TimeSpan elapsed, bool canceled)
    {
        try
        {
            var args = trace.Args;
            var ok = reply?.Ok == true;
            var repository = args is null ? "" : Text(args, "repositoryId");
            var (target, hash) = args is null || !ok ? (args is null ? null : Target(tool, args), (string?)null) : (Target(tool, args), ContentHash(tool, args));
            var workflow = ok && tool == RelayTools.TaskUpdate && args?["workflow"] is JsonObject flow ? Text(flow, "state") : "";
            var raw = arguments.ValueKind == JsonValueKind.Undefined ? null : arguments.GetRawText();
            var role = Bounded(trace.Role?.ToLowerInvariant(), RelayLimits.MaxActivityRoleLength);
            // The texts wait in the queue until the writer counts them. Past the hub's cap the event goes without them: token counts are
            // dropped before any activity row is.
            var privatePayload = arguments.ValueKind == JsonValueKind.Object ?
                arguments.TryGetProperty("nativeJoin", out _) || arguments.TryGetProperty("nativePreflight", out _) : tool == RelayTools.SessionOpen;
            var (requestText, replyText) = counter is null || privatePayload ? (null, null) : (raw, reply is null || trace.ReplyTokens is not null ? null : reply.DataJson);
            if (!TokenTextBudget.Shared.TryHold(requestText, replyText)) (requestText, replyText) = (null, null);
            activity.Record(new ActivityEvent
            {
                Timestamp = trace.At, Kind = ActivityEvent.Call, RepositoryId = repository.Length <= RelayLimits.MaxIdentifierLength ? repository : "",
                SessionId = Bounded(trace.SessionId), Role = role, Tool = tool, Ok = ok, BackgroundCall = trace.BackgroundCall,
                CallOrigin = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("callOrigin", out var origin) && origin.ValueKind == JsonValueKind.String ? origin.GetString() : null,
                Code = reply?.Code ?? (canceled ? "Canceled" : Unavailable),
                RequestBytes = raw is null ? 0 : Encoding.UTF8.GetByteCount(raw),
                ReplyBytes = reply is null ? 0 : Encoding.UTF8.GetByteCount(reply.DataJson), DurationMilliseconds = elapsed.TotalMilliseconds,
                // Usage counts only with a committed call: a rejected milestone is usually resent with the same usage.
                InputTokens = ok ? Tokens(trace.Usage, "inputTokens") : null, OutputTokens = ok ? Tokens(trace.Usage, "outputTokens") : null,
                RequestId = Bounded(args is null ? null : Text(args, "requestId")), Target = target, ContentHash = hash,
                WorkflowState = workflow is "ready" or "blocked" or "completed" ? workflow : null,
                OfferTarget = ok && reply is not null && args is not null ? OfferTarget(args, reply) : null,
                // Estimated tokens (o200k) of what the agent sent and got back, as sent. The texts are counted by the activity writer, off
                // the call's path (see TokenMeter); a compacted reply was already counted.
                RequestText = requestText, ReplyText = replyText,
                ReplyTokens = counter is null ? null : trace.ReplyTokens
            });
            // A compacted reply is counted here, with the step it reached, so analytics shows which tools compact most often; an enforced
            // reply that no step could shrink is counted as over.
            if (trace.CompactStep > 0 || trace.OverBudget) activity.Record(new ActivityEvent
            {
                Timestamp = trace.At, Kind = ActivityEvent.Budget, RepositoryId = repository.Length <= RelayLimits.MaxIdentifierLength ? repository : "",
                SessionId = Bounded(trace.SessionId), Role = role, Tool = tool, Ok = true, Code = trace.CompactStep > 0 ? "step" + trace.CompactStep : "over", ReplyTokens = trace.ReplyTokens
            });
        }
        catch (Exception) { /* Never fail or slow the call for telemetry. */ }
    }

    /// <summary>The record a call wrote or claimed: a task, a context note, or a private conversation. Status repeats are counted per target.</summary>
    internal static string? Target(string tool, JsonObject args)
    {
        var taskId = Text(args, "taskId");
        return tool switch
        {
            RelayTools.TaskUpdate or RelayTools.ActionReport or RelayTools.TaskClaim or RelayTools.ActionBegin when taskId.Length > 0 => TaskTarget(args, taskId),
            RelayTools.SendMessage when taskId.Length > 0 => TaskTarget(args, taskId),
            RelayTools.SendMessage when Text(args, "recipientSessionId").Length > 0 => "message:" + Uri.EscapeDataString(Text(args, "sessionId")) + ">" + Uri.EscapeDataString(Text(args, "recipientSessionId")),
            RelayTools.ContextPut when Text(args, "contextId").Length > 0 => "context:" + BoardKey(args) + "/" + Uri.EscapeDataString(Text(args, "contextId")),
            // Lets analytics count refused claims per resource.
            RelayTools.ClaimAcquire when Text(args, "resourceId").Length > 0 => "resource:" + Uri.EscapeDataString(Text(args, "repositoryId")) + "/" + Uri.EscapeDataString(Text(args, "resourceId")),
            // Only a first page counts as an inbox read; continuation pages belong to the same read.
            RelayTools.CheckInbox when Number(args, "cursor") == 0 => "inbox:" + Uri.EscapeDataString(Text(args, "sessionId")),
            _ => null
        };
    }

    internal static string TaskTarget(JsonObject args, string taskId) => "task:" + BoardKey(args) + "/" + Uri.EscapeDataString(taskId);

    /// <summary>A fingerprint of the status text a call wrote: task facts, notes, message and workflow; a message's text; a note's content.</summary>
    internal static string? ContentHash(string tool, JsonObject args)
    {
        JsonNode? content = tool switch
        {
            RelayTools.TaskUpdate when args.ContainsKey("patch") || args.ContainsKey("notes") || args.ContainsKey("message") || args.ContainsKey("workflow") =>
                new JsonObject { ["patch"] = args["patch"]?.DeepClone(), ["notes"] = args["notes"]?.DeepClone(), ["message"] = args["message"]?.DeepClone(), ["workflow"] = args["workflow"]?.DeepClone() },
            RelayTools.SendMessage => args["text"]?.DeepClone(),
            RelayTools.ContextPut => args["content"]?.DeepClone(),
            _ => null
        };
        return content is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToJsonString())), 0, 16);
    }

    internal static string? OfferTarget(JsonObject args, RelayReply reply)
    {
        if (args["includeNextWork"] is null) return null;
        var offered = JsonNode.Parse(reply.DataJson)?["nextWork"]?["offer"]?["taskId"];
        return offered is JsonValue value && value.TryGetValue<string>(out var taskId) && taskId.Length is > 0 and <= RelayLimits.MaxIdentifierLength ? TaskTarget(args, taskId) : null;
    }

    /// <summary>A reported count, or null when absent or malformed. A bad usage report is ignored rather than failing the milestone.</summary>
    internal static long? Tokens(JsonObject? usage, string key) =>
        usage?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), out var tokens) && tokens is >= 0 and <= RelayLimits.MaxReportedTokens ? tokens : null;
    internal static string? Bounded(string? value, int maximum = RelayLimits.MaxIdentifierLength) => string.IsNullOrEmpty(value) || value.Length > maximum ? null : value;

    internal static string BoardKey(JsonObject args) => AggregateKinds.BoardKey(Text(args, "repositoryId"), Text(args, "boardId", AggregateKinds.DefaultBoard));

    /// <summary>Records one offer outcome for later analysis of which kinds are mostly dismissed. Never fails the call.</summary>
    public void RecordOutcome(JsonObject args, string kind, string outcome, string itemId)
    {
        try
        {
            activity.Record(new ActivityEvent
            {
                Timestamp = DateTimeOffset.UtcNow, Kind = ActivityEvent.Attention, RepositoryId = Text(args, "repositoryId"), SessionId = Bounded(Text(args, "sessionId")),
                Tool = Bounded(kind), Ok = true, Code = outcome, Target = Bounded(itemId)
            });
        }
        catch (Exception) { /* Telemetry never affects the call. */ }
    }

    /// <summary>Each session's last idle-hold wake ID handed out, so a hold wake is counted once however often watchers ask.</summary>
    private readonly ConcurrentDictionary<string, string> holdWakes = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts an idle-hold wake the first time its ID is handed to a reader. The hub cannot see whether the adapter then queued a turn,
    /// and a hub restart may count one period again; the count is telemetry only.
    /// </summary>
    public void RecordHoldWake(JsonObject args, string wake)
    {
        var sessionId = Text(args, "sessionId");
        if (holdWakes.TryGetValue(sessionId, out var last) && last == wake) return;
        if (holdWakes.Count >= RelayLimits.MaxHoldWakeSessions) holdWakes.Clear();
        holdWakes[sessionId] = wake;
        try { activity.Record(new ActivityEvent { Timestamp = DateTimeOffset.UtcNow, Kind = ActivityEvent.Wake, RepositoryId = Text(args, "repositoryId"), SessionId = Bounded(sessionId), Ok = true, Code = "Ok" }); }
        catch (Exception) { /* Telemetry never affects the call. */ }
    }
}

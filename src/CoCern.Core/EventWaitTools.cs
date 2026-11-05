using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

// Composes only authenticated primary transactions. Sources capture their own
// baseline atomically when their durable watch is installed, never from a read
// followed by a write. All retries keep the same absolute deadline and identity.
internal sealed class EventWaitTools(Aggregates aggregates)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "repositoryId", "boardId", "sessionId", "sessionToken", "requestId", "hubIncarnation",
        "action", "waitId", "kind", "taskId", "resourceId", "generation", "peerSessionId", "replyTo", "expiresAt"
    };

    public async Task<RelayReply> RunAsync(RelayCaller caller, JsonObject args)
    {
        if (args.Any(x => !Fields.Contains(x.Key))) return Error(RelayErrorCodes.InvalidArguments);
        try
        {
            var action = Text(args, "action"); var waitId = Text(args, "waitId");
            if (!Identifier(waitId)) return Error(RelayErrorCodes.InvalidArguments);
            JsonObject Own(bool requestId = true)
            {
                var own = new JsonObject { ["repositoryId"] = args["repositoryId"]!.DeepClone(),
                    ["sessionId"] = args["sessionId"]!.DeepClone(), ["sessionToken"] = args["sessionToken"]!.DeepClone() };
                if (requestId) own["requestId"] = args["requestId"]!.DeepClone();
                return own;
            }
            if (action is "read" or "cancel")
            {
                var own = Own(action != "read"); own["waitId"] = waitId;
                // A read deliberately carries no deduplication key. Reusing a
                // public requestId must still see cancellation/completion.
                return await aggregates.Session(caller, action == "read" ? EventWaitAggregate.ReadOperation : EventWaitAggregate.CancelOperation, own);
            }
            if (action != "register" || !DateTimeOffset.TryParse(Text(args, "expiresAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiresAt)) return Error(RelayErrorCodes.InvalidArguments);
            var kind = Text(args, "kind");
            EventWaitRequest request;
            var boardKey = Aggregates.BoardKey(args);
            if (kind == "task_completed" && !Identifier(boardKey, 256)) return Error(RelayErrorCodes.InvalidArguments);
            switch (kind)
            {
                case "task_completed" when Identifier(Text(args, "taskId")):
                    request = new(waitId, kind, boardKey + "/" + Text(args, "taskId"), "", expiresAt, BaselineRevision: 0, QualifyingData: "completed"); break;
                case "claim_released" when Identifier(Text(args, "resourceId"), 256) && args["generation"] is JsonValue number
                    && number.TryGetValue<long>(out var generation) && generation > 0:
                    request = new(waitId, kind, Text(args, "resourceId"), generation.ToString(CultureInfo.InvariantCulture), expiresAt); break;
                case "peer_reply" when Identifier(Text(args, "peerSessionId")) && Identifier(Text(args, "replyTo")):
                    request = new(waitId, kind, Text(args, "replyTo"), "", expiresAt,
                        PeerSessionId: Text(args, "peerSessionId"), ReplyTo: Text(args, "replyTo")); break;
                default: return Error(RelayErrorCodes.InvalidArguments);
            }
            var register = Own(); register["request"] = JsonSerializer.SerializeToNode(request, Json);
            var result = await aggregates.Session(caller, EventWaitAggregate.RegisterOperation, register);
            if (!result.Ok) return result;
            // A deterministic identity reconnects interrupted composition, but
            // it grants nothing: both owner and source validate the binding.
            var watchId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { Text(args, "repositoryId"), Text(args, "sessionId"), waitId }))));
            if (kind == "peer_reply")
            {
                // Success follows the source's atomic arm boundary. The durable
                // ACK may arrive later; accepted mail buffers at the owner.
                var arm = PeerReplyWaits.CreateArm(Text(args, "repositoryId"), Text(args, "sessionId"), request);
                var armed = await aggregates.Call(AggregateKinds.Session, arm.PeerSessionId, caller, PeerReplyWaits.ArmOperation,
                    JsonSerializer.SerializeToNode(arm, Json)!.AsObject());
                if (!armed.Ok) return armed;
            }
            else
            {
                var watch = new EventWaitSourceWatch(Text(args, "repositoryId"), Text(args, "sessionId"), waitId, watchId, kind,
                    kind == "task_completed" ? AggregateKinds.Board : AggregateKinds.Lock,
                    kind == "task_completed" ? boardKey : request.Target,
                    kind == "task_completed" ? Text(args, "taskId") : null, expiresAt,
                    kind == "claim_released" ? request.Revision : null, request.QualifyingData);
                var bind = JsonSerializer.SerializeToNode(watch, Json)!.AsObject();
                bind["sessionId"] = args["sessionId"]!.DeepClone(); bind["sessionToken"] = args["sessionToken"]!.DeepClone();
                var bound = await aggregates.Session(caller, EventWaitSources.SourceBindOperation, bind);
                if (!bound.Ok) return bound;
            }
            return result;
        }
        catch (InvalidOperationException) { return Error(RelayErrorCodes.InvalidArguments); }
        catch (FormatException) { return Error(RelayErrorCodes.InvalidArguments); }
    }

    private static bool Identifier(string value, int max = 128) => value is { Length: > 0 } && value.Length <= max
        && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate);
}

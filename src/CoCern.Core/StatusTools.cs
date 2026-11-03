using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// <c>get_status</c>, which composes control status, the session's own status with its attention, and the board's standing orders; and
/// <c>order_put</c> and <c>order_remove</c>, which write those orders.
/// </summary>
internal sealed class StatusTools(Aggregates aggregates, StandingDelivery standing, AttentionOptions attention, OrderOptions? orders, CallTelemetry? telemetry)
{
    /// <summary>
    /// Control status, and with a session its own status. With attention on, the session part carries the count of actionable items;
    /// the adapter's <c>attentionDetail</c> adds the top item, the wake candidate, the inbox count and the board's standing revision.
    /// </summary>
    public async Task<RelayReply> StatusAsync(RelayCaller caller, JsonObject args, bool detail)
    {
        if (Text(args, "sessionId").Length == 0) return await WithOrdersAsync(caller, args, await aggregates.Control(caller, RelayTools.GetStatus, args));
        var read = args.DeepClone().AsObject();
        if (attention.Enabled) { read["attentionStatus"] = detail ? "detail" : "count"; read["maxHoldMinutes"] = attention.MaxHoldMinutes; }
        var result = await aggregates.Session(caller, RelayTools.GetStatus, read);
        if (!result.Ok) return result;
        var control = await aggregates.Control(caller, RelayTools.GetStatus, args);
        if (!control.Ok) return control;
        var data = Aggregates.Parse(result); data["control"] = JsonNode.Parse(control.DataJson);
        if (detail && attention.Enabled && data["attention"] is JsonObject summary)
        {
            if (await standing.RevisionAsync(caller, args) is { } revision) summary["standing"] = revision;
            if (Text(summary, "wake") is { } wake && wake.StartsWith(AttentionWeights.HoldWakePrefix, StringComparison.Ordinal)) telemetry?.RecordHoldWake(args, wake);
        }
        return detail ? Ok(data, result.Version) : await WithOrdersAsync(caller, args, Ok(data, result.Version));
    }

    /// <summary>
    /// Administrator only, like <c>standing_put</c>: a session's self-reported role is not an authority. An orders-off hub answers
    /// <see cref="OrdersUnavailable"/> once the caller is known to be an administrator. A <c>claim_released</c> trigger names the resource
    /// as registered; it is stored under its canonical ID, which is the lock that tells the board.
    /// </summary>
    public async Task<RelayReply> OrderAsync(RelayCaller caller, string tool, JsonObject args)
    {
        if (!caller.IsAdministrator) return Error(AdministratorRequired);
        if (orders is not { Enabled: true }) return Error(OrdersUnavailable, new { message = "Standing orders are turned off on this hub (Relay:Orders:Enabled)." });
        if (tool == RelayTools.OrderPut)
        {
            args["defaultExpiryDays"] = orders.DefaultExpiryDays;
            if (args["trigger"] is JsonObject trigger && Text(trigger, "on") == OrderTriggers.ClaimReleased && Text(trigger, "resourceId") is { Length: > 0 } resource)
            {
                var resolved = await aggregates.Resolve(caller, Text(args, "repositoryId"), "", resource);
                if (!resolved.Ok) return resolved;
                trigger["resourceId"] = Text(Aggregates.Parse(resolved), "resourceId");
            }
        }
        return await aggregates.Board(caller, tool, args);
    }

    /// <summary>With orders on, a <c>get_status</c> reply lists the board's standing orders, compactly; a background detail read does not.</summary>
    private async Task<RelayReply> WithOrdersAsync(RelayCaller caller, JsonObject args, RelayReply status)
    {
        if (!status.Ok || orders is not { Enabled: true }) return status;
        try
        {
            var read = await aggregates.Board(caller, OrdersRead.Operation, new OrdersRead(Text(args, "repositoryId"), Text(args, "boardId", "default")).ToJson());
            if (!read.Ok || Aggregates.Parse(read)["orders"] is not JsonArray { Count: > 0 } list) return status;
            var data = Aggregates.Parse(status); data["orders"] = list.DeepClone(); data["ordersRevision"] = Aggregates.Parse(read)["revision"]?.DeepClone();
            return FitsWire(Ok(data, status.Version)) ? Ok(data, status.Version) : status;
        }
        catch (Exception) { return status; }
    }
}

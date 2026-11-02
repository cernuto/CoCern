using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>What the tool definitions cost each agent, in tokens, and which tip rules switched themselves off: for status and the dashboard.</summary>
internal sealed class ToolTokenStatus(ITokenCounter? counter, FeedbackRules? feedback)
{
    /// <summary>Estimated tokens of the tool definitions each role carries, counted once.</summary>
    private volatile ToolTotals? toolTokens;
    private sealed record ToolTotals(int Worker, int Administrator);

    /// <summary>
    /// An administrator's <c>get_status</c> shows the estimated tokens of the tool definitions each role carries, and with tips on the tip
    /// rules that switched themselves off, for review. Other callers' replies are unchanged.
    /// </summary>
    public RelayReply AddTo(RelayReply status)
    {
        try
        {
            if ((counter is null || toolTokens is null) && feedback is null) return status;
            var data = JsonNode.Parse(status.DataJson)!.AsObject();
            // Shown once the startup warm-up has counted them, so no status read waits for the tokenizer to load.
            if (counter is not null && toolTokens is { } totals)
                data["toolTokens"] = new JsonObject { ["worker"] = totals.Worker, ["administrator"] = totals.Administrator, ["encoding"] = counter.Encoding, ["estimated"] = true };
            if (feedback?.Rules.Where(rule => !rule.Enabled).Select(rule => (JsonNode)rule.Rule).ToArray() is { Length: > 0 } off) data["tipRulesOff"] = new JsonArray(off);
            return Ok(data, status.Version);
        }
        catch (Exception) { return status; }
    }

    /// <summary>Tokens of each tool's description and forwarding schema for one role: what every connected agent carries on every turn.</summary>
    private int ToolDefinitionTokens(bool administrator) =>
        RelayToolCatalog.ForRole(administrator).Sum(tool => counter!.Count(tool.Description) + counter.Count(RelayToolCatalog.ForwardingSchema(tool).GetRawText()));

    /// <summary>The token totals and switched-off tip rules for the dashboard. Totals are null until the startup warm-up has counted them.</summary>
    public CoCern.Contracts.Dashboard.DashboardTokens Summary() => new()
    {
        WorkerToolTokens = counter is null ? null : toolTokens?.Worker, AdministratorToolTokens = counter is null ? null : toolTokens?.Administrator,
        Encoding = counter?.Encoding ?? "", TipRulesOff = feedback?.Rules.Where(rule => !rule.Enabled).Select(rule => rule.Rule).ToArray() ?? []
    };

    /// <summary>Counts the tool definitions once, which also loads the tokenizer, so the first call does not pay for it.</summary>
    public void Warm()
    {
        if (counter is null) return;
        try { toolTokens ??= new(ToolDefinitionTokens(false), ToolDefinitionTokens(true)); }
        catch (Exception) { /* Counted on first use instead. */ }
    }
}

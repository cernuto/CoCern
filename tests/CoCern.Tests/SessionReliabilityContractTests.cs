using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public sealed class SessionReliabilityContractTests
{
    [Fact]
    public void Action_reports_advertise_the_required_attempt_and_safe_recovery_guidance()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.ActionReport, out var report));
        var full = RelayToolCatalog.InputSchema(report!);
        var compact = RelayToolCatalog.ForwardingSchema(report!);
        Assert.Contains(full.GetProperty("required").EnumerateArray(), field => field.GetString() == "attempt");
        Assert.Contains(compact.GetProperty("required").EnumerateArray(), field => field.GetString() == "attempt");
        Assert.DoesNotContain(full.GetProperty("required").EnumerateArray(), field => field.GetString() == "outcome");
        var alternatives = full.GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal("outcome", alternatives[0].GetProperty("required")[0].GetString());
        Assert.True(alternatives[1].GetProperty("properties").GetProperty("recover").GetProperty("const").GetBoolean());
        Assert.Equal("accepted", alternatives[2].GetProperty("properties").GetProperty("restoration").GetProperty("const").GetString());
        foreach (var schema in new[] { full, compact })
        {
            var attempt = schema.GetProperty("properties").GetProperty("attempt");
            Assert.Equal("integer", attempt.GetProperty("type").GetString());
            Assert.Equal(1, attempt.GetProperty("minimum").GetInt32());
            Assert.Contains("action_begin", attempt.GetProperty("description").GetString());
            Assert.Contains("ordinary report", schema.GetProperty("properties").GetProperty("outcome").GetProperty("description").GetString());
        }
        Assert.Contains("Do not start or replay", ChatGuidance.Refusal(RelayErrorCodes.AttemptRequired, RelayTools.ActionReport));
        Assert.Contains("original session", ChatGuidance.Refusal(RelayErrorCodes.VersionConflict, RelayTools.ActionBegin));
        Assert.Contains("without inventing results", ChatGuidance.Refusal(RelayErrorCodes.InvalidWorkflow, RelayTools.TaskUpdate));
    }

    [Fact]
    public void Task_reads_explain_list_only_fields_without_changing_read_modes()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.TaskGet, out var read));
        var caller = new RelayCaller("alice", false, ["repo"], true);
        RelayReply? Validate(JsonObject args)
        {
            args["repositoryId"] = "repo";
            return RelayRequestGuard.Validate(caller, read!, JsonSerializer.SerializeToElement(args), out _);
        }

        foreach (var details in new[] { true, false })
        {
            var reply = Validate(new() { ["taskId"] = "work", ["includeDetails"] = details });
            Assert.Equal(RelayErrorCodes.InvalidArguments, reply?.Code);
            var data = JsonNode.Parse(reply!.DataJson)!;
            Assert.Equal("includeDetails", data["field"]!.GetValue<string>());
            Assert.Contains("Omit openOnly, includeDetails and snapshotTag", data["reason"]!.GetValue<string>());
        }
        Assert.Null(Validate(new() { ["taskId"] = "work" }));
        Assert.Null(Validate(new() { ["includeDetails"] = true }));
        var compact = RelayToolCatalog.ForwardingSchema(read!);
        Assert.Contains("Board list only", compact.GetProperty("properties").GetProperty("includeDetails").GetProperty("description").GetString());
        Assert.Contains("even when false", ChatGuidance.Refusal(RelayErrorCodes.InvalidArguments, RelayTools.TaskGet));
    }
}

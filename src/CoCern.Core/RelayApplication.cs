using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// Executes public tools, in three stages: <see cref="CallGuard"/> admits the call and verifies its session, <see cref="ToolRouter"/> runs
/// it (one aggregate call, or a use case that composes several), and <see cref="ReplyAddOns"/> adds what the reply carries on its way out,
/// in one fixed order, ending with compaction. Aggregates enforce the rules. Around the stages, each call is recorded and fed to the
/// feedback rules, off its path.
/// </summary>
public sealed class RelayApplication : IRelayApplication
{
    private readonly Aggregates aggregates;
    private readonly AttentionOptions attentionOptions;
    private readonly ToolTokenStatus tokenStatus;
    private readonly CallTelemetry? telemetry;
    private readonly FeedbackRules? feedback;
    private readonly CallGuard guard;
    private readonly ToolRouter router;
    private readonly ReplyAddOns addOns;
    private readonly AdminActionRecorder actions;
    private readonly string incarnation = Guid.NewGuid().ToString("N");
    public string IncarnationId => incarnation;

    public RelayApplication(IGrainFactory grains, IMachineDirectory? machines = null, IActivitySink? activity = null, IFullTextSearch? search = null, AttentionOptions? attention = null, IAnalyticsTool? analytics = null, AnomalyOptions? anomaly = null, OrderOptions? orders = null, IDocCacheTools? docs = null,
        ITokenCounter? tokens = null, TokenOptions? tokenOptions = null, FeedbackRules? feedback = null, ITaskArchive? archive = null, IRelayObservationSource? observations = null, IAdminTools? admin = null, Func<string, string?>? canonicalRepository = null, ISessionContextCatalog? contextCatalog = null)
    {
        // A turned-off feature answers for itself, so no call site checks for it.
        aggregates = new(grains, archive);
        attentionOptions = attention ?? new();
        // The shared token counter when Relay:Tokens:Enabled is on; null otherwise, and then nothing is counted or compacted.
        var counter = tokenOptions?.Enabled == false ? null : tokens;
        tokenStatus = new(counter, feedback);
        telemetry = activity is null ? null : new(activity, counter);
        this.feedback = feedback;
        var claims = new ClaimComposer(aggregates);
        // One standing cache for every reader, so a standing write through this hub reaches all of them.
        var standing = new StandingDelivery(aggregates);
        var docTools = docs ?? DocCacheTool.Off;
        guard = new(aggregates, incarnation, canonicalRepository);
        actions = new(aggregates);
        var machineDirectory = machines ?? new MachineDirectory(grains);
        router = new(aggregates, claims,
            new SessionTools(aggregates, standing, attentionOptions, anomaly, docTools, searchOn: search is not null, analyticsOn: analytics is not null && !ReferenceEquals(analytics, AnalyticsTool.Off)),
            new StatusTools(aggregates, standing, attentionOptions, orders, telemetry),
            new MessageTools(aggregates, machineDirectory, attentionOptions, telemetry, incarnation),
            new PeerTools(aggregates, machineDirectory, incarnation),
            new SearchTools(search, analytics ?? AnalyticsTool.Off),
            docTools, admin ?? AdminTools.Off, new SessionContextTools(grains, contextCatalog, tokens, canonicalRepository));
        addOns = new(aggregates, claims, standing, guard, tokenStatus, attentionOptions, search, telemetry, counter, tokenOptions ?? new(), observations);
    }

    /// <summary>Reads the open target session's tool settings for an authenticated administrator.</summary>
    public Task<RelayReply> ReadSessionToolSettingsAsync(RelayCaller caller, string repositoryId, string sessionId)
    {
        if (!caller.IsAdministrator || !caller.MayAccess(repositoryId) || string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > RelayLimits.MaxIdentifierLength)
            return Task.FromResult(Error(SessionDenied));
        return aggregates.Call(AggregateKinds.Session, sessionId, caller with { IsInternal = true, ActingParticipant = null, GoalParticipants = null },
            SessionToolSettings.ReadOperation, new JsonObject { ["repositoryId"] = repositoryId });
    }

    /// <summary>Runs one public tool call and, when a sink is configured, records it once. Recording never changes or fails the call.</summary>
    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (telemetry is null && feedback is null || !RelayToolCatalog.TryGet(tool, out _)) return await ExecuteCoreAsync(caller, tool, arguments, null, cancellationToken);
        var trace = new CallTrace { At = DateTimeOffset.UtcNow };
        var started = Stopwatch.GetTimestamp();
        RelayReply? reply = null;
        try { return reply = await ExecuteCoreAsync(caller, tool, arguments, trace, cancellationToken); }
        finally
        {
            telemetry?.RecordCall(tool, arguments, trace, reply, Stopwatch.GetElapsedTime(started), cancellationToken.IsCancellationRequested);
            if (feedback is not null && reply is not null) ObserveFeedback(caller, tool, trace, reply);
        }
    }

    /// <summary>The stages, then the refusal's line. It is added here, on the way out, so no stored reply holds it and a replayed refusal gets it too.</summary>
    private async Task<RelayReply> ExecuteCoreAsync(RelayCaller caller, string tool, JsonElement arguments, CallTrace? trace, CancellationToken cancellationToken) =>
        WithRefusalHint(tool, await RunStagesAsync(caller, tool, arguments, trace, cancellationToken));

    /// <summary>A refusal about what the chat may do keeps its code and data, and says what's missing and how to get it, unless it already says so.</summary>
    private static RelayReply WithRefusalHint(string tool, RelayReply reply)
    {
        if (reply.Ok || ChatGuidance.Refusal(reply.Code, tool) is not { } hint) return reply;
        var data = Aggregates.Parse(reply);
        if (data.ContainsKey(ChatGuidance.HintField) || data.ContainsKey("guidance")) return reply;
        data[ChatGuidance.HintField] = hint;
        var hinted = reply with { DataJson = data.ToJsonString() };
        return FitsWire(hinted) ? hinted : reply;
    }

    /// <summary>
    /// Guard, router, add-ons. From session verification on, an argument that a handler or aggregate cannot read answers
    /// <c>InvalidArguments</c>; the admission checks before it answer for themselves. <c>session_open</c> is complete as routed: its reply
    /// is bounded there and takes no add-ons.
    /// </summary>
    private async Task<RelayReply> RunStagesAsync(RelayCaller caller, string tool, JsonElement arguments, CallTrace? trace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!guard.TryAdmit(caller, tool, arguments, trace, out var admitted, out var rejected)) return rejected;
        try
        {
            var (call, refused) = await guard.VerifyAsync(admitted, trace);
            if (refused is not null) return await actions.AfterAsync(call, refused);
            var result = call.Args.ContainsKey("filePath") || call.Args.ContainsKey("confirmGeneration")
                ? await new FileClaimTools(aggregates, guard).RunAsync(call)
                : await router.RouteAsync(call, trace, cancellationToken);
            var routed = await actions.AfterAsync(call, result);
            var withSettings = await router.WithToolSettingsAsync(call, routed);
            return tool == RelayTools.SessionOpen ? BoundWire(withSettings) : await addOns.ApplyAsync(call, withSettings, trace);
        }
        catch (ArgumentException) { return Error(InvalidArguments); }
        catch (InvalidOperationException) { return Error(InvalidArguments); }
        catch (JsonException) { return Error(InvalidArguments); }
    }

    /// <summary>The token totals and switched-off tip rules for the dashboard.</summary>
    public CoCern.Contracts.Dashboard.DashboardTokens TokenSummary() => tokenStatus.Summary();

    /// <summary>Counts the tool definitions once at startup, which also loads the tokenizer.</summary>
    public void WarmTokenCounts() => tokenStatus.Warm();

    /// <summary>
    /// Feeds a finished call to the feedback rules, off the call's path, and delivers what they decide: a tip as a lowest-tier <c>tip</c>
    /// attention item at the session, and a proposed standing line through the board's internal hub proposal.
    /// </summary>
    private void ObserveFeedback(RelayCaller caller, string tool, CallTrace trace, RelayReply reply)
    {
        try
        {
            if (trace.Args is not { } args) return;
            var trusted = caller with { IsInternal = true };
            FeedbackActions Observe(string sessionId) => feedback!.Observe(new FeedbackCall(feedback.Options.Time.GetUtcNow(), Text(args, "repositoryId"), sessionId, tool, args, reply.DataJson, reply.Ok,
                trace.ModelVisible, trace.KnownEtag, reply.Ok ? CallTelemetry.Target(tool, args) : null, reply.Ok ? CallTelemetry.ContentHash(tool, args) : null, CallTelemetry.Bounded(Text(args, "requestId"))));
            if (trace.SessionId is { Length: > 0 } verified)
            {
                Advisory.Run(() => Deliver(trusted, Observe(verified)));
                return;
            }
            // A read that needs no session (task_get) counts only for a session that proves itself, so no caller can run up another
            // session's counts or send it tips. The check runs off the call's path.
            if (!reply.Ok || Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0) return;
            Advisory.Run(async () => { if ((await guard.VerifySessionAsync(trusted, args)).Ok) await Deliver(trusted, Observe(Text(args, "sessionId"))); });
        }
        catch (Exception) { /* Feedback never affects the call. */ }
    }

    /// <summary>Delivers what the rules decided; callers run it off the call's path.</summary>
    private async Task Deliver(RelayCaller trusted, FeedbackActions actions)
    {
        if (actions.Tip is { } tip && attentionOptions.Enabled)
            await aggregates.Session(trusted, AttentionSuggestion.Operation, new AttentionSuggestion(tip.RepositoryId, tip.SessionId, new AttentionContent
            {
                Kind = AttentionKinds.Tip, Subject = tip.Rule, Priority = AttentionWeights.Low, Note = tip.Text, Revision = tip.Revision, ExpiryDays = RelayLimits.TipExpiryDays
            }).ToJson());
        if (actions.Proposal is { } proposal)
            await aggregates.Call(HubStandingProposal.TargetKind, AggregateKinds.BoardKey(proposal.RepositoryId, AggregateKinds.DefaultBoard), HubProposer(proposal.RepositoryId), HubStandingProposal.Operation,
                new HubStandingProposal(proposal.RepositoryId, AggregateKinds.DefaultBoard, proposal.Rule, proposal.LineId, proposal.Text).ToJson());
    }

    /// <summary>The hub's own identity for proposals: internal, so only this hub can use it, and scoped to the one repository.</summary>
    private static RelayCaller HubProposer(string repository) => new(SessionRoles.HubProposer, false, [repository], true);
}

/// <summary>Advisory work runs off the call's path and never affects the call: any failure is dropped.</summary>
internal static class Advisory
{
    public static void Run(Func<Task> work) => _ = Task.Run(async () =>
    {
        try { await work(); }
        catch (Exception) { /* Advisory: the call stands either way. */ }
    }, CancellationToken.None);
}

public static class RelayCoreServiceCollectionExtensions
{
    public static IServiceCollection AddCoCernCore(this IServiceCollection services) =>
        services.AddSingleton<RelayApplication>().AddSingleton<IRelayApplication>(provider => provider.GetRequiredService<RelayApplication>()).AddSingleton<IMachineDirectory, MachineDirectory>();
}

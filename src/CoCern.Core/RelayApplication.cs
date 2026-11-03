using System.Diagnostics;
using System.Text;
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
/// Executes public tools. Each tool maps to one aggregate call, or to a use case that composes several
/// (session open, task claim with resources, action begin, convenience renewals/acks, next-work offers).
/// Aggregates enforce the rules; this layer authenticates the session, routes, composes and bounds replies.
/// </summary>
public sealed class RelayApplication(IGrainFactory grains, IMachineDirectory? machines = null, IActivitySink? activity = null, IFullTextSearch? search = null, AttentionOptions? attention = null, IAnalyticsTool? analytics = null, AnomalyOptions? anomaly = null, OrderOptions? orders = null, IDocCacheTools? docs = null,
    ITokenCounter? tokens = null, TokenOptions? tokenOptions = null, FeedbackRules? feedback = null, ITaskArchive? archive = null) : IRelayApplication
{
    private readonly IMachineDirectory directory = machines ?? new MachineDirectory(grains);
    private readonly AttentionOptions attentionOptions = attention ?? new();
    /// <summary>The shared token counter when <c>Relay:Tokens:Enabled</c> is on; null otherwise, and then nothing is counted or compacted.</summary>
    private readonly ITokenCounter? counter = tokenOptions?.Enabled == false ? null : tokens;
    private readonly TokenOptions tokenSettings = tokenOptions ?? new();
    // A turned-off feature answers for itself, so no call site checks for it.
    private readonly Aggregates aggregates = new(grains, archive);
    private readonly ClaimComposer claims = new(new Aggregates(grains, archive));
    private readonly StandingDelivery standing = new(new Aggregates(grains, archive));
    private readonly ToolTokenStatus tokenStatus = new(tokenOptions?.Enabled == false ? null : tokens, feedback);
    private readonly IDocCacheTools docTools = docs ?? DocCacheTool.Off;
    private readonly IAnalyticsTool analyticsTool = analytics ?? AnalyticsTool.Off;
    private readonly CallTelemetry? telemetry = activity is null ? null : new(activity, tokenOptions?.Enabled == false ? null : tokens);
    /// <summary>
    /// A private send first reads one worker entry to check the caller's access to the repository; the entries are never used, so one
    /// keeps the check while a large directory can no longer make the probe's reply too large and refuse every private message.
    /// </summary>
    private const int WorkerProbeLimit = 1;
    private readonly string incarnation = Guid.NewGuid().ToString("N");
    public string IncarnationId => incarnation;

    /// <summary>Runs one public tool call and, when a sink is configured, records it once. Recording never changes or fails the call.</summary>
    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (activity is null && feedback is null || !RelayToolCatalog.TryGet(tool, out _)) return await ExecuteCoreAsync(caller, tool, arguments, null, cancellationToken);
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

    private async Task<RelayReply> ExecuteCoreAsync(RelayCaller caller, string tool, JsonElement arguments, CallTrace? trace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RelayToolCatalog.TryGet(tool, out var definition)) return Error(UnknownTool);
        if (RelayRequestGuard.Validate(caller, definition, arguments, out var args) is { } rejected) return rejected;
        // Fields only the hub sets are dropped from what the client sent, before anything reads them.
        ServerArguments.StripClientValues(tool, args);
        var controls = CallControls.Take(args);
        var (offerWanted, statusDetail, pageSeen, seenIds) = (controls.OfferWanted, controls.StatusDetail, controls.PageSeen, controls.SeenIds);
        if (trace is not null)
        {
            trace.Args = args;
            if (tool is RelayTools.TaskUpdate or RelayTools.ActionReport) trace.Usage = controls.Usage;
            (trace.ModelVisible, trace.KnownEtag) = (controls.ModelVisible, controls.KnownEtag);
        }
        args["hubIncarnation"] = incarnation;
        var trusted = caller with { IsInternal = true };
        try
        {
            if (tool == RelayTools.SessionOpen)
            {
                var opened = BoundWire(await OpenSessionAsync(trusted, args));
                if (trace is not null && opened.Ok) { trace.SessionId = Text(Parse(opened), "sessionId"); trace.Role = Text(args, "role"); }
                if (opened.Ok && string.Equals(Text(args, "role"), SessionRoles.Manager, StringComparison.OrdinalIgnoreCase)) StartAnomalyMonitor(trusted, Text(args, "repositoryId"));
                if (opened.Ok) LearnWorkspace(args);
                return opened;
            }
            JsonObject? session = null;
            if (definition.RequiresSession || tool == RelayTools.GetStatus && Text(args, "sessionId").Length > 0)
            {
                var verified = await VerifySessionAsync(trusted, args);
                if (!verified.Ok) return verified;
                session = Parse(verified);
                // A background status read (the prompt hook, a wake watcher) is not the session's own turn, so it carries no role.
                if (trace is not null) { trace.SessionId = Text(args, "sessionId"); trace.Role = statusDetail && tool == RelayTools.GetStatus ? null : Text(session, "role"); }
            }
            if (tool is RelayTools.TaskClaim or RelayTools.ClaimAcquire or RelayTools.ActionBegin && await PausedAsync(trusted, args) is { } paused) return paused;
            var result = tool == RelayTools.GetStatus ? await StatusAsync(trusted, args, statusDetail) : await DispatchAsync(trusted, tool, args, session, cancellationToken);
            if (result.Ok && tool == RelayTools.GetStatus && caller.IsAdministrator && !statusDetail) result = tokenStatus.AddTo(result);
            if (result.Ok) result = await PiggybackAsync(trusted, args, result);
            if (result.Ok && tool == RelayTools.TaskUpdate && args["includeNextWork"]?.GetValue<bool>() == true) result = await WithNextWorkAsync(trusted, args, session, result);
            if (result.Ok && tool == RelayTools.StandingPut) standing.Wrote(args, result.Version);
            if (result.Ok && session is not null && StandingDelivery.Carries(tool)) result = await standing.AttachIfChangedAsync(trusted, args, session, result);
            var offers = result.Ok && session is not null && offerWanted && attentionOptions.Enabled && IsBoundary(tool);
            if (offers) result = await WithAttentionAsync(trusted, args, tool, result);
            // A page reported as seen is marked from the reply as sent, after compaction, so a message cut from the page stays unseen.
            var markSeen = result.Ok && session is not null && attentionOptions.Enabled && tool == RelayTools.CheckInbox && (offers || pageSeen || seenIds.Length > 0);
            // The next-work offer this reply carries becomes a next_work item too, raised after the reply's own attention offer was chosen.
            if (result.Ok && tool == RelayTools.TaskUpdate && attentionOptions.Enabled) await RaiseOfferedWorkAsync(trusted, args, result);
            // The claimer's own next_work item for the task (from a next-work offer) closes with the claim.
            if (result.Ok && tool == RelayTools.TaskClaim && attentionOptions.Enabled) await CloseClaimedWorkAsync(trusted, args, result);
            // Related work is looked up after the reply, off the claim's path; the items are offered from the next boundary.
            if (result.Ok && tool == RelayTools.TaskClaim && attentionOptions.Enabled && search is not null)
            {
                var claimed = result;
                Advisory(() => RaiseRelatedWorkAsync(trusted, args, claimed));
            }
            var sent = Bound(definition, args, result, trace);
            if (markSeen && sent.Ok) await MarkSeenAsync(trusted, args, sent, pageSeen || offers, seenIds);
            return sent;
        }
        catch (ArgumentException) { return Error(InvalidArguments); }
        catch (InvalidOperationException) { return Error(InvalidArguments); }
        catch (JsonException) { return Error(InvalidArguments); }
    }

    /// <summary>
    /// A manager session starts its repository's anomaly monitor when analytics, attention and the monitor are all on. It runs after the
    /// reply, because the monitor grain may be busy with a check, and it never schedules a second check.
    /// </summary>
    private void StartAnomalyMonitor(RelayCaller caller, string repository)
    {
        if (anomaly is not { Enabled: true } || !attentionOptions.Enabled) return;
        Advisory(() => Call(AggregateKinds.Anomaly, repository, caller, Operations.AnomalyStart, new() { ["repositoryId"] = repository })); // else the next manager starts it
    }

    /// <summary>
    /// The adapter reports its git workspace with session_open (never advertised in a schema). The doc cache may index it, after the reply
    /// and off the open's path: a refused or failing workspace never changes the open.
    /// </summary>
    private void LearnWorkspace(JsonObject args)
    {
        if (args["workspace"] is not JsonValue reported || !reported.TryGetValue<string>(out var workspace) || string.IsNullOrWhiteSpace(workspace)) return;
        var repository = Text(args, "repositoryId");
        Advisory(() => docTools.LearnAsync(repository, workspace));
    }

    /// <summary>
    /// Bounds a reply to the wire. A tool in <c>Relay:Tokens:EnforceTools</c> is first compacted to its token budget; this runs on the
    /// composed reply, after the aggregate stored its own, and is a pure function of it, so a retried request gets the same compacted reply.
    /// Every other tool is measure-only and goes out unchanged. A token is at least one UTF-8 byte, so a reply with no more bytes than its
    /// budget is within it and is not counted on the call path (the activity writer counts it later, as for a measure-only tool).
    /// </summary>
    private RelayReply Bound(RelayTool tool, JsonObject args, RelayReply result, CallTrace? trace)
    {
        if (counter is null || !tokenSettings.Enforces(tool.Name) || !result.Ok || Encoding.UTF8.GetByteCount(result.DataJson) <= tool.TokenBudget) return BoundWire(result);
        Compaction compaction;
        try { compaction = ReplyCompactor.Compact(ReplyCompactor.FitPieces(result, tool, counter), tool, counter, enforce: true, Number(args, "cursor")); }
        catch (Exception) { return BoundWire(result); }
        var bounded = BoundWire(compaction.Reply);
        if (trace is not null && ReferenceEquals(bounded, compaction.Reply)) { trace.ReplyTokens = compaction.Tokens; trace.CompactStep = compaction.Step; trace.OverBudget = compaction.OverBudget; }
        return bounded;
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
                Advisory(() => Deliver(trusted, Observe(verified)));
                return;
            }
            // A read that needs no session (task_get) counts only for a session that proves itself, so no caller can run up another
            // session's counts or send it tips. The check runs off the call's path.
            if (!reply.Ok || Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0) return;
            Advisory(async () => { if ((await VerifySessionAsync(trusted, args)).Ok) await Deliver(trusted, Observe(Text(args, "sessionId"))); });
        }
        catch (Exception) { /* Feedback never affects the call. */ }
    }

    /// <summary>Delivers what the rules decided; callers run it off the call's path.</summary>
    private async Task Deliver(RelayCaller trusted, FeedbackActions actions)
    {
        if (actions.Tip is { } tip && attentionOptions.Enabled)
            await CallSession(trusted, Operations.AttentionRaise, new JsonObject
            {
                ["repositoryId"] = tip.RepositoryId, ["sessionId"] = tip.SessionId, ["kind"] = AttentionKinds.Tip, ["subject"] = tip.Rule,
                ["priority"] = AttentionWeights.Low, ["note"] = tip.Text, ["revision"] = tip.Revision, ["expiryDays"] = RelayLimits.TipExpiryDays
            });
        if (actions.Proposal is { } proposal)
            await Call(AggregateKinds.Board, AggregateKinds.BoardKey(proposal.RepositoryId, AggregateKinds.DefaultBoard), HubProposer(proposal.RepositoryId), Operations.StandingProposeHub, new JsonObject
            {
                ["repositoryId"] = proposal.RepositoryId, ["boardId"] = "default", ["rule"] = proposal.Rule,
                ["line"] = new JsonObject { ["id"] = proposal.LineId, ["text"] = proposal.Text }
            });
    }

    /// <summary>Runs advisory work off the call's path. It never affects the call: any failure is dropped.</summary>
    private static void Advisory(Func<Task> work) => _ = Task.Run(async () =>
    {
        try { await work(); }
        catch (Exception) { /* Advisory: the call stands either way. */ }
    }, CancellationToken.None);

    /// <summary>The hub's own identity for proposals: internal, so only this hub can use it, and scoped to the one repository.</summary>
    private static RelayCaller HubProposer(string repository) => new(SessionRoles.HubProposer, false, [repository], true);

    private Task<RelayReply> DispatchAsync(RelayCaller caller, string tool, JsonObject args, JsonObject? session, CancellationToken cancellationToken = default) => tool switch
    {
        RelayTools.TaskClaim => claims.ClaimTaskAsync(caller, args, session),
        RelayTools.ClaimRenew or RelayTools.ClaimRelease when Text(args, "taskId").Length > 0 && Text(args, "resourceId").Length == 0 => CallBoard(caller, tool, args),
        RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.ClaimRelease => claims.ResourceClaimAsync(caller, tool, args, session),
        RelayTools.ActionBegin => claims.BeginActionAsync(caller, args, session),
        RelayTools.SendMessage => SendMessageAsync(caller, args),
        RelayTools.GetStatus => StatusAsync(caller, args, false),
        RelayTools.ChatDirectory => directory.DirectoryAsync([Text(args, "repositoryId")], CancellationToken.None),
        RelayTools.ControlRequest when Text(args, "command") == "revoke_machine" => RevokeMachineAsync(caller, args),
        RelayTools.Search => SearchAsync(args, cancellationToken),
        RelayTools.AnalyticsQuery => AnalyticsAsync(caller, args, cancellationToken),
        RelayTools.SessionBind or RelayTools.CheckInbox or RelayTools.Ack => CallSession(caller, tool, args),
        RelayTools.AttentionRespond => RespondToAttentionAsync(caller, args),
        RelayTools.OrderPut or RelayTools.OrderRemove => OrderAsync(caller, tool, args),
        RelayTools.DocSearch => docTools.SearchAsync(args),
        RelayTools.DocSection => docTools.SectionAsync(args),
        // Administrator only, checked first, so a worker learns nothing about the hub's doc cache settings.
        RelayTools.DocSync => !caller.IsAdministrator ? Task.FromResult(Error(AdministratorRequired)) : docTools.SyncAsync(args, cancellationToken),
        RelayTools.WorkerList or RelayTools.ControlRequest => CallControl(caller, tool, args),
        RelayTools.TaskGet => aggregates.ReadTaskAsync(caller, args, cancellationToken),
        _ => CallBoard(caller, tool, args)
    };


    /// <summary>
    /// Full-text search over committed state, as the verified session (checked before dispatch): the request's repository plus that
    /// session's own inbox. The reply is compact: per hit its ID, kind, status, date and snippet, and its board when not default. The
    /// agent opens a hit with task_get, context_get or check_inbox. Weakest hits give way until the reply fits its byte budget.
    /// </summary>
    private async Task<RelayReply> SearchAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (search is null) return Error(SearchUnavailable);
        var limit = args["limit"] is null ? RelayLimits.DefaultSearchResults : (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultSearchResults), 1, RelayLimits.MaxSearchResults);
        var kinds = args["kinds"] is JsonArray requested ? Strings(requested).Distinct(StringComparer.Ordinal).ToArray() : [];
        var status = Text(args, "status");
        IReadOnlyList<SearchHit> hits;
        // The index is a read-only copy of committed state: a database fault, or a store closing during shutdown, makes search unavailable
        // for this call, never an argument error or an internal error.
        try { hits = await search.SearchAsync(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "query"), kinds, limit, status.Length > 0 ? status : null, cancellationToken); }
        catch (Exception exception) when (Aggregates.IsStoreFault(exception)) { return Error(Unavailable); }
        var items = new JsonArray();
        foreach (var hit in hits)
        {
            var item = new JsonObject { ["id"] = hit.DocId, ["k"] = hit.Kind, ["s"] = hit.Status };
            // A document with no recorded time has no date to show.
            if (hit.UpdatedAt > DateTimeOffset.MinValue) item["at"] = hit.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd");
            item["x"] = hit.Snippet;
            if (hit.BoardId is { } board) item["b"] = board;
            items.Add(item);
        }
        var reply = new JsonObject { ["hits"] = items };
        while (items.Count > 0 && Encoding.UTF8.GetByteCount(reply.ToJsonString()) > RelayLimits.MaxSearchReplyBytes)
        {
            items.RemoveAt(items.Count - 1);
            reply["truncated"] = true;
        }
        return Ok(reply);
    }

    /// <summary>
    /// Administrator only, checked here because no aggregate handles this tool: a session's self-reported role is not an authority.
    /// An analytics-off hub answers <see cref="AnalyticsUnavailable"/> once the caller is known to be an administrator.
    /// </summary>
    private Task<RelayReply> AnalyticsAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken) =>
        !caller.IsAdministrator ? Task.FromResult(Error(AdministratorRequired))
        : analyticsTool.RunAsync(caller, args, cancellationToken);

    /// <summary>
    /// Administrator only, like <c>standing_put</c>: a session's self-reported role is not an authority. An orders-off hub answers
    /// <see cref="OrdersUnavailable"/> once the caller is known to be an administrator. A <c>claim_released</c> trigger names the resource
    /// as registered; it is stored under its canonical ID, which is the lock that tells the board.
    /// </summary>
    private async Task<RelayReply> OrderAsync(RelayCaller caller, string tool, JsonObject args)
    {
        if (!caller.IsAdministrator) return Error(AdministratorRequired);
        if (orders is not { Enabled: true }) return Error(OrdersUnavailable, new { message = "Standing orders are turned off on this hub (Relay:Orders:Enabled)." });
        if (tool == RelayTools.OrderPut)
        {
            args["defaultExpiryDays"] = orders.DefaultExpiryDays;
            if (args["trigger"] is JsonObject trigger && Text(trigger, "on") == OrderTriggers.ClaimReleased && Text(trigger, "resourceId") is { Length: > 0 } resource)
            {
                var resolved = await Resolve(caller, new JsonObject { ["repositoryId"] = Text(args, "repositoryId") }, resource);
                if (!resolved.Ok) return resolved;
                trigger["resourceId"] = Text(Parse(resolved), "resourceId");
            }
        }
        return await CallBoard(caller, tool, args);
    }

    /// <summary>With orders on, a <c>get_status</c> reply lists the board's standing orders, compactly; a background detail read does not.</summary>
    private async Task<RelayReply> WithOrdersAsync(RelayCaller caller, JsonObject args, RelayReply status)
    {
        if (!status.Ok || orders is not { Enabled: true }) return status;
        try
        {
            var read = await CallBoard(caller, Operations.Orders, new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["boardId"] = Text(args, "boardId", "default") });
            if (!read.Ok || Parse(read)["orders"] is not JsonArray { Count: > 0 } list) return status;
            var data = Parse(status); data["orders"] = list.DeepClone(); data["ordersRevision"] = Parse(read)["revision"]?.DeepClone();
            return FitsWire(Ok(data, status.Version)) ? Ok(data, status.Version) : status;
        }
        catch (Exception) { return status; }
    }

    /// <summary>The agent's answer to an attention item. "Later" uses the hub's configured snooze; a client cannot set it.</summary>
    private async Task<RelayReply> RespondToAttentionAsync(RelayCaller caller, JsonObject args)
    {
        args["snoozeBoundaries"] = attentionOptions.SnoozeBoundaries;
        var reply = await CallSession(caller, RelayTools.AttentionRespond, args);
        if (reply.Ok) { var data = Parse(reply); telemetry?.RecordOutcome(args, Text(data, "kind"), Text(data, "outcome"), Text(data, "itemId")); }
        return reply;
    }

    /// <summary>Revoking a machine is a discovery concern, not a control-aggregate one: it needs the machine ID and is refused for non-administrators by the discovery grain.</summary>
    private Task<RelayReply> RevokeMachineAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "machineId").Length == 0 ? Task.FromResult(Error(InvalidArguments)) : directory.RevokeAsync(caller, Text(args, "machineId"), CancellationToken.None);

    /// <summary>Records the session in the control directory, initializes its private aggregate, and attaches the requested task packet.</summary>
    private async Task<RelayReply> OpenSessionAsync(RelayCaller caller, JsonObject args)
    {
        var opened = await CallControl(caller, RelayTools.SessionOpen, args);
        if (!opened.Ok) return opened;
        var data = Parse(opened);
        // The same record the directory queued, from the client's fields only: the session takes its freshness from the same defaults.
        var record = SessionRecord.Opened(Text(data, "sessionId"), Text(data, "sessionToken"), caller.PrincipalId, Text(args, "repositoryId"), args);
        var initialized = await Call(AggregateKinds.Session, record.SessionId, caller, SessionOpen.Operation, new SessionOpen(record).ToJson());
        if (!initialized.Ok) return initialized;
        // The session closed before this open reached it: a close overtook it, or this retry replays the handle of a session closed since.
        // The chat opens a new one, as after any SessionUnknown.
        if (Parse(initialized)["expired"]?.GetValue<bool>() == true) return Error(SessionUnknown, new { expired = true, reopen = RelayTools.SessionOpen });
        // The queued initialization remains a retryable durable effect and deduplicates at its recipient.
        if (Text(args, "taskId").Length > 0)
        {
            var packet = await CallBoard(caller, RelayTools.TaskGet, args);
            data["taskPacket"] = packet.Ok ? JsonNode.Parse(packet.DataJson) : JsonSerializer.SerializeToNode(new { code = packet.Code });
        }
        await standing.AttachAtOpenAsync(caller, args, data);
        return Ok(data, opened.Version);
    }

    private Task<RelayReply> VerifySessionAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0
            ? Task.FromResult(Error(SessionDenied))
            : CallSession(caller, Operations.Verify, args);

    private async Task<RelayReply?> PausedAsync(RelayCaller caller, JsonObject args)
    {
        var status = await CallControl(caller, RelayTools.GetStatus, args);
        if (!status.Ok) return status;
        return Parse(status)["paused"]?.GetValue<bool>() == true ? Error(Paused) : null;
    }

    /// <summary>A named recipient is probed in the worker directory first; delivery itself enforces repository identity and unknown recipients stay visibly pending.</summary>
    private async Task<RelayReply> SendMessageAsync(RelayCaller caller, JsonObject args)
    {
        if (Text(args, "recipientSessionId").Length > 0)
        {
            var probe = await CallControl(caller, RelayTools.WorkerList, new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["limit"] = WorkerProbeLimit });
            if (!probe.Ok) return probe;
        }
        return Text(args, "taskId").Length > 0 ? await CallBoard(caller, RelayTools.SendMessage, args) : await CallSession(caller, RelayTools.SendMessage, args);
    }

    /// <summary>
    /// Control status, and with a session its own status. With attention on, the session part carries the count of actionable items;
    /// the adapter's <c>attentionDetail</c> adds the top item, the wake candidate, the inbox count and the board's standing revision.
    /// </summary>
    private async Task<RelayReply> StatusAsync(RelayCaller caller, JsonObject args, bool detail)
    {
        if (Text(args, "sessionId").Length == 0) return await WithOrdersAsync(caller, args, await CallControl(caller, RelayTools.GetStatus, args));
        var read = args.DeepClone().AsObject();
        if (attentionOptions.Enabled) { read["attentionStatus"] = detail ? "detail" : "count"; read["maxHoldMinutes"] = attentionOptions.MaxHoldMinutes; }
        var result = await CallSession(caller, RelayTools.GetStatus, read);
        if (!result.Ok) return result;
        var control = await CallControl(caller, RelayTools.GetStatus, args);
        if (!control.Ok) return control;
        var data = Parse(result); data["control"] = JsonNode.Parse(control.DataJson);
        if (detail && attentionOptions.Enabled && data["attention"] is JsonObject attention)
        {
            if (await standing.RevisionAsync(caller, args) is { } revision) attention["standing"] = revision;
            if (Text(attention, "wake") is { } wake && wake.StartsWith(AttentionWeights.HoldWakePrefix, StringComparison.Ordinal)) telemetry?.RecordHoldWake(args, wake);
        }
        return detail ? Ok(data, result.Version) : await WithOrdersAsync(caller, args, Ok(data, result.Version));
    }

    /// <summary>Calls that carry an attention offer. Background readers send <c>attention: false</c>, so no unseen offer is marked shown.</summary>
    private static bool IsBoundary(string tool) => tool is RelayTools.TaskUpdate or RelayTools.ActionReport or RelayTools.TaskClaim or RelayTools.CheckInbox;

    /// <summary>
    /// Runs one boundary at the caller's session and attaches at most one compact offer. The committed result never changes, and the
    /// boundary is skipped when the reply has no room left for an offer, so an item is marked shown only when its offer is delivered.
    /// </summary>
    private async Task<RelayReply> WithAttentionAsync(RelayCaller caller, JsonObject args, string tool, RelayReply committed)
    {
        try
        {
            var data = Parse(committed);
            // A model-visible inbox page shows its messages, so their info items are not offered. They close after compaction, from
            // the page as sent, so a message cut from the page keeps its item.
            var onPage = tool == RelayTools.CheckInbox && data["items"] is JsonArray page
                ? new JsonArray(page.OfType<JsonObject>().Select(message => Text(message, "messageId")).Where(id => id.Length > 0).Select(id => (JsonNode)id).ToArray()) : null;
            data["attention"] = new string('x', RelayLimits.MaxAttentionOfferBytes);
            if (!FitsWire(Ok(data, long.MaxValue))) return committed;
            var request = new JsonObject
            {
                ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["sessionToken"] = Text(args, "sessionToken"),
                ["focusTaskId"] = Text(args, "taskId"), ["focusBoardId"] = Text(args, "boardId", "default"), ["snoozeBoundaries"] = attentionOptions.SnoozeBoundaries
            };
            if (onPage is { Count: > 0 }) request["pageMessageIds"] = onPage;
            var offer = await CallSession(caller, Operations.AttentionOffer, request);
            if (!offer.Ok || Parse(offer)["attention"] is not JsonObject item) return committed;
            data["attention"] = item.DeepClone();
            telemetry?.RecordOutcome(args, Text(item, "k"), "shown", Text(item, "id"));
            return Ok(data, committed.Version);
        }
        catch (Exception) { return committed; }
    }

    /// <summary>Closes the info items of messages an adapter reader showed the model without an offer. Advisory: the read stands either way.</summary>
    private async Task MarkSeenAsync(RelayCaller caller, JsonObject args, RelayReply read, bool pageSeen, string[] seenIds)
    {
        try
        {
            var ids = seenIds.ToList();
            if (pageSeen && Parse(read)["items"] is JsonArray page) ids.AddRange(page.OfType<JsonObject>().Select(message => Text(message, "messageId")).Where(id => id.Length > 0));
            if (ids.Count == 0) return;
            await CallSession(caller, Operations.AttentionSeen, new JsonObject
            {
                ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["sessionToken"] = Text(args, "sessionToken"),
                ["messageIds"] = new JsonArray(ids.Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxAcknowledgedMessages + RelayLimits.MaxPageSize).Select(id => (JsonNode)id).ToArray())
            });
        }
        catch (Exception) { /* The read stands either way. */ }
    }

    /// <summary>
    /// The task a reply's <c>nextWork</c> offered becomes a <c>next_work</c> item at the caller, at the board revision the offer was read at,
    /// so the claim that closes next_work items closes it too. It expires after a day, since a claim by another session cannot reach it.
    /// </summary>
    private async Task RaiseOfferedWorkAsync(RelayCaller caller, JsonObject args, RelayReply reply)
    {
        try
        {
            if (Parse(reply)["nextWork"]?["offer"] is not JsonObject offer || Text(offer, "taskId") is not { Length: > 0 } taskId) return;
            await CallSession(caller, Operations.AttentionRaise, new JsonObject
            {
                ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["kind"] = AttentionKinds.NextWork, ["taskId"] = taskId,
                ["boardId"] = Text(args, "boardId", "default"), ["priority"] = offer["workflow"]?["priority"]?.DeepClone(), ["note"] = "offered as your next work; claim it with task_claim",
                ["revision"] = Math.Max(1, Number(offer, "snapshotRevision")), ["expiryDays"] = RelayLimits.OfferedWorkExpiryDays
            });
        }
        catch (Exception) { /* The offer in the reply stands either way. */ }
    }

    /// <summary>Closes the caller's <c>next_work</c> item for the task it just claimed, at the claim's revision. Advisory.</summary>
    private async Task CloseClaimedWorkAsync(RelayCaller caller, JsonObject args, RelayReply claimed)
    {
        try
        {
            var claim = Parse(claimed);
            if (claim["newlyAcquired"]?.GetValue<bool>() != true) return;
            await CallSession(caller, Operations.AttentionClose, new JsonObject
            {
                ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["taskId"] = Text(args, "taskId"), ["boardId"] = Text(args, "boardId", "default"),
                ["kinds"] = new JsonArray(AttentionKinds.NextWork), ["revision"] = Number(claim, "changeRevision")
            });
        }
        catch (Exception) { /* The claim stands either way. */ }
    }

    /// <summary>
    /// After a new task claim, searches the repository for tasks like it and raises, to the claiming session, <c>duplicate_warning</c>
    /// for open look-alikes and <c>related_work</c> for completed ones, IDs only. A hit must match every word of the title or goal and
    /// score at least <see cref="RelayLimits.RelatedWorkMinScore"/>, so one shared word is not enough. Both close when the claimed task
    /// completes. It runs after the claim reply, so any failure is ignored and the claim stands.
    /// </summary>
    private async Task RaiseRelatedWorkAsync(RelayCaller caller, JsonObject args, RelayReply claimed)
    {
        if (search is null) return;
        try
        {
            var claim = Parse(claimed);
            if (claim["newlyAcquired"]?.GetValue<bool>() != true) return;
            var taskId = Text(args, "taskId");
            var read = await CallBoard(caller, RelayTools.TaskGet, new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["boardId"] = Text(args, "boardId", "default"), ["taskId"] = taskId });
            if (!read.Ok) return;
            var task = Parse(read);
            var query = task["facts"]?["title"] is JsonValue title && title.TryGetValue<string>(out var named) && named.Length > 0 ? named : Text(task["workflow"]?["packet"] as JsonObject ?? new JsonObject(), "goal");
            if (query.Length == 0) return;
            if (query.Length > RelayLimits.MaxSearchQueryLength) query = query[..RelayLimits.MaxSearchQueryLength];
            var board = Text(args, "boardId", "default");
            var hits = (await search.SearchAllWordsAsync(Text(args, "repositoryId"), Text(args, "sessionId"), query, [SearchKinds.Task], RelayLimits.MaxRelatedWorkHits + 1))
                .Where(hit => (hit.DocId != taskId || (hit.BoardId ?? "default") != board) && hit.Score >= RelayLimits.RelatedWorkMinScore).ToArray();
            async Task Raise(string kind, IEnumerable<SearchHit> found)
            {
                var ids = found.Select(hit => hit.DocId).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxRelatedWorkHits).ToArray();
                if (ids.Length == 0) return;
                await CallSession(caller, Operations.AttentionRaise, new JsonObject
                {
                    ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["kind"] = kind, ["boardId"] = board, ["subject"] = taskId,
                    ["note"] = (kind == AttentionKinds.DuplicateWarning ? "may duplicate " : "related to ") + taskId,
                    ["refs"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()), ["revision"] = Number(claim, "changeRevision"), ["expiryDays"] = attentionOptions.ItemExpiryDays
                });
            }
            await Raise(AttentionKinds.DuplicateWarning, hits.Where(hit => hit.Status != "completed"));
            await Raise(AttentionKinds.RelatedWork, hits.Where(hit => hit.Status == "completed"));
        }
        catch (Exception) { /* Related work is advisory, and the reply has already gone. */ }
    }

    /// <summary>Adds an advisory next-work offer to a committed milestone. The milestone stands whatever the offer's outcome.</summary>
    private async Task<RelayReply> WithNextWorkAsync(RelayCaller caller, JsonObject args, JsonObject? session, RelayReply committed)
    {
        JsonNode next;
        try
        {
            var status = await CallControl(caller, RelayTools.GetStatus, args);
            if (!status.Ok) next = Outcome(false, status.Code);
            else if (Parse(status)["paused"]?.GetValue<bool>() == true) next = Outcome(true, Paused);
            else
            {
                var read = args.DeepClone().AsObject(); read.Remove("requestId"); read.Remove("busyResources"); read["limit"] = RelayLimits.NextWorkCandidates;
                var offered = await CallBoard(caller, Operations.NextWork, read);
                var offer = offered.Ok ? Parse(offered) : null;
                var busy = offer?["candidates"] is JsonArray candidates ? await BusyResourcesAsync(caller, args, session, candidates) : [];
                if (busy.Count > 0)
                {
                    // Ask again so that, within equal priority, a task whose listed resources are all free is offered first.
                    read.Remove("limit"); read["busyResources"] = JsonSerializer.SerializeToNode(busy);
                    offered = await CallBoard(caller, Operations.NextWork, read);
                    offer = offered.Ok ? Parse(offered) : null;
                }
                offer?.Remove("candidates");
                next = offer ?? Outcome(false, offered.Code);
            }
        }
        catch (Exception) { next = Outcome(false, Unavailable); }
        var data = Parse(committed); data["nextWork"] = next;
        return Ok(data, committed.Version);
    }

    /// <summary>
    /// The candidates' listed resources that another session holds now, in their listed spelling. At most
    /// <see cref="RelayLimits.MaxOfferResourceLookups"/> are looked up; unknown or unchecked resources count as free. Advisory only.
    /// </summary>
    private async Task<List<string>> BusyResourcesAsync(RelayCaller caller, JsonObject args, JsonObject? session, JsonArray candidates)
    {
        try
        {
            var listed = candidates.SelectMany(candidate => Strings(candidate?["resources"])).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxOfferResourceLookups).ToArray();
            if (listed.Length == 0) return [];
            var lookup = new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["machineId"] = session is null ? "" : Text(session, "machineId") };
            var resolve = lookup.DeepClone().AsObject(); resolve["resourceIds"] = JsonSerializer.SerializeToNode(listed);
            var resolved = await CallControl(caller, Operations.ResolveAll, resolve);
            if (!resolved.Ok || Parse(resolved)["resolved"] is not JsonObject canonical) return [];
            async Task<bool> IsBusy(string resource)
            {
                if (canonical[resource] is not JsonValue id) return false;
                var claim = await Call(AggregateKinds.Lock, id.GetValue<string>(), caller, Operations.ClaimState, lookup);
                return claim.Ok && Parse(claim)["busy"]?.GetValue<bool>() == true;
            }
            var busy = await Task.WhenAll(listed.Select(IsBusy));
            return listed.Where((_, index) => busy[index]).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Optional claim renewals and inbox acknowledgments riding on another call. Their outcomes never change the committed result.</summary>
    private async Task<RelayReply> PiggybackAsync(RelayCaller caller, JsonObject args, RelayReply committed)
    {
        if (args["renewClaims"] is not JsonArray && args["ackMessageIds"] is not JsonArray) return committed;
        var conveniences = new JsonObject();
        var authorization = await VerifySessionAsync(caller, args);
        if (args["renewClaims"] is JsonArray renewals)
        {
            var outcomes = new JsonArray();
            foreach (var item in renewals.Take(RelayLimits.MaxClaimResources))
            {
                // The write has committed: a malformed entry gets its own outcome, never an error for the whole call.
                if (item is not JsonObject) { outcomes.Add(JsonSerializer.SerializeToNode(new { ok = false, code = InvalidArguments })); continue; }
                var renew = args.DeepClone().AsObject(); renew.Remove("requestId"); renew["resourceId"] = item!["resourceId"]?.DeepClone(); renew["generation"] = item["generation"]?.DeepClone();
                var resolved = authorization.Ok ? await claims.ResolveForSession(caller, renew, Parse(authorization), Text(renew, "resourceId")) : authorization;
                var result = resolved.Ok ? await Call(AggregateKinds.Lock, Text(Parse(resolved), "resourceId"), caller, RelayTools.ClaimRenew, renew) : resolved;
                outcomes.Add(JsonSerializer.SerializeToNode(new { ok = result.Ok, code = result.Code, data = JsonNode.Parse(result.DataJson) }));
            }
            conveniences["renewals"] = outcomes;
        }
        if (args["ackMessageIds"] is JsonArray ids)
        {
            var ack = args.DeepClone().AsObject(); ack.Remove("requestId"); ack["messageIds"] = ids.DeepClone();
            var result = authorization.Ok ? await CallSession(caller, RelayTools.Ack, ack) : authorization;
            conveniences["ack"] = Outcome(result.Ok, result.Code);
        }
        var data = Parse(committed); data["conveniences"] = conveniences;
        return Ok(data, committed.Version);
    }


    // Grain addressing and archive-aware board calls live in Aggregates; these keep the call sites short.
    private Task<RelayReply> Resolve(RelayCaller caller, JsonObject args, string resource) => aggregates.Resolve(caller, args, resource);
    private Task<RelayReply> CallControl(RelayCaller caller, string operation, JsonObject args) => aggregates.Control(caller, operation, args);
    private Task<RelayReply> CallSession(RelayCaller caller, string operation, JsonObject args) => aggregates.Session(caller, operation, args);
    private Task<RelayReply> CallBoard(RelayCaller caller, string operation, JsonObject args) => aggregates.Board(caller, operation, args);
    private Task<RelayReply> Call(string kind, string key, RelayCaller caller, string operation, JsonObject args) => aggregates.Call(kind, key, caller, operation, args);
    private static JsonObject Parse(RelayReply reply) => Aggregates.Parse(reply);
    private static JsonNode Outcome(bool ok, string code) => JsonSerializer.SerializeToNode(new { ok, code })!;
}

public static class RelayCoreServiceCollectionExtensions
{
    public static IServiceCollection AddCoCernCore(this IServiceCollection services) =>
        services.AddSingleton<RelayApplication>().AddSingleton<IRelayApplication>(provider => provider.GetRequiredService<RelayApplication>()).AddSingleton<IMachineDirectory, MachineDirectory>();
}

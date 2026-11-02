using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// Executes public tools. Each tool maps to one aggregate call, or to a use case that composes several
/// (session open, task claim with resources, action begin, convenience renewals/acks, next-work offers).
/// Aggregates enforce the rules; this layer authenticates the session, routes, composes and bounds replies.
/// </summary>
public sealed class RelayApplication(IGrainFactory grains, IMachineDirectory? machines = null, IActivitySink? activity = null, IFullTextSearch? search = null, AttentionOptions? attention = null, AnalyticsTool? analytics = null, AnomalyOptions? anomaly = null, OrderOptions? orders = null, DocCacheTool? docs = null,
    ITokenCounter? tokens = null, TokenOptions? tokenOptions = null, FeedbackRules? feedback = null, ITaskArchive? archive = null) : IRelayApplication
{
    private readonly IMachineDirectory directory = machines ?? new MachineDirectory(grains);
    private readonly AttentionOptions attentionOptions = attention ?? new();
    /// <summary>The shared token counter when <c>Relay:Tokens:Enabled</c> is on; null otherwise, and then nothing is counted or compacted.</summary>
    private readonly ITokenCounter? counter = tokenOptions?.Enabled == false ? null : tokens;
    private readonly TokenOptions tokenSettings = tokenOptions ?? new();
    /// <summary>Estimated tokens of the tool definitions an agent carries, per role, counted once.</summary>
    private volatile ToolTotals? toolTokens;
    private sealed record ToolTotals(int Worker, int Administrator);
    private const int WorkerProbeLimit = 50;
    private readonly string incarnation = Guid.NewGuid().ToString("N");
    /// <summary>
    /// Each board's last standing read (tick count, revision, delivered JSON), reused for <see cref="RelayLimits.StandingCacheSeconds"/>. An entry
    /// never moves to an older revision; a write leaves only its new revision (no JSON), so a read already in flight cannot restore stale standing.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long At, long Revision, string? Json)> standingCache = new(StringComparer.Ordinal);
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
            if (activity is not null) RecordCall(tool, arguments, trace, reply, Stopwatch.GetElapsedTime(started), cancellationToken.IsCancellationRequested);
            if (feedback is not null && reply is not null) ObserveFeedback(caller, tool, trace, reply);
        }
    }

    private async Task<RelayReply> ExecuteCoreAsync(RelayCaller caller, string tool, JsonElement arguments, CallTrace? trace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RelayToolCatalog.TryGet(tool, out var definition)) return Error(UnknownTool);
        if (RelayRequestGuard.Validate(caller, definition, arguments, out var args) is { } rejected) return rejected;
        // Reported usage is telemetry only: it is recorded with the call and never reaches aggregate state.
        if (args.TryGetPropertyValue("usage", out var usage))
        {
            args.Remove("usage");
            if (trace is not null && tool is RelayTools.TaskUpdate or RelayTools.ActionReport) trace.Usage = usage as JsonObject;
        }
        if (trace is not null) trace.Args = args;
        // The attention opt-out and the adapter's status detail are read here and never reach an aggregate.
        var offerWanted = args["attention"] is not JsonValue optOut || optOut.GetValue<bool>();
        var statusDetail = args["attentionDetail"] is JsonValue detail && detail.GetValue<bool>();
        // An adapter reader whose reply reaches the model without an offer (a waiting read, the waiter's printed inbox) reports what it
        // showed: seen (the returned page) or seenMessageIds (the messages it printed), so their info items close.
        var pageSeen = args["seen"] is JsonValue seenFlag && seenFlag.GetValue<bool>();
        var seenIds = args["seenMessageIds"] is JsonArray seenList ? Strings(seenList).ToArray() : [];
        // The etag the agent passed as ifNoneMatch, forwarded by the adapter only so the feedback rules can tell an unchanged read.
        if (trace is not null) { trace.ModelVisible = offerWanted && !args.ContainsKey("waitSeconds"); trace.KnownEtag = Bounded(Text(args, "knownEtag"), RelayLimits.MaxKnownEtagLength); }
        args.Remove("knownEtag");
        args.Remove("attention"); args.Remove("attentionDetail"); args.Remove("attentionStatus"); args.Remove("maxHoldMinutes"); args.Remove("seen"); args.Remove("seenMessageIds");
        args["hubIncarnation"] = incarnation;
        var trusted = caller with { IsInternal = true };
        try
        {
            if (tool == RelayTools.SessionOpen)
            {
                var opened = BoundWire(await OpenSessionAsync(trusted, args));
                if (trace is not null && opened.Ok) { trace.SessionId = Text(Parse(opened), "sessionId"); trace.Role = Text(args, "role"); }
                if (opened.Ok && string.Equals(Text(args, "role"), SessionRoles.Manager, StringComparison.OrdinalIgnoreCase)) StartAnomalyMonitor(trusted, Text(args, "repositoryId"));
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
            if (result.Ok && tool == RelayTools.GetStatus && caller.IsAdministrator && !statusDetail) result = WithTokenStatus(result);
            if (result.Ok) result = await PiggybackAsync(trusted, args, result);
            if (result.Ok && tool == RelayTools.TaskUpdate && args["includeNextWork"]?.GetValue<bool>() == true) result = await WithNextWorkAsync(trusted, args, session, result);
            if (result.Ok && tool == RelayTools.StandingPut) CacheStanding(BoardKey(args), 0, result.Version, null);
            if (result.Ok && session is not null && CarriesStanding(tool)) result = await WithStandingAsync(trusted, args, session, result);
            var offers = result.Ok && session is not null && offerWanted && attentionOptions.Enabled && IsBoundary(tool);
            if (offers) result = await WithAttentionAsync(trusted, args, tool, result);
            // A page reported as seen is marked from the reply as sent, after compaction, so a message cut from the page stays unseen.
            var markSeen = !offers && result.Ok && session is not null && attentionOptions.Enabled && tool == RelayTools.CheckInbox && (pageSeen || seenIds.Length > 0);
            // The next-work offer this reply carries becomes a next_work item too, raised after the reply's own attention offer was chosen.
            if (result.Ok && tool == RelayTools.TaskUpdate && attentionOptions.Enabled) await RaiseOfferedWorkAsync(trusted, args, result);
            // The claimer's own next_work item for the task (from a next-work offer) closes with the claim.
            if (result.Ok && tool == RelayTools.TaskClaim && attentionOptions.Enabled) await CloseClaimedWorkAsync(trusted, args, result);
            // Related work is looked up after the reply, off the claim's path; the items are offered from the next boundary.
            if (result.Ok && tool == RelayTools.TaskClaim && attentionOptions.Enabled && search is not null)
            {
                var claimed = result;
                _ = Task.Run(() => RaiseRelatedWorkAsync(trusted, args, claimed), CancellationToken.None);
            }
            var sent = Bound(definition, args, result, trace);
            if (markSeen && sent.Ok) await MarkSeenAsync(trusted, args, sent, pageSeen, seenIds);
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
        _ = Task.Run(async () =>
        {
            try { await Call(AggregateKinds.Anomaly, repository, caller, Operations.AnomalyStart, new() { ["repositoryId"] = repository }); }
            catch (Exception) { /* The next manager session starts it. */ }
        }, CancellationToken.None);
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

    /// <summary>
    /// An administrator's <c>get_status</c> shows the estimated tokens of the tool definitions each role carries, and with tips on the tip
    /// rules that switched themselves off, for review. Other callers' replies are unchanged.
    /// </summary>
    private RelayReply WithTokenStatus(RelayReply status)
    {
        try
        {
            if ((counter is null || toolTokens is null) && feedback is null) return status;
            var data = Parse(status);
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
    public CoCern.Contracts.Dashboard.DashboardTokens TokenSummary() => new()
    {
        WorkerToolTokens = counter is null ? null : toolTokens?.Worker, AdministratorToolTokens = counter is null ? null : toolTokens?.Administrator,
        Encoding = counter?.Encoding ?? "", TipRulesOff = feedback?.Rules.Where(rule => !rule.Enabled).Select(rule => rule.Rule).ToArray() ?? []
    };

    /// <summary>Counts the tool definitions once, which also loads the tokenizer, so the first call does not pay for it.</summary>
    public void WarmTokenCounts()
    {
        if (counter is null) return;
        try { toolTokens ??= new(ToolDefinitionTokens(false), ToolDefinitionTokens(true)); }
        catch (Exception) { /* Counted on first use instead. */ }
    }

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
                trace.ModelVisible, trace.KnownEtag, reply.Ok ? Target(tool, args) : null, reply.Ok ? ContentHash(tool, args) : null, Bounded(Text(args, "requestId"))));
            if (trace.SessionId is { Length: > 0 } verified)
            {
                _ = Task.Run(() => Deliver(trusted, Observe(verified)), CancellationToken.None);
                return;
            }
            // A read that needs no session (task_get) counts only for a session that proves itself, so no caller can run up another
            // session's counts or send it tips. The check runs off the call's path.
            if (!reply.Ok || Text(args, "sessionId").Length == 0 || Text(args, "sessionToken").Length == 0) return;
            _ = Task.Run(async () =>
            {
                try { if ((await VerifySessionAsync(trusted, args)).Ok) Deliver(trusted, Observe(Text(args, "sessionId"))); }
                catch (Exception) { /* Advisory. */ }
            }, CancellationToken.None);
        }
        catch (Exception) { /* Feedback never affects the call. */ }
    }

    /// <summary>Delivers what the rules decided, off the call's path. Advisory: any failure is ignored.</summary>
    private void Deliver(RelayCaller trusted, FeedbackActions actions)
    {
        if (actions.Tip is null && actions.Proposal is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (actions.Tip is { } tip && attentionOptions.Enabled)
                    await CallSession(trusted, Operations.AttentionRaise, new JsonObject
                    {
                        ["repositoryId"] = tip.RepositoryId, ["sessionId"] = tip.SessionId, ["kind"] = AttentionKinds.Tip, ["subject"] = tip.Rule,
                        ["priority"] = AttentionWeights.Low, ["note"] = tip.Text, ["revision"] = tip.Revision, ["expiryDays"] = RelayLimits.TipExpiryDays
                    });
                if (actions.Proposal is { } proposal)
                    await Call(AggregateKinds.Board, Uri.EscapeDataString(proposal.RepositoryId) + "/default", HubProposer(proposal.RepositoryId), Operations.StandingProposeHub, new JsonObject
                    {
                        ["repositoryId"] = proposal.RepositoryId, ["boardId"] = "default", ["rule"] = proposal.Rule,
                        ["line"] = new JsonObject { ["id"] = proposal.LineId, ["text"] = proposal.Text }
                    });
            }
            catch (Exception) { /* Advisory: the call stands either way. */ }
        }, CancellationToken.None);
    }

    /// <summary>The hub's own identity for proposals: internal, so only this hub can use it, and scoped to the one repository.</summary>
    private static RelayCaller HubProposer(string repository) => new(SessionRoles.HubProposer, false, [repository], true);

    /// <summary>Delivers one aggregate's due outbox effects; called by the hub's due-work pump.</summary>
    public async Task DispatchDueAsync(DueWorkReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = new RelayCaller("relay-dispatcher", false, [], true);
        var reply = await Call(reference.GrainType, reference.GrainKey, dispatcher, Operations.Dispatch, new() { ["workId"] = reference.WorkId });
        if (!reply.Ok) throw new InvalidOperationException("Durable dispatch remains pending: " + reply.Code);
    }

    private Task<RelayReply> DispatchAsync(RelayCaller caller, string tool, JsonObject args, JsonObject? session, CancellationToken cancellationToken = default) => tool switch
    {
        RelayTools.TaskClaim => ClaimTaskAsync(caller, args, session),
        RelayTools.ClaimRenew or RelayTools.ClaimRelease when Text(args, "taskId").Length > 0 && Text(args, "resourceId").Length == 0 => CallBoard(caller, tool, args),
        RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.ClaimRelease => ResourceClaimAsync(caller, tool, args),
        RelayTools.ActionBegin => BeginActionAsync(caller, args, session),
        RelayTools.SendMessage => SendMessageAsync(caller, args),
        RelayTools.GetStatus => StatusAsync(caller, args, false),
        RelayTools.ChatDirectory => directory.DirectoryAsync([Text(args, "repositoryId")], CancellationToken.None),
        RelayTools.ControlRequest when Text(args, "command") == "revoke_machine" => RevokeMachineAsync(caller, args),
        RelayTools.Search => SearchAsync(args, cancellationToken),
        RelayTools.AnalyticsQuery => AnalyticsAsync(caller, args, cancellationToken),
        RelayTools.SessionBind or RelayTools.CheckInbox or RelayTools.Ack => CallSession(caller, tool, args),
        RelayTools.AttentionRespond => RespondToAttentionAsync(caller, args),
        RelayTools.OrderPut or RelayTools.OrderRemove => OrderAsync(caller, tool, args),
        RelayTools.DocSearch => docs is null ? DocCacheOff() : docs.SearchAsync(args),
        RelayTools.DocSection => docs is null ? DocCacheOff() : docs.SectionAsync(args),
        // Administrator only, checked first, so a worker learns nothing about the hub's doc cache settings.
        RelayTools.DocSync => !caller.IsAdministrator ? Task.FromResult(Error(AdministratorRequired)) : docs is null ? DocCacheOff() : docs.SyncAsync(args, cancellationToken),
        RelayTools.WorkerList or RelayTools.ControlRequest => CallControl(caller, tool, args),
        RelayTools.TaskGet => ReadTaskAsync(caller, args, cancellationToken),
        _ => CallBoard(caller, tool, args)
    };

    /// <summary><c>task_get</c>: the board's reply, or for a task the board archived, the archived copy, read-only and marked <c>archived: true</c>.</summary>
    private async Task<RelayReply> ReadTaskAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken)
    {
        var reply = await CallBoard(caller, RelayTools.TaskGet, args);
        // The board answers TaskArchived for an ID it still remembers, and NotFound once it forgot it; the archive table has both.
        if (reply.Code is not (TaskArchived or NotFound) || archive is null || Text(args, "taskId") is not { Length: > 0 } taskId) return reply;
        string? json;
        try { json = await archive.ReadArchivedTaskAsync(Text(args, "repositoryId"), Text(args, "boardId", "default"), taskId, cancellationToken); }
        catch (Exception exception) when (exception is System.Data.Common.DbException or ObjectDisposedException) { return Error(Unavailable); }
        return json is null ? reply : ArchivedTasks.Read(args, json);
    }

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
        catch (Exception exception) when (exception is System.Data.Common.DbException or ObjectDisposedException) { return Error(Unavailable); }
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

    private static Task<RelayReply> DocCacheOff() =>
        Task.FromResult(Error(DocCacheUnavailable, new { message = "The doc cache is turned off on this hub (Relay:DocCache:Enabled)." }));

    /// <summary>
    /// Administrator only, checked here because no aggregate handles this tool: a session's self-reported role is not an authority.
    /// An analytics-off hub answers <see cref="AnalyticsUnavailable"/> once the caller is known to be an administrator.
    /// </summary>
    private Task<RelayReply> AnalyticsAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken) =>
        !caller.IsAdministrator ? Task.FromResult(Error(AdministratorRequired))
        : analytics is null ? Task.FromResult(Error(AnalyticsUnavailable, new { message = "Analytics is turned off on this hub (Relay:Analytics:Enabled)." }))
        : analytics.RunAsync(caller, args, cancellationToken);

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
        if (reply.Ok) { var data = Parse(reply); RecordOutcome(args, Text(data, "kind"), Text(data, "outcome"), Text(data, "itemId")); }
        return reply;
    }

    /// <summary>Records one offer outcome for later analysis of which kinds are mostly dismissed. Never fails the call.</summary>
    private void RecordOutcome(JsonObject args, string kind, string outcome, string itemId)
    {
        if (activity is null) return;
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

    /// <summary>Revoking a machine is a discovery concern, not a control-aggregate one: it needs the machine ID and is refused for non-administrators by the discovery grain.</summary>
    private Task<RelayReply> RevokeMachineAsync(RelayCaller caller, JsonObject args) =>
        Text(args, "machineId").Length == 0 ? Task.FromResult(Error(InvalidArguments)) : directory.RevokeAsync(caller, Text(args, "machineId"), CancellationToken.None);

    /// <summary>Records the session in the control directory, initializes its private aggregate, and attaches the requested task packet.</summary>
    private async Task<RelayReply> OpenSessionAsync(RelayCaller caller, JsonObject args)
    {
        var opened = await CallControl(caller, RelayTools.SessionOpen, args);
        if (!opened.Ok) return opened;
        var data = Parse(opened);
        var initialize = args.DeepClone().AsObject();
        initialize["sessionId"] = Text(data, "sessionId"); initialize["sessionToken"] = Text(data, "sessionToken"); initialize["principalId"] = caller.PrincipalId; initialize.Remove("requestId");
        var initialized = await Call(AggregateKinds.Session, Text(data, "sessionId"), caller, Operations.Open, initialize);
        if (!initialized.Ok) return initialized;
        // The queued initialization remains a retryable durable effect and deduplicates at its recipient.
        if (Text(args, "taskId").Length > 0)
        {
            var packet = await CallBoard(caller, RelayTools.TaskGet, args);
            data["taskPacket"] = packet.Ok ? JsonNode.Parse(packet.DataJson) : JsonSerializer.SerializeToNode(new { code = packet.Code });
        }
        try
        {
            // A fresh chat receives the board's standing once, and the session records it so later replies leave it out.
            if (await StandingAsync(caller, args) is { } standing && Number(standing, "revision") > 0 && Attach(data, standing))
            {
                var session = args.DeepClone().AsObject(); session["sessionId"] = Text(data, "sessionId"); session["sessionToken"] = Text(data, "sessionToken");
                await MarkStandingSeenAsync(caller, session, Number(standing, "revision"));
            }
        }
        catch (Exception) { /* Standing is a convenience; the session is open either way. */ }
        return Ok(data, opened.Version);
    }

    /// <summary>
    /// Successful calls whose reply carries changed standing. <c>check_inbox</c> is left out: the adapter's background wake readers and
    /// <c>waitSeconds</c> polls discard inbox replies unseen and send no opt-out for standing, so standing attached there could be recorded
    /// as received without reaching the model. Attention offers do ride <c>check_inbox</c>, because those readers send <c>attention: false</c>.
    /// </summary>
    private static bool CarriesStanding(string tool) => tool is RelayTools.TaskUpdate or RelayTools.ActionReport or RelayTools.TaskClaim;

    /// <summary>Attaches the board's standing when its revision is newer than the one this session last received. Never changes the committed result.</summary>
    private async Task<RelayReply> WithStandingAsync(RelayCaller caller, JsonObject args, JsonObject session, RelayReply committed)
    {
        try
        {
            var seen = session["standingSeen"] is JsonObject boards ? Number(boards, Text(args, "boardId", "default")) : 0;
            if (await StandingAsync(caller, args) is not { } standing || Number(standing, "revision") <= seen) return committed;
            var data = Parse(committed);
            if (Attach(data, standing)) await MarkStandingSeenAsync(caller, args, Number(standing, "revision"));
            return Ok(data, committed.Version);
        }
        catch (Exception) { return committed; }
    }

    /// <summary>Attaches the full standing when the reply still fits; otherwise only its revision, and the text waits for a later, smaller reply.</summary>
    private static bool Attach(JsonObject data, JsonObject standing)
    {
        data["standing"] = standing.DeepClone();
        if (FitsWire(Ok(data, long.MaxValue))) return true;
        data["standing"] = new JsonObject { ["revision"] = Number(standing, "revision"), ["detailFetchRequired"] = true };
        return false;
    }

    private Task<RelayReply> MarkStandingSeenAsync(RelayCaller caller, JsonObject args, long revision) => CallSession(caller, Operations.StandingSeen, new JsonObject
    {
        ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["sessionToken"] = Text(args, "sessionToken"),
        ["boardId"] = Text(args, "boardId", "default"), ["standingRevision"] = revision
    });

    /// <summary>The board's delivered standing, read at most once per <see cref="RelayLimits.StandingCacheSeconds"/>; a write through this hub clears it at once.</summary>
    private async Task<JsonObject?> StandingAsync(RelayCaller caller, JsonObject args)
    {
        var key = BoardKey(args);
        var now = Environment.TickCount64;
        if (standingCache.TryGetValue(key, out var cached) && cached.Json is not null && now - cached.At < RelayLimits.StandingCacheSeconds * 1000L) return JsonNode.Parse(cached.Json)!.AsObject();
        var read = await CallBoard(caller, Operations.Standing, new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["boardId"] = Text(args, "boardId", "default") });
        if (!read.Ok) return null;
        CacheStanding(key, now, read.Version, read.DataJson);
        return Parse(read);
    }

    private void CacheStanding(string key, long at, long revision, string? json)
    {
        if (standingCache.Count >= RelayLimits.StandingCacheBoards && !standingCache.ContainsKey(key)) standingCache.Clear();
        standingCache.AddOrUpdate(key, (at, revision, json), (_, old) => revision >= old.Revision ? (at, revision, json) : old);
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

    private async Task<RelayReply> ResourceClaimAsync(RelayCaller caller, string tool, JsonObject args)
    {
        // Only task_claim names the task claim a grant serves; a client's own value is dropped (it is also left out of the fingerprint).
        args.Remove("taskClaimGeneration");
        var resolved = await Resolve(caller, args, Text(args, "resourceId"));
        if (!resolved.Ok) return resolved;
        return await Call(AggregateKinds.Lock, Text(Parse(resolved), "resourceId"), caller, tool, args);
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
            try { if (await StandingAsync(caller, args) is { } standing) attention["standing"] = Number(standing, "revision"); }
            catch (Exception) { /* The revision is a convenience for the prompt hook. */ }
            if (Text(attention, "wake") is { } wake && wake.StartsWith(AttentionWeights.HoldWakePrefix, StringComparison.Ordinal)) RecordHoldWake(args, wake);
        }
        return detail ? Ok(data, result.Version) : await WithOrdersAsync(caller, args, Ok(data, result.Version));
    }

    /// <summary>Each session's last idle-hold wake ID handed out, so a hold wake is counted once however often watchers ask.</summary>
    private readonly ConcurrentDictionary<string, string> holdWakes = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts an idle-hold wake the first time its ID is handed to a reader. The hub cannot see whether the adapter then queued a turn,
    /// and a hub restart may count one period again; the count is telemetry only.
    /// </summary>
    private void RecordHoldWake(JsonObject args, string wake)
    {
        var sessionId = Text(args, "sessionId");
        if (activity is null || holdWakes.TryGetValue(sessionId, out var last) && last == wake) return;
        if (holdWakes.Count >= RelayLimits.MaxHoldWakeSessions) holdWakes.Clear();
        holdWakes[sessionId] = wake;
        try { activity.Record(new ActivityEvent { Timestamp = DateTimeOffset.UtcNow, Kind = ActivityEvent.Wake, RepositoryId = Text(args, "repositoryId"), SessionId = Bounded(sessionId), Ok = true, Code = "Ok" }); }
        catch (Exception) { /* Telemetry never affects the call. */ }
    }

    /// <summary>
    /// Successful calls that carry an attention offer. <c>check_inbox</c> is one, so a manager that mostly reads its inbox still gets offers;
    /// the adapter's background readers (wake watchers, the Claude waiter, <c>waitSeconds</c> polls) send <c>attention: false</c>, so they
    /// never mark an offer shown that the model does not see.
    /// </summary>
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
            // A model-visible inbox page shows its messages, so their info items are closed rather than offered.
            var seen = tool == RelayTools.CheckInbox && data["items"] is JsonArray page
                ? new JsonArray(page.OfType<JsonObject>().Select(message => Text(message, "messageId")).Where(id => id.Length > 0).Select(id => (JsonNode)id).ToArray()) : null;
            data["attention"] = new string('x', RelayLimits.MaxAttentionOfferBytes);
            if (!FitsWire(Ok(data, long.MaxValue))) return committed;
            var request = new JsonObject
            {
                ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId"), ["sessionToken"] = Text(args, "sessionToken"),
                ["focusTaskId"] = Text(args, "taskId"), ["focusBoardId"] = Text(args, "boardId", "default"), ["snoozeBoundaries"] = attentionOptions.SnoozeBoundaries
            };
            if (seen is { Count: > 0 }) request["seenMessageIds"] = seen;
            var offer = await CallSession(caller, Operations.AttentionOffer, request);
            if (!offer.Ok || Parse(offer)["attention"] is not JsonObject item) return committed;
            data["attention"] = item.DeepClone();
            RecordOutcome(args, Text(item, "k"), "shown", Text(item, "id"));
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

    /// <summary>
    /// Claims the task, then each canonical resource in a stable order. If any resource is busy, the grants this
    /// request newly acquired are released and a newly acquired task claim is rolled back.
    /// </summary>
    private async Task<RelayReply> ClaimTaskAsync(RelayCaller caller, JsonObject args, JsonObject? session)
    {
        // The board refuses a claim that leaves out a resource the packet declares; it needs the declared names resolved like the claim's own.
        args.Remove("declaredResources");
        if (await DeclaredResourcesAsync(caller, args, session) is { } declared) args["declaredResources"] = declared;
        var canonical = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var resource in Strings(args["resourceIds"]))
        {
            var resolved = await Resolve(caller, args, resource);
            if (!resolved.Ok) return resolved;
            canonical.Add(Text(Parse(resolved), "resourceId"));
        }
        if (!GrantFits(args, canonical)) return Error(PayloadTooLarge);
        var claimArgs = args.DeepClone().AsObject(); claimArgs["resourceIds"] = JsonSerializer.SerializeToNode(canonical);
        var task = await CallBoard(caller, RelayTools.TaskClaim, claimArgs);
        if (!task.Ok) return task;
        var acquired = new List<(string Resource, long Generation)>();
        var grants = new JsonArray();
        foreach (var resource in canonical)
        {
            var resourceArgs = args.DeepClone().AsObject(); resourceArgs["requestId"] = Text(args, "requestId") + ":resource:" + resource;
            // The lock records which task claim its grant serves, so a standing order's release cannot drop a later claim.
            resourceArgs["taskClaimGeneration"] = Parse(task)["claimGeneration"]?.DeepClone();
            var claim = await Call(AggregateKinds.Lock, resource, caller, RelayTools.ClaimAcquire, resourceArgs);
            if (!claim.Ok)
            {
                await RollBackClaimAsync(caller, args, acquired, task);
                return Error(Busy, new { resourceId = resource, detail = JsonNode.Parse(claim.DataJson), rollback = "new_grants_released" });
            }
            var grant = Parse(claim);
            if (grant["newlyAcquired"]?.GetValue<bool>() == true) acquired.Add((resource, Number(grant, "generation")));
            grant["resourceId"] = resource; grants.Add(grant);
        }
        var data = Parse(task); data["grants"] = grants; data["complete"] = true;
        return Ok(data, task.Version);
    }

    /// <summary>
    /// The complete grant response is authorization material and cannot be truncated after acquiring ownership.
    /// Reserve its worst-case metadata before changing any aggregate.
    /// </summary>
    private static bool GrantFits(JsonObject args, SortedSet<string> resources)
    {
        var maxLease = DateTimeOffset.MaxValue.ToString("O");
        var grants = resources.Select(resource => new { resourceId = resource, holder = Text(args, "sessionId"), generation = long.MaxValue, leaseUntil = maxLease, newlyAcquired = false }).ToArray();
        var preview = Ok(new { taskId = Text(args, "taskId"), newlyAcquired = false, claimGeneration = long.MaxValue, leaseUntil = maxLease, resourceIds = resources, grants, complete = true }, long.MaxValue);
        return FitsWire(preview, RelayLimits.AggregateReplyBytes);
    }

    private async Task RollBackClaimAsync(RelayCaller caller, JsonObject args, List<(string Resource, long Generation)> acquired, RelayReply task)
    {
        foreach (var grant in acquired)
        {
            var release = args.DeepClone().AsObject(); release["generation"] = grant.Generation; release.Remove("requestId");
            await Call(AggregateKinds.Lock, grant.Resource, caller, RelayTools.ClaimRelease, release);
        }
        var claimed = Parse(task);
        if (claimed["newlyAcquired"]?.GetValue<bool>() == true)
        {
            var release = args.DeepClone().AsObject(); release["claimGeneration"] = claimed["claimGeneration"]?.DeepClone(); release["claimRequestId"] = Text(args, "requestId"); release.Remove("requestId");
            await CallBoard(caller, Operations.TaskUnclaim, release);
        }
    }

    /// <summary>
    /// Records the action with the session's verified binding. A first (replayable) attempt must also block its
    /// resources before the agent may invoke it; if that cannot be confirmed, the agent is told not to proceed.
    /// </summary>
    private async Task<RelayReply> BeginActionAsync(RelayCaller caller, JsonObject args, JsonObject? session)
    {
        args["bindingVersion"] = session?["bindingVersion"]?.DeepClone(); args["binding"] = session?["binding"]?.DeepClone();
        // Nothing starts while a resource the packet declares is held by another session, or not held by this one.
        args.Remove("declaredResources");
        if (await DeclaredResourcesAsync(caller, args, session) is { } declared)
        {
            args["declaredResources"] = declared;
            var lookup = new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["sessionId"] = Text(args, "sessionId") };
            foreach (var resource in declared.Select(entry => entry.Value is JsonValue value && value.TryGetValue<string>(out var id) ? id : "").Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var state = await Call(AggregateKinds.Lock, resource, caller, Operations.ClaimState, lookup);
                if (!state.Ok) return state;
                if (Parse(state)["held"]?.GetValue<bool>() == true) continue;
                return Parse(state)["busy"]?.GetValue<bool>() == true ? Error(Busy, new { resourceId = resource, replayAllowed = false }) : Error(ClaimRequired, new { resourceId = resource, replayAllowed = false });
            }
        }
        var result = await CallBoard(caller, RelayTools.ActionBegin, args);
        if (!result.Ok || Parse(result)["replayAllowed"]?.GetValue<bool>() != true) return result;
        var actionId = Text(args, "actionId");
        var attempt = Number(Parse(result)["action"]!.AsObject(), "attempt", 1);
        var delivery = await Call(AggregateKinds.Board, BoardKey(args), caller, Operations.Dispatch, new() { ["operation"] = Operations.Block, ["actionKey"] = CoordinationStateMachine.ActionKey(args, actionId, attempt) });
        if (!delivery.Ok) return Error(ActionPending, new { actionId, replayAllowed = false, reason = delivery.Code });
        var current = await CallBoard(caller, RelayTools.TaskGet, args);
        if (!current.Ok || current.Version != result.Version) return Error(ActionChanged, new { actionId, replayAllowed = false });
        return result;
    }

    /// <summary>
    /// The task's declared <c>packet.resources</c>, each resolved to its canonical resource as a claim resolves its own (aliases by the
    /// session's machine), with <c>""</c> for a name no resource answers to; null when the task declares none or cannot be read (the board
    /// then answers the call itself). One read of the task's workflow, and one control lookup only when it declares resources.
    /// </summary>
    private async Task<JsonObject?> DeclaredResourcesAsync(RelayCaller caller, JsonObject args, JsonObject? session)
    {
        if (Text(args, "taskId").Length == 0) return null;
        var read = await Call(AggregateKinds.Board, BoardKey(args), caller, RelayTools.TaskGet, new JsonObject
        {
            ["repositoryId"] = Text(args, "repositoryId"), ["boardId"] = Text(args, "boardId", "default"), ["taskId"] = Text(args, "taskId"), ["section"] = "workflow"
        });
        if (!read.Ok || Parse(read)["packet"]?["resources"] is not JsonArray listed) return null;
        var names = listed.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var name) ? name : "").Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return null;
        var resolved = await CallControl(caller, Operations.ResolveAll, new JsonObject
        {
            ["repositoryId"] = Text(args, "repositoryId"), ["machineId"] = session is not null && Text(session, "machineId").Length > 0 ? Text(session, "machineId") : Text(args, "machineId"),
            ["resourceIds"] = JsonSerializer.SerializeToNode(names)
        });
        // Without the lookup the board checks each declared name as listed, which only refuses more.
        if (!resolved.Ok) return null;
        var map = Parse(resolved)["resolved"] as JsonObject;
        var declared = new JsonObject();
        foreach (var name in names) declared[name] = map?[name] is JsonValue canonical && canonical.TryGetValue<string>(out var id) ? id : "";
        return declared;
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
        if (args["renewClaims"] is JsonArray claims)
        {
            var outcomes = new JsonArray();
            foreach (var item in claims.Take(RelayLimits.MaxClaimResources))
            {
                var renew = args.DeepClone().AsObject(); renew.Remove("requestId"); renew["resourceId"] = item!["resourceId"]?.DeepClone(); renew["generation"] = item["generation"]?.DeepClone();
                var resolved = authorization.Ok ? await Resolve(caller, renew, Text(renew, "resourceId")) : authorization;
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

    private Task<RelayReply> Resolve(RelayCaller caller, JsonObject args, string resource)
    {
        var resolve = args.DeepClone().AsObject(); resolve["resourceId"] = resource; resolve.Remove("requestId");
        return CallControl(caller, Operations.Resolve, resolve);
    }

    private Task<RelayReply> CallControl(RelayCaller caller, string operation, JsonObject args) => Call(AggregateKinds.Control, AggregateKinds.ControlKey, caller, operation, args);
    private Task<RelayReply> CallSession(RelayCaller caller, string operation, JsonObject args) => Call(AggregateKinds.Session, Text(args, "sessionId"), caller, operation, args);
    private Task<RelayReply> CallBoard(RelayCaller caller, string operation, JsonObject args)
    {
        // Only the hub names archived dependencies; a client's own archivedDependsOn is always dropped, with or without an archive store.
        if (operation == RelayTools.TaskUpdate) args.Remove("archivedDependsOn");
        return archive is not null && operation is RelayTools.TaskUpdate or RelayTools.TaskClaim or RelayTools.ActionBegin or RelayTools.ActionReport or RelayTools.SendMessage or RelayTools.ClaimRenew or RelayTools.ClaimRelease
            ? CallTaskWriteAsync(caller, operation, args)
            : Call(AggregateKinds.Board, BoardKey(args), caller, operation, args);
    }

    /// <summary>
    /// A task write, checked against the archive table, which keeps every archived ID after the board forgets it: a new task under an
    /// archived ID, or a write the board answers <c>NotFound</c> for one, gets <c>TaskArchived</c>; a workflow's <c>dependsOn</c> IDs found
    /// there are passed as <c>archivedDependsOn</c>, so they count as completed. A client cannot supply <c>archivedDependsOn</c> itself.
    /// </summary>
    private async Task<RelayReply> CallTaskWriteAsync(RelayCaller caller, string operation, JsonObject args)
    {
        var (repository, board, taskId) = (Text(args, "repositoryId"), Text(args, "boardId", "default"), Text(args, "taskId"));
        try
        {
            if (operation == RelayTools.TaskUpdate && args["workflow"] is JsonObject workflow && workflow["dependsOn"] is JsonArray dependsOn)
            {
                var ids = dependsOn.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var id) ? id : "").Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxDependencies).ToArray();
                if (ids.Length > 0 && await archive!.ArchivedIdsAsync(repository, board, ids) is { Count: > 0 } found)
                    args["archivedDependsOn"] = new JsonArray(found.Order(StringComparer.Ordinal).Select(id => (JsonNode)id).ToArray());
            }
            if (taskId.Length > 0 && operation == RelayTools.TaskUpdate && ExpectedVersionMatches(args, 0) && (await archive!.ArchivedIdsAsync(repository, board, [taskId])).Count > 0)
                return Error(TaskArchived, new { taskId, archived = true });
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or ObjectDisposedException) { return Error(Unavailable); }
        var reply = await Call(AggregateKinds.Board, BoardKey(args), caller, operation, args);
        if (reply.Code != NotFound || taskId.Length == 0) return reply;
        try { return (await archive!.ArchivedIdsAsync(repository, board, [taskId])).Count > 0 ? Error(TaskArchived, new { taskId, archived = true }) : reply; }
        catch (Exception exception) when (exception is System.Data.Common.DbException or ObjectDisposedException) { return reply; }
    }
    private Task<RelayReply> Call(string kind, string key, RelayCaller caller, string operation, JsonObject args) => CoordinationGrain.Aggregate(grains, kind, key).ExecuteAsync(caller, operation, args.ToJsonString());

    private sealed class CallTrace
    {
        public DateTimeOffset At { get; init; }
        public JsonObject? Args { get; set; }
        public JsonObject? Usage { get; set; }
        public string? SessionId { get; set; }
        public string? Role { get; set; }
        /// <summary>False for an adapter's background read (<c>attention: false</c>) or a <c>waitSeconds</c> long-poll.</summary>
        public bool ModelVisible { get; set; } = true;
        public string? KnownEtag { get; set; }
        /// <summary>The reply's tokens when compaction already counted the reply as sent, and the step it reached.</summary>
        public int? ReplyTokens { get; set; }
        public int CompactStep { get; set; }
        public bool OverBudget { get; set; }
    }

    /// <summary>Builds and queues the call's activity event. Any failure here is swallowed: telemetry must never fail a call.</summary>
    private void RecordCall(string tool, JsonElement arguments, CallTrace trace, RelayReply? reply, TimeSpan elapsed, bool canceled)
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
            var (requestText, replyText) = counter is null ? (null, null) : (raw, reply is null || trace.ReplyTokens is not null ? null : reply.DataJson);
            if (!TokenTextBudget.Shared.TryHold(requestText, replyText)) (requestText, replyText) = (null, null);
            activity!.Record(new ActivityEvent
            {
                Timestamp = trace.At, Kind = ActivityEvent.Call, RepositoryId = repository.Length <= RelayLimits.MaxIdentifierLength ? repository : "",
                SessionId = Bounded(trace.SessionId), Role = role, Tool = tool, Ok = ok,
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
    private static string? Target(string tool, JsonObject args)
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

    private static string TaskTarget(JsonObject args, string taskId) => "task:" + BoardKey(args) + "/" + Uri.EscapeDataString(taskId);

    /// <summary>A fingerprint of the status text a call wrote: task facts, notes, message and workflow; a message's text; a note's content.</summary>
    private static string? ContentHash(string tool, JsonObject args)
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

    private static string? OfferTarget(JsonObject args, RelayReply reply)
    {
        if (args["includeNextWork"] is null) return null;
        var offered = JsonNode.Parse(reply.DataJson)?["nextWork"]?["offer"]?["taskId"];
        return offered is JsonValue value && value.TryGetValue<string>(out var taskId) && taskId.Length is > 0 and <= RelayLimits.MaxIdentifierLength ? TaskTarget(args, taskId) : null;
    }

    /// <summary>A reported count, or null when absent or malformed. A bad usage report is ignored rather than failing the milestone.</summary>
    private static long? Tokens(JsonObject? usage, string key) =>
        usage?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), out var tokens) && tokens is >= 0 and <= RelayLimits.MaxReportedTokens ? tokens : null;
    private static string? Bounded(string? value, int maximum = RelayLimits.MaxIdentifierLength) => string.IsNullOrEmpty(value) || value.Length > maximum ? null : value;

    private static string BoardKey(JsonObject args) => Uri.EscapeDataString(Text(args, "repositoryId")) + "/" + Uri.EscapeDataString(Text(args, "boardId", "default"));
    private static JsonObject Parse(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
    private static JsonNode Outcome(bool ok, string code) => JsonSerializer.SerializeToNode(new { ok, code })!;
}

public static class RelayCoreServiceCollectionExtensions
{
    public static IServiceCollection AddCoCernCore(this IServiceCollection services) =>
        services.AddSingleton<RelayApplication>().AddSingleton<IRelayApplication>(provider => provider.GetRequiredService<RelayApplication>()).AddSingleton<IMachineDirectory, MachineDirectory>();
}

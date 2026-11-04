using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// The pipeline's last stage: what a routed reply picks up on its way out, in one fixed order, <see cref="Steps"/>. Each step keeps its
/// own condition and never changes the committed result; it adds to the reply, or does advisory work beside it. The order is part of the
/// behaviour: the attention offer is chosen once the reply carries everything else, so it is skipped when no room is left; the offered
/// work is raised after that offer was chosen; compaction runs on the composed reply; and a page is marked seen from the reply as sent,
/// after compaction, so a message cut from the page stays unseen.
/// </summary>
internal sealed class ReplyAddOns(Aggregates aggregates, ClaimComposer claims, StandingDelivery standing, CallGuard guard, ToolTokenStatus tokenStatus, AttentionOptions attention,
    IFullTextSearch? search, CallTelemetry? telemetry, ITokenCounter? counter, TokenOptions tokenSettings, IRelayObservationSource? observations = null)
{
    /// <summary>One reply on its way out: the call, the reply so far, and what an earlier step decided for a later one.</summary>
    internal sealed class Outgoing(RelayCall call, RelayReply reply, CallTrace? trace)
    {
        public RelayCall Call { get; } = call;
        public CallTrace? Trace { get; } = trace;
        /// <summary>The reply so far. Each step before <c>bound</c> adds to it; <c>bound</c> makes it the reply as sent.</summary>
        public RelayReply Reply { get; set; } = reply;
        /// <summary>Set by <c>attention_offer</c>: this call ran an attention boundary, whatever it offered.</summary>
        public bool RanBoundary { get; set; }
        /// <summary>Set by <c>attention_offer</c>: this <c>check_inbox</c> page is marked seen once it is sent.</summary>
        public bool MarkSeen { get; set; }
        /// <summary>Standing arbitration delays the ordinary offer until final reply fit is known.</summary>
        public bool DeferredAttention { get; set; }
    }

    internal delegate Task Step(ReplyAddOns addOns, Outgoing reply);

    /// <summary>The add-ons, in the order they run. Reordering them changes replies; a test pins this order.</summary>
    internal static IReadOnlyList<(string Name, Step Run)> Steps { get; } =
    [
        ("token_status", static (addOns, reply) => addOns.AddTokenStatus(reply)),
        ("piggyback", static (addOns, reply) => addOns.PiggybackAsync(reply)),
        ("next_work", static (addOns, reply) => addOns.OfferNextWorkAsync(reply)),
        ("standing", static (addOns, reply) => addOns.DeliverStandingAsync(reply)),
        ("attention_offer", static (addOns, reply) => addOns.OfferAttentionAsync(reply)),
        ("raise_offered_work", static (addOns, reply) => addOns.RaiseOfferedWorkAsync(reply)),
        ("close_claimed_work", static (addOns, reply) => addOns.CloseClaimedWorkAsync(reply)),
        ("related_work", static (addOns, reply) => addOns.LookUpRelatedWork(reply)),
        ("bound", static (addOns, reply) => addOns.BoundToWire(reply)),
        ("mark_seen", static (addOns, reply) => addOns.MarkSeenAsync(reply)),
    ];

    /// <summary>Runs every step on a routed reply and returns the reply as sent.</summary>
    public async Task<RelayReply> ApplyAsync(RelayCall call, RelayReply routed, CallTrace? trace)
    {
        var reply = new Outgoing(call, routed, trace);
        foreach (var (_, run) in Steps) await run(this, reply);
        return reply.Reply;
    }

    /// <summary>An administrator's <c>get_status</c>, but not a background detail read, shows what the tool definitions cost (<see cref="ToolTokenStatus"/>).</summary>
    private Task AddTokenStatus(Outgoing reply)
    {
        var call = reply.Call;
        if (reply.Reply.Ok && call.Tool == RelayTools.GetStatus && call.Caller.IsAdministrator && !call.Controls.StatusDetail) reply.Reply = tokenStatus.AddTo(reply.Reply);
        return Task.CompletedTask;
    }

    /// <summary>Optional claim renewals and inbox acknowledgments riding on another call. Their outcomes never change the committed result.</summary>
    private async Task PiggybackAsync(Outgoing reply)
    {
        if (!reply.Reply.Ok) return;
        var (caller, args, committed) = (reply.Call.Caller, reply.Call.Args, reply.Reply);
        if (args["renewClaims"] is not JsonArray && args["ackMessageIds"] is not JsonArray) return;
        var conveniences = new JsonObject();
        var authorization = await guard.VerifySessionAsync(caller, args);
        if (args["renewClaims"] is JsonArray renewals)
        {
            var outcomes = new JsonArray();
            foreach (var item in renewals.Take(RelayLimits.MaxClaimResources))
            {
                // The write has committed: a malformed entry gets its own outcome, never an error for the whole call.
                if (item is not JsonObject) { outcomes.Add(JsonSerializer.SerializeToNode(new { ok = false, code = InvalidArguments })); continue; }
                var renew = args.DeepClone().AsObject(); renew.Remove("requestId"); renew["resourceId"] = item!["resourceId"]?.DeepClone(); renew["generation"] = item["generation"]?.DeepClone();
                var resolved = authorization.Ok ? await claims.ResolveForSession(caller, renew, Parse(authorization), Text(renew, "resourceId")) : authorization;
                var result = resolved.Ok ? await aggregates.Call(AggregateKinds.Lock, Text(Parse(resolved), "resourceId"), caller, RelayTools.ClaimRenew, renew) : resolved;
                outcomes.Add(JsonSerializer.SerializeToNode(new { ok = result.Ok, code = result.Code, data = JsonNode.Parse(result.DataJson) }));
            }
            conveniences["renewals"] = outcomes;
        }
        if (args["ackMessageIds"] is JsonArray ids)
        {
            var ack = args.DeepClone().AsObject(); ack.Remove("requestId"); ack["messageIds"] = ids.DeepClone();
            var result = authorization.Ok ? await aggregates.Session(caller, RelayTools.Ack, ack) : authorization;
            conveniences["ack"] = Outcome(result.Ok, result.Code);
        }
        var data = Parse(committed); data["conveniences"] = conveniences;
        reply.Reply = Ok(data, committed.Version);
    }

    /// <summary>A <c>task_update</c> with <c>includeNextWork</c> carries an advisory next-work offer (<see cref="WithNextWorkAsync"/>).</summary>
    private async Task OfferNextWorkAsync(Outgoing reply)
    {
        var call = reply.Call;
        if (reply.Reply.Ok && call.Tool == RelayTools.TaskUpdate && call.Args["includeNextWork"]?.GetValue<bool>() == true) reply.Reply = await WithNextWorkAsync(call.Caller, call.Args, call.Session, reply.Reply);
    }

    /// <summary>
    /// A <c>standing_put</c> through this hub refreshes the board's cached standing; a call that carries standing (<see cref="StandingDelivery.Carries"/>)
    /// attaches it when it changed since the session last received it.
    /// </summary>
    private async Task DeliverStandingAsync(Outgoing reply)
    {
        var call = reply.Call;
        if (reply.Reply.Ok && call.Tool == RelayTools.StandingPut) standing.Wrote(call.Args, reply.Reply.Version);
        if (reply.Reply.Ok && call.Session is not null && StandingDelivery.Carries(call.Tool)) reply.Reply = await standing.AttachIfChangedAsync(call.Caller, call.Args, call.Session, reply.Reply);
    }

    /// <summary>
    /// A boundary call, from a session whose adapter wants offers, attaches at most one attention offer (<see cref="WithAttentionAsync"/>).
    /// It also decides whether a <c>check_inbox</c> page is marked seen once sent: one that ran a boundary, or that its reader reports seen.
    /// </summary>
    private async Task OfferAttentionAsync(Outgoing reply)
    {
        var (call, controls) = (reply.Call, reply.Call.Controls);
        var offers = reply.Reply.Ok && call.Session is not null && controls.OfferWanted && attention.Enabled && IsBoundary(call.Tool);
        reply.DeferredAttention = offers && controls.ModelVisible && controls.PresentStanding;
        if (offers && !reply.DeferredAttention) reply.Reply = await WithAttentionAsync(call.Caller, call.Args, call.Tool, reply.Reply);
        reply.RanBoundary = offers && !reply.DeferredAttention;
        // A page reported as seen is marked from the reply as sent, after compaction, so a message cut from the page stays unseen.
        reply.MarkSeen = reply.Reply.Ok && call.Session is not null && attention.Enabled && call.Tool == RelayTools.CheckInbox && (offers || controls.PageSeen || controls.SeenIds.Length > 0);
    }

    /// <summary>The next-work offer a <c>task_update</c> reply carries becomes a next_work item too, raised after the reply's own attention offer was chosen.</summary>
    private async Task RaiseOfferedWorkAsync(Outgoing reply)
    {
        var call = reply.Call;
        if (reply.Reply.Ok && call.Tool == RelayTools.TaskUpdate && attention.Enabled) await RaiseOfferedWorkAsync(call.Caller, call.Args, reply.Reply);
    }

    /// <summary>The claimer's own next_work item for the task (from a next-work offer) closes with the claim.</summary>
    private async Task CloseClaimedWorkAsync(Outgoing reply)
    {
        var call = reply.Call;
        if (reply.Reply.Ok && call.Tool == RelayTools.TaskClaim && attention.Enabled) await CloseClaimedWorkAsync(call.Caller, call.Args, reply.Reply);
    }

    /// <summary>Related work is looked up after the reply, off the claim's path; the items are offered from the next boundary.</summary>
    private Task LookUpRelatedWork(Outgoing reply)
    {
        var call = reply.Call;
        if (reply.Reply.Ok && call.Tool == RelayTools.TaskClaim && attention.Enabled && search is not null)
        {
            var claimed = reply.Reply;
            Advisory.Run(() => RaiseRelatedWorkAsync(call.Caller, call.Args, claimed));
        }
        return Task.CompletedTask;
    }

    /// <summary>The composed reply, bounded to the wire: from here on it is the reply as sent.</summary>
    private async Task BoundToWire(Outgoing reply)
    {
        reply.Reply = Bound(reply.Call.Definition, reply.Call.Args, reply.Reply, reply.Trace);
        if (!reply.DeferredAttention) return;
        var call = reply.Call;
        var committed = reply.Reply;
        try
        {
            // An urgent item wins even when its own attachment cannot fit this reply.
            if (!await HasUrgentAsync(call))
            {
                var correlation = Text(call.Args, "requestId") is { Length: > 0 } request ? call.Tool + ":" + request : "";
                var candidates = observations is null ? null : await observations.ReadPendingStandingAsync(Text(call.Args, "repositoryId"), call.Caller.PrincipalId, Text(call.Args, "sessionId"), correlation);
                foreach (var candidate in candidates ?? Array.Empty<StandingPresentation>())
                {
                    var effect = JsonNode.Parse(candidate.SummaryJson)!.AsObject();
                    var data = Parse(committed); data["attention"] = effect.DeepClone();
                    // Compact before reserving, and require the entire escaped effect to survive every actual reply budget.
                    var prepared = Bound(call.Definition, call.Args, Ok(data, committed.Version), null);
                    if (!FitsWire(prepared) || !JsonNode.DeepEquals(Parse(prepared)["attention"], effect)
                        || Encoding.UTF8.GetByteCount(candidate.SummaryJson) > RelayLimits.MaxAttentionOfferBytes
                        || counter is not null && tokenSettings.Enforces(call.Tool) && counter.Count(prepared.DataJson) > call.Definition.TokenBudget) continue;
                    if (!reply.RanBoundary)
                    {
                        var urgent = await WithAttentionAsync(call.Caller, call.Args, call.Tool, committed, urgentOnly: true);
                        reply.RanBoundary = true;
                        if (Parse(urgent)["attention"] is JsonObject)
                        {
                            reply.Reply = Bound(call.Definition, call.Args, urgent, reply.Trace);
                            return;
                        }
                    }
                    if (!(await guard.VerifySessionAsync(call.Caller, call.Args)).Ok) break;
                    // Keep transport correlation separate from requestId: generic replay runs before the board's pending/effect check.
                    var claim = new JsonObject
                    {
                        ["repositoryId"] = Text(call.Args, "repositoryId"), ["boardId"] = candidate.BoardId,
                        ["sessionId"] = Text(call.Args, "sessionId"), ["proposalId"] = candidate.ProposalId,
                        ["presentationRequest"] = correlation, ["expectedEffect"] = effect.DeepClone()
                    };
                    var reserved = await aggregates.Board(call.Caller, Operations.PresentStanding, claim);
                    if (!reserved.Ok || Parse(reserved)["attention"] is not JsonObject shown || shown.ToJsonString() != candidate.SummaryJson) continue;
                    var final = Parse(prepared); final["attention"] = shown.DeepClone();
                    reply.Reply = Ok(final, committed.Version);
                    // No compaction follows the claim. MarkSeen reads this exact final page.
                    if (reply.Trace is not null && counter is not null && tokenSettings.Enforces(call.Tool)) reply.Trace.ReplyTokens = counter.Count(reply.Reply.DataJson);
                    telemetry?.RecordOutcome(call.Args, AttentionKinds.StandingProposal, "shown", candidate.ProposalId);
                    return;
                }
            }
        }
        catch (Exception) { /* The ordinary call stands; a lost presentation reply leaves its dashboard reservation intact. */ }
        if (!reply.RanBoundary)
        {
            reply.Reply = Bound(call.Definition, call.Args, await WithAttentionAsync(call.Caller, call.Args, call.Tool, committed), reply.Trace);
            reply.RanBoundary = true;
        }
    }

    private async Task<bool> HasUrgentAsync(RelayCall call)
    {
        var args = new AttentionOffer(Text(call.Args, "repositoryId"), Text(call.Args, "sessionId"), Text(call.Args, "sessionToken"))
        {
            FocusTaskId = Text(call.Args, "taskId"), FocusBoardId = Text(call.Args, "boardId", "default"), SnoozeBoundaries = attention.SnoozeBoundaries
        }.ToJson();
        args["urgentOnly"] = true; args["preview"] = true;
        var preview = await aggregates.Session(call.Caller, AttentionOffer.Operation, args);
        return !preview.Ok || Parse(preview)["attention"] is JsonObject;
    }

    /// <summary>A <c>check_inbox</c> page decided seen closes the info items of the messages it shows, as sent.</summary>
    private async Task MarkSeenAsync(Outgoing reply)
    {
        var (call, controls) = (reply.Call, reply.Call.Controls);
        if (reply.MarkSeen && reply.Reply.Ok) await MarkSeenAsync(call.Caller, call.Args, reply.Reply, controls.PageSeen || reply.RanBoundary, controls.SeenIds);
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

    /// <summary>Calls that carry an attention offer. Background readers send <c>attention: false</c>, so no unseen offer is marked shown.</summary>
    private static bool IsBoundary(string tool) => tool is RelayTools.TaskUpdate or RelayTools.ActionReport or RelayTools.TaskClaim or RelayTools.CheckInbox;

    /// <summary>
    /// Runs one boundary at the caller's session and attaches at most one compact offer. The committed result never changes, and the
    /// boundary is skipped when the reply has no room left for an offer, so an item is marked shown only when its offer is delivered.
    /// </summary>
    private async Task<RelayReply> WithAttentionAsync(RelayCaller caller, JsonObject args, string tool, RelayReply committed, bool urgentOnly = false)
    {
        try
        {
            var data = Parse(committed);
            // A model-visible inbox page shows its messages, so their info items are not offered. They close after compaction, from
            // the page as sent, so a message cut from the page keeps its item.
            var onPage = tool == RelayTools.CheckInbox && data["items"] is JsonArray page
                ? page.OfType<JsonObject>().Select(message => Text(message, "messageId")).Where(id => id.Length > 0).ToArray() : null;
            data["attention"] = new string('x', RelayLimits.MaxAttentionOfferBytes);
            if (!FitsWire(Ok(data, long.MaxValue))) return committed;
            var request = new AttentionOffer(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "sessionToken"))
            {
                FocusTaskId = Text(args, "taskId"), FocusBoardId = Text(args, "boardId", "default"), SnoozeBoundaries = attention.SnoozeBoundaries,
                PageMessageIds = onPage is { Length: > 0 } ? onPage : null
            };
            var offerArgs = request.ToJson();
            if (urgentOnly) offerArgs["urgentOnly"] = true;
            var offer = await aggregates.Session(caller, AttentionOffer.Operation, offerArgs);
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
            var seen = new AttentionSeen(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "sessionToken"),
                ids.Distinct(StringComparer.Ordinal).Take(RelayLimits.MaxAcknowledgedMessages + RelayLimits.MaxPageSize).ToArray());
            await aggregates.Session(caller, AttentionSeen.Operation, seen.ToJson());
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
            var priority = offer["workflow"] is JsonObject workflow && workflow["priority"] is JsonValue ? AttentionWeights.PriorityLevel(Number(workflow, "priority")) : null;
            await Suggest(caller, args, new AttentionContent
            {
                Kind = AttentionKinds.NextWork, TaskId = taskId, BoardId = Text(args, "boardId", "default"), Priority = priority, Note = "offered as your next work; claim it with task_claim",
                Revision = Math.Max(1, Number(offer, "snapshotRevision")), ExpiryDays = RelayLimits.OfferedWorkExpiryDays
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
            var close = new AttentionClaimed(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "boardId", "default"), Text(args, "taskId"), Number(claim, "changeRevision"));
            await aggregates.Session(caller, AttentionClaimed.Operation, close.ToJson());
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
            var read = await aggregates.Board(caller, RelayTools.TaskGet, new JsonObject { ["repositoryId"] = Text(args, "repositoryId"), ["boardId"] = Text(args, "boardId", "default"), ["taskId"] = taskId });
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
                await Suggest(caller, args, new AttentionContent
                {
                    Kind = kind, BoardId = board, Subject = taskId, Note = (kind == AttentionKinds.DuplicateWarning ? "may duplicate " : "related to ") + taskId,
                    Refs = ids, Revision = Number(claim, "changeRevision"), ExpiryDays = attention.ItemExpiryDays
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
            var status = await aggregates.Control(caller, RelayTools.GetStatus, args);
            if (!status.Ok) next = Outcome(false, status.Code);
            else if (Parse(status)["paused"]?.GetValue<bool>() == true) next = Outcome(true, Paused);
            else
            {
                var current = args["taskId"] is JsonValue value && value.TryGetValue<string>(out var taskId) ? taskId : null;
                var read = new NextWorkRead(Text(args, "repositoryId"), Aggregates.BoardIdOf(args), Text(args, "sessionId"), current, Limit: RelayLimits.NextWorkCandidates);
                var offered = await aggregates.Board(caller, NextWorkRead.Operation, read.ToJson());
                var offer = offered.Ok ? Parse(offered) : null;
                var busy = offer?["candidates"] is JsonArray candidates ? await BusyResourcesAsync(caller, args, session, candidates) : [];
                if (busy.Count > 0)
                {
                    // Ask again so that, within equal priority, a task whose listed resources are all free is offered first.
                    offered = await aggregates.Board(caller, NextWorkRead.Operation, (read with { Limit = 0, BusyResources = busy }).ToJson());
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
            var repository = Text(args, "repositoryId");
            var resolve = new ResourceResolveAll(repository, listed, session is null ? "" : Text(session, "machineId"));
            var resolved = await aggregates.Request(AggregateKinds.ControlKey, caller, resolve);
            if (!resolved.Ok || Parse(resolved)["resolved"] is not JsonObject canonical) return [];
            var lookup = new ClaimStateQuery(repository, Text(args, "sessionId"));
            async Task<bool> IsBusy(string resource)
            {
                if (canonical[resource] is not JsonValue id) return false;
                var claim = await aggregates.Request(id.GetValue<string>(), caller, lookup);
                return claim.Ok && Parse(claim)["busy"]?.GetValue<bool>() == true;
            }
            var busy = await Task.WhenAll(listed.Select(IsBusy));
            return listed.Where((_, index) => busy[index]).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Raises the hub's own <paramref name="item"/> at the caller's session (<see cref="AttentionSuggestion"/>).</summary>
    private Task<RelayReply> Suggest(RelayCaller caller, JsonObject args, AttentionContent item) =>
        aggregates.Session(caller, AttentionSuggestion.Operation, new AttentionSuggestion(Text(args, "repositoryId"), Text(args, "sessionId"), item).ToJson());

    private static JsonObject Parse(RelayReply reply) => Aggregates.Parse(reply);
    private static JsonNode Outcome(bool ok, string code) => JsonSerializer.SerializeToNode(new { ok, code })!;
}

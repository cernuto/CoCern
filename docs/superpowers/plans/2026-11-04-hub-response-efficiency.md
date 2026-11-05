# Hub response compaction

## Current state

`codex/hub-response-compaction-main` contains the implementation and tests, includes main through `d3fd48d`, and is not deployed. The original 24-case red run established the missing behavior; the expanded focused suite passed 27/27 at `88ceb44`. The first integrated affected run completed 460 cases with 456 passes and four fixture failures: exact pipeline order, explicit full bindings and continuation tags. These fixtures are corrected. The subsequent review-boundary run passed 45/46, including scoped status/list validators and oversized worker/task piece reassembly; its only failure was the synthetic board fixture omitting the framework's normal request map before a strict state comparison. That fixture is corrected in `7592ccb`; the corrected affected run and narrow re-review remain pending while the other worker owns the timing window.

## Worker and task lists

The default worker list omits binding blobs and retains identity, binding version and presence. It omits a row only when every recorded activity timestamp is older than 24 hours and there is no work or mail mark. Old and malformed marks, unreadable timestamps and missing evidence keep the row. Labels never establish eligibility. `includeStale:true` returns the previous full directory rows, and the internal machine directory explicitly uses this mode. Listing never closes a session. Directory marks are eventually consistent observations, so they do not prove that the underlying inbox or action set is empty.

Only board-list reads compact tasks. A task satisfying the existing `TaskArchiving.Archivable` predicate becomes `{taskId,state:"completed"}`; live claims, unresolved actions and workflowless tasks keep their relevant fields. Repeated `sections` are omitted from compact rows. `openOnly:true` excludes only safely collapsible tasks, while `includeDetails:true` retains the previous full summaries. Individual detail, delta, archive and the shared summary stay unchanged.

Compact worker and open-only task lists emit a scoped membership `snapshotTag`. Continuations echo that tag with the numeric cursor; a missing tag on continuation or a changed membership returns `ListRestartRequired`. Clock-only idle and claim-expiry transitions can change membership. Full list cursor and piece behavior remains available. Metadata, pages and pieces must fit existing reply limits together.

## Conditional status

`statusTag` is a separate opt-in contract from the adapter's existing conditional-read etag. An empty string requests a full response with a tag. A matching tag may return the normal ok envelope with `{unchanged:true,statusTag}`. Every call still authorizes the caller and session, reads current status and composes applicable add-ons. No privileged payload is cached and no aggregate reads are skipped.

The validator covers freshly returned session/control/attention data, caller identity and repository privileges, repository, board, hub incarnation, attention-detail and hold selection, effective attention settings and reply version. Pause, cancellation targets, inbox changes, actionable/top/wake changes and time-driven hold, snooze, freshness or expiry changes must invalidate it. Full replies remain the fallback for history, piggyback writes, missing required status fields, uncertainty, or a full response that cannot retain its validator within the existing wire and token limits. Ordinary callers remain unchanged. Caller-side adoption belongs to the Claude worker.

## What the model reads

Counters at the hub see the reply it sends, not what reaches a chat. `ModelReply` in the adapter runs last on the way out of the MCP tool, so nothing the adapter learns from a reply is affected. It writes JSON without HTML-safe escapes, drops the `content` a `context_put` echoes back, and in `check_inbox` drops each item's `repositoryId` and null `replyTo`, whole-second `sentAt`, and a null `nextCursor` or false `truncated`. Replayed over one long chat's own replies (84 `check_inbox`, 5 `context_put`), a script's estimate is 9.9% and 88.7% fewer characters. That is one chat, not a production measurement, and these savings will not appear in `relay_tokens` or `noisiest_tools`. A repeated read of unacknowledged messages still costs their text: 25 of 143 items in that chat were repeats, and leaving them out would risk hiding mail from a chat whose context was compacted.

## Verification and file boundaries

The production edit set is `ToolSchemas.cs`, `ControlAggregate.cs`, `TaskBoardAggregate.cs`, `DiscoveryGrain.cs`, `RelayRequestGuard.cs` and `ReplyAddOns.cs`. Tests are in `HubResponseCompactionTests.cs` and affected existing suites. The new public event-wait work can add separate schema cases and helpers; it must preserve the existing worker, task and status cases when integrating.

Remaining checks include full binding recovery, safe retention, byte and piece bounds, clock-only membership restarts, unchanged detail/delta/archive contracts, fresh authorization, scoped validators, pause/cancel/inbox and attention transitions, history/piggyback fallback, pipeline ordering and catalog caps. Broader affected suites and the project gate remain required before completion. No cap increase is part of this feature.

Efficiency comparisons use the same authorized fixture for compact and full responses, measuring serialized UTF-8 bytes and the production o200k token counter separately. Historical cohorts were response bytes: background status 10,916,527 across 22,040 calls; inbox with unknown origin 9,690,383 across 6,361; workers with unknown origin 4,038,039 across 533; task reads with unknown origin 2,424,816 across 581. These figures are not token counts or measured savings. Unknown origin does not identify a provider, and unmatched cumulative snapshots cannot establish improvement.

The `88ceb44` focused run measured full/compact pairs on the same fixtures: status 519/97 bytes and 172/43 tokens; workers 4,636/3,054 bytes and 1,561/1,140 tokens; board lists 2,055/1,418 bytes and 571/415 tokens. These are fixture measurements, not estimates of historical production savings. Random fixture identifiers can change token totals between runs.

The separate call-origin telemetry work is already integrated into the relevant release batches and is not part of this compaction edit set. It grants no authority and cannot classify historical calls retrospectively.

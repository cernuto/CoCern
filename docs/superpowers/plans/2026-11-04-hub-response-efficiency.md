# Hub response compaction

## Current state

`codex/hub-response-compaction-main` contains the prepared tests and implementation source. It is WIP and is not deployed. The first 24-case run produced 22 failures and two compatibility passes. After implementing the worker and task lists and replacing missing-field null dereferences with explicit assertions, the next run passed 22 cases; only the two status-validator cases remained red. The status implementation and additional HTTP and wire-measurement cases are now authored but unverified. Runtime work is paused while the exclusive Batch E full gate runs.

## Worker and task lists

The default worker list omits binding blobs and retains identity, binding version and presence. It omits a row only when every recorded activity timestamp is older than 24 hours and there is no work or mail mark. Old and malformed marks, unreadable timestamps and missing evidence keep the row. Labels never establish eligibility. `includeStale:true` returns the previous full directory rows, and the internal machine directory explicitly uses this mode. Listing never closes a session. Directory marks are eventually consistent observations, so they do not prove that the underlying inbox or action set is empty.

Only board-list reads compact tasks. A task satisfying the existing `TaskArchiving.Archivable` predicate becomes `{taskId,state:"completed"}`; live claims, unresolved actions and workflowless tasks keep their relevant fields. Repeated `sections` are omitted from compact rows. `openOnly:true` excludes only safely collapsible tasks, while `includeDetails:true` retains the previous full summaries. Individual detail, delta, archive and the shared summary stay unchanged.

Compact worker and open-only task lists emit a scoped membership `snapshotTag`. Continuations echo that tag with the numeric cursor; a missing tag on continuation or a changed membership returns `ListRestartRequired`. Clock-only idle and claim-expiry transitions can change membership. Full list cursor and piece behavior remains available. Metadata, pages and pieces must fit existing reply limits together.

## Conditional status

`statusTag` is a separate opt-in contract from the adapter's existing conditional-read etag. An empty string requests a full response with a tag. A matching tag may return the normal ok envelope with `{unchanged:true,statusTag}`. Every call still authorizes the caller and session, reads current status and composes applicable add-ons. No privileged payload is cached and no aggregate reads are skipped.

The validator covers freshly returned session/control/attention data, caller identity and repository privileges, repository, board, hub incarnation, attention-detail and hold selection, effective attention settings and reply version. Pause, cancellation targets, inbox changes, actionable/top/wake changes and time-driven hold, snooze, freshness or expiry changes must invalidate it. Full replies remain the fallback for history, piggyback writes, missing required status fields, uncertainty, or a full response that cannot retain its validator within the existing wire and token limits. Ordinary callers remain unchanged. Caller-side adoption belongs to the Claude worker.

## Verification and file boundaries

The production edit set is `ToolSchemas.cs`, `ControlAggregate.cs`, `TaskBoardAggregate.cs`, `DiscoveryGrain.cs`, `RelayRequestGuard.cs` and `ReplyAddOns.cs`. Tests are in `HubResponseCompactionTests.cs` and affected existing suites. The new public event-wait work can add separate schema cases and helpers; it must preserve the existing worker, task and status cases when integrating.

Remaining checks include full binding recovery, safe retention, byte and piece bounds, clock-only membership restarts, unchanged detail/delta/archive contracts, fresh authorization, scoped validators, pause/cancel/inbox and attention transitions, history/piggyback fallback, pipeline ordering and catalog caps. Broader affected suites and the project gate remain required before completion. No cap increase is part of this feature.

Efficiency comparisons use the same authorized fixture for compact and full responses, measuring serialized UTF-8 bytes and the production o200k token counter separately. Historical cohorts were response bytes: background status 10,916,527 across 22,040 calls; inbox with unknown origin 9,690,383 across 6,361; workers with unknown origin 4,038,039 across 533; task reads with unknown origin 2,424,816 across 581. These figures are not token counts or measured savings. Unknown origin does not identify a provider, and unmatched cumulative snapshots cannot establish improvement.

The separate call-origin telemetry work is already integrated into the relevant release batches and is not part of this compaction edit set. It grants no authority and cannot classify historical calls retrospectively.

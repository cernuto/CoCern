# Live dashboard insights — design and execution plan

Paul requested retained-context views, task-brief/excerpt savings, watcher polling and saved-baseline comparisons with real-time updates. Extend the existing Blazor dashboard and authorized host APIs; retain current presentation patterns and the existing two-second viewer-driven SignalR stream. Existing hourly Analytics remains explicitly hourly.

## Contracts and behavior

Add Retained context and Efficiency navigation destinations. Active views refresh bounded reads at most once every five seconds, driven by fresh dashboard snapshots; no reads while disconnected or after disposal, no overlapping requests, and generation/cancellation checks prevent stale responses overwriting a new selection. Show collection/sample times, unavailable/stale/empty states and sample counts. Context body reads are explicit, escaped exact text with IDs, generation/version and part preserved; no bodies are pushed into broad snapshots.

Retained context APIs list authorized repository collections, manifests, search results and exact entry parts using the existing catalog/grain/resource rules. Revalidate repository permission against the authenticated dashboard viewer on every request, preserve existing token/credential boundaries, bound inputs/results and cancel superseded reads. Source text is never rendered as executable markup. No save/delete controls are needed for this read-only view.

Efficiency reports a rolling hour of real activity with labeled scope (hub-wide or a selected repository), watcher calls/session-hour, reply sizes, measured cadence distributions and retained-history/database summaries where available. Capture task-brief and focused-document before/after bytes and relay token estimates from the same source version during actual reads; retain sample counts and explicitly identify estimates. Missing measurements stay unknown. Do not synthesize savings from unrelated historical calls or expose private content in telemetry.

Use only sanitized aggregate values from the frozen November 10 baseline and preserve its capture timestamp, one-hour duration, hub-wide scope and original evidence SHA-256. Ship the aggregate baseline with the host; do not depend on ignored workstation artifacts at runtime. Repository-specific selections must show that the hub-wide baseline is not comparable. Show normalized rates alongside workload/session counts and guard zero denominators. Historical rollups must not be mistaken for fewer requests.

## Bounded implementation ownership

1. Retained-context backend agent owns new contracts and authorized host read service/routes. Publish DTO/endpoint details early. Avoid shared-file edits until coordinated with root.
2. Efficiency backend agent owns new contracts, bounded store queries, same-source measurement telemetry, baseline loading and metrics service/routes. Publish DTO/endpoint details early. Preserve existing calls/model-visible reply shapes and retention. No timers when views are absent.
3. UI agent owns new Razor components, navigation integration and scoped styling, coordinating against backend DTO contracts. Existing dashboard connection supplies refresh pulses.
4. Root owns integration/registration, docs, independent review and verification; resolve shared edits explicitly.

## Verification milestone

Follow AGENTS.md: no TDD, no per-batch runs, no broad gates. Add tests only for an actual authorization/replay/data-loss boundary or real regression. Before the single milestone run, map changed production symbols through Roslyn find_tests_for_symbol/analyze_change_impact and run only exact covering methods via the repository runner. Build into isolated output only to produce the browser preview binaries. Confirm newly added Razor members via search_symbols before trusting diagnostics. Use the built-in browser for navigation, measured/unknown states, exact context rendering and observed automatic updates. Review cancellation, repository isolation, baseline scope, weighted counts and retained data fences from source.

## Decisions

The prior conversation supplies the feature scope and explicit implementation authority. Repository manager instructions choose bounded delegated implementation. Planning skill defaults for TDD, per-task tests and an extra execution-approval stop are superseded by Paul's no-TDD/milestone rules and current instruction to implement. Keep the live deployed host and real database untouched during development; use isolated preview storage. Deployment and merging are separate from this implementation unless Paul explicitly requests them.

## Integration ledger

- Dashboard viewer reads resolve the exact currently enrolled administrator through DashboardSecurity.Caller, retaining existing expiry/token-rotation checks.
- Projection observations are process-local and body-free. Task briefs measure already-produced normal recent-task JSON and selected brief JSON bytes; token savings remain unknown. Focused documents carry exact-version source size/token counts internally and record returned text sizes, without a second source read.
- Source review fixed inactive repository discovery with an authenticated bounded repository inventory, and suppresses baseline comparisons if activity loss makes completeness unknown.
- Paul added a caller-owned label setter for every ordinary/native/chat catalog during implementation. A bounded agent owns this addition and checks that native presence refresh cannot erase the explicit label. Current client tool caches may need reconnect after deployment; no client settings or native permission changes are part of this work.
- Verification selection will use the two new retained-context authorization/version methods, the existing changed DashboardSecurity.IsValid token-rotation method, and only label methods selected by Roslyn. TaskBoardAggregate.Brief has no tests returned at maximum semantic walk depth and analyze_change_impact, so no unrelated task-brief tests are scheduled; live isolated reads will inspect its measured-byte outcome.

- Paul authorized README simplification followed by merge, push and cleanup. New README/reference text now focuses on features, freshness, own-chat naming and comparison limits.
- Label source review found a genuine classic-handoff open replay resetting local label/version; the shared owned-session lock now preserves matching cached state and the existing private-handle regression includes that sequence.
- A fresh short activity history also suppresses baseline changes until a full hour is represented; unavailable activity retains the prior good display instead of rendering unknown counts as zero.

## Verification results

- Isolated builds at `3570902` and `d42d186` succeeded; eight exact Roslyn-selected methods passed. Receipts and the aggregate browser record are in `artifacts/live-dashboard-insights/verification-summary.json`. No unrelated tests ran.
- The built-in browser confirmed automatic refresh, same-section excerpt measurement, watcher fixtures, short-history comparison limits, exact retained text pinned through a new revision, search, inactive repositories and the renamed chat. These are disposable preview fixtures, not production savings.
- Source review found and resolved repository inventory, incomplete baseline comparisons, classic label replay and section-size measurement issues. README and reference text were simplified.
- Paul’s merge/push/cleanup request supersedes the branch skill’s choice menu; repository rules supersede its suite reruns. The running deployed host and real database remain unchanged.

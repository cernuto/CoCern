# Efficiency Improvements Implementation Plan

> **For agentic workers:** Use bounded subagents for separate components; root owns integration and verification. Track checkable steps below.

**Goal:** Reduce unnecessary coordination output by guiding focused reads, exposing all open chats through one directory, and separating background polling from model-facing tool traffic in analytics.

**Architecture:** Extend existing guidance, directory and activity reporting paths. Retain the existing tools, ETags, permissions, session handles, wake routes, task claims and four-second inbox wait. Registered chat identity and direct-wake reachability remain separate facts. Preserve older analytics data as unknown where its origin was not recorded.

**Tech Stack:** Existing C#/.NET, Orleans state, SQLite activity log, DuckDB analytics and Blazor dashboard; no new dependencies.

**Spec:** The user's approved Noisiest tools review and explicit instruction to implement those efficiency improvements, work with Claude and the other Codex session, keep it simple, then commit, merge and push the completed work.

## Global Constraints

- Use both Roslyn CodeLens and Visual Studio MCP for C#; no semantic shell searches or builds for diagnostics.
- Preserve existing uncommitted traffic, wake and polling changes. Never build into outputs used by a running application.
- No new tools, hidden ETags, forced usage quotas, credential changes or invented reachability.
- Tests use isolated output under ignored artifacts paths; root alone restarts the shared host, commits and pushes.

## Review Focus

- A hub-local session without a route must appear once and must not be marked reachable.
- Closed sessions and sessions outside the authorized repository must not appear.
- Old analytics rows must remain unknown rather than silently becoming model or background calls.
- Explicit model-visible reads with attention suppressed must not be mislabeled merely because attention is false.
- Guidance must preserve complete task claims, checkpoint paging and authoritative inbox handling while avoiding repeated shared text.

## Task 1: Focused-read guidance

Files: AdapterInstructions.cs, RelayToolCatalog.cs descriptions, existing SessionGuidanceTests and TokenEfficiencyTests where needed.

- [x] Prefer search then a scoped task_get when locating tasks, sections/ETags/deltas for rereads, chat_directory for chat discovery, and worker_list for worker state.
- [x] Preserve includeNextWork, atomic reply acknowledgments, explicit cache ownership and task-claim requirements.
- [x] Replace redundant wording and verify the existing guidance/token contracts with no new tools or per-tool boilerplate.

## Task 2: Complete chat directory

Files: DiscoveryGrain.cs / MessageTools.cs only as required, existing DiscoveryGrainTests and relevant directory tests.

- [x] Add failing tests for hub-local registered sessions, route enrichment/deduplication, closed sessions, unavailable directory and repository isolation.
- [x] Merge bounded authorized worker identity with announced routes using repository plus session ID. Keep route details when present and reachable=false when absent.
- [x] Preserve ETags, bounded results and conservative failure behavior; run the focused discovery tests.

## Task 3: Separate background traffic in analytics

Files: existing call controls, adapter background-reader paths, activity event/storage/rollup/view paths, named Noisiest tools query and dashboard projection, plus focused analytics tests.

- [x] Trace origin at the actual background-reader boundary, not from attention opt-out alone. Use the smallest reported telemetry marker needed; never use it as authorization.
- [x] Add backward-compatible nullable activity origin, preserving historical unknowns and aggregate totals through rollups and old snapshots.
- [x] Expose model-facing, background and unknown rows or categories in the existing Noisiest tools report without a new report/tool or dashboard redesign.
- [x] Test a normal model call, background watcher/poll, attention-suppressed model read, older database migration and aggregate reconciliation.

## Integration

- [x] Root reviews each component, runs focused tests and compiler diagnostics, then deploys actual binaries with the shared host stopped.
- [ ] Verify all three chats appear once, ETags return unchanged, guidance refreshes through the development adapter, and analytics categories reconcile to total calls/bytes.
- [ ] Use the integrated browser for dashboard verification and ask the existing remote Codex to validate directory/conditional reads across PCs.
- [ ] Inspect untracked design documents and repository changes; exclude temporary attachments and credentials. Commit completed source/docs/tests, merge relevant branch work, push main, and have the second PC confirm the published SHA can be fetched.

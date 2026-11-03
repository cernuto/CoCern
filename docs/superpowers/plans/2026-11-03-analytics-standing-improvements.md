# Analytics and Standing Improvements Implementation Plan

> **For agentic workers:** Use bounded subagents for the independent slices below; manager retains integration, review and runtime verification.

**Goal:** Make analytics economical and actionable, make Standing proposals manageable from the dashboard, and explain why pending items do not govern work.

**Architecture:** Extend existing analytics_query and standing_put rather than introducing duplicate tools or permissions. Reuse shared domain validation, revision delivery, viewer authorization and response budgets. Preserve current callers; diagnostic visibility never authorizes a request.

**Tech Stack:** .NET 10, Orleans, DuckDB/SQLite, Blazor WebAssembly, MCP.

**Spec:** docs/audits/2026-11-03-dashboard-analytics.md plus Paul's explicit request to implement and review Standing.

## Constraints and ownership

- Both Roslyn CodeLens and Visual Studio MCP on CoCern.slnx are mandatory for every C# worker; use semantic inspection/diagnostics and verify Razor generated members after changes.
- Root Codex retains wake/inbox edits, host operations and its ongoing files. No overlapping edits to its request guard/router/session files without an explicit handoff.
- Standing worker owns StandingView and dedicated management endpoints/tests; analytics API/profile worker owns AnalyticsTool, compact analytics schema and adapter profile; dashboard worker owns AnalyticsView/LogsView and dedicated presentation helpers; correctness worker owns NamedQueries/Views.sql and new regression tests.
- Parent owns query registry additions, shared DTO integration, snapshot-boundary investigation, documentation, independent review and browser validation. Ask for file ownership before expanding a slice.
- All tests build into isolated artifacts paths. No worker restarts or publishes the shared hub. Live grants are never auto-approved; client permissions remain authoritative.

## Tasks

- [x] Standing: explain active instructions versus pending proposals and expiring grants; add same-origin authenticated Approve/Dismiss/Remove controls using existing domain APIs, revision checks and history. Add tests for authorization, stale revisions and proposal decisions.
- [x] Analytics API: selected-report batch with consistent snapshot, bounded serialized output, explicit partial errors and conditional etag reads. Preserve single named query and SQL behavior.
- [x] Diagnostics visibility: add a read-only diagnostics profile exposing existing worker tools plus analytics, retaining existing hub admin checks and current profile compatibility.
- [x] Dashboard: top-ten noisy tools and origin filter, snapshot age and clear units; useful log search/routine-event filtering; keep numeric equivalents and responsive layout.
- [x] Metrics/correctness: distinguish relay tokens from reported model usage, expose coverage where measurable; canonical retry identity before period filtering; event-time-or-unknown conflict attribution; regression evidence for retention gaps rather than fabricated history.
- [x] Review shared participant projection and consolidation impact. Retain clear tool interfaces unless measured schema and execution costs improve; do not merge all operations into a generic tool.
- [x] Independently review changes, run combined relevant tests from isolated outputs, test the disposable candidate dashboard via the built-in browser before coordinated deployment, measure schema/response savings and update the audit status.

## Standing diagnosis at start

The default board has revision 0, no active instructions or grants, and three unapproved proposals from November 2. Proposals do not expire merely from age or because the proposing session closes. standing_put.remove can remove them, while approve activates a proposal. The dashboard currently displays proposals without any decision controls. Approved instructions already deliver on session open and later boundary replies only when the board revision changes, so the delivery machinery exists but has no approved content to deliver. Grants additionally require a matching grant: authorityRef and scope/expiry checks; they never override client permission rules.

## Review focus

Check proposal-only decisions whose standing revision remains unchanged; duplicate/stale decisions must be explicit. Enforce viewer authorization, same-origin transport and expected revision on dashboard writes. Never activate expired grants. Batch results must not mix snapshots, exceed 8 KiB, hide errors or reuse etags across different selections/repositories. Diagnostics profile must not advertise admin mutations or permit worker-token analytics. Preserve model/background/unknown distinctions and avoid calling relay bytes model context. Unknown history remains unknown.

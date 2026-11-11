# Database Efficiency Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for implementation, with the repository's no-TDD and selected-test rules taking precedence over skill defaults.

**Goal:** Implement all four optimizations approved by Paul after the November 10 live database review, starting with background status traffic and compact replies.

**Architecture:** Keep notification signals advisory and authenticate every reconciliation. Coalesce dirty session IDs while preserving target-change pulses, reconnect recovery, periodic full reconciliation, and the final pre-dispatch control read. Give conditional background status a summary projection; bound replay housekeeping at successful mutation boundaries; count the actual attention offer rather than its enclosing status summary.

**Tech Stack:** Existing C#/.NET, Orleans, SignalR, SQLite, and xUnit; no new dependencies or schema migration.

**Spec:** Paul's instruction in this chat to implement the four findings in `.artifacts/database-efficiency-review/report.md`.

## Global Constraints

- Use Roslyn CodeLens for semantic inspection, impact, test selection, and diagnostics, and Visual Studio MCP for IDE state. Visual Studio currently targets M3TS-SNX and must not be switched or interrupted.
- Preserve fresh authorization, pause/cancellation decisions, private-handle rejection, and the control check immediately before dispatch.
- Preserve explicit manager-history reads, administrator authorization, and visible current manager/control state.
- Preserve retry-window guarantees, early-eviction tombstones, legacy undated entry protection, and grant replay validation.
- No live database modification, deployment, application restart, broad test runs, TDD, or per-change verification gates.
- Work on `codex/database-efficiency` in the verified checkout. A separate checkout is unnecessary for these source edits; test binaries, if required, must use isolated output.

## Review Focus

- A hint for one session must not suppress another session's hint during coalescing.
- Frequent hints must not postpone periodic full reconciliation indefinitely.
- A replaced or rejected private handle must retain existing recovery behavior.
- Summary validators must remain scoped to authenticated authority, repository, board, and incarnation, and never suppress explicit history obligations.
- Retention must never evict a recent reply without its replay fence, including legacy entries without timestamps.

## Tasks

- [x] Targeted background reconciliation: update `src/CoCern.Adapter/WakeWatcher.cs` so WakeNudge tracks dirty session IDs alongside its coalesced channel; external target-change pulses request a full pass; reconnect events target their route; periodic full-pass deadlines survive hint traffic.
- [x] Compact status replies: pass the conditional-read projection from `ToolRouter` through `StatusTools` to `ControlAggregate`; transport flags have already been removed from aggregate arguments. Retain the explicit history path and existing validator restrictions.
- [x] Replay retention: update `src/CoCern.Domain/Retention.cs` and `CoordinationStateMachine.cs` for amortized age cleanup on successful mutations, including acknowledgments and expired tombstones, without making status reads commit or scan retention maps.
- [x] Attention telemetry: update `src/CoCern.Core/TokenMeter.cs` to measure nested status offers and normal boundary offers correctly, leaving genuine over-budget event counts intact.
- [x] Extend only existing test containers for the observed compact-status, notification-fanout, retention, and telemetry regressions; add no unrelated test matrix.
- [x] At the combined milestone, ask Roslyn for tests covering changed symbols and run only the relevant selection using isolated binaries. Inspect semantic diagnostics and request one source-only review of the complete diff.
- [x] Update `docs/reference.md` with resulting behavior, record validation, and complete the CoCern task.

## Execution Notes

- Plan approved in scope by Paul's direct implementation instruction; no additional approval or design handoff is needed.
- Repository instructions override skill defaults that request TDD, whole-suite baselines, per-task gates, or redundant confirmation.
- Initial Git metadata writes needed sandbox elevation; the authorized feature branch was created successfully. The live database review artifacts remain separate from production changes.
- Source review caught transport controls being stripped before aggregate dispatch. The summary projection is now passed explicitly from captured controls and overwritten by `StatusTools`, preserving the public history-read path.
- Paul's additional request for .NET 11/C# 15 opportunities is included. Use C# 15 collection constructor arguments to pre-size per-pass session sets and retention snapshots; assess Runtime Async against the existing Orleans/dependency stack before enabling an additional compiler/runtime mode.
- Final verification: .NET 11 isolated solution build passed, and all seven selected regression methods passed without skips. Receipt: `artifacts/smoke/9b2d0bbb71c2-60DF60431AA3/receipt.json`. No product source changed after that verified commit.

# Analytics Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Build ANALYTICS-PAGE-1: an accessible dashboard over the existing hourly analytics snapshot.

**Architecture:** A viewer-authorized, read-only endpoint runs the fixed named reports for one repository and a 1–14 day window and returns a bounded typed response. Blazor renders comparisons and categorical charts as inline SVG, with explicit snapshot time, sample counts, incomplete history and unavailable states. Existing live dashboard traffic is separate from this hourly data.

**Tech Stack:** Existing ASP.NET Core, Blazor WebAssembly, DuckDB analytics, CSS variables and inline SVG; no new dependencies.

**Spec:** CoCern task ANALYTICS-PAGE-1 version 1, directly authorized by Paul in this chat on 2026-11-02. The task packet is the feature specification.

## Global Constraints

- Codex uses Roslyn CodeLens and Visual Studio MCP for all C# work, and the built-in browser for all browser verification.
- The endpoint uses the existing dashboard viewer policy, fixed query names and existing reply limits; no viewer-supplied SQL or increased limits.
- Days range is 1 to 14 inclusive; comparison uses the preceding equal period relative to snapshot time.
- Hourly data must display its snapshot time and must not be called live.
- Paired bars cover tokens/calls/messages per task and reply bytes by role; horizontal lists cover noisiest tools and claim conflicts; stacked bars cover guardrails and attention outcomes; numeric summaries cover assignment latency and blocked hours.
- Preserve current work, sessions, auth, documents and Add another PC behavior. Coordinate shared builds and files with Claude; Root owns final verification for this feature.
- Shared VS workspace is intentional for this running-app task; no unrelated branch changes, commits or publishing by workers.

## Review Focus

- Out-of-order responses after changing repository/days must not overwrite the latest selection.
- Null samples and absent history must remain unknown, while measured zero stays zero.
- Payload/row/key limits and partial query failures must remain visible and bounded.
- Mixed snapshot times during a refresh must not be presented as a consistent comparison.
- SVGs require accessible names and numeric equivalents; long labels and phone widths must fit without clipping.

## Decisions and Interfaces

Use the existing Segoe UI type stack and teal/gold comparison colors within the dashboard's blue surfaces. Align labels and numbers to shared columns; reserve the strongest contrast for period comparisons. Use two columns on desktop and one on phones. Keep empty states descriptive and actionable, without invented sample data.

The repository selector uses known repository IDs from the dashboard snapshot, with a conservative default; the endpoint requires an explicit repository ID. Proposed DTOs in the analytics section of DashboardContracts.cs:

- `DashboardAnalyticsSnapshot`: RepositoryId (string), Days (int), Status (string: ready/no-snapshot/unavailable/error/too-large), Message (nullable string), SnapshotAt and AvailableFrom (nullable DateTimeOffset), Queries (DashboardAnalyticsQuery[]).
- `DashboardAnalyticsQuery`: Name (string), Rows (DashboardAnalyticsRow[]), Error (nullable string), Truncated (bool).
- `DashboardAnalyticsRow`: Key (string), Current/Previous (nullable double), CurrentCount/PreviousCount (long).
- `GET /dashboard/api/analytics?repositoryId=<id>&days=<1..14>` returns this DTO, with invalid inputs rejected and the existing viewer policy required.

Current source exposes ten public named reports despite the packet saying eleven. Ruling: use all ten public reports because the packet's enumerated chart families also total ten; do not expose alert-only queries or invent a metric. AvailableFrom remains null until trustworthy history coverage exists. Explicitly state that coverage is unknown and selected periods may contain fewer days; never infer a full period from a zero count. Cost if wrong: another intended public report would require a small follow-up panel rather than silently exposing an unrelated alert query.

### Task 1: Bounded analytics endpoint

**Files:** DashboardContracts.cs analytics DTO section; new Host/Dashboard/DashboardAnalytics.cs; DashboardEndpoints.cs registration/map; new tests/CoCern.Tests/DashboardAnalyticsTests.cs. Analytics metadata support only if needed and coordinated.

- [x] Add HTTP acceptance tests first: unauthorized request, chosen days and repository, fixed report coverage and known seeded values, no snapshot/unavailable, short history, oversized result and null/zero distinction.
- [x] Root builds through VS and runs the filter to observe expected RED from missing endpoint.
- [x] Implement the DTO projection and read-only endpoint under the viewer policy; preserve existing bounds and reject inconsistent snapshots.
- [x] Root builds and runs targeted tests to GREEN; worker supplies semantic diagnostics and exact file list.
- [x] Fresh scoped review verifies contract, privacy, bounds and snapshot semantics.

### Task 2: Analytics page and charts

**Files:** new AnalyticsView and chart components; Dashboard.razor and Dashboard.razor.cs route/nav only; dashboard.css analytics section; docs/reference.md dashboard description; focused UI tests where practical.

- [x] Cover null, zero, partial/error and finite-number chart formatting with meaningful render/model tests before implementation where supported by the existing test stack.
- [x] Add Analytics between Sessions and Database and consume the endpoint once per selection, cancelling/ignoring stale responses.
- [x] Render paired, horizontal, stacked and numeric presentations with sample counts, units, snapshot date and honest empty/partial states.
- [x] Keep SVG accessible names and equivalent values; verify desktop and phone layouts with the built-in browser.
- [x] Document report semantics, days range, hourly freshness, bounds and missing-history behavior.
- [x] Fresh scoped review checks accessibility, state handling and data interpretation.

### Task 3: Integration and completion

- [x] Verify newly generated Razor symbols after Roslyn reload, then compiler diagnostics.
- [x] Coordinate one VS build/restart, run all analytics tests and the full suite, and inspect actual results.
- [x] Capture screenshots and write a concise artifact report with limitations and test evidence.
- [x] Update ANALYTICS-PAGE-1 criteria only after evidence passes, release its claim, and hand the frozen file list to Claude.

## Progress

- Planning: complete. Claim generation 1; no source implementation started yet.
- Root preserves the authorized unattended workflow; decisions about minor implementation details do not require a new approval cycle.
- Backend and presentation acceptance tests written before implementation. Temporary exclusion of the unrelated unfinished DocSearchIndexTests.cs is permitted only for one independent analytics RED build, then immediately removed; final builds/full-suite verification include all tests.
- Task 1 complete: actual RED 25/25 missing-feature failures (15 endpoint and 10 presentation cases). Backend first GREEN attempt exposed missing production DI registration; corrected factory and rerun 15/15 pass. Fresh review approves spec and quality after that fix. Artifacts: ui-analytics-red.log, ui-analytics-backend-green.log, ui-analytics-backend-fix1.log, analytics-backend-review.md.
- Task 2 implementation released after the backend test/review gate. All temporary test compile exclusions have been removed; full source builds successfully with peer doc-index files included.

- Completion: full suite 1076/1076; final focused 28/28; real snapshot loaded; all browser states checked; phone width375 fits and intermediate desktop header wraps. All review findings resolved. Task completed and claim released. User requested commit and merge after verification.

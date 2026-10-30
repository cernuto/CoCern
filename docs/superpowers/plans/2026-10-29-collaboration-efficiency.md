# Collaboration Efficiency Implementation Plan

**Goal:** Apply the Manager retrospective by reducing repeated board reads, preventing incomplete structured packets from appearing ready, and combining result submission with an advisory next-work offer.

**Spec:** `docs/manager-retrospective-2026-10-29.md` and existing `docs/architecture.md`.

**Architecture:** Extend the existing board/application/tool boundaries. Keep four grains, existing authenticated calls, normal task claims, durable effects and reported evidence. No native chat sender, execution engine, new model scheduler, or new authentication authority.

## Scope and compatibility

- Existing task operations remain compatible. Structured workflow is opt-in through `task_update.workflow`, stored separately from free-form facts. A facts field named status cannot replace authoritative structured readiness.
- A workflow includes state (`ready`, `blocked`, `completed`), a bounded closure packet (goal, target/source description, authority reference, restoration expectation, stop condition), bounded named acceptance criteria and their reported outcomes/evidence, bounded typed blockers/recheck conditions, optional target session and priority. A completed workflow requires reported passed criteria with evidence and no unresolved external action/restoration. Reports remain participant evidence, not independent runtime verification or human acceptance.
- Blocked workflow cannot become ready merely by lease or observation expiry. An explicit versioned update records recheck evidence; an already applicable human grant can be that evidence when resolving an unnecessary coordinator hold. This does not override provider approval or create new user-permission requirements.
- Delta reads use `task_get` without taskId plus `sinceRevision`. Use a durable board-wide task-change revision and per-task change marker. Only actual committed task changes advance it, including ownership/lease metadata; rejected operations and transport duplicates do not. No copied transcript/change-history store. First access to old boards makes existing tasks visible from revision zero without changing their expected task versions.
- Delta pages return a pinned snapshotRevision and continuation. Concurrent board change returns an explicit restart-required result rather than losing changes. Clients advance their durable checkpoint only after all pages. Ordinary reads remain compatible and bounded.
- `task_update.includeNextWork=true` adds an independent advisory `nextWork` result. Only explicitly ready valid workflows, with no blockers/unknown actions or active conflicting owner, are eligible; respect target session, exclude the current task, and order by configured priority then stable identity. Global pause suppresses offers. Offer is a snapshot, not a reservation or permission to execute; task_claim remains required.
- Offer failures/truncation cannot turn a committed milestone into failure or hide mandatory action/grant/cleanup fields. Include a compact packet when it fits; otherwise give identity/version and an explicit detail-fetch marker. Preserve 32 KiB actual serialized reply bounds and private session/repository authorization.

## Task 1 — Core behavior

Owned files: Core state machine/application and a focused workflow helper, `tests/PkRelay.Tests/CollaborationTests.cs` (add to isolated Core test harness).

- [x] Write executable failing tests for delta creation/update/retry/pagination/concurrent-change/legacy/restart; structured readiness/completion/blocker transitions; authority-reference preservation; and report-plus-offer pause/ownership/target/isolation/bounds behavior.
- [x] Implement bounded opt-in workflow validation and authoritative readiness, durable task-change revision, delta reads, and advisory next-offer composition.
- [x] Run isolated behavioral tests, then request independent review. Keep binary artifacts separate from shared integration outputs.

## Task 2 — Surfaces and integration

Owned files: Host/Adapter schemas, focused integration tests, usage docs and README. Use the Core contract above and existing tools; add no new endpoints.

- [x] Advertise new optional fields and explain snapshot checkpoints, reported completion and advisory offers.
- [x] Exercise real HTTP/MCP and SQLite close/reopen with delta checkpoints and a result/next-offer in one authenticated call, including rejected forged sessions and bounded wire output.
- [x] Update documentation and independently run the whole suite and live smoke, then publish updated binaries.

## Review focus

Mutation retries must not advance change revisions or dispatch twice. Delta paging must never silently skip an intervening update. Incomplete or blocked work must not appear ready after time passes. Offers must not grant execution/ownership or bypass pause and targeting. A failed optional read must not reverse a committed report or erase uncertainty/restoration evidence. Schema changes must describe actual behavior across REST, remote MCP and stdio.

## Execution

The user explicitly asked to apply the retrospective to code. Proceed with the bounded implementation and tests without another design-approval stop. Required Roslyn CodeLens and Visual Studio MCP checks were performed; Roslyn is ready on PkRelay, VS remains SNX and is not to be changed. Use semantic inspection/diagnostics and the documented actual-test/binary production exception. No rig work, deployment, automation or other chat instruction changes are authorized by this task.

Implementation and independent verification completed: 46 isolated Core cases and five collaboration integration cases passed; the shared suite passed 117 total cases with zero failures/skips. Live REST/stdio smoke exercised report-plus-offer and durable deltas. Independent review found two readiness bypasses, fixed through executable regression tests and confirmed in scoped re-review. Release publication and final patch checks complete the local build; IDE and external onboarding limitations remain explicit in `docs/verification.md`.

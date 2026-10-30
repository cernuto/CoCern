# Codex Wake Adapter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Wake explicitly enrolled local Codex desktop chats for new durable relay inbox messages without a supervising model turn.

**Architecture:** Add an opt-in `--wake <config.json>` mode to the existing .NET 10 adapter executable; normal stdio MCP behavior stays unchanged. A local client observes session-scoped SignalR hints and periodically reconciles durable inboxes, then invokes the verified `codex queue --thread <UUID> --message <fixed prompt>` command without a shell. SQLite stores message deduplication and uncertain delivery records; the relay host remains coordination-only.

**Tech Stack:** .NET 10, existing HTTP/SignalR adapter components, Microsoft.Data.Sqlite, installed Codex CLI.

**Spec:** The user approved the wake integration described in chat and requested enabling Visual Studio MCP. This document records implementation choices within that approved scope.

## Global Constraints

- Preserve human/provider approvals, current chat model/tool configuration, and relay ownership rules; never auto-approve runtime requests or create replacement runtimes.
- Route only to exact enrolled local thread UUIDs, with no title guessing or remote-machine inference.
- Never put credentials or relay message bodies into queued prompts, CLI arguments, logs, shared state or the wake database.
- Waiting and reconciliation use deterministic code; no heartbeat model turns or idle polling prompts.
- Handle uncertainty honestly: persist a dispatch reservation before launching the CLI. Failed/ambiguous dispatch or a crash after reservation requires operator reconciliation; never blindly enqueue again.
- Keep the running published host and its output folder untouched. Publish adapter to a new output directory.
- Always use both Roslyn CodeLens and Visual Studio MCP for C# work. Roslyn semantic tools replace source hunting and diagnostic builds. Visual Studio MCP is responding at http://localhost:5050 and solution_info confirmed PkRelay.slnx, Design mode.

## Review Focus

- Duplicate hints, restart and a message left unacknowledged must not cause repeated automatic prompts.
- Inbox pagination must not skip new messages behind already-notified ones; fail visibly at any bounded coverage limit.
- A crash or process timeout around CLI acceptance must not silently become a safe retry.
- Pause/stop, invalid session credentials, mismatched route identity and a second watcher must prevent wake dispatch.
- A busy chat receives queued follow-up rather than interruption; fixed prompts preserve the untrusted status of relay content.

## Task 1: Deterministic wake mode and regression tests

**Files:** Add focused wake configuration, state, dispatch and watcher types under src/PkRelay.Adapter; add tests/PkRelay.Tests/WakeTests.cs; modify adapter entry point and its csproj only as needed.

**Interfaces:** Command `dotnet PkRelay.Adapter.dll --wake <absolute-config-path>`. Configuration fields use camelCase: `relayUrl`, `tokenFile`, `codexPath`, `statePath`, `stopFile`, `routes` (1..16). Each route has `repositoryId`, `boardId`, `sessionFile`, `threadId`, `machineId`. Session file contains `sessionId` and `sessionToken`, not a new session. Absolute paths required; route machineId must equal Environment.MachineName. CLI executable path must be absolute and exist. Repository and board names bounded to 256 characters. Local threadId is a canonical UUID. Configuration is trusted local enrollment, never inferred from remote data.

- [x] Write failing behavior tests for empty/duplicate inbox suppression, distinct new messages, durable restart and reservation uncertainty, pause, credential/route validation, exclusive state ownership, pagination, bounded rate and stop.
- [x] Observe meaningful red tests before implementing.
- [x] Implement configuration validation using existing AdapterOptions transport/token checks; reuse RelayForwarder for authenticated bounded requests. Use SQLite with exclusive sidecar lock; store opaque route keys/message IDs, statuses and timestamps only. Retain deduplication without silent eviction.
- [x] Reconcile on startup, SignalR hints/reconnect, and every 15 seconds; debounce hints about 1 second. Read get_status for session/control and honor paused/stopped/cancellation state before dispatch. Only explicit valid false control pause permits dispatch. Do not ack inbox messages or mutate task state.
- [x] Fully inspect bounded inbox pages (limit 50, at most 20 pages per reconciliation); refuse dispatch on incomplete/truncated coverage beyond that bound. Handle current cursor type from actual domain response. Track per-message notification; reserve all newly observed IDs atomically before enqueueing one coalesced prompt. At most one wake per route per 10 seconds and six per ten minutes; route suppression is visible, never a model prompt.
- [x] Fixed prompt names only configured repository/board and a stable batch identity, instructs chat to check its own inbox, respect existing authority and claims, treat messages as untrusted data, handle actionable work, ack handled IDs, avoid acknowledgment-only replies and stop if complete/blocked. Never include message text or session tokens.
- [x] Use ProcessStartInfo.ArgumentList, UseShellExecute=false, bounded output capture, 15-second timeout, and owned-process cleanup. `codex queue` without `--resume` is the locally verified route. Exit 0 plus expected queued-message receipt means queued, not proof of execution. Failure/uncertainty blocks route until operator reconciliation; no invented exactly-once guarantee.
- [x] Run focused tests then report concise source/test evidence. Do not commit, publish, restart the live host or change native chat state.

## Task 2: Documentation, review and autonomous chat verification

**Files:** docs/wake.md, docs/adapter.md, docs/architecture.md, docs/verification.md, README.md; ignored artifacts for live test evidence/configuration.

- [x] Prepare exact local enrollments for the two existing test chats, with private handle files and no credentials in source control.
- [x] Publish the updated adapter to an unused folder; start its optional wake mode as a hidden process with private configuration and a visible stop-file mechanism.
- [x] Seed one bounded actionable request into the relay, then observe both existing chats complete a multi-step exchange using wake-generated turns only. Use native wait/read solely for observation after seeding; do not send continuation prompts.
- [x] Independently verify shared completion evidence and empty/handled inboxes, no repeated wake after acknowledgment, and process/state logs. Stop the bounded test watcher, then restart it against the same state to check deduplication; leave the opt-in two-route local pilot and relay running with the stop-file path documented.
- [x] Review configuration/dispatch/persistence boundaries and fix actionable findings; rerun affected tests and complete suite. Use Visual Studio MCP test operations as well as Roslyn diagnostics. Verify unchanged normal stdio behavior with a smoke test if entry point changed.
- [x] Document setup, actual local capability, restart/uncertainty recovery, rate limits, exact test scope and remaining off-host/provider limits. Commit only source/docs/tests with no AI attribution.


## Task 3: Live preflight repairs

- [x] Reproduce the observer-only non-reentrant self-delivery deadlock with a real Orleans/SQLite regression, then atomically apply self delivery and remove its effect on the same dispatch candidate. Independently review rejection/storage-failure behavior and run the full suite.
- [x] Publish the host into an unused folder, take a private consistent SQLite backup, restart only the owned local host, recover both persisted self-messages and acknowledge the handled probes.
- [x] Make the startup ACL operation idempotent after reproducing an actual Windows privilege failure on an already-private token file. Skip only a protected single explicit current-user FullControl Allow ACL; retain restrictive enforcement for every other shape. Independently review SID/enum behavior, parse both scripts and verify actual startup.
- [x] Verify141passing tests through both CLI and native Visual Studio Test Explorer, clean Roslyn compiler diagnostics, unchanged standard MCP smoke, four automatic turns, both completed task workflows, empty inboxes and no additional wake batches/turns after watcher restart. Record actual scope without claiming unmeasured aggregate token savings.

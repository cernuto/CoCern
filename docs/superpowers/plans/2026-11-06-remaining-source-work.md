# Remaining CoCern source implementation plan

**Goal:** Complete the remaining code for native activation, file claims, bounded chat-approved goals and hub-owned per-chat tool settings while preserving the running CoCern installation.

**Authority:** Paul requested all remaining code on November 6 and explicitly prohibited deployment and testing until he gives the go-ahead. This supersedes older plan instructions for test runs, builds, client experiments, hook installation and deployment.

**Architecture:** Extend existing durable board/session state, native binding proofs, hook plans and MCP transport. Standard connections remain available. Session settings narrow the existing authenticated credential ceiling; they never create administrator authority. Goals attach immutable approval evidence and membership to existing task workflows and Standing grants. Runtime acceptance evidence remains explicitly unverified until authorized checks run.

**Spec:** The existing auto-connect design and native-autojoin, chat-decisions, native-producer-and-activation, file-claim-consumer and features-choose-themselves documents, together with Paul's current source-only instruction.

## Constraints

- No tests, builds, packages, deployments, live acceptance probes, process restarts, hook changes or client settings changes. Semantic source inspection and source review only.
- Use Roslyn CodeLens for C# semantic inspection and diagnostics, and check Visual Studio MCP state. Keep the unrelated M3TS-SNX Visual Studio solution untouched.
- Retain this semantic workspace and edit only source on the feature branch; do not write to any active binary output or immutable installed package.
- Native identity, permission behavior, authentication and exact repository/machine/checkout/generation scope remain enforced. No synthetic proof, copied peer handle or native-to-standard fallback.
- Add only a small regression for a real security, replay or data-loss boundary; do not execute it.
- This chat exposes only CoCern messaging operations, so task claims are unavailable. Do not open a replacement session or alter the live tool profile to obtain them. Use bounded nonoverlapping source ownership and report no claim that was not granted.
- Root owns shared contracts, schemas, tool dispatch, Program wiring and final integration. Workers request shared edits rather than racing another owner.

## Task 1: Bounded goals

Implement goal approval through the existing record_decision path, with a versioned existing root task, frozen goal text, finite expiry, verified native participant membership and exact approval evidence. Reject ambiguous/unverified members. Link descendant workflow authority to the original immutable grant. Enforce revocation, expiry, root completion, member-only execution and owner-only root completion. Route goal outcomes to the owner's current verified session while retaining dashboard recovery. Preserve ordinary Standing and raw task behavior.

Owner files: StandingPreferences partials, TaskWorkflow, TaskBoardAggregate partials, BoardState, goal-specific new domain files, and goal-specific Core task/claim integration. Shared contracts and ToolRouter edits are supplied to root as exact integration requirements. SessionState, SessionAggregate, SessionTools and adapter configuration belong to Task 2 or Task 3.

## Task 2: Hub-owned session settings and tool refresh

Add a bounded durable session tool profile selected by an authenticated administrator with exact human words, version checks and an audit record. Session roles remain routing metadata. Profiles only narrow the caller's existing authenticated authority. Default new chats receive useful worker coordination tools without environment profile selection. Preserve legacy explicit narrowing until an explicit hub setting replaces it. Deliver current settings and revision to adapters, enforce them on the hub, and refresh MCP tool metadata when supported. Surface an honest reconnect requirement when a client cannot refresh; do not claim client acceptance without running it. Include native adapter handling.

Owner files: SessionState, SessionAggregate and session-specific partials, Core SessionTools and new session-settings services, Adapter CoordinationTools and new tool-surface components. Root owns central catalog/schema/dispatch and native factory/Program integration. Do not modify board/Standing/task files owned by Task 1 or setup/hook files owned by Task 3.

## Task 3: Supported native and file-claim activation

Replace the unconditional native-promotion no-op with a narrowly verified settings plan. Require actual profile/provider/helper identity, fresh installed-client permission evidence and an explicit owned activation choice. Preserve disabled hooks and unrelated handlers. Wire protected PRE and POST file-claim hooks as an explicit, reversible owned setting using existing scope/replay/lifecycle fences; no hook is enabled in a live profile during development. Keep ordinary setup standard until the activation requirements are met. Improve read-only readiness so missing proof or an old helper has an exact recovery action rather than a misleading success.

Owner files: Adapter Setup/ClientSettings partials, CodexNativeSetup, RepairCommand, activation-related NativeStartupEvidence/registration helpers and focused security regression sources. Root owns Program, NativeJoin factory, LocalChatWake/LocalWakeConnection, central contracts and UI integration. Do not edit CoordinationTools or board/session files.

## Task 4: Reliability integration and operational evidence

Root reviews local watcher creation, handle restoration, presence/reachability and older-helper catalog diagnostics; fixes source defects without inspecting private peer files. Add any missing read-only readiness and acceptance receipt support so future fresh-PC/SC3, Manager ping and permission checks can distinguish configuration, catalog, invocation and actual wake evidence. Update dashboard settings/diagnostics for the new source paths using current UI conventions. Treat old blockers as historical evidence; do not mutate the live board.

## Task 5: Source review and bookkeeping

Review worker diffs and cross-component contracts, then obtain an independent source-only review. Correct findings without running code. Reconcile historical plans and documentation with implemented source versus deferred runtime evidence. Leave an exact list of future authorized checks and all remaining limitations. Do not mark acceptance, deployment or real-client gates passed.

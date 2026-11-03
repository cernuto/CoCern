# Permanent wake integration implementation plan

> **For agentic workers:** Use bounded subagent implementation for the local adapter runtime and Claude collaboration for the installed Stop hook. Root owns integration and live deployment.

**Goal:** Install native idle wake support automatically through existing setup and development flows, preserve current chats, and verify collaboration across both PCs.

**Architecture:** Reuse `WakeWatcher`, the node's Codex dispatcher and `ClaudeWaiter`. Claude's installed Stop hook arms its existing waiter; explicit-token adapters own a watcher for their own chats during the MCP process lifetime. Local wake state uses a separate private credential scope, retaining token-file references rather than copying credentials into metadata.

**Tech Stack:** Existing .NET adapter, SignalR hints with bounded fallback, native Codex queue and Claude Code Stop hooks.

**Spec:** `docs/superpowers/specs/2026-11-03-auto-connect-design.md`, narrowed by Paul's request to permanent automatic wakes and cross-PC verification.

## Global constraints

- Keep current participants and their principals; no node enrollment or replacement session is needed on the hub PC.
- Preserve other user hooks, credentials and client settings. Update only CoCern-owned entries and retain normal backups.
- Use Roslyn CodeLens and Visual Studio MCP against CoCern.slnx. Produce test/deployment binaries only in isolated artifacts.
- A stored message, queue exit or background reader is not proof of a model wake; require a new native turn and correlated reply.
- `wake:false` remains durable mail and must not wake a model. Use existing policy, deduplication and bounded fallback.

## Review focus

- Restored explicit-token sessions must be captured only after successful authenticated calls and retain their native chat identity.
- Multiple hubs and adapter instances must not select an ambiguous Claude handle or dispatch duplicate wakes.
- Hook replacement must preserve unrelated handlers and remain idempotent after an adapter update.
- Disconnects and hook deadlines must release watcher ownership and stop background work.
- Raw-token-only configurations must not gain plaintext credential persistence.

### Task 1: Installed Claude Stop hook (Claude)

**Files:** Node/StopHook.cs, Program.cs, Setup/ClientSettings.cs, Setup/SetupCommand.cs, StopHookTests.cs.

- [x] Add failing tests for Stop-hook installation, owned-handler refresh, unrelated-hook preservation and malformed input.
- [x] Implement a stdin-native-ID wrapper around ClaudeWaiter: actionable mail prints the wake notice to stderr and exits 2; unavailable/disconnected/duplicate/timeout cases exit 0 silently.
- [x] Install during normal setup, with an explicit 24-hour hook deadline and earlier waiter cancellation matching the existing idle limit.
- [x] Run focused hook/setup/waiter tests using isolated repository artifacts.

### Task 2: Local adapter wake runtime (bounded coding agent)

**Files:** Node/LocalWakeConnection.cs, Node/LocalChatWake.cs, Node/SessionHandoff.cs, Node/ClaudeWaiter.cs, CoordinationTools.cs and focused tests.

- [x] Add failing tests for private scoped connection references, authenticated session restore, same-principal calls, ambiguous identity and runtime cleanup.
- [x] Implement local connection resolution and session observation without mixing admin handles with node credentials.
- [x] Reuse existing native identity locators, WakeWatcher and Codex dispatcher; serialize per-chat ownership and stop with the MCP lifetime.
- [x] Let ClaudeWaiter find an unambiguous local connection as well as existing node enrollment.
- [x] Verify observation through actual ChatCalls and preserve node-mode behavior.

### Task 3: Integration and live proof (root)

**Files:** Program.cs lifecycle, Development/DevelopmentSetup.cs, Setup/ConnectCommand.cs, focused integration tests and result documentation.

- [x] Wire runtime creation/observation/disposal and install the owned Claude handler from development and local-connect setup too.
- [x] Independently review changes, diagnostics and focused regressions; fix new failures before deployment.
- [x] Publish immutable adapter binaries and replace only the identified temporary CoCern Stop hook, preserving the existing sessions and other hooks.
- [x] Verify native wake after ended turns in both directions, exact replies across PCs, duplicate suppression, an FYI quiet window with a positive control, and scoped document reads.
- [x] Include Paul's authorized telephone-game variations as transport accuracy/timing checks, separately from idle-wake proof.
- [x] Commit and publish completed changes under Paul's standing commit/merge/push instruction, leaving source clean and recording any provider limits truthfully.

## Verification before deployment

The isolated permanent-wake candidate passed 215/215 focused tests, including actual conditional ChatCalls restoration, concurrent native ownership, hook replacement, node behavior and runtime cleanup. The known unrelated Windows Git PATH baseline test was excluded explicitly. Both MCP targets were verified; independent final review found no remaining blockers. Test result: artifacts/permanent-wake-isolated/results/permanent-wake-isolated.trx.

## Live verification

All planned live checks passed using the installed product integration, including an already-idle root Codex wake in about 7.6 seconds, Claude wake in about 1.7 seconds, a roughly 149-second FYI quiet window, cross-PC document reads and all three telephone variations. See docs/audits/2026-11-03-permanent-wake-tests.md for timestamps, correlation evidence and operating limits.

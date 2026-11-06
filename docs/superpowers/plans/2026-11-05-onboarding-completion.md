# Remaining onboarding and reliability implementation plan

Paul authorized implementing the remaining items in the onboarding improvement table: a supported self-contained Windows installer and complete Repair flow, clear pending-work results, and end-to-end connection diagnostics. The earlier native-startup implementation remains subject to genuine client verification and Codex's own hook review.

## Architecture and boundaries

- Package the existing self-contained hub and adapter with one guided Windows setup entry point offering Host, Join and Repair. Users of the package need neither an SDK nor a developer verification script. Reuse existing enrollment, credential ownership, native identity and startup machinery.
- Make Repair preview its checks and planned changes, then repair only owned configuration and startup wiring. Preserve credentials, unrelated settings, administrator choices and current sessions; never approve hook trust. Provide the same flow from Connect.
- Have every model-visible inbox result carry a complete eligible offer or a bounded explanation of why none is offered. Internal polls do not consume offers. Preserve authorization, snooze, focus, replay and version fences.
- Record bounded credential-free traces for development queue/generation work, adapter HTTP headers/body and hub execution under a shared correlation ID. Show recent traces from Connect, with measured boundaries and unavailable phases clearly identified.

## Ownership

1. Installer agent: setup executable/project, packaging scripts, adapter Repair command, installer/repair tests and source quickstart docs. Coordinate Adapter.Program dispatch and native enrolled-node support; no changes to the existing launch profile.
2. Pending-work agent: InboxWait and attention/domain/core response composition, related tool guidance and focused actual-regression tests.
3. Diagnostics agent: adapter/development trace capture, hub timing/correlation, local diagnostics reader and credential-free trace contracts. Coordinate host middleware/DI routes with root.
4. Root: shared Repair contracts, Connect Repair/diagnostics UI and endpoint integration, independent source review, package/deployment verification and built-in browser smoke tests.

## Verification

- Source-only reviews are the safety net. No TDD, broad test gates or test/build run per change.
- Once significant changes are integrated, use Roslyn test discovery/change impact to select only exact relevant methods, then produce isolated binary/package outputs and verify once at the milestone. Fixtures never alter real machine settings, registrations, profiles or credentials.
- Inspect Roslyn and Visual Studio connections. Roslyn targets CoCern; Paul reserved Visual Studio for M3TS-SNX, which stays untouched. Refresh Razor generation and confirm new members before trusting diagnostics.
- Deploy only to the existing CoCern database and preserve active immutable adapter versions. Smoke-test via the built-in browser, leave the application available and report any actual user hook-review step without bypassing it.

## Verified outcome

- Implemented the Windows Host/Join/Repair package, explicit pending-work results, correlated diagnostics and Connect health/repair/export UI. Source review also fixed the registered local native watcher path and apphost native argument preservation.
- Fourteen selected regression/security cases passed across the milestone run and focused correction reruns. No unrelated test suites ran. Roslyn compiler errors and diff whitespace errors are zero.
- Published win-x64 package integrity: 1,463 manifest files verified; Setup, Host and Adapter executables are 162,304 bytes each. The shared-runtime ZIP is 135,006,972 bytes. Setup help starts with the external runtime unavailable.
- Built-in browser smoke verified owned Repair, immediate refreshed status, disabled repeat repair, diagnostics refresh/export and absence of horizontal overflow. The downloaded export contains timing metadata without the hub credential or private payload fields. The final app remains open on the original database.
- Genuine Codex startup and wake verification still require Codex's own hook review and an actual startup event. No trust approval, fabricated native event, replacement session or other chat's credentials were used.

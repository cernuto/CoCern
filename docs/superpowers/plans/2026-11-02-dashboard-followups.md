# Dashboard and coordination follow-ups implementation plan

> **For agentic workers:** Use the subagent-driven-development workflow. The user has explicitly requested fully unattended work with Claude; continue without optional approval pauses.

**Goal:** Complete follow-up items 2–5: usable long-lived dashboard authentication, a truthful session-interaction map, a clean idle-wake test, and the diagnosed TLS/observation/navigation/identity issues.

**Architecture:** Extend the existing ASP.NET Core/Blazor application and its bounded SQLite observation pipeline. Keep durable session identities independent of browser cookies. Use safe persisted interaction metadata for an SVG graph and existing configured certificate pinning for the manual waiter.

**Tech stack:** Existing .NET 10, C#, Blazor, SQLite and SVG; no new graph library.

**Spec:** The user's messages in this chat, including the Application Insights image, the request for weeks-long sessions, and “do 2-5.”

## Constraints and execution rulings

- Preserve the shared dirty workspace and Claude's ongoing session-renaming work. Continue in the already verified Visual Studio workspace because the user launched this app there and explicitly requested coordinated rebuild/debug work.
- Both Roslyn CodeLens and Visual Studio MCP are required. Semantic tools inspect C#; Visual Studio owns actual builds/debugger operations. Root alone opens binary rebuild windows, never overwriting running application outputs.
- Codex browser verification uses only the built-in browser; Claude uses Edge. Do not disclose tokens, private handles, message bodies, or inbox text in graph data or reports.
- Normal dashboard sign-in defaults to eight hours with a nonpersistent browser-session cookie. An explicit Remember this browser choice defaults to a 30-day persistent cookie. Both lifetimes are configurable and bounded; background activity must not extend an absolute expiry indefinitely. Preserve origin, transport, principal validation and sign-out controls.
- Session graph nodes represent durable session identities; lines represent recorded interactions. Relay polling is not proof of model availability. Preserve selection/inspector behavior, provide honest empty/truncated states and bound node/edge/history work.
- A clean idle-wake pass requires the peer's waiter to be armed, its model turn to have ended, no user/manual inbox trigger, a unique nonce, and a resulting correlated reply. Adapter-only wake success is separate evidence.
- No commits, merges, pushes, or destructive history/data cleanup are part of this implementation.

## Task 1: Authentication and root navigation

Owner: dashboard_auth agent. Files: DashboardEndpoints.cs, SignIn.razor, auth settings/client/DTO code as needed, focused authentication tests. Auth-specific new DTOs go in separate files to avoid the graph contract owner.

- [ ] Inspect login/cookie validation and current configuration.
- [ ] Write behavior tests for normal versus remembered sign-in, configured durations and invalid bounds, preserved security checks/sign-out, and root redirect. Send TEST_READY; root verifies expected RED before production changes.
- [ ] Implement configured absolute lifetimes and Remember this browser UI using reported actual durations; redirect `/` to `/dashboard/`.
- [ ] Root verifies focused tests and browser behavior, including cookie expiration/persistence metadata without revealing credentials.

## Task 2: Session interaction graph and stable activity observation

Owner: session_graph agent. Files: DashboardContracts.cs graph DTOs, safe activity/projection/storage/collector seams, graph component, Sessions integration, dedicated graph styles, focused graph/observation tests.

- [ ] Inspect existing activity metadata and sanitization; settle exact safe projection interfaces with root before exposing data.
- [ ] Write tests for directed counts/last-interaction times, authorization/repository scoping, malformed/unknown endpoints, bounds/truncation, and unavailable activity snapshots. Root verifies RED.
- [ ] Implement an accessible SVG session map with selection, edge metadata, legends, and empty/truncated states. No message-body exposure or invented latency/availability.
- [ ] Prevent known activity timestamps disappearing during transient SQLite gate contention using explicit unavailable/stale observation semantics without holding JSON projection under the shared writer gate.
- [ ] Root verifies generated Razor members, diagnostics, tests, representative interactions, navigation and narrow/wide layouts in the built-in browser.

## Task 3: Manual waiter TLS and machine identity

Owner: waiter_identity agent. Files: existing adapter waiter/options/forwarding seams and dedicated tests. Coordinate Adapter Program edits; Claude owns rename-related Bind/schema/instruction changes.

- [ ] Trace config-file waiter option propagation and compare with working node-path certificate pinning.
- [ ] Write real boundary regression tests for configured correct/wrong pins and preserved HTTP loopback behavior; root verifies RED.
- [ ] Apply the existing pin-validation mechanism to the manual waiter without weakening TLS validation.
- [ ] Fill missing machine identity from verified client-side identity only; preserve caller-supplied and node identities. Coordinate an honest update path for the already-open Claude session.
- [ ] Root verifies focused tests and the live peer waiter using the rebuilt isolated adapter.

## Task 4: Clean idle wake, integration and review

Owner: root, coordinated with Claude.

- [ ] Acquire and renew the complete CoCern task claim and explicit file ownership; acknowledge handled peer messages.
- [ ] Collect TEST_READY states and verify each expected RED from one stable source snapshot; then release owners for implementation. Record build/freeze state durably to avoid crossed message/revert loops.
- [ ] Independently review agent changes; use a fresh bounded reviewer after implementation agents free a slot.
- [ ] Perform coordinated Visual Studio build/restart, focused checks, the complete regression suite and isolated smoke checks. Preserve original session IDs and verify rediscovery after restart.
- [ ] Arrange a nonce test while Claude is genuinely idle and its waiter is armed. Observe process exit and receive a correlated wake reply without a manual/user trigger; diagnose failures at the hub, adapter and client notification boundaries.
- [ ] Capture final browser evidence, leave the main hub running, stop disposable fixtures, release claims, and report precise completion evidence and any external client limitation.

## Review focus and shared-file scan

| Owners | Shared interface or risk | Ruling |
| --- | --- | --- |
| Auth / graph | Dashboard contracts and styles | Auth adds separate DTO files; graph owns DashboardContracts.cs and dedicated graph styles. |
| Waiter / Claude rename | Adapter instructions, ToolSchemas and SessionAggregate.Bind | Claude retains rename files; waiter coordinates through root before any overlapping edit. |
| Graph / root | Activity metadata, SQL gate and honest availability | Graph proposes safe fields and bounded semantics; root reviews and verifies live evidence. |
| All owners | Binary outputs and Razor generators | Root alone builds after a stable freeze; added Razor symbols must be found after regeneration. |
| Authentication | Missing/invalid lifetimes, remembered-cookie persistence, cross-origin login | Validate configuration and test consumer-visible cookie/login behavior. |
| Graph | Empty history, unknown sessions, duplicate events, large inventories, private content | Bounded safe metadata projection with explicit uncertainty; meaningful real-store fixtures. |
| Wake | Wrong pin, client not idle, pending messages, one-shot waiter not rearmed | Verify each boundary independently; never count a user-triggered turn as automatic wake. |

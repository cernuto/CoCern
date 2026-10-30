# Live dashboard implementation plan

> Agentic workers use bounded implementation scopes and independent review. Claude alone stages, commits, merges and pushes.

**Goal:** A PkRelay-served Blazor WebAssembly monitoring dashboard with live SignalR data, an enjoyable activity board and usable mobile/desktop views.

**Architecture:** One shared host sampler builds bounded, immutable observation snapshots from the existing storage owner and safe log templates. A separate read-only observer cookie/API/hub delivers snapshots to a WASM client; SQL metrics use generic DTOs with provider-specific collection behind the server boundary.

**Tech stack:** Existing .NET 10, ASP.NET Core, SignalR, System.Text.Json and Microsoft.Data.Sqlite; new Blazor WebAssembly client, with package versions matching the existing 10.0.14 baseline.

**Spec:** `docs/superpowers/specs/2026-10-30-live-dashboard-design.md`.

## Constraints and review focus

- Use Visual Studio MCP for IDE operations and Roslyn semantic inspection/diagnostics after verifying both targets. Never switch shared targets without coordination.
- Builds produce actual binaries only in isolated `artifacts/dashboard-*` outputs; the running hub/node outputs remain untouched. Browser work uses Codex's built-in browser exclusively.
- Viewer scope is explicitly instance-wide through an admin-plus-allowlist grant; cookies never authorize agent APIs. Revalidate live streams; test cross-origin/remote plaintext denial and revocation.
- Data is allowlisted, bounded and truthful: never publish raw grain payloads, credentials, inbox bodies or formatted exceptions; unknown counts stay null and capped counts carry accuracy labels.
- Test stale/reordered snapshots, auth expiry, disconnect/reconnect, empty inventories, truncation, long labels, reduced motion and mobile overflow.

## Task 1: Server observation boundary

Owner: fresh bounded backend worker. Files: new `src/PkRelay.Contracts/DashboardContracts.cs`, `src/PkRelay.Host/Dashboard/*`, observation/metrics port and implementation files in Core/Storage, minimal `SqliteStore.cs` and host registration/endpoint wiring. Tests: new `tests/PkRelay.Tests/DashboardTests.cs`. Root owns all project manifests and client files.

- [x] Add meaningful failing integration tests for explicit observer sign-in, cookie-only protected reads, cookie denial by agent APIs, origin/HTTPS checks, bounded sanitized session/SQL snapshots and stream behavior.
- [x] Implement the exact shared DTOs in the spec as records with initialized properties, plus a generic database metrics port and bounded provider implementation through the existing store owner.
- [x] Implement the dedicated dashboard cookie policy, one shared sampler, fixed-template bounded log ring, read endpoint and SignalR hub. Guard expiry/revocation and current snapshot failures without blocking relay work.
- [x] Run focused tests red then green using `--artifacts-path artifacts/dashboard-backend`; report the exact evidence and changed paths. Coordinate builds with root.
- [x] Have root/Claude review the auth, projection and resource bounds before integration.

## Task 2: WASM client and host asset integration

Owner: Codex/root for manifests and integration; bounded client worker for components/services. Files: new `src/PkRelay.Dashboard/*`, shared snapshot-ordering helper if needed, `Directory.Packages.props`, `PkRelay.slnx`, host/client project manifests. Tests: new focused client protocol tests plus browser interaction checks. Backend worker does not edit these manifests.

- [x] Pin WebAssembly packages to the existing 10.0.14 baseline; reference only Contracts from the client and expose its static assets under `/dashboard/` from Host.
- [x] Add a small pure snapshot-ordering rule with failing tests for duplicates, older revisions and new process instances; use it in the client. Do not add tests mirroring CSS or trivial rendering.
- [x] Implement same-origin sign-in/auth status, SignalR initial/reconnect handling, complete snapshot replacement, stale state and explicit sign-out. Clear credentials and keep them out of storage/URLs.
- [x] Build the responsive activity board, stable machine/session placement, selected-session inspector, Sessions/SQL/Logs views and local motion/theme preferences. Render actual data with clear unknown/truncation labels.
- [x] Produce isolated binaries, confirm new Razor members are visible to Roslyn, and run diagnostics. Ask Claude/backend worker to cross-review the integration boundary.

## Task 3: Smoke, polish and delivery

Owner: Codex browser tests; Claude review and Git integration.

- [x] Launch an isolated test host with distinct database, ports, cluster identity, protected operator credential and observer allowlist. Avoid seed/demo data in production output.
- [x] Use the built-in browser at desktop and mobile widths. Check sign-in/out, deep links, live updates, selection, generic SQL rows/tables, logs, long labels, empty states, reduced motion, disconnection/reconnect, scrolling and no horizontal overflow. Capture reviewable screenshots and fix observed defects.
- [x] Run the complete existing and new test suite from isolated outputs and current Roslyn diagnostics. Verify that observer traffic does not create sessions, acknowledge messages, acquire claims or queue wakes.
- [x] Document what was actually verified, including the limits of mobile emulation vs a physical remote phone and trusted-LAN TLS setup.
- [ ] Claude performs final review, commit, merge and push under Paul's direct authorization. The eight review fixes and final test/browser evidence have been handed back; coordinate any live deployment separately.


## Task 4: Game-board refinement from direct preview feedback

Owner: fresh bounded visual worker for ActivityBoard/CSS; root for overview layout, publishing and browser verification.

- [x] Record Paul's reiterated fun-game direction and a calm strategy-map treatment, keeping truthful status and actual-event motion.
- [x] Reorganize overview desktop areas to board/traffic beside inspector; preserve narrow-screen reading order.
- [x] Implement machine outposts, session identity tokens and subtle routes without adding dependencies or invented progress.
- [x] Publish into a stopped isolated preview output, confirm fresh Razor generation, and smoke desktop/mobile/light/dark/motion/selection/traffic again.
- [x] Capture the refined board and update verification/handoff evidence.

## Task 5: Verified review follow-up before Git integration

Owner: three fresh bounded storage, host and client workers; root retains source review, integration, docs, isolated publishing and built-in-browser verification. Claude owns all Git mutations.

- [x] Eliminate idle/default collection, move JSON parsing outside the shared storage gate, and bound frames without repeated whole-snapshot serialization.
- [x] Prioritize announced session grain keys before historical rows when inventory exceeds 200.
- [x] Preserve discovery parse/size truncation flags.
- [x] Remove Unicode Format characters from displayed labels.
- [x] De-duplicate public SessionId in the server projection.
- [x] Treat mismatched hub incarnation as stale while retaining original timestamps.
- [x] Use monotonic client receipt/advancement timing for observation freshness and a server logical display clock.
- [x] Limit login attempts per address and cache enrollment briefly while preserving live revocation.
- [x] Run meaningful regression tests, the integrated suite and fresh semantic diagnostics; publish only into a stopped isolated preview.
- [x] Recheck the built-in browser at desktop/mobile sizes and hand all eight results to Claude in one concise message.

The existing administrator-token sign-in model is retained, with the requested trusted-network/certificate warning. Deferred items are documentation only.




## Task 6: Re-review integration boundaries

- [x] Pass the actual application incarnation to observation and regress through real application open/bind rather than manufactured storage metadata.
- [x] Isolate sampling/publication exceptions per iteration, retain authorization checks, and verify real host recovery.
- [x] Keep initial/new-process frames unconfirmed until a later observed timestamp advances, with explicit waiting status and no device clock comparison.
- [x] Reproduce all three defects red, then pass focused9/29/7 cases and the full448-test suite.
- [x] Publish isolated Release and verify real current→restart-stale→rebound-current behavior plus desktop/mobile smoke; record source/artifact/evidence hashes.
- [x] Return the verified re-review source to Claude for final Git integration.

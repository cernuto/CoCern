# Build verification

Verified October 29, 2026 in `C:\Users\PaulC\source\repos\PkRelay`.

## Initial build checks

- The complete shared suite passed **95 tests, zero failed, zero skipped** using `dotnet test tests/PkRelay.Tests/PkRelay.Tests.csproj --no-restore --nologo --verbosity minimal`.
- Roslyn CodeLens reported zero compiler errors and warnings for the loaded five-project PkRelay solution. Optional analyzer execution was not claimed.
- Release publication succeeded for both `artifacts/host` and `artifacts/adapter`. These folders contain the actual deployable binaries and runtime dependencies.
- The isolated process smoke passed authenticated REST task continuity, anonymous-access rejection, OpenAPI/Scalar retrieval, and real stdio MCP initialize/tool-list/tool-call using the official SDK server.
- The documented startup script launched the published host with a separate temporary database and loopback ports. Health and Scalar returned HTTP 200; the test host was stopped afterward. No bearer value was printed.

The tests exercise SQLite persistence/ETag conflicts, tombstones, atomic due-index replacement, exclusive ownership, backup recovery, schema rejection and fair due-work pagination; real Orleans activation; separate session handles/private inboxes; composed-claim rollback; action replay/attempt fencing; restoration obligations and administrator acceptance; actual serialized reply bounds; authenticated REST/MCP; session-scoped SignalR; OAuth transport/scope validation; and queued-message recovery after an actual SQLite close/reopen.

Host fixtures assert their actual SQLite path, Orleans ports and cluster identity. A test exposed an earlier configuration-order error that reused default values before fixture overrides. That error was fixed through deferred configuration; the final passing results include the isolation assertions. Earlier restart/isolation claims were withdrawn until those corrected tests passed.

Independent component reviews found action-attempt, restoration, claim-set, convenience-authentication, wire-bound, OAuth and dispatch-fairness defects. They were repaired with regression tests. Scoped re-review found one additional administrator-acceptance edge case, subsequently reproduced in three nonterminal states and fixed. The coordinating agent independently inspected the amended acceptance guard and post-failure retry timestamp; the full 95-test run includes those regressions.

## Environment-dependent limits

Visual Studio MCP port 5050 remains connected to the SNX solution. The user requested a separate PkRelay instance, which was opened, but Windows app-control approval timed out during its MCP setup. Port 5051 was not reachable at the final check. No SNX IDE build, solution switch, debugger or document operation was performed. PkRelay IDE build/test verification therefore remains pending. Binary/test production used the CLI's documented actual-binary/test exception after both required MCP connections were checked and the mismatch reported.

Real off-host TLS, an external OAuth issuer/JWKS lifecycle, ChatGPT account registration, Secure MCP Tunnel onboarding and client-specific wake mechanisms were not configured or verified. JWT tests use a signed local fixture and normal named authentication options. The build exposes configured authentication and coordination tools; it does not supply an OAuth issuer or guarantee unattended model wakeup.

This is the first continuity and reported-action build. It does not implement optional MCP Events, cross-provider chat bridges, multi-hub clustering, automatic source transfer or an IDE executor. Claims remain advisory, and external execution/restoration is reported by authorized participants. Actual total token savings have not been measured.

The ignored default test database under `src/PkRelay.Host/data` was retained for diagnosis and is not part of source control or the published output. Live test logs and temporary databases are local verification artifacts. No enrolled rig, other chat or application was operated during these checks.

## Collaboration efficiency update

After the Manager retrospective, the existing board/application/tools gained durable task-change checkpoints with pinned delta pagination, opt-in closure packets with reported acceptance criteria and typed blockers, and an advisory next-work offer attached to `task_update`. Existing unstructured task clients remain compatible. See [collaboration.md](collaboration.md) for the request/response contract and boundaries.

The independently run complete suite passed **117 tests, zero failed, zero skipped**. It includes the earlier 95 cases plus 22 collaboration cases, including actual HTTP/MCP, SQLite checkpoint continuation/restart, criteria and blocker validation, report/offer composition, private handle isolation and serialized reply limits. Core's initial missing-feature regressions were observed failing before implementation. Integration setup/cursor mismatches were corrected without counting them as valid missing-feature evidence.

Independent review found two readiness bypasses: cached claim retries and new action reservations could use old readiness after a task became blocked/completed/retargeted. Six cases reproduced those defects before fixing; scoped re-review confirmed both addressed while preserving existing-action reconciliation and cleanup reporting. Compiler diagnostics were clean after the fixes.

The updated live process smoke passed authenticated REST report-plus-offer and actual stdio MCP task delta retrieval, alongside the existing authentication, metadata and protocol checks. Owned test processes were stopped. Release host and adapter binaries were republished. Dedicated PkRelay Visual Studio MCP verification and external onboarding retain the environment limits above. No running rig campaign or remote deployment was changed, and actual aggregate token savings remain unmeasured.

## Automatic local Codex wake integration

The optional adapter `--wake` mode adds trusted local chat enrollment, authenticated SignalR hints and durable inbox reconciliation, fixed Codex CLI queue prompts, SQLite notification deduplication/reservations, exclusive ownership, bounded pagination, persistent rate limits and a local stop file. Normal stdio MCP behavior remains available. Setup and delivery/recovery boundaries are documented in [wake.md](wake.md).

Visual Studio MCP was enabled during this work. Its native `solution_info` confirmed the PkRelay solution at port 5050 and `debugger_status` confirmed Design mode. The earlier SNX/missing-PkRelay IDE limitation above describes the initial build; it no longer describes the current connection. Visual Studio built the actual test binaries successfully, and Test Explorer reported **141 passed, zero failed, zero skipped, zero not run**. The coordinating agent's independent complete CLI run also passed **141 tests**. Roslyn compiler diagnostics were clean; optional analyzer execution is not claimed.

The 24 added cases comprise 23 wake cases and one real Orleans/SQLite self-delivery case. Initial missing-wake behavior was observed failing before implementation. Independent review found a presentation-dependent route key that could bypass durable deduplication and uncertainty after restart; three real SQLite restart regressions failed before the key was fixed to canonical hub origin, repository and session inbox identity. Scoped re-review found the issue addressed.

A live observer-only self-message probe exposed an existing non-reentrant grain self-call deadlock. A real Orleans regression timed out before the repair. Self delivery now applies to the owner's dispatch candidate so inbox insertion and outbox removal commit atomically, followed by best-effort notification. The regression verifies single delivery, repeated dispatch, restart durability, due-work removal and acknowledgment. Independent review checked rejection and storage-failure behavior and found no actionable issue. Other participant delivery does not recursively drain a recipient's outbox.

The original isolated process smoke passed again after the entry-point change: authenticated REST milestone/offer, anonymous rejection, Scalar/OpenAPI and actual stdio MCP initialization, tool listing and task-delta calls. It used owned temporary processes and databases and stopped them afterward.

The installed Codex CLI queued probes to both exact existing desktop test chats, and both chats actually ran reply turns without the supervising chat's continuation tool. The optional `--resume` flag warned about a missing standalone daemon; queue-only delivery was verified through the running desktop app instead. A queue receipt alone remains insufficient evidence of execution on another installation or while the desktop app is closed.

The complete autonomous exchange then passed on board `local-auto-wake-20261029-01`. After one relay seed message, the watcher generated exactly four automatic turns: A produced a nonce/checksum and handed off; B independently verified and completed review; A verified B's report and completed production; B verified both completions and acknowledged the final receipt without replying. Both tasks finished at version 3 with passed evidence. The supervisor independently recomputed the checksum, checked both empty inboxes and inspected four queued SQLite wake batches across two routes. No native chat continuation was sent after seeding. Waiting and inbox reconciliation occurred in ordinary adapter code; aggregate token savings were not measured.

The watcher honored its stop file. After a private consistent SQLite backup, only the owned local relay was restarted from fresh published binaries using the same database. A bootstrap retry exposed a Windows privilege error when reapplying an already-correct token-file ACL; the script now skips rewriting only an already-protected DACL with exactly one explicit current-user FullControl Allow rule, otherwise retaining the original restrictive ACL path. Independent review found no weaker condition, both startup scripts parsed, and the actual restart succeeded. The updated host recovered both previously stuck observer self-messages, which were explicitly acknowledged. Restarting the watcher against its unchanged wake database preserved exactly four queued batches, zero uncertain batches and zero new chat turns; both participant inboxes remained empty. Relay health was `ok`, and the two enrolled local test routes were left running as an opt-in pilot.

Local verification artifacts are under ignored `artifacts/local-two-chat`, including `auto-observer-final.json`, `auto-wake-final.json`, `auto-restart-verification.json` and `self-delivery-recovery.json`. Private enrollment/state lives under ignored `data/wake-live` and each chat's restricted session file. No rig campaign, remote deployment, provider approval setting or scheduled automation was changed. Remote hosts and other client wake protocols remain unverified.

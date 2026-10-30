# Build verification

Verified October 29, 2026 in `C:\Users\PaulC\source\repos\PkRelay`.

## Completed checks

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

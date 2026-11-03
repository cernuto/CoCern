# Hub identity recovery implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore a missing hub certificate from a private automatic backup without silently changing an established hub identity.

**Architecture:** Extend the existing HubCertificate loader and private atomic-file primitive. Keep a backup and a fingerprint marker beside the configured certificate, and treat an existing local-hub record as established identity at the host startup boundary. This is the first independently testable slice of approved auto-connect parts 8–11; it does not implement identity-change acceptance or the manager role.

**Tech Stack:** C#/.NET 10, existing X509/SQLite/Orleans host, xUnit.

**Spec:** docs/superpowers/specs/2026-11-03-auto-connect-design.md (part 8, gate 6, order item 7).

## Global constraints

- Traffic between PCs stays encrypted, the hub's identity stays checked and every caller still has to prove who it is.
- No silent new certificate: restore an automatic backup; do not regenerate an established identity after loss.
- Keep corrupt or conflicting identity artifacts unchanged and report a plain recovery error; do not recommend delete-and-repin as normal recovery.
- Preserve the existing explicit certificate path and legacy copy migration, including retention of another hub's source file.
- Both Roslyn CodeLens and Visual Studio MCP are required. Use semantic tools for C# and diagnostics; never build into the running checkout's outputs.
- No live certificate, pin, credential, permission or hub-process changes. Fixtures use fresh temporary directories only.
- Claude owns StopHook.cs, OwningProcess.cs, ClaudeWaiter.cs and OwningProcessTests.cs in the primary checkout; exclude them.
- No AI attribution; commit paragraphs and bullets use soft wrapping.

## Review focus

- Partial writes and concurrent first starts: the actual primary winner, backup and marker must agree, with no leftover temporary files.
- Existing primary but unreadable or different backup: no silent overwrite of evidence and no new identity.
- Both copies missing after an established start: marker or existing local-hub record prevents regeneration.
- Private key permissions: backup, marker and restored primary must use the existing owner-only atomic-write path.
- Legacy configured paths and a second preview hub: recovery must not delete or modify the original source identity.

### Task 1: Private backup and fail-closed recovery

**Files:**
- Modify: src/CoCern.Host/HubNetwork.cs (HubCertificate and HubNetwork.Configure only).
- Test: tests/CoCern.Tests/HubNetworkTests.cs.

**Interfaces:**
- Consume existing HubCertificate.WritePrivately(string path, byte[] bytes), CertificateFingerprint.Of(X509Certificate2), LocalHubRecord.FileName and database-path resolution.
- Produce HubCertificate.LoadOrCreate(string path, string hostName, bool allowCreate = true). Existing two-argument callers keep first-initialization compatibility.
- Backup path is path + ".backup"; durable identity marker is path + ".identity" containing the primary's SHA-256 fingerprint as UTF-8 text, not a private key.
- HubNetwork.Configure passes allowCreate:false when local-hub.json already exists beside this hub's database. A marker also forbids regeneration regardless of allowCreate.
- Existing valid primary is authoritative for creating missing artifacts. Validate the actual winning primary after a concurrent create before deriving backup/marker. Validate an existing backup and marker against that primary; refuse corruption or mismatch without replacing either.
- Missing primary restores validated backup through the private atomic write and validates the resulting winner. Missing primary and backup with marker or allowCreate:false fails; a truly new directory with no identity evidence may initialize normally.

- [ ] **Step 1: Add failing behavioral regressions.** Tests must prove: missing primary restores byte-identical fingerprint and private key; valid primary gains backup/marker; invalid backup cannot cause regeneration; conflicting backup/marker are left unchanged; established marker with both certificate copies removed fails; allowCreate:false without copies fails; existing local-hub startup record prevents silent new identity; repeated/concurrent starts agree and leave no temporary files. Extend existing owner-only Windows ACL / Unix0600 checks to all artifacts. Update exact-directory inventories only for the defined new artifacts. Preserve corrupt-primary and second-hub source-retention regressions.
- [ ] **Step 2: Run the affected HubNetworkTests against the baseline and retain the expected red evidence.** Use dotnet test tests/CoCern.Tests/CoCern.Tests.csproj --artifacts-path artifacts/identity-recovery-tests --filter FullyQualifiedName~HubNetworkTests --logger trx --results-directory artifacts/identity-recovery-tests/results -m:1 --nologo. Build outputs are isolated; test discovery errors are not an acceptable red result.
- [ ] **Step 3: Implement the bounded recovery behavior above.** Reuse the current loader and private atomic writer; do not add dependencies or a second certificate store. Dispose every temporary certificate/private-key handle. Return an actionable recovery message without exposing private data or silently rotating trust.
- [ ] **Step 4: Run the same tests green and use Roslyn diagnostics.** Report exact passed/failed/skipped counts and TRX path. Do not run a full suite or publish/restart anything; root serializes broader checks.
- [ ] **Step 5: Self-review and commit only the two owned files to codex/auto-connect-features.** Report BASE/HEAD, test command/result and concerns in the task report. Root dispatches a fresh task reviewer before the next implementation task.

## Subsequent plans in the authorized build

1. Connection discovery and plain reachability/identity/authentication errors; explicit identity-change recovery remains a separately tested path with human confirmation.
2. Durable singleton manager assignment, handoff, recovery and atomic audit plus dashboard pause/stop/selection safeguards; keep assignment inert until safeguards pass.
3. Direct human Standing decision recording and deduplicated presentation in active chats.
4. Manager diagnostics/setup tools reusing existing services and scoped authorization.
5. Verified node update and rollback between wakes.
6. Hub-owned session capabilities and tool-list refresh; remove normal role/profile switches only after runtime authorization and cached-client behavior are proven.
7. End-to-end isolated browser and cross-PC acceptance, then integration through the designated Git/deployment owner.

## Preflight decisions

The approved design and Paul's explicit instruction to start the build authorize execution; no repeated design-approval question is needed. These tasks are staged, not a claim that all features already exist. Existing full-solution baseline at ecaff26 is 1,446 passed / 0 failed / 0 skipped; do not repeat it merely to create this isolated workspace. The known uncommitted Claude wake fix is independent of this baseline and is not included in these commits.

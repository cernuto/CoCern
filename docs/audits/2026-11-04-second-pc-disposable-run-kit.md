# Second-PC disposable run kit: node update acceptance

Prepared 2026-11-04. **Every execution case is NOT RUN.** This is a procedure, not execution authority or evidence that an updater/native provider is accepted.

Basis: [acceptance cases 9.2–9.3](2026-11-03-feature-build-acceptance.md) and [node self-update plan, Task 6](../superpowers/plans/2026-11-03-node-self-update.md). Prepared on source `afeb39c1cde1ae247ca5d233007e552bdee632e9`; this documentation branch does not contain the later updater implementation. Root must bind the procedure to the reviewed, frozen Node Task 5 candidate and its actual entrypoints before execution. No command below is an instruction to update an existing installation.

## Ownership and execution gate

Root owns candidate production, publication fixtures, integration and release. The second-PC owner records the disposable PC journey. The coordinating manager schedules the run and receives results; a peer's assignment or go message grants no live permission. Paul supplies any still-missing direct authority for the bounded fixture operation and native client prompts. Existing applicable authority may be referenced rather than requested again.

Do not execute until root's frozen candidate/fixture manifest, applicable direct execution authority and a coordinated runner window are recorded together. Message the manager before starting. No failure injection, certificate removal, session closure, settings replacement or updater bootstrap targets the shared hub or the existing enrolled node. A missing prerequisite leaves its case NOT RUN.

The read-only existing-PC baseline is separate: DESKTOP-8IV8M8M, node PID 7896, product `1.0.0+1fc283b0a009cad33b6d6dccf5382a7588cec34e`, DLL SHA256 `A65BF7DE476ADBFF84096CE93ADB588EB04D68BD01C084FE01CE8656969FAEC7`, observed `2026-11-04T16:35:28Z`. Its enrollment remains untouched. Hub machine LastSeen correlation was unconfirmed; chat reachability does not establish it. This old pre-updater baseline does not establish a working supervisor bootstrap.

## Fixture manifest required from root

Supply a private run manifest before assigning the execution window. Evidence reports contain only nonsecret identities and comparison results, never credentials, authentication headers, join material or private credential paths.

| Required input | Binding that must be recorded |
| --- | --- |
| Frozen source and review | Exact candidate commit, clean source identity, accepted Node Tasks 1–5, scoped/full regression references, supported OS/RID/runtime, supervisor protocol. `2a79671 or later` alone is not a frozen candidate. |
| Images A and B | A is a known working updater-capable fixture node; B is its reviewed successor. Supply product versions, DLL/EXE SHA256, immutable tree ContentId, ZIP byte count/SHA256/ReleaseId, and descriptor metadata. ZIP identity, tree identity and DLL identity are distinct. |
| Supervisor and bootstrap | Exact supervisor DLL/EXE/hash, protocol, stable startup command, supported install/bootstrap procedure, rollback image A, original fixture data directory and ownership model. A later update must not rewrite that startup. Authorize initial fixture bootstrap separately from automatic A-to-B acceptance. |
| Publication fixture | Disposable TLS hub on PC 1 with isolated database/certificate/publication/cache roots, exact endpoint, authenticated release mode and expected pin. It must serve controlled per-case releases and retain required old bytes. No shared-hub publication or arbitrary remote URL. |
| Failure controls | Root-provided, reviewed mechanism for truncated transfer, advertised wrong digest, valid ZIP with invalid extraction entry, and verified but failing successor. Identify exactly which bytes/response/process are controlled and how normal publication is restored. No ad hoc mutation of a live immutable image. |
| Native clients | Actual Codex and Claude versions/surfaces; isolated client/config/session roots on genuinely separate PCs; verified provider support and normal permission gates. Specify exact disposable chat targets and which path is native versus unchanged raw. Missing/disabled native support remains NOT RUN. |
| Observation | Known read-only means to identify the exact owned supervisor/worker PID, loaded path/hash, one singleton owner, update journal state, committed current/previous and authenticated hub machine LastSeen. Supply a bounded log/schema contract with redaction. |
| Window and cleanup | Exclusive publication owner and second-PC fixture owner, fixed run ID, start/end, timeout policy, approved cleanup roots and retention location. No competing node/profile/publication tests. |

The owner proves resolved absolute fixture paths are inside the agreed disposable roots and distinct from production data, installed adapter versions, existing profile/session roots and shared certificates. Check this before any fixture writes. Use unique state per case; do not copy production credentials, pins, sessions, grants or wake databases. Enroll the disposable node under a distinct fixture identity. Keep each case's original enrollment/data directory unchanged across its A/B transition.

Paul's authority record must identify the disposable hub and both PCs/profiles, initial setup/bootstrap and trust prompts, permitted publication/failure injection, actual native message/wake targets, any narrowly required fixture appointment/grant, and cleanup. Native ask/deny controls remain in force; no synthetic human approval, permission bypass or grant inferred from a manager label. PC reboot is not included unless explicitly authorized; prefer the approved fixture's original startup path for the persistence check.

## Evidence packet and fixed observation bounds

For every row below record case ID and status, run/fixture ID, frozen commit, descriptor and image hashes, PC/client versions and identities, UTC start/end, elapsed monotonic time, expected result, actual result and sanitized evidence references. Record supervisor separately from worker, served package, host and installed MCP backend/loaded metadata.

Privately compare before/after enrollment identity, hub origin/pin, credential, participant binding, session ownership, pending mail/cache and reserved/queued/uncertain wake state. Publish equality/preservation booleans, not secret values or hashes derived from credentials. Normal legitimate mail/ack changes are reported explicitly; do not require byte-for-byte equality of an actively changing database. Work, roles, claims and tokens must not transfer between sessions.

These are proposed run bounds; root must confirm them against the frozen candidate's real cadence before execution. Freeze any changes before a case begins, never extend a deadline to turn missing evidence green.

| Observation | Bound |
| --- | --- |
| Initial healthy A and hub LastSeen | 120 seconds after fixture startup; no extra enrollment on transient network failure. |
| Ordinary update discovery/staging/activation | 10 minutes after one publication. Do not force an announce or bypass five-minute staging cadence/fifteen-minute failure backoff. |
| Drain and candidate readiness | 30 seconds each under the implementation contract. Observe deferral or rollback; never cancel a dispatch to make drain succeed. |
| Retained A restart after failure | 120 seconds after the recorded failure/rollback decision, including one ordinary authenticated announce. |
| Ended native turn and quiet control | Observe actual turn completion, then 120 seconds without a new turn or CoCern polling call. An already active turn cannot satisfy idle wake. |
| Fresh actionable nonce | New native turn within 120 seconds of durable store, correlated reply and explicit message acknowledgement within a further 120 seconds. |
| Same-release no-op | At least two ordinary announce cycles and one normal staging opportunity, bounded by 10 minutes. |
| Whole case | 25 minutes for 9.2; 20 minutes for each 9.3 variant. A fixture scheduling/prerequisite failure before exercising the trigger is NOT RUN; an exercised behavioral deadline miss is FAIL. |

Uncertain dispatch is a stop condition immediately, not a timeout to retry. Do not clear, requeue or mark handled a reservation to repair the fixture.

## Shared setup and real wake receipt

1. Match binaries and endpoint to the manifest. Confirm only the disposable hub's certificate/credential is used. Verify no redirect following before an authenticated download; wrong pin must fail before HTTP. Root's supporting tests do not replace this run's frozen binary identity check.
2. Start A through the approved stable fixture supervisor/startup. Observe one worker owning the original fixture singleton, the correct supervisor/worker image, healthy authenticated announce and exact fixture machine LastSeen. Capture the before-state invariants. If a cold fixture bootstrap is needed, complete and record it before the timed update journey; it is not automatic-update proof.
3. Use actual isolated native clients on PC 1 and PC 2. For each required Codex/Claude surface, record actual version, loaded metadata identity when observable, native chat/participant/session identity and normal allow/ask/deny behavior. Do not activate a provider whose permission gate is unproved. If profiles cannot coexist safely with the running live clients, stop and request a supported isolated setup.
4. For a wake receipt, first finish the recipient's prior bounded task and observe its native turn ending. Record quiet interval start/end. Sender then stores exactly one fresh actionable fixture message with a unique nonce. Observe durable message ID/store time, any reservation/dispatch classification, new native turn start, exact-target nonce reply and acknowledgement IDs. Queue acceptance, inbox reads, journal readiness, a manually triggered turn, or a read-only poll are not a wake receipt.
5. Send a separate `wake:false` control during its own 120-second idle observation; no native turn is expected. Follow with a distinct actionable positive control and the same receipt requirements. Silence without that positive control is insufficient. No manual queue injection, click or in-turn waiting counts.

## Case procedures

Each failure variant uses its own disposable hub publication/cache root, original node data directory and isolated native profiles, cloned from the manifest's healthy fixture procedure rather than production state. Independent fixtures prevent one release's backoff or uncertain state from being hidden by a reset. The two physical PCs remain the same real machines. Record Codex and Claude results separately; one client passing does not cover the other.

| Case / status | Disposable fixture and exact trigger | Required procedure and evidence |
| --- | --- | --- |
| 9.2 update with pending mail — NOT RUN | Hub `R-92`, node `N-92`, client profiles `P-92-Codex` and `P-92-Claude`; healthy A and verified B. These are run-manifest IDs, not executable paths. | Record A/identity/singleton baseline. Arrange one uniquely identified ordinary pending message at a root-specified observable, safe boundary; do not fabricate or hold a reservation. Publish B once through the fixture owner. Let ordinary scheduling download/verify/drain/handoff. Observe verified B's PID/path/DLL/tree, retained A, stable supervisor/startup and one owner of the original singleton. Track that same pending message through preserved state and eventual legitimate handling without duplicate wake/ack. If a reservation becomes uncertain, stop. Capture the shared real wake receipt after B is healthy, including quiet control and fresh positive nonce. Pending-mail handling while a turn is active is separate from proof of a later idle wake. |
| 9.2 same-release no-op — NOT RUN | Same healthy `R-92/N-92` after successful B, using the exact same ReleaseId and bytes. | Offer B again; do not regenerate ZIP bytes and call it the same release. Observe the defined no-op window: unchanged worker PID/path/current/previous/activation history, no second download/activation caused by replay, no duplicate wake/ack. Ordinary background activity is allowed and recorded. A distinct ordinary positive nonce still works. Restart through the original authorized fixture startup path and confirm committed B is selected, enrollment/state preserved and one singleton. This restart is a separate persistence subcase; no-op PID assertion ends before it. |
| 9.3 partial transfer — NOT RUN | `R-93-partial/N-93-partial/P-93-partial-*`; healthy A, authentic descriptor for B, reviewed server control sends fewer ZIP bytes then closes. | Offer once and allow one normal attempt. Record advertised/received byte counts, fixed refusal reason and no staging commit/candidate execution. Verify original A remains healthy (or retained A only if the implementation legitimately restarted it), identity/singleton/state intact. Restore normal fixture publication without retrying the failed case; perform a fresh ordinary quiet/nonce wake with A. |
| 9.3 hash mismatch — NOT RUN | `R-93-hash/N-93-hash/P-93-hash-*`; authentic descriptor and root-controlled changed payload of equal length. | Deliver bytes whose hash disagrees with the descriptor. Record expected/actual package hashes and refusal before extraction/execution, old A identity/path/hash and invariant booleans. Never modify a verified in-use directory. Prove A's fresh ordinary wake after restoring the fixture endpoint. |
| 9.3 unpack rejection — NOT RUN | `R-93-unpack/N-93-unpack/P-93-unpack-*`; root-provided ZIP/descriptor hashes agree, but archive contains a deliberately invalid traversal/alias/link entry. | Record the reviewed malicious-entry classification, not private path content. Allow normal verifier/extractor to reject it. Prove zero writes outside staging, no committed candidate/process launch, clean or quarantined staging per contract, unchanged A and protected state. Restore endpoint and prove A's fresh ordinary wake. Do not create the invalid archive or relax extraction checks during execution. |
| 9.3 successor startup failure/rollback — NOT RUN | `R-93-start/N-93-start/P-93-start-*`; A plus root-provided fully verified compatible B-fail that exits or fails readiness under a reviewed control. | Confirm package verification succeeded before handoff. Observe successful drain or safe deferral; only after old owned worker exits/releases lock may the supervisor launch B-fail. Record exact owned child PID/path/hash, nonce/readiness outcome, failure deadline, stop of only that child/owned descendants, rollback journal and retained A restart. Require one owner of the same singleton, same supervisor/startup, authenticated A announce, unchanged identity/credentials/bindings and preserved pending/uncertain state. Then perform A's fresh ordinary idle wake. No broad process kill, reenrollment or manual repair. Startup-exit and readiness-timeout controls are separately reported if both are required. |

For 9.3 PASS, each variant requires its own normal wake after the failure. Do not reuse a receipt from 9.2 or another variant. If a failed candidate is ever executed before verification, record FAIL and stop the entire run. If a required failure mechanism is unavailable, its variant is NOT RUN; another failure is not a substitute.

## Stop, report and cleanup

Stop immediately and send the manager/root case ID, observed nonsecret facts and outstanding obligations when any of these occurs:

- Wrong commit/hash, missing review/bootstrap/runtime compatibility, changed fixture ownership, or any path/endpoint/profile overlaps production.
- Request for a new live grant, trust bypass, unapproved provider activation, client setting replacement, shared-hub publication or PC restart beyond recorded authority.
- Wrong machine/chat/participant, missing pin, unexpected credential/identity replacement, second singleton owner, or work/mail/roles/claims transferred to another session.
- Any uncertain wake, duplicate nonce/turn/ack, unexpected `wake:false` turn, inability to distinguish native idle from active work, or an unverified executable launch.
- Drain/readiness/rollback deadline missed, stale readiness accepted, original A unavailable, cleanup/restoration incomplete, or the fixture would require manual queue/reset/reenrollment to proceed.

Do not continue sibling cases after a safety/invariant failure until root rechecks the frozen fixture and records a disposition. Preserve uncertain state and evidence. An inconclusive observation is not permission to retry a mutation. Report behavioral mismatches as FAIL and unmet/unexercised prerequisites as NOT RUN with the exact blocker.

End each case by stopping only the exact owned fixture processes through their approved lifecycle, verifying lock release and restoring fixture publication controls. Never stop another session's Claude waiter, live node, MCP backend or native model turn. Retain evidence and any unsettled state until root approves cleanup. Before deleting any disposable tree, resolve and verify its absolute containment against the agreed root; no computed broad deletion or automatic destruction of uncertain wake state.

Send one bounded result packet per case to the manager and root with explicit message acknowledgements: status by provider/subcase, timestamps, frozen identities, ordinary wake nonce receipts, invariant booleans, remaining restoration, and evidence paths. Root decides integration/release. This kit does not mark broader native-join, permission, manager, metadata-refresh or cross-PC acceptance green from node protocol evidence.

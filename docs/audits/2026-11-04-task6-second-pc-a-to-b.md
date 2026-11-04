# Task6 second-PC receipt: narrow authenticated A-to-B convergence

**Result: PASS for the narrow normal-update convergence case only.** This does not complete acceptance 9.2, Task6 or the release gate. Live download/drain/Starting history, pending-mail, native-wake, no-op, failure/rollback and restart cases remain NOT RUN at this checkpoint. The earlier cold-bootstrap 120-second authenticated-announce subcase remains UNCONFIRMED / NOT RUN; see the [phase0/phase1 receipt](2026-11-04-task6-second-pc-phase0-phase1.md).

Frozen source: `a612571ac4d47129748de24fd7def2da0ae7ae52`. Physical PC2: DESKTOP-8IV8M8M. Disposable fixture machine: `73e2146bc6ee4621af1f2a6276ba2891`. The manager accepted the independently authenticated running A baseline before root's distinct B publication. No restart, reset, reenrollment, forced announce, staging override or backoff bypass was used.

## Actual ordered receipts

All times are UTC. Root published B once at `20:58:39.7099044Z`; the fixed ten-minute deadline was `21:08:39.7099044Z`.

| Event | Observed receipt |
| --- | --- |
| Root's last sampled A | Observer `20:59:53.720293Z`; authenticated LastSeen `20:59:39.7162502Z`; worker 25736 |
| B worker creation | Local Windows process creation `20:59:50.072528Z`; worker 8484, parent supervisor 29748 |
| Retained readiness file | LastWrite `20:59:56.4818947Z`; PID 8484 and B ContentId match the owned worker/image |
| Root's first sampled B | Observer `20:59:58.765564Z`; authenticated LastSeen `20:59:57.2388652Z`; worker 8484 |
| Authenticated B elapsed from publication | Approximately 77.529 seconds, within the original ten-minute bound |
| Committed journal | LastWrite `21:00:01.6629023Z`; Current B, Previous A, Pending null |
| First local B journal observation | `21:01:50.148Z`; already committed B with A retained |

The last sampled A and first sampled B are five-second observer samples, not a complete sequence of process states. Their ordering does not assert the exact drain/start boundary. No observer witnessed live download/drain/Starting transitions. The file timestamps and authenticated rows above are retained receipts, not reconstructed phase history. An optional local command-context parser failed to extract the launch context: nonce/launch-ID comparison is NOT RUN, not an observed mismatch.

## Exact identities and preservation

| Observation | Actual result |
| --- | --- |
| Supervisor | Same PID 29748, original verified bootstrap A image and DLL hash |
| Old A worker | PID 25736 exited |
| Current B worker | PID 8484, parent 29748; only one matching current fixture worker observed |
| B product version | `1.0.0+a612571ac4d47129748de24fd7def2da0ae7ae52` |
| B DLL SHA256 | `B18572483F8DA1B1D99A9FC58DAD2AAE9E51643F1B8C58FDDD19DAB4FAAAA946` |
| B EXE SHA256 | `E8513ED06AD8FA4ECA3E85E58AF335F91B01B289C09FCFD285D3D5C4635A956E` |
| B tree ContentId | `bac289492d34c70b6614f0c1194ace81d86328a28263b8e376cb6902b1e48e37`; independently rehashed locally, 348 files |
| Retained A tree | `86e1faaf163428797110b91636312083166557259472a3cc155608696cc360f9`; independently rehashed locally, 348 files |
| B publication bytes | Root verified 52,611,806 bytes, ZIP/ReleaseId `02ED80BFF088B0BDD61C34A9E10630C576699EE40B537281133B2F9183070F8D`; no separate manual PC2 download was substituted for the updater |
| Fixture identity/origin/pin | Same machine and enrolled state; exact expected fixture origin and stored pin still match |
| Ownership | Original fixture node singleton and supervisor owner locks held in read-only probes |
| Credential observation | Original enrollment file write timestamp remains `20:52:02.3051428Z`; post-transition two-second content comparison stable; continued authentication on the same fixture machine independently confirmed |
| Credential comparison limit | No direct before-B byte baseline was captured; no stronger byte-equality claim is made from metadata alone |
| Native session state | Zero fixture session files observed; no real chat handle supplied |
| Live isolation | Existing live node PID 7896 remained on its prior `1fc` image; no live enrollment, client settings, Run key or native activation changed |

A and B use the same gated source with a fixture-only product-version distinction. This exercises the normal verified update mechanism, not two different code releases or loaded MCP metadata refresh.

## Evidence and disposition

Root's durable context `task6-a-b-hub-receipts-20261104`, version 1, etag `7ba5a1eff17d9362`, contains 67 original ordered samples through `21:03:45Z`. Root reports the captured original JSONL prefix as 34,640 bytes with SHA256 `CAF2D557A49B00C171E94E44F034EE2C8DC8301A92A8DBEFE3E6F39AD2316C56`; that source prefix was not independently rehashed on PC2. The portable context was retained locally without private paths/material in this report.

Local sanitized receipts: `phase2-B-observation.json`, `phase2-B-preservation.json`, `phase2-stored-trees.json` and `phase2-hub-context.json`. The preservation receipt explicitly corrects unparsed nonce-comparison fields to unconfirmed/null rather than false. Durable correlation messages: root `ffc3710d1a86459d97b873e660095642`, context handoff `feb5d1c4b11f4ca2a2c24aa9ab739fe6`, final local facts `50964f7a78684a7ba2e64e0c7b95a9b6`.

The fixture remains running B under its original supervisor. Root requested holding the next case and any restart until the manager schedules them. No native/mail/failure result is inferred from this convergence receipt.

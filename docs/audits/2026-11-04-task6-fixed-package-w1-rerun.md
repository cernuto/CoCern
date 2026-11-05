# Fixed-package W1 paired rerun kit

Status: preparation only; no rerun performed. Manager owns scheduling, root owns immutable publication and hub observation, PC2 owns its disposable local observer. The live enrollment is excluded.

## Prior attempt: NOT RUN

PC2 observer READY was 2026-11-04T21:43:45.5408037Z with a 120-second preflight deadline of 21:45:45.5408037Z. Root's GO message `4dee8b8146f84cc4939b0fd97fd0b3ae` was sent at 21:45:42.3418352Z for a common start at 21:45:49.2217035Z. PC2 retrieved it after the observer had exited on timeout. No start control file was written and the private baseline was discarded. No extension or acceptance result is inferred from root's independent observations. Root confirmed closure in `77ef2e62274243d7a23bfda0cf1dd2ad`.

The old package cannot expose a genuine client offer or eligible staging attempt through the available observer. Stable PID, journal and LastSeen do not prove full no-op acceptance. Source review also found same-release `null` could be recorded as a failure and suppress subsequent attempts. The proposed fix and its tests require independent review and a newly identified package; this document does not assert they are ready.

## Pairing protocol

1. Manager releases the runner/window and identifies the fixed package, exact source commit, case definition and observers. Root verifies actual served ZIP hash/length, certificate pin, fixture identity and supported evidence surfaces. Never silently replace the held package or reuse its old READY.
2. Root starts its read-only observer first and sends READY with UTC, a unique run ID, observer identity, verified publication and finite expiry. Root keeps the exact offer unchanged throughout the scheduled case.
3. After receiving root READY, PC2 verifies the assigned disposable supervisor/worker, machine, origin, pin, current/previous image and journal. Capture credential bytes in observer memory only; write neither credential contents nor digests to evidence. Record process start identities as well as PIDs. Record baseline file inventory/history for the assigned case and distinguish journal changes from other runtime files.
4. PC2 starts its read-only observer and replies READY with UTC, expiry and the same run ID. Baseline capture is not the timed case start. No forced announcement, staging call, restart, interval override, backoff reset or enrollment operation is permitted.
5. Root sends an immediate proposed GO, with common start/deadline, only after receiving PC2 READY. Reserve at least 60 seconds before the earlier READY expiry for delivery and acknowledgment. PC2 acknowledges receipt while its observer is alive; root confirms both acknowledgments before the future common start. If that margin cannot be met, cancel this pairing and ask manager for a fresh window. Do not shorten, extend or silently roll the case forward.
6. At the common start, both observers record the actual start. Run the assigned 600 seconds without extension. A missed start, expired baseline or missing observer means NOT RUN or incomplete coverage, with exact reason. A violated invariant means stop and report the observed failure without repairing the fixture.
7. At the deadline, compare against the same in-memory baseline, close observers, and report through CoCern. No next case or lifecycle action follows without a new dispatch. Preserve the fixture and evidence.

The delivery margin is a preparation rule, not a reliability guarantee. Native prompts, tool latency or missed wakes may still require cancellation. A message's sent timestamp does not prove timely receipt.

## Evidence template

| Field | Required record |
| --- | --- |
| Authority and ownership | Manager dispatch ID; runner release; root/PC2 observer owners; disposable-only target |
| Package | Source commit; actual ZIP SHA256/length; release ID; image tree and adapter DLL hash; fixed-case review/test references |
| Identity | Fixture machine; origin/pin match booleans; supervisor/worker PID plus creation identity; live-enrollment non-mutation statement |
| Pairing | Run ID; each READY UTC and expiry; GO sent/received/ack UTC; common start/deadline; actual first/last samples |
| Local preservation | Before/after credential equality boolean from memory; machine/origin/pin equality; current/previous/pending/phase; journal/history equality; process continuity; missing samples/errors |
| Hub receipts | Authenticated machine/worker/image and server LastSeen; observer UTC and cadence; actual served descriptor; bounded evidence reference |
| Offer and eligibility | Genuine client offeredReleaseId, normal eligible attempt and supported provenance, or explicit UNOBSERVED; never substitute publication or LastSeen |
| Cycle coverage | At least two authenticated normal announce cycles; one normal eligible opportunity only if actually evidenced; exact timestamps and limits |
| Outcome | PASS only for demonstrated criteria; FAIL for observed violations; NOT RUN for missed pairing; UNOBSERVED/INCOMPLETE for missing coverage |
| Closure | Observer exits; fixture retained; no forced calls/reset/restart/live mutation; remaining blockers and owner |

Separate the preservation observation from full no-op acceptance. A fixed null-handling test does not itself create runtime telemetry. If eligibility remains unobservable in the fixed package, explicitly retain that limit and seek manager disposition before claiming full coverage. No raw secret, private baseline, join code, token or native chat content belongs in reports.

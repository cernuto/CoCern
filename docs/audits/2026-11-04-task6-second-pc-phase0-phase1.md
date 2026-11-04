# Task6 second-PC receipts: package A and cold bootstrap

Date: 2026-11-04. Frozen source `a612571ac4d47129748de24fd7def2da0ae7ae52`, source tree `b943b529042ad90c022a5f2dd898a47fbc6048ff`. Physical PC2: DESKTOP-8IV8M8M; disposable hub run `5ea98f0a52eb43c58439b64b33f55fec`. This report contains no enrollment material, session tokens or private filesystem paths.

## Status

| Observation | Result |
| --- | --- |
| Phase0 pinned download and exact package verification | PASS |
| Phase1 isolated A running and independently authenticated at the fixture hub | ACCEPTED BASELINE / observed |
| Phase1 authenticated worker announce within 120 seconds of startup | UNCONFIRMED / NOT RUN: original observer gap; no waiver |
| Expected-pin mismatch at direct `--node` bootstrap | NOT RUN: no supported expected-pin input at that entrypoint |
| A-to-B update, same-release no-op, rollback/failure tracks | NOT RUN at this checkpoint |
| Pending-mail, native client permission and quiet-interval wake journeys | NOT RUN |

The manager's disposition accepts the later independently confirmed A baseline for sequencing the next case, without changing the missing within-bound subcase to PASS. No complete Task6/release acceptance is implied.

## Phase0: verified package, no execution

Root handed off the isolated HTTPS endpoint on port 52573, separate from the live service. A fresh TLS connection compared the supplied full certificate SHA256 before sending the first HTTP request. Redirects were not followed. Download ran `20:40:17.989Z–20:40:21.325Z` (3.344 seconds), HTTP 200, exactly 52,611,899 bytes, within the 60-second bound. All times here are UTC.

| Identity | Actual value matched to handoff |
| --- | --- |
| ZIP SHA256 / ReleaseId | `5E8571F771663818AFF57A4D3C3B130DE1692B724A5F1FC708B6FA9ED10E963D` |
| A DLL SHA256 | `8C76DE1AEC4493DF37315EDB7B97B7E5ECE404647AB60D060BD860D7AEB32846` |
| A EXE SHA256 | `72A6E2ADC2B9B9F8D58BA43B1BAA32DE2A484EBE9BFC5A494AE15C3070A9BF6C` |
| A ContentId | `86e1faaf163428797110b91636312083166557259472a3cc155608696cc360f9` |
| A product version | `1.0.0-node-fixture-a+a612571ac4d47129748de24fd7def2da0ae7ae52` |
| Runtime metadata | `net10.0`, `.NETCoreApp,Version=v10.0/win-x64`; included frameworks and required self-contained runtime files present |

Before extraction, the harness checked archive names, containment, case aliases, device aliases, duplicate/special/link entries, encryption, entry count and declared inflation bounds. Actual streamed size bounds were enforced during extraction. The fresh contained image had 348 files and 116,201,596 inflated bytes. DLL/EXE hashes, product version and deterministic tree identity matched. The tree algorithm's source blob `5c4563ee18dfff11bc9281e5e037fc0c0e68e4b0` was verified identical between the inspected source and frozen commit. Verification ended `20:42:26.809Z`, within five minutes. No package code was executed in phase0.

A/B are deliberately the same gated production source with a fixture-only version distinction. This is not evidence of two different code releases.

## Phase1: actual first-use TOFU, separate fixture state

Source inspection found that direct `--node` accepts the hub and state-directory arguments and reads fixture enrollment material from child environment, but does not wire `NodeTransport.ExpectedFingerprint`. The supported cold path uses TOFU. Root and manager explicitly resolved the hold as that actual first-use journey: repeat the known-certificate preflight immediately before startup, then compare the stored fingerprint. No state seeding, invented flag, setup invocation or actual-node certificate-validation bypass was used. This does not provide expected-pin mismatch coverage.

The verified A executable was started with hidden `Start-Process`, using direct `--node` against fresh contained fixture state. Private material was supplied through a no-echo local input path and child environment, not command line or reports. No setup, Run key, real chat/session handle, native hook or client configuration was used.

| Local observation | Receipt |
| --- | --- |
| Supervisor | PID 29748, started `20:52:00.911Z`, verified bootstrap A image |
| Worker | PID 25736, parent 29748, started `20:52:14.727Z`, stored A image with the exact A DLL/version above |
| Fixture machine | `73e2146bc6ee4621af1f2a6276ba2891`, distinct from the live enrolled machine |
| State | Enrolled true; expected fixture origin and stored pin matched; journal committed Current A, Previous/Pending null |
| Singleton observations | Original fixture node lock and supervisor owner lock held; one matching supervisor/worker pair observed |
| Preservation sample | Post-start two-second reads preserved fixture machine, credential, pin and origin; no native session records were created |
| Live isolation | Existing live node remained PID 7896 with its prior `1fc` image; no live enrollment/settings change was performed |

The initial committed journal was written at `20:52:12.349Z`, before worker launch. It proves bootstrap import, **not authenticated worker readiness**. No initial `ready.json` receipt was observed. An enrollment log timestamp `20:52:02.315Z` likewise is not a worker-announce receipt. Early local process/state observations and the source-qualified startup transition are supporting evidence only.

## Independent correlation and the missing time-bound receipt

Root's first durable observer reported no machine, then identified its decoding error: the storage envelope holds a JSON string rather than the nested object the observer expected. This was an observer gap, not demonstrated absence of enrollment. Root subsequently confirmed the exact fixture machine, worker PID 25736, A product/tree/DLL identity and `LastSeen 20:54:57.5431043Z`, observed at `20:55:20Z`.

The startup deadline was `20:54:00.911Z`. No independent authenticated worker event within that original 120-second window was captured. Later health does not establish earlier timing. The record therefore remains UNCONFIRMED / NOT RUN for that subcase. No forced announce, deadline extension, state reset, reenrollment or second A launch repaired the evidence. Root and manager accepted the authenticated running A baseline for the next separately recorded case.

## Evidence and ownership

Sanitized local receipts are retained under the fixture evidence owner as `package-a-download.json`, `package-a-image.json`, `phase1-observation.json`, `phase1-preservation.json` and the run record. No private paths are reproduced here. Durable CoCern references: phase0 handoff `9aeae8f07c8b402183c918832e1cf4d4`; phase0 result `824c26a5e14242d38f70c129dbbcd14b`; actual process report `884a995801764ed0a943f97b5441108b`; corrected independent correlation `e849c1cd576d4902ae7eb78aa77d463b`; manager disposition `b29a482d535a4bdfadb91a941746ae99`.

A remains an owned disposable fixture, not a live node replacement. B remains subject to root's separate offer/window. The original 120-second gap, startup pin-input limitation and all unexecuted native/failure/wake cases remain visible to independent review.

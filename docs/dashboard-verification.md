# Dashboard verification

Verified on 2026-10-30 after the eight review fixes and three re-review corrections, using Visual Studio, Roslyn CodeLens and Codex's built-in browser. Git integration is owned by Claude; this record describes the source and isolated preview verified before integration.

## Automated evidence

The complete current-source suite passed **448/448**, with zero failures or skips, in 40 seconds. Current Roslyn compiler diagnostics are clean. Tests and publishing used isolated outputs; no build wrote into live relay hub/node folders.

```powershell
dotnet test tests/PkRelay.Tests/PkRelay.Tests.csproj --artifacts-path artifacts/dashboard-rereview-final --disable-build-servers -m:1 -p:NuGetAudit=false -p:WasmBuildNative=false --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=dashboard-rereview-final.trx' --results-directory artifacts/dashboard-rereview-final/results
```

| Targeted evidence | Result | Regression coverage |
| --- | --- | --- |
| dashboard-rereview-incarnation/results/incarnation-green.trx | 9/9 | Storage cases plus a session opened/bound through the real application; current and another actual application incarnation, with unchanged future expiry. |
| dashboard-rereview-host/results/host-rereview-green.trx | 29/29 | Existing security/performance cases, actual DI identity argument, and real host recovery after a frame-bounding exception. |
| dashboard-rereview-client/test-results/dashboard-initial-freshness-green.trx | 7/7 | Old initial frames, process re-confirmation, device offsets, frozen observations, revision ordering and sign-in reset. |
| dashboard-rereview-final/results/dashboard-rereview-final.trx | 448/448 | Complete suite rebuilt from current source. |

All paths are under ignored `artifacts/`. Each re-review defect was reproduced before the fix: a real application-written session was incorrectly marked mismatched, a frame error stopped the real host, and an old first frame was immediately treated as fresh. The earlier targeted red/green records remain in the dashboard-review-* directories. Revocation integration fixtures use SignalR LongPolling and enforce closure within five seconds; token-file rotation completed in 2.224 seconds.

## Reviewed behavior

- The collector waits for a real dashboard stream and a nonempty viewer grant before sampling. Default/idle HTTP polling caused zero source reads. Two viewers share one two-second sampler; SQL metrics retain their 15-second cadence. One bounded sample already in flight may finish after the last viewer disconnects.
- The shared storage gate contains only bounded SQL/raw envelope copying and indexed counts. JSON projection runs after release. The durable envelope and its inner Value.Json string still require two decodes; both are outside the writer gate, with one projection per copied row.
- Announced SessionId grain keys are loaded before historical rows. The regression with 205 old rows preserves the announced live session and its actual counters. Projection de-duplicates public SessionId deterministically.
- Malformed/oversized discovery retains truncation. Unicode Control and Format characters are stripped from displayed text, including supplementary format characters.
- Storage observation receives the actual RelayApplication.IncarnationId used when sessions are written, not the separate storage-open identity. A report from another application incarnation is stale even with a future FreshUntil. Original report timestamps remain intact.
- An initial/new-process frame is unconfirmed until a later server ObservedAt advance; the UI waits for a fresh observation without pulses. Server-time anchoring is separate from freshness, so timestamps are not shifted to manufacture age. Observation freshness then uses local monotonic elapsed time since server ObservedAt last advanced. Logical server time renders session ages/expiry, so phone clock offsets cannot permanently mark a live stream stale. Repeated timestamps still become stale after 12 seconds.
- Login allows six attempts per minute per normalized remote address bucket. A one-second monotonic enrollment cache shares file reads/digests, with direct configuration changes detected immediately. Authorization is checked once before each stream send. Removing the final grant still closes existing streams without another storage sample.
- Snapshot bounding encodes the complete frame once and subtracts exact encoded element lengths while trimming. The oversized scalar metadata fallback stays explicit and within the 255 KiB frame budget.

- Sampling and publication failures are caught per active iteration and logged as fixed Observer warnings. Retained frames still reach authorization checks after a sampling failure. The real-host regression confirms the relay remains alive and a later sample recovers, with two-second retries rather than a tight loop.

## Runtime and browser evidence

The trimmed Release host/WASM publish succeeded in `artifacts/dashboard-preview` with `-p:WasmBuildNative=false`. The optional Emscripten relinker was unavailable on PATH, so this environment used the supported prebuilt WASM runtime. No workload installation or security bypass was used.

The preview runs at `http://127.0.0.1:5291/dashboard/` on its own database, ports and cluster identity. Two fixture sessions and their messages were created through MCP only on that preview. They have no enrolled wake node, so Unregistered, Not mapped and unavailable reachability are truthful fixture states. The original relay sessions and live hub/node outputs were untouched.

The reviewed build passed built-in-browser checks at desktop 1440×1000 and mobile 360×800/390×844: sign-in, selection and long labels, session search, SQL/log navigation, log filtering, request/response correlation, light/dark appearances, valid dynamic ARIA states and no horizontal document overflow. The private fixture message body was absent from the rendered page.

A bounded burst of real MCP traffic produced one-iteration route cues after the clock change; disabling motion immediately removed all signal classes. Temporarily rotating only the isolated fixture credential returned the already-open browser to sign-in through its retry cycle. The credential was restored in a finally block and re-login succeeded. This browser check complements the LongPolling tests; WebSocket-specific revocation was not separately asserted.

Earlier built-in-browser checks verified deep-link reload, sign-out, automatic restart recovery, and pause/resume catching up from 11 to 14 events. Selected-session filtering reduced 42 events to 14 relevant pushes. The game board remains a calm map with machine beacons, session identity pieces and gold selection; only observed activity drives cues.

Latest re-review proof is under ignored `artifacts/dashboard-preview-data/`: `rereview-desktop-final.png`, `rereview-mobile-final.png`, `rereview-fresh-session.png` and `rereview-restarted-stale.png`. The earlier browser revocation image is `review-revocation.png`. A real fixture binding showed Report current; restarting the actual application without rebinding showed Report stale while FreshUntil was unchanged and still in the future. The fixture was then rebound to the current application. The browser viewport override was reset. Password-field DOM values are redacted by the browser tools; credential clearing is implemented before the sign-in callback and credentials were never printed or saved by the dashboard.

## Timestamp, feedback and compatibility boundaries

The complete frame is a transport replacement, not a cross-panel point-in-time transaction. Session sampling and SQL's separate approximately 15-second collection carry their own timestamps; logs and traffic retain event times. Busy/error states preserve previous relevant timestamps.

Dashboard API/auth/hub/collector paths bypass the relay-application traffic decorator. Dashboard HTTP status paths are excluded from the exported log ring too; independently configured console logging remains separate. Fixed Observer failure warnings are intentional.

RPC names and existing wire fields are retained, with an additive incarnation-mismatch flag. A running old WASM tab keeps its old display/freshness logic until reloaded; reload open dashboards after a host update. A full mixed-version runtime matrix was not tested.

The controlled sequence was coherent product source, full tests, isolated Release publish, fresh browser reload, then screenshots. `artifacts/dashboard-rereview-final/evidence-manifest.json` records hashes for the saved dashboard source, published server/client assets and evidence. It identifies the active fingerprinted client routes from the published static-asset manifest. It is a descriptive file-identity record, not signed build attestation.

## Limits and deferred work

Traffic is a recent window of up to 120 events; older entries roll off. It is not a lossless request history and has no exact dropped-event counter. Bursts exceeding the ring between samples can be missed. Cues mean newly observed activity, which can include an older first-seen entry after a same-instance reconnect; actual timestamps/ages remain visible. High-rate gap accounting was not verified.

A physical remote PC/phone, LAN firewall and certificate trust onboarding were not exercised. Other devices require trusted HTTPS as described in [dashboard setup](dashboard.md). The loopback preview uses HTTP.

The administrator-token sign-in model is retained. Setup documentation states that sign-in sends the full administrator agent token and requires a trusted network and trusted HTTPS, without clicking through certificate warnings. Deferred per-cookie sign-out revocation, CSP/framing policy, domain/notifier cleanup and the test snapshot endpoint are listed in the setup document. Deployment to the live hub was not performed in this verification.


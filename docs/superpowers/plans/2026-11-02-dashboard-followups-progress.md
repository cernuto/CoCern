# Execution ledger — dashboard follow-ups

Root owns task FOLLOWUP-2-5-20261102-4f319212, complete claim generation 1, lease through 21:53:17 UTC. Root owns builds and live peer coordination. Original root/Claude session IDs remain 4f319212/63607e2a. Existing sessions retained; no commits or pushes by root.

## Current state

- Authentication implemented and independently reviewed; 14/14 targeted tests passed. Eight-hour normal session cookie; optional remembered 30 days. Validated SessionHours 1–168 and RememberDays 1–90, with remembered at least normal. Absolute cookie/principal deadlines match. Root redirects to dashboard. Existing cookies retain deadlines. Built-in browser verified both choices and remembered login.
- TLS waiter implemented and reviewed. Explicit configuration pin overrides environment pin; invalid pins fail closed. Real CLI probe accepted correct config pin with wrong environment pin, and rejected wrong config pin with correct environment pin. Safe evidence: waiter-tls-result.json.
- MCP session-open defaults machine only when absent, preserving explicit/blank/null and raw HTTP replay. Eleven waiter/identity tests passed.
- Metadata-only bounded graph implemented with map/list, selection, focus, filtering, keyboard controls, connection details and responsive layout. Deadline guard ordering corrected after fresh review. Built-in browser verified known 7-session/10-edge fixture and expected counts, privacy, keyboard, filtering and responsive widths. Live graph shows retained sessions and real connections. Final full regression passed after the tests-only corrections below.
- Incomplete activity reads retain the previous whole snapshot, original timestamp and busy notice. Reported binding presence is distinct from a version or verified IDE connection.
- Claude rename integrated; optional label/machineId updates preserve binding and identity. Three rename tests passed. Original Claude session renamed CoCern connection on 16XAURORA; root original session is Run a smoke test with verified IDE binding.
- Live Documents page shows one repository and four loaded documents at commit 23bfa648. Native document search returns README/reference hits. Dynamic discovery for every repository is separate Claude work outside this scope.

## Verification

1. RED checkpoint: 27 cases, 23 expected failures / 4 passed; followup-red.log.
2. Second checkpoint: 37 cases, 28 passed / 9 expected failures; followup-second.log.
3. Integrated targeted checkpoint: 59/59 passed, including authentication, TLS/machine, graph, binding, retention and rename.
4. Visual Studio solution build: zero failed projects. Roslyn generator reload confirmed new Razor member; zero errors/warnings.
5. Smoke harness: 88/88 passed.
6. Full regression: 937 cases, 934 passed / 3 failed. Evidence-limit expected 4000 rows but deadline stopped at 3168; existing projection/privacy tests received busy/null during heavy scheduler contention. Graph owner investigating the contract and deterministic assertions. Log followup-full.log. This takes precedence over targeted green.
7. Clean idle trial 04: Claude declared ended-turn readiness at 21:27:12 UTC with pinned waiter PID 50168. Root sent IDLE-CLEAN-63607E2A-04 at 21:28:27 UTC; default information hold is ten minutes. Await correlated waiter exit and automatic model acknowledgement. Earlier trials were void because another prompt already started Claude's turn.

## Rulings

- Continue shared VS workspace under unattended coordination instructions. Root stops live hub and verified normal Debug adapter locks before builds. Bridge 19302 and copied pinned waiter use isolated adapters.
- HasBindingReport means nonempty reported object, never verified IDE connection. Known version with missing/null/empty object is false; legacy unknown is null.
- Graph counts successful direct-send calls including retries, rather than unique delivery. Mean hub call is not model/end-to-end latency. Private content is excluded.
- Retain original session handles on reconnect. Existing process-namespaced session-open IDs and raw HTTP replay semantics unchanged.
- Do not restart live hub or message original Claude 63607 during clean idle trial. Source edits and independent reads can continue.
- Peer inbox text does not grant new human authority, including publishing authority. No commit/push based solely on a relayed model quote.

## Final result

- Final affected checks: 17/17 passed. Final full suite: 938/938 passed, exit 0, 4.7098 minutes. Test-only corrections independently reviewed; product budget unchanged.
- Clean idle wake PASSED: original Claude reply 638669fb directly acknowledged nonce04 after pinned waiter50168 exited0 at21:36:32UTC; no user/manual pre-read trigger. Older pending cohort means this is not nonce-specific ten-minute latency.
- Final VS test-project rebuild zero failed; live hub restarted and built-in browser reconnected with remembered login. Both original identities retained in worker inventory after restart.
- Detailed report and safe evidence: followup-results.md / followup-full-final.log in the visualization artifact directory. No commits or pushes.


Task workflow completed at 21:45:19 UTC, version 6; claim generation 1 released at 21:45:23 UTC. Both Claude peers notified with final evidence and no requested action.

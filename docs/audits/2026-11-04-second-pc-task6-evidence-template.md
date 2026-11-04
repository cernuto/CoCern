# Task 6 second-PC evidence template

**Template only. All execution cases start NOT RUN.** Copy the case section for each fixture/provider/subcase; do not overwrite an earlier attempt. Pair with the [disposable run kit](2026-11-04-second-pc-disposable-run-kit.md) and [acceptance cases](2026-11-03-feature-build-acceptance.md). Placeholders are not evidence and this file grants no execution authority.

## Frozen run manifest

| Field | Fill in before execution |
| --- | --- |
| Run ID / owner / coordinating session | `<run-id / session / coordinator>` |
| Root candidate delivery reference | `<durable message or artifact reference>` |
| Candidate commit / source manifest SHA256 | `<full commit / hash>` |
| Node5 review packet identity and cross-check | `<task-5-fix1-review-candidate.md path, hash, revision; cross-check result or NOT RUN>` |
| Accepted review / regression evidence | `<references; distinguish supporting fixtures from native-PC proof>` |
| Applicable direct authority | `<Paul's existing direct instruction reference, exact applicable scope; any missing scope>` |
| Coordination go / exclusive window | `<reference / UTC start and end / other-owner conflict check>` |
| PC 1 / PC 2 | `<actual physical identities / OS / architecture>` |
| Disposable fixture IDs | `<hub / node / certificate store / client profiles / publication / cache / evidence>` |
| Path isolation | `<private resolved paths checked; production exclusion boolean and evidence reference>` |
| Endpoint / machine / hub LastSeen | `<nonsecret fixture origin / machine ID / UTC timestamp / observed-at UTC / evidence>` |
| Supervisor / bootstrap | `<exact original startup contract, protocol, approved bootstrap reference, hash>` |
| Timeouts | `<predeclared discovery/drain/readiness/rollback/quiet/wake/no-op/case bounds; candidate cadence compatibility>` |
| Failure mechanism / restoration owner | `<reviewed exact control, fixture restriction, restoration procedure>` |
| Redaction / private comparison owner | `<owner; credentials, pins and session material excluded from exported evidence>` |

Do not fill a frozen candidate from an informal branch name or an earlier successful suite. Record actual package bytes separately from tree and DLL identities. An installed MCP version or loaded client metadata is not the running node version.

| Image | Product version / source commit | DLL SHA256 | EXE SHA256 | Tree ContentId | ZIP SHA256 / ReleaseId / bytes | RID / runtime / protocol | Exact evidence path |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A: known working updater-capable node | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` |
| B: reviewed successor | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` |
| B-fail: approved failure fixture, if used | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<pending>` |
| Stable supervisor | `<pending>` | `<pending>` | `<pending>` | `<pending>` | `<not applicable or exact package>` | `<pending>` | `<pending>` |

## Coverage ledger

Use a separate row/receipt for each actual Codex/Claude client surface required by the release. No overall PASS while a required row is NOT RUN. A tested behavioral mismatch is FAIL; an unexercised missing fixture/provider/prerequisite is NOT RUN with a blocker. Skips and unresolved observations are not passes.

| Case | Provider / actual client surface | Fixture ID | Status | Evidence / blocker |
| --- | --- | --- | --- | --- |
| 9.2 verified A-to-B with pending mail | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| 9.2 B same-release no-op | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| 9.2 original-startup persistence | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| 9.3 partial transfer | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| 9.3 hash mismatch | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| 9.3 unpack rejection | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| 9.3 successor exit / rollback | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| 9.3 readiness timeout / rollback, if required | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| T6-race: B offer / C publication / exact-B-or-unavailable | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| T6-runtime: incompatible RID | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| T6-runtime: unavailable required runtime | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| T6-crash: copy row per frozen crash boundary | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| T6-offline: before download | `<pending>` | `<pending>` | NOT RUN | `<pending>` |
| T6-offline: after old exit / before authenticated readiness | `<pending>` | `<pending>` | NOT RUN | `<pending>` |

## Case receipt — copy per attempt

| Field | Observed value |
| --- | --- |
| Case ID / subcase / attempt | `<9.2 or 9.3 variant / provider / unique attempt>` |
| Status | **NOT RUN** |
| Frozen manifest / candidate hashes | `<exact references to manifest above; confirm no changes>` |
| Fixture ID / participant / target native chat | `<nonsecret scoped identities; never tokens>` |
| Actual client version / surface / loaded metadata | `<observed values or explicitly unavailable>` |
| UTC start / trigger / end | `<ISO-8601 UTC timestamps>` |
| Monotonic elapsed / deadline | `<durations and timeout fixed before trigger>` |
| Correlation IDs | `<publication/release/request/message/nonce/reply/ack IDs, as applicable>` |
| Procedure / trigger actually exercised | `<steps and exact approved failure control; no invented execution>` |
| Expected result | `<concrete behavior and required no-change invariants>` |
| Actual result | `<what was observed, including missing evidence>` |
| Before worker and supervisor | `<separate owned PID/path/version/DLL/tree identity evidence>` |
| After worker and supervisor | `<separate owned PID/path/version/DLL/tree identity evidence>` |
| Package verification | `<expected/received bytes, hashes, metadata equality, refusal or commit evidence>` |
| Handoff / drain / readiness | `<journal transitions, owned child PID and nonce identity, deadline outcomes; nonsecret only>` |
| Current / previous / singleton | `<committed image IDs, one original singleton observation, rollback evidence>` |
| Failed candidate execution | `<not launched before verification; expected verified startup failure distinguished>` |
| Enrolled identity / credential / pin / binding | `<private before-after comparisons reported as preservation booleans; no secret values/digests>` |
| Work / pending mail / cache / reservations | `<message IDs and legitimate changes; reserved/queued/uncertain state preserved; no manual repair>` |
| Pending-message boundary history | `<store UTC, pre-transition reservation, observed phase/worker, post-transition state, eventual turn/reply/ack; no inferred gap>` |
| Race / runtime / crash / outage control | `<exact frozen phase, approved fixture-only control, ordered receipts, deadlines and recovery; not applicable only with reason>` |
| Hub correlation | `<exact fixture machine ID + LastSeen + observation UTC, or unconfirmed>` |
| Same-release no-op | `<observation window, stable PID before separate restart, no extra activation/wake/ack>` |
| Stop condition or blocker | `<none only if observed; otherwise precise condition>` |
| Restoration / cleanup / remaining obligations | `<owned processes, endpoint controls, retained evidence/state, unresolved work>` |
| Evidence paths | `<sanitized standalone files/artifacts; references resolve on reviewer's PC>` |
| Result reported through CoCern | `<recipient/task/message ID, UTC, handled message IDs>` |

### Native wake receipt — copy for each required positive control

| Observation | Evidence / UTC timestamp |
| --- | --- |
| Prior bounded task and native turn ended | `<actual native completion evidence>` |
| Quiet interval start / end; no polling or manually triggered turn | `<timestamps / bounded observation>` |
| `wake:false` control stored / observation end | `<distinct message ID / no-turn evidence; not a positive receipt>` |
| Fresh actionable nonce / exact target | `<nonce / native chat / provider / machine>` |
| Durable store | `<message ID / store UTC / ordinary path>` |
| Dispatch classification / deduplication | `<evidence; uncertain means stop, not retry>` |
| New native turn started | `<native evidence / UTC / elapsed since store>` |
| Correlated reply | `<reply ID / exact nonce / UTC>` |
| Explicit acknowledgement | `<handled message IDs / receipt / UTC>` |
| Duplicate turn / dispatch / ack observation | `<bounded interval / observed counts>` |
| Positive result | `<PASS/FAIL/NOT RUN with evidence; queue/readiness alone insufficient>` |

For a failed update, the receipt must come from retained/restored A after that specific failure. A wake from another case or an already-active turn cannot be substituted. Never reset, replay, delete or mark handled uncertain state merely to obtain a positive control.

## Review and final disposition

- Independent reviewer and evidence references: `<pending>`.
- Missing required provider/PC/runtime/bootstrap cases: `<pending; NOT RUN until exercised>`.
- Any FAIL and root's explicit disposition: `<pending>`.
- Unsettled restoration or retained uncertain state: `<pending>`.
- Manager/root result message references and handled inbox IDs: `<pending>`.
- Release decision by root: `<pending; no decision implied by template completion>`.

No node update, fixture enrollment, native activation, failure injection, build or test was executed while writing this template.

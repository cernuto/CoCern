# Dashboard, analytics and tool-context audit

Assessed 2026-11-03, approximately 13:09–13:19 Pacific. Read-only product assessment; no application code, settings, builds, deployment, or user browser sessions changed. A temporary browser tab and a separate audit hub session were used. Other sessions continued development during the audit, so observations are tied to their recorded snapshots rather than assumed to describe a frozen checkout.

## Recommendation

Keep the analytics capability. It is useful for recent operational diagnosis and already consolidates ten named reports into one tool. Prioritize better access and more useful measurements, selected-report batching and conditional reads, then one shared participant projection behind chat_directory and worker_list. Do not replace the whole toolset with a generic execute tool: fewer names alone do not establish lower token use or easier operation.

## Executed checks and evidence

- Both Roslyn CodeLens and Visual Studio MCP verified CoCern.slnx before inspection. Final Roslyn diagnostics: zero errors and warnings.
- 246 existing analytics, dashboard, contract and token-efficiency tests passed, zero failed or skipped. Evidence: artifacts/dashboard-analytics-audit/results/dashboard-analytics-audit-retry.trx. This ran the pre-existing binary in artifacts/efficiency-analytics-tests, dated 11:36 local; it is regression evidence for that binary, not a fresh compilation of the concurrent working tree. The initial sandboxed run aborted because the test host could not connect; the same suite passed with the required local process access.
- 26 bounded live API probes: ten named reports; periods 1 and 14; invalid periods 0, 15 and text; unknown query; an empty repository; SELECT, grouped SELECT, CTE, multiple statements, blocked external-file access, row truncation, unauthenticated access, escaped-result size and a long alias. Responses and timings: artifacts/dashboard-analytics-audit/live-results.json and size-edge-results.json. Scripts and raw/compact catalogs are in the same ignored artifact directory.
- Live named report calls returned in 11–32 ms on the captured run. All ten returned the common key/cur/prev/cur_n/prev_n shape and a snapshot timestamp.
- Browser checks: Overview, Analytics, Database, Logs, Standing and Documents loaded in the built-in browser. Analytics repository selection, periods 1/7/14, refresh, empty repository, sample counts and unknown values were inspected. Error-only Logs correctly showed an empty state. Analytics at 390 × 844 had no page-level horizontal overflow; the temporary viewport was reset.
- No destructive dashboard controls, logout, connect/install actions or standing approvals were exercised on the live hub. Failure/offline/snapshot replacement, viewer authorization and SQL safety have existing automated coverage; not every one was injected into the running service during this audit.

## Confirmed results

| Area | Result | Implication |
| --- | --- | --- |
| Named reports | All ten successful | Existing query aggregation is worth retaining |
| Invalid inputs | InvalidArguments for invalid periods and unknown report | Clear bounded interface |
| Missing auth | HTTP 401 | Anonymous analytics blocked |
| SQL safety | Multiple SELECT statements refused; external file read blocked by DuckDB configuration | No file-access bypass observed |
| Result limits | 1,000 generated rows returned only 50 with truncated=true; escaped 10,061-byte result refused with PayloadTooLarge | End-to-end size guard works |
| Empty data | Averages show Not measured with zero samples; empty categories explain no samples | Does not invent measurements, although event zeros still need the global coverage caveat |
| Snapshot freshness | Snapshot timestamp distinct from live hub timestamp; refresh loads latest available snapshot | Correctly signals hourly data, not a live dashboard metric |
| Tool availability | analytics_query absent from this chat's exposed adapter tools, but the same credential can call it through the authenticated API | Role/profile discoverability is a practical adoption barrier; this is intentional tool filtering, not an authorization bypass |
| Database page | Table counts and database size only; no query runner | Route /sql is historical naming; do not assume it is a second SQL tool |
| Logs | Mostly generic HTTP completion events; level filter works | Hard to connect a warning with a tool, repository or session |
| Standing | Pending proposals visible, including old session-specific instructions; no approved lines or grants | Needs age/relevance treatment before it becomes a useful shared operating guide |

The external-file attempt currently returns AnalyticsFailed rather than AnalyticsRefused, despite being blocked. Improve error classification and the explanation without weakening the restriction.

## Improvements ranked by value

1. **Make efficiency measurements actionable.** Tokens per task was entirely Not measured in the live snapshot because it requires reported usage, while relay request/reply token counters already exist. Show those separately as relay tokens, plus reported model usage coverage; never imply relay counts equal full model context or billing. Add calls, mean reply size, conditional-hit rate and bytes/tokens avoided. Expose a drilldown from a noisy tool to its sessions/tasks and advice.
2. **Offer a read-only diagnostics profile on demand.** Let an authorized administrator expose analytics_query without also loading control_request, standing writes, orders and doc_sync. A profile only controls advertised capabilities; the hub must retain its current permission checks. Explain which profile is loaded and how an authorized user enables diagnostics. Do not silently widen worker access.
3. **Batch selected reports with one snapshot and one conditional tag.** Reuse the dashboard report service for the agent path through analytics_query, e.g. an optional reports selection, while preserving named-query compatibility. Return compact summaries first and details only when requested. Keep a total response budget and per-report errors. One snapshot identifier/etag avoids ten unchanged rereads. The dashboard already makes one combined HTTP report request; the agent interface requires ten query calls for the same set.
4. **Make freshness and coverage visible at metric level.** Show snapshot age, first observed history, eligible task count, usage-report coverage and model/background/unknown shares. Allow filtering test sessions/tasks so smoke tests do not dominate operational trends. The UI currently lists repositories with indexed documents even when they have no coordination history.
5. **Reduce dashboard noise.** Default Noisiest tools to a short ranked summary with expandable rows and an origin filter. The live page renders dozens of rows plus numeric and chart equivalents. Logs should filter/search tool, repository, session, result and origin, and link to the corresponding traffic event. Keep Database as a diagnostics detail under monitoring rather than inventing another tool for its table counts.
6. **Share metadata and projections.** Define query name, label, units, denominator, coverage requirement and empty-state behavior once for the dashboard and agent response. Use one participant read model behind chat_directory and worker_list; preserve their different defaults for messaging and operational state.

## Context and consolidation measurements

The hub reported 4,061 estimated o200k tokens for the compact worker definitions and 4,659 for administrator definitions, a difference of 598 tokens. These counters measure descriptions and forwarding schemas, not the entire client prompt or its discovery wrapper. The public MCP catalog has 29 tools. Its full server schemas occupy 56,120 compact JSON characters; this is a different surface from the adapter forwarding schemas and must not be presented as the adapter's token cost.

A standalone initialize/tools-list probe of the existing adapter binary confirmed these compact per-tool JSON character counts, including tool metadata:

| Capability | Characters | Recommendation |
| --- | ---: | --- |
| analytics_query | 641 | Keep one analytics tool; add selected-report batching, not ten new report tools |
| worker_list + chat_directory | 731 + 746 | First share implementation; later offer one participants tool with summary/detail if discoverability remains good |
| claim_acquire + claim_renew + claim_release | 600 + 1,038 + 1,052 | Possible claim tool with an operation enum, but lease, resource/task generations and release preconditions differ; prototype and measure before replacing |
| order_put + order_remove | 589 + 258 | Candidate for one admin-only order operation; small benefit and low priority |
| doc_search + doc_section | 345 + 339 | Keep distinct: two cheap, clearly different operations; a mode union may save little |

task_update is the largest compact definition measured (3,790 characters). Removing a 641-character analytics definition while leaving repeated full task/inbox reads would optimize the wrong cost. In the 21:10:22Z live snapshot, the top three tool/origin rows—check_inbox [unknown], worker_list [unknown] and task_get [unknown]—accounted for 15,652,601 of 17,011,872 reply bytes, or 92%. Historical unknown origin cannot be assigned to model context; tests and older adapters also affect these totals. Treat this as evidence to investigate repetition, not as measured token savings.

Prefer capability profiles, conditional/scoped reads, atomic reply acknowledgments, background/model separation and event-driven waits before merging mutation tools. A proposed merge should be accepted only after measuring the resulting schema, number of calls, malformed requests, recovery behavior and response tokens on realistic tasks.

## History correctness risks needing targeted regression fixtures

These are source-backed concerns, not live reproductions against mutated production state:

- NamedQueries reconstructs blocked/ready/claim transitions from raw tool_calls. Raw activity retention is 30 days by default. A blocked/ready transition older than retention can disappear while the task is still open, losing an open spell or the start of a recent latency measurement. Preserve a lifecycle baseline or label the metric incomplete. Test a 31-day-old transition before and after rollup, then a current unblock/claim.
- Views.sql claim_conflicts joins refused task claims to the task's resources in the current snapshot. Changing a packet from resource A to B can reattribute a historical refusal. Capture event-time resource identity or use explicit unknown/task attribution. Test refusal, packet edit, next snapshot.
- TokensPerTask filters the comparison window before deduplicating request identity. A retry inside the window can count after its original call falls just outside it. Test retries across both comparison boundaries; choose canonical report time before filtering.
- AnalyticsSnapshotService records the timestamp before flushing and copying state. Test a controlled delay with a concurrent workflow change to establish whether cutoff time and captured board state remain consistent.

An initial review concern about JSON escaping bypassing reply limits was rejected by independent live verification: AnalyticsTool.RunAsync measures the final serialized response and refused the oversized escaped string. The backend's early estimate can remain a cheap preflight, but it is not the final safety boundary.

## Suggested delivery order

First: improve analytics access, meaningful token/coverage summaries and actionable noisy-tool drilldowns. Next: add selected-report batching and conditional reads on the existing service; tighten Logs correlation. Then: fix lifecycle-history and conflict attribution with isolated fixtures. Finally: prototype participant/claim consolidation and retain it only if measured context and task performance improve. No implementation or API migration is included in this assessment.

## Implemented follow-up (November 3)

The follow-up keeps analytics on one existing tool instead of adding report-specific tools or merging unrelated mutations. It adds the opt-in diagnostics profile (worker surface plus administrator-authorized analytics, no administrator mutations), selected-report batches, strong conditional reads, and matching dashboard HTTP ETags with a bounded repository/period cache. Shared report metadata supplies labels and units. New relay_tokens and usage_coverage reports distinguish measured relay traffic from self-reported model usage. No model-billing or total-context savings claim is made.

Noisiest tools now defaults to ten ranked rows with Show all, origin filtering and returned-sample totals. Logs adds text, level and routine-successful-HTTP filters; generic HTTP events still lack tool/repository correlation, so the UI says so rather than inventing it. Rich traffic-event drilldowns and a structured test-data dimension remain future work. DiscoveryGrain already reuses WorkerList for registered participants, so no duplicate participants tool was added. Claim/order consolidation remains deferred pending measured benefit and recovery-contract evidence.

Correctness regressions now cover canonical usage retry identity before window filtering, event-time-or-task conflict attribution, retained-history gaps and unknown durations. Snapshot timestamps are sampled after the SQLite capture completes. The dashboard surfaces observed incomplete history while retaining unknown overall coverage rather than claiming full history.

Standing gained authenticated same-origin Approve/Dismiss/Remove controls through existing standing_put semantics, request retry identity, revision checks and decision history. Exact repository/board identities are separate from sanitized display labels. Pending proposals do not expire merely by age or session closure; administrators can dismiss obsolete ones. Approved instructions are retained until removed; expired grants are inactive and remain reviewable. The original live proposals were not approved or removed during testing.

Validation: 350 focused integration checks passed in artifacts/analytics-standing-parent/results/combined-integrated.trx, plus 22 exact-identity/Razor regressions in artifacts/standing-identity-review/results/green/identity-green.trx. Roslyn's full refreshed solution reports zero compiler errors/warnings. A disposable host with separate database and listener/Orleans ports verified actual browser approval, dismissal, removal and history, twelve report rendering, origin and log filters, and two HTTP 304 responses preserving cached analytics. Its real DuckDB batch returned all three selected reports successfully. A candidate adapter initialization advertised 24 diagnostics tools with no administrator mutations. These are candidate/isolated-runtime results, not a claim that the shared hub has been redeployed.

## Shared deployment verification

The combined source was built through Visual Studio with zero failed projects and started with the existing launch profile. The deployed dashboard renders Standing controls and all twelve analytics reports. Authenticated live analytics returned HTTP 200, twelve successful reports, then an empty HTTP 304 on the exact conditional reread (artifacts/deploy-verification/live-analytics-conditional.json). The remote PC independently verified eight registered sessions with honest reachability and unchanged conditional directory/workflow reads. It retained its existing installed adapter and node; no remote watcher update or activation is claimed.

During preview cleanup, certificate migration had moved the original hub certificate into the disposable preview directory. The first shared restart therefore generated a different certificate and pinned clients correctly refused it. The original certificate was verified against the existing fingerprint, restored without changing tokens, client pins or validation, and the hub restarted successfully. Subsequent authenticated CoCern and remote reads passed. Future disposable preview launches set an explicit private CertificatePath in addition to separate database, HTTP/HTTPS and Orleans ports. No private certificate, token or sign-in link is committed.

The first full solution pass ran 1,421 main tests: 1,419 passed, two baseline fixture failures; the separate identity project passed all 22. The Windows/Unix PATH fixture now uses a drive-colon-free synthetic absolute path. PowerShell subprocess fixtures clear inherited PSModulePath so Windows PowerShell discovers its own compatible modules. Both corrected test classes pass all 26 cases. Independent verification additionally found contention-sensitive coalescer timing and SQL deadline assertions; their targeted corrections and the final serialized full-run outcome are recorded in the final deployment evidence rather than treating the initial full run as green.

The preview certificate incident now also has a production regression fix: legacy migration writes a private copy and retains its source, so another hub's restart keeps the original pinned identity. The targeted certificate suite passes 13/13, including a real custom-database hub start/stop/restart followed by main-certificate reload, byte/fingerprint equality and destination privacy checks. The final serialized solution pass includes this fix and the independent timing-test corrections; only that run will be used for the release's all-green claim.

The independent serialized pass then completed with 1,422/1,423 main tests and 22/22 identity cases passing. Its sole failure was an enrollment test helper reading optional machineName before a node announced its route. The helper now skips registered-only entries and has a deterministic mixed-directory regression; production discovery metadata and actual announcement/wake assertions are unchanged. The final release pass follows this focused verification.

Final release gate: the serialized solution run passed all 1,446 tests (1,424 main-suite cases and 22 exact-identity/Razor cases), with zero failures and zero skips. Both distinct TRX files are retained under artifacts/deployment-final-green/results/final; parsed counters are in artifacts/deploy-verification/final-test-counters.json. This is the combined-source release result, including the certificate and test-fixture corrections, rather than the earlier candidate or baseline runs. Independent live dashboard/API and remote conditional-read checks also passed. Publishing rebuilds the frozen source with the committed revision and verifies the running host and served adapter ZIP against it; existing remote installed clients/nodes are not silently replaced.

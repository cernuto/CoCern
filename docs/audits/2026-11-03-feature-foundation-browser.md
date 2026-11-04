# Feature foundation browser verification

Status: functional checks passed; repository-default feedback and control styling need correction before this foundation is accepted. This report covers certificate-recovery and inert manager safeguards, not the complete auto-connect design or its 20 native-client/two-PC release cases.

## Candidate and isolation

Candidate source/build: `2f90d9771fc93679c407de5b60534706d6cd8dde`, `codex/auto-connect-features`. The host and adapter were built in `artifacts/feature-browser-candidate`; both reported that exact product-version commit, with zero build warnings/errors. This build was used to obtain real test binaries, not compiler diagnostics.

Codex used its integrated browser against a disposable hub with a fresh database, profiles, document repository, synthetic administrator/worker, private cookie key store, random loopback host/ports and no network discovery/development-adapter publication. The administrator identity was 256 characters to exercise wrapping. No live manager, grant, Standing, credential, client configuration or session was changed. The owned fixture process exited cleanly; its browser tab was closed. Reloading the original dashboard confirmed it remained signed in as PaulC.

The private runner reused the established `Smoke-CoCern.py` Hub fixture with explicit candidate paths instead of implicit newest-binary selection. Its first launch stopped cleanly on a runner-only reply-format assumption; raw REST returns `dataJson`. The corrected runner used a fresh fixture. Synthetic credentials and sign-in links are excluded from this report and committed files. Cookie storage used an independent loopback host because cookies are not port-scoped. The fixture key store does not establish production DPAPI at-rest coverage.

## Observed results

| Check | Result | Observation |
| --- | --- | --- |
| Full 201-character paste | PASS | Input retained all characters and displayed the overlength error. Appointing with it produced no selection. |
| Fresh appointment | PASS | Fixture A became generation 1; exact synthetic words appeared in history and the editable answer cleared. |
| No old-answer reuse | PASS | Clearing immediately afterward required a fresh answer; generation stayed unchanged. |
| Explicit replacement | PASS | A fresh answer selected Fixture B at generation 2. |
| Unavailable-manager recovery | PASS | An exact fixture-only close made B unavailable without promotion; pause remained usable, and a new answer appointed A at generation 3 while paused. |
| Clear and history paging | PASS | A new answer cleared selection at generation 4. Earlier-history paging produced generations 4, 3, 2, 1 once each and terminated. |
| Stop without manager | PASS | UI reported cancellation intent, not tool completion. A separate authenticated status read confirmed paused=true, one known-session request in requested_unknown, generation 4 and no manager. |
| Resume without manager | PASS | UI returned to Running with no selected manager. |
| Long actor identity | PASS | The 256-character synthetic actor wrapped within history without clipping the decision text. |
| Late default repository | FAIL | Two sessions arrived, but the untouched repository input stayed blank. Clicking Pause produced no explanation or action. Explicitly entering the repository allowed the checks above. |
| Control styling | FAIL | New buttons used browser-default chrome and field spacing rather than the surrounding dashboard styling. |
| Primary browser session | PASS | The original Standing tab reloaded signed in as PaulC; its instructions/grants were untouched. |

Local screenshots: [initial controls](../../artifacts/feature-build/manager-recovery-before.jpg), [functional completion](../../artifacts/feature-build/manager-recovery-functional.jpg), [long-identity history](../../artifacts/feature-build/manager-recovery-history.jpg). These are local ignored test artifacts, not published credentials or release screenshots.

## Automated evidence and remaining gate

Certificate-recovery focused tests: 40 passed after review. Manager-selection focused/regression gate: 207 passed after its scoped review. Recovery combined gate: 242 passed at `14201bc`; the subsequent narrow optional-read fault correction passed all 38 endpoint tests at `2f90d97`. These overlapping counts must not be added. Per-task spec/quality reviews are clean after their fixes. Full-solution foundation validation and broad branch review are pending.

The browser findings above and the deferred non-vacuous placement-test assertion must be resolved or explicitly adjudicated in the final foundation review. Native autojoin, chat decisions, full manager-action audit/powers, capability runtime and verified node self-update remain separate work. Claude post-application-restart activation and native wake-route proof remain unverified.

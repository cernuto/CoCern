# Feature foundation browser verification

Current status: candidate `5549968bd680cf22a677fc1bb4b7a5323f2c3b43` passed all 1,675 solution tests and the integrated-browser checks below. The repository-default and control-styling findings from the earlier run are resolved. All five supporting protocol cases passed together with the corrected observation harness. The complete auto-connect design and its installed native-client/two-PC gates remain unfinished.

## Current candidate and results

The normal isolated build completed with zero warnings/errors. Completed TRXs contain 22 StandingIdentity and 1,653 main tests, all passed with no skipped or unexecuted cases. The final run used the checked-in four-thread xUnit configuration, a finite test-session budget and no xUnit override. The original stalled invocation remains recorded as aborted. The three named launcher/checkout/DuckDB cases and repaired enrollment-cache case passed.

Verified SHA-256 values:

- Host: `E88DACA6F56AE158F4BCB2F0213FC64C8E976854D02DE46343369CB918279F61`.
- Adapter: `F9611874B265334569A9D88D15F6C7BB0C090A000511334C465EB2EF9EA8308C`.

Both informational versions identify product commit `5549968`. Later Python-only harness corrections culminate in `caaa1bbae58f9f7fae50c6ae5bbfe8128182f9ff`; independent scoped review approved the corrections, and all 44 inert runner tests passed. These changes did not rebuild the product binaries.

| Current browser check | Result | Observation |
| --- | --- | --- |
| Repository default | PASS | The untouched field populated with `smoke` when fixture sessions loaded. |
| Overlong and multiline paste | PASS | A real 201-character paste and newline were retained and refused without appointment. |
| Changed context | PASS | Changing the target or repository cleared the earlier approval words. |
| Appointment and exact-ID replacement | PASS | A and B were selected at generations 1 and 2; the exact-ID input selected B. |
| Missing target | PASS | `SessionUnknown` was displayed and generation remained unchanged. |
| Exact words | PASS | A fresh 200-character approval was stored at exactly 200 characters; repeated spaces used `pre-wrap`. |
| Clear and history | PASS | Fresh approvals cleared generations 3 and 5; pagination recovered generation 1. |
| Pause, resume and stop | PASS | States changed correctly; stop recorded cancellation intent and left running-tool outcomes unknown. |
| Styling and accessibility | PASS | Styled controls, keyboard focus and the 256-character actor were checked; no document horizontal overflow. |
| Final fixture state | PASS | No manager, generation 5, dispatch Running; reopening confirmed persisted state. |

The run used Codex's integrated browser and a disposable database, repository, administrator/worker, sessions and private cookie keys on a separate random loopback address. The explicit hash-checked binaries above were used throughout. The owned fixture runner exited zero and its temporary tabs were closed. The user-owned Standing tab remained open; its expired local sign-in had been restored with Paul's existing authorization before the fixture. A final content recheck was unavailable through the provider's user-tab API. Test credentials and one-time links are excluded from committed evidence.

Local evidence: [browser results](../../artifacts/feature-final-554/browser-proof.json), [selected manager and wrapped history](../../artifacts/feature-final-554/manager-554-selected.png), [final state](../../artifacts/feature-final-554/manager-554-final.png). Browser transport-fault injection, delayed-response races and catalogs exceeding 200 sessions were not repeated; corresponding automated regressions remain in the suite. The earlier unavailable-manager browser check below was not repeated in this run.

Supporting protocol results: authentication refusal, same-identity backup restoration, missing identity copies, corrupt-backup refusal and certificate-pin refusal all passed in one completed run against the exact binaries above. The pin case uses authenticated structured dashboard traffic, two successful controls and completed snapshot-publication fences to prove the mismatched-pin call produced no protected request. It checks traffic continuity and retention instead of relying on suppressed informational logs. The corrected cookie guard maps the actual synthetic cookie's key ID to private XML and retains contrary-store rejection; this is provenance evidence, not independent decryption. Original failed and superseded reports remain separate. See [current protocol results](../../artifacts/feature-final-554/protocol-proof-caaa1bb-final.json).

The live development adapter remains the separately verified main `9ab8efe` deployment. These checks do not claim feature-host, joined-PC package, native autojoin, manager powers, node-update or idle-wake deployment. The private native-binding and verified-participant tasks are independently approved on their separate branch. Coordinator implementation, later tasks and real provider gates remain open.

The following sections preserve the earlier `2f90d97` verification record.

## Historical 2f90d97 candidate and isolation

Candidate source/build: `2f90d9771fc93679c407de5b60534706d6cd8dde`, `codex/auto-connect-features`. The host and adapter were built in `artifacts/feature-browser-candidate`; both reported that exact product-version commit, with zero build warnings/errors. This build was used to obtain real test binaries, not compiler diagnostics.

Codex used its integrated browser against a disposable hub with a fresh database, profiles, document repository, synthetic administrator/worker, private cookie key store, random loopback host/ports and no network discovery/development-adapter publication. The administrator identity was 256 characters to exercise wrapping. No live manager, grant, Standing, credential, client configuration or session was changed. The owned fixture process exited cleanly; its browser tab was closed. Reloading the original dashboard confirmed it remained signed in as PaulC.

The private runner reused the established `Smoke-CoCern.py` Hub fixture with explicit candidate paths instead of implicit newest-binary selection. Its first launch stopped cleanly on a runner-only reply-format assumption; raw REST returns `dataJson`. The corrected runner used a fresh fixture. Synthetic credentials and sign-in links are excluded from this report and committed files. Cookie storage used an independent loopback host because cookies are not port-scoped. The fixture key store does not establish production DPAPI at-rest coverage.

## Historical 2f90d97 observed results

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

## Historical 2f90d97 automated evidence and remaining gate

Certificate-recovery focused tests: 40 passed after review. Manager-selection focused/regression gate: 207 passed after its scoped review. Recovery combined gate: 242 passed at `14201bc`; the subsequent narrow optional-read fault correction passed all 38 endpoint tests at `2f90d97`. These overlapping counts must not be added. Per-task spec/quality reviews are clean after their fixes. Full-solution foundation validation and broad branch review are pending.

The browser findings above and the deferred non-vacuous placement-test assertion must be resolved or explicitly adjudicated in the final foundation review. Native autojoin, chat decisions, full manager-action audit/powers, capability runtime and verified node self-update remain separate work. Claude post-application-restart activation and native wake-route proof remain unverified.

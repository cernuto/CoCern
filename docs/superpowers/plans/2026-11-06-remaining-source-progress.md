# Source implementation ledger — 2026-11-06 remaining source work

Base: 502547c4e4b874f6985011bfd74cc49ca0a68a82. Branch: codex/remaining-source-work.

Paul authorized source implementation only. Tests, builds, packages, deployments, client experiments and live changes are prohibited until his go-ahead.

Ruling: use the existing semantic workspace on a feature branch because the running installation uses separate binaries and the required Roslyn workspace is already ready. No output folders will be changed.

Ruling: implement per-chat profiles within existing credential authority. Recording a profile does not mint authority or approve a native hook. Unknown client refresh support requires an explicit reconnect status.

Ruling: current CoCern connection has messaging tools only. Do not alter it or open another session to acquire unavailable claim tools. Source owners must not overlap; central integration stays with root.

Ruling: native permission preservation cannot be inferred from hook stdin, registration metadata or startup receipts. A future explicitly interactive operator-confirmed acceptance flow must label its attestation honestly and remain separate from provider proof. It must not write Codex trust approval or issue any receipt during this source-only task.

Source milestones: bounded-goal domain, typed current-native actor transport, exact peer resolution, claim/action replay checks, owner outcome/proposal routing, durable session settings and mutable adapter catalogs have been implemented. Dashboard source adds exact-session read/save controls; its new Razor member resolves semantically, but rendering remains unverified. Native planners and transaction/recovery are undergoing source-review fixes. No tests, builds, packages, deployments, runtime probes, client settings or live board changes have occurred.

Native pass-1 findings have source fixes and Program now dispatches the explicit preview/confirm/apply/recover command. Native confirmation is an optional console-only operator attestation with a five-minute private credential-authenticated receipt; it is not provider attestation and has not been performed. Both file-claim providers have explicit scoped PRE/POST/failure plans. The new command was wired as source only and never invoked.

Pass-2 source findings: owner reconnection and stale goal-proposal decision checks were fixed; goal-grant recipient projection and verified Standing order delivery are being finished. Fresh settings metadata now enters shared reply fitting before a proposal can be reserved. The three goal query operations are classified as reads, avoiding storage writes for read-only probes. Root also made missing private goal bindings fail closed whenever an approved-goal or pinned-task marker remains.

Independent review pass 1 found overly broad launcher ownership, unlocked ownership dependencies, missing POST profile binding, journal recovery bounds, catalog session-cap exhaustion and unreachable setup paths. The native owner is fixing native findings. Root fixed catalog retention and missing-goal-binding refusal. Pass 2 is reviewing goal/identity boundaries and the completed UI.

| Tasks | Shared interface | Resolution |
| --- | --- | --- |
| Goals / settings | record_decision versus session_settings | Separate durable board and session transactions; root owns central schemas/catalog/dispatch. |
| Settings / activation | adapter tool surface and native registration | Settings owner edits CoordinationTools; activation owner edits Setup; root owns Program/native factory. |
| Goals / reliability | verified current native owner | Goals use authoritative native registry; no label or recency inference. |
| All / verification | Runtime evidence | Source inspection only; all runtime acceptance remains pending. |

- [x] Goals implemented and source-reviewed, including recipient projection and deferred order failure handling.
- [x] Session settings and tool refresh implemented and source-reviewed, with credential ceiling and exact acting proof.
- [x] Native/file-claim source command, operator-attested receipt, owned planning and transaction/recovery implemented and source-reviewed.
- [x] Reliability, UI and central contracts integrated. The inspector key source fix is reviewed; stale Razor generation remains unverified.
- [x] Independent source review findings resolved for their reported triggers; no runtime acceptance claimed.
- [x] Documentation reconciled and future verification handoff recorded in 2026-11-06-verification-handoff.md.

Final source review confirmed the inspector's type/session tuple key and corrected deferred-order diagnostic target. Roslyn still displays the old generated inspector expression and its three parser diagnostics; no build or semantic recompilation was invoked to replace it. All functional review findings are resolved in source. Tests, builds, packages, runtime probes, deployment, operator confirmation, live settings, hooks, board records and running processes remain untouched. The changes are being checkpointed locally on the feature branch; pushing is deferred so this work does not trigger automated verification before Paul's go-ahead.

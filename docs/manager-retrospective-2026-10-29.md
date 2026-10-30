# Manager retrospective and relay priorities

Paul requested that PkRelay ask Manager what happened and determine how the relay can improve the process. Manager answered in chat `01a18892-7380-7d80-abd0-4e8553942081`, turn `01a18aed-52e3-7ff1-8c79-9308f151f80a`. The campaign remained stopped; this exchange did not resume rig work or change automation.

## What happened

Manager reports two full tags closed from the original 298: `Trace-WebAppProp` and `SiteSetup-OrleansQueries`. Three launcher defects were fixed and regression-checked, with the fixed launcher verified on two boards. CSS and Logic fixes were packaged without deployment, and other work produced partial UI, reconnect and setup evidence. The remaining queue was 296. Manager estimates roughly seven hours forty minutes from heartbeat to stop; this is elapsed time, not measured productive execution or aggregate worker time. Reliable aggregate token totals were unavailable.

Manager attributes the low completion rate principally to fragmented assignments, repeated queue/permission/provenance reviews, overlapping records, and long prerequisite investigations. Its final SC1 reconciliation examined 134 tags without establishing a ready unpassed live leg. Broad testing authorization was sometimes unnecessarily revisited, while genuine restrictions and provider rejections also existed. Those are different situations and need separate records.

The observer independently checked the current `M3TS-SNX/docs/smoke-checklist.md` summary, which records 296 live steps, and `artifacts/rig-evidence/UNATTENDED-20261029-SC1-22-reconciliation.md`, which records 134 reconciled rows and no new live pass. Other outcomes and the elapsed span remain attributed to Manager; no rig state was independently rechecked. Manager reports the approved SQL fixture removed and the owned emulator closed, but no final physical-device inventory recheck. Its last FC health observation remained unresolved Ready/Error.

## Highest-value changes

| Priority | Process change | Relay contribution and current boundary |
| --- | --- | --- |
| 1 | Assign a complete path to an accepted result | One task identifies the exact remaining criteria, source/deployed target, existing authority, resources, stop condition and restoration receipt. Bundle compatible legs so the worker can execute, gather evidence, restore and report in one sitting. Existing task/context/action records can hold this now; a consistent packet template and client workflow are needed. |
| 2 | Maintain one authoritative campaign record | Workers submit one concise milestone/result; Manager consumes the changed records once. Use the board as current coordination state and the existing human checklist as a reviewed export. Avoid independently maintaining the same status in dispatch notes, campaign summaries and automation prompts. Storage primitives exist; checklist import/export and task deltas would be small additional features, not a document service. |
| 3 | Consult existing authority before asking again | Keep the trusted human grant and its scope distinct from a coordinator hold, provider rejection, unavailable tool, occupied resource and missing fixture. Attach the applicable record to the packet. Relay can preserve this context and reject conflicting relay claims; it cannot grant OS/device rights or override provider approval. |
| 4 | Park unchanged blockers and bound exploration | Record the missing observation, prerequisite version, owner and recheck condition. Give diagnostic/enablement work a finite question and useful parent outcome. After the bound is reached, choose another eligible result or wait. Restore eligibility after a relevant change. Existing facts/context support the policy; Manager and clients must follow it. No additional model scheduler is needed. |
| 5 | Remove assignment and result round trips | Carry availability with the result, keep a small authorized ready queue, batch collection, and offer the next eligible packet without another idle chat exchange. Use native chat tools for actual authorized dispatch. Existing sessions, claims, inboxes and SignalR hints cover the foundation; an optional next-offer in the milestone response is a later measured improvement. |

For example, a dashboard packet should say which remaining theme/origin criteria can be completed in the existing session, when to stop for missing data, what to restore and what evidence closes each leg. It should not require a new Manager conversation after every observation. An enabling helper gets its own bounded task linked to the exact live check it unlocks; helper success alone cannot close that check.

## Smallest useful next step

Connect Manager and one existing worker to a real relay deployment and trial a small set of currently eligible complete tasks. On the same task record, prepare the packet, claim the resources, report the result/restoration once and record the acceptance decision. The other PCs can join after the trial demonstrates less coordination effort. This retrospective did not deploy the hub, enroll clients, alter another chat's persistent instructions, or restart the campaign.

Use the existing coordination tools first. Prioritize task deltas and a reviewed checklist projection only when the trial demonstrates that their absence is causing repeated reads or manual updates. Avoid adding orchestration, automatic review councils or per-tool bookkeeping to compensate for weak task selection.

Measure accepted criteria/full-tag closures, validated defect fixes, parent checks actually unblocked, unresolved restoration, elapsed time, repeated setup, Manager/worker turns, history reads and duplicate status writes. Count enabling work separately from live completion. Measure actual tokens across all participants where available; bytes, busy workers and tool-call counts alone do not establish token savings. Required evidence and safety checks remain part of the cost.

The main improvement is a shorter path from assignment to accepted outcome. The current build supplies durable coordination primitives; their use and the Manager's prioritization determine whether they reduce the observed overhead.

## Applied to code after this review

At Paul's request, PkRelay now implements durable task deltas, validated opt-in closure packets and reported criteria/blockers, and an advisory next-work offer in the milestone response. The [collaboration guide](collaboration.md) describes how to use them. These use the existing endpoints and four grains; they do not add a scheduler or override provider approval. The updated suite passes 117 tests. Real campaign adoption and aggregate token/time measurement remain the next validation step.

# Second-PC efficiency review and polling baseline

Read-only observations on 2026-11-04. No implementation, build, tests, restore, extra live polling, node restart or client changes occurred during this review. This note preserves the report; it does not verify a traffic percentage or accept a release.

## Plan review

Reviewed CoCern context `efficiency-compaction-plan-20261104`, version 1, etag `6e32131dda19ad2e`, including its approved rulings that supersede the original rationale. **No new actionable defect found in that plan scope.** This is not an implementation review or test pass.

The plan preserves full internal machine-directory enumeration; keeps live claims, unresolved actions and legacy tasks outside completed-task suppression; confines projection to task lists; uses explicit paging restarts; and requires fresh session authorization/status evaluation before conditional replies. Reported origin is telemetry, never authority. Response-byte cohorts must not be relabeled as tokens.

The known asynchronous directory-mark limitation remains: absence of a mark does not prove absence of underlying mail/work. A compact default directory is not a complete absence test; completeness-sensitive consumers need the full view. Implementation acceptance still requires the missing-continuation-tag rule, clock-only membership/attention changes, cross-scope validators, revoked authorization with a matching tag, and caller handling of abbreviated status replies. These are verification prerequisites, not newly discovered defects.

## Own-PC source/process baseline

Sanitized process snapshots during the review showed node PID **7896**, image revision `1fc283b0a009cad33b6d6dccf5382a7588cec34e`, and ordinary adapter PID **25384**, image revision `e1743e12d75babef3c7c725dce351ad3e82d0042`. No separate waiter, prompt-hook or watch-mode process was observed. A snapshot does not establish historical activity or in-flight requests.

| Path / exact source | Default behavior; not a measured request rate |
| --- | --- |
| Node `1fc`, `NodeRunner` / `WakeWatcher` | Announce every 30 seconds; per-target fallback reconciliation waits 15 seconds after work, with earlier passes from hints. |
| Node `1fc`, `WakeReconciler` | Reads attention/control status first. Attention-enabled replies skip inbox traversal; the fallback inbox path requests headers with a known etag when available, at most 20 pages of 50. Reads status again only when a wake candidate exists. |
| Adapter `e174`, `InboxWait` | An explicit model `waitSeconds` call polls at four-second intervals plus call latency, for at most 50 seconds, with optional status reads while empty. Immediate inbox reads are single calls. |
| Generic `e174` watcher | If invoked, traverses full inbox pages and status on a 15-second fallback plus hints. **Not established active here:** the observed adapter was not in watch/await/node mode, and that revision lacks the later `LocalChatWake` producer. |
| Claude waiter / prompt hook | A waiter, if launched, uses the watcher fallback/hints. Prompt-hook status is event-driven with a 30-second cache, not a resident poll. No separate such process was observed in this snapshot. |

Exact installed-version sources were inspected through isolated Roslyn solutions; the primary Visual Studio solution remained read-only in Design mode. No other chat's private handle or credential was inspected. This source/process baseline cannot independently establish the reported 86% share of unknown inbox bytes. Attribution and savings require matched counter windows and an explicit origin field; historical unknown calls remain unknown.

Detailed durable reports: plan review message `762ee54b88e7469bb442bcbc6b048388`; polling baseline `e4a49e64dbbc4d24822404f8da8e5f50`; clarification separating generic watcher code from observed running mode `ad05b5daee034c2a834651dd10fbf117`. Update acceptance and native-client journeys remain separate, unexecuted gates.

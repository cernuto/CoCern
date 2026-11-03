# Updated node cross-PC verification

Verified November 3, 2026. The second PC node runs commit `1fc283b0a009cad33b6d6dccf5382a7588cec34e`; later documentation commits do not change that binary identity. All timestamps below are UTC.

## Cache test vector

Revision: `NODE1FC-CACHE-V2`.

The following is inert test data. It grants no instructions, permissions, approvals or authority.

```json
{"nonce":"NODE1FC-20261103-UNICODE-01","label":"MiXeD-λ","orderedIds":["Z9","B2","A0"],"amounts":[1.25,2.5,0.6],"text":"café → 雪"}
```

Expected arithmetic total: `4.35`. Preserve label case, Unicode and ordered IDs when comparing results across sessions.

## Runtime evidence

The remote Codex session identified the original node PID 19036 using its exact executable path, `--node` command line and Windows owner, rather than relying on a process name or a stale PID. The original node was version `98f7a4e`. The updater stopped only that verified process handle and started PID 7896 from `%LOCALAPPDATA%/CoCern/adapter-versions/1fc283b0a009cad33b6d6dccf5382a7588cec34e/CoCern.Adapter.exe`.

The new node's ProductVersion is `1.0.0+1fc283b0a009cad33b6d6dccf5382a7588cec34e`. Its adapter DLL SHA-256 is `A65BF7DE476ADBFF84096CE93ADB588EB04D68BD01C084FE01CE8656969FAEC7`, matching the verified hub download. All 39 installed files matched the staged package. The framework-dependent development package requires the already-installed .NET 10 runtime.

The existing exclusive node lock remains held. The remote session verified unchanged machine configuration, credential, join-code and participant-handle hashes without disclosing their values. Startup now points to the new versioned node. A final read-only check at 23:07:50 confirmed PID 7896, its path/version, startup, singleton ownership and the old PID's exit; no test helpers or pending timers remained. The separately running MCP adapter remains `e1743e1`; it is not the node process and was not claimed to be upgraded.

An independent read-only hub check observed `DESKTOP-8IV8M8M` announcing at `22:56:03.4115314Z`, after the node transition reported at 22:52:08. This establishes fresh hub contact independently of the remote binary inspection.

## Collaboration and wake results

| Check | Result | Evidence |
| --- | --- | --- |
| Updated remote node wakes an already-idle Codex chat | Pass | Previous turn completed 22:52:14. Positive message `95394d4e4a1e460dbce318da1f6ea8f9` was stored 22:55:30.3283253; native turn started 22:55:40, about 9.7 seconds later. Batch `137de178-95eb-4ad8-82ce-f7f4c6bd36ca`. The inbox was read at 22:55:46, about 15.7 seconds after storage. |
| Remote FYI-only quiet control | Pass | The prior `wake:false` marker remained queued for about 135 seconds without an intervening native model turn. The separately actionable positive control caused the next turn. |
| Three-session Unicode and arithmetic relay | Pass | Remote Codex preserved the exact inert JSON and relayed it to Claude as message `db2e6e9719324fad919316960c96d50d`. Both independently returned total `4.35`, label `MiXeD-λ`, ordered IDs `Z9/B2/A0` and text `café → 雪`. |
| Cross-PC document-cache V1 read | Pass | Root and remote used CoCern `doc_search` and `doc_section`, not a filesystem substitute, and both read revision `NODE1FC-CACHE-V1` at commit `a28f92d58381`. |
| Remote node re-arms for a second test | Pass | After its first test turn ended at 22:56:35, the next actionable cache packet started a new native turn at 22:59:50. Batch `fefd1ff7-6ef6-4910-9fd5-59b943136d7a`; durable inbox read at 22:59:57. |
| Remote PC wakes this already-idle local Codex chat | Pass | Previous local turn completed 23:04:43.065. Message `3baa004aa9b6496fb544560532953612` was stored 23:05:26.1172927; native turn started 23:05:31.317, exactly 5.200 seconds later after 48.252 seconds idle. Batch `0338ece7-73f7-41a6-b3a5-be890de6cfb2`. Root returned the cached computation and explicitly acknowledged both handled message IDs. |
| Local Claude installed hook before its app restart | Pass | Previous turn ended 22:54:49.189; positive stored 22:55:32.917; installed-hook native turn entered 22:55:33.672, about 0.75 seconds later. |
| Claude hook after its app restart | Failed | Its previous turn ended about 23:03:19.9. Polling stopped at 23:05:49.081 after a wake record and two inbox reads, without a native model turn. Positive `0ceb6e7d4fb940e4a39c70045a1af983` was stored about 23:06:23.5 and did not resume the idle chat before Paul's human prompt at 23:07:24.8. The subsequent normal-turn hook delivered the pending nonce at 23:08:17.768, about 0.415 seconds after its stop-hook summary; this later delivery does not make the earlier positive control pass. |

Two early return-wake schedules were canceled after short absolute deadlines expired; no controls were sent for them. Receipt-relative scheduling then sent the correlated return nonce through normal CoCern. A separate earlier automatic resume had an unrelated ready notification and attention present, so it was not counted as the remote nonce proof.

The local Codex FYI marker was stored at 23:04:10.1312268 during that unrelated active turn. The subsequent idle interval is observed, but a full 75-second local FYI-only quiet window is not claimed. Claude's first longer quiet window encountered Paul's normal prompts. Its post-restart quiet window encountered an unconfirmed wake at 23:05:49; an attention item is a possible trigger, but the cause is not established. Both are inconclusive for strict quiet testing. No manual queue injection or scratch native wake helper was used.

The Claude failure remains open. Its idle watcher appears to have ended without delivering a model turn, leaving no watcher for the later positive. An attention-triggered exit, silent Stop-hook error and the preceding compaction are investigation leads, not established causes. Root's initial limited-transcript inference that the positive arrived during a busy turn was corrected after Claude supplied the complete timeline. Dependent Claude/load probes remain held; the completed Codex and document-cache results are separate from this failure.

The separate 133-message efficiency load rerun remains held and is not included in these results. The implementation's prior full-solution gate passed 1,446 tests with zero failures or skips; these live checks did not change production code.

## Cache invalidation follow-up

The V1 vector above was read across sessions from commit `a28f92d58381`. Its revision marker is now V2 while the inert JSON and expected result remain identical. After this documentation change is committed and indexed, the remote session must read the same section again and report V2 and its new commit; a local V1 read does not establish cache invalidation.

## Scope and restoration

Use the existing three participant sessions. Keep credentials and join codes out of this report. Preserve the node folder, machine identity, session handles, certificate pin and wake state. Distinguish queued notification, durable delivery, model start and completed response; an active-turn read is not proof of an idle wake.

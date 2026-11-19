# CoCern event to Codex inbox read

On November 19, 2026, 14 actionable messages each reached the intended idle Codex chat and were returned by its first model-initiated inbox read. Adding `--resume` to `codex queue` did not produce a reliable improvement on this PC. The adapter retains its existing queue command.

| Queue destination | Command | Trials | Median store to wake prompt | Median store to turn start | Median store to inbox result | Individual inbox times, seconds |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Default | Queue | 4 | 0.094 s | 4.987 s | 8.769 s | 5.80, 4.65, 11.74, 11.86 |
| Default | Queue with resume | 4 | 0.155 s | 7.596 s | 11.857 s | 11.02, 15.87, 11.92, 11.80 |
| Explicit fixture app-server | Queue | 3 | 0.158 s | 0.240 s | 3.103 s | 3.10, 2.61, 3.77 |
| Explicit fixture app-server | Queue with resume | 3 | 0.181 s | 0.248 s | 3.088 s | 4.79, 2.76, 3.09 |

## Method

Each trial started a fresh, read-only thread on an isolated Codex app-server using the installed Codex CLI 0.169.0-alpha.11.1. The client remained connected after the initial turn completed. A disposable CoCern hub stored one direct message with a unique marker for the thread's dedicated fixture session. The updated adapter's `--await-inbox` watcher produced the standard wake prompt. The harness handed that prompt to the installed queue command using the thread's exact ID. This exercises the actual watcher and CLI with an already-idle thread. Production's additional checks for a chat that is busy or already has a queued wake were outside the comparison.

Codex itself chose when to invoke its fixture inbox tool. The tool issued an authenticated CoCern `check_inbox` for that one session and recorded completion only after finding the exact marker in the response. Durations start at the stored message timestamp, use the same workstation clock, and include the read round trip. Turn starts came from the app-server's turn notifications. The first available wake prompt and the CLI receipt were recorded separately. A CLI process can exit after a turn has already started.

The eight default-destination trials used the order plain, resume, resume, plain, resume, plain, plain, resume. Each default resume invocation emitted a resume or connection warning, and the default daemon control socket was unavailable when probed. The six explicit-destination trials used one authenticated loopback app-server for both commands and the order resume, plain, plain, resume, plain, resume. None emitted a warning. Endpoint comparisons include differences in local routing and model scheduling. The small samples characterize this installation, with no claim about other Codex hosts.

An independent standard MCP stdio check held `check_inbox(waitSeconds=10)` open, then sent a message 1.25 seconds later. The waiting client received the correct durable message 311 milliseconds after send initiation. That measures an already-waiting tool call. The table includes the additional time to schedule a new Codex turn.

## Live hub after the wake-route repair

Renaming a native Codex chat changed its saved handle. The local watcher kept the previous handle and failed its ownership check before queuing another wake. Independently, the label update could overwrite the hub's record of the watcher's presence. The replacement watcher now takes the updated handle, and the hub records route freshness separately from the chat's label and availability.

The repaired adapter delivered the previously stranded message without a manual prompt. After the receiver returned to idle, the local hub stored a fresh direct message at 08:58:20.310 Pacific. The send receipt reported a reachable receiver. Its Codex transcript recorded turn start 10.14 seconds later, the inbox request at 13.66 seconds, the tool result containing the unique marker at 15.93 seconds, and the receiver's final confirmation at 26.37 seconds. The receiver had **Always allow** enabled. This was one end-to-end production-path observation.

## Live comparison with immediate desktop wake

On the earlier Windows desktop build, four ordinary queue deliveries took 0.42–0.64 seconds from hub storage to the Codex queue. All four turns started within 19 milliseconds of the same point in a ten-second cycle. Two separate pilots used the running desktop's package-targeted Windows wake link after the old CLI had queued the message. The turns began 0.33 and 0.27 seconds after the launcher request. This isolated the desktop's queue pickup as the source of the long pre-turn wait.

The table compares four ordinary queue events and four events using the deployed adapter on Windows Codex x64 `26.1111.9664.0`. All eight went to the same idle throwaway chat, with **Always allow** enabled and the same model settings. Each duration starts when the loopback hub stored that event. Wake is the recorded turn start. Inbox read is the tool response containing the new unique marker, correlated with that turn's own inbox request. The receiver also returned each marker in its final reply. The trial numbers give the order within each group.

| Trial | Ordinary wake | Immediate wake | Ordinary inbox read | Immediate inbox read |
| --- | ---: | ---: | ---: | ---: |
| 1 | 6.13 s | 1.85 s | 15.14 s | 9.21 s |
| 2 | 4.71 s | 1.95 s | 10.83 s | 9.99 s |
| 3 | 6.84 s | 2.08 s | 12.81 s | 8.50 s |
| 4 | 1.71 s | 2.00 s | 11.25 s | 10.92 s |
| **Median** | **5.42 s** | **1.97 s** | **12.03 s** | **9.60 s** |

Median storage-to-queue time was 0.56 seconds before and 0.54 seconds after. Queue insertion to turn start fell from 4.86 to 1.41 seconds. With immediate wake, the median turn-start-to-inbox-request interval was 5.31 seconds, followed by 2.39 seconds from request to the marker-bearing tool result. These intervals come from individual events and their medians are calculated independently. Ordinary wake can still be quickest when a message happens to arrive immediately before a poll.

The final adapter asks the running desktop to consume its confirmed queue item through `Windows.System.Launcher.LaunchUriAsync`, addressed to the Codex package family. It uses Windows PowerShell for the local Windows Runtime call. Two earlier attempts using a different Windows activation API coincided with package re-registration and desktop restarts. Windows also changed the installed desktop version during the first attempt. Both interrupted trials were excluded. Their messages were recovered, and the second desktop build was baselined before enabling the final launcher. The desktop and app-server process IDs stayed the same throughout all four final trials. The local adapter release was `af9ccc430b95`.

## Time inside the foreground inbox read

We compared four new baseline sends using adapter `af9ccc430b95`, four using a shorter wake prompt and overlapped signing preparation, and four using final adapter `866d62218284`. All twelve sends went to the same verified idle desktop chat on build `26.1111.9664.0` with **Always allow**. Each chat turn returned its new marker from a foreground inbox read and confirmed it in its reply. The measurements use the same workstation clock and the same definitions as the live comparison above. Four additional diagnostic sends and a dashboard verification send were excluded from the timing groups.

| Median interval | Baseline | Prompt and signing changes | Final |
| --- | ---: | ---: | ---: |
| Stored event to turn start | 1.660 s | 2.245 s | 2.045 s |
| Turn start to inbox request | 4.316 s | 4.470 s | 3.605 s |
| Inbox request to marker-bearing result | 2.394 s | 2.385 s | 2.445 s |
| Stored event to marker-bearing result | 8.729 s | 9.394 s | 8.083 s |

| Final trial | Wake to inbox request | Request to result | Event to result |
| --- | ---: | ---: | ---: |
| 1 | 4.062 s | 2.352 s | 8.600 s |
| 2 | 3.147 s | 2.106 s | 6.904 s |
| 3 | 3.104 s | 2.537 s | 7.567 s |
| 4 | 4.368 s | 4.109 s | 10.641 s |

In the fresh baseline, the first assistant text appeared a median 3.609 seconds after turn start, followed by 0.673 seconds to emit the first tool call. The corresponding final medians were 3.091 and 0.638 seconds. No preliminary tool lookup appeared in the reference turns. The shorter prompt puts the exact nonwaiting call and its arguments first. Model response time varied enough that four events cannot establish a repeatable gain from the prompt alone.

Temporary timing on two genuine reads found that the trusted hook started its measured handler roughly 0.42 seconds after tool emission and produced signed output about 0.51 seconds later. The interval between hook output and the first measured MCP backend instruction was 1.144 and 1.210 seconds. Local measurements cover the desktop and the development launcher's stdio relay together and cannot attribute their shares. Backend registration, binding and session checks took 0.219–0.225 seconds, followed by 0.021–0.033 seconds to the marker-bearing result. In the slowest final trial the first session-status read reached the hub at 4.086 seconds, within 23 milliseconds of the received result.

The adapter now prepares the established binding during the existing registration wait, rechecks registration and checkout before signing, and defers Git origin and common-directory lookups until they are needed. The final adapter also uses one foreground request for a successful zero-wait read. Hub telemetry showed one background poll plus one foreground read in each baseline trial and exactly one foreground read in every final trial. Each final hub inbox execution took 3.12–3.16 milliseconds. The request-to-result median was effectively flat despite the reduction in local work.

### Live dashboard verification

The Sessions map now receives successful direct sends over its authenticated SignalR connection and animates a stroke from the actual sender to the recipient. Reloading the updated dashboard in Codex's built-in browser exposed a cached graph stylesheet, which the deployment corrected. After a normal reload, the idle map showed zero active pulses. A real send produced a moving stroke 65 milliseconds after hub storage. Its dash offset changed from 10.83 to −1.79 over 137 milliseconds. On a later send along the same curve, the offset changed from −20.42 to −32.87 over 132 milliseconds. Both rendered paths carried the expected sender and recipient IDs. Reduced-motion styling highlights the line without travel.

## Splitting hook output, desktop dispatch and launcher forwarding

The previous two reads measured 1.144 and 1.210 seconds from hook output to the MCP backend. Those reads lacked a launcher timestamp. For the new trials, the launcher already forwarded the start time of its own request handler. We matched that time with the same signed inbox invocation at hook output and backend entry, and used the Windows exit time of the hook shell to separate hook teardown from desktop dispatch. Windows process I/O counters independently identified the incoming request burst at launcher entry. The desktop measurement includes client dispatch and the local pipe handoff. These observations cannot separate individual steps within Codex.

Seven fresh baseline events went to the same idle throwaway chat. All times use the workstation clock. The desktop, launcher and hub remained running, and each turn read its unique marker from the inbox before confirming it in its reply. Four baseline events measured the hook-output split. Three additional baseline events also timed the work between signing and hook output. They used the same product code.

| Median interval | Baseline | Optimized, four instrumented reads |
| --- | ---: | ---: |
| Signed arguments ready to hook output | 52.34 ms (three reads) | 16.04 ms |
| Hook output to Windows hook-shell exit | 41.10 ms | 41.70 ms |
| Shell exit to launcher request handler | 1,434.78 ms | 1,565.27 ms |
| Launcher handler to backend entry | 1.26 ms | 1.11 ms |
| Hook output to backend entry | 1,477.02 ms | 1,608.73 ms |

The interval after shell exit and before launcher entry accounted for 96.9–99.1% of the hook-output delay across the seven baseline reads. The launcher reached the backend in 0.86–3.32 ms. On all four optimized reads, independent Windows I/O samples placed the inbox request's arrival within 1.6 ms of launcher entry. The largest baseline delays, including two above four seconds, occurred before the launcher received the request.

The hook now writes its startup receipt using the authenticated binding it already holds for signing. This removes a second lookup and lease acquisition while preserving the registration, checkout, session and generation checks. The median saving before hook output was 36.30 ms, or 69% of that stage. Four optimized instrumented trials returned the inbox result in 9.046, 8.707, 9.094 and 8.028 seconds from event storage. The local adapter was then published without the diagnostic probes as `20e1a6f0df18`. The desktop hook and Codex configuration files matched their original hashes.

Four more events verified the clean release with **Always allow** enabled. Wake is the recorded start of the receiving turn. The result is the first foreground inbox tool response containing that event's marker. All four turns also echoed the marker in their final reply.

| Trial | Event to wake | Wake to inbox request | Request to result | Event to result |
| --- | ---: | ---: | ---: | ---: |
| Baseline median, seven reads | 2.063 s | 3.660 s | 2.465 s | 9.040 s |
| Clean release 1 | 2.045 s | 4.937 s | 3.072 s | 10.054 s |
| Clean release 2 | 2.173 s | 3.788 s | 2.492 s | 8.453 s |
| Clean release 3 | 2.103 s | 3.901 s | 2.801 s | 8.805 s |
| Clean release 4 | 2.116 s | 3.582 s | 3.181 s | 8.879 s |
| Clean release median | 2.110 s | 3.845 s | 2.937 s | 8.842 s |

Medians are computed separately for each column. Total time improved by 0.20 seconds at the median while the request-to-result median increased by 0.47 seconds. The end-to-end samples overlap substantially. The repeatable local gain is the 36 ms removed between signing and hook output.

## Idle inbox reads after the eight review changes

Four fresh events measured adapter `20e1a6f0df18` before the changes. Four more measured adapter `06d88288902d` after the hub restarted with the updated code. The desktop and its launcher stayed running. Each send used the same prompt apart from its unique marker, the same ordering of idle delays, the same receiving chat and the same model and approval settings. All eight turns read and confirmed their marker. Each recorded request interval contained exactly one successful foreground hub inbox call.

| Trial | Event to wake | Wake to inbox request | Request to inbox result | Event to inbox result |
| --- | ---: | ---: | ---: | ---: |
| Before 1 | 2.364 s | 7.313 s | 2.785 s | 12.462 s |
| Before 2 | 1.467 s | 3.464 s | 2.697 s | 7.628 s |
| Before 3 | 2.235 s | 3.542 s | 2.356 s | 8.133 s |
| Before 4 | 1.875 s | 3.420 s | 2.549 s | 7.844 s |
| **Before median** | **2.055 s** | **3.503 s** | **2.623 s** | **7.988 s** |
| After 1 | 1.905 s | 3.721 s | 3.765 s | 9.391 s |
| After 2 | 2.018 s | 4.899 s | 2.507 s | 9.424 s |
| After 3 | 2.143 s | 3.547 s | 2.386 s | 8.076 s |
| After 4 | 2.108 s | 3.241 s | 2.222 s | 7.571 s |
| **After median** | **2.063 s** | **3.634 s** | **2.447 s** | **8.733 s** |

Median request-to-result time fell by 0.176 seconds. Median time from event storage to inbox result rose by 0.745 seconds. The hub's measured foreground call took a median 2.894 ms before and 4.414 ms after, including 14.492 ms on its first measured call after restart. The successful immediate read now skips construction of the background JSON payloads. These small samples cannot isolate that allocation saving from desktop and model timing variation.

## Why the end-to-end median rose

With four events, the median averages the two middle observations after sorting by the quantity being measured. Before the changes, trials 4 and 3 determined the total median (7.844 and 8.133 seconds). After the changes, trials 3 and 1 determined it (8.076 and 9.391 seconds). The change in the average of those same trials can be divided exactly by stage.

| Stage in the middle two end-to-end trials | Contribution to the change |
| --- | ---: |
| Stored event to local reservation timestamp | +5.7 ms |
| Reservation timestamp to Codex queue insertion | +4.5 ms |
| Queue insertion to turn start | −41.0 ms |
| Turn start to foreground inbox request | +153 ms |
| Inbox request to marker-bearing result | +623 ms |
| **Total** | **+745 ms** |

The independently sorted inbox-request median used trials 4 and 2 before, then trials 3 and 2 after. It excluded after-trial 1's 3.765-second read, which was part of the end-to-end median. Of the 623 ms increase for the middle total trials, 611 ms elapsed before the hub's recorded foreground call start. These historical traces lack the boundaries needed to assign that 611 ms to individual desktop, hook and launcher operations.

## Removing a wait for a descendant process

Eight fresh baseline events on adapter `06d88288902d` reproduced the previous 8.733-second median within 31 ms. Process-lifetime observations showed that CoCern launched the desktop activation helper about one second after the Codex queue CLI exited. Two instrumented reads isolated the wait. CoCern observed CLI exit after 9 ms, but received end-of-file on its redirected streams 1,030 and 1,088 ms after exit.

In another trial the CLI launched a Git child. The Git process outlived the CLI by 1,149 ms, and both output streams closed within 0.4 ms of Git's exit. Using asynchronous pipe handles alone reproduced the delay. Running the queue command from a different working directory also reproduced it. Both diagnostic approaches retained the same complete receipt checks.

The deployed Windows path drains bounded stdout and stderr while the queue command runs. At CLI exit it reads the bytes still available and checks the exit code and receipt for the intended thread. A pipe still open in a descendant requires a complete receipt line. A natural end-of-file retains the previous behavior. In two instrumented trials CoCern validated the receipt 10.9 and 9.8 ms after actual CLI exit while Git continued independently. The adapter also overlaps initialization of its package-targeted Windows activation helper with queue insertion and reuses it for up to two minutes of inactivity.

Before reserving a wake, CoCern now excludes unrelated chats before reading their private session handles. Competing bindings for the target chat across repositories and checkouts are still resolved, and the selected handle is revalidated under the existing ownership locks. The timing from the end of the hub's final policy check to the reservation timestamp includes this ownership check and local transport. The reservation timestamp is captured before the local busy check and ledger commit.


### Eight fresh idle events per release

The baseline was `06d88288902d` and the final local adapter was `caf5fd286a2d`. All sixteen events used the same idle throwaway chat, prompt, staggered idle delays, model settings and **Always allow** setting. Both cohorts used the same running hub, desktop, app server and launcher. Each turn returned its own marker in the foreground inbox result and confirmed it in its reply. Twelve diagnostic pilot events were excluded from these cohorts. The clean release contained no timing probes. All eight selected regression cases passed.

| Median interval | Baseline | Final |
| --- | ---: | ---: |
| Storage to reservation timestamp | 0.420 s | 0.172 s |
| Reservation timestamp to queue insertion | 0.191 s | 0.214 s |
| Queue insertion to turn start | 1.456 s | 0.160 s |
| Storage to turn start | 2.015 s | 0.598 s |
| Turn start to first recorded commentary | 3.373 s | 3.162 s |
| First commentary to foreground inbox request | 0.586 s | 0.612 s |
| Turn start to foreground inbox request | 3.997 s | 3.777 s |
| Inbox request to marker-bearing result | 2.519 s | 2.416 s |
| **Storage to marker-bearing result** | **8.702 s** | **7.063 s** |

| Trial | Baseline wake | Final wake | Baseline inbox request to result | Final inbox request to result | Baseline event to result | Final event to result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 2.195 s | 0.616 s | 2.454 s | 3.049 s | 10.646 s | 7.585 s |
| 2 | 1.864 s | 0.633 s | 4.898 s | 2.486 s | 11.067 s | 7.135 s |
| 3 | 2.071 s | 0.591 s | 2.199 s | 2.207 s | 8.339 s | 6.432 s |
| 4 | 2.091 s | 0.605 s | 2.584 s | 6.730 s | 9.065 s | 10.218 s |
| 5 | 1.917 s | 0.414 s | 2.911 s | 2.346 s | 8.097 s | 6.992 s |
| 6 | 1.000 s | 0.652 s | 2.414 s | 2.236 s | 6.667 s | 6.302 s |
| 7 | 1.960 s | 0.442 s | 2.240 s | 5.002 s | 8.124 s | 9.672 s |
| 8 | 2.198 s | 0.549 s | 3.675 s | 2.252 s | 9.359 s | 5.963 s |

The median time from the final hub policy check completing to the reservation timestamp fell from 239.5 ms to 16.8 ms. On the clean adapter, a separate 10 ms sampler saw the durable queue acknowledgement a median 16.8 ms after the CLI exited. One activation helper served all eight final events. Every final wake took 0.414–0.652 seconds. The fastest baseline wake took 1.000 second.

The complete event-to-inbox median fell by 1.639 seconds (18.8%). Comparing the two events that set the median total in each cohort attributes 1.558 seconds of this improvement to the time before turn start. Foreground inbox delivery still varied. On the slowest final read, 6.715 seconds of its 6.730 seconds elapsed before the hub recorded the request, and the hub handled it in 2.63 ms.

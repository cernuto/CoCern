# Scoped source reviews, 4 November 2026

These reviews are scoped to the exact commits named below. They are not release or native-provider acceptance. Both Roslyn CodeLens and Visual Studio connections were checked; the primary Visual Studio host remained running. Source-only reviews did not run their branches. Cold dependency references prevented compilation-health conclusions.

## Task 1 presentation

Commit 25c3aca5632071f25f6d83022f9d36562a333a85 was approved within its presentation scope. The complete 14-production-file and four-test/helper diff was inspected; 24 frozen source hashes and the actual 149-passed TRX were checked. Markers do not change effective standing, grants, answers or revisions. The query uses an authoritative board and exact-effect reservation, checks fresh pending work on a repeated request, and verifies complete escaped output fits before claiming it. Urgent preview and ordinary-tip handling, final seen reporting, legacy suppression and decision cleanup were covered. Native cross-PC origin, fallback journeys and other task acceptance remain separate.

## One-time inbox delivery

Commit 5265f1efd4cd319a2ea4801e7b81a552c9b80fb4 requires a correction: ClaudeWaiter.ReadInboxAsync captures session A and its receipt route, but CallAsSessionAsync reselects a handle for each page and report. An A-to-B switch mid-read can apply a cursor to the wrong inbox, mix repository headers, and record B output under A, allowing repeated output later on B. Pin one handle for the whole sequence or discard and restart on identity drift. The existing plain-waiter session-refresh test does not exercise this paginated printing path. This P2 finding was independently validated and sent to the owner; correction review is pending.

Truncated or omitted content retains an inbox pointer. The accepted output/state crash gap does not delete durable messages, and seen reporting is distinct from acknowledging or deleting inbox messages.

## Administrator tools

Commit ff2664d82db07e49ece157f268a8c614b0580720 requires a correction: AdminToolsService.ConnectAsync enqueues every setup attempt, while failed RunConnectAsync attempts remove only the dictionary entry. Queue eviction depends on the dictionary exceeding 64 entries. Repeated failures therefore accumulate ordering entries indefinitely, even with the same request ID and an otherwise empty dictionary. Bound ordering state as well as dictionary state. This P2 finding was independently validated and sent to the owner; correction review is pending. The later comment-only commit was excluded from this review. Requested transcript use of a join code was treated as authorized; no code or credential is included here.

## Native merge rehearsal and guidance

A source-only rehearsal of native commit 86e52613ded5b2e95877d57235a5424ba7184bf1 onto main 19cf69944b26d3b9acc67454d2d56f22311095d0 found one conflicted file, ClaudeWaiter, with two regions. The rehearsal was aborted cleanly. The final integration resolution was then inspected: the legacy waiter refresh path remains, the native path uses validated binding and dispatch identity, and shared wait cleanup retains exclusive state and PID handling. Native guidance and shared standing-question restrictions remain present for both instruction profiles. No native activation or end-to-end provider acceptance is implied.

The scoped run-kit review at 5869f50d approved the documented procedure for sections 9.2 and 9.3, not execution. The owner must still bind committed artifacts, bootstrap and fault mechanisms, provider permissions, and per-case evidence. Publication races, incompatible versions, crashes and offline recovery remain separate acceptance cases.

# Test suite audit and cost recommendations, 5 November 2026

Source-only audit of `tests/` at `c2b1bc0`, the recorded TRX results under `artifacts/`, and the combined-gate record of 4 November. Nothing was built or run for this audit.

## What the numbers say

| Measure | Value |
| --- | --- |
| Test declarations (`[Fact]`/`[Theory]`) | 1,856 across 138 files |
| `[InlineData]` rows | 1,245 (so the executed case count is 2,210 at the last gate) |
| Growth | 68 cases on 29 Oct, 880 on 3 Nov, 1,856 on 4 Nov: 27x in six days, doubled in the last one |
| Test code vs production code | 51,847 lines of tests vs 41,024 lines of `src/` (1.26 : 1) |
| Commits since 28 Oct touching tests | 366 of 564 |
| Full gate wall time | 9 to 10 minutes per run (serial MSBuild, xUnit capped at four threads) |
| Full runs recorded | five full-suite TRX files on 3 Nov, three gate candidates on 4 Nov, 44 TRX files in two days |
| CPU time in one full run (3 Nov, 1,436 cases) | 2,141 s of summed test durations: 36 CPU-minutes |
| Where that time goes | 107 tests over 5 s account for 58 % of it; 396 tests over 1 s account for 95 %; the other 1,040 tests cost almost nothing |
| In-process hub hosts started per run | 126 `RelayHostFixture` / `WebApplicationFactory` constructions, only one shared `IClassFixture` |
| Temp directories created per run | 96 `Path.GetTempPath()` sites; 20 files spawn real processes |
| Disk | `artifacts/` is 22 GB: 55 folders, about 20 of them a 1.1 GB isolated checkout-plus-build made for one agent or one gate |

The slowest single tests in the last full run:

| Seconds | Test | Why it is slow |
| --- | --- | --- |
| 121 | `AnalyticsSnapshotScrubTests.Domain_built_state_leaves_only_as_the_kinds_the_domain_allows…` | stuffs a lock with `RelayLimits.MaxRecords` renewals, then snapshots through SQLite and DuckDB |
| 101 | `DiscoveryDomainTests.A_full_directory_is_truncated_to_the_reply_budget…` | enrolls `MaxMachines × MaxChatsPerMachine` chats to prove a binary search is fast |
| 68, 49, 28, 19 | four `CoordinationTests` cases | fill boards and acknowledgment windows to their production limits |
| 49 | `TokenEfficiencyTests.Token_counting_adds_well_under_a_millisecond_per_call` | a latency benchmark: 100 warm-up calls plus 400 timed calls per host, twice |
| 46, 23 | two `AddPcTests` cases | real adapter install and certificate pinning through spawned processes |

Slowest classes: `CoordinationTests` 179 s, `HostTests` 146 s, `DiscoveryHostTests` 126 s, `DocCacheTests` 122 s, `AnalyticsSnapshotScrubTests` 122 s for four tests, `NodeTests` 113 s, `TokenEfficiencyTests` 103 s, `DiscoveryDomainTests` 101 s, the six `Dashboard*` classes about 330 s combined.

## What is driving the cost

1. **Per-test hosts.** 126 tests each start a complete hub (Orleans silo, SQLite, Kestrel, dashboard) and tear it down. One shared fixture per configuration would start a handful.
2. **Limit-stuffing proofs.** Tests prove behaviour at `RelayLimits` and `DiscoveryLimits` production values by generating thousands of records. The limits have no test seam, so each such test costs a minute.
3. **Benchmarks disguised as tests.** Latency medians, memory-retention checks and "takes well under a second" assertions run on every gate and are flaky on a busy machine by construction.
4. **TDD ceremony left in the tree.** The artifacts hold red/green and before/after TRX pairs (`local-wake-red`, `identity-red`/`identity-green`, `baseline`/`fixed`). Each pair is two extra suite runs that prove nothing a source review would not.
5. **Full runs as checkpoints.** Three gate candidates ran on 4 November, each a fresh 1.1 GB isolated checkout and a 10-minute run, because every merge candidate got a full gate instead of one per deploy.
6. **Parallel file families.** Six `Dashboard*Tests` files, five `NativeJoin*Tests`, three `DocCache*Tests`, and pairs for Collaboration, Discovery, AdminTools, AnalyticsBatch and ManagerSelection. They overlap and each starts its own hosts.
7. **1,245 data rows.** Most theories enumerate every value rather than the boundaries. The 4 November "reduce public event waits to seven boundary and smoke cases" commit is the model to follow everywhere.

## Recommendations to the manager

Targets: at most 600 executed cases, a full run under three minutes, a KEEP smoke set under 90 seconds, and at most one full run per deploy candidate.

1. **Freeze now.** No new test files. A new test is allowed only under AGENTS.md rule 3: a real security boundary, a replay or data-loss fence, or a regression that actually happened. Every pull request that adds a test names which of the three it is.
2. **Define the KEEP set this week** and tag it `[Trait("Suite", "Keep")]`: one host-level test per public tool and role boundary, the authentication and private-inbox boundaries, the request-replay and waiting-clear fences, the three deploy prerequisites already named in the gate record, and the handful of real regressions. Target 100 to 150 cases. The gate runs `dotnet test --filter Suite=Keep`; the rest is `Deep` and runs once per deploy candidate, never per merge.
3. **Delete, do not demote, the benchmarks.** `TokenEfficiencyTests` timing cases, `DocCacheMemoryTests`, and every `Assert.True(elapsed < …)` leave the test project. If a number matters, it belongs in a benchmark harness run by hand.
4. **Give the limits a seam.** Make `RelayLimits`, `DiscoveryLimits` and `MaxRecords` settable from tests so the limit-stuffing proofs run at a limit of 5 instead of thousands. The top six slow tests drop from about five minutes to under a second.
5. **Share hosts.** Replace per-test `RelayHostFixture` construction with one collection fixture per host configuration. Expect the host-bound classes to run five to ten times faster.
6. **Merge the families.** One file per area: Dashboard, NativeJoin, DocCache, Collaboration, Discovery, AdminTools, AnalyticsBatch, ManagerSelection. Delete overlapping cases while merging; prefer one broad test over many narrow ones.
7. **Collapse data rows** to boundaries: smallest, largest, one past each, and one representative. Aim for 1,245 rows to become under 200.
8. **Process rules for every chat:** no red/green or before/after runs; no full run as a checkpoint; workers never run the suite, only the gate owner does, once per deploy candidate; reuse one gate checkout instead of a fresh 1.1 GB copy per run; delete `artifacts/` folders older than the current candidate.
9. **Track it.** Record executed cases, full-run wall time, and full runs per day in this folder each week. If cases or wall time grow between two deploys, the growth is reverted before the deploy.

Items 4 and 5 give most of the time back; items 1, 2 and 8 stop the regrowth. Items 3, 6 and 7 are the deletion work and can go to one worker as a single chunk.

# Database efficiency implementation

Paul approved implementing the four findings from the November 10 live database review, then requested a .NET 11 and C# 15 optimization pass.

## Implemented behavior

- The wake watcher retains dirty session IDs across hint coalescing. A hint reconciles its affected routes; target-list changes request a full pass, unhealthy routes remain eligible, and the periodic full-pass deadline is independent of hint activity. The fresh pre-dispatch control read and rejected-private-handle behavior are preserved.
- Conditional detail status uses a manager summary even for administrator readers. The captured conditional-read control is passed explicitly through the router because transport controls have already been removed from aggregate arguments. Explicit manager history and action-history requests retain the full path and bypass unchanged replies.
- Successful aggregate mutations perform hourly age cleanup for replay, acknowledgment, and resolved-action maps below their capacity thresholds. Recent entries and early-eviction tombstones remain protected, undated legacy entries retain the previous capacity-handling path, and reads/cached retries do not perform housekeeping writes. Idle state is cleaned on its next successful mutation.
- Attention-budget telemetry counts the nested status offer or the actual boundary offer. Status-summary metadata no longer causes offer-budget warnings. Genuine oversized offers remain recorded; historical activity is unchanged.

## .NET 11 and C# 15 assessment

The checkout already targets `net11.0`, pins SDK `11.0.100`, and selects C# `15`. No framework or dependency upgrade is needed.

C# 15 collection expression arguments allow explicit constructor capacities and comparers. The watcher now creates its live-session hash set with the known target count and ordinal comparer; retention snapshots allocate their list with the map's known count. These avoid incremental collection growth and a LINQ snapshot iterator in the new cleanup path. The benefits are allocation-oriented; no end-to-end speedup percentage is claimed. See [Microsoft's C# 15 collection expression arguments](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-15#collection-expression-arguments).

.NET 11 Runtime Async is the most relevant additional runtime candidate for `StatusTools.StatusAsync`, aggregate dispatch, and wake reconciliation. Microsoft documents the `runtime-async=on` compiler feature and reduced state-machine/continuation overhead; the documentation inspected still describes it as preview. It is not enabled by this change. A focused allocation/throughput comparison with the existing Orleans call context, cancellation, and debugger behavior should precede adoption. Existing .NET 11 runtime/JIT improvements already apply without a syntax rewrite. See [Microsoft's .NET 11 runtime changes](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-native-async).

The new JSON streaming APIs are useful for future large exports, but the current bounded MCP status replies require a complete envelope and validator. Replacing that protocol with a stream would be a larger contract change. Union types and closed hierarchies could improve modeling, but the review found no measured performance case for migrating existing coordination contracts. See [Microsoft's .NET 11 libraries](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries) and [C# 15 features](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-15).

## Verification

One independent source review found that transport flags were removed before the initial summary check. Passing the projection explicitly through `ToolRouter` and `StatusTools` fixed it; the follow-up review reported no additional actionable correctness findings.

Roslyn and Visual Studio connections were checked. Visual Studio remains on M3TS-SNX. Roslyn reports a degraded load because its compiler is older than several .NET 11/Razor generators; changed test and core projects had no reported errors after the static-token reference correction. The actual .NET 11 isolated build is therefore the final compiler check, including the C# 15 collection syntax.

Final isolated build and selected-test results are recorded after execution. The selection covers targeted wake hints/recovery, private-handle suspension, compact administrator status/history, fresh pause/cancellation projection, replay cleanup/retry fences, and attention-budget accounting. No live database changes, deployments, or application restarts are included.

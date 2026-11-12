# Retained searchable session context implementation

Implemented on `codex/retained-session-context` from `55da931a37d4b126a617d77fe058a1a083c2f47c` (`codex/model-visible-context`). The last application-source change is `1665bdf5dc1b72e2f3f6cc87a6b92fa6645b9fd0`. The repository's current running services, installed adapters and M3TS-SNX Visual Studio session were left untouched. Deployment and the live database migration have not been performed.

## Result

`session_context_put`, `session_context_get` and `session_context_search` retain deliberately saved repository-shared findings, decisions, evidence and handoffs. A stable context ID outlives the originating session. A persistent Orleans grain owns the collection and a rebuildable Lucene RAM index; SQLite commits a body-free discovery catalog in the same transaction as grain state. Existing inbox acknowledgment and shared-note behavior remain independent.

HTTP MCP, the stdio adapter's authenticated read-only HTTP bridge, and the development proxy compose the existing workflow resource with two retained-context resource templates. Clients can read the same bounded content through tools when Resources are unavailable. Resource authorization uses the current credential's repository access, without borrowing a last-used chat identity. No new package or external service was added.

A new session joins through its own normal connection, discovers context by task or bounded recent metadata, selects a relevant collection, reads its compact manifest, searches for a specific question, and fetches only exact entry parts. It rechecks current task controls and source evidence before acting, then saves a milestone update under its own identity. Several matching collections remain explicit choices. The reference documentation includes a concrete finding/decision/handoff example.

Collections hold current versions only, with explicit forgetting and no automatic transcript capture or eviction. Exact entry references include an opaque persisted collection generation, entry version and part number; resets and collection recreation cannot substitute new text under an old reference. The manifest remains a mutable current view. Existing historical backup and replay-window semantics remain in force.

## Source review and fixes

The design received a fresh builder check with no consequential unanswered questions. A fresh whole-branch source review covered `55da931..38ba131`, including authorization, transaction/catalog consistency, replay, deletion, search failures and both resource registrations. Three P2 findings were accepted and fixed in `93f45bd`:

- Numeric revisions could restart after reset, making a previous exact URI alias newly created content. Persist a random collection generation and require it in exact resource addresses and pinned tool reads.
- Generic provider clearing could leave discovery metadata behind. Atomically clear the catalog projection on a null-payload grain-state commit.
- A part near the byte ceiling could fit its JSON projection but exceed the resource limit after Markdown framing. Validate the shared complete renderer at save time, alongside token and escaped-JSON limits.

Milestone execution exposed a transport-specific numeric representation issue: resource readers constructed `JsonValue<int>` part numbers while validation required `long`. `f684687` normalizes canonical whole-number representations across transports. A subsequent replay-fixture failure came from assigning an already parented JsonNode twice; `cfa9388` changes only that fixture to clone its patch. `d967b75` expands only the existing security method through the actual adapter HTTP bridge and rendering. `1665bdf` completes body-free cold-index rebuild count/duration telemetry through the existing optional activity sink; telemetry failures do not alter search results.

The reviewer declined to judge runtime transport/test outcomes and pre-existing degraded Roslyn SDK/source-generator diagnostics. The executor verified the runtime paths described below and left unrelated tooling/source issues unchanged. No review minors remain deferred.

## Verification evidence

Both required servers were checked: Roslyn CodeLens targets `CoCern.slnx`; Visual Studio MCP targets M3TS-SNX and was not switched. Roslyn semantic inspection and diagnostics were used throughout. Roslyn's degraded baseline includes existing generator/preview errors outside the new handwritten source; the isolated real solution builds provide the binary evidence. The final build completed with zero errors and the same 19 existing warnings observed in the preceding milestone builds.

Tests were selected through Roslyn `find_tests_for_symbol` and `analyze_change_impact`. RPC implementation lookups sometimes returned no direct test; interface callers and the tools' exact test callers established the relevant path. The mapping and changed symbols are recorded in each receipt. The repository's no-TDD, no-unrelated-tests and milestone-only verification rules took precedence over generic skill defaults. No whole-suite or whole-class tests ran.

| Exact test method | Most recent passing revision | Local receipt |
| --- | --- | --- |
| `SessionContextTests.Committed_context_survives_delivery_lifecycle_and_reactivation` | `1665bdf` | `artifacts/smoke/1665bdf5dc1b-97ABDF08862F/receipt.json` |
| `SessionContextTests.Replay_versions_and_forget_preserve_reference_integrity` | `cfa9388` | `artifacts/smoke/cfa938844bd9-27719969E84D/receipt.json` |
| `SessionContextSecurityTests.Repository_access_is_rechecked_across_tools_resources_and_successor_sessions` | `d967b75` | `artifacts/smoke/d967b750f6ce-0B8FDC2FD3B3/receipt.json` |

The fully qualified prefix is `CoCern.Tests.`. These are passing outcomes across selected milestone runs, not a claim that one final three-test run occurred. After `f684687`, the only application-source edit is cold-index activity instrumentation in `SessionContextGrain.SearchAsync`; its related persistence method was rerun at `1665bdf`. Subsequent changes before that instrumentation were confined to the replay fixture and expanded adapter-security assertions. The latest three receipts each contain exactly one passed, executed method, with no skipped or unselected case accepted. The earlier `f684687` run passed recovery and security but failed the replay fixture; its overall failed receipt is preserved honestly.

The persistence method verifies acknowledged-message independence, originating-session closure, a fixture restart on the same database, discovery by a fresh session, Lucene retrieval, HTTP MCP resource reads, failed transaction rollback and index-failure recovery with exact committed reads still available. The replay method verifies request fingerprints, stale versions, replacement/deletion/recreation, forgetting, generic provider clearing, coordinated reset and resource-framing refusal. The security method verifies distinct/revoked repository access, fresh-session identity, forged client provenance/labels, anonymous HTTP denial, HTTP MCP resource isolation, and allowed/forbidden reads through `RelayForwarder.ReadSessionContextResourceAsync` and `SessionContextMcp.ReadResult`.

The stdio adapter's actual HTTP bridge and MCP result conversion are exercised in-process against the disposable host; an installed stdio process was not launched. Development-proxy wiring was source-reviewed. Tests use disposable hosts/storage and change no machine, registry, client or user settings. No installed hub or adapter was restarted.

The command was `./scripts/Build-CoCern.ps1 -Selection artifacts/retained-session-context-selection.json`, using isolated output folders and exact fully qualified method filters. Initial sandbox attempts failed on CLI telemetry registry access and WebAssembly MSBuild task-host IPC before test execution. Process-only `DOTNET_CLI_TELEMETRY_OPTOUT=1` and approved process escalation allowed the disposable verification processes; no persistent machine setting was changed. Subsequent failures were diagnosed and corrected as described above, with reruns restricted to changed/failed methods.

## Illustrative token sample

The local sample uses the built `Microsoft.ML.Tokenizers` `o200k_base` tokenizer, eight synthetic retained reports, one compact manifest, one matching search hit and one actual 512-raw-token prefix of the relevant report. The resource count includes the same disclaimer and fenced JSON framing as the production renderer. The full-payload baseline serializes all eight reports. This is an illustrative payload comparison rather than a model-executed continued-task benchmark.

| Payload or read count | Full corpus | Selective retrieval |
| --- | --- | --- |
| Serialized payload tokens | 6,266 | 945 |
| Manifest / search / resource part tokens | — | 127 / 99 / 719 |
| Reads | 1 | 3 |

Local script/output: `artifacts/retained-context-token-sample.ps1` and `.json`. These ignored artifacts accompany the verification receipts on this workstation. The sample excludes request/envelope/tool-catalog/history/reasoning tokens and client-specific resource handling. It establishes neither production savings, billed-token reduction, model accuracy nor latency improvement. Functional recovery of the selected evidence is verified separately by the persistence test. A real model-task evaluation remains future measurement, not a release gate or an implemented claim.

## Rulings made

These preserve the execution ledger's decisions in order, including their practical costs.

1. Use a dedicated branch in the existing authorized checkout instead of another worktree to preserve Roslyn/Visual Studio bindings and the planning documents. Cost: concurrent source edits in that checkout would require reconciliation.
2. Follow AGENTS.md and the approved plan instead of skill defaults requiring TDD, per-task runs and a full suite. Cost: source review carries interim coverage risk; verification proves only the selected related boundaries.
3. Preserve existing JSON tool-envelope conventions instead of adding unused typed DTO graphs; new shared records cover resource addresses/replies and Domain catalog projection. Cost: fewer compile-time checks for JSON field wiring.
4. Compose dynamic resources through an optional delegate on the existing workflow registration, including the development proxy. Cost: a backend without the delegate explicitly refuses dynamic reads and must opt in to support them.
5. Keep body-free deleted catalog tombstones so replayed forgetting does not spuriously advance discovery revisions. Cost: minimal identifiers/revisions remain until reset, although titles, summaries and bodies are removed.
6. Reserve 250 reply tokens for envelope/link content and validate complete resource framing plus escaped JSON parts before saving. Cost: metadata-heavy content can be refused below its raw-text ceiling.
7. Include a persisted collection generation in exact entry references beyond the draft's revision-only address. Cost: one additional opaque public identifier in exact addresses and pinned tool reads; current manifests intentionally remain mutable.
8. Verify runtime behavior here while leaving pre-existing Roslyn SDK/generator issues unchanged. Cost: runtime evidence comes from isolated binaries, and unrelated semantic-tool limitations remain.
9. Use the illustrative serialized payload comparison plus functional recovery assertions instead of claiming a model-executed continued-task benchmark. Cost: no empirical production/model-efficiency or accuracy conclusion can yet be drawn.

## Handover

The feature, review fixes, targeted verification and recovery guidance are complete. The branch has not been merged, pushed or deployed. A deployment would apply schema version 3 through the established backup/migration path. Review the current task/control state before any later deployment; retained context itself grants no authority.

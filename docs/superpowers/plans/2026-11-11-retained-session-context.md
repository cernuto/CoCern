# Retained Searchable Session Context Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan. The repository's no-TDD, source-review and selected-test rules override skill defaults. This document authorizes no implementation or deployment by itself.

**Goal:** Give CoCern sessions a persistent, searchable context collection that later authorized sessions can discover and retrieve through bounded tools and MCP Resources.

**Architecture:** A new persistent `SessionContextGrain` owns each stable repository/context collection and a rebuildable Lucene RAM index, following the existing document-search grain pattern. SQLite commits a small discovery projection atomically with context state. Three worker tools and resource templates expose the same authorized, versioned views; a new session discovers by task or purpose and reads a compact manifest before retrieving details.

**Tech Stack:** Existing C#/.NET, Orleans, SQLite, Lucene.NET, Microsoft.ML.Tokenizers and the current MCP SDK; no new dependencies.

**Implementation status (2026-11-12):** Completed on `codex/retained-session-context`. Source review fixes and the three selected security/replay/data-loss methods are verified across the milestone runs; the final isolated solution build has zero errors. [Implementation evidence and decisions](../../reviews/2026-11-12-retained-session-context.md) records exact revisions, transport coverage, the illustrative token sample and plan adaptations. Deployment has not been performed.

**Spec:** [Retained searchable session context design](../specs/2026-11-11-retained-session-context-design.md). Read it before implementing. It records proposed defaults and the exact bounds; the implementation plan adds the source map and execution order.

## Global constraints

- Use both Roslyn CodeLens and Visual Studio MCP at the start of every C# work session. Read the Roslyn skill before semantic inspection; use Roslyn for symbols, source, references, diagnostics and test selection. Visual Studio currently targets M3TS-SNX; leave that solution and debugger undisturbed. Obtain any applicable task/resource claims before covered work.
- This plan concerns deliberately saved repository-shared knowledge. Preserve private native identity and existing repository authorization; do not infer private access from a context URI, chat label, principal match or last-used adapter session.
- Keep inbox delivery/acknowledgment, existing shared notes, document search and the `chat` profile compatible. Retained context survives closing a session, and is removed by explicit forgetting or the coordinated database reset.
- Context/entry IDs are exact 64-character maximum ASCII identifiers; canonical repository spelling uses the existing repository resolver. Use a length-prefixed or equivalent collision-safe grain key, never raw ambiguous concatenation.
- Limits: 64 current entries per collection; 8,192 UTF-8 bytes per entry; 1 MiB serialized collection state; 8 combined entry changes and 32 KiB patch per save; 8 task IDs and 8 pinned IDs; 400-token summary and 200-token combined next steps. Whole manifest: 1,200 tokens/8 KiB. Whole search/discovery/read response: 1,500 tokens/8 KiB; 5 search hits, 160-token excerpts, 512-token resource parts and 10 discovery rows maximum. Respect lower existing transport bounds.
- Use existing request replay and expected-version rules. No silent eviction, automatic transcript ingestion, automatic model summarization, renewed authority from memory, or replacement of versioned content with newer text under an old URI.
- No TDD, per-task test/build gates, whole-suite runs or unrelated test expansion. Keep new automated cases to the real authorization, replay and data-loss boundaries listed below. Verify once at the major milestone, selecting exact tests through Roslyn. No machine/client setting changes from tests.
- Keep commit/PR paragraphs and bullets unwrapped and omit all AI co-author attribution. Do not deploy or restart running processes as part of implementing this source plan without the applicable user authorization.

## Review focus

1. A context URI supplied to another transport or a new chat must still require current access to that exact repository; forged labels, source-session fields and replay envelopes must not change that scope. Covered by the consolidated security case in Task 7.
2. Acknowledgment, old-session closure, cold activation and a lost/failed write must not remove committed knowledge or publish uncommitted knowledge through discovery, search or resources. Covered by the consolidated persistence case in Task 7.
3. A retried patch, reordered update or deleted/recreated entry ID must not produce duplicated changes or let an old versioned reference resolve to different content. Covered by the consolidated replay/version case in Task 7.
4. An invalidated/rebuilding Lucene index must not return terms from forgotten or superseded content; failed indexing must leave exact durable reads available with a visible search-unavailable result. Covered with the persistence/replay cases, not a separate test matrix.
5. A new session without an old handle and an MCP client without resource discovery must both have the same bounded recovery path. Cover this in the persistence case through the tools and authenticated resource reader; use the existing MCP smoke only if Roslyn selects it for changed code.

## File structure and interfaces

The following are proposed new types and paths, not claims that these symbols exist already.

| Area | New files | Existing integration points |
| --- | --- | --- |
| Wire contract | `src/CoCern.Contracts/SessionContextContracts.cs`, `SessionContextLimits.cs`, `SessionContextResourceUri.cs` | `RelayToolCatalog.cs`, `ToolSchemas.cs`, `RelayErrorCodes.cs` |
| Domain state | `src/CoCern.Domain/SessionContextAggregate.cs`, `State/SessionContextState.cs`, `SessionContextReadModels.cs` | `CoordinationModel.cs`, `CoordinationStateMachine.cs` |
| Grain and retrieval | `src/CoCern.Core/SessionContextGrain.cs`, `SessionContextIndex.cs`, `SessionContextTools.cs`, `SessionContextPorts.cs` | `CoordinationGrains.cs`, `Aggregates.cs`, `ToolRouter.cs`, `RelayRequestGuard.cs`, `ReplyCompactor.cs` only for the new view policies |
| Persistence | `src/CoCern.Storage/SqliteStore.SessionContext.cs` | `SqliteStore.cs`, `SqliteGrainStorage.cs`, `SqliteStore.Reset.cs` |
| MCP/transport | `src/CoCern.Adapter/SessionContextMcp.cs`, `src/CoCern.Host/SessionContextEndpoints.cs` | Adapter `WorkflowMcp.cs`, `RelayForwarder.cs`, `CoordinationTools.cs`, `ConditionalRead.cs`; host `RelayMcpServer.cs`, `RelayEndpoints.cs`, `RelayHostBuilderExtensions.cs` |
| Recovery guidance | No new runtime subsystem | `src/CoCern.Adapter/WorkflowGuidance.cs`, `Node/NativeStartupEvidence.cs`, repository `skills/cocern-workflow/SKILL.md`, `README.md`, `docs/reference.md` |
| Focused boundary coverage | `tests/CoCern.Tests/SessionContextTests.cs`, `SessionContextSecurityTests.cs` | Existing storage/domain harnesses and authenticated MCP fixture, reused rather than copied |

### Contract names used by all tasks

- `SessionContextPatch`: nullable metadata changes plus entry `upserts`, `deleteEntryIds`, `taskIds` and `pinnedEntryIds`; absence preserves an existing field, while an explicit empty array clears it. Scalar metadata does not accept ambiguous `null` as a destructive write. The `forget` operation is a separate exclusive flag on the save call.
- `SessionContextEntryInput`: `entryId`, `kind`, `title`, `text`, optional `sourceRefs`. It contains no writable author, source session, time or version.
- `SessionContextCatalogQuery`: canonical repository, optional exact task ID, bounded limit and opaque cursor. `SessionContextCatalogPage` returns bounded purpose/title, related tasks, update time, context ID, manifest URI, catalog revision and continuation; no entry bodies.
- `SessionContextManifest`: context ID/revision, title, summary, next steps, task IDs and pinned entry references, including entry versions and URIs.
- `SessionContextPart`: context ID/revision, entry ID/version/kind, provenance, text, part number/count and optional next URI. First part is `0`.
- `SessionContextSearchRequest`: context ID, plain-word query and limit. `SessionContextSearchReply` contains collection revision, encoding and bounded hits with entry IDs/versions, kinds, snippets, source references and resource URIs.
- `SessionContextResourceAddress`: parsed repository/context and either manifest selection or entry ID/version/part selection. `SessionContextResourceReply` is the protocol-neutral successful DTO for a URI, MIME type and bounded Markdown text; read errors retain domain codes before MCP translation.
- `SessionContextCatalogProjection` is a Domain-owned projection of committed metadata, including deletion state. Storage consumes this type instead of reading Domain JSON paths itself.

## Task 1: Define contracts, identifiers and bounded projections

**Files:** Create the three Contracts files and the Domain state/read-model files above; update the catalog/schema/error-code integration points and `CoordinationModel.cs`.

**Interfaces produced:** `SessionContextResourceUri.TryParse(string uri, out SessionContextResourceAddress address)`, `ManifestUri(string repositoryId, string contextId)` and `PartUri(string repositoryId, string contextId, string entryId, long entryVersion, int part, string generation)`; the DTOs above; the Domain catalog and manifest read models. The persisted generation addition is the final-review reset/recreation fence recorded in the implementation ledger.

- [x] Define `AggregateKinds.SessionContext` and its collision-safe key helper. Add the three public tool names, input schemas and new bounded error codes: `ContextBudgetExceeded`, `ContextVersionUnavailable`, `ContextListRestartRequired` and `ContextSearchUnavailable`, reusing existing not-found/version/replay refusal codes where equivalent.
- [x] Define the current-entry model and collection revision. Entry versions equal the revision that last changed the entry; a removed ID reused later receives a new revision. Specify minimal collection-deletion tombstones and body-free replay receipts.
- [x] Implement pure URI parsing/building and projection helpers. Validate path segments, exact ID syntax, positive entry versions and nonnegative parts, rejecting malformed or extra URI components. Repository authorization happens after parsing and canonicalization, never in the URI builder.
- [x] Centralize the spec's limits and response-budget checks. Validate UTF-8 bytes separately from UTF-16 title length and tokenizer counts; reject oversized required manifest metadata before saving.
- [x] Define deterministic 512-token entry parts and a fixed first-release chunker version. Part and next-URI generation must preserve original text, including Unicode boundaries. Include only current retained versions and refuse obsolete requested versions explicitly.

**Deliverable:** One shared schema and address vocabulary for the grain, tools and both MCP transports, without changing existing tool behavior.

## Task 2: Persist collections and discovery in one transaction

**Files:** Create `SessionContextAggregate.cs`, `SessionContextGrain.cs`, `SessionContextPorts.cs` and `SqliteStore.SessionContext.cs`; integrate `CoordinationStateMachine.cs`, `CoordinationGrains.cs`, `Aggregates.cs`, `SqliteGrainStorage.cs`, `SqliteStore.cs` and `SqliteStore.Reset.cs`.

**Interfaces:** `ISessionContextGrain : IRelayAggregateGrain` adds `Task<SessionContextSearchReply> SearchAsync(RelayCaller caller, SessionContextSearchRequest request)` in Task 3. `ISessionContextCatalog.ReadAsync(SessionContextCatalogQuery query, CancellationToken cancellationToken)` returns `Task<SessionContextCatalogPage>`. `SessionContextAggregate.Handle(AggregateContext context)` handles save, manifest and part reads through the existing aggregate dispatcher.

- [x] Add the new aggregate dispatch and mutation classification. Implement creation under expected version `0`, atomic metadata/entry patches, body-free replay receipts, explicit entry deletion and exclusive `forget:true`. Validate all IDs and bounds before changing the candidate. Stamp authorship/time from the verified call; do not trust matching client fields.
- [x] Add the persistent grain using the existing storage provider and candidate-commit/reconciliation behavior. A missing context read remains not-found and does not create a collection. Session expiration and board archiving must not target retained-context grains.
- [x] Add the catalog schema at the next schema version using the existing backup/migration path. Store repository/context key, title/purpose preview, task associations, update time and context revision; use a repository/update-time index and a persisted repository catalog revision. Keep entry bodies out of the catalog.
- [x] Extend `SqliteGrainStorage.WriteStateAsync` and `SqliteStore.CommitAsync` with the Domain catalog projection. Upsert/remove catalog metadata and increment its revision within the same transaction as the retained grain state. Keep existing search/archive/due-work commits intact. A rollback must leave both state and discovery unchanged.
- [x] Implement bounded discovery with repository/task filtering and a cursor bound to that selection, catalog revision and hub incarnation. A changed snapshot returns `ContextListRestartRequired`, not an invented continuation. Add projection repair/backfill from the new Domain read model for retained-context states if needed; run no migration of ordinary inbox bodies into memory.
- [x] Include the catalog/projection counters and the new aggregate kind in coordinated reset and applicable storage cleanup. Forget removes catalog content atomically with the tombstoned collection; existing database backup semantics remain unchanged.

**Deliverable:** Durable explicit context saving and discovery that survive old-session closure and process restart, independently of Lucene and inbox state.

## Task 3: Add the rebuildable Lucene grain index

**Files:** Create `SessionContextIndex.cs`; finish `SessionContextGrain.cs`. Reuse `DocSearchIndex.cs`, `DocCacheText.cs` (`ApiDocAnalyzer`, `DocQuery`, `DocChunks`) as source patterns. Change shared primitives only when actual reuse requires it.

**Interfaces produced:** `SessionContextIndex.Replace(SessionContextState state)`, `Apply(SessionContextState state)` and `Search(SessionContextSearchRequest request)`, with `IDisposable`; the grain search method defined in Task 2. `Apply` compares entry versions, so unchanged entries are not reindexed.

- [x] Build the sibling index with `RAMDirectory`, the current analyzer/plain-word query approach, BM25, entry IDs/versions/kinds and selective snippet highlighting. Index actual context fields, never fake document paths or Git commit identifiers. Keep excerpts and complete results inside the spec's budgets.
- [x] Lazily build from committed collection state on the first search, with the new index staged until complete. Store the indexed collection revision and apply only changed/removed entries on subsequent searches. Do not serialize Lucene objects or keep a second durable copy of the bodies.
- [x] Use the same uncertain-commit guard for search as for aggregate operations. If the base grain needs an accessor, add `protected bool TryGetCommittedDocument(out CoordinationDocument document)` which refuses access while its commit is uncertain; do not expose mutable state outside the grain. Do not add reentrant index mutations.
- [x] Dispose the index on deactivation. Index failures invalidate search and return `ContextSearchUnavailable`; direct manifest/part reads stay available from committed state. Never report an empty successful search when building failed.
- [x] Remove forgotten/superseded entries before serving a newer revision. Responses identify the collection revision used, so subsequent exact-part reads can detect replacement rather than silently accepting newer content.

**Deliverable:** Ranked, bounded retrieval from a selected context collection without scanning its complete contents through the model or activating every old collection.

## Task 4: Expose the three model tools and final response budgets

**Files:** Create `SessionContextTools.cs`; integrate `ToolRouter.cs`, `RelayRequestGuard.cs`, `RelayHostBuilderExtensions.cs`, `CoordinationTools.cs`, `ConditionalRead.cs`, the Contracts catalog/schemas and narrowly scoped compaction policy in `ReplyCompactor.cs` if necessary.

**Interfaces produced:** `SessionContextTools.PutAsync(RelayCall call, CancellationToken cancellationToken)`, `GetAsync(RelayCall call, CancellationToken cancellationToken)` and `SearchAsync(RelayCall call, CancellationToken cancellationToken)`, each returning `Task<RelayReply>`. `ReadResourceAsync(RelayCaller caller, SessionContextResourceAddress address, CancellationToken cancellationToken)` returns `Task<SessionContextResourceReply>` for Task 5 with shared read authorization/projection.

- [x] Route `session_context_put`, `session_context_get` and `session_context_search` through current caller/session validation, tool-profile checks, repository normalization and replay envelopes. Creation requires an explicit stable context ID; an ID collision under expected version `0` is a conflict, not automatic renaming.
- [x] Implement discovery, manifest and part modes for `session_context_get` with explicit mutually exclusive fields. A first unversioned entry read returns its version and part `0`; subsequent part reads retain that version. Never fetch the whole collection to return one part.
- [x] Add the conditional-read capability for exact `session_context_get` selections. Preserve authorization before every unchanged response. Do not turn search or saves into conditional operations or add the tools to the intentionally narrow `chat` profile.
- [x] Bound the final returned metadata plus text, not only individual snippets. Prefer omitting optional previews and weakest search hits; keep cursors and required mutation receipts. Resource links and text must not duplicate full entry content.
- [x] Register only existing services/dependencies. Keep ordinary `search` on its current SQLite FTS backend; retained-context search is explicit rather than silently broadening its scope or scanning every context.
- [x] Attach existing call-origin/token telemetry and small context-specific counts for save/read/search, conflicts, discovery and index rebuilds. Do not log entry bodies, search queries or source text. Count final model-facing responses and resource projections consistently.

**Deliverable:** An LLM can save, discover, search and read context through ordinary tools even when its MCP host does not surface Resources.

## Task 5: Compose MCP Resources across HTTP and stdio

**Files:** Create `SessionContextMcp.cs` and `SessionContextEndpoints.cs`; integrate `WorkflowMcp.cs`, `RelayForwarder.cs`, `RelayMcpServer.cs`, `RelayEndpoints.cs` and host service registration.

**Interfaces produced:** A composed `WithCoCernResources` registration taking a dynamic-read delegate, static workflow helpers and the two resource templates from the spec; `RelayForwarder.ReadSessionContextResourceAsync(string uri, CancellationToken cancellationToken)` using the authenticated read-only hub route; both use `SessionContextResourceReply` and the shared URI parser.

- [x] Extract composition from the existing workflow registration without changing its workflow resource or prompt. Register list/template/read handlers once. Keep the workflow resource in `resources/list`; put only the two bounded dynamic templates in `resources/templates/list`. Dynamic instances are discovered through the tools.
- [x] Implement the core resource read against current credential access to the URI's exact canonical repository. It must use the same manifest/part projection as tools and must not rely on a last-used session, native hook arrival, label or URI secrecy.
- [x] Wire HTTP MCP directly to that service and add authenticated `GET /api/session-context/resource?uri=...` for the stdio adapter. Preserve existing transport restrictions and error policy. The route is read-only and returns a protocol-neutral DTO; it does not perform tool/session joining as a side effect.
- [x] Convert results to bounded `text/markdown` resources. Return exact versioned URIs for parts and a current manifest URI with its revision in the content. Translate unavailable versions, deleted/missing contexts and repository denials without leaking another repository's existence. Match the negotiated protocol's resource errors.
- [x] Add SDK resource-link content to relevant tool results where supported. Resource-template discovery and resource reads must work without subscriptions, push notifications or a new SDK/protocol version. The equivalent tool read is the supported model fallback.

**Deliverable:** A context reference resolves identically through authenticated HTTP MCP, stdio MCP and the bounded tool view, without crossing repository boundaries.

## Task 6: Implement the new-session recovery workflow and documentation

**Files:** `WorkflowGuidance.cs`, `Node/NativeStartupEvidence.cs`, repository `skills/cocern-workflow/SKILL.md`, `README.md`, `docs/reference.md`; adjust the skill packaging source only if current bundling requires it.

- [x] Add one short recovery instruction to existing startup/resume/compaction guidance: when continuing work without enough context, discover saved context for the task, read its manifest and retrieve details selectively. Do not inject the retained corpus into startup or perform background saves/reads on every turn.
- [x] Document the exact sequence from the spec: own normal join; `session_context_get` with task filter or bounded discovery; select a matching context; read manifest; search; fetch exact parts; recheck current task/claims/controls; update at a milestone under the new session identity. Same-chat replacement and a wholly new Codex/Claude chat use the same sequence.
- [x] Show discovery ambiguity explicitly. A remembered context ID can skip listing, but the current task determines relevance; neither the newest row nor a matching label implicitly selects or authorizes work. A missing/forgotten/reset context is reported honestly and the session continues from current source/task evidence.
- [x] Explain retention and visibility in the save tool description and docs: deliberately saved repository-shared context, independent of inbox acknowledgment; private transcript capture is outside scope. Explain versioned-resource replacement errors, explicit forgetting and historical backup/replay semantics.
- [x] Provide a small A-to-B example using one finding, one decision and one handoff with related task ID. Session B uses no credential or handle from A. Document tools as the fallback when a client does not expose resource templates.

**Deliverable:** A fresh authorized session can find and use the saved knowledge without relying on prompt history, copied credentials, automatic resource inclusion or a live old session.

## Task 7: Review and verify once at the implementation milestone

**Files:** New `SessionContextTests.cs` and `SessionContextSecurityTests.cs`; extend existing boundary fixtures only where needed. No production change is made merely to satisfy a broad gate.

- [x] Add `SessionContextSecurityTests.Repository_access_is_rechecked_across_tools_resources_and_successor_sessions`: use two disposable repositories and distinct callers. The authorized new session reads published context without the old handle; a caller lacking repository access cannot list, search or read it by exact ID/URI, including through the other transport or after a previously successful read. Client-provided provenance/labels do not affect access.
- [x] Add `SessionContextTests.Committed_context_survives_delivery_lifecycle_and_reactivation`: save a bounded handoff/evidence set, acknowledge the source message, close the source session, deactivate/restart the fixture and discover/read/search it from a new session. Inject a failed or uncertain commit and indexing failure; assert no uncommitted content reaches state, catalog or search and direct committed reads remain accurate. Both tool and protocol-neutral resource-reader views stay bounded. Use the existing disposable Orleans/storage fixture and no machine settings.
- [x] Add `SessionContextTests.Replay_versions_and_forget_preserve_reference_integrity`: replay the same save, conflict changed arguments, issue stale expected versions, replace an entry between part reads and delete/recreate its ID. Assert monotonic versions, no double apply, no text substitution under old URIs, and removal from current catalog/search/resources after forget/reset. Fold the related replay/deletion assertions together rather than multiplying parameterized cases.
- [x] Review source for all five Review Focus items and each spec requirement. Check schema migration/backup/reset paths, profile/catalog costs, both MCP registrations and absence of accidental whole-collection reads. Keep the design's builder-check result with the handoff; the initial check found no consequential unanswered questions.
- [x] At the major milestone, ask Roslyn `find_tests_for_symbol` / `analyze_change_impact` for the actual changed production symbols and save exact related methods with their symbol evidence in ignored `artifacts/retained-session-context-selection.json`. Include only mapped KEEP/smoke methods. Before implementation, exploratory mappings for `DocSearchIndex.Search` and `WorkflowMcp.WithCoCernWorkflow` returned no tests; do not treat that as permission for a blanket suite or assume those mappings will stay unchanged.
- [x] Use Roslyn diagnostics for source errors. Build only at the authorized milestone when the real binary is needed, into isolated output using `./scripts/Build-CoCern.ps1 -Selection artifacts/retained-session-context-selection.json`. Inspect actual selected TRX outcomes; no unselected, skipped or missing cases count as success. No tests or builds are part of writing this plan.
- [x] Record an illustrative serialized continued-task payload comparison with and without selective retrieval, including manifest/search/read token counts and read counts; verify correct recovery separately in the persistence boundary method. This is a narrower substitute for a model-executed continued-task benchmark, recorded as a ruling in the implementation evidence. Do not claim measured production savings or impose a benchmark threshold as a merge gate.

**Milestone acceptance:** Session B recovers A's intentionally saved task context after A closes and after a hub restart, using its own connection; relevant details are searchable through Lucene and readable as bounded MCP Resources; all repository, replay/version and data-loss boundaries above hold. Deployment remains a separately authorized action.

## Planning evidence and handoff

- Roslyn CodeLens is connected to `C:\Users\PaulC\source\repos\CoCern\CoCern.slnx`; Visual Studio MCP is connected to `M3SNXSolution`. Neither solution was switched during planning.
- Source inspection confirmed `DocSearchIndex` uses `RAMDirectory` and BM25; `DocSearchGrain` performs lazy/version-aware loading and disposes its index; current context notes already have version/provenance; inbox acknowledgment removes indexed inbox bodies; current MCP resource handling is static workflow-only.
- The working tree was clean when planning began. The original planning task added only the design and implementation-plan documents; subsequent implementation and its verification are recorded in the linked evidence report. No deployed binaries or client settings were changed.
- The design received the brainstorming skill's fresh builder check; its result was `nothing important left`. Proposed visibility, bounds and current-version-only retention remain explicit design defaults for Paul to review.

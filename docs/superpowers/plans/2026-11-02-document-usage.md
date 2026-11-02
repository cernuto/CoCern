# Document utilization on Documents

User request: show utilization stats for each document. Continue unattended, using Roslyn CodeLens for C# semantics and Visual Studio MCP for actual builds/debugger operations.

## Scope and meaning

Show actual per-document search activity, matched sections, successful section reads, last use and tracking start. Keep source loads/index builds separate as cache-work counters. Usage must survive document-grain deactivation and hub restart, contain metadata only, and distinguish unavailable historic data from observed zero use. Match-count label must reflect the actual instrumentation boundary rather than imply final wire-delivery counts if tracked earlier.

## Implementation boundaries

- Backend: document-grain/directory state and document DTOs; additive persisted usage schema, no query/content/token recording. Preserve search timeout/failure/rollback behavior and remove obsolete path counters.
- Projection: passive activity snapshot and frame bounds; match stats to exactly the shown paths, fairly allocate the unchanged global filename budget, trim path/metric pairs coherently.
- UI: compact readable per-file stats with explicit period, honest unknown states, existing index diagnostics separate; mobile and accessibility checks.
- Root: coordination, exact tool/claim checks, RED/GREEN Visual Studio checkpoints, browser verification, final review and integration evidence. Source files for Claude's independent SESSION-CLEANUP remain separate.

## Verification

1. Capture meaningful failing tests before production behavior changes; DTO declarations may precede the checkpoint to keep Razor/test compilation possible.
2. Verify usage increments, zero/no-match/missing-section handling, path/repository isolation, persistence across reload and metadata-failure behavior without extra source reads or document activation.
3. Verify fair global path limit and exact JSON frame bounds including Unicode and matching usage metadata.
4. Verify generated Razor symbols after reload, compiler diagnostics, relevant regressions and live browser behavior using real doc_search/doc_section requests.
5. Leave hub running, preserve sessions/sign-in, stop fixtures, report evidence and any limitations. No root commits or pushes requested.

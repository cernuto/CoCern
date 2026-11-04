# Session fallback retry correction

> **For agentic workers:** Use superpowers:subagent-driven-development for one bounded implementation task and one independent task review. No deployment before root validation.

**Goal:** A session-required call that omitted its own handle keeps the original session on a same-request retry after the adapter default changes.

**Architecture:** Keep the existing verified-own-handle fallback. Bind the effective private envelope for an implicitly addressed request to its original repository/tool/request ID within that adapter instance; preserve fully explicit raw calls and do not add persistence, a new transport, a public lookup or another credential registry. Same-request retry is not permission to replay an uncertain mutation under a replacement session.

**Spec:** Paul's direct request to validate/redeploy origin/main604dfae or later, with its session fallback and corrected guidance. Review finding main-session-fallback-review.md in root's manager-safeguards integration workspace.

## Constraints

- Base604dfae008b777ea66e399dcd7c65b3c3e7b4c1f; isolated session-fallback-replay native worktree. Primary VS/main remains running/read-only; do not edit or build it.
- Both Roslyn CodeLens semantic MCP and Visual Studio MCP required; read the Roslyn skill first. Load/verify exact correction solution, use semantic lookups/diagnostics, no C# grep/build diagnostics. Native actual file/Git/test operations need require_escalated.
- Preserve explicit sessionId/sessionToken, including blank/null/invalid/foreign/partial values and the existing no-foreign-completion rule. Preserve raw HTTP/schema/session-open/control-request behavior and authorization. No guessed native identity, peer handle or privileges.
- Private binding data never appears in model replies or logs. Keep exact business input/request ID; no auto mutation retry, session recreation or transfer of unresolved work.
- Recovery after an adapter restart is not a process-memory guarantee. Codex without exported native identity still needs its saved token until session_open; an uncertain old write must be reconciled in its original session, never retried under a new default. Describe this honestly.
- Root owns runtime publish, Git integration and full suite. No live config, credentials, node, hook, grant, account or app changes. No subagents.

## Task 1: Preserve an implicit retry's effective session

**Files:** CoordinationTools.cs, AdapterInstructions.cs and docs/reference.md under their existing src/CoCern.Adapter/docs paths; tests/CoCern.Tests/AdapterChatCallsTests.cs. Preserve public signatures. Do not edit SessionHandoff, LocalChatWake, hub replay semantics, tool catalog or other source. Root plan file is committed separately.

- [ ] Reproduce the exact P2 with a disposable real hub: authenticated session A is learned; an implicitly addressed send_message with request R commits but its response is deliberately lost; a valid explicitly authenticated call or session_open changes the default to B; identical original omitted-handle arguments R retry. Baseline must deliver twice or return a different sender/message receipt. Compilation/fixture failure is not RED. Use existing host/test seams and synthetic credentials; no real endpoint.
- [ ] Implement the smallest immutable per-request self-envelope binding, or an equally small explicit refusal that prevents that retry executing as B. Reuse existing tool/request classification. The original chosen handle survives default updates; concurrent resolution cannot cross envelopes. Fully explicit callers keep their existing session-scoped behavior.
- [ ] Keep the internal request-binding memory bounded without silently evicting a known retry and reassigning it. If a new implicit binding cannot be recorded, fail closed with fixed guidance and leave explicitly addressed calls usable. A fixed internal capacity (4096) is sufficient; no user setting or persistent request store. Do not broaden into automatic join or global exact-once delivery.
- [ ] Add negative/positive controls: same implicit retry uses A after explicit B or new session_open; concurrent calls do not cross; altered business arguments reuse original scope and receive existing hub conflict; explicit B is preserved; omitted/foreign/null/partial input rules remain; failed calls cannot poison the own cache; no handle in ordinary result/error. Clarify the old inbox test's title if it claims acknowledgement it does not perform.
- [ ] Qualify AdapterInstructions for process-local compaction versus restart with positively exported identity. Preserve the documented Codex limitation and original-session requirement for uncertain retries; no fabricated recovery promise.
- [ ] Run meaningful RED then GREEN and one covering AdapterChatCalls/Adapter/LocalWake/SessionHandoff-relevant filter. Record exact TRXs/counts, semantic diagnostics and restart limits. No full solution during implementation. Root owns the final corrected full-suite/build/publish window.
- [ ] Self-review, commit only the four assigned source/doc/test files, no AI trailers or hardwrapped commit prose; report precise private API/cache limits, repro observations, commands, paths, commit and concerns to the private report.

## Review focus

A changed default must not turn the same original request into a second write; a partial explicit envelope must not acquire another session's token; saturation must fail safely instead of forgetting scope; a failed call must not teach ownership; a backend restart must not be described as preserving process-local uncertain-request bindings.

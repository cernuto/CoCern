# Native join reliability implementation plan

**Goal:** Make an authorized Codex chat join CoCern on its first real tool call, preserve its existing classic identity, and explain setup failures before asking for a resume.

**Spec:** Paul's supplied task `NATIVE-JOIN-RELIABILITY-20261106`, incorporating Claude's `native-join-reliability-design-20261106` report.

**Architecture:** Keep private registration, exact per-call stamps and caller repository authorization. Resolve authorized repositories independently of whether the hub has an explicit clone. Record startup intent without allocating; the first real stamped call joins or adopts the exact privately proved classic session. Connect runs only a nonallocating preflight.

**Tech stack:** Existing .NET/Blazor projects, native Codex hooks, private node state and existing CoCern session aggregates. No new runtime dependencies.

## Constraints

- Initial authority kept work on the current branch with no live client changes. Paul subsequently approved FC's adapter and hook-path update, personally reviewed the modified hooks, and requested commit, merge and push. Enrollment credentials, unrelated client configuration and programmatic trust approvals remain unchanged; synthetic native events/stamps are prohibited.
- Network caller paths remain untrusted; `HubLocalRequest.IsLocal` controls local checkout evidence. `caller.MayAccess` remains the repository authority.
- Preserve classic session identity, label, tasks, IDE bindings, inbox and credentials. Invalid adoption proof must allocate nothing.
- Source reviews precede a single accumulated deploy verification. No TDD, test matrices, per-fix builds or unrelated tests; select exact tests with Roslyn and use temporary/fake seams only.
- Use Roslyn plus the Visual Studio connection checks; leave the unrelated live M3TS Visual Studio instance untouched under Paul's existing exception.
- No desktop control. Browser verification must use the built-in browser only and must not take over Paul's desktop; resolve any remaining conflict before that check.

## Implementation ownership and acceptance

- [x] Repository resolution: use discovered registry clones as origin candidates; carry a validated optional configured repository ID; allow an exactly authorized hint or unique finite authorization for clone-less repositories; reject unauthorized, ambiguous and conflicting known origins. Extend the existing broad resolver regression. Owner: native_mapping_reliability.
- [x] Classic adoption: load only this exact chat's private classic handle; send proof only on the private wire; verify and attach native participant fields atomically to the same session; retain idempotent request/replay fences. Extend the existing session/native continuity regressions for preservation and invalid-proof nonallocation. Owner: native_classic_adoption.
- [x] Lazy startup: genuine SessionStart records pending intent, with no directory row or background allocation; first genuine PreToolUse establishes the binding and signs within its bounded budget; preserve inert metadata and registration/call authorization. Extend existing hook regressions for no-call nonallocation and mid-life first-call join. Owner: native_lazy_join.
- [x] Connect diagnostics: run bounded, read-only native preflight for the configured checkout during readiness refresh; show exact terminal refusal codes and the relevant next action, separately from startup, session and catalog readiness. Read sanitized own-connection diagnostics for this chat's missing catalog. Extend the existing broad readiness regression. Owner: root.
- [x] Integration: source review closed all findings; 12 Roslyn-selected cases passed across the initial six passes and six failure-only reruns. The self-contained Windows package was verified and deployed. Connect refresh passed in the hidden built-in browser. After Paul's separate path-update approval and human hook review, FC's genuine native inbox/directory/peer calls and self-send/receive/ack passed under its original session. No programmatic trust approval was written.

## Review focus

- Known clones with wrong origins must not fall through to a permissive repository hint.
- Adoption credentials must never enter public hook output, logs, shared notes or persisted native request arguments.
- Concurrent adoption or a retried request must retain one identity and reject a different native participant.
- Merely starting an ephemeral thread or listing MCP tools must not allocate a directory session.
- An old successful startup receipt must not hide a current preflight refusal or claim that an absent MCP catalog loaded successfully.

The earlier bootstrap-gate repair remains a prerequisite, and its four selected regressions passed before this broader task packet arrived. FC's original session, token, label, enrollment and configured permissions were preserved during the authorized live verification. Server role/task/IDE fields were covered by the authenticated Host regression rather than a separate live reread. A separate Manager ping remains unverified; fresh-PC/SC3 checks and the implementation chat's older-helper catalog are separate outstanding work.

# Reliable Codex setup implementation plan

> For agentic workers: use bounded subagents for independent source changes; the manager owns integration, deployment and evidence.

**Goal:** A normal installation produces a usable CoCern connection without native hook approval or manual bridge recovery, and preserves an explicitly selected working mode during repair.

**Architecture:** Apply the existing native-autojoin Task 4 rule: the standard connector remains usable while unverified native integration is only staged. Existing native callers retain their authentication mode and strict runtime checks. Network-listener requests remain remote even when a tunnel forwards their socket over loopback.

**Tech stack:** Existing .NET, MCP SDK, BlazorBlueprint and Windows package; no additional runtime dependencies.

**Spec:** Existing `2026-11-03-native-autojoin.md`, Task 4, plus Paul's November 6 request to remove installation/setup friction and his SC3 Visual Studio tunnel question.

## Global constraints

- Keep repository permissions, enrollment credentials, session ownership, native stamps, helper-version checks and explicit user disables intact.
- Do not turn a refused native invocation into a raw operation or change an existing native caller's authentication mode silently.
- Routine setup, Connect and Repair must not activate native mode or re-enable native handlers.
- Optional native staging or workflow-skill maintenance cannot prevent a standard connector or an enrolled background node from starting.
- Do not overwrite a user-modified workflow skill; retain it and report the optional limitation.
- Do not report a SessionStart receipt as proof that MCP tool calls succeeded.
- Use both required semantic/IDE connections under Paul's existing M3TS-SNX Visual Studio exception; all browser verification uses the built-in browser.
- No TDD, suites or per-task test gates. At the real deployment milestone select only directly relevant methods using Roslyn mappings/impact, then verify those methods and actual browser behavior.

## Task 1: Keep standard setup usable

Modify Codex setup, Connect, enrollment and Repair so standard entries receive independent workflow-skill maintenance and optional private inactive staging. Return a truthful `standard` readiness state. Preserve native mode when it was already selected, preserve hook/configuration bytes and disabled choices, and refresh existing private helper metadata only when the running helper is provably the effective configured image and the previous candidate's scope/profile/definition are valid. Record no synthetic startup or tool proof.

Extend only the existing staging/repair regressions needed to pin the activation, disabled-handler and helper-update failures. Map their changed symbols before the deployment run.

## Task 2: Make Connect describe the connection accurately

Recognize standard mode as a normal connected state and remove native approval/checklist requirements from that path. Keep genuine missing, stale, foreign or failed client configuration visible. Show an existing native receipt as native startup verification, without implying full tool-call verification. Keep the page compact.

## Task 3: Preserve local trust at the network listener

Use one host-owned locality predicate for local dashboard operations and same-host checkout identity. Exclude the configured network HTTPS port even if the peer is loopback; preserve existing host/header checks and genuine local endpoint behavior. Add one focused security-boundary regression and any needed resolver regression using fixture-local connection metadata.

Only the network listener is a candidate for remote forwarding. Forwarding the actual privileged local listener remains local access by design and is not presented as an ordinary remote-node connection.

## Task 4: Package and verify the accumulated change

Review the complete source diff, confirm generated Razor contains the updated helpers, and run the small exact Roslyn-selected regression set. Produce binaries into isolated artifacts, preserving small apphosts and all live output folders. Deploy against the existing database/token without rewriting approved client hook definitions. Verify standard-mode installation/repair with isolated fixtures, the Connect page with the built-in browser, and denial of local-only privileges on the network listener. Add a simple Connect-page link to the existing hub-served Windows adapter ZIP and verify its response. Use FC's existing shared chat to investigate the missing native stamp and determine supported native recovery, preserving its original working classic connection while gathering genuine hook evidence. No fake native events, credentials, trust approval or permission bypass may substitute for real client verification.

Document that ordinary private dev-tunnel web URLs need separate tunnel authorization and present a different TLS certificate. Describe private raw port forwarding to the network listener only after its local-trust boundary is fixed; do not create or expose a tunnel as part of answering feasibility.

## Review focus

- Classic configuration plus disabled owned hooks survives repeated setup/repair unchanged.
- Existing native mode never masquerades as standard when its launch or candidate is invalid.
- A helper update cannot rewrite trusted commands, fake verification, or relax runtime version/scope checks.
- A forwarded loopback peer on the network port cannot obtain local viewer access or same-host checkout proof.
- Stale addresses, pins and errors remain visible even when a standard readiness state is present.

# Native registration bootstrap repair

Status: implemented and source-reviewed. The catalog/registration regressions passed, including independently authenticated concurrent backends. The later native-join reliability milestone verified genuine native FC calls under its original session after an authorized path update and human hook review.

FC's hooks are enabled and trusted, and its native repository preflight now succeeds after repairing the explicit hub checkout mapping. Its native MCP connection still fails registration during hot reload. A fresh ordinary turn did not recover tool discovery, and the available headless attachment failed. All further work must use APIs, CLI and semantic tools without desktop or browser control.

The adapter currently waits for a one-shot private registration exchange before starting MCP initialization. The genuine hook only answers briefly on SessionStart or PreToolUse. Once an uninitialized MCP connection disappears, it cannot supply the tool invocation that would run PreToolUse. Increasing both startup timeouts does not remove this dependency.

## Runtime ordering

Validate the existing setup-owned candidate, private file protections, profile, credential scope, definition and exact helper module before exposing an explicitly selected native endpoint. Initialization and catalog discovery then provide inert metadata. Neither operation proves hook trust or native startup, allocates a session, starts background hub recovery, or grants permission to invoke a tool.

Start a per-backend registration listener before MCP runs. Re-arm failed or expired exchanges with fresh challenges until authentication or shutdown; retain success only in memory for that backend. Genuine hook handling may drain the bounded set of concurrently waiting instances within its existing budget. Never share an authenticated flag or use a previous startup receipt as a substitute for a new instance exchange.

Require the gate in the native tool factory. Every invocation must pass that backend's authenticated registration and current candidate check before the existing call-bound MAC, expiry, generation, invocation, argument and binding validation. Keep these invocation checks unchanged. Start native background recovery only after registration succeeds, and cancel listeners and recovery cleanly with the host.

Default standard setup and inactive native preparation remain unchanged. This repair deliberately changes discovery ordering while retaining invocation authorization. Metadata availability must not be presented as native verification.

## Verification and deployment

Use source review as the safety net. Extend the existing broad native wiring regression to exercise discovery before the genuine fixture helper, refusal with zero hub work before authentication, and retained stamp requirements after authentication. Cover late or concurrent registration through the same bounded fixture where practical. Select only the relevant methods through Roslyn at the actual distribution milestone; no TDD, matrices or unrelated runs.

FC's approved commands currently point into an immutable package. Do not overwrite that package, relax its module check, or write Codex trust approvals. Prepare the corrected package and exact owned configuration update before requesting any review that Codex requires for changed definitions. Preserve FC's original identity, private bindings, enrollment and permissions. Actual verification uses the same FC chat's normal Codex tools; synthetic fixture success does not replace this live check.

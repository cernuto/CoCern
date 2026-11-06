# Native Codex onboarding milestone

Paul authorized implementation of Connect and verify, automatic native Codex startup and a CoCern workflow skill. Existing native identity checks, private credentials and Codex hook trust review remain the authority boundaries.

1. Extend the credential-free Connect contract with native setup and observed readiness evidence.
2. Install owned Codex hook definitions and the packaged user skill idempotently, preserving unrelated configuration. Never write trust approvals. GET status remains read-only.
3. Reuse NativeJoin on real SessionStart events, including resume and compaction; emit concise workflow context without exposing saved credentials. Persist bounded, private readiness evidence and verify the live handle and wake route before reporting ready.
4. Add a guided Connect panel with distinct configured, awaiting startup and verified states, actionable next steps and refresh.
5. Review source, refresh Roslyn, select only tests tied to changed symbols, produce isolated deployment binaries at the milestone, deploy to the existing hub/database and smoke-test with the built-in browser. Report any real trust-review step that requires Paul.

No TDD or broad test gates. Preserve the existing launchSettings edit and M3TS-SNX Visual Studio instance. Do not commit or push without a new instruction.

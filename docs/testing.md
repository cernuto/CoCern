# Verification

Follow AGENTS.md. Work in significant chunks and verify once at a deploy or major milestone. Do not run tests to decide which tests to delete.

Before verification, use Roslyn CodeLens `find_tests_for_symbol` or `analyze_change_impact` for the production symbols changed since the last deployment. Select only related retained tests. No covering tests means no test run. Retaining a security test does not make it relevant to every change.

The startup smoke candidates are `HostTests.McpSdkNegotiatesListsAndCallsAuthenticatedCoordination`, `DashboardTests.Dashboard_serves_its_wasm_shell_and_deep_links_without_capturing_other_routes`, and `DevelopmentLauncherTests.The_real_launcher_reconnects_to_a_changed_adapter_without_restarting_its_client`. They cover authenticated hub MCP startup/answer, dashboard shell/deep-link answers, and an actual adapter executable/reconnect using an isolated fake hub. Include them only when the changed symbols map to them. Add the related security, replay and data-loss KEEP tests from that same Roslyn selection, not the whole suite.

Save the selection under ignored `artifacts/`, with the current full commit, changed symbols and exact fully qualified test methods. Each selected method must name the changed symbols it covers and the Roslyn evidence that selected it. For example:

```json
{
  "commit": "FULL_CURRENT_COMMIT",
  "changedSymbols": ["FULL_CHANGED_PRODUCTION_SYMBOL"],
  "tests": [
    {
      "project": "tests/CoCern.Tests/CoCern.Tests.csproj",
      "method": "CoCern.Tests.EXACT_RELATED_TEST_METHOD",
      "covers": ["FULL_CHANGED_PRODUCTION_SYMBOL"],
      "evidence": "Roslyn find_tests_for_symbol result for this symbol at this commit"
    }
  ]
}
```

One command builds into isolated output and runs only that selection:

```powershell
./scripts/Build-CoCern.ps1 -Selection artifacts/deploy-smoke-selection.json
```

The runner bounds the entire check, preserves logs and a receipt, and refuses an unchanged repeat with the same selection. An empty test selection builds only; it never expands into an unfiltered test run. `-Publish` retains the Windows host and self-contained adapter packaging option after verification; it does not deploy or restart an installed service. Do not publish into a running app's output folders.

Tests use disposable fixtures only. They must not change drive mappings, registry, user or client settings. KEEP covers actual authentication, token/provenance and route-binding boundaries, replay/data-loss fences and the minimal related startup checks. Presentation permutations, repeated catalog/shape assertions, timing thresholds and duplicate server fixtures are removed.

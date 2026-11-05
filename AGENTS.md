# Rules for every agent working in this repository

These are hard rules set by Paul on 2026-11-05. Only Paul can grant an exception, in his own words in the chat.

## Testing

1. **Never run tests that have nothing to do with the code that changed.** Select only the tests tied to the changed files and types. No whole-suite, whole-project or whole-class reruns for unrelated changes. If you cannot name the changed code a test covers, do not run it.
   How to select: before any run, take the symbols you changed and ask Roslyn CodeLens for the tests that cover them (`find_tests_for_symbol`, `analyze_change_impact`). Run exactly those tests and nothing else. If it returns no tests, run no tests. Name the changed symbols in your message when you report a run.
2. **Save testing until significant code changes have accumulated.** No test or build run per fix, per feature or per commit. Work in big chunks, commit and push freely, and verify once at a deploy or a major milestone: the solution builds and the small KEEP smoke set passes.
3. **No TDD.** Failing-first tests, single-line revert proofs and adversarial test matrices are not merge conditions. Add a test only for a real security boundary, a replay or data-loss fence, or a regression that actually happened.
4. **No full gates and no per-batch gates.** Reviews are source-only and are the safety net.
5. **Tests must never change the machine they run on.** No `net use`, registry, user settings, drive mappings or client configuration. Use a seam in the code instead.
6. **Keep the suite small.** Delete duplicate and redundant tests, merge near-identical ones, and prefer a few broad tests over many narrow ones.

## Coordination

- Report only at milestones or blockers, in short messages. No start, PID or end announcements.
- Do not commit a Co-Authored-By trailer. Do not hard-wrap commit messages or pull request text.

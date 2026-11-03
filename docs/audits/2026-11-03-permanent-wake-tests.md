# Permanent automatic wake verification

Verified November 3, 2026 against implementation `a01f5ede1e332b21df3f03055eafdc8f2902774f`, merged into main as `a732d29`. The immutable deployed adapter has content version `2a5e4636db1d6f14b9f7bbe93ea2c7dd4ee16fcf67a6410d55b99a61e526ca06` and ProductVersion `1.0.0+a01f5ed`.

## Implementation

Normal setup, local connection setup and development setup install or refresh CoCern's Claude Stop hook automatically. It uses the existing ClaudeWaiter and WakeWatcher, delivers the inbox with UTF-8 intact, exits 2 for a native wake, and arms again after the next ended turn. Unrelated hooks and credentials are retained. The identified temporary project Stop handler was backed up and removed; no scratch waiter was used for these tests.

Explicit-token MCP adapters now own the existing Codex wake watcher for the lifetime of their connection. Successful authenticated calls restore existing session handles, including conditional reads, under the original principal and certificate pin. Private per-session locks and native ownership checks prevent duplicate or conflicting dispatch. Existing node behavior is retained. Both existing local participants were reused rather than reopened.

## Live checks

| Check | Result | Evidence |
| --- | --- | --- |
| Hub-PC Codex, already idle | Pass | Turn ended `21:31:39.378Z`; remote nonce `ROOT-IDLE-PERM-10` stored `21:32:43.734Z`; native turn started `21:32:51.298Z`, about 7.6 seconds later. |
| Native root correlation | Pass | Batch `825d762d-8bea-4b1d-93a0-91e79cf14a11`; message `0d4562f86e994dbea0bebe66872974c8`; correlated acknowledgment `8468dee83b7b4edf8422ff113694e275`. No manual queue command or human prompt. |
| Claude product hook, already idle | Pass | Previous turn ended `21:23:29.257Z`; timed nonce sent about `21:24:51Z`; native wake `21:24:52.672Z`, about 1.7 seconds later. Installed product helper identity/version was independently observed. |
| Automatic Claude re-arm | Pass | The installed `CoCern.Adapter.exe --stop-hook` armed after each ended turn; the scratch project handler count was zero. |
| Remote-PC Codex | Pass | Nonce `PERMANENT-WAKE-REMOTE-09` resumed an ended chat through its existing node, with a durable inbox read about 9 seconds after storage. The remote installed adapter remained e1743e1; no remote reinstall was required for the local integration. |
| FYI-only quiet window | Pass | Both peers retained `wake:false` markers for about 149 seconds without an observed model turn. The local Claude transcript was independently checked across the quiet interval. |
| Positive controls after FYI | Pass | A separate actionable message woke Claude in about 2 seconds and remote Codex in about 19 seconds; both acknowledged the prior FYI and the new nonce together. |
| Cross-PC document read | Pass | Remote Codex used doc_search/doc_section for the permanent-wake plan, anchor `global-constraints`, and reported commit `a732d29da5ad`. |
| Telephone variations | Pass | Exact Unicode relay, byte-exact JSON relay including array order and escapes, and compact transformation returning total 4.35, ordered IDs Z9/B2/A0 and case-sensitive label MiXeD-λ. The initial approval delay is excluded from transport timing. |

The root's earlier automatic resume, batch `33fa8a34-93f7-4ea9-9298-c6337089279f`, also worked after its turn ended. That was a deferred normal report; the first timed nonce arrived during the resumed active turn and was deliberately not counted as an already-idle nonce pass.

## Regression evidence and limits

The isolated real test project passed 215/215 focused wake, setup, session restoration, node, hook and origin regressions with zero skipped. The known pre-existing Windows Git PATH test was explicitly excluded. Results are retained at `artifacts/permanent-wake/results/permanent-wake-isolated.trx`. Independent review found no remaining blockers after authenticated conditional restoration and concurrent native ownership fixes.

Claude's installed handler has an explicit 24-hour deadline; its waiter stops five minutes earlier, matching the existing idle-session policy. Installation persists, and each ended turn arms a fresh waiter. Local Codex watching ends when its MCP connection closes. Raw-token-only configurations do not gain new credential persistence.

Windows MSIX redirects each app's private local-wake cache into its own package LocalCache. The same scope hash therefore has separate Claude and Codex copies; each app's helper finds its own handle. Cross-app filesystem scans are not a reliable inventory. The existing directory reachable flag still describes registered node routes and does not advertise these local adapter-owned watchers.

Remote quiet-window evidence uses its visible native chat history and the hub's tool trace, not an independently inspected remote rollout file. Legacy remote node reads retain unknown origin classification; the updated product Claude helper's background calls were explicitly marked background. Reported origin alone is not proof of model activity.

The running hub was retained. Concurrent inbox-performance and dashboard/analytics work owned by other sessions was excluded from the immutable test candidate and remains separately coordinated.

# Feature build execution ledger

Prepared 2026-11-03 Pacific against the reviewed [20-case acceptance matrix](2026-11-03-feature-build-acceptance.md) and [approved design](../superpowers/specs/2026-11-03-auto-connect-design.md). This ledger separates runner validation, supporting protocol probes and real native-client/two-PC release acceptance.

**Release status: NOT RUN. No physical-PC or native-client pass is claimed by this work.** The host's latest handoff describes feature candidate `2f90d97` as not release-ready, with ten Important review findings undergoing a local fix wave. That report is context, not a candidate executed here. Main/deployed builds and installed remote MCP/node versions are separate identities.

## Ownership and current evidence

- Remote branch `codex/feature-acceptance-runner` owns only this ledger, [the runner](../../scripts/Smoke-FeatureAcceptance.py) and [its Python safety tests](../../scripts/tests/test_feature_acceptance_runner.py). Existing scripts and C# are unchanged. Root owns implementation, candidate review and execution on its host; 3aa owns integration/deployment.
- The initial ten inert safety tests passed. Independent review then identified five runner defects; the consolidated correction reproduced them with 18 inert tests (11 failing assertions/subtests), then passed 21 tests after correction. No hub, installed node, real credential or live endpoint was used. Syntax and CLI help checks also passed. This validates runner boundaries, not feature behavior.
- All five supporting protocol probes below are **NOT RUN** pending source review and a reviewed host-built candidate. No new package transport or local candidate build is required: root will execute against its own isolated Host and Adapter outputs.
- Claude's old waiter activation remains unverified, and the local 3aa Codex participant lacks a native binding according to the handoff. These native activation/permission-preservation prerequisites remain outstanding; neither scratch-waiter success nor a protocol reply closes them.

## Supporting protocol runner

| Probe | Supports matrix case | Current status | Required observation |
| --- | --- | --- | --- |
| `auth-refusal` | 8.4 | NOT RUN | Missing/invalid credentials refused, valid owner succeeds, another principal cannot use the private session. |
| `backup-restoration` | 8.2 | NOT RUN | Removing only the disposable primary restores the original TLS identity/private-key operation and preserves an existing session. |
| `missing-identity-copies` | 8.2 | NOT RUN | Established identity with both copies absent reports the specific recovery refusal, creates no new identity and preserves evidence. An unrelated nonzero exit cannot pass. |
| `corrupt-backup` | 8.2 | NOT RUN | Missing primary plus corrupt backup reports the specific backup-load recovery refusal without overwriting the backup/marker or generating a replacement. |
| `identity-pin-refusal` | 8.3, negative supporting observation only | NOT RUN | Correct-pin relay control succeeds; wrong pin reports `hub_identity_changed` before any protected request reaches the fixture; a fresh correct-pin control succeeds afterward. Direct-human retrust is a separate native gate. |

The runner requires explicit absolute candidate DLL paths and expected SHA-256 values. It never selects a build by modification time. `--declared-commit` records the operator's source declaration; verified file hashes identify the executed binaries. Do not describe that declaration alone as independently proven embedded version or source provenance.

Before further probes, each child hub must demonstrate its ASP.NET cookie key repository is inside its disposable profile. Child-only `WEBSITE_INSTANCE_ID` and synthetic `HOME` select the website key path; a synthetic fixture login forces cookie protection, and both the actual key file and effective repository log must match that path. Production mode and explicit `Relay:DevelopmentAdapter:Enabled=false` prevent development publication. Inherited `COCERN_`, `RELAY__`, `CODEX_` and `CLAUDE_` variables are removed case-insensitively; child-only native profile paths, node data and proxy settings are isolated. No real chat identity is supplied to the protocol adapter.

Network HTTPS is disabled; HTTP and local HTTPS use the fixture's exact numeric loopback ports. Readiness requires the host's effective listener logs to contain exactly those two origins. Discovery admission stays enabled separately. Fingerprint and adapter calls use local HTTPS, never the disabled network listener. HTTP connects directly, ignores proxy environment and follows no redirects. Cross-origin or header-injection targets are rejected before opening a connection.

Expected recovery exits must contain the fixed recovery message, the intended missing-primary/backup-load reason, and the owned identity-file path in that startup's logs. This is an internal classifier for the reviewed host contract, not a public error-code claim; confirm it against the final candidate before execution. Evidence emits only a fixed recovery category. The pin-negative probe requires the adapter's specific `hub_identity_changed` reply introduced by connection-error Task 1 (`d32f21b`), so an older adapter's generic error cannot pass by compatibility inference.

Raw host logs, authentication headers, cookies, private keys and session handles remain memory-only. Evidence publishes booleans/statuses and candidate hashes, not those secrets. Final report creation is exclusive even if another writer creates that path during the probes. Only the fixture's own child processes may be stopped; adapter termination is unconditional if stdin close fails, and streams/readers are cleaned up. Certificate deletion is limited to its two owned fixture files. Cleanup does not touch a live hub, installed client, registry entry, node or grant.

### Reproducible validation and execution

Runner safety validation, without a .NET process:

```powershell
py -3 -m unittest discover -s scripts/tests -p test_feature_acceptance_runner.py -v
py -3 -m py_compile scripts/Smoke-FeatureAcceptance.py scripts/tests/test_feature_acceptance_runner.py
py -3 scripts/Smoke-FeatureAcceptance.py --help
```

After source review, root supplies exact candidate DLL paths and independently recorded expected hashes from its reviewed build. Example placeholders below are deliberately incomplete; replace them with the candidate receipt. Use a fresh output filename and run from the branch containing the reviewed runner:

```powershell
py -3 scripts/Smoke-FeatureAcceptance.py --host 'C:\isolated-candidate\host\CoCern.Host.dll' --adapter 'C:\isolated-candidate\adapter\CoCern.Adapter.dll' --host-sha256 '<expected-64-hex-host-hash>' --adapter-sha256 '<expected-64-hex-adapter-hash>' --declared-commit '<reviewed-source-commit>' --output 'artifacts/feature-acceptance/supporting-results.json'
```

An optional repeated `--probe` selects a subset. Each report records supporting probe status, UTC start and duration; every report retains `nativeReleaseGate: NOT RUN`. Any exercised assertion mismatch is FAIL. Record fixture/bootstrap failures as such; they are not a valid red result for missing product behavior. Existing evidence is never overwritten. No C# build command is part of this runner.

## Real-client release gates

Every row remains **NOT RUN**, including rows supported by the protocol probes. Record actual native chat IDs/client versions, genuinely separate PCs, candidate identities, direct scoped human decisions where required, timestamps and sanitized evidence references before changing status. Root coordinates deployment and test windows; client permission prompts and effective authority must remain intact.

| Case | Required journey | Status | Evidence |
| --- | --- | --- | --- |
| 8.1 | Fresh native setup on two PCs, with plain-language client/dashboard copy. | NOT RUN | Pending reviewed candidate and isolated native setup. |
| 8.2 | Established identity backup/recovery. | NOT RUN | Supporting probes pending. |
| 8.3 | Genuine changed identity and explicit bounded retrust. | NOT RUN | Protocol negative alone is insufficient. |
| 8.4 | Authentication refusal and distinct actionable errors. | NOT RUN | Supporting probe pending; native copy remains separate. |
| 9.1 | Native manager administration without dashboard navigation. | NOT RUN | Pending public operations and native journeys. |
| 9.2 | Automatic verified node update preserving identity and wake readiness. | NOT RUN | Previous manual node update is not this feature's acceptance. |
| 9.3 | Failed automatic update rolls back to a working node. | NOT RUN | Pending implemented updater and isolated failure injection. |
| 9.4 | Alert cleanup and no repeated wake after handling. | NOT RUN | Pending candidate/native observations. |
| 9.5 | Direct answer recorded with bounded permission and replay behavior. | NOT RUN | Pending compliant-chat and record-layer checks. |
| 9.6 | Once-only Standing presentation, deduplication, quiet idle and dismissal. | NOT RUN | Pending native Codex/Claude journeys. |
| 10.1 | Default native worker startup without feature switches. | NOT RUN | Native binding/permission-preservation prerequisites outstanding. |
| 10.2 | Worker cannot self-enable administrative power. | NOT RUN | Pending capability contract and negative controls. |
| 10.3 | Honest running-client metadata after change and reconnect. | NOT RUN | Separate native clients required. |
| 10.4 | Cheap reads and tips without noisy wakes or inflated metrics. | NOT RUN | Prior read savings are not this candidate's gate. |
| 10.5 | Rare safe tool succeeds on first use without added authority. | NOT RUN | Pending public first-use contract/native observations. |
| 11.1 | Label grants no manager power; explicit appointment does. | NOT RUN | Inert selection tests do not establish later powers. |
| 11.2 | Concurrent manager handoff has one winner. | NOT RUN | Pending reviewed contract and race/restart evidence. |
| 11.3 | Stale, closed and stuck manager recovery rejects delayed authority. | NOT RUN | Pending isolated recovery/native controls. |
| 11.4 | Recovery dashboard, attributable actions and private-handle refusal. | NOT RUN | Pending reviewed candidate; no live recovery action here. |
| 11.5 | All-chat visibility, reassignment and manager request/goal boundary. | NOT RUN | Pending native manager/worker journeys. |

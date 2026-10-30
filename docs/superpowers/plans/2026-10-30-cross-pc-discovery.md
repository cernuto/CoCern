# Cross-PC Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PCs on the local network find the PkRelay hub, enroll themselves with no code typed, report the chats that have connected to the relay, and wake those chats from routes a discovery grain decides.

**Architecture:** A new discovery aggregate in `PkRelay.Domain` and its grain in `PkRelay.Core` own machines, connected chats, machine credentials and wake routes. The host gains an HTTPS listener on all interfaces with a self-generated certificate, a UDP beacon, an anonymous enrollment endpoint and machine-credential authentication. The adapter gains `--node` (find hub, pin its certificate, enroll, announce, map chats, wake local Codex chats) and `--setup` (logon start and client MCP registration); the stdio adapter and `--await-inbox` read the node's state.

**Tech Stack:** .NET 10, ASP.NET Core (Kestrel, rate limiting), Microsoft Orleans 10, SQLite, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-30-cross-pc-discovery-design.md`

## Global Constraints

- C# work loads the `roslyn-codelens:roslyn-codelens` skill first and uses its semantic tools for lookups and diagnostics (user rule, every subagent brief repeats it).
- Never build into a running app's output folders. The hub currently runs from `src/PkRelay.Host/bin/Debug/net10.0` (Visual Studio) and waiters run from `artifacts/adapter-*-wake`. Stop the hub (`mcp__visualstudio__debugger_stop` or close its console) before any build or `dotnet test`, and restart it from Visual Studio afterwards.
- One hub silo and one SQLite file. No Orleans cluster across PCs.
- The hub listens on all network interfaces; existing loopback HTTP on port 5271 keeps working unchanged.
- No join code. Any machine that reaches the hub may enroll.
- Enrolled machines get principal `machine:<machineId>`, `IsAdministrator = false`, and the repositories in `Relay:Discovery:Repositories`, default `["pkrelay"]`. They are refused `control_request`.
- Only chats that called `session_open` are announced or woken.
- The discovery grain decides identity and wiring only; nothing assigns work.
- Each PC wakes only its own chats. Wake prompts stay fixed and never contain message text.
- Tokens, machine credentials and session secrets never appear in logs, tool output, the directory, or repository files. Credentials are stored hashed at the hub and in current-user-only files on each PC.
- Setup never changes firewall rules. It backs up any client settings file before editing, edits idempotently, and leaves other settings untouched.
- Existing behaviour stays: the start script, the Visual Studio launch profile's loopback client, `--wake` and `--await-inbox <config>` modes, existing sessions, and all 154 existing tests.
- Commit messages: no `Co-Authored-By` trailer, no hard-wrapped lines (user rule).

## Review Focus

1. **Hub certificate changes** after a node pinned it (data folder wiped, hub moved): the node must stop and report the mismatch, never re-trust silently. Test in Task 6.
2. **A second node started on the same PC** with the same data folder: it must fail fast on the state lock instead of announcing a duplicate identity. Test in Task 6.
3. **Hub unreachable** (laptop off the network, hub stopped): the node keeps its identity and retries with backoff; it must not re-enroll under a new machine ID. Test in Task 6.
4. **A chat that cannot be mapped**, or maps to more than one thread: it is announced as connected but not wakeable, never woken by a guess. Test in Task 7.
5. **A malformed client settings file** (invalid TOML or JSON): setup refuses to edit it, leaves it byte-for-byte unchanged, and reports which file. Test in Task 9.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/PkRelay.Domain/DiscoveryAggregate.cs` (new) | Machines, chats, credential hashes, routes, directory, revocation rules |
| `src/PkRelay.Domain/CoordinationModel.cs` | Add `AggregateKinds.Discovery`, discovery operation names |
| `src/PkRelay.Core/DiscoveryGrain.cs` (new) | Grain interface, grain, `IMachineDirectory` service over it |
| `src/PkRelay.Core/RelayApplication.cs` | Route `chat_directory` and `control_request` `revoke_machine` |
| `src/PkRelay.Contracts/RelayToolCatalog.cs`, `ToolSchemas.cs` | `chat_directory` tool; `revoke_machine` command |
| `src/PkRelay.Host/DiscoveryEndpoints.cs` (new) | Enrollment and announce endpoints, rate limiting |
| `src/PkRelay.Host/RelayAuthentication.cs` | Machine-credential authentication |
| `src/PkRelay.Host/HubNetwork.cs` (new) | All-interface HTTPS listener, certificate, UDP beacon |
| `src/PkRelay.Adapter/Node/*.cs` (new folder) | Node state, hub locator, certificate pinning, enrollment, announce loop, chat locators, route-driven waking |
| `src/PkRelay.Adapter/Setup/*.cs` (new folder) | Logon start, Codex and Claude Code MCP registration |
| `src/PkRelay.Adapter/AdapterOptions.cs`, `RelayForwarder.cs`, `Program.cs`, `WakeWatcher.cs`, `CoordinationTools.cs` | Node fallback, pinned handler, new modes, dynamic routes, session handle hand-off |
| `tests/PkRelay.Tests/Discovery*Tests.cs`, `NodeTests.cs`, `SetupTests.cs` (new) | Tests per task |
| `docs/discovery.md` (new), `README.md`, `docs/wake.md`, `src/PkRelay.Host/Properties/launchSettings.json` | Docs and local profile |

---

### Task 1: Discovery aggregate (domain rules)

**Files:**
- Create: `src/PkRelay.Domain/DiscoveryAggregate.cs`
- Modify: `src/PkRelay.Domain/CoordinationModel.cs` (constants)
- Test: `tests/PkRelay.Tests/DiscoveryDomainTests.cs`

**Interfaces:**
- Produces: `AggregateKinds.Discovery = "discovery"`, key `AggregateKinds.ControlKey` (`"hub"`).
- Produces: `public static class DiscoveryStateMachine { public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now); }`. Unlike `CoordinationStateMachine`, it is machine-scoped, not repository-scoped; internal operations require `caller.IsInternal`.
- Produces: `public static class DiscoveryOperations` with `Enroll = "__enroll"`, `Validate = "__validate"`, `Announce = "__announce"`, `Directory = "__directory"`, `Revoke = "__revoke"`.
- Produces: `public static class DiscoveryLimits { MaxMachines = 256; MaxChatsPerMachine = 64; AnnounceInterval = 30 s; LivenessTimeout = 90 s; }`.
- Reply shapes (JSON in `DataJson`):
  - `__enroll` args `{ machineId, machineName, credentialHash }` → `{ machineId, principalId }`. Rejects with `CapacityExceeded` past `MaxMachines`, the new code `MachineRevoked` (added to `RelayErrorCodes`, classified `Denied`) for a denied ID, and `AliasConflict` if the ID is already enrolled with a different hash.
  - `__validate` args `{ credentialHash }` → `{ machineId, principalId }` or `NotFound`. Read-only.
  - `__announce` args `{ machineId, machineName, clients: string[], chats: [{ provider, threadId, relaySessionId, repositoryId, label, wakeable, waiterRunning }] }` → `{ routes: [{ provider, threadId, relaySessionId, repositoryId }] }` for that machine's wakeable chats. Replaces the machine's chat list, updates `lastSeen`.
  - `__directory` args `{ repositories: string[] }` → `{ items: [{ label, provider, machineName, relaySessionId, repositoryId, reachable }] }`, filtered to the given repositories. `reachable` = machine seen within `LivenessTimeout` and (Codex: `wakeable`; Claude: `waiterRunning`). Never includes `threadId`, hashes or credentials.
  - `__revoke` args `{ machineId }` → `{ revoked: true }`; removes its credential hash and chats, adds the ID to the deny list. Caller must be administrator.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Enroll_then_validate_resolves_the_machine_principal()
// Apply(__enroll {machineId:"m1", machineName:"PC-B", credentialHash:H}) Ok;
// Apply(__validate {credentialHash:H}) data.principalId == "machine:m1"; unknown hash → Code "NotFound".
[Fact] public void Reenrolling_the_same_id_with_another_hash_is_refused()   // Code == "AliasConflict"
[Fact] public void Revoked_machine_id_cannot_enroll_again()                  // Code == "MachineRevoked"
[Fact] public void Announce_returns_routes_only_for_wakeable_chats_of_that_machine()
// two chats, one wakeable:false → routes.Count == 1, routes[0].threadId == the wakeable one
[Fact] public void Directory_filters_repositories_and_hides_thread_ids()
// chat in "other" repo excluded for repositories:["pkrelay"]; DataJson does not contain any threadId or hash
[Fact] public void Machine_silent_past_liveness_timeout_is_not_reachable()  // now + 91 s → reachable == false; record still present
[Fact] public void Revoke_requires_administrator()                           // non-admin internal caller → "AdministratorRequired"
```

- [ ] **Step 2: Run** `dotnet test tests/PkRelay.Tests/PkRelay.Tests.csproj --filter DiscoveryDomainTests` → FAIL (types not defined).
- [ ] **Step 3: Implement** `DiscoveryAggregate.cs` following the existing aggregate style (`JsonFields`, `Replies`, `using static RelayErrorCodes`), state shape `{ machines: { <id>: {...} }, denied: { <id>: true } }`. Add `MachineRevoked` to `RelayErrorCodes` (classified `Denied`).
- [ ] **Step 4: Run** the filter → PASS; run the whole suite → 154 + 7 pass.
- [ ] **Step 5: Commit** `feat(domain): add discovery aggregate for machines, chats and routes`

### Task 2: Discovery grain and machine directory service

**Files:**
- Create: `src/PkRelay.Core/DiscoveryGrain.cs`
- Modify: `src/PkRelay.Core/RelayApplication.cs` (`AddPkRelayCore` registers the service)
- Test: `tests/PkRelay.Tests/DiscoveryGrainTests.cs` (real silo, as `CoreSiloTests`)

**Interfaces:**
- Consumes: Task 1 `DiscoveryStateMachine`, `DiscoveryOperations`.
- Produces: `public interface IDiscoveryGrain : IGrainWithStringKey { Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson); }` and `[GrainType("pkrelay.discovery")] DiscoveryGrain` with `[PersistentState("discovery", "relay")]`. Same commit-or-reload discipline as `CoordinationGrain.CommitAsync`.
- Produces: `public sealed record MachineIdentity(string MachineId, string PrincipalId);`
- Produces: `public interface IMachineDirectory` registered as singleton:
  - `Task<MachineIdentity?> EnrollAsync(string machineId, string machineName, string credential, CancellationToken ct)` — hashes `credential` with SHA-256; returns null on refusal.
  - `Task<MachineIdentity?> ValidateAsync(string credential, CancellationToken ct)` — caches positive results for 30 s.
  - `Task<RelayReply> AnnounceAsync(MachineIdentity machine, JsonObject announcement, CancellationToken ct)`
  - `Task<RelayReply> DirectoryAsync(IReadOnlyList<string> repositories, CancellationToken ct)`
  - `Task<RelayReply> RevokeAsync(RelayCaller administrator, string machineId, CancellationToken ct)` — also evicts the validation cache.

- [ ] **Step 1: Write failing tests** `Enrollment_survives_grain_deactivation` (enroll, deactivate via a second silo start on the same memory storage, validate still resolves) and `Revocation_takes_effect_immediately_despite_cache` (validate, revoke, validate → null).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** grain and service.
- [ ] **Step 4: Run** → PASS; full suite green.
- [ ] **Step 5: Commit** `feat(core): add discovery grain and machine directory service`

### Task 3: Directory tool and revocation command

**Files:**
- Modify: `src/PkRelay.Contracts/RelayToolCatalog.cs`, `ToolSchemas.cs`, `src/PkRelay.Core/RelayApplication.cs`
- Test: `tests/PkRelay.Tests/DiscoveryGrainTests.cs`, `ContractsTests.cs`

**Interfaces:**
- Produces: tool `RelayTools.ChatDirectory = "chat_directory"`: not a mutation, no session, not administrator-only, required `repositoryId`. Description: "List chats on every PC that have connected to the relay, with the session ID to message and whether each is reachable now. Shows no secrets." It returns `DirectoryAsync` filtered to the caller's repositories intersected with the requested `repositoryId`.
- Produces: `control_request` command `revoke_machine` with argument `machineId`, routed to `IMachineDirectory.RevokeAsync`.

- [ ] **Step 1: Failing tests:** `Chat_directory_returns_only_callers_repositories`, `Revoke_machine_command_is_administrator_only`, and keep `Forwarding_catalog_stays_within_its_context_budget` passing (`< 20_000`).
- [ ] **Step 2–4:** run → FAIL, implement, run → PASS.
- [ ] **Step 5: Commit** `feat: add chat_directory tool and revoke_machine command`

### Task 4: Machine-credential authentication and enrollment endpoints

**Files:**
- Create: `src/PkRelay.Host/DiscoveryEndpoints.cs`
- Modify: `src/PkRelay.Host/RelayAuthentication.cs`, `RelayEndpoints.cs`, `RelayHostBuilderExtensions.cs`
- Test: `tests/PkRelay.Tests/DiscoveryHostTests.cs` (uses `RelayHostFixture` with `Relay:Discovery:Enabled = true`)

**Interfaces:**
- Consumes: Task 2 `IMachineDirectory`.
- Configuration: `Relay:Discovery:Enabled` (default `false`), `Relay:Discovery:Repositories` (default `["pkrelay"]`).
- `RelayAuthenticationHandler`: after configured clients and before OAuth, when discovery is enabled, `ValidateAsync(token)`; success → `RelayAuthentication.Principal(identity.PrincipalId, false, discoveryRepositories, Scheme.Name)`. The HTTPS-or-loopback rule still applies.
- `POST /api/discovery/enroll` — anonymous, body `{ machineId, machineName }`, response `{ machineId, credential }` where the hub generates `credential` (32 random bytes, hex). Rate-limited per remote IP: 10 per minute (`Microsoft.AspNetCore.RateLimiting` fixed window). Refusals return the reply's code with `403` for `MachineRevoked`, `409` for `AliasConflict` and `503` for `CapacityExceeded`.
- `POST /api/discovery/announce` — machine principals only (403 for configured clients), body as Task 1 `__announce` minus `machineId`, response `{ routes }`. Sends a SignalR hint `discovery.changed` to group `machine:<id>` when that machine's routes change.

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] Enrolled_machine_can_call_coordination_tools_but_not_control_request()
// enroll → credential; GET /api/tools with it → 200; POST control_request → 403 AdministratorRequired
[Fact] Machine_principal_is_limited_to_discovery_repositories()   // repositoryId "repo-not-listed" → 403 RepositoryDenied
[Fact] Enrollment_is_rate_limited_per_address()                    // 11th enroll within a minute → 429
[Fact] Enroll_and_announce_return_404_when_discovery_is_disabled()
[Fact] Credentials_never_appear_in_logs()                          // capture ILogger output; does not contain the issued credential
```

- [ ] **Step 2–4:** run → FAIL, implement, run → PASS; full suite green.
- [ ] **Step 5: Commit** `feat(host): authenticate enrolled machines and add discovery endpoints`

### Task 5: Hub on the network: HTTPS on all interfaces and UDP beacon

**Files:**
- Create: `src/PkRelay.Host/HubNetwork.cs`
- Modify: `src/PkRelay.Host/RelayHostBuilderExtensions.cs`
- Test: `tests/PkRelay.Tests/HubNetworkTests.cs`

**Interfaces:**
- Configuration: `Relay:Network:Enabled` (default `false`), `Relay:Network:HttpsPort` (default `5273`), `Relay:Network:BeaconPort` (default `5274`), `Relay:Network:CertificatePath` (default `data/hub-certificate.pfx`, content-root relative).
- `public static class HubCertificate { public static X509Certificate2 LoadOrCreate(string path, string hostName); public static string Fingerprint(X509Certificate2 certificate); }` — self-signed ECDSA P-256, two-year validity, SAN = host name plus every non-loopback IPv4 address, saved as a PFX with no password and current-user-only file permissions. `Fingerprint` = uppercase hex SHA-256 of the raw certificate.
- Kestrel, when enabled: keep the existing URLs and add `ListenAnyIP(HttpsPort, o => o.UseHttps(certificate))`.
- `internal sealed class HubBeacon : BackgroundService` — UDP listener on `BeaconPort`; on datagram `PKRELAY-DISCOVER/1` replies to the sender with JSON `{ "url": "https://<host-or-ip>:<HttpsPort>/", "fingerprint": "<hex>" }`; ignores anything else; replies at most 20 times per second.

- [ ] **Step 1: Failing tests:** `Certificate_is_reused_across_starts` (two `LoadOrCreate` calls → same fingerprint), `Certificate_file_is_private_to_current_user` (single explicit ACL rule for the current user), `Beacon_answers_discover_and_ignores_other_datagrams` (send to the beacon on loopback with a free port; reply parses, fingerprint matches; a garbage datagram gets no reply within 500 ms).
- [ ] **Step 2–4:** run → FAIL, implement, run → PASS.
- [ ] **Step 5: Docs:** write the one inbound firewall rule for ports 5273/TCP and 5274/UDP into `docs/discovery.md` as a command Paul runs himself.
- [ ] **Step 6: Commit** `feat(host): listen on the network over HTTPS and answer discovery beacons`

### Task 6: Node: find, pin, enroll, announce

**Files:**
- Create: `src/PkRelay.Adapter/Node/NodeState.cs`, `HubLocator.cs`, `PinnedCertificate.cs`, `NodeRunner.cs`
- Modify: `src/PkRelay.Adapter/Program.cs`, `RelayForwarder.cs`, `AdapterOptions.cs`
- Test: `tests/PkRelay.Tests/NodeTests.cs`

**Interfaces:**
- `NodeState` — folder `%LOCALAPPDATA%\PkRelay\node` (overridable with `--data-dir` for tests and for a simulated second machine). Files: `node.json` `{ machineId, hubUrl, fingerprint }`, `credential` (current-user-only), `node.lock` (exclusive, held while the node runs), `sessions\<relaySessionId>.json` (session handles, current-user-only), `waiters\<threadId>.pid`. `public static NodeState Open(string? dataDir)`; `MachineId` generated once (`Guid.NewGuid().ToString("N")`).
- `public sealed record HubAdvertisement(Uri Url, string Fingerprint);` `public static Task<HubAdvertisement?> HubLocator.FindAsync(int beaconPort, TimeSpan timeout, CancellationToken ct)` — broadcasts `PKRELAY-DISCOVER/1` to `255.255.255.255:<beaconPort>` and to loopback.
- `public static HttpClientHandler PinnedCertificate.CreateHandler(string fingerprint)` — accepts the server certificate only when its fingerprint matches; no redirects, no cookies (as `RelayForwarder.CreateHandler`).
- `AdapterOptions` gains `string? PinnedFingerprint`; `RelayForwarder.CreateHandler(AdapterOptions)` returns the pinned handler when set. `RelayHubConnection.Create` uses the same handler.
- `NodeRunner.RunAsync(NodeState state, CancellationToken ct)`: locate hub (or use stored `hubUrl`), pin on first use, enroll once, then announce every 30 s and on a `discovery.changed` hint. On fingerprint mismatch: write the reason to stderr and exit code 4. Hub unreachable: keep identity, retry with backoff capped at 60 s.
- CLI: `--node [--data-dir <path>] [--hub <https-url>]`.

- [ ] **Step 1: Failing tests** (Review Focus 1–3):

```csharp
[Fact] Changed_hub_certificate_stops_the_node_without_retrusting()   // pinned F1, hub presents F2 → exit 4, node.json still F1
[Fact] Second_node_on_the_same_data_dir_fails_fast()                 // second Open → AdapterConfigurationException within 1 s
[Fact] Unreachable_hub_keeps_identity_and_does_not_reenroll()        // enroll count stays 1 across 3 failed announce cycles
[Fact] First_run_enrolls_once_and_announces()                        // in-process host fixture; machine appears in __directory
```

- [ ] **Step 2–4:** run → FAIL, implement, run → PASS.
- [ ] **Step 5: Commit** `feat(adapter): add node mode that finds, pins, enrolls and announces`

### Task 7: Connected chats: session hand-off and chat mapping

**Files:**
- Create: `src/PkRelay.Adapter/Node/ChatLocators.cs`
- Modify: `src/PkRelay.Adapter/AdapterOptions.cs` (node fallback), `CoordinationTools.cs` (hand-off), `Node/NodeRunner.cs` (include chats in announcements)
- Test: `tests/PkRelay.Tests/NodeTests.cs`

**Interfaces:**
- `AdapterOptions.FromEnvironment`: when neither `PKRELAY_URL` nor `PKRELAY_TOKEN_FILE`/`PKRELAY_TOKEN` is set and node state exists, use the node's `hubUrl` (loopback `http://127.0.0.1:5271/` on the hub PC), its credential and fingerprint. When they are set, behave exactly as today.
- After a successful `session_open`, the stdio adapter (only when running on node credentials) writes `sessions\<relaySessionId>.json` `{ sessionId, sessionToken, repositoryId, label, openedAt }` with current-user-only permissions.
- `public sealed record ChatLocation(string Provider, string ThreadId);` `public interface IChatLocator { string Provider { get; } ChatLocation? Locate(string relaySessionId, DateTimeOffset openedAt); }`
  - `CodexChatLocator` — root `%USERPROFILE%\.codex\sessions`; considers rollout files modified since `openedAt` − 5 min; matches only a line recording the output of the `session_open` tool whose `sessionId` equals `relaySessionId`; thread ID from the filename suffix.
  - `ClaudeChatLocator` — root `%USERPROFILE%\.claude\projects`; same rule over `*.jsonl` tool results; session ID from the filename.
  - Both first check the adapter process's environment for a client-provided chat ID if the spike (Step 1) finds one, and never search for or read a token.
  - Zero or several matches → null; the chat is announced with `wakeable: false`.

- [ ] **Step 1: Spike (no commit):** in one Codex chat and one Claude Code chat, record the stdio MCP server's environment variables and the exact JSONL record shape of a `session_open` result. Write findings into `docs/discovery.md`. If a client exposes its chat ID in the environment, use it as the first strategy.
- [ ] **Step 2: Failing tests** (Review Focus 4): `Locator_matches_only_the_session_open_result` (fixture transcript where another chat's ID appears in a `chat_directory` result → still a single match), `Ambiguous_or_missing_match_is_announced_unwakeable`, `Session_handle_file_is_private_and_holds_no_machine_credential`, `Explicit_environment_keeps_existing_behaviour`.
- [ ] **Step 3–5:** run → FAIL, implement, run → PASS.
- [ ] **Step 6: Commit** `feat(adapter): hand connected chats to the node and map them to their threads`

### Task 8: Route-driven waking

**Files:**
- Modify: `src/PkRelay.Adapter/WakeWatcher.cs`, `Node/NodeRunner.cs`, `Program.cs`, `WakeConfiguration.cs`
- Create: `src/PkRelay.Adapter/Node/CodexLocator.cs`
- Test: `tests/PkRelay.Tests/WakeTests.cs`, `NodeTests.cs`

**Interfaces:**
- Refactor: `internal static Task<string> WakeWatcher.RunAsync(WakeRuntime runtime, Func<CancellationToken, Task<IReadOnlyList<WakeTarget>>> targets, bool exitWhenQueued, TimeSpan? timeout, CancellationToken ct, HttpMessageHandler? handler = null, TimeProvider? clock = null)` with `public sealed record WakeTarget(WakeRoute Route, WakeSession Session, IWakeDispatcher Dispatcher);` and `public sealed record WakeRuntime(AdapterOptions Options, string StatePath, string StopFile, string RelayUrl);`. The existing `RunAsync(WakeConfiguration, …)` and `AwaitInboxAsync` become thin wrappers that supply static targets; their tests stay unchanged.
- Targets are re-read every loop and on hints; a target that disappears stops being reconciled.
- `public static string? CodexLocator.Find()` — newest `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe`; null → Codex routes are reported unwakeable.
- Node: for each Codex route from the grain whose session handle file exists, a `CodexWakeDispatcher`; state in `<node>\wake.db`, stop file `<node>\stop`.
- `--await-inbox --chat <claudeSessionId> [--timeout-minutes N]`: route and handle come from node state; writes `waiters\<threadId>.pid` while running and deletes it on exit; the node reports `waiterRunning` when that PID is alive.

- [ ] **Step 1: Failing tests:** `Routes_added_while_running_are_reconciled_without_restart`, `Removed_route_is_no_longer_woken`, `Missing_codex_executable_marks_routes_unwakeable`, `Claude_waiter_presence_follows_its_process`.
- [ ] **Step 2–4:** run → FAIL, implement, run → PASS; existing `WakeTests` unchanged and passing.
- [ ] **Step 5: Commit** `feat(adapter): wake chats from discovery routes`

### Task 9: One-time setup

**Files:**
- Create: `src/PkRelay.Adapter/Setup/SetupCommand.cs`, `LogonStart.cs`, `ClientSettings.cs`
- Modify: `src/PkRelay.Adapter/Program.cs`
- Test: `tests/PkRelay.Tests/SetupTests.cs`

**Interfaces:**
- CLI: `--setup [--skip-client-config] [--data-dir <path>]`, run from the copied published folder.
- `LogonStart.Register(string dotnetPath, string adapterDll)` — `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `PkRelayNode` = `"<dotnet>" "<dll>" --node`. No administrator rights. Idempotent.
- `ClientSettings.RegisterCodex(string configTomlPath, string dotnetPath, string adapterDll)` — adds table `[mcp_servers.pkrelay]` with `command` and `args = ["<dll>"]` if absent.
- `ClientSettings.RegisterClaudeCode(string claudeJsonPath, string dotnetPath, string adapterDll)` — adds `mcpServers.pkrelay = { "type": "stdio", "command": "<dotnet>", "args": ["<dll>"] }` at the user level if absent.
- Both: back up to `<file>.pkrelay-backup-<yyyyMMddHHmmss>` before the first change; a missing file is created; an existing `pkrelay` entry is left untouched; an unparseable file is not modified and setup exits 5 naming the file.
- Setup then starts the node once in the background and prints the machine ID and the hub it found.

- [ ] **Step 1: Failing tests** (Review Focus 5): `Malformed_settings_file_is_left_unchanged` (bytes identical, exit code 5), `Setup_is_idempotent` (second run → no new backup, identical files), `Existing_settings_are_preserved` (other keys and comments in config.toml unchanged), `Skip_client_config_touches_no_client_files`. Tests use temp files and a test registry path abstraction, never the real user's files or registry.
- [ ] **Step 2–4:** run → FAIL, implement, run → PASS.
- [ ] **Step 5: Commit** `feat(adapter): add one-time setup for logon start and client registration`

### Task 10: Docs, local profile and end-to-end check

**Files:**
- Create: `docs/discovery.md`
- Modify: `README.md`, `docs/wake.md` (node supersedes hand-written configs; migration: stop the old watcher), `docs/architecture.md` (discovery grain section), `src/PkRelay.Host/Properties/launchSettings.json` (`Relay__Discovery__Enabled=true`, `Relay__Network__Enabled=true`)

- [ ] **Step 1:** Write the docs: joining a PC, the firewall rule, revocation, the open-trust warning, stopping the node, and migration from `--wake`/`--await-inbox` configs.
- [ ] **Step 2: End-to-end on this PC.** Stop the old Claude waiter and hand-written configs. Restart the hub from Visual Studio. Publish the adapter to a fresh `artifacts/adapter-node`. Run `--setup --skip-client-config --data-dir data/node-a`, then a second node with `--data-dir data/node-b` to simulate PC B. Verify against the spec's acceptance list: both machines appear in `chat_directory`; a Codex chat connected through node B's credentials is woken by a message from this Claude chat; a machine credential is refused `control_request`; stopping node B marks it unreachable within 90 s and restarting resumes the same machine ID. Record results in `docs/verification.md`, marking cross-PC network discovery as not yet exercised on a physical second PC.
- [ ] **Step 3:** Full suite green (all prior tests plus new ones).
- [ ] **Step 4: Commit** `docs: document cross-PC discovery and record verification`

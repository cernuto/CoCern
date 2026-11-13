# MCP-Bridge Ideas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build five of the six approved parts of the MCP-Bridge design: jittered retry pacing, a delivery field on failed writes, adapter build reporting and connect checks, rarely used tools on demand for Codex, and secret scrubbing in adapter logs.

**Architecture:** Every task is independent and ships alone, in the order below. All but tasks 3 and 4 change only `CoCern.Adapter` and `CoCern.Contracts`; the hub's authorization, its own MCP endpoint and its storage format are untouched. Part 3 of the design (folder removal stops running transfers) is not in this plan: it amends the file-transfer design, which is not approved yet, and is built with that work.

**Tech Stack:** C# on .NET 11, the `ModelContextProtocol` SDK, SignalR client, ASP.NET Core on the hub. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-13-mcp-bridge-ideas-design.md`

## Global Constraints

- Repository rules in `AGENTS.md` override this plan's defaults. No failing-first tests, no test or build run per task or per commit. Verification happens once, in task 8.
- Add only the two tests named in tasks 5 and 6 (a real security boundary). No other tests.
- For any C# lookup use Roslyn CodeLens (`search_symbols`, `get_method_source`, `find_references`, `find_callers`, `get_diagnostics`), not Grep or `dotnet build`. Grep only non-C# text. Line numbers below were read on 13 November 2026 at commit `0208f4b`; confirm each with `get_method_source` before editing.
- No new NuGet packages. Polly was considered and rejected in the spec.
- Work on a new branch from `main`, not on `codex/adapter-file-transfers`. The spec and this plan are untracked files there; carry both to the new branch in the first commit.
- Commit messages: no `Co-Authored-By` trailer, no hard-wrapped lines.
- Every fixed text below is exact copy. Do not reword it.

## Review Focus

These are for the source review, which is this repository's safety net; they get no tests beyond the two named.

1. `tool_call` naming a listed tool, `tool_call` itself, `tool_describe`, or a name differing only in case: refused with `unknown_tool`, and no hub request is made.
2. A chat whose profile becomes `chat` while Codex still lists `tool_call`: every target is refused locally, and the hub would refuse anyway.
3. A real pause (`control-blocked`) during or after a hub outage is never counted as unreachable, and the first answered read resets the interval to 15 seconds.
4. Nothing reports `not_applied` once request bytes may have left. A timeout, even one that struck during connect, is `uncertain`.
5. A PC that has never announced a build, or a hub offering no node release, shows adapter-current as unknown (`null`), never as out of date.

---

### Task 1: Retry pacing

**Files:**
- Create: `src/CoCern.Adapter/RetryPacing.cs`
- Modify: `src/CoCern.Adapter/WakeReconciler.cs:162-171` (`Permitted`), `src/CoCern.Adapter/WakeWatcher.cs:205-291`, `src/CoCern.Adapter/Node/NodeRunner.cs:328`, `src/CoCern.Adapter/SessionHints.cs:112`, `src/CoCern.Adapter/Node/LocalChatWake.cs:266-270`

**Interfaces:**
- Produces: `RetryPacing.Factor()`, `RetryPacing.Jitter(TimeSpan)`, `RetryPacing.Unreachable(int)`, and the status string `WakeReconciler.HubUnreachable`.

- [ ] **Step 1: Create `RetryPacing`**

```csharp
internal static class RetryPacing
{
    /// <summary>Uniform in [0.8, 1.2): the same ±20% LocalChatWake.HeldRetry uses.</summary>
    internal static double Factor();
    internal static TimeSpan Jitter(TimeSpan delay);      // delay * Factor()
    /// <summary>15 s, 30 s, then 60 s for 1, 2, and 3 or more reads in a row that did not reach the hub.</summary>
    internal static TimeSpan Unreachable(int misses);
}
```

Change `LocalChatWake.HeldRetry` to multiply by `RetryPacing.Factor()` instead of its inline expression.

- [ ] **Step 2: Tell an unreachable hub from a pause in `WakeReconciler`**

Add `internal const string HubUnreachable = "hub-unreachable";`. In `Permitted`, after the `Rejection(status)` line, when `status` is `{ Ok: false, Code: "hub_unavailable" or "hub_timeout" }`, set `sessionProblem = HubUnreachable` and return false. `ReconcileAsync` already returns `sessionProblem ?? "control-blocked"`. Run `find_references` on `WakeReconciler.ReconcileAsync` and Grep the literal `"control-blocked"` under `src` and `tests`; any consumer that treats `control-blocked` specially must treat `hub-unreachable` as a failed read, not as a pause.

- [ ] **Step 3: Pace routes in `WakeWatcher`**

Beside `lastStatus`, keep `Dictionary<string, double> pace` and `Dictionary<string, int> unreachable`, keyed like `lastStatus`. At both places that assign `lastStatus[routeKey]` (lines 214 and 230), set `pace[routeKey] = RetryPacing.Factor()` and set `unreachable[routeKey]` to its previous value plus one when the status is `WakeReconciler.HubUnreachable`, else zero. The factor is drawn once per read because `PollWait` is evaluated repeatedly while waiting, and a fresh draw each time would move the due time.

In `PollWait`: a hinted route is unchanged. When `runtime.Interval` is set, the result is unchanged (an explicit caller interval is honoured exactly). Otherwise the wait is `RetryPacing.Unreachable(count)` when the last status is `HubUnreachable` and `runtime.FallbackWait(false)` for every other status, multiplied by the route's stored factor. Leave lines 255-261 (no targets, rejected handles, never-read routes) as they are.

- [ ] **Step 4: Jitter the node and hint retries**

`NodeRunner.cs:328`: `await Task.Delay(RetryPacing.Jitter(retry), ct);`. `SessionHints.cs:112`: wait `RetryPacing.Jitter(delay)` when `ok` is false. The doubling in both stays as it is.

- [ ] **Step 5: Commit**

```bash
git add src/CoCern.Adapter docs/superpowers
git commit -m "Jitter adapter retries and back off while the hub is unreachable"
```

---

### Task 2: Delivery field on failed writes

**Files:**
- Modify: `src/CoCern.Contracts/ChatGuidance.cs`, `src/CoCern.Adapter/RelayForwarder.cs:140-194` (`SendCoreAsync`), `src/CoCern.Adapter/CoordinationTools.cs:313`, `docs/reference.md` (Calls, Retries paragraph)

**Interfaces:**
- Produces: `ChatGuidance.DeliveryField`, `ChatGuidance.NotApplied`, `ChatGuidance.Uncertain`, `ChatGuidance.Delivery(string delivery, bool native)`.

- [ ] **Step 1: Add the fixed texts to `ChatGuidance`**

```csharp
public const string DeliveryField = "delivery";
public const string NotApplied = "not_applied";
public const string Uncertain = "uncertain";
public static string Delivery(string delivery, bool native);
```

`Delivery` returns exactly:
- `not_applied`: `The hub did not apply this.`
- `uncertain`, classic: `The hub may have applied this. Retry in this session with the same requestId and arguments.`
- `uncertain`, native: `The hub may have applied this. Read the current state before repeating it.`

- [ ] **Step 2: Mark adapter-originated failures in `RelayForwarder`**

Add `private AdapterReply WithDelivery(string tool, AdapterReply reply, string delivery)`. It returns `reply` unchanged unless `RelayToolCatalog.TryGet(tool, out var entry) && entry.IsMutation`. Otherwise it adds `delivery` to the reply's data object (creating one when data is null) and adds `hint` (`ChatGuidance.HintField`) from `ChatGuidance.Delivery(delivery, nativeSurface)` unless the data already has `hint` or `guidance`.

Wrap each adapter-originated return in `SendCoreAsync`:

| Return | Delivery |
| --- | --- |
| `invalid_arguments`, the local `request_too_large` (lines 142-144) | `not_applied` |
| `redirect_rejected` | `not_applied` |
| `unauthorized`, and `forbidden`, `request_too_large`, `rate_limited` from `TransportCode` | `not_applied` |
| `hub_error` from `TransportCode` | `uncertain` |
| The reader's own `response_too_large` | `uncertain` |
| `hub_timeout` | `uncertain` |
| `hub_identity_changed` | `not_applied` |
| `hub_unavailable` from `HttpRequestException` when `headersRead` is false and `HttpRequestError` is `ConnectionError`, `NameResolutionError`, `SecureConnectionError` or `ProxyTunnelError` | `not_applied` |
| Every other `hub_unavailable` (other `HttpRequestException`, any `IOException`) | `uncertain` |
| `invalid_hub_reply` | `uncertain` |

A hub envelope with one of its own failure codes passes through with no field. `TransportCode` has several call sites; give it the status once and derive the delivery from the code so the table is applied in one place.

- [ ] **Step 3: `implicit_request_capacity`**

At `CoordinationTools.cs:313`, add `delivery = "not_applied"` to that reply's data beside its existing `guidance`. No hint is added, because `guidance` is present.

- [ ] **Step 4: Confirm the fields reach the model, and document**

Read `McpReadResults.FromAdapter` with `get_method_source` and confirm a failed reply's data is rendered whole. In `docs/reference.md`, add to the Retries paragraph one sentence naming `delivery`, its two values and that only writes carry it.

- [ ] **Step 5: Commit**

```bash
git add src docs/reference.md
git commit -m "Say whether a failed write reached the hub"
```

---

### Task 3: Report each PC's adapter build

**Files:**
- Modify: `src/CoCern.Storage/SqliteStore.Observation.cs:337` and the machine row type it constructs, `src/CoCern.Contracts/AdminToolContracts.cs:20-54`, `src/CoCern.Host/AdminToolsService.cs:48-70`, the dashboard machine list (Grep `.razor` files for the machine row type), `docs/reference.md:53,57`

**Interfaces:**
- Consumes: `NodeBuildObservation` (`src/CoCern.Contracts/NodeRelease.cs:8`), stored under `EnrolledMachine.NodeBuildKey`; the offered release as `DiscoveryEndpoints.cs:272-275` obtains it (`connect.NodeAdapter()`, then `cache.ReleaseAsync(adapter.Folder, …)`, giving `NodeRelease.AdapterDllSha256` and `Package.ProductVersion`).
- Produces: `HubDiagnosisPc.AdapterVersion` (`string?`), `HubDiagnosisPc.AdapterCurrent` (`bool?`), `HubDiagnosis.OfferedAdapterVersion` (`string?`).

- [ ] **Step 1: Carry the stored build into the machine observation**

Where the machine row is built, read the object under `EnrolledMachine.NodeBuildKey` and set two new nullable properties on the row: the product version and the adapter DLL hash. Leave them null when the machine has no stored build.

- [ ] **Step 2: Change the `hub_diagnose` contract**

Remove `HubDiagnosis.VersionNote`. Add `OfferedAdapterVersion` to `HubDiagnosis` and `AdapterVersion`, `AdapterCurrent` to `HubDiagnosisPc`. The reply carries no hash, content ID or process ID.

- [ ] **Step 3: Compare in `DiagnoseAsync`**

Obtain the offered node release once, the way the announce endpoint does, catching the same exceptions and treating any failure as no release. `AdapterCurrent` is `null` when the PC reported no build or there is no offered release; otherwise it is whether the PC's DLL hash equals the release's, ignoring case.

- [ ] **Step 4: Dashboard and documentation**

Show version and a current, out of date or unknown mark in the dashboard's machine list. In `docs/reference.md`, replace the sentence "PCs don't report an adapter version to the hub yet, so it can't say whether one is current." with what `hub_diagnose` now reports, and update its table row.

- [ ] **Step 5: Commit**

```bash
git add src docs/reference.md
git commit -m "Report each PC's adapter build in hub_diagnose"
```

---

### Task 4: Checks in the connect result

**Files:**
- Create: `src/CoCern.Host/Dashboard/ConnectChecks.cs`
- Modify: `src/CoCern.Contracts/DashboardContracts.cs:370-374`, `src/CoCern.Host/Dashboard/DashboardConnect.cs:28-29`, the Connect page (Grep `.razor` files for `DashboardRepairCheck` to find the existing row markup), `docs/reference.md:57`

**Interfaces:**
- Consumes: `DashboardConnect.RepairAsync(bool check, CancellationToken)` returning `DashboardRepairStatus.Checks`.
- Produces: `DashboardConnectStatus.Checks` (`DashboardRepairCheck[]`, default empty); `ConnectChecks.For(string client, IReadOnlyList<DashboardRepairCheck> rows)`.

- [ ] **Step 1: Add `Checks` to `DashboardConnectStatus`**

- [ ] **Step 2: Create `ConnectChecks.For`**

It keeps, in their original order, the rows whose `Id` belongs to the client:
- every client: `adapter`, `enrollment`, `hub`, `hub-transport`, `hub-access`, `startup`
- `claude-code` also: `claude`, `claude-wake`
- `codex` also: `codex`, `codex-native-helper`, `native-activation-preview`, `workflow-skill`, `native-setup`, `native-startup`, `session`, `wake`

For `codex`, a `native-startup`, `session` or `wake` row whose state is not `passed` is returned with state `warning` and its detail prefixed with exactly `This can't pass until the app starts a chat. `.

- [ ] **Step 3: Attach the rows in `DashboardConnect.ConnectAsync`**

After the connect run, when its `Unavailable` is null, call `RepairAsync(check: true, …)` and return the status with `Checks = ConnectChecks.For(client, repair.Checks)`. When the repair check is itself unavailable, `Checks` stays empty and the connect result is otherwise unchanged. Both callers (`AdminToolsService` through its `runConnect` delegate, and `DashboardEndpoints.cs:140`) get the rows with no further change.

- [ ] **Step 4: Page and documentation**

Render the rows under the connected client on the Connect page with the existing repair-check row markup. In `docs/reference.md`, add to the `connect_app` paragraph that the reply carries `checks`.

- [ ] **Step 5: Commit**

```bash
git add src docs/reference.md
git commit -m "Return installation checks from connect_app and Connect"
```

---

### Task 5: Rarely used tools on demand

**Files:**
- Create: `src/CoCern.Adapter/OnDemandTools.cs`
- Modify: `src/CoCern.Contracts/RelayToolCatalog.cs:58-118`, `src/CoCern.Adapter/AdapterOptions.cs:34,73`, `src/CoCern.Adapter/CoordinationTools.cs:22-34,65-74,201-232`, `src/CoCern.Adapter/ToolSurface.cs:35,53-56,67-85,87-150`, `src/CoCern.Adapter/Program.cs:236-237`
- Test: the existing test file covering `ToolSurface` or `RelayForwarder.ForwardAsync` (select with `find_tests_for_symbol`)

**Interfaces:**
- Produces: `RelayTool.OnDemand`; `AdapterOptions.OnDemand`; `OnDemandTools.Describe`, `OnDemandTools.Call`, `OnDemandTools.TryTarget`; `CoordinationTools.ListedSchema(RelayTool, bool native)`.

- [ ] **Step 1: Flag the catalog**

Add a last positional parameter `bool OnDemand = false` to the `RelayTool` record and set it true on exactly these 18 entries in `RelayToolCatalog.All`: `doc_search`, `doc_section`, `doc_sync`, `session_context_put`, `session_context_get`, `session_context_search`, `worker_list`, `record_decision`, `standing_propose`, `standing_put`, `order_put`, `order_remove`, `session_tool_settings_set`, `control_request`, `analytics_query`, `hub_diagnose`, `add_pc`, `connect_app`. `event_wait` and `peers` stay false.

- [ ] **Step 2: Add the option**

The spec calls this a start option. The adapter takes its options from environment variables (`AdapterOptions.FromEnvironment`), so it is one: `AdapterOptions.OnDemand` (`bool`, init), set from `COCERN_ON_DEMAND_TOOLS=true`, parsed beside `EventWaits`. Give `CoordinationTools.Create` and `CreateNative` a last parameter `bool onDemand = false` and pass `options.OnDemand` from `Program.cs`.

- [ ] **Step 3: Create `OnDemandTools`**

```csharp
internal static class OnDemandTools
{
    internal const string Describe = "tool_describe";
    internal const string Call = "tool_call";
    /// <summary>Reads {"name": string, "arguments": object}; false for any other shape.</summary>
    internal static bool TryTarget(JsonElement input, out string name, out JsonElement arguments);
    internal static McpServerTool DescribeTool(Func<IReadOnlyList<RelayTool>> available, bool native);
    internal static McpServerTool CallTool(Func<IReadOnlyList<RelayTool>> available, Func<string, JsonElement, CancellationToken, Task<AdapterReply>> call);
}
```

`available` returns the on-demand tools the current profile and ceiling allow. Descriptions, exactly:
- `tool_describe`: `Rarely used CoCern tools are not listed: docs, retained context, standing, orders, setup, analytics and diagnostics. Call with no name to list the ones this chat may use, or with name for one tool's arguments.`
- `tool_call`: `Run a tool found with tool_describe: name and its arguments. Listed tools are called directly, not through this.`

`tool_describe` with no `name` answers `{ "tools": [{ "name", "description" }] }`; with a `name` in `available` it answers `{ "name", "description", "inputSchema" }`, where the schema is `CoordinationTools.ListedSchema(tool, native)`. `tool_call` requires `name` and `arguments`. Both refuse a name not in `available`, compared ordinally, with `unknown_tool` and the `ChatGuidance.ToolOff` hint, without calling anything. Otherwise `tool_call` returns `call(name, arguments, ct)` rendered with `McpReadResults.FromAdapter(name, reply)`.

Extract the schema building in the `ForwardedTool` constructor (lines 219-231) into `internal static JsonElement ListedSchema(RelayTool tool, bool native)` and use it from both places, so a described tool shows the schema it would have had when listed.

- [ ] **Step 4: List and allow**

In both `Build` functions, when `onDemand` is true: compute the allowed tools as now, list only those with `OnDemand` false, and when at least one allowed tool has `OnDemand` true append `DescribeTool` and `CallTool`. The classic `call` delegate is `calls.CallAsync`; the native one is the same lambda the native `ForwardedTool` uses (extract it so both share it).

`ToolSurface` must allow an on-demand target that is not in the collection. Change `Configure` to also take `Func<string?, bool, IReadOnlySet<string>> onDemandNames`. Keep a set of allowed on-demand names, recomputed under the gate wherever `Compose` runs (`Observe`, `Restore`) as the union over the same choices, and make `Allows(name)` answer true for a name in the collection or in that set. With `onDemand` false the set is always empty and behaviour is unchanged.

`ConnectionTraceStore.Valid` admits only known tool names into diagnostics. Read it with `get_method_source` and begin the `tool_call` trace under the target's name.

- [ ] **Step 5: Write the boundary test**

In the selected existing test file, one test: with a `worker` profile selected and `onDemand` true, `tool_call` with `name: "control_request"` returns `unknown_tool` and the forwarder's HTTP handler sees no request; the same call with `name: "doc_search"` reaches the handler as `api/tools/doc_search`.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "List rarely used tools on demand behind tool_describe and tool_call"
```

---

### Task 6: Native stamps and Codex setup

**Files:**
- Modify: `src/CoCern.Adapter/Node/NativeJoinHook.cs:176-189`, the Codex launch environment written in `src/CoCern.Adapter/Setup/ClientSettings.cs`, `src/CoCern.Adapter/Setup/CodexNativeSetup.cs` and `src/CoCern.Adapter/Setup/ConnectCommand.cs:203-244`, `docs/session-tool-settings.md`, `docs/reference.md` (Tools)
- Test: the existing `NativeJoinHook` test file (select with `find_tests_for_symbol` on `NativeJoinHook.InvokeAsync`)

**Interfaces:**
- Consumes: `OnDemandTools.Describe`, `OnDemandTools.Call`, `OnDemandTools.TryTarget`, `RelayTool.OnDemand`; `NativeJoinHook.StampAsync(join, capture, tool, invocation, input, ct)` and `NativeJoinHook.InvokeAsync(join, provider, tool, input, ct)`, both unchanged.

- [ ] **Step 1: Stamp the target in the `PreToolUse` hook**

In the `PreToolUse` case, before `ValidInvocation` is applied to the tool name:
- `tool_describe`: mark the call recognized and return 0 with no output, so it passes unstamped.
- `tool_call`: read the target with `OnDemandTools.TryTarget`; require `RelayToolCatalog.TryGet(target, out var entry) && entry.OnDemand`, else throw the existing `ArgumentException("Invalid native invocation.")`. Call `StampAsync` with the target name and the inner arguments, and write as the updated input `{ "name": target, "arguments": <the stamped arguments> }`. `ValidInvocation(target, invocation)` still runs inside `StampAsync`.

The stamp therefore sits inside `arguments._cocernNative` and its MAC covers the target name and the inner arguments. The adapter's native `tool_call` delegate from task 5 passes exactly that name and those arguments to `registration.InvokeAsync`, which verifies them with no change. Read `NativeStartupEvidence.RecordStampedAsync` and pass it the stamped inner arguments, the shape it receives today.

- [ ] **Step 2: Write the boundary test**

One test in the selected file: a stamp produced by `StampAsync` for target `doc_search` and arguments `{"query":"a"}` is accepted by `InvokeAsync` for that target and those arguments, and refused (reply not `Ok`, nothing forwarded) when presented with target `doc_section`, and when presented with arguments `{"query":"b"}`.

- [ ] **Step 3: Turn it on for Codex**

Add `COCERN_ON_DEMAND_TOOLS=true` to the environment of the Codex launch entry in both forms (standard and native), where `COCERN_REPOSITORY` and `COCERN_ROLE` are written for Codex. Claude Code and Claude Desktop entries are not changed. An existing Codex install will then report its `codex` check as `needs_repair` until Repair rewrites the entry; that is the intended rollout.

- [ ] **Step 4: Document**

In `docs/session-tool-settings.md`, add a paragraph: with on-demand tools, a Codex chat lists the same tools for `worker`, `diagnostics` and `admin`, and reaches the rest through `tool_describe` and `tool_call`; the hub still checks every call. In `docs/reference.md` Tools, add the two adapter tools and the 18 names they cover.

- [ ] **Step 5: Commit**

```bash
git add src tests docs
git commit -m "Stamp on-demand tool calls by target and enable them for Codex"
```

---

### Task 7: Scrub known secrets from adapter and node logs

**Files:**
- Create: `src/CoCern.Adapter/SecretScrub.cs`
- Modify: `src/CoCern.Adapter/Program.cs:276`, `src/CoCern.Adapter/Node/NodeRunner.cs:94,239`, `src/CoCern.Adapter/Setup/SetupCommand.cs:79,251,283,326`, and the places secrets are first held (below)

**Interfaces:**
- Produces:

```csharp
internal static class SecretScrub
{
    internal const string Marker = "[secret removed]";
    /// <summary>Ignores null and anything shorter than 8 characters. Thread-safe. Holds at most 4,096 values.</summary>
    internal static void Add(string? secret);
    /// <summary>Replaces every held value with Marker, longest first, by ordinal comparison.</summary>
    internal static string Clean(string text);
}
```

- [ ] **Step 1: Create `SecretScrub`**

- [ ] **Step 2: Register secrets where they are first held**

`AdapterOptions.Token` in its constructor; `NodeTransport.JoinCode` when set; the machine credential when `NodeState` reads it; `NativeJoinBinding.Secret` when created or loaded; each session token `ChatCalls` learns.

- [ ] **Step 3: Clean at the raw-exception sites**

At each listed line, pass the exception message through `SecretScrub.Clean` before writing it. The separate `CoCern.Setup` project holds none of these secrets and is not changed. The hub is not changed, and the join code's single log line at generation (`DiscoveryEndpoints.cs:176`) stays.

- [ ] **Step 4: Commit**

```bash
git add src/CoCern.Adapter
git commit -m "Scrub known secrets from adapter and node log lines"
```

---

### Task 8: Milestone verification

- [ ] **Step 1: Diagnostics.** Run Roslyn `get_diagnostics` for the solution. Expected: no errors, no new warnings.
- [ ] **Step 2: Select tests.** For each changed symbol (`WakeReconciler.ReconcileAsync`, `WakeRuntime.FallbackWait`, `RelayForwarder.SendCoreAsync`, `ToolSurface.Allows`, `ToolSurface.Observe`, `CoordinationTools.Create`, `CoordinationTools.CreateNative`, `NativeJoinHook.RunAsync` or whichever method holds the `PreToolUse` case, `AdminToolsService.DiagnoseAsync`, `DashboardConnect.ConnectAsync`, `RelayToolCatalog.All`), ask `find_tests_for_symbol`. Run exactly those tests plus the two new ones and the KEEP smoke set in `docs/testing.md`. Name the changed symbols when reporting the run.
- [ ] **Step 3: Build once.** Build the solution to a folder no running hub or adapter uses. Expected: success.
- [ ] **Step 4: Measure.** With the built adapter started with `COCERN_ON_DEMAND_TOOLS=true`, list its tools for a worker and for an administrator and confirm the two lists are identical and neither contains any of the 18 names. Report the token count of `tool_describe` plus `tool_call` against the spec's 300-token estimate.

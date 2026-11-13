# Six improvements taken from the MCP-Bridge review

## Intent

Paul asked for a review of [MCP-Bridge](https://github.com/Darek3399/MCP-Bridge), a Windows tray app that puts local MCP servers behind one endpoint, to find ideas for CoCern. He then asked for the tool-list cost to be measured and for the other ideas to be included in one design. Everything below that is not in that sentence is the designer's proposal, not a decision by Paul. Nothing is implemented, and product work waits for approval of this document, part by part.

Only ideas are taken. MCP-Bridge's licence is custom and no code is copied.

The six parts are independent. Each can be approved, built and shipped alone, and the order at the end is a recommendation.

## Goals, non-goals and what would make this a failure

Goals:

- A Codex chat's tool list stops changing when its profile moves between `worker`, `diagnostics` and `admin`, and gets smaller.
- A hub restart no longer makes every adapter on a PC retry at the same instant.
- A failed write tells the chat whether it reached the hub.
- Setup says which step failed, and the hub says which adapter build each PC runs.
- Removing a file-transfer folder on a PC stops transfers already running.

Non-goals:

- No change to who may call what. The hub's per-call check in `CallGuard.ProfileRefusal` stays the only authority.
- No change to the hub's own MCP endpoint or `GET /api/tools`, which list full schemas for direct callers.
- No public tunnel, server presets, clipboard import or anything else specific to managing third-party servers.
- No trimming of individual tool schemas. `task_update`'s listed schema is 791 tokens and deserves its own look, but that came from the measurement, not from this review.

It would be a failure if a chat could reach a tool through the new call-by-name path that its profile hides, if everyday tools became slower to call, or if a retry change made a chat miss a wake it gets today.

## Measurements behind part 1

Taken on 13 November 2026 from the deployed hub (`artifacts/deployment-efficiency-20261112/host`). The hub's `get_status` reports 4,970 tool-definition tokens for a worker and 6,005 for an administrator (o200k estimates of description plus forwarding schema). A per-tool count from the same assemblies adds up to exactly those figures. Call counts are model-facing calls in `activity_log` from 9 to 13 November, all repositories.

| Group | Tools | Listed tokens | Calls |
| --- | --- | --- | --- |
| Everyday: the `chat` profile | 11, plus `peers` for native chats | 1,589 | 12,902 (79%) |
| Task workflow: `task_update`, `task_get`, `task_claim`, `action_begin`, `context_put`, `context_get`, `search`, `attention_respond`, `claim_acquire` | 9 | 1,906 | 3,331 (20%) |
| Rarely used: `doc_search`, `doc_section`, `session_context_put`, `session_context_get`, `session_context_search`, `worker_list`, `record_decision`, `standing_propose`, and the ten administrator-only tools | 18 | 2,510 | 107 (0.7%) |

A worker's share of the rarely used group is 8 tools and 1,475 tokens. All ten administrator-only tools are in it, each with 11 calls or fewer in the four days.

Limits: four days of data; calls with no recorded origin are left out, which undercounts `session_open` and `get_status`. The development launcher also appends about 444 tokens of server guidance to `session_open`'s description (`DevelopmentLauncher.AdvertiseGuidance`); that is a development-adapter cost and is not part of this design.

## Part 1: rarely used tools on demand

### How it works today

The adapter lists whatever `CoordinationTools.Build(profile, ceiling)` puts in its tool collection: `RelayToolCatalog.ForRole`, then the static chat-only and diagnostics filters, then `SessionToolProfiles.Allows`. When the hub selects another profile, `ToolSurface.Observe` rebuilds the collection. Codex reads the list only when it starts a backend, so a profile change does not reach a running Codex chat, and a shared native backend lists the union of its sessions' tools.

Every call runs through `ForwardedTool.InvokeAsync`, then `ChatCalls.CallAsync` (classic) or, for a native chat, `NativeRegistrationGate.InvokeAsync`, `NativeJoinHook.InvokeAsync` and `NativeJoin.CallAsync`. These apply the adapter's own handling by tool name: `waitSeconds`, `ifNoneMatch`, and the session fill-in. `RelayForwarder.ForwardAsync` then refuses with `unknown_tool` any name that is not in the current collection once a profile is selected (`ToolSurface.Allows`).

A native call also carries a stamp. The chat's `PreToolUse` hook stamps each `mcp__cocern__` call, accepting only catalog tool names (`ValidInvocation`), and `NativeJoinHook.InvokeAsync` refuses a call without a stamp whose MAC covers the tool name and its arguments. A tool that is not in the catalog is therefore blocked on a native chat today.

### Approaches considered

1. Everything behind two tools, as MCP-Bridge does. Rejected: 99% of calls go to the everyday and task-workflow tools, and each would need an extra step.
2. The rarely used group on demand, for every client.
3. The rarely used group on demand, only where the adapter is started for it, which Codex setup does. Chosen, as the designer's call. Claude Code already defers MCP tools and honours list changes, and its tool search finds only listed tools, so hiding tools there would lose discoverability for no gain.

### Design

Add a catalog flag, proposed name `OnDemand`, set on the 18 rarely used tools. It is data in `RelayToolCatalog`, so the split has one home. `event_wait` is opt-in and outside the three groups; it stays listed as it is today when enabled.

Add an adapter start option, proposed name `--on-demand-tools`, written by Codex setup for both standard and native launches. With it, `Build` leaves `OnDemand` tools out of the collection and adds two adapter-local tools:

- `tool_describe`: with no `name`, lists the on-demand tools this chat may call, one line each. With `name`, returns that tool's description and the same forwarding schema it would have had when listed.
- `tool_call`: takes `name` and `arguments` and runs the named tool.

Both descriptions are fixed text that names the kinds of tool behind them (docs, retained context, standing, orders, setup, analytics, diagnostics) and never the profile's exact set. The listed tools are therefore identical for `worker`, `diagnostics` and `admin`; only `chat`, which has no on-demand tools and so gets neither new tool, differs.

`tool_call` resolves in the adapter and then enters the same classic or native call path with the target's name, so all name-keyed handling applies unchanged and the hub sees, authorizes, logs and counts the real tool. The hub needs no change. A `tool_call` naming a listed tool, itself or `tool_describe` is refused, so there is one way to call each tool.

On a native chat the stamp binds the target, never the wrapper. This is the designer's call and the part of this design that most needs a security review. The `PreToolUse` hook, on seeing `tool_call`, checks that `name` is an on-demand catalog tool and stamps the target name with the inner `arguments`; the adapter then verifies that stamp against the same target and arguments it is about to run. A stamp made for one target cannot be used for another, and the replay fence stays on the real call. `tool_describe` reaches no hub and changes nothing, so the hook lets it through unstamped and the adapter answers it locally from the catalog and the current profile.

The adapter's allow check changes in one place: `ToolSurface` keeps the set of on-demand names the current profile and ceiling allow, beside the collection, and `Allows` answers from both. A hidden tool still gets `unknown_tool` and its existing hint. The remembered-profile file keeps working as now; it matters less, because the list no longer depends on which of the three profiles is selected.

Cost to the chat: about 300 tokens for the two tools (estimated, not measured), against 2,510 saved for an administrator and 1,475 for a worker. The hub's `toolTokens` counts catalog tools and keeps reporting the full lists. A first use of a rarely used tool costs one extra call.

One trade-off to accept knowingly: a client permission rule that always allows `tool_call` covers every on-demand tool, administrator ones included. The hub's own requirements (administrator credential, `humanWords`, `expectedVersion`) are unaffected.

## Part 2: connection check results

### How it works today

More exists than the review assumed. The adapter's `--repair --check --json` returns `DashboardRepairCheck` rows (`adapter`, `enrollment`, `hub`, `hub-transport`, `hub-access`, `codex`, `claude`, `claude-wake`, `startup`, `workflow-skill`, `native-setup`, `native-startup`, `session`, `wake`), each `passed`, `warning`, `needs_repair` or `unavailable`. Codex connect also reports readiness flags after a native launch.

Three gaps remain. `connect_app` for Claude Code and Claude Desktop writes configuration and verifies nothing afterwards, and a failure is one sentence in `unavailable`. The node already sends `nodeBuild` (version, content ID, hash) with every announce and the hub stores it, but `hub_diagnose` and the dashboard's machine list leave it out, and `hub_diagnose` still returns a fixed note saying PCs do not report a version. The reference says the same.

### Design

- `connect_app` and the dashboard's Connect return, as `checks`, the rows the full `--repair --check` already produces for the client they just set up. Connect's own shorter repair path gives a Claude app only its adapter, connection and hub rows, so this means using the full check, not that path. A check that cannot pass until the app restarts (`session`, `wake`) is reported `warning` with a fixed line saying so. No new result type and no new checks: a row that does not exist for a client is simply absent.
- `hub_diagnose` and the dashboard machine list report each PC's stored build and whether it matches the package the hub gives nodes for self-update (`NodeAdapter()`), which can differ from the one served at `/join/adapter.zip`. Remove the fixed version note and correct `docs/reference.md`.

Not included: a test wake sent by the hub. Reachability stays announced state; a real probe would cost a model turn in the target chat.

## Part 3: folder removal stops running transfers

This amends [Files between PCs through CoCern](2026-11-12-adapter-file-transfers-design.md), which is another chat's design awaiting Paul's approval. It is written here as a proposal and that document is not edited.

That design already says a session cannot enable transfer access or widen the allowed roots, and that local access is rechecked before a received file is published. It does not say when the local configuration is read. Proposed addition: the adapter reads the allowed read and write roots from the local configuration at each admission and each recheck, never once at start. A running upload rechecks its read root at every chunk; a running receive rechecks its write root before each range request and again before publishing. A root that has been removed ends the transfer as `failed` with the existing local-path-denied reason, and the receiver removes its own partial file. No adapter restart is needed to revoke.

## Part 4: retry pacing

### How it works today

`WakeRuntime.FallbackWait` gives every route that is not quiet and listening a fixed 15 seconds, with no jitter and no growth. That covers more than failures: a busy or paused chat, a rejected handle awaiting its replacement, and polling while the hint connection is down all use it, and an unreachable hub currently reads as `control-blocked`, the same as a real pause. Routes paced in the same pass come due together. The node doubles from 1 to 60 seconds with no jitter (`NodeRunner.NextRetry`), and `SessionHints` doubles from 1 to 30 seconds with no jitter. Jitter exists in only two places, `LocalChatWake.HeldRetry` (±20%) and `NativeRegistrationGate.BusyRetry`. This PC runs about twenty adapter processes, and the hub restarts on every Visual Studio build, so they all retry on the same beat.

### Design

- Add ±20% jitter, the same rule `HeldRetry` uses, to the wake watcher's 15-second pace in every case, to the node's retry and to `SessionHints`' retry. Jitter alone removes the shared beat and slows nothing.
- Let the interval grow only for a route whose status read failed to reach the hub (the adapter's `hub_unavailable` and `hub_timeout`): 15, 30, then 60 seconds, reset by the first read that gets an answer. A busy or paused chat, a rejected handle and a route polling for hints keep 15 seconds, so no wake that takes 15 seconds today takes longer. This needs the reconciler to tell an unreachable hub from a real pause; if that proves invasive, ship the jitter alone.

Polly was considered, at Paul's question, and is not used; this is the designer's call. Polly wraps one operation and waits between its attempts. The wake watcher is a scheduler instead: one loop computes a due time per route, sleeps until the earliest, and is woken early by hints, so there is no single operation to wrap, and the node's loop is a permanent cycle, not a retry that ends. What Polly would contribute here is the delay arithmetic, which `HeldRetry` already has in two lines. The adapter has three package references today (`ModelContextProtocol`, `Microsoft.AspNetCore.SignalR.Client`, `Microsoft.Data.Sqlite`) and ships to every PC, so a fourth should buy more than that. The hint channel's reconnect steps can take jitter through SignalR's own retry-policy hook. Polly's HTTP handler must stay off `RelayForwarder` in particular: by default it retries every method, POST included, and the adapter deliberately never repeats a write on its own.

Hints and reconnection still interrupt the wait as they do now, and explicit caller intervals are still honoured. The 60-second ceiling is chosen so that a wake route recovers within about a minute of the hub returning, which is what the hinted interval already allows.

## Part 5: say whether a failed write reached the hub

### How it works today

When the adapter cannot complete a call, the chat gets `hub_unavailable` with "Can't reach the hub…", or `hub_timeout` or `invalid_hub_reply` with no data. A refused connection and a timeout after the request was sent look the same. The chat cannot tell a write that never left from one the hub may have applied.

### Design

On adapter-originated failures of a mutation, add one field, proposed name `delivery`, and one fixed hint:

- `not_applied`: the write did not run. Either nothing was sent (local validation, the adapter's own size limit, `implicit_request_capacity`, and connection, name-resolution or certificate failures), or the hub's HTTP layer answered with a refusal before running it (`unauthorized`, `forbidden`, `rate_limited`, HTTP 413, `redirect_rejected`). Hint: the hub did not apply this.
- `uncertain`: sending began and no usable answer came back (`hub_timeout`, `invalid_hub_reply`, `response_too_large`, `hub_error`). Hint for a classic chat: the hub may have applied this; retry in this session with the same `requestId` and arguments. Hint for a native chat, which supplies no `requestId`: the hub may have applied this; read the current state before repeating it.

The classic hint is the existing retry contract, stated at the moment it matters. Reads carry no field. Hub-originated error codes are out of scope: the code has no record of which refusals precede any state change, and building one is a separate piece of work.

## Part 6: scrub known secrets from adapter and node logs

### How it works today

There is no redaction code. Protection is by allow-list: diagnostics accept only fixed tool, stage and code values, and node output prints only plain identifiers. Raw exception messages are logged in a handful of adapter and node places (`Program`, `NodeRunner`, `SetupCommand`, `Setup`). No path was found that puts a token, header or command line into a log, so this part is insurance, not a fix for a known leak.

### Design

A small scrubber in the adapter and node that holds the literal secrets the process knows (client token, join code, machine credential, native binding secret, session tokens as they are learned) and replaces any occurrence with a fixed marker. Apply it at the raw-exception log sites. The hub is out of scope, and the join code's single deliberate log line at generation stays.

This is the weakest of the six. Dropping it loses little.

## Recommended order

1. Part 4, retry pacing: small, and the problem is visible on this PC today.
2. Part 5, delivery field: small, and it removes a real ambiguity.
3. Part 2, the `hub_diagnose` build report and the documentation correction, then the `connect_app` checks.
4. Part 1, on-demand tools: the largest, adapter-only.
5. Part 3 rides with the file-transfer work if that design is approved.
6. Part 6 last, or not at all.

## Left to the builder

Final names for the flag, the option, the two tools and the `delivery` field; the exact fixed texts; how the adapter tells "nothing sent" from "sent" for each transport failure; and where the on-demand set lives inside `ToolSurface`. Use Roslyn CodeLens to select the exact methods.

Tests, following the repository rules: two for the security boundary in part 1. A tool hidden by the profile is refused through `tool_call` exactly as it is when called directly, and on a native chat a stamp made for one target and arguments is refused for any other. Part 3's revocation belongs with the file-transfer tests. The other parts need none. Verification waits for the implementation milestone.

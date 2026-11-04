# Manager Admin Tools Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. Plan only: nothing here is built yet.

**Goal:** Close the `admin-tools` criterion on AUTO-CONNECT-BUILD-20261103: "Manager diagnostics/setup/admin work reuses existing services and preserves scoped authority." An administrator chat can diagnose the hub, produce the add-a-PC command, connect an app on the hub's PC, and clear its alert backlog by tool, without the dashboard.

**Spec:** `docs/superpowers/specs/2026-11-03-auto-connect-design.md`, part 9 ("Missing tools", "Alert backlog", "Safeguards"), part 11 ("What it can do", "What it can't do"), acceptance gate 8, order item 8.

**Architecture:** Three new administrator-only tools in the existing catalog, routed by `ToolRouter` to one new port in `CoCern.Core`, with a Host implementation that calls the services the dashboard already uses (`DashboardAddPc`, `DashboardConnect`, `DashboardCollector`). One additive field on `attention_respond`. No new grain, schedule, storage table or transport.

**Baseline read at main 19cf699.** `add_pc`, `connect_app` and `hub_diagnose` exist on no branch. `RelayToolCatalog.cs`, `ToolSchemas.cs`, `ToolRouter.cs`, `SessionAttention.cs` and `DashboardConnect.cs` differ from main on none of `codex/auto-connect-features`, `codex/native-hooks` or `codex/node-update-handoff`; `RelayHostBuilderExtensions.cs` differs on the first and third, so expect a small merge there. Re-confirm all of this before editing.

## Scope limits

- **No new authority.** The tools are `AdministratorOnly: true`, gated by the caller's existing `IsAdministrator`, exactly like `control_request` and `analytics_query`. Being the recorded manager grants nothing here: `ManagerSelection` stays inert, and mapping the manager role to tools belongs to the `capabilities` and `manager` criteria.
- **Not in this plan:** the durable every-action audit, `record_decision` and approvals (`decisions` criterion), node self-update (`updates`), per-session settings replacing `COCERN_ROLE` (part 10), the repeat-wake fix, assign and reassign work, and "tune how chats work". Gate 8 is not fully met by this plan alone: it also needs `decisions` and `updates`.
- **Secrets.** No reply contains a session token, client token, certificate private key or token file contents. The join code is the one exception, and only under Decision 1.
- **Client settings.** `connect_app` must never be able to grant administrator access (Decision 2).
- **Standing rules:** Roslyn CodeLens for all C#; tests with `dotnet test --artifacts-path <scratch>` or a worktree build, never the live hub's bin folders; no live hub or client settings changes during implementation; commit only these files; no AI attribution; soft-wrapped text.

## Decisions needed before Task 2 and Task 3

**Decision 1: `add_pc` puts a long-lived secret into a model transcript.** `DashboardAddPc.Command` embeds the join code from `HubJoinCode`, which is read from the persistent `data/join-code` file, and anyone holding it can start enrolling a PC. Today the dashboard shows it only on the hub's PC (`DashboardConnect.OnThisPc`). A tool reply lands in the chat's transcript, and possibly in provider logs. The design asks for the command, so the recommendation is to return it, refuse unless the call arrives over loopback, keep the tool non-mutating so no reply is cached for replay, and state in the reply that it holds a secret not to be forwarded. The alternative is a short-lived single-use code, which is larger because it changes enrollment. Paul's call.

**Decision 2: `connect_app` and the admin choice.** `DashboardConnect.ConnectAsync(client, admin, ...)` can write administrator access into an app's settings. Recommendation: the tool takes no `admin` argument and always passes `null`, so a chat cannot widen a client's power; widening stays on the dashboard until `decisions` exists.

## Files

- Modify `src/CoCern.Contracts/RelayToolCatalog.cs`: `RelayTools` names and three catalog entries.
- Modify `src/CoCern.Contracts/ToolSchemas.cs`: `client` enum on `connect_app`; `itemIds` on `attention_respond`.
- Modify `src/CoCern.Contracts/RelayLimits.cs`: `MaxAttentionItemsPerCall`.
- Create `src/CoCern.Contracts/AdminToolContracts.cs`: reply records for the three tools.
- Create `src/CoCern.Core/AdminTools.cs`: `IAdminTools` port and the refusal when none is registered, following the `IAnalyticsTool` pattern.
- Modify `src/CoCern.Core/ToolRouter.cs`: three routes.
- Create `src/CoCern.Host/AdminToolsService.cs`: implements the port over the existing services.
- Modify `src/CoCern.Host/RelayHostBuilderExtensions.cs`: registration, moving `DashboardAddPc` and `DashboardConnect` registration only if the port cannot reach them.
- Modify `src/CoCern.Domain/SessionAttention.cs` and, if the route needs it, `src/CoCern.Core/MessageTools.cs`: bulk respond and closed-session alerts.
- Modify `docs/reference.md`: tool table rows and one paragraph; `README.md` only if a dashboard-only claim becomes false.
- Create `tests/CoCern.Tests/AdminToolsTests.cs`, `AdminToolsHostTests.cs` and `AttentionBulkTests.cs`.
- Modify `tests/CoCern.Tests/ContractsTests.cs` only if a catalog count is pinned; the budgets stay as they are.

## Task 1: `hub_diagnose` (read-only)

Comes first: it is the smallest, and needs no decision.

Reply `HubDiagnosis`: hub version and uptime; listener addresses and the certificate's public fingerprint; whether the certificate backup is present and matches; discovery and network enabled flags; analytics snapshot age or why it is unavailable; session count against the directory limit; and per PC its machine ID, name, announced node or adapter version, last seen and whether it is current. Bounded by the existing reply limit with `truncated`. Source: the data `DashboardCollector` already gathers; no new probes beyond reading the backup's presence through the existing hub certificate code.

- [ ] Write failing tests in `AdminToolsTests`/`AdminToolsHostTests`:
  - a worker gets the existing administrator refusal, and the tool is absent from `RelayToolCatalog.ForRole(false)`;
  - an administrator gets all fields against a host fixture;
  - no field holds a token, join code or private key (assert against the fixture's known secret values, on the raw JSON);
  - a PC with an old adapter version and a PC not seen recently are reported as such;
  - an unavailable analytics snapshot gives a reason, not a failure of the whole reply;
  - repository scoping: an administrator whose `Repositories` exclude a repository sees no session labels from it, via `RelayCaller.MayAccess`.
- [ ] Implement contract, port, route and service; run the failing tests to green.
- [ ] Confirm `Forwarding_catalog_stays_within_its_context_budget` still passes (administrator surface under 22,000 characters, worker under 20,000).
- [ ] Decide whether the diagnostics profile should expose it (`CoordinationTools.IsAllowed` allows only `analytics_query` there today). Recommendation: yes, one more read-only name, with a test.

## Task 2: `add_pc` (after Decision 1)

Reply: `DashboardAddPcStatus` as it is, with `Unavailable` passed through.

- [ ] Write failing tests:
  - worker refused; absent from the worker catalog;
  - the reply's command equals `DashboardAddPc.Command` for the fixture;
  - a call not arriving over loopback is refused with no join code in the reply, matching `DashboardConnect.OnThisPc`;
  - discovery off and adapter missing return `Unavailable` as readable text;
  - `IsMutation` is false, and repeated calls store nothing in the request replay state;
  - the join code does not appear in `DashboardTrafficBuffer`, call telemetry or hub logs after a call (assert on the fixture's value).
- [ ] Implement. The telemetry test may need the reply excluded from any body capture; if it does, make that change the smallest possible and name it in the commit.

## Task 3: `connect_app` (after Decision 2)

Mutation with `requestId`, hub's PC only. `client` is required, from the client names `DashboardConnect` already accepts. Reply: `DashboardConnectStatus`, with no paths to token files beyond what the dashboard page shows.

- [ ] Write failing tests:
  - worker refused; not over loopback refused;
  - unknown client gives `InvalidArguments` and starts no process;
  - any `admin` argument is refused rather than ignored;
  - a retry with the same `requestId` returns the first reply without running setup twice, and changed arguments give `RequestConflict`;
  - unrelated settings in the app's file survive (reuse the existing setup fixtures, pointing at a temporary home);
  - setup failure returns the failure text, not a hub error.
- [ ] Implement over `DashboardConnect.ConnectAsync(client, null, ct)`. Tests must never touch the real user's client settings.

## Task 4: alert backlog

`attention_respond` gains optional `itemIds` (at most `MaxAttentionItemsPerCall`, suggest 50), exclusive with `itemId`, same `action`. Not administrator-only: each session clears only its own items. Alerts about a session that has closed stop being offered.

- [ ] Write failing tests in `AttentionBulkTests`:
  - several items in one call, one state write, and a per-item result;
  - one unknown item does not fail the others;
  - both `itemId` and `itemIds`, an empty list, and over the limit are each `InvalidArguments`;
  - another session's item ID is refused and unchanged;
  - `postpone` still never acknowledges a message;
  - a replayed `requestId` returns the first reply;
  - an alert about a closed session is no longer offered, and an alert about a live one still is;
  - the single `itemId` form behaves exactly as before.
- [ ] Implement in `SessionAttention.Respond`, keeping schema text short: the worker catalog is near its budget.

## Task 5: documentation and verification

- [ ] `docs/reference.md`: tool table rows, the loopback rule, the secret warning and what still needs the dashboard.
- [ ] Full suite in an isolated build; report any test that passes only with an in-tree build.
- [ ] Live check on an isolated hub, never the shared one: a real adapter with `COCERN_ROLE=admin` lists the three tools and calls each; a worker adapter lists none of them.
- [ ] Independent review of secret handling and authority before merge.
- [ ] Update criterion `admin-tools` with evidence, stating what remains for gate 8.

## Review focus

- A reply or log that leaks the join code. This is the highest risk in the plan.
- `connect_app` running a process on the hub's PC on a remote caller's say-so; loopback must be judged from the real connection, not a header. Note that a remote administrator through a local proxy would count as loopback, as it already does for the dashboard.
- The honest limit from the design stands: the hub's PC token is already an administrator token, so these tools widen convenience rather than authority, but each new tool is one more thing a misled administrator chat can be talked into. Without the `manager` criterion's audit, only ordinary call telemetry records these calls, and that is best-effort. If that is not acceptable, the audit must land first.

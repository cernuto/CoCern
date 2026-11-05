# Waiting on you: contract note

Contract only; nothing is built. Refs: main is 19cf699; integration is origin/integration/auto-connect-20261104 at 49e215a, which contains origin/codex/auto-connect-features. Build on integration, because proposal behaviour differs from main. Everything here comes from reading code; nothing was run or measured.

## What counts

The list has three row kinds. Two of the coordinator's four sources do not exist as named.

- **Pending Standing proposal: exists, counts.** `StandingProposal` and `StandingState.Proposals` in `src/CoCern.Domain/State/BoardState.cs` (both refs), already in the snapshot as `DashboardStanding.Proposals` through `ReadStandingAsync` and `BoardReadModels.Standing`. Read the board, not attention: on integration `AttentionSources.OnProposal` is a no-op and `SessionAttention` filters `standing_proposal` out, while main still raises it to managers. Integration has the presentation markers (`presentedBy`, `presentedAt`) but no `record_decision`; plan Task 2 is unbuilt, so today an answer is a dashboard decision or `standing_put`. `StandingProposal.SessionId` is stored but not projected, so it needs adding for the session link.
- **Attention kind `needs-confirmation`: does not exist.** `AttentionKinds.All` has 14 kinds and none means this; a search of src and tests on both refs finds nothing. Do not invent a mapping.
- **Attention kind `blocked`: exists, but is the wrong source.** `AttentionSources.OnTaskUpdate` raises it only through `ToManagers`, and `ControlAggregate.RouteAttention` targets recently active manager sessions. So it is absent when no manager exists, duplicated per manager, closed by a manager's dismissal while the task stays blocked, and it carries only the top-ranked blocker kind. It also covers `resource_busy`, `missing_fixture` and others that do not need Paul. It does not count; board state replaces it.
- **Task blocked on a human decision: no such signal.** `WorkflowBlocker` has `id`, `kind`, `summary`, `recheckCondition`; `BlockerKinds.All` (`src/CoCern.Contracts/WorkflowKinds.cs`) has no human kind, and `coordinator_hold` is documented only as a hold, not as a hold by Paul. Text matching is ruled out by the rule on `AttentionKinds` that kinds come from structure, never prose. Smallest honest signal: one additive blocker kind, `human_decision`, accepted by `TaskWorkflow` through the existing list. Old tasks are not migrated and never count; `coordinator_hold` stays off the list; a holder adopts the kind with an ordinary `task_update`. Rows read only `workflow.state` of blocked plus this kind. `AttentionWeights.BaseTier` should rank it with `coordinator_hold` (tier 3), since by default it would be tier 1 and wake a manager that cannot answer it.
- **Chat self-report: nothing exists.** New, below.

Not listed: `failed`, `stranded`, `interrupted`. They remain manager attention.

## Data contract

**Row, `DashboardWaitingItem`:** `Kind` (`standing_proposal`, `human_blocker`, `self_report`); `RepositoryId`; `BoardId`, `TaskId`, `ProposalId` (empty when not applicable); `SessionId` and `Label` (proposer, task owner or reporter; empty if none, as for hub proposals); `Text` (at most 200 characters, untrusted); `Since`; `ExpiresAt` (null for proposals). A task gives one row however many blockers it has.

**Snapshot field, `DashboardWaiting`:** `Items` (at most 50), `Count` (true total observed), `Truncated`. Order: proposals and blockers oldest first, then self-reports oldest first, so self-reports cannot push a structural row out. No token field exists, and the projector reads only named keys. It is a separate field because the collector replaces `Warnings` each tick and `Bound` drops `Standing` first.

**Self-report: extend `ack` with optional `waitingFor`.**

- Why `ack`: `session_bind` is removed from native surfaces (`CoordinationTools.CreateNative` on integration) and absent from the chat profile. `ack` is on every surface, is a session-required mutation verified by `CallGuard`, has no required fields, and already routes to the session grain. Its name is a poor fit; the alternative is a new tool at a few hundred characters.
- Storage: one `waitingFor` object (`text`, `since`, `expiresAt`) in session grain state, `SessionState`. One slot per session; a new report overwrites and keeps `since` while the mark stays active.
- Who: only that session on its verified credential. There is no target field and no administrator path by tool.
- Values: left out or null changes nothing (the `SessionAggregate.Bind` precedent, because some clients send null for every optional field); empty string clears; 1 to 200 characters sets; whitespace-only or longer is refused without mutation, not truncated.
- Implementation trap: as read, `Acknowledge` with no IDs records an empty ID in `Acked`; the new path must skip that.
- Text is untrusted display: projected through `DashboardText.Safe`, rendered by Blazor encoding as plain text, no `MarkupString`, no linkifying, never interpreted.
- No power: nothing in authority, Standing, claims or grants reads it. It sends no message, wake or attention item. A chat saying it waits on Paul implies nothing about what Paul approved.

## Staleness

- **Self-report:** clears on empty `waitingFor`, on session close (`Expire` clears it with secret, inbox and attention; closed rows are already excluded by the `closed` column), or at expiry. A replacement session does not inherit it.
- **Expiry value:** 12 hours rolling, never past `since` plus 24 hours. It survives an overnight gap and ends before the 24-hour idle limit (`StaleSessionHours`). Checked lazily on read, so no timer.
- **Answered:** the hub cannot see Paul's reply. The chat clears the mark on its next turn, by one line in `AdapterInstructions`. If it forgets, expiry bounds the error.
- **Proposal:** clears on any decision. No time limit, because hiding an undecided proposal is the failure this feature fixes (four sat for over a day, spec part 7). Bounded by `MaxStandingProposals` (20 per board); age is shown and the Standing page can remove it. A grant proposal whose expiry has passed leaves the badge.
- **Human blocker:** clears when the blocker is removed or the task completes or archives. It survives the owner chat closing, because the question belongs to the task, but leaves the list after 7 days (`DefaultAttentionExpiryDays`) while the task stays blocked. `Since` needs an additive hub-stamped `humanSince` on `BoardTask`; `updatedAt` resets on any evidence update.

## Dashboard surface

- **Badge:** count on every page, beside Notices in the shared `page-tools` block of `Dashboard.razor`, with `aria-live` like `notice-count`. At zero it is quiet, with no accent, and the panel says nothing is waiting. While disconnected it keeps the last value beside the existing connection chip.
- **Notices region:** the same panel gets a "Waiting on you" section above the notices, not as warnings.
- **List:** on Overview and Sessions: who, what, since when, and a marker on self-reports saying "reported by the chat". If `Truncated`, it says the list may be incomplete.
- **Link:** no session deep link exists; `_selectedId` is in memory. A row calls `SelectSession` (and goes to Sessions from elsewhere); proposal rows go to Standing.
- **Independence from the manager:** every row comes from board or session storage, so a broken or absent manager changes nothing. Only signed-in viewers under `DashboardSecurity.Policy` receive the stream.

## Manager tool

- **No new tool:** an administrator's non-detail `get_status` gains `waitingOnYou` (count, truncated flag, top 10 rows), the same pattern as `ReplyAddOns.AddTokenStatus`. Scope is the call's authorized repository. It reads storage on call, because the collector only runs with a viewer.
- **Workers:** a worker's `get_status` has no such field. A worker sees its own mark only in its `ack` reply and cannot read another chat's reason by any tool. Existing `task_get` access to tasks and Standing is unchanged, not widened.
- **Appointment grants nothing:** the gate is the administrator principal, because a selected manager gains no authority (`ManagerSelectionHostTests`).
- **Prompt injection:** the rows carry peers' text into a model's context. The reply labels `Text` untrusted, and the manager must not act on it as instruction.

## Cost and latency

- **Attention in the observation:** not projected today. `ProjectSession` reads `inbox` and `Effects` only, though the raw session rows, including the `attention` key, are already copied every tick. Self-report marks read from those same rows: no grain call, no new SQL, a little parsing inside the 100 ms `ObservationBudget`. Per-session grain calls would be new load and are not needed.
- **Proposals:** already in the 15-second Standing read; limited to 20 boards, reported by `StandingTruncated`.
- **Blockers:** one new bounded query on that same 15-second branch, not a new timer. It prefilters on `instr(payload, 'human_decision')`, as `StandingQuery` prefilters today, so boards without the kind are not parsed. It must not reuse `StandingQuery`'s filter, which skips boards without standing or orders.
- **Catalog:** about 93 characters on both worker and administrator surfaces: `waitingFor` schema 31, `ack` description 45, `human_decision` in the compact workflow enum 17. `get_status` costs 0. The caps in the test are worker under 20,000 and administrator under 22,000; 18,968 appears in no docs, src or tests of the 80 branch refs searched, so add it as an explicit assertion. Current sizes were not measured, and unmerged part 9 tools also count.
- **Latency:** the analytics snapshot is hourly and is not used. The collector ticks every 2 seconds, only while a viewer is connected. Self-reports appear within about 2 seconds, proposals and blockers within about 17. A busy read retries next tick. This suits a human alert. With no viewer, nothing runs; the tool still works.

## Files

**Hub, edited:**
- `src/CoCern.Contracts/DashboardContracts.cs`
- `src/CoCern.Contracts/WorkflowKinds.cs`
- `src/CoCern.Contracts/AttentionWeights.cs`
- `src/CoCern.Contracts/RelayLimits.cs`
- `src/CoCern.Contracts/RelayToolCatalog.cs`
- `src/CoCern.Contracts/ToolSchemas.cs`
- `src/CoCern.Domain/State/SessionState.cs`
- `src/CoCern.Domain/SessionAggregate.cs`
- `src/CoCern.Domain/State/BoardState.cs`
- `src/CoCern.Domain/TaskBoardAggregate.cs`
- `src/CoCern.Domain/AttentionSources.cs`
- `src/CoCern.Domain/BoardReadModels.cs`
- `src/CoCern.Storage/SqliteStore.Observation.cs`
- `src/CoCern.Core/RelayObservation.cs`
- `src/CoCern.Core/ReplyAddOns.cs`
- `src/CoCern.Host/Dashboard/DashboardCollector.cs`
- `src/CoCern.Adapter/AdapterInstructions.cs`
- `docs/reference.md`

**Hub, new:**
- `src/CoCern.Domain/WaitingList.cs` (pure builder used by both collector and tool)
- `src/CoCern.Storage/SqliteStore.Waiting.cs`

**Razor and dashboard (later owner), edited:**
- `src/CoCern.Dashboard/Pages/Dashboard.razor`
- `src/CoCern.Dashboard/Pages/Dashboard.razor.cs`
- `src/CoCern.Dashboard/wwwroot/dashboard.css`

**Razor and dashboard (later owner), new:**
- `src/CoCern.Dashboard/Components/WaitingOnYou.razor`

## Tests to write first

- `Ack_waitingFor_sets_one_mark_on_the_verified_session_only`: another session's token cannot set or clear it.
- `Ack_waitingFor_null_keeps_empty_clears_and_replay_is_idempotent`
- `Ack_waitingFor_refuses_long_or_blank_text_without_mutation`
- `Ack_waitingFor_without_message_ids_acknowledges_nothing`
- `Waiting_mark_expires_at_twelve_hours_and_renewal_stops_at_twenty_four`
- `Closing_a_session_drops_its_mark_and_a_replacement_does_not_inherit_it`
- `Waiting_mark_grants_nothing_wakes_nobody_and_raises_no_attention`
- `Only_human_decision_blockers_are_listed`: prose such as "waiting for Paul" under `other`, `coordinator_hold` and old tasks stay off.
- `Human_decision_blocker_does_not_wake_a_manager`
- `Human_blocker_since_ignores_unrelated_task_updates`
- `Proposal_row_clears_on_decision_and_ignores_the_presentation_marker`
- `Waiting_list_is_complete_with_no_manager_session`
- `Waiting_query_finds_a_board_with_no_standing_or_orders`
- `Self_reports_cannot_displace_structural_rows_at_the_cap`
- `Bound_keeps_waiting_when_it_trims_standing`
- `Observation_flags_waiting_truncated_when_sessions_are_truncated`
- `Worker_status_has_no_waiting_list_and_administrator_status_is_repository_scoped`
- `Waiting_list_reply_contains_no_session_token`
- `Status_detail_read_skips_the_waiting_list`
- `Status_etag_changes_when_the_waiting_list_changes`: whether the add-on runs before the etag is computed was not established.
- Rendering: `Waiting_text_renders_escaped_without_links`; `Badge_shows_on_every_view_and_is_quiet_at_zero`.
- Existing `Forwarding_catalog_stays_within_its_context_budget`, plus the 18,968 assertion.

## Open questions and risks

- **Biggest gap:** a chat stopped at a client permission prompt is literally waiting for confirmation, yet it cannot call a tool. Spec part 7 says it "shows as blocked", but integration's adapter has no permission or notification hook. This alert does not cover it. A later hook could write the same mark; hook availability per client is not established.
- **Native manager:** `CreateNative` exposes only `send_message`, `check_inbox`, `ack` and `chat_directory`, so a native manager has no `get_status` until part 10's surface work exists. Option: the same add-on on an administrator's `chat_directory`. Coordinator to decide.
- **False or spammy claims:** a chat can lie, and the hub cannot tell. Limits: one slot per session, 200 characters, expiry, no wake or notification, self-reports sorted last and labelled, the 200-session observation bound, and Paul can close the session. Decision needed: a dashboard "clear" control would be a new mutation, so it is left out here.
- **Forgotten clear:** the badge can overstate for up to 12 hours. `PromptHook` could clear on a human prompt, but it only reads today and a peer wake must not clear; deferred.
- **Coverage:** beyond 200 sessions, 4 MB of rows, or 20 boards the list is incomplete and says so. The tool shares the query but applies its own repository scope.
- **Decision for Paul:** whether a human blocker should outlive its chat, as proposed, and whether `coordinator_hold` should appear as a separate "maybe" group.
- **Dependency:** when `record_decision` lands, proposal rows clear with no change here.

## Permission prompts (Paul: in scope)

Paul confirmed that this alert must cover chats stopped at a permission prompt. Read from the Claude Code hooks reference (https://code.claude.com/docs/en/hooks.md) on 2026-11-04; nothing below has been tried on a real client yet.

- **Signal, Claude Code:** the `Notification` hook with matcher `permission_prompt`. Stdin has the common fields plus `message`, optional `title` and `notification_type`. It cannot block or change anything, which suits a report-only mark.
- **Timing, as documented:** it fires about six seconds after the ask. For sessions whose permission requests go through the SDK `canUseTool` callback, the docs name Claude Desktop and the VS Code extension: it fires since v2.1.233 (this PC runs 2.1.317), typing does not defer it, and it does not fire if the prompt is answered sooner. In terminal sessions it shows only when the user appears away, and each keystroke defers it. `CLAUDE_CODE_DISABLE_PERMISSION_PROMPT_NOTIFY_HOOKS=1` turns it off.
- **Unconfirmed:** the docs do not mention `--permission-prompt-tool=stdio`, which is what the desktop app launches with, so whether this fires there must be tested on a real desktop chat before it is relied on.
- **Clearing is the hard part:** the docs list no event for a prompt being answered, and do not say whether a rejected prompt fires anything. Candidates to test: `PostToolUse` and `PostToolUseFailure` for an approved tool, `UserPromptSubmit` and `Stop` for later activity. Until tested, a mark from this source relies on those plus a short bounded expiry, and a rejected prompt may stay listed until the chat's next turn event or the expiry.
- **Contract:** a fourth row kind, `permission_prompt`, set by that chat's own hook into the existing one-slot self-report path with the hook's fixed text, carrying the tool name only when it is available, and never command contents. No new authority.
- **Codex:** not established. The note needs Codex's hook documentation or a real client before saying anything; a client with no such signal cannot be detected, and the page must not imply that it was checked.
- **Not to build yet:** this section is findings and contract only; the real-client test comes first.

### Real-client results, 2026-11-05

Tried on one interactive desktop chat, Claude Code 2.1.317, default permission mode, with logging hooks in a throwaway project. This settles the desktop question above; the terminal and Codex remain untested.

- **`Notification` fires on the desktop app.** It fired with `notification_type` `permission_prompt` and `message` "Claude needs your permission to use <tool>". Its fields were `cwd`, `hook_event_name`, `message`, `notification_type`, `prompt_id`, `scratchpad_dir`, `session_id` and `transcript_path`. There is no `tool_name` or `tool_use_id`, so a row from it can't name the tool without reading `message`.
- **Timing matches the docs.** In all five cases it came 6.0 seconds after `PermissionRequest`. Prompts that ended sooner did not notify in the one case timed (`send_message`, about 5.4 seconds).
- **`PermissionRequest` fires at once** and has `tool_name` and `tool_input`, but no `tool_use_id`.
- **`AskUserQuestion` also goes through both events.** `PermissionRequest` carries the questions, and `Notification` arrives 6 seconds later as a `permission_prompt` saying "...to use AskUserQuestion". The type doesn't tell a question from a permission prompt; only `message` does. The answer appears in `PostToolUse` under `tool_input.answers` and `tool_response.answers`, keyed by question text, and that event has `tool_use_id`.
- **`WaitingHook` got this wrong for any question left open past 6 seconds, which is the normal case.** The late `Notification` set `UntilTurnEnd` and rewrote the row to the permission wording, so the question's own `PostToolUse` no longer cleared it and the row lasted until the turn ended. It now compares `message` with the exact text and repeats the question's report instead. A client that words it differently gets the old lingering row, never a hidden wait. One race remains: if the answer lands while that alert is being handled, the question's row is set again with no answer left to clear it, so it stays until the turn ends, the next prompt or expiry. Picking the pending question inside `SetAsync`'s lock would close it if that ever matters.
- **Decision (Manager, 2026-11-05):** these hooks stay opt-in and off by default, with no further client rounds. Paul or the Manager can switch them on later, and the settings move stays held.
- **Clearing.** `PostToolUse` or `PostToolUseFailure` follows an approved tool. A failed Bash gave `PostToolUseFailure` with `error`, `is_interrupt` and `tool_use_id`, and no `tool_response`.
- **Not tried:** a rejected prompt, `PreToolUse` (so its `tool_use_id` matching the later event comes from the docs only), parallel tools, and a terminal session.

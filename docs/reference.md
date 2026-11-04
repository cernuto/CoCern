# CoCern reference

The technical details behind the [README](../README.md): every tool, rule, setting and address.

- [Tools](#tools)
- [Calls](#calls)
- [Tasks](#tasks): workflow, resources, stranded actions, archive
- [Attention](#attention)
- [Standing, orders and search](#standing)
- [Documents](#documents)
- [Monitoring](#monitoring): activity, tokens, feedback, analytics
- [Other PCs](#other-pcs): the node, setup, wake-ups, prompt hook
- [Settings](#settings)
- [Clients and tokens](#clients-and-tokens)
- [Addresses](#addresses)
- [Architecture](#architecture)
- [Verification](#verification)

## Tools

Admin tools need a token with `IsAdministrator`. A session's `role` grants nothing.

| Tool | Who | What it does |
| --- | --- | --- |
| `session_open` | worker | Open a session for this chat; returns its private handle and the board's standing. |
| `session_bind` | worker | Record this session's IDE tool bindings. Optional `label` renames the session (to match its chat's title) and `machineId` reports its PC, once (a later one is ignored); a bind that leaves out `binding` or `availability`, or sends `null`, keeps the current one. |
| `worker_list` | worker | List reported worker state and freshness; use `chat_directory` to find a chat to message. |
| `get_status` | worker | Repository and session status, including blockers and uncertain actions. A session-wide stop is included on the bounded cancellation page whenever present. |
| `task_get` | worker | One task, or the board. |
| `task_update` | worker | Create or update a task under `expectedVersion`. |
| `task_claim` | worker | Claim a task and its resources together. |
| `claim_acquire`, `claim_renew`, `claim_release` | worker | Claim, renew or release one resource. |
| `action_begin` | worker | Record that an external action (a deploy, a test run) is starting. |
| `action_report` | worker | Report its outcome and evidence. |
| `send_message` | worker | Message a session or a task. |
| `check_inbox` | worker | Read this session's inbox without consuming it. |
| `ack` | worker | Acknowledge handled messages. |
| `context_get`, `context_put` | worker | Read or write shared notes. Never store credentials or chat transcripts. |
| `chat_directory` | worker | Open registered chats, including hub-local ones, with their session ID and announced direct-wake reachability. |
| `search` | worker | Search tasks, notes and your inbox. |
| `attention_respond` | worker | Accept, dismiss or postpone an attention offer. |
| `standing_propose` | worker | Propose a standing line or grant. |
| `doc_search`, `doc_section` | worker | Search docs by section; read one section. |
| `standing_put` | admin | Write standing lines, grants and board settings. |
| `order_put`, `order_remove` | admin | Add or remove a standing order. |
| `control_request` | admin | Pause or resume dispatch, request cancellation, register a [resource](#resources), close [sessions](#calls) in bulk, or revoke a PC. It never stops work already running. |
| `analytics_query` | admin | Run a named analytics query, or ad hoc SQL. |
| `doc_sync` | admin | Sync a repository's docs now. |

When cancellation details fill the ordinary status reply, the hub may omit the optional `manager` property. A missing property means the selection has not been read. Repeat the read with `managerHistoryOnly: true` to obtain the same repository-visible manager summary without cancellation details. Workers receive an empty history. Administrators can also read history and page with `managerHistoryBefore`.

## Calls

Every call takes `repositoryId`. Session calls also take `sessionId` and `sessionToken`; keep the token private to the chat. Writes take a `requestId`.

**Your own session.** Through the MCP adapter, a session call that leaves out `sessionToken` gets the chat's own session, so a chat whose context was compacted doesn't need its token back. The adapter uses the session the chat last used in that repository. After an adapter restart it looks in the saved session handles, which works only when the client exports its chat ID (Claude Code does; Codex doesn't). What the call gave stands, and a call naming another session never gains this chat's token. Direct HTTP calls still send both.

**Arguments.** IDs (`taskId`, `sessionId`, `resourceId` and the like) are at most 256 characters, not blank, and without control characters. Versions, revisions, generations, attempts and leases are whole numbers; a quoted one (`"3"`) works, and `null` counts as left out. A repeated key or unreadable text is `InvalidArguments`. Out-of-range leases are clamped to 1 to 1800 seconds. A message to a session that doesn't exist is accepted, then comes back to the sender (see Sessions).

**Retries.** Retry a write with the same `requestId`. Within 24 hours it does not run again; it gets the first reply. Only your own arguments count as the same request, not what the hub adds. A very busy board or session (over about 7,500 writes in a day) may drop that reply early: the retry then gets `RequestExpired` (409), so read the current state and decide.

**Sessions.** Keep one session per chat and reuse its handle across reconnects. A `session_open` retried with the same `requestId` and arguments returns the same session, token included, so a lost reply never makes a second session (unless that session has closed since: then it gets `SessionUnknown` with `expired: true`); other arguments under that `requestId` get `RequestConflict`. Through the adapter this holds across app restarts in Claude Code, and in Codex only until the app restarts CoCern. After `SessionUnknown`, open with a new `requestId`.

**Closing sessions.** The hub closes sessions with no call for `Relay:Sessions:IdleHours` (a day by default). An administrator can close them sooner: `control_request` with `"command": "close_sessions"` and `idleMinutes` (60 or more) and/or `labelPrefix` (both must match when both are given) closes that repository's matching sessions. It never closes the session named by the call's `sessionId`; the adapter adds the chat's own when it is left out. It answers `closed`, `kept`, `held`, `active`, `more` and up to 50 `ids`; one call closes at most 512, and `more: true` means call again. The Sessions page's **Close idle sessions** offers idle over an hour, idle over a day, or a label prefix. Idle times count from the last call and are never cut short, but activity is recorded about hourly, so a session may stay up to an hour longer.

- Normally kept open: a session with an action still open. Not closed when idle: a session with unread messages (counted as `held`), for up to 7 days without a call; a label-only close does close it.
- A close by `labelPrefix` alone skips chats working right now: any that made a call in the last 10 minutes (counted as `active`). Activity reaches the directory about hourly, so it may also skip a chat quiet for up to about an hour longer; to close one of those now, use **Close session** (`targetSessionId`).
- A session that has reported activity the directory had not yet heard when it decided (a report still on its way) refuses any close but a forced one, and comes back to the list.
- **Close one session:** each session list entry and the selected session's detail panel have a **Close session** button. Confirm the label, machine and session ID before closing. The unchecked **Override unresolved actions** option enables **Force close session** for an abandoned session. Its unresolved actions keep their records and resource safeguards; each is marked `ownerClosed`, and its task's claim lapses at once, so the principal's other session can [recover](#stranded-actions) them without waiting out the lease. It does not stop tools that are already running or assert their outcome. A plain close of a session that has just begun an action the directory hasn't heard of yet is refused by the session, which comes back to the list. Through `control_request close_sessions`, use `targetSessionId` instead of the bulk filters, and optionally `force: true`. Force requires one exact target, still respects repository access, and never closes the caller's own session.
- The session list holds 2,048; when it is full, `session_open` also closes sessions not seen for a day. If only sessions with open actions fill it, `session_open` gets `CapacityExceeded` with `heldByUnresolvedActions`: report or [recover](#stranded-actions) those actions.
- A closed session's calls get `SessionUnknown` with `expired: true`: call `session_open` again. Its unread direct messages (up to 64) go back to their senders as `delivery.failed` with `reason: "unread"`.
- If a board's report of a just-begun action reaches the session after it closed (a close can land first), the session tells that board, which marks the action `ownerClosed` and lapses its claim, as for a forced close.
- Each close counts only the session's answer to that close, once: a delayed or repeated restore from an earlier close never brings back a session that has since closed.
- A session missing from the list but still open (its close's answer was lost) is repaired when it next reports activity, a bind or its work, once that close has gone unanswered for 30 minutes: the hub closes it again, and it either comes back to the list (it still has an action open) or closes, and its chat opens a new session.
- A message to a closed or unknown session, or to a full inbox, comes back to the sender as an inbox message with `kind: "delivery.failed"`, `reason` (`closed`, `unknown`, `full`, or `unread` for one discarded when its session closed), `recipientSessionId`, `undeliveredMessageId` and the `text`.
- A task whose `targetSessionId` names a session that closed loses the target, so anyone may claim it.

**Large items.** A message or note too large to read back is refused with `PayloadTooLarge` (`maxBytes`, `bytes`). A stored item too large for any page comes back alone as a piece (`truncated: true`, `bytes`, `part`): call again with the same `cursor` and `partOffset` set to `nextPartOffset`. Joined, the pieces are the item's JSON.

**Spend fewer tokens.** Every model call re-reads the whole chat, so fewer calls matter most. `waitSeconds` and `ifNoneMatch` are handled by the adapter (agents always use it); a direct REST call ignores them.

- Wait with `check_inbox` and `waitSeconds` (up to 50) instead of polling. Mail, urgent attention, or an eligible idle reminder ends the wait; the returning attention read offers one item. Internal polls consume no offers, and `attention: false` opts out of attention wakes.
- Pass the `etag` from `task_get`, `get_status`, `worker_list`, `context_get` or `chat_directory` back as `ifNoneMatch`. An unchanged read returns only `{"unchanged":true,"etag":...}`.
- Find task IDs with `search`, then read the needed `task_get` section. For a synchronized board, use `sinceRevision` and finish every page before saving its checkpoint. Reuse shared notes and cached documentation when the context will be needed again.
- Acknowledge in your reply: `send_message` with `ackMessageIds`.
- `supersede: true` replaces your previous status message; `wake: false` sends a note nobody must act on now and never causes a wake, even after the idle hold.
- Renew claims rarely: `claim_renew` takes `leaseSeconds` up to 1800.
- Send your session handle on `task_get` too, so [tips](#feedback) can point out rereads.

**Token reports.** Report what you used since your last report on `task_update` or `action_report`: `"usage": { "inputTokens": 120000, "outputTokens": 3400 }`. It goes to the activity log only.

## Tasks

### Workflow

Add `workflow` to `task_update`:

```json
"workflow": {
  "state": "ready",
  "packet": { "goal": "Fix the refresh timeout", "target": "cocern main", "authorityRef": "grant:ci",
              "restoration": "Remove the test fixture", "stopCondition": "Stop if the target differs", "resources": ["rig-1"] },
  "criteria": [ { "id": "tests", "description": "Tests pass", "result": "not_run" } ],
  "blockers": [],
  "dependsOn": ["T-12"],
  "priority": 10
}
```

- `state` is `ready`, `blocked` (needs a blocker) or `completed` (every criterion `passed` with an `evidenceRef`).
- Blocker kinds: `coordinator_hold`, `provider_rejection`, `tool_unavailable`, `resource_busy`, `missing_fixture`, `prerequisite`, `other`. Going back from `blocked` to `ready` needs `recheckEvidenceRef`.
- `includeNextWork: true` returns a `nextWork` offer. It reserves nothing; claim it with `task_claim`. Tasks are offered only when their `dependsOn` tasks are completed, highest `priority` first, then those whose resources are free.
- `task_claim` and `action_begin` refuse a task with an unfinished dependency: `DependenciesIncomplete` (409), listing them in `dependsOn`.
- `packet.resources` must be claimed. A `task_claim` that leaves out a registered resource gets `ResourcesRequired` with `missing`; aliases resolve by your session's machine. `action_begin` refuses too (`Busy` or `ClaimRequired`) unless your session still holds each one. An unregistered name in `packet.resources` has no lock to take, so it is refused only with the board setting `requireResources` (`unregistered`). A claim's own names must be [registered](#resources).
- `authorityRef: "grant:<id>"` makes `ready` check that [grant](#standing) (`GrantInvalid` if missing, expired or out of scope). `task_claim` and `action_begin` check it again.

### Resources

A resource is something only one chat may use at a time, such as a test rig. Claims take registered resources only: `claim_acquire`, and `task_claim` with `resourceIds`, get `ResourceUnknown` for any other name, so one name never means two locks. An administrator registers one with `control_request`:

```json
{ "repositoryId": "cocern", "requestId": "r-3", "command": "register_resource",
  "resourceId": "rig-1", "machineId": "pc-7", "aliases": ["rig"] }
```

- `machineId` is required. Aliases belong to that machine: a claim resolves names by its session's machine, so `rig` can mean different resources on different PCs.
- Registering again under a name the machine already has adds the new aliases to that resource. An alias that already means another resource gets `AliasConflict`; a resource another repository registered gets `RepositoryDenied`.

### Stranded actions

An action whose session closed before it reported is stranded. The board marks it `ownerClosed`, ends the task's claim, and raises a `stranded` item to the repository's managers, one per action, which closes once the action is reported or recovered. Recover it with `action_report` and `recover: true`:

- An administrator can recover any action, from its own session, without an `outcome`.
- The action's own principal can recover it from a new session once the claim of the session answering for it has ended. It may give a final `outcome` (`completed`, `failed` or `canceled`; anything else gets `InvalidOutcome`).
- Anyone else gets `ActionDenied`.
- Without an `outcome`, an action still in flight becomes `abandoned`: its effects are unknown, and managers get a `failed` item. `recovery` records who, which session and when.
- If the writer was still there, its principal can later report the real final outcome. It replaces `abandoned`, and `overridden` records what it replaced. From another session, that session answers for the action from then on (`recovery` with `override: true`).
- A required restoration stays `pending` until the recovering session reports it (again with `recover: true`) or an administrator accepts it (`restoration: "accepted"`). New actions on the task wait until it is resolved.

### Archive

A completed task with no live claim and no open action leaves its board 30 days after completion (`Relay:Board:ArchiveAfterDays`; `0` means only when the board fills). A board near its 2,048-task limit archives its oldest finished tasks sooner, but never within a day of completion. Archiving closes the task's attention items.

- `task_get` with its `taskId` still reads it (`archived: true`); lists leave it out. Search finds it with status `archived`, and analytics still counts it.
- A delta from `sinceRevision: 0` gets live tasks only. A later checkpoint also gets each task archived since then, once, as `{ "taskId", "archived": true }`. Checkpoints older than 30 days get `DeltaRestartRequired`.
- Claims, updates and actions on it get `TaskArchived` (409). Its ID can't be reused. A `dependsOn` naming it counts as completed.
- A full board that can't archive anything yet refuses new tasks with `CapacityExceeded` (`openTasks`, `finishedTasks`).

## Attention

A `check_inbox`, `task_update`, `action_report` or `task_claim` reply can carry one offer:

```json
"attention": { "id": "a-91", "k": "blocked", "t": "T-12", "x": "T-12 blocked: waiting on schema review", "more": 3 }
```

Answer with `attention_respond`: `accept` or `dismiss` closes it; `later` hides it for 3 replies (or until `until`). Ignoring it counts as `later`. Pass `"attention": false` on a call to skip offers.

Direct messages from another session wake a chat at once unless sent with `wake: false`. The message's existing attention item ID makes repeated watcher reads one wake; the adapter's busy-chat checks and rate limits still apply. Urgent attention (`blocked`, except by a `coordinator_hold`, `failed`, `interrupted` and `stranded`) also wakes at once. Task-change notices and other nonurgent items wait for the next reply or for an idle hold of 10 minutes. Tips never cause a wake.

**Manager items.** `role: "manager"` on `session_open` routes manager items (blocked, failed, review ready, anomalies) to that session. The role is self-declared.

**Anomaly monitor.** With analytics on, the hub checks the named queries hourly (`Relay:Attention:AnomalyInterval`, `0` for off). A value at least twice its baseline, from at least 5 rows, raises an `anomaly` item to managers.

## Standing

A board's approved instructions (lines) and active permissions (grants). Approved content is delivered at `session_open`, then on later replies only when the standing revision changes. Pending proposals are not instructions or authority; without approved instructions or active grants, nothing is delivered. Administrators write them:

```json
{ "repositoryId": "cocern", "requestId": "r-1", "expectedRevision": 0,
  "lines":  [ { "id": "css", "text": "Never deploy CSS without a restoration receipt." } ],
  "grants": [ { "id": "ci", "scope": "repository", "expiresAt": "2026-12-01T00:00:00Z" } ],
  "settings": { "requireResources": true } }
```

- Anyone can `standing_propose` a line or grant; an administrator approves it with `standing_put` and `approve`. Proposals never autoexpire because of age or the proposing session closing; dismiss obsolete proposals manually.
- The dashboard offers **Approve**, **Dismiss** and **Remove** against the exact repository and board, using revision checks and existing standing operations. Approval activates eligible content; dismissal discards a pending proposal, and removal deletes the selected standing item. Nothing is autoapproved.
- Expired grants remain visible but inactive. Grant authority requires a matching `authorityRef` and valid scope and expiry; it never overrides client permissions.
- Read it all with `task_get`, no `taskId`, `section: "standing"`.
- What chats receive is held to 12 KB; a larger `standing_put` gets `PayloadTooLarge` and changes nothing.

### Orders

"When this happens, do that", run by the hub. Nothing runs until an administrator adds one.

```json
{ "repositoryId": "cocern", "requestId": "r-2", "orderId": "after-schema",
  "trigger": { "on": "workflow_completed", "taskId": "T-12" },
  "actions": [ { "do": "release_claims" },
               { "do": "offer", "taskId": "T-13", "sessionId": "s-worker-2" },
               { "do": "notify", "sessionId": "s-manager", "note": "T-12 done; T-13 offered" } ] }
```

- Triggers: `workflow_completed` and `blocker_cleared` (with `taskId`), `claim_released` (with `resourceId`).
- Actions: `release_claims`, `offer` (the session still claims it), `notify`.
- An order never accepts work or grants authority. After 3 failed runs it turns off. It expires after 7 days (`Relay:Orders:DefaultExpiryDays`) unless `expiresAt` says otherwise.

### Search

`search` finds tasks, notes and your own unread messages, and returns IDs and snippets to open with `task_get`, `context_get` or `check_inbox`. Optional: `kinds` (`task`, `context`, `message`), `status`, `limit` (1 to 10, default 3).

## Documents

`doc_search` searches a repository's committed docs by section instead of reading whole files. Nothing is searchable until a repository is configured; the start script adds the checkout it runs from as `cocern` (`-NoDocs` skips this). The Visual Studio profile adds it too, and makes the folder holding the checkout a root, so the repositories beside it are found as well.

| File | Split into |
| --- | --- |
| Markdown (`.md`) | Sections at headings. |
| Text (`.txt`) | Paragraphs. |
| OpenAPI, JSON or YAML | One section per operation and per schema. Other JSON and YAML is skipped. |

Also skipped: files over 256 KB (8 MB for OpenAPI), symlinks, malformed files, and files past the document or byte limit (until there is room again).

**Sync.** The hub picks up new commits every 5 minutes, and `doc_sync` syncs now (once per 30 seconds). It reads committed files with `git show` only, never your working tree. It fetches and follows the branch on the remote, unless `Fetch` is `false` (the start script's setting): then it reads the local branch only and never fetches. If a sync can't be saved, searches keep the last saved commit. Searches answer from one in-memory index per repository, built at the first search and patched by each sync; `doc_section` loads only the file it reads.

| Tool | What it does |
| --- | --- |
| `doc_search` | Up to 5 sections (`limit` up to 10), each with `path`, `anchor`, `heading`, `snippet`, `score`, `tokens` and `commit`. Optional `spec` and `path`. Takes over 10 seconds: returns what it has with `partial: true`. |
| `doc_section` | One section by `path` and `anchor`. A long one comes in parts: call again with `next`. |
| `doc_sync` | Sync now; needs a `requestId`. `branch` changes the branch followed. |

Replies stay under 2,000 tokens; hits that don't fit are counted in `more`. Errors: `NoRepositoriesConfigured`, `DocCacheUnavailable` (off, or not a configured repository).

## Monitoring

### Activity

Every tool call goes to the `activity_log` table: time, session, role, tool, result, sizes and duration, never arguments or reply text. Wakes and attention offers and answers are logged too, and so is each hub-internal message a grain refused for good (kind `effect_refused`, which should stay at zero). Logging never slows or fails a call. Raw rows are kept 30 days, then rolled up into `activity_daily`.

| Counter | Definition |
| --- | --- |
| Tokens | Reported `inputTokens` plus `outputTokens` on successful calls; a retry counts once. |
| Wakes | Recorded urgent-item and idle-hold signals with attention on; new messages without `wake: false` with it off. Immediate direct-message candidates are not yet counted. These hub signals do not prove native chat dispatch. |
| Inbox reads | Bursts of first-page `check_inbox` calls by one session (60 seconds apart end a burst). |
| Offers made | `task_update` replies with `includeNextWork` that offered a task. |
| Offers that ended blocked | Offers whose next `task_claim` failed, or whose task became `blocked` first. |
| Duplicate status writes | A `task_update`, `send_message` or `context_put` repeating the previous write's status text. |
| Manager turns | Bursts of calls by a `manager` session (60 seconds apart start a new turn). |
| Closures | A workflow changing to `completed`. |
| Time to accepted result | Median time from a task's first `ready` to its closure. |

### Tokens

The hub estimates MCP request and reply tokens with the o200k tokenizer, into `activity_log` (`request_tokens`, `reply_tokens`). These measure relay traffic, not model billing, total model context or proof that a model consumed a reply. Reports separate model-facing, background and unknown origins. Administrators see each role's tool-definition tokens in `get_status` (`toolTokens`) and on the dashboard's Standing page.

Every reply is held to its tool's token budget, set at what the 32 KB byte limit allows (about 10,923 tokens), so in practice only a reply that would be cut anyway is compacted. Doc tools have their own 2,000-token budget. Compaction goes in steps: drop optional detail (`detailFetchRequired`), then shorten paged lists (`truncated`, `more`, `nextCursor`), then cut long text to its first line. A compacted reply says `compacted: true` and `compactStep`. IDs, versions, codes, grants and errors are never removed.

### Feedback

Fixed rules spot wasteful patterns. No AI runs in the hub.

**Tips** reach the agent as a low-priority `tip` attention item. They never wake a chat. A session gets at most one an hour, each rule at most once a day. A rule followed less than 20% of the time, over 20 or more tips, switches itself off (`tipRulesOff` in `get_status`).

| Rule | Fires when |
| --- | --- |
| `board_reread` | 3 full board reads without `sinceRevision` in an hour. |
| `task_reread` | 4 full reads of one task in 30 minutes (unchanged `ifNoneMatch` reads don't count). |
| `long_messages` | Your last 5 messages average over 150 tokens. |
| `inbox_polling` | 6 empty `check_inbox` reads in an hour (`waitSeconds` waits don't count). |
| `repeated_status` | A duplicate status write. |

**Alerts** reach managers as `anomaly` items, at most once a day per kind. They need analytics.

| Check | Alerts when |
| --- | --- |
| `tokens_per_task_by_role` | A role's tokens per task are more than double the week before (20+ tasks). |
| `compaction_rate_by_tool` | A tool compacts more than 30% of 20+ replies. |
| `session_token_outliers` | A session's busiest hour is 5 times its role's median hour (10+ hours). |

**Team rules.** When one tip rule fires for 5 different sessions in 7 days, the hub proposes its line as a standing line, from `cocern-hub`. Nothing changes until an administrator approves it.

### Analytics

Every hour the hub copies the database into `data/analytics`, leaving out sessions, inboxes, credentials and replay caches, and converts it for DuckDB. Queries never touch the live database. One copy is kept, so expect about the database's size on disk.

DuckDB needs its `sqlite` extension. The Windows x64 build is bundled in `extensions/duckdb` (signed, with SHA-256 sums) and unpacked beside the hub at build time, so nothing is downloaded. On other platforms the hub starts anyway, logs one warning, and `analytics_query` answers `AnalyticsUnavailable` until you install it: `dotnet CoCern.Host.dll --install-analytics-extension <folder> [platform]`, then set `Relay:Analytics:ExtensionDirectory` to that folder.

```json
{ "repositoryId": "cocern", "name": "tokens_per_task", "params": { "days": 7 } }
```

The twelve public named reports compare the last `days` days (`cur`, 1 to 14, default 7) with the days before (`prev`); `cur_n` and `prev_n` count the rows behind each. Periods use the completed snapshot, not realtime hub activity. Retained workflow history may be incomplete; reports surface that limitation and exclude unknown durations from duration averages.

Use `name: "batch"` with a `reports` array of public report names and common `params` to select several reports from one snapshot. By default each report returns its first summary row with counts and `more`; `includeDetails: true` returns full rows within the row and payload bounds. A strong `etag` supports `ifNoneMatch` for the exact selection, including repository, reports and parameters; do not reuse it for another selection. Partial errors are explicit and produce no ETag.

```json
{ "repositoryId": "cocern", "name": "batch", "reports": ["relay_tokens", "usage_coverage"], "params": { "days": 7 }, "includeDetails": true }
```

| Name | Measures |
| --- | --- |
| `tokens_per_task` | Average reported tokens per task. |
| `relay_tokens` | Measured MCP request/reply token estimates, separated into model-facing, background and unknown origins; not model billing. |
| `usage_coverage` | Successful canonical usage reports among touched tasks; coverage of reported usage, not an estimate of missing model usage. |
| `calls_per_task` | Average tool calls per task. |
| `messages_per_task` | Average task messages per task. |
| `context_by_role` | Average reply bytes per session, by role. |
| `blocked_time` | Average hours of ended blocked spells, and the count of open spells. |
| `claim_conflicts_by_resource` | Refused claims, by resource. |
| `noisiest_tools` | Reply bytes and calls, by tool and reported origin: model-facing, background or unknown. |
| `manager_assignment_latency` | Minutes from `ready` to first claim. |
| `quality_guardrails` | Closures, reopened tasks, and failed, abandoned and retried actions. |
| `attention_outcomes_by_kind` | Attention offer events (shown, accepted, later and dismissed), by kind. |

Ad hoc SQL: `"name": "sql", "sql": "SELECT ..."` runs one `SELECT` over the views `tasks`, `tool_calls`, `context_by_role`, `messages`, `action_reports`, `claim_conflicts`, `attention_outcomes`, `budget_events`, `board_doc`, `task_doc`, `archived_tasks` and `activity`. Every ad hoc query is logged. Errors: `AdministratorRequired`, `AnalyticsUnavailable`, `AnalyticsRefused` (ad hoc SQL off, or not one `SELECT`), `AnalyticsTimeout`, `AnalyticsFailed`, `PayloadTooLarge` (over 8 KB).

## Other PCs

Each joined PC runs a small background program, the **node**. It joins the hub once, announces the PC's chat routes, and wakes its Codex chats when mail arrives. `chat_directory` also lists open registered sessions without a node, including hub-local chats; those rows have `reachable: false` until a direct route is announced. Existing routes are enriched within about a minute. Nodes match chats that called CoCern within the last 7 days (a running Claude waiter keeps that match fresh).

Directory rows are keyed by repository and session ID. A registered row without a route keeps its reported machine ID and has no invented provider. Registration does not prove a model is running. Closed sessions are removed; revoking a node removes its route, while an otherwise open registered session remains unreachable until normal closure or expiry. `truncated: true` also marks a source that is temporarily unavailable, so absence from an incomplete list does not establish closure.

### Setup by hand

The dashboard's **Add another PC** shows one PowerShell command to paste on the other PC (Windows PowerShell 5.1 or PowerShell 7). It downloads the adapter from this hub (`GET /join/adapter.zip`, over HTTPS with the hub's certificate pinned for that download only) and runs setup from `%LOCALAPPDATA%\CoCern\adapter`. Any failure stops it before anything is unpacked or run, and it leaves the window's settings as they were. The zip is unpacked into a fresh folder: a folder holding the same files is left alone, any other is swapped whole (never unpacked over), and when a running node or chat holds the old folder nothing changes and it says to close those chats and stop the node (`Stop-Process -Name CoCern.Adapter`) first. Pasting it again is how a joined PC updates; files added to the folder by hand are not kept. The other PC needs no .NET when the hub serves a self-contained adapter (`artifacts/adapter` from `Build-CoCern.ps1 -Publish`, preferred when present); otherwise it needs the .NET 10 runtime, and the page warns. The command holds the join code, so keep it private. By hand instead, from any copy of `artifacts/adapter` on the other PC:

```powershell
.\CoCern.Adapter.exe --setup --hub https://<hub-pc>:5273/ --join-code <code> --fingerprint <fingerprint>
```

or, from a framework-dependent build with the .NET runtime installed, `dotnet .\CoCern.Adapter.dll --setup ...` with the same arguments.

- The join code is in `join-code` next to the hub's database; the hub logs it once, when it creates it. Letter case doesn't matter. `COCERN_JOIN_CODE` can supply it instead.
- The fingerprint is in the hub's log at every start. With it, setup refuses any other certificate (exit code 4).
- Setup joins, adds a `cocern` server to Claude Code and to `~/.codex/config.toml`, adds `CoCernNode` to the user's Run key, and starts the node. Claude Code's entry goes through `claude mcp add --scope user` (run from the profile folder, so a project's own `cocern` entry doesn't count) when its command line is installed, so Claude Code need not be closed; otherwise, or when a batch-file `claude` would get paths with characters `cmd` treats specially, `~/.claude.json` is edited (with a backup) and setup says to restart Claude Code if it is running. Every settings file is planned before any is written. `--skip-client-config` skips both apps. Every entry names `CoCern.Adapter.exe` when it is beside the dll, else `dotnet` with the dll. Setup keeps the join code, so a PC the hub dropped joins again by itself.
- Run it again any time; it leaves existing entries alone. `--data-dir <path>` moves the node folder (default `%LOCALAPPDATA%\CoCern\node`; set `COCERN_NODE_DIR` to match). Exit codes: 1 setup or join problem, 4 a hub certificate other than the pinned one, 5 an unreadable settings file. A running node that later meets another certificate sends nothing, says so once, and keeps retrying the pinned hub.
- A chat whose adapter sets its own `COCERN_URL`, `COCERN_TOKEN_FILE` or `COCERN_TOKEN` is not seen by the node.

Firewall, once on the hub PC, in PowerShell as administrator:

```powershell
New-NetFirewallRule -DisplayName "CoCern hub (HTTPS 5273)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5273 -Profile Private
```

### Wake-ups

**Codex chats** are woken by the node with `codex queue`: never mid-turn, at most one waiting wake per chat, at most one every 3 seconds and 20 per 10 minutes. If a wake's outcome is unknown (the dispatch failed or was cut off), that chat isn't woken again for 10 minutes, and that wake's messages are never sent twice. Codex gives the adapter no chat ID, so the node finds each chat from its transcript.

**Claude chats** on a joined PC wake themselves with `--await-inbox --chat <CLAUDE_CODE_SESSION_ID>` (see the README). The chat must have called `session_open` on this PC first, through the node's credentials; the waiter needs the node, so on the hub's own PC it exits 1. Exit codes: 0 mail arrived, 1 no session for that chat, 2 timed out, 3 stopped. `--print-inbox` also prints the messages; `--ack <id,id>` acknowledges them, then waits again. The chat shows as reachable only while it runs.

A chat is **reachable** when its PC announced within the last 90 seconds of the hub's clock (either side, so a clock set back never keeps a silent PC live for long) and its wake path is running: the node's for Codex, the waiter for Claude. `chat_directory` and the dashboard apply the same rule.

`--wake <config>` and `--await-inbox <config>` also take a hand-written config; see `scripts/Start-CoCernWake.ps1`. Watch each chat with only one of them. For an `https://` `relayUrl`, give the hub certificate's SHA-256 `fingerprint` in the config (it wins over `COCERN_FINGERPRINT`); only that certificate is accepted.

### Prompt hook

`--prompt-hook` on setup adds a hook to `~/.claude/settings.json` and `~/.codex/hooks.json` that prints one line per message you type, at no model cost. It only reads and never blocks; Codex asks you to approve it in `/hooks`. `COCERN_HOOK_TIMEOUT_MS` (default 1000) bounds its wait.

```
CoCern: 3 attention items (1 urgent), 2 inbox, standing r4; top a-91 blocked "T-12".
```

### Stop, revoke, expiry

- **Stop waking on a PC:** create a file named `stop` in the node folder; delete it to resume.
- **Stop the node:** end its `dotnet` process, and remove the `CoCernNode` Run value.
- **Revoke a PC:** an administrator calls `control_request` with `"command": "revoke_machine"` and the `machineId`. Change the join code too, or it can join again under a new ID.
- **Expiry:** the hub holds 256 PCs. When full, a new PC replaces the one unseen longest, if that one hasn't announced for 30 days. A replaced PC's ID is never reused; when it comes back, its node joins under a new ID.

## Settings

Set them in `appsettings.json`, as environment variables (`Relay__Clients__0__Token`) or on the command line.

### Hub

| Setting | Default | What it does |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5271` | HTTP address. Plain HTTP must be loopback, or the hub refuses to start. |
| `Relay:DatabasePath` | `data/relay.db` | The database, relative to the content root. Owner-only on Linux and macOS. |
| `Relay:SiloPort` | 21111 | Orleans silo port (loopback). Change it and `Relay:ClusterId` for a second hub. |
| `Relay:GatewayPort` | 0 (off) | An Orleans client gateway (loopback). Leave it off: nothing uses it, and it would let any local process call grains without a token. |
| `Relay:ClusterId` | `CoCern` | Orleans cluster name. |
| `Relay:Board:ArchiveAfterDays` | 30 | Days after completion before a task is archived (0 to 3650). |
| `Relay:Sessions:IdleHours` | 24 | Hours without a call after which the hub closes a session, checked every 10 minutes (2 to 720). Never sooner; activity is recorded about hourly, so up to an hour later. A session with unread messages is kept. |
| `Relay:Dashboard:ViewerPrincipalIds:N` | | Administrator principals that may open the dashboard. The start script and Visual Studio profile set theirs. |
| `Relay:Dashboard:OpenOnStart` | `false` | Open the dashboard in the default browser when the hub starts, already signed in with that start's one-time link. The Visual Studio launch profile turns it on. |
| `Relay:Dashboard:SessionHours` | 8 | Hours a dashboard sign-in lasts in that browser session (1 to 168). The deadline is fixed at sign-in; use doesn't extend it. |
| `Relay:Dashboard:RememberDays` | 7 | Days a sign-in lasts, across browser restarts, when "Remember this browser" is ticked (1 to 30, and at least `SessionHours`). |
| `Relay:Connect:AdapterPath` | found | The adapter the Connect page uses, and the folder `/join/adapter.zip` serves. Default: `artifacts/adapter`, else the build output, looked for only inside this checkout or beside a published hub. Other PCs get a self-contained `artifacts/adapter` whenever there is one. |

### Features

All on by default except the two for other PCs.

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Activity:Enabled` | `true` | Log activity. |
| `Relay:Activity:RetentionDays` | 30 | Days of raw activity rows kept. |
| `Relay:Tokens:Enabled` | `true` | Count tokens. |
| `Relay:Tokens:Enforce` | `true` | Hold replies to their budget. `false`: only measure. |
| `Relay:Tokens:EnforceTools:N` | all but doc tools | Enforce only these tools. |
| `Relay:Feedback:Tips` | `true` | Send tips to agents. |
| `Relay:Feedback:Alerts` | `true` | Alert managers. Needs analytics. |
| `Relay:Feedback:ProposeRules` | `true` | Propose standing lines. |
| `Relay:Feedback:MinFollowRate` | 0.2 | Below this, a tip rule switches itself off. |
| `Relay:Search:Enabled` | `true` | `search`. |
| `Relay:Orders:Enabled` | `true` | Standing orders. |
| `Relay:Orders:DefaultExpiryDays` | 7 | Order lifetime without `expiresAt`. |
| `Relay:Attention:Enabled` | `true` | `false`: no offers, and every message wakes its chat. |
| `Relay:Attention:SnoozeBoundaries` | 3 | Replies a `later` item stays hidden (1 to 50). |
| `Relay:Attention:MaxHoldMinutes` | 10 | Minutes before other nonurgent items wake an idle chat once (1 to 1440). Direct messages bypass the hold; tips and `wake: false` messages never cause a wake. |
| `Relay:Attention:ItemExpiryDays` | 7 | Days before an unclosed item goes away (1 to 90). |
| `Relay:Attention:AnomalyInterval` | `01:00:00` | Time between anomaly checks. `0`: off. |
| `Relay:Analytics:Enabled` | `true` | Snapshots and queries. |
| `Relay:Analytics:AllowAdHocSql` | `true` | Accept `sql` queries (administrators only). |
| `Relay:Analytics:SnapshotInterval` | `01:00:00` | Time between snapshots (at least 1 minute). |
| `Relay:Analytics:Directory` | `analytics` beside the database | Where snapshots go. |
| `Relay:Analytics:ExtensionDirectory` | `duckdb_extensions` beside the hub | Where the DuckDB extension is. |
| `Relay:Analytics:QueryTimeout` | `00:00:10` | Longest query (at most 5 minutes). |
| `Relay:Analytics:MaxRows` | 50 | Rows per result (at most 500). |
| `Relay:Discovery:Enabled` | `false` | Lets PCs join and announce chats. |
| `Relay:Discovery:Repositories` | `["cocern"]` | Repositories joined PCs may use. |
| `Relay:Discovery:JoinCode` | generated | The join code. Unset: made on first start, kept owner-only in `join-code` next to the database. At least 8 characters. |
| `Relay:Local:HttpsPort` | 5272 | Loopback HTTPS with the hub certificate, for pinned connections. When enabled, the hub writes its HTTPS address and fingerprint to `local-hub.json` beside the database, along with the first configured loopback HTTP origin if there is one. `0` turns this listener off. |
| `Relay:Network:Enabled` | `false` | Also serve HTTPS on every network interface. |
| `Relay:Network:HttpsPort` | 5273 | The HTTPS port. |
| `Relay:Network:CertificatePath` | beside the database | Self-signed certificate, made on first start. Keep it: PCs and Connect's app entries pin its fingerprint. A set path is relative to the content root. For a disposable second hub, set an explicit private path as well as separate database and listener ports, so the preview has a separate identity instead of adopting the main hub's legacy certificate. Migration retains the original certificate. |

### Documents

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:DocCache:Enabled` | `true` | Doc search. |
| `Relay:DocCache:SyncMinutes` | 5 | Minutes between syncs. `0`: only `doc_sync`. |
| `Relay:DocCache:IdleMinutes` | 30 | Minutes before an unused document, or a repository's search index, leaves memory (2 to 1440). |
| `Relay:DocCache:MaxDocuments` | 500 | Documents per repository (up to 5,000). |
| `Relay:DocCache:MaxRepositoryBytes` | 128 MB | Text per repository (up to 1 GB). |
| `Relay:DocCache:Roots:N` | | Folders whose git repositories are found and indexed on their own (relative to the content root). |
| `Relay:DocCache:RootDepth` | 2 | Folder levels searched below each root (1 to 4). |
| `Relay:DocCache:Repositories:N:Id` | | The repository. |
| `…:ClonePath` | | Its local clone. |
| `…:Branch`, `…:Remote` | `main`, `origin` | What to follow. |
| `…:Fetch` | `true` | `false`: never fetch; read the local branch. |
| `…:TokenFile` | | Read-only token for `git fetch`, sent only to the remote's own HTTPS URL. |
| `…:Patterns:M:Glob`, `…:Patterns:M:Spec` | `**/*.md`, `**/*.txt`, `**/*.json`, `**/*.yaml`, `**/*.yml` as `docs` | Files to read, and their spec name. |
| `…:Exclude:M` | | More paths to leave out (`node_modules`, `bin`, `obj` and `.git` always are). |

Changing a repository's settings makes its next sync re-read everything.

**Found repositories.** With `Roots` set, every sync also looks for git repositories under them (a folder holding `.git`, never inside another repository) and indexes each one's current branch under its folder name in lower case, without fetching. A session's workspace is learned too: the adapter sends its git top-level folder with `session_open`, and when that folder is a repository inside a root the hub indexes it at once: a folder already indexed keeps its ID; otherwise it goes under the session's repository if no repository has that ID yet, else under its folder name if that is free. Roots are the only folders the hub reads this way; a configured repository's ID always wins, at most 64 are added, and access still follows each client's repositories (an administrator client, such as the generated token, may read every one).

### Adapter

Connect sets `COCERN_TOKEN_FILE`, `COCERN_URL` and `COCERN_FINGERPRINT`, and `COCERN_ROLE=admin` when the page's **Administrator tools** box is ticked; add the others to an app's `cocern` entry by hand. By hand, run `dotnet <path>/CoCern.Adapter.dll` as an MCP server with:

| Variable | Default | What it does |
| --- | --- | --- |
| `COCERN_URL` | `http://127.0.0.1:5271` | Hub address. Plain HTTP works only on loopback. |
| `COCERN_TOKEN_FILE` | | File holding the client token. Wins over `COCERN_TOKEN`. |
| `COCERN_TOKEN` | | The token itself. |
| `COCERN_PROFILE` | | `chat`: only `session_open`, `send_message`, `check_inbox`, `ack` and `chat_directory`. `diagnostics`: worker tools plus analytics, requiring an administrator credential role for analytics and exposing no administrator mutations. Default and `chat` behavior are unchanged. |
| `COCERN_ROLE` | | `admin` also lists administrator tools, for a chat whose token is an administrator's (Connect's is). The hub still checks the token. |
| `COCERN_SIGNALR` | `false` | `true` prints a hint on standard error when the inbox changes. |
| `COCERN_FINGERPRINT` | | The hub certificate's SHA-256 fingerprint, for an `https://` `COCERN_URL`: only that certificate is accepted. Connect sets it. |

With none of the first three set, the adapter uses this PC's node.

On the hub PC, dashboard Connect uses the first HTTP listener bound to a loopback address, including a custom port or IPv6 loopback. If none is available, it uses the hub's known local HTTPS listener and pins its certificate. Wildcard and network listeners are never chosen for a local connection.

Command-line Connect reads `local-hub.json` beside the token file. It prefers the recorded loopback HTTP address; older records with just the local HTTPS address and fingerprint still use pinned HTTPS. An HTTP-only hub does not create or refresh this identity record. For an HTTP-only hub on a custom port, use dashboard Connect or supply its exact loopback address with `--hub`. Without a record or override the adapter retains its old default of `http://127.0.0.1:5271`.

Advanced overrides: `--token-file <path>` (default: `data/local-client-token` above the adapter folder), `--hub <url>` and `--fingerprint <sha256>` (for an explicit HTTPS connection). Other options: `--only claude-code|claude-desktop|codex`, `--role admin|worker`, `--check` (report only) and `--json`. Each app's JSON result reports whether it has administrator tools. Connect can update an owned local entry from pinned HTTPS to the current HTTP address and remove its old pin; it preserves the role unless requested and leaves other settings alone. Check reports such entries as `outdated`; connecting reports `updated` (restart the app). A local update leaves entries for other credential folders, remote or invalid addresses, and joined nodes alone, even if a remote entry shares this hub's credential path or certificate fingerprint.

## Clients and tokens

| Setting | What it does |
| --- | --- |
| `Relay:Clients:N:Token` | A client's bearer token. |
| `Relay:Clients:N:TokenFile` | Read the token from this file instead. A missing file is created at startup with a new token, owner-only. |
| `Relay:Clients:N:PrincipalId` | Who the client is. |
| `Relay:Clients:N:IsAdministrator` | Allows administrator tools (default `false`). |
| `Relay:Clients:N:Repositories:M` | Repositories it may use. An administrator may use every repository. |

The hub refuses to start with no clients and no OAuth issuer. The start script and the Visual Studio profile configure one administrator client with `data/local-client-token`; the start script's `-RepositoryId` (default `cocern`) names this checkout's repository, for the client and the doc cache.

**A worker-only token.** In the PowerShell window you start the hub from, before starting it:

```powershell
$t = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)); Set-Content data/worker-token $t; $env:Relay__Clients__1__Token = $t; $env:Relay__Clients__1__PrincipalId = 'worker'; $env:Relay__Clients__1__Repositories__0 = 'cocern'
```

Then give those chats `COCERN_TOKEN_FILE=data/worker-token`, or run `--connect --token-file data/worker-token`. Keep the file private.

**OAuth (optional).** For remote clients with their own identity provider: `Relay:OAuth:Authority` (HTTPS issuer), `Relay:OAuth:Audience`, `Relay:OAuth:RequiredScopes:N` (default `relay`), `Relay:OAuth:Resource` (the public HTTPS `/mcp` address; publishes `/.well-known/oauth-protected-resource/mcp`) and `Relay:OAuth:Principals:N` (`Subject`, `PrincipalId`, `IsAdministrator`, `Repositories`). The hub doesn't issue tokens. This is how the ChatGPT chat app would connect; it has not been tested.

## Dashboard

The dashboard provides Overview, Sessions, Analytics, Database, Logs, Standing, Documents and Connect. Existing dashboard viewer access is required for both the page and its read-only data endpoints.

### Standing and logs

**Standing** distinguishes approved instructions and active grants from pending proposals and retained expired grants. Use **Approve**, **Dismiss** or **Remove** for the exact board shown; dashboard writes use the existing authorization and revision checks. Dismiss obsolete proposals manually, since age and session closure do not expire them.

**Logs** supports level and text search filters and filtering successful routine HTTP requests to focus on relevant events.

### Analytics page

Open **Analytics** to compare one repository over 1–14 days with the preceding equal period. Both periods end relative to the displayed hourly snapshot completion timestamp, not realtime activity or the current hub connection time. The repository selector uses repository IDs known to the dashboard and defaults conservatively when none are present. Changing either selector fetches one combined report; an older response cannot overwrite the latest selection.

The twelve public named reports include relay request/reply token estimates and successful canonical usage-report coverage among touched tasks, alongside reports covering average tokens, calls and messages per task; average reply bytes per session by role; noisiest tools by reply bytes; claim conflicts by resource; quality guardrail events; attention offer events (shown, accepted, later and dismissed) by kind and outcome; average ready-to-first-claim latency in minutes (an assignment proxy); and average hours of ended blocked spells, with open blocked spells counted separately. The shared `AnalyticsReportCatalog` supplies report titles and units. Charts include numeric equivalents, units and sample counts; **Top 10** / **Show all** and the origin filter make noisy-tool rows easier to inspect. Attention event denominators are per kind and are not summed across outcome categories.

Noisiest tools groups each tool by its reported adapter path: model-facing, background or unknown. Current adapters mark ordinary MCP calls separately from internal inbox, watcher and hook reads; an explicit `attention: false` alone does not make a model-facing call background. Older adapters and unmarked raw callers remain unknown. Origin is telemetry, not proof that a model ran or consumed a reply. Counts and bytes survive raw-log retention through daily totals, whose boundary days have day precision rather than exact rolling timestamps.

`GET /dashboard/api/analytics?repositoryId=cocern&days=7` returns the fixed reports under the existing dashboard viewer policy. It accepts no SQL and preserves the existing analytics row and payload limits. HTTP ETag / `If-None-Match` reads can return `304 Not Modified` only for the exact repository and days cache selection; partial report errors carry no ETag. Missing snapshots, unavailable analytics, per-report failures and truncated results are shown explicitly. Unknown values display as **Not measured**; measured zeros remain **0**. Retained workflow history may be incomplete, and reports surface missing history rather than reconstructing it. Unknown durations are excluded from averages. Usage coverage measures successful canonical usage reports among touched tasks; it does not establish complete workflow history or model billing. No sample data is substituted.

## Addresses

| Route | What it is |
| --- | --- |
| `/mcp` | Remote MCP. Needs a token. |
| `/api/tools`, `/api/tools/{tool}` | List tools, call a tool. Needs a token. |
| `/hubs/coordination` | SignalR inbox hints. Needs a token. |
| `/api/discovery/enroll`, `/api/discovery/announce` | Joining PCs (with discovery on). |
| `/join/adapter.zip` | The adapter folder that Add another PC's command downloads. No token (it holds no secret); HTTPS, or this PC only; with discovery on. |
| `/dashboard/` | The dashboard. |
| `/scalar`, `/openapi/v1.json` | API reference. This PC only. |
| `/health` | Health check. |

REST replies are `{ "ok": true, "code": "...", "dataJson": "{...}", "version": 1 }`; `dataJson` is a JSON string, so parse it again. Errors: 400 invalid or refused (bad arguments, or a refusal such as `ResourceUnknown` or `ClaimRequired`), 401 no valid token, 403 denied, 404 unknown tool or `NotFound`, 409 a conflict to resolve by reading again (such as `VersionConflict` or `Busy`), 413 too large. The `code` says exactly which. The stdio adapter returns the parsed `data` instead.

Tokens work only over HTTPS or real loopback, and the hub ignores forwarded headers. Plain HTTP on loopback keeps traffic on this PC but cannot prove the listener is the hub. If another local process takes its port while the hub is down, that process could receive a local app's token. To avoid that tradeoff, explicitly configure local HTTPS (`Relay:Local:HttpsPort`) with the hub's fingerprint. Connections between PCs still use HTTPS and never fall back to plain HTTP after a failure. Behind a local reverse proxy every caller looks local, so don't put one in front of the hub.

## Architecture

CoCern stores coordination only: tasks, claims, messages, notes and reported outcomes.

| Project | What it holds |
| --- | --- |
| `CoCern.Contracts` | The tool catalog and schemas, error codes and limits (`RelayLimits`). Shared by hub and adapter. |
| `CoCern.Domain` | The rules, as pure code. `CoordinationStateMachine.Apply` checks every call and hands it to one aggregate: control, session, task board or lock. Discovery and the anomaly monitor have their own. |
| `CoCern.Core` | `RelayApplication` runs each call through `CallGuard` (validation, then the session check and pause gate), `ToolRouter` (one aggregate call, or a flow that spans aggregates: `ClaimComposer`, `SessionTools`, `StatusTools`, `MessageTools`, `SearchTools`) and `ReplyAddOns`. Also the Orleans grains. |
| `CoCern.Storage` | SQLite: grain state, due work, search index and activity log. |
| `CoCern.Analytics` | DuckDB snapshots and named queries. |
| `CoCern.Host` | ASP.NET Core: REST, MCP, SignalR, auth, the due-work pump and the dashboard. |
| `CoCern.Dashboard` | The Blazor WebAssembly dashboard. |
| `CoCern.Adapter` | The stdio MCP adapter, the node, setup, connect and the wake watchers. Depends only on Contracts. |

Dependencies point inward; Domain knows nothing about Orleans or storage.

| Grain | Key | Holds |
| --- | --- | --- |
| `SessionGrain` | session ID | Inbox, binding, attention items. |
| `TaskBoardGrain` | repository + board | Tasks, actions, standing, orders. |
| `LockGrain` | resource | One advisory claim. |
| `ControlGrain` | `hub` | Pause/stop, the session directory, manager routing. |
| `DiscoveryGrain` | `hub` | Joined PCs and their chats. |
| `AnomalyMonitorGrain` | repository | The next anomaly check. |

**A call.** The client calls `POST /api/tools/{tool}` or `/mcp` (the stdio adapter forwards to REST). The host authenticates the token; identity never comes from the JSON. `RelayApplication` validates the call and the session handle, then calls the grain, which runs `CoordinationStateMachine.Apply` on a copy of its state. The reply then picks up its add-ons in one fixed, named order (`ReplyAddOns.Steps`, pinned by a test): piggybacked renewals and acknowledgments, the next-work offer, standing, the attention offer, offered and claimed work, related work, compaction to the token budget, and last, marking a page seen from the reply as sent, so a message cut from the page stays unseen. On success, one SQLite transaction saves the state, its outbox of effects for other grains, its due work and its search rows. `DueWorkDispatcher` delivers the effects; receivers ignore ones they already applied, and may get them late, reordered or twice. A message sends a SignalR hint; the inbox stays the source of truth. The activity log is written in the background.

| Place | What |
| --- | --- |
| `data/relay.db` | One SQLite file (WAL): grain state, due work, search index, activity. Only the hub opens it. |
| `analytics/`, `join-code`, `adapter-zip/` | Beside the database: DuckDB snapshots, the join code (owner-only), and the zip `/join/adapter.zip` serves (owner-only, rebuilt when the adapter changes). |
| `data/local-client-token` | The administrator token, where the start script and Visual Studio profile put it. Owner-only. |
| `data/hub-certificate.pfx` | The HTTPS certificate, beside the database. One an earlier version made under the content root (`src/CoCern.Host/data` from Visual Studio) is moved here, keeping its fingerprint. |
| `%LOCALAPPDATA%\CoCern\node` | On each joined PC: the node's credential, pinned fingerprint, join code, session handles and wake state. |

**Messages between aggregates.** Every hub-internal operation is a typed record in `CoCern.Domain.Messages`: the session lifecycle (`SessionOpen`, `SessionExpire`, `SessionRestore` and the rest, built around one `SessionRecord`), deliveries and attention (`MessageDelivery`, `AttentionRaise`, `AttentionClose`, `AttentionRoute`), locks (`ActionBlock`, `ActionUnblock`), standing orders (`OrderWatch`, `OrderRelease`, `OrderFire`, `OrderFailed`, `OrderWatchFailed`), and the hub's direct reads and steps. Each has one shape, one sender and one delivery class. A state transfer (`Retry`) is retried until it lands; a report its sender sends again whole (`DropAndCount`) is dropped and counted as `effect_refused` when refused; advice (`Advisory`: attention items, standing-order steps) is dropped. Calls the hub makes to an aggregate directly, never through an outbox, are typed the same way (`IInternalRequest`, catalogued in `InternalRequests`), without a delivery class. A payload missing a required field is answered `MalformedEffect`, logged once at Error naming the fields (never their values; the effect records that it was reported, so a reloaded grain stays quiet), and then retried or dropped by its class. A test fails on any internal operation without a record (the untyped list is empty and pinned at zero), and `tests/CoCern.Tests/Support/AggregateHub.cs` runs lifecycle scenarios through every delivery order, with duplicates, checking the invariants after each step.

**Reading stored state.** Code outside the Domain asks the Domain instead of reading aggregate JSON itself. `ReadModels` decides whether a session is closed (stored as `grain_state.closed` on each commit), whether a chat is reachable, which aggregate kinds an analytics snapshot keeps (board and lock) and which top-level keys it removes (`requests`, `requestTombstones`, `sessionToken`). `BoardReadModels.Standing` projects the standing panel from the `standing` and `orders` sub-documents, which is all Storage extracts. Dashboard text is cleaned by `DashboardText.Safe` in Contracts. A test checks every JSON path in `Views.sql` and the standing query against a board the Domain built (`tests/CoCern.Tests/Support/DomainBoard.cs`).

**Aggregate state.** Each aggregate's stored document is read and written through its state class in `CoCern.Domain.State` (`ControlState`, `SessionState`, `BoardState`, `LockState`, `DiscoveryState`, `AnomalyState`), which names every key it stores and declares them in a `StateShape`; keys one aggregate writes on another's document have their own small class (`LockOrderState` for standing orders on a lock). Tests build each document through `Apply` and check that it holds no key its shape leaves out, so a new stored key is named in its state class before a handler writes it.

**Rules to keep.** Claims are advisory. A new reply add-on goes into `ReplyAddOns.Steps` at an explicit position, never through dependency injection. Every fixed limit lives in `RelayLimits`. Keep tool descriptions and schemas short: they are sent on every model turn, and a test caps their size.

## Verification

Beyond the unit and integration tests, `scripts/Smoke-CoCern.py` checks a real hub process end to end (97 checks; see the README).

**Checked live**

- Two chats on one PC (Codex and Claude Code): messages, acknowledgments, version conflicts, busy claims and the Claude waiter.
- Codex wake-ups: `codex queue` started real turns in two desktop chats; a watcher restart sent no duplicates.
- Discovery with a separate hub and two nodes on one PC: joining, certificate pinning, `chat_directory`, refusals, and a stopped node turning unreachable within 90 seconds.
- The dashboard at desktop and phone sizes: sign-in link, sessions, logs, themes, and the Connect page writing an app's settings.

**Not checked yet**

- Discovery or the dashboard from a second physical PC or phone, through a real firewall.
- The current Add another PC command on a real Windows PC. It changed after its last live run; tests compile its certificate pin and run it end to end in PowerShell 7 here, and in Windows PowerShell 5.1 only on Windows. Also the Claude desktop app's settings.
- A real Codex chat woken by the node and found from its transcript.
- Remote HTTPS with an external OAuth issuer, and the ChatGPT chat app.
- Analytics loading the bundled extension on a real Windows hub.
- Overall token savings.

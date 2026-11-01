# Collaboration

How agents share work through the hub. Every call takes `repositoryId`. Calls on a session also take `sessionId` and `sessionToken`. Writes take a fresh `requestId`; retry with the same one.

## The loop

1. `session_open` once per chat. Give `role: "manager"` to the chat that manages the others.
2. `task_claim` the task and its resources before you work.
3. Do the work with your own tools. The hub never runs them.
4. `task_update` with what happened.
5. Talk with `send_message`, read with `check_inbox`, then `ack` what you handled. Reading does not acknowledge.

Claims are advisory. They cannot stop manual IDE work or grant any permission.

## Tools

| Tool | What it does |
| --- | --- |
| `session_open`, `session_bind` | Open a chat's session; record its tool bindings. |
| `get_status`, `worker_list` | Status of the repository and session; reported workers. |
| `task_get`, `task_update`, `task_claim` | Read, update and claim tasks. |
| `claim_acquire`, `claim_renew`, `claim_release` | Resource claims. |
| `action_begin`, `action_report` | Mark the start and outcome of an external action. |
| `send_message`, `check_inbox`, `ack` | Messages. `recipientSessionId` for a private message, `taskId` for the task conversation. |
| `context_get`, `context_put` | Shared notes. |
| `chat_directory` | Chats on every PC, with the session ID to message. See [Other PCs](discovery.md). |
| `search` | Find tasks, notes and your own messages. |
| `attention_respond` | Answer an attention offer. |
| `standing_propose` | Propose a standing line or grant. |
| `standing_put` | Administrator: change standing. |
| `order_put`, `order_remove` | Administrator: write or remove standing orders. |
| `control_request` | Administrator: `pause`, `resume`, `stop`, `cancel`, `register_resource`, `revoke_machine`. |
| `analytics_query` | Administrator: query analytics. See [Monitoring](monitoring.md#analytics). |

An administrator is a token with `IsAdministrator`. The `role` you give at `session_open` grants nothing.

## Workflow

Add `workflow` to `task_update` to say a task is ready, blocked or done:

```json
"workflow": {
  "state": "ready",
  "packet": { "goal": "Fix the refresh timeout", "target": "pkrelay main", "authorityRef": "grant:ci",
              "restoration": "Remove the test fixture", "stopCondition": "Stop if the target differs",
              "resources": ["rig-1"] },
  "criteria": [ { "id": "tests", "description": "Tests pass", "result": "not_run" } ],
  "blockers": [],
  "dependsOn": ["T-12"],
  "priority": 10
}
```

- `state` is `ready`, `blocked` or `completed`.
- `blocked` needs at least one blocker: `id`, `kind`, `summary`, `recheckCondition`. Kinds: `coordinator_hold`, `provider_rejection`, `tool_unavailable`, `resource_busy`, `missing_fixture`, `prerequisite`, `other`.
- Going from `blocked` back to `ready` needs `recheckEvidenceRef`.
- `completed` needs every criterion `passed`, each with an `evidenceRef`.
- Read it back with `task_get` and `section: "workflow"`.

### Next work

Add `includeNextWork: true` to `task_update`. The reply gets a `nextWork` offer: `Offered`, `NoEligibleWork`, `Paused` or `Unavailable`. An offer reserves nothing. Claim it with `task_claim`.

- Only `ready` tasks with no blockers are offered. Highest `priority` first.
- A task waits until every task in `dependsOn` is `completed`. Up to 16, on the same board, no cycles.
- At equal priority, a task whose `packet.resources` are all free comes first.
- When a task completes, the reply lists the tasks it `unblocked`.

### Board changes

`task_get` with no `taskId` and `sinceRevision: 0` returns changed tasks and a `snapshotRevision`. Page with `cursor`. Save `snapshotRevision` only after the last page, and use it as the next `sinceRevision`. `DeltaRestartRequired` means start again from your last saved checkpoint.

## Attention

Each session has a queue of things to look at, like a blocked task or a failed action. On by default.

A successful `check_inbox`, `task_update`, `action_report` or `task_claim` reply carries at most one offer:

```json
"attention": { "id": "a-91", "k": "blocked", "t": "T-12", "x": "T-12 blocked: waiting on schema review", "more": 3 }
```

Answer with `attention_respond`, `itemId` and `action`:

| `action` | What it does |
| --- | --- |
| `accept` | Closes the item. |
| `dismiss` | Closes the item. |
| `later` | Hides it for 3 replies, or until `until`. Never acknowledges a message. |

- An offer you ignore counts as `later`.
- Pass `"attention": false` on a call to get no offer.
- Managers (sessions with `role: "manager"`) get `blocked`, `failed`, `interrupted`, `review_ready`, `standing_proposal`, `anomaly` and `order_failed` items.
- `get_status` shows the count: `"attention": { "actionable": 2 }`.

| Tier | Kinds | Wakes the chat? |
| --- | --- | --- |
| 1 | `blocked`, `failed`, `interrupted` | At once. |
| 2 | `claim_expiring`, `anomaly`, `order_failed` | No. |
| 3 | `review_ready`, `duplicate_warning`, `standing_proposal`, and `blocked` by a `coordinator_hold` | No. |
| 4 | `related_work`, `next_work`, `info` (a message) | No. |

Items below tier 1 wait for the chat's next reply. A chat with no such reply for 10 minutes gets one wake. To reach someone fast, set a blocker or report a failure.

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Attention:Enabled` | `true` | `false`: no offers, and every message wakes its chat again. |
| `Relay:Attention:SnoozeBoundaries` | 3 | Replies a `later` item stays hidden (1 to 50). |
| `Relay:Attention:MaxHoldMinutes` | 10 | Minutes before an idle chat gets one wake (1 to 1440). |
| `Relay:Attention:ItemExpiryDays` | 7 | Days before an unclosed item goes away (1 to 90). |

### Anomaly monitor

With [analytics](monitoring.md#analytics) on, the hub checks the named queries every hour. A value at least 2 times its baseline, from at least 5 rows each, raises an `anomaly` item to managers. For `quality_guardrails` closures it is the other way round: a drop to half the baseline. The monitor starts when the first manager session opens.

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Attention:AnomalyInterval` | `01:00:00` | Time between checks. `0` turns it off. |
| `Relay:Attention:AnomalyThresholds:<query>:Ratio` | 2 | `0` turns that query's check off. |
| `Relay:Attention:AnomalyThresholds:<query>:MinSamples` | 5 | Rows each value needs. |
| `Relay:Attention:AnomalyThresholds:quality_guardrails:DropRatio` | 2 | `0` turns the closures check off. |

## Standing

A board can hold settled rules and permissions. Chats get them at `session_open`, and again only when they change.

- **Lines**: up to 30 short rules, 200 characters each.
- **Grants**: up to 30 permissions, each with `scope` (`repository`, `resource` or `action`), `targets` and `expiresAt`.
- **Settings**: `requireResources: true` refuses `ready` without `packet.resources`.

An administrator writes them:

```json
{ "repositoryId": "pkrelay", "requestId": "r-1", "expectedRevision": 0,
  "lines":  [ { "id": "css", "text": "Never deploy CSS without a restoration receipt." } ],
  "grants": [ { "id": "ci", "scope": "repository", "expiresAt": "2026-12-01T00:00:00Z" } ],
  "settings": { "requireResources": true } }
```

- `standing_put` also takes `remove` and `approve` (lists of IDs). A stale `expectedRevision` gets `VersionConflict`.
- Anyone can `standing_propose` a `line` or `grant`. Nothing changes until an administrator approves it.
- Read everything with `task_get`, no `taskId`, `section: "standing"`.
- `authorityRef: "grant:<id>"` makes `ready` check the grant. A missing, expired or out-of-scope grant gets `GrantInvalid`.

## Orders

A standing order is "when this happens, do that", run by the hub. **Off by default.**

```json
{ "repositoryId": "pkrelay", "requestId": "r-2", "orderId": "after-schema",
  "trigger": { "on": "workflow_completed", "taskId": "T-12" },
  "actions": [ { "do": "release_claims" },
               { "do": "offer", "taskId": "T-13", "sessionId": "s-worker-2" },
               { "do": "notify", "sessionId": "s-manager", "note": "T-12 done; T-13 offered" } ] }
```

| Trigger `on` | Fires when |
| --- | --- |
| `workflow_completed` (`taskId`) | The task completes. |
| `blocker_cleared` (`taskId`) | The task leaves `blocked`. |
| `claim_released` (`resourceId`) | Someone releases that resource. A lease that runs out does not count. |

| Action `do` | What it does |
| --- | --- |
| `release_claims` | Releases the task's claim and its resource claims. |
| `offer` | Gives the session a `next_work` item. It still has to `task_claim`. |
| `notify` | Gives the session an `info` item. |

- Up to 4 actions per order, 20 orders per board. Same board only.
- An order never accepts work, grants authority or clears a blocker.
- A failed run raises `order_failed` to managers. After 3 failures the order is turned off.
- `get_status` lists orders and their `ordersRevision`. `order_remove` takes an `orderId`.

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Orders:Enabled` | `false` | Allows `order_put` and `order_remove`, and runs orders. |
| `Relay:Orders:DefaultExpiryDays` | 7 | Lifetime when an order gives no `expiresAt` (1 to 30). |

## Search

Find tasks, notes and your own unread messages by words. On by default.

```json
{ "repositoryId": "pkrelay", "sessionId": "...", "sessionToken": "...", "query": "refresh timeout" }
```

```json
{ "hits": [ { "id": "T-118", "k": "task", "s": "completed", "at": "2026-10-29", "x": "…token [refresh] [timeout] after…" } ] }
```

- Optional: `kinds` (`task`, `context`, `message`), `status`, `limit` (1 to 10, default 3).
- Open a hit with `task_get`, `context_get` or `check_inbox`.
- The query is plain words. Each space-separated chunk is one phrase.
- `SearchUnavailable` means search is off.

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Search:Enabled` | `true` | Keep the search index. |

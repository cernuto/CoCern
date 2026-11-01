# Attention

Each session keeps a small queue of attention items: things it should look at, such as a blocked task, a failed action or a review that is ready. The hub ranks them and offers one at a time on replies the agent already gets. The agent answers with `attention_respond`.

Attention is on by default. Set `Relay:Attention:Enabled` to `false` to turn it off.

## Where offers appear

A successful `task_update`, `action_report` or `task_claim` reply carries at most one offer:

```json
"attention": { "id": "a-91", "k": "blocked", "t": "T-12", "x": "T-12 blocked: waiting on schema review", "more": 3 }
```

`k` is the kind, `t` the task, `b` the board when not `default`, `x` at most 200 characters of text, and `more` how many other items could be offered now.

- Pass `"attention": false` on one of these calls to get no offer, for example in the middle of careful work.
- `check_inbox` never carries an offer. The adapter's background inbox reads would mark it shown before the model saw it.
- `get_status` shows only the count: `"attention": { "actionable": 2 }`. Snoozed and expired items are not counted.
- An offer is counted as seen only when it is attached to a reply. If a reply has no room left, no offer is made.

## Answer an offer

```json
{ "repositoryId": "repo", "requestId": "r-7", "sessionId": "...", "sessionToken": "...", "itemId": "a-91", "action": "later" }
```

| Action | What it does |
| --- | --- |
| `accept` | Closes the item. |
| `dismiss` | Closes the item. |
| `later` | Hides it for the next 3 replies, or until `until` (an ISO time) when you give one. |

- `later` never acknowledges a message. Only `ack` does.
- An item that blocks another session's claimed task can be put off at most twice, and for at most 30 minutes after the first time. After that `later` answers `SnoozeLimit` (HTTP 409).
- An offer you do not answer counts as `later` at your next reply.
- An unknown or closed item answers `NotFound`.

## Where items come from

| Source | Raises | To | Closes when |
| --- | --- | --- | --- |
| Board | `blocked` (kind of its most urgent blocker) | managers | the blocker is cleared or the task completes |
| Board | `failed` (an action reported `failed`) | managers | the task completes |
| Board | `review_ready` (the task completed) | managers | the task leaves `completed` |
| Board | `next_work` (a dependency completed) | the unblocked task's `targetSessionId` | anyone claims that task |
| Board | `standing_proposal` | managers | an administrator approves or rejects it |
| Lock | `claim_expiring` (lease ends within 60 seconds, action in flight) | the holder | renew, release, or the action resolves |
| Claim | `duplicate_warning` (open look-alike tasks), `related_work` (completed ones), IDs only | the claimer | the claimed task completes |

- A manager is a session opened with `role: "manager"` (any case). Give that role at `session_open`.
- The session that caused a change gets no copy of its own manager-bound item.
- Each raise and close commits with the change that caused it, through the source's outbox.
- Look-alikes come from `search` on the task's title or goal, so they need search turned on.
- The `nextWork` field on `task_update` is unchanged. `next_work` items cover tasks unblocked for someone else.

## How items are ranked

| Tier | Kinds |
| --- | --- |
| 1 | `blocked`, `failed`, `interrupted` |
| 2 | `claim_expiring`, `anomaly`, `order_failed` |
| 3 | `review_ready`, `duplicate_warning`, `standing_proposal`, and `blocked` from a `coordinator_hold` |
| 4 | `related_work`, `next_work`, `info` |

- Lower tier first. An item that blocks another session's claimed task moves up one tier.
- Within a tier: priority (`low` 0, `normal` 1, `high` 2) plus an aging bonus of 1 per 4 hours waited, at most 2. Then oldest first.
- Items for the task you are working on are skipped.
- The kind comes from structure (a blocker kind, a closure, a claim), never from the text.

## Wakes

- Only a tier 1 item wakes a chat at once. See [wake.md](wake.md).
- Other items wait for a reply. A chat with no offer-carrying reply for 10 minutes gets one wake for its oldest waiting item.
- Each item wakes a chat at most once.

## Items

- One open item per kind, task and subject. Raising it again updates it.
- For 10 minutes after an item closes, the same raise sent again is ignored, so a repeated delivery cannot reopen it.
- At most 50 open items per session. When full, the lowest-tier oldest item is dropped.
- Every item expires after 7 days if nothing closes it.
- Items live in the session's own state, next to the inbox, and survive restarts.
- Each shown offer and each answer is recorded in the [activity log](activity.md) (kind `attention`).

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Attention:Enabled` | true | Offers on replies, and storing and routing items. |
| `Relay:Attention:SnoozeBoundaries` | 3 | Replies a `later` item stays hidden when no time is given (1 to 50). |
| `Relay:Attention:MaxHoldMinutes` | 10 | Minutes an idle chat waits before one wake for a lower-tier item (1 to 1440). |
| `Relay:Attention:ItemExpiryDays` | 7 | Days before an item nobody closed disappears (1 to 90). |

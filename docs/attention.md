# Attention

Each session keeps a small queue of attention items: things it should look at, such as a blocked task, a failed action or a review that is ready. The hub ranks them and offers one at a time on replies the agent already gets. The agent answers with `attention_respond`.

Attention is on by default. Set `Relay:Attention:Enabled` to `false` to turn it off.

## Where offers appear

A successful `check_inbox`, `task_update`, `action_report` or `task_claim` reply carries at most one offer:

```json
"attention": { "id": "a-91", "k": "blocked", "t": "T-12", "x": "T-12 blocked: waiting on schema review", "more": 3 }
```

`k` is the kind, `t` the task, `b` the board when not `default`, `x` at most 200 characters of text, and `more` how many other items could be offered now.

- Pass `"attention": false` on one of these calls to get no offer, for example in the middle of careful work.
- The adapter's background inbox reads (wake watchers, the Claude waiter, and `check_inbox` with `waitSeconds`) send `attention: false`, so they never use up an offer the model does not see.
- **Upgrade adapters before or together with the hub.** Older adapters' background reads do not send `attention: false`, so they consume offers the model never sees.
- A `check_inbox` reply closes the info items of the messages it lists, since the model now sees them. A waiting read and the Claude waiter's printed inbox take no offer, but they still report the messages they showed, so those items close too.
- `get_status` shows only the count: `"attention": { "actionable": 2 }`. Snoozed and expired items are not counted.
- An offer is counted as seen only when it is attached to a reply. If a reply has no room left, no offer is made.
- An offer on a reply that is lost on the way counts as `later` at your next reply, like an offer you did not answer.

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
| Lock | `claim_expiring` (lease ends within 60 seconds, action in flight) | the holder | renew, release, the lease ends, or the action resolves |
| Lock | `interrupted` (the lease ended with the action unresolved) | the holder and managers | the action is reported resolved |
| Claim | `duplicate_warning` (open look-alike tasks), `related_work` (completed ones), IDs only | the claimer | the claimed task completes |
| Next-work offer | `next_work` for the task a `task_update` reply's `nextWork` offered | the caller | the caller claims it, or after a day |
| Inbox | `info` for each message that may wake its chat | the recipient | `check_inbox` shows it, or `ack` |

- A manager is a session opened with `role: "manager"` (any case). Give that role at `session_open`.
- The session that caused a change gets no copy of its own manager-bound item.
- A session neither fresh nor active (no offer-carrying reply) for 24 hours, such as a closed chat, gets no manager items.
- Each raise and close commits with the change that caused it, through the source's outbox.
- Look-alikes come from `search` on the task's title or goal, so they need search turned on. A task must contain every word and match well, so one shared word is not enough. The search runs after the claim reply, so the items appear from the next reply on.
- The `nextWork` field on `task_update` is unchanged.

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
- Everything else, messages included, waits for a reply. A chat with no offer-carrying reply for 10 minutes gets one wake, however many items wait. The next one can come 10 minutes after its next reply.
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
| `Relay:Attention:Enabled` | true | Offers on replies, storing and routing items, and urgent-only wakes. `false` restores the old wakes: every message wakes. |
| `Relay:Attention:SnoozeBoundaries` | 3 | Replies a `later` item stays hidden when no time is given (1 to 50). |
| `Relay:Attention:MaxHoldMinutes` | 10 | Minutes an idle chat waits before one wake for a lower-tier item (1 to 1440). |
| `Relay:Attention:ItemExpiryDays` | 7 | Days before an item nobody closed disappears (1 to 90). |

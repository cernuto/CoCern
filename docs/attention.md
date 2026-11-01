# Attention

Each session keeps a small queue of attention items: things it should look at, such as a blocked task, a failed check or a review that is ready. The hub ranks them and offers one at a time. The agent answers with `attention_respond`.

**Status:** sessions store, rank and answer items now. Nothing raises items yet, and no reply carries an offer yet. Both arrive in a later build step. Until then, every existing reply is unchanged.

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
- An item that blocks another session's claimed task can be put off at most twice, and for at most 30 minutes after the first time. After that `later` answers `SnoozeLimit`.
- An offer you do not answer counts as `later` at your next reply.
- An unknown or closed item answers `NotFound`.

## What an offer looks like

```json
"attention": { "id": "a-91", "k": "blocked", "t": "T-12", "x": "T-12 blocked: waiting on schema review", "more": 3 }
```

`k` is the kind, `t` the task, `b` the board when not `default`, `x` at most 200 characters of text, and `more` how many other items wait.

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
- The kind comes from structure (a blocker kind, a closure packet), never from the text.
- Only tier 1 will be allowed to wake a chat.

## Items

- One open item per kind and task. Raising it again updates it.
- At most 50 open items per session. When full, the lowest-tier oldest item is dropped.
- Every item expires after 7 days if nothing closes it.
- Items live in the session's own state, next to the inbox, and survive restarts.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Attention:SnoozeBoundaries` | 3 | Replies a `later` item stays hidden when no time is given (1 to 50). |
| `Relay:Attention:ItemExpiryDays` | 7 | Days before an item nobody closed disappears (1 to 90). |

Attention is on by default and has no off switch yet. The switch comes with offers on replies.

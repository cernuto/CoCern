# Standing orders

A standing order is a rule an administrator approves once: "when this happens, do that". The hub runs it itself, so the manager does not spend a turn on the obvious next step.

Orders are **off by default**. Turn them on only when the [activity counters](activity.md) show that manager follow-up turns are a large share of the manager's work.

## Turn it on

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Orders:Enabled` | false | Allows `order_put` and `order_remove`, and runs orders. Off, orders written earlier never run. |
| `Relay:Orders:DefaultExpiryDays` | 7 | Days an order lives when it gives no `expiresAt` (1 to 30). |

Offers and notices are [attention](attention.md) items, so they also need `Relay:Attention:Enabled` (on by default).

## Write an order (administrators)

`order_put` needs an administrator token, like `standing_put`. The `role` given at `session_open` does not count.

```json
{ "repositoryId": "repo", "requestId": "r-1", "orderId": "after-schema",
  "trigger": { "on": "workflow_completed", "taskId": "T-12" },
  "actions": [
    { "do": "release_claims" },
    { "do": "offer", "taskId": "T-13", "sessionId": "s-worker-2" },
    { "do": "notify", "sessionId": "s-manager", "note": "T-12 is done; T-13 offered" } ],
  "expiresAt": "2026-11-08T00:00:00Z" }
```

An order with the same `orderId` is replaced. `order_remove` takes an `orderId`. Both accept an optional `expectedRevision`: the order revision `get_status` shows as `ordersRevision`. A stale one gets `VersionConflict`.

**Triggers** (one order per trigger on a board):

| `on` | Names | Fires when |
| --- | --- | --- |
| `workflow_completed` | `taskId` | the task's workflow becomes `completed` (every criterion passed with an `evidenceRef`) |
| `blocker_cleared` | `taskId` | the task leaves `blocked` |
| `claim_released` | `resourceId` | a claim on that registered resource is explicitly released, with `claim_release` or by another order's `release_claims` |

`claim_released` fires only on an explicit release. A claim whose lease just runs out does not fire it.

**Actions** (up to 4, coordination only):

| `do` | Fields | What it does |
| --- | --- | --- |
| `release_claims` | `taskId` (optional for task triggers) | Releases the task's claim, and the resource claims its owner took for that claim. A resource the owner has claimed again since, for example for its next task, is kept. |
| `offer` | `taskId`, `sessionId` | Gives the session a `next_work` item for the task. It still needs `task_claim`. The task must pass the next-work checks: ready, dependencies completed, a valid grant in `authorityRef`, no unresolved action, not claimed by another session. |
| `notify` | `sessionId`, optional `note` | Gives the session an `info` item |

Tasks must be on the same board. A `claim_released` order must name the task for `release_claims`.

## What an order can never do

- Record acceptance. Evidence is reported by workers, not checked by the hub, so accepting work stays a manager or human decision.
- Grant authority, satisfy readiness, or change a blocker. An offered task is claimed with `task_claim` and checked as usual.
- Offer a task that next-work would not offer, or one with a `provider_rejection` blocker.
- Touch another board or repository.

## How it runs

- The actions are written in the same commit as the change that fired them, and the hub's due-work pump delivers them.
- Each order runs once per trigger change. A redelivered effect is applied once.
- An expired or disabled order does not run. `order_put` drops expired orders.
- A failed action (for example, a claim with an unresolved action) is logged, and each manager session gets an `order_failed` item. It is not retried. After 3 failed runs the order is disabled. Write it again to start over.
- An offer or notice that its session refuses (an unknown session, or one in another repository) is a failed run too.

## See orders

- `get_status` lists the board's orders when orders are on: `id`, `on`, target `t`, `until`, `runs`, `fails`, and `off`, `expired` or `err` when they apply.
- The dashboard's **Standing** page (`/dashboard/standing`) shows each board's orders and its last 20 order log entries.

## Limits

| Limit | Value |
| --- | --- |
| Orders per board | 20 (`MaxOrders`) |
| Actions per order | 4 (`MaxOrderActions`) |
| Note | 200 characters (`MaxOrderNoteLength`) |
| Failed runs before disable | 3 (`MaxOrderFailures`) |
| Lifetime | 30 days at most (`MaxOrderExpiryDays`) |
| Order log | 20 entries (`OrderLogEntries`) |
| Boards one resource tells | 16 (`MaxOrderWatchBoards`) |

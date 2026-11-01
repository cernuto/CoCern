# Activity log and counters

The hub records every tool call in the `activity_log` table, so you can measure how the team works before changing anything. Recording is on by default. It never changes a reply, never fails a call, and never makes a call wait: when the queue is full, events are dropped.

## What is recorded

One row per public tool call, from REST, MCP and SignalR alike: time, repository, session, the session's `role` from `session_open`, tool, result code, request and reply size, and duration. A row never holds arguments or reply text. It keeps only the task, note or resource the call wrote or claimed, and a short fingerprint of the status text it wrote there.

The hub also adds one `wake` row each time something can wake a chat: with [attention](attention.md) on, a new tier 1 item or an idle-hold wake; with it off, a message without `wake: false` landing in a session inbox. Background status reads (`get_status` with `attentionDetail`, from the prompt hook and wake watchers) are recorded without a role, so they are not manager turns. Each attention offer shown and each answer adds one `attention` row: `tool` is the item's kind and `code` is `shown`, `accepted`, `later` or `dismissed`. Expired items are not recorded.

A background writer saves rows about once a second, in its own short transaction. Once a day it rolls rows older than the retention period into `activity_daily` (one row per day, repository, role and tool) and deletes them.

## Reporting tokens

`task_update` and `action_report` accept an optional `usage` object:

```json
"usage": { "inputTokens": 120000, "outputTokens": 3400 }
```

Report the tokens you used since your last report. The hub copies them into the activity row only, and only when the call succeeds, because a rejected milestone is usually sent again with the same `usage`. They are never stored in the task. A malformed `usage` is ignored. The stdio adapter advertises `usage` on `task_update` only, to keep its tool definitions small; `action_report` accepts it as well.

## Counters

Counters are read per repository and time window from `activity_log`.

| Counter | Exact definition |
| --- | --- |
| Tokens | Sum of reported `inputTokens` and `outputTokens` on successful calls. An exact retry (same session, tool and `requestId`) counts once. |
| Wakes | With attention on: new tier 1 attention items, and each idle-hold wake the first time the hub hands it out. With attention off: new messages stored in a session inbox without `wake: false`. Redeliveries do not count. Whether the chat's model actually ran is up to the adapter and is not seen here. |
| Inbox reads | Bursts of successful first-page `check_inbox` calls (`cursor` 0 or absent) by one session. A burst ends after 60 seconds with no such read, so one adapter wait that polls every 2 seconds is one read, and so are back-to-back waits. Later pages belong to the same read. |
| Offers made | Replies to `task_update` with `includeNextWork` that offered a task. |
| Offers that ended blocked | An offer of task X to session S that ended in S's next `task_claim` of X failing, or in X's workflow being set to `blocked` before S claimed it or it completed. Offers still open are not counted. |
| Duplicate status writes | A successful `task_update`, `send_message` or `context_put` that writes the same status text to the same task, conversation or note as the write just before it, under a different `requestId`. For `task_update` the status text is `patch`, `notes`, `message` and `workflow` together. |
| Manager turns | Bursts of calls by one session whose role is `manager`. A gap of 60 seconds or more starts a new turn. |
| Closures | A task's workflow changing to `completed` through `task_update`. Writing `completed` again does not count. |
| Manager turns per closure | Manager turns divided by closures in the same window. |
| Time to accepted result | Median time from a task's first `ready` workflow write to its closure. Closures with no logged `ready` are left out. |

A closure is the worker's reported completion. The hub keeps no separate acceptance record, so this is the closest signal.

## How to read the counters

The counters are computed by `SqliteStore.ReadActivityCountersAsync`. To look at the raw rows, open a copy of the database (or the live one read-only) with `sqlite3`:

```sql
-- Calls, errors and tokens per role and tool in the last day
SELECT role, tool, COUNT(*) AS calls, SUM(1 - ok) AS errors, SUM(input_tokens) AS input, SUM(output_tokens) AS output
FROM activity_log
WHERE at_ticks >= (strftime('%s', 'now', '-1 day') * 10000000 + 621355968000000000)
GROUP BY role, tool ORDER BY calls DESC;

-- Older days, after the rollup
SELECT day, role, tool, calls, input_tokens, output_tokens FROM activity_daily ORDER BY day DESC LIMIT 50;
```

`at_ticks` is .NET UTC ticks. `kind` is `call`, `wake` or `attention`. To compare periods with DuckDB, see [analytics.md](analytics.md).

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `Relay:Activity:Enabled` | true | Record activity. Set false to turn it off. |
| `Relay:Activity:RetentionDays` | 30 | Days of raw rows kept before the daily rollup. At least 1. |

## Database

The log ships with SQLite schema version 2. A version 1 database is upgraded on first start, after the usual pre-migration backup next to it (`relay.db.pre-migration-*.db`). Version 2 also adds the search index; see [search.md](search.md). The hub refuses newer versions, and refuses to start if its SQLite has no FTS5. Neither case changes the database.

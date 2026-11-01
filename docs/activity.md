# Activity log and counters

The hub records every tool call in the `activity_log` table, so you can measure how the team works before changing anything. Recording is on by default. It never changes a reply, never fails a call, and never makes a call wait: when the queue is full, events are dropped.

## What is recorded

One row per public tool call, from REST, MCP and SignalR alike: time, repository, session, the session's `role` from `session_open`, tool, result code, request and reply size, and duration. A row never holds arguments or reply text. It keeps only the task or note the call wrote, and a short fingerprint of the status text it wrote there.

The hub also adds one `wake` row each time a message that can wake its chat lands in a session inbox.

A background writer saves rows about once a second, in its own short transaction. Once a day it rolls rows older than the retention period into `activity_daily` (one row per day, repository, role and tool) and deletes them.

## Reporting tokens

`task_update` and `action_report` accept an optional `usage` object:

```json
"usage": { "inputTokens": 120000, "outputTokens": 3400 }
```

Report the tokens you used since your last report. The hub copies them into the activity row only. They are never stored in the task. A malformed `usage` is ignored.

## Counters

Counters are read per repository and time window from `activity_log`.

| Counter | Exact definition |
| --- | --- |
| Tokens | Sum of reported `inputTokens` and `outputTokens`. An exact retry (same session, tool and `requestId`) counts once. |
| Wakes | New messages stored in a session inbox without `wake: false`. Redeliveries do not count. Whether the chat's model actually ran is up to the adapter and is not seen here. |
| Inbox reads | Successful `check_inbox` calls for the first page (`cursor` 0 or absent). Later pages belong to the same read. Adapter waits and wake checks count too. |
| Offers made | Replies to `task_update` with `includeNextWork` that offered a task. |
| Offers that ended blocked | An offer of task X to session S that ended in S's next `task_claim` of X failing, or in X's workflow being set to `blocked` before S claimed it or it completed. Offers still open are not counted. |
| Duplicate status writes | A successful `task_update`, `send_message` or `context_put` that writes the same status text to the same task, conversation or note as the write just before it, under a different `requestId`. For `task_update` the status text is `patch`, `notes`, `message` and `workflow` together. |
| Manager turns | Calls by sessions whose role is `manager`. A burst counts as one turn; a gap of 60 seconds or more starts a new one. |
| Closures | A task's workflow changing to `completed` through `task_update`. Writing `completed` again does not count. |
| Manager turns per closure | Manager turns divided by closures in the same window. |
| Time to accepted result | Median time from a task's first `ready` workflow write to its closure. Closures with no logged `ready` are left out. |

A closure is the worker's reported completion. The hub keeps no separate acceptance record, so this is the closest signal.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `Relay:Activity:Enabled` | true | Record activity. Set false to turn it off. |
| `Relay:Activity:RetentionDays` | 30 | Days of raw rows kept before the daily rollup. At least 1. |

## Database

The log ships with SQLite schema version 2. A version 1 database is upgraded on first start, after the usual pre-migration backup next to it (`relay.db.pre-migration-*.db`). Version 2 also adds an empty `search_index` table for search, which comes later. The hub refuses newer versions, and refuses to start if its SQLite has no FTS5. Neither case changes the database.

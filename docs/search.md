# Search

The hub keeps a full-text index of tasks, notes and inbox messages. It is written in the same transaction as the state it describes, so it never disagrees with committed state. There is no `search` tool yet; agents cannot query it until the tool ships.

Search is on by default. To turn it off:

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Search:Enabled` | true | Keep the search index. Off empties it; on again rebuilds it at the next start. |

## What is searchable

| Source | One document per | Text | Who can find it |
| --- | --- | --- | --- |
| Board | Task | Title, packet goal and target, blockers, `dependsOn`, `packet.resources`, `facts.notes`, the latest 20 reports and messages | Sessions in the repository |
| Board | Context note | `content` | Sessions in the repository |
| Session | Unread inbox message with text | Message text | Only that session |

- A task's title is `facts.title` when set, else the packet `goal`, else the task ID.
- Standing lines and grants are not searchable. Chats already receive them; see [standing.md](standing.md).
- Acknowledged messages drop out. Task-change notifications have no text, so they are skipped; the task itself is searchable.
- Locks, the control record and discovery give nothing.
- Fields named like a credential (`token`, `secret`, `password`, `apiKey`, `privateKey`, `authorization`, `cookie` and similar) are never indexed. Do not put secrets in task text anyway.

## How queries work

- A query is read as plain words. Quotes, `AND`, `OR`, `NEAR`, `*` and `column:` are just words, never search syntax.
- Each space-separated chunk is one phrase, so `T-118` matches "T 118" but not a T and a 118 far apart.
- Every phrase must match. Only when that finds fewer hits than asked for, hits with any of the phrases are added after them.
- A title match counts four times a body match. Hits much weaker than the best one are dropped (under a quarter of its score).
- Results are scoped to one repository, plus the asking session's own inbox. They can be narrowed by kind (`task`, `context`, `message`) and by status.
- Each hit gives the ID, kind, status, date and a short snippet with matches in `[brackets]`. Open it with `task_get`, `context_get` or `check_inbox`.

## Index upkeep

- Each commit rewrites only the documents whose source changed: a task when its `changeRevision` moves, a note when its `version` moves, a message when it arrives or is acknowledged. A commit that only changes the outbox writes nothing.
- On start, the hub builds the index from all saved state when it was never built (a version 1 upgrade or an older version 2 database) or when extraction rules changed. This runs in the startup transaction.
- Searches use their own read-only connection, so they never wait for a write.

## Limits

| Limit | Value |
| --- | --- |
| Body per document | 4,096 bytes (`RelayLimits.MaxSearchBodyBytes`) |
| One part of a body (a note, report or message) | 1,024 bytes (`RelayLimits.MaxSearchFieldBytes`) |
| Title | 256 bytes (`RelayLimits.MaxSearchTitleBytes`) |
| Words per query | 16 (`RelayLimits.MaxSearchQueryTerms`) |
| Hits per search | 10 (`RelayLimits.MaxSearchResults`) |

Reports and messages share the body, newest first, so the cap drops the oldest of either.

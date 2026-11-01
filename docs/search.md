# Search

The `search` tool finds tasks, notes and your own inbox messages by words. It returns IDs and short snippets; open the one you need with `task_get`, `context_get` or `check_inbox`. The index is written in the same transaction as the state it describes, so it never disagrees with committed state.

## Use it

```json
{ "repositoryId": "repo", "sessionId": "...", "sessionToken": "...", "query": "refresh timeout T-118" }
```

| Argument | Required | Meaning |
| --- | --- | --- |
| `query` | yes | Words to find, at most 256 characters. |
| `kinds` | no | Any of `task`, `context`, `message`. Default: all. |
| `status` | no | Only hits with this status, such as `ready`, `blocked`, `completed`, `open`, `note` or `unread`. |
| `limit` | no | Most hits, 1 to 10. Default 3. |

Reply:

```json
{ "hits": [ { "id": "T-118", "k": "task", "s": "completed", "at": "2026-10-29", "x": "…token [refresh] [timeout] after…" } ] }
```

- `b` names the board when a hit is not on `default`; pass it as `boardId` to `task_get`.
- The reply stays under 2,048 bytes. When hits had to be dropped to fit, it carries `"truncated": true`.
- A session is required. You only ever see your own repository and your own inbox.
- `SearchUnavailable` means the hub keeps no index.

## Turn it off

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Search:Enabled` | true | Keep the search index. Off empties it and the tool answers `SearchUnavailable`; on again rebuilds it at the next start. |

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
- Snippets show matches in `[brackets]`.

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
| Hits per search | 3 by default, 10 at most (`RelayLimits.DefaultSearchResults`, `MaxSearchResults`) |
| Reply | 2,048 bytes (`RelayLimits.MaxSearchReplyBytes`) |
| Query | 256 characters (`RelayLimits.MaxSearchQueryLength`) |

Reports and messages share the body, newest first, so the cap drops the oldest of either.

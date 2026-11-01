# Search

Search is being built in steps. Today the hub only knows what text is searchable. Nothing is indexed yet, and there is no `search` tool yet, so nothing changes for agents.

## What will be searchable

| Source | One document per | Text | Who can find it |
| --- | --- | --- | --- |
| Board | Task | Title, packet goal and target, blockers, `dependsOn`, `packet.resources`, `facts.notes`, the latest 20 reports and messages | Sessions in the repository |
| Board | Context note | `content` | Sessions in the repository |
| Board | Standing line or grant | Line text; grant scope, targets and expiry | Sessions in the repository |
| Session | Unread inbox message with text | Message text | Only that session |

- A task's title is `facts.title` when set, else the packet `goal`, else the task ID.
- Pending standing proposals are not searchable.
- Acknowledged messages drop out. Task-change notifications have no text, so they are skipped; the task itself is searchable.
- Locks, the control record and discovery give nothing.

## Limits

| Limit | Value |
| --- | --- |
| Body per document | 4,096 bytes (`RelayLimits.MaxSearchBodyBytes`) |
| One part of a body (a note, report or message) | 1,024 bytes (`RelayLimits.MaxSearchFieldBytes`) |
| Title | 256 bytes (`RelayLimits.MaxSearchTitleBytes`) |

Newer reports and messages come first, so the cap drops the oldest.

Fields named like a credential (`token`, `secret`, `password`, `apiKey`, `privateKey`, `authorization`, `cookie` and similar) are never indexed. Do not put secrets in task text anyway.

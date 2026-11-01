# Standing preferences and grants

Each board can hold settled rules and permissions. They are stated once and sent to each chat only when they change, so new chats need no re-briefing and settled permissions are not asked for again.

Nothing changes until an administrator writes standing for a board. A board without standing behaves as before.

## What a board holds

- **Lines**: up to 30 short conventions of at most 200 characters, such as "Never deploy CSS without a restoration receipt." A line is never a permission.
- **Grants**: up to 30 typed permissions. Each has an ID, a `scope` (`repository`, `resource` or `action`), `targets`, and a required `expiresAt`. The hub records who granted it and when.
- **Settings**: `requireResources` (see [dependencies.md](dependencies.md)).
- **Revision**: goes up by one when a line, grant or setting actually changes. A put that changes nothing, or only approves an identical line or rejects a proposal, keeps the revision.

## Change standing (administrators)

`standing_put` needs an administrator token and the current `expectedRevision` (0 for a new board). One call can do several things:

```json
{ "repositoryId": "repo", "requestId": "r-1", "expectedRevision": 3,
  "lines": [{ "id": "css", "text": "Never deploy CSS without a restoration receipt." }],
  "grants": [{ "id": "rig", "scope": "resource", "targets": ["rig-1"], "expiresAt": "2026-12-01T00:00:00Z" }],
  "remove": ["old-line"],
  "approve": ["p-1a2b3c4d5e6f"],
  "settings": { "requireResources": true } }
```

- `lines` and `grants` add an item, or replace the one with the same ID. Line and grant IDs must differ.
- `remove` takes line, grant or proposal IDs. Removing a proposal rejects it.
- `approve` turns pending proposals into lines or grants. A proposal whose ID matches an existing line or grant is an edit: `task_get` shows it with `replaces: true`, and history records it with `~`.
- A repository grant may leave out `targets`; it then covers this repository. It cannot name another repository.
- A stale revision returns `VersionConflict` with the current revision.

## Propose standing (any session)

`standing_propose` takes a `line` (`{id?, text}`) or a `grant` (`{id?, scope, targets, expiresAt}`) and returns a `proposalId`. A proposal changes nothing until an administrator approves it. It does not change the revision and is not sent to chats. A board keeps up to 20 pending proposals, and at most 5 from one session.

## Read standing

`task_get` with `section: "standing"` and no `taskId` returns everything: lines with author and approval, grants (expired ones marked `expired`), settings, pending proposals, and the last 20 changes, newest first. History marks changes `+` added, `~` edited and `-` removed.

The dashboard's **Standing** page (`/dashboard/standing`) shows the same, read-only, for up to 20 boards. It refreshes every 15 seconds.

## How chats receive it

- `session_open` returns `standing` when the board has any: `revision`, `lines` (ID and text), active `grants`, and `settings`.
- The session remembers the revision it received. Later `task_update`, `action_report` and `task_claim` replies carry `standing` only when the board's revision is newer.
- `check_inbox` never carries it, because background inbox reads are not seen by the model.
- If a reply would be too large, it carries only `{"revision": N, "detailFetchRequired": true}`, and the full text comes with a later reply.
- Standing counts as received when the reply carrying it is sent. If that reply is lost, the chat gets the text again only at the next revision; it can read it any time with `task_get`.
- Expired grants are left out of what chats receive. Expiry does not change the revision, so a chat is not told when a grant lapses; each grant carries its `expiresAt`.
- The hub reuses a board's standing for up to 5 seconds. A write through the same hub is seen at once, and a read that started before the write cannot bring back the older standing.

## Grants in readiness

A packet's `authorityRef` can name a grant as `grant:<id>`. Then a `ready` write is refused with `GrantInvalid` unless the grant:

- exists,
- has not expired, and
- covers the task: a repository grant for this repository, or a resource grant whose targets include every ID in `packet.resources`.

`reason` in the reply is `unknown`, `expired` or `scope`. An action grant never covers a task, because a task names no action class. Once a grant expires or is removed, tasks that name it are no longer offered. Any other `authorityRef` text is kept as a plain reference, as before.

## Who is an administrator

The token's principal must have `IsAdministrator` (`Relay:Clients:N:IsAdministrator`, or the OAuth mapping). The `role` given at `session_open` does not grant it. Give the manager chat an administrator token only if it should change standing.

## Limits

| Limit | Value |
| --- | --- |
| Lines | 30 of 200 characters (`MaxStandingLines`, `MaxStandingLineLength`) |
| Grants | 30, with up to 16 targets each (`MaxStandingGrants`, `MaxGrantTargets`) |
| Line, grant and proposal IDs | 64 characters (`MaxStandingIdLength`) |
| Pending proposals | 20 per board, 5 per session (`MaxStandingProposals`, `MaxStandingProposalsPerSession`) |
| History | 20 entries of up to 20 changes (`StandingHistoryEntries`, `StandingHistoryChanges`) |
| Text sent to a chat | 12,288 bytes (`MaxStandingBytes`) |

# Why hub mail does not wake an idle Codex chat, and the unresponsive row

Note only. Read against main on 2026-11-05. Paths are under `src/CoCern.Adapter/` unless shown otherwise.

**Correction.** The first version of this note said no Codex chat was watched, that Codex chats had no handles, and that local wake setup was failing silently. All three were wrong. The check looked in the wrong folder, as described below. The fixes it led to are still useful, for a different reason.

## Finding

Idle Codex chats are unwatched after every adapter restart. `LocalChatWake`'s `observed` set is in memory, so a restarted adapter has no watcher for a chat until that chat next calls the hub. A deploy restarts every backend, and there were many deploys on the day of the stalls. This fits "wakes only when something pokes it", but it is inferred: no real idle chat has yet been shown waking or failing to wake because of it.

The dashboard's "No direct chat route" is not the cause. It is set only from node announcements, and `LocalChatWake` never announces, so on the hub PC it reads false even for a working route (`SessionInspector.razor:11`, `ReadModels.cs:85`, `SqliteStore.Observation.cs:291`, from reading).

## Where local wake state lives

The Claude and Codex desktop apps are packaged, so Windows redirects AppData writes by them and their child processes, including MCP adapters. Each client sees its own view of `%LOCALAPPDATA%\CoCern\local-wake`:

- Codex: `%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\CoCern\local-wake`
- Claude: `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\CoCern\local-wake`

A file missing from one client's view says nothing about another client. Check `status\<pid>.json` in the right folder. Status files hold no secrets; session handles hold tokens, so list their names only.

## How it works

1. The hub stores the message, raises an attention item and sends a hint. It dispatches nothing.
2. On the hub PC, each adapter creates a `LocalChatWake` when `COCERN_TOKEN_FILE` is set (`Program.cs:199`).
3. Any successful session call goes through `LocalChatWake.Observe`, which writes a session handle under `sessions\` and marks the session observed. `SessionHandoff.Restore` recreates a missing handle on the next call.
4. The locator maps the handle to a Codex thread by scanning rollouts, since Codex exports no chat ID (`AdapterOptions.cs:107`).
5. A watcher runs per thread, with its state in `wake-<thread>.db`, and calls `codex queue` (`CodexWakeDispatcher.cs:34`).

## Evidence

Observed in Codex's package folder at 03:19Z on 2026-11-05, about five minutes after a deploy:

| Check | Result |
|---|---|
| Session handles | Three, including root and the reviewer |
| `wake-*.db` | Two |
| Codex adapters reporting status | Six, all on the deployed build |
| Status `watching` | One adapter, root's, after root's call at 03:14:25Z |
| Status `no_observed_sessions` | Five, each with `rearm_skip_not_armed_here` |
| Armed records | One, root's |
| Codex `queue_1.sqlite` | 0 queued items, so a stuck queue item isn't the cause now |

## Not established

- That restarts are the whole cause. A real idle chat needs to be sent wake-on mail while watched, and the reply correlated.
- The second PC, which uses a node rather than `LocalChatWake`.
- Other stop points found by reading, which may also apply: mail carrying a `taskId` isn't direct, so it waits for the 10-minute hold (`src/CoCern.Domain/SessionAttention.cs`); and a handle never located within 30 minutes stays unmapped (`Node/ChatLocators.cs`).

## What was built

- Restart re-arm: a backend swapped under the same launcher re-arms the chats its launcher had armed, if active within 24 hours. Armed records exist only since that build, so the first deploy after it could not re-arm anything; the next one is the first that can. A restart of Codex itself still re-arms nothing, by ruling, and those chats wait for their next call.
- Status files and fixed reason codes, which is how this correction was found.
- Newest-unread wake candidate, so one wake that never ran can't block newer mail.

## Must be proven on a real Codex client

- A `codex queue` item starts a turn on a truly idle thread on the hub PC, in this version. Count only a correlated reply as success, not exit 0.
- The queue item drains.
- The adapter stays alive while idle. Adapters from 18:04Z were still running at 00:45Z, which is encouraging, not proof.
- Which adapter serves which thread, since Codex exports no chat ID.

## The unresponsive row

Proposed addition to the Waiting-on-you contract, as one new kind. The shape is not frozen and not compiled.

- **Kind:** `unresponsive`, in `Items` rather than `Maybe`, because a stalled chat is what this alert exists to catch.
- **Rule:** the session holds unread wake-on mail older than N minutes, and its last non-background call is older than N minutes. N defaults to 10.
- **Limit:** only sessions with a non-background call in the last 24 hours, so abandoned chats don't fill the strip. They count toward the existing `Items` cap of 50.
- **Fields:** `SessionId`, `Label`, `Since` set to the oldest such message's time, `Text` a fixed hub-written line with the counts and never message content.

Source of the time: `activity_log`, written about once a second, which `DashboardSession.LastActivityAt` already reads per session (`SqliteStore.Activity.cs:29`, `SqliteStore.Observation.cs:135`). That value includes watcher polling, so it cannot be used as it stands: a stalled chat with a live watcher looks active. The query needs `background_call = 0`. Its cost past many background rows, and the handling of unclassified NULL rows, are not measured. Root's origin tags can replace this filter once they land.

The hub records no model-turn time. A non-background hub call is the closest signal, so a chat working for 10 minutes without calling the hub while holding mail will show as unresponsive. Word the row as "no hub call", not "stuck".

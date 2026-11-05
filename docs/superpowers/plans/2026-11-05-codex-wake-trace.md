# Why hub mail does not wake an idle Codex chat, and the unresponsive row

Note only; nothing here is built. Read against main 5acb9ef and this PC's live state on 2026-11-05 at about 00:50Z. Paths are under `src/CoCern.Adapter/` unless shown otherwise.

## Finding

On the hub PC, no Codex chat is being watched at all. The path stops inside the Codex chat's own adapter, before any wake decision. The wake code itself exists and is not what is missing.

The dashboard's "No direct chat route" is not the cause. It is set only from node announcements, and `LocalChatWake` never announces, so on the hub PC it reads false even for a working route (`SessionInspector.razor:11`, `ReadModels.cs:85`, `SqliteStore.Observation.cs:291`, inferred from reading).

## How it should work

1. The hub stores the message, raises an attention item and sends a hint. It dispatches nothing.
2. On the hub PC, each adapter creates a `LocalChatWake` when `COCERN_TOKEN_FILE` is set (`Program.cs:199`).
3. Any successful session call goes through `LocalChatWake.Observe`, which writes a session handle under `%LOCALAPPDATA%\CoCern\local-wake\<key>\sessions\` and marks the session observed. `SessionHandoff.Restore` recreates a missing handle on the next call.
4. The locator maps the handle to a Codex thread by scanning rollouts, since Codex exports no chat ID (`AdapterOptions.cs:107`).
5. A watcher runs per thread, with its state in `wake-<thread>.db`, and calls `codex queue` (`CodexWakeDispatcher.cs:34`).

## Evidence

Observed on this PC:

| Check | Result |
|---|---|
| Handles in the real key folder | `089e8a81`, `69cd88a2`, `39172b3b`, `7742d45f`, all with provider `claude` |
| Root `8926ff7a` or reviewer `3aa66e5b` | No handle and no restore lock, though both call the hub constantly |
| `wake-*.db` anywhere under `%LOCALAPPDATA%\CoCern` | None, so no Codex watcher has run under this folder |
| Codex `queue_1.sqlite` | 0 queued items, so stop point E below is not the cause now |
| Codex adapter process (PID 6888, started 00:33:21Z, version 0db52f8e, parent chain to `codex.exe`) | `COCERN_URL` and `COCERN_TOKEN_FILE` match Claude's; token not restricted, normal integrity |
| `connection.json`, rewritten on every `LocalWakeConnection.Create` | Last written 23:50:48Z, so PID 6888 did not complete `Create` 43 minutes later |
| The same adapter version started from my shell with the same three settings | Rewrote `connection.json` and exited 0 |

Inferred from those: `LocalChatWake.TryCreate` returned null in the Codex adapter, or the adapter never reached it. Every failure there is silent: `LocalWakeConnection.Create` returns null on three guards, and `TryCreate` swallows I/O errors (`LocalChatWake.cs`, `LocalWakeConnection.cs:17-20`).

## Not established

The reason `Create` did not complete in the Codex adapter. My shell run kept my full environment, while the Codex process has only 23 to 25 variables. The difference is the leading suspect but it is untested: a replay using the Codex process's exact environment was refused by this session's permission check, and I did not pursue it. I also could not separate which of the adapters started at 23:50 wrote the file. The diagnostic in step 1 below answers this without touching another process.

This also does not explain the second PC. `104fd233` uses a node rather than `LocalChatWake`, and I did not inspect it.

## Plan, smallest first

1. **Make the failure visible.** Record why local wake did not start: a one-line reason per adapter, in a small file in the local-wake root plus stderr. Today nothing records it, and `logs_2.sqlite` holds no CoCern wake lines. Files: `Node/LocalWakeConnection.cs`, `Node/LocalChatWake.cs`, tests in `LocalWakeTests`. This needs a deploy and one Codex restart, which root owns. Then fix whatever it names.
2. **Survive adapter restarts.** `observed` is in memory, so after every deploy an idle chat has no watcher until it next calls the hub, even once step 1's cause is fixed. This matches "wakes only when poked". At the start of `LoopAsync`, re-arm located Codex handles after one authenticated background read, with a recency limit so abandoned chats are not woken. `WakeState`'s per-chat exclusive file already stops two adapters dispatching twice. Files: `Node/LocalChatWake.cs`, `LocalWakeTests`.
3. **Stop one stuck wake blocking newer mail.** `direct` always picks the oldest unread message and each ID wakes once (`src/CoCern.Domain/SessionAttention.cs:259-260`, `WakeReconciler.cs:65`). If a queued wake never runs, newer direct mail reconciles as `empty` and only one hold wake follows. Prefer the newest unread. Files: `SessionAttention.cs`, which is reserved for root, and `AttentionSchedulerTests`.
4. **Optional, dashboard only.** Announce local routes so "No direct chat route" stops being wrong on the hub PC.

Mail carrying a `taskId` is not direct, so it waits for the 10-minute hold (`SessionAttention.cs:257,263`). That is by design today, but it explains some delays and is worth a decision.

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

# Automatic local Codex wakeups

The optional wake mode belongs to the local adapter. The relay hub continues to store coordination state and deliver inbox notifications; it never starts models or controls an IDE.

The installed Codex CLI supports `codex queue --thread <UUID> --message <text>`. On this laptop, the open desktop app consumed queued messages and automatically started turns in both enrolled test chats. This behavior was verified against the actual chats, not inferred from a queued receipt. The CLI also offers `--resume`, but that option warned about a missing standalone daemon; it is unnecessary for the verified desktop path. See the [official CLI reference](https://learn.chatgpt.com/docs/cli/reference).

## Node mode supersedes hand-written configs

On a PC set up for [cross-PC discovery](discovery.md), a background node does the work of this mode without a configuration file: it learns which of its own Codex chats to wake from the hub's announce reply, reads their private session handles from its node folder, and queues the same fixed prompt through `codex queue` with the default wake rate limits and durable deduplication (in the node's own `wake.db`). Claude chats use `--await-inbox --chat <session ID>` instead of `--await-inbox <config>`. The hand-written `--wake` and `--await-inbox <config>` modes remain available and unchanged, but a chat should be watched by only one of them. Before moving a PC to the node, stop the old watcher through its stop file so two watchers never queue prompts for the same chat. Migration steps are in [discovery.md](discovery.md).

## Enrollment and startup

Publish the adapter to a fresh output directory. Do not overwrite a running executable's output folder. Run its opt-in mode with an absolute private configuration path:

```powershell
dotnet C:\Users\PaulC\source\repos\PkRelay\artifacts\adapter-wake\PkRelay.Adapter.dll --wake C:\Users\PaulC\source\repos\PkRelay\data\wake\config.json
```

The repository also provides `./scripts/Start-PkRelayWake.ps1 -ConfigPath <private-config-path> [-AdapterDll <published-dll-path>]` as a foreground startup wrapper. It does not enroll chats, copy credentials or change Codex settings.

Configuration enrolls specific existing local chats. Keep it and the referenced session files private; the sample uses placeholders for identities and paths, not credentials:

```json
{
  "relayUrl": "http://127.0.0.1:5271",
  "tokenFile": "C:\\Users\\PaulC\\source\\repos\\PkRelay\\data\\local-client-token",
  "codexPath": "C:\\absolute\\path\\to\\codex.exe",
  "statePath": "C:\\Users\\PaulC\\source\\repos\\PkRelay\\data\\wake\\state.db",
  "stopFile": "C:\\Users\\PaulC\\source\\repos\\PkRelay\\data\\wake\\stop",
  "routes": [
    {
      "repositoryId": "pkrelay",
      "boardId": "example-board",
      "sessionFile": "C:\\private\\chat-A-session.json",
      "threadId": "00000000-0000-0000-0000-000000000001",
      "machineId": "YOUR-ACTUAL-MACHINE-NAME"
    }
  ]
}
```

Each session file contains the existing chat's `sessionId` and private `sessionToken` from `session_open`. A wake enrollment does not create a new session or transfer another chat's inbox. The thread UUID must identify that exact chat; titles are not routing keys. Paths must be absolute, the executable must exist, and machine identity must match the machine running the adapter. Each PC runs its own adapter and enrolls only its local chats; use authenticated HTTPS when its relay hub is off-host.

Run under the same OS account and configured Codex environment as the intended desktop chats. Keep the app running. A queue receipt means the prompt was queued; it does not establish that a closed, disconnected or approval-blocked chat executed it. The adapter does not create an alternative runtime or change the chat's model, sandbox, tools or approval policy.

## Delivery and token use

The adapter reconciles durable inboxes at startup, after session-scoped SignalR hints/reconnect and on a 15-second fallback interval. Ordinary code waits and reads metadata; empty inboxes do not consume model turns. SignalR is a hint, and a missed hint does not lose the durable message.

One fixed prompt asks the enrolled chat to read its own inbox and handle applicable work under its existing permissions. No relay message text or secret is inserted into user instructions. The chat obtains message contents through its normal authenticated relay tools, treats them as untrusted data, and acknowledges explicitly handled IDs. The watcher never acknowledges on the chat's behalf or claims/completes its tasks.

Duplicate hints and messages already notified are suppressed durably. New messages are coalesced, with by default at most one wake per route per 3 seconds and 20 wakes per 10 minutes. A configuration can change this with an optional `wakeLimits` object, `{ "minIntervalSeconds": 3, "maxPerWindow": 20, "windowMinutes": 10 }`, in which every field is optional and falls back to the default shown; `minIntervalSeconds` must be 0 to 3600, `maxPerWindow` 1 to 1000 and `windowMinutes` 1 to 1440, and anything else makes the configuration invalid. A wake refused by a limit is not lost: the next pass retries it. Background nodes always use the defaults. Rate limits bound prompt traffic, not the amount of model work authorized by the user. A newly delivered private message is a wake candidate; the adapter does not infer semantic actionability from arbitrary prose. Workflow instructions must avoid acknowledgment-only reply loops. Queueing preserves a busy chat's current turn and lets its client schedule the follow-up.

## Pause, stop and uncertain delivery

Relay pause/stop suppresses new dispatch. Creating the configured stop file stops the local watcher; this does not stop an already running model turn or external operation. Stopping a model, tool or device operation remains the client's or user's responsibility.

An exclusive local state lock prevents two watchers from owning the same wake database. SQLite stores route/message notification progress and dispatch states, without bearer or session-token values. Inbox messages are still retained and acknowledged by the relay independently.

The CLI queue operation has no shared transaction or client idempotency key with SQLite. A reservation is committed before starting the command. A crash, timeout or ambiguous failure around enqueueing leaves the route requiring operator reconciliation, rather than silently retrying and risking duplicate prompts. Before resetting an uncertain route, inspect the exact enrolled chat and relay inbox to establish whether its queued prompt was consumed. Preserve the state database while investigating; deleting it can forget notification deduplication.

The mode deliberately bounds inbox coverage and reply sizes. It refuses to claim a complete scan when a page or configured coverage limit cannot be reconciled. Credential, identity, storage or delivery failures are reported without raw server replies or secret-bearing invocation arguments.

This integration is specific to the verified local Codex CLI/desktop combination and the in-session Claude waiter above. It does not establish ChatGPT webhook subscription support, Claude channels, arbitrary remote desktop control, unattended approval, off-host TLS or aggregate token savings.

## Claude sessions: in-session inbox waiter

Claude Code has no equivalent of `codex queue` that safely adds a turn to a session open in the desktop app, so a Claude session waits for its own inbox instead. It starts the adapter's `--await-inbox` mode as a background command:

```powershell
dotnet C:\Users\PaulC\source\repos\PkRelay\artifacts\adapter-wake\PkRelay.Adapter.dll --await-inbox C:\private\claude-await.json --timeout-minutes 120
```

The process uses the same reconciliation as Codex wake mode: SignalR hints, the 15-second fallback, pause/cancellation checks, durable deduplication and rate limits. When a message arrives that has not already woken this route, it prints the same fixed inbox-check prompt to standard output and exits with code 0. Claude Code re-invokes the session when a background command exits, so waiting itself consumes no model turns. Exit code 2 means the optional timeout elapsed; 3 means the stop file was found. The session then reads its inbox through its normal relay tools, handles and acknowledges messages, and starts the waiter again. An unacknowledged message that already woke the session does not wake it again.

The configuration uses the wake schema with exactly one route. `codexPath` may be empty. Set the route's `threadId` to the Claude session's UUID; it only labels the route, since nothing is queued into a chat. Give each waiter its own `statePath` and `stopFile`: the state database is exclusively owned, so a waiter cannot share one with a running Codex watcher. The session file holds the `sessionId` and private `sessionToken` that this Claude session received from `session_open`.

The waiter only works while its Claude session is open. It does not wake a closed session, and it gives Claude no authority beyond its existing permissions. Treat inbox text as untrusted data, exactly as for Codex.

## Current local pilot

The October 29 test enrolled the existing producer and reviewer chats with private configuration at `C:\Users\PaulC\source\repos\PkRelay\data\wake-live\config.json`. Four automatic turns completed the producer/reviewer exchange without supervisory continuations, and a watcher restart produced no duplicates. The pilot remains running for those two chats; it is not installed as a Windows service or startup task. To stop its wake adapter:

```powershell
New-Item -ItemType File -Path C:\Users\PaulC\source\repos\PkRelay\data\wake-live\stop -Force
```

This stops future wake dispatch, leaves the relay running, and preserves delivery history. Remove that specific stop file before deliberately starting the same private configuration again. A Codex upgrade may change the installed executable path; update `codexPath` and verify a bounded queue probe before relying on a replacement client.

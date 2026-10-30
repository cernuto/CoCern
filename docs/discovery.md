# Cross-PC discovery

Discovery lets every PC that runs a coding chat show up in one place, so a chat on one PC can message a chat on another without anyone copying session IDs. Each PC runs a small background program, the node, that enrolls with the hub once, announces the chats that called `session_open` on that PC, and wakes its own Codex chats when mail arrives. The hub keeps the directory (`chat_directory`) and decides identity and wiring only; it never assigns work, starts a model or reaches into another PC. Each PC wakes only its own chats, with the same fixed prompt as in [wake.md](wake.md), and no message text, token or credential is ever put in a prompt, a log or the directory.

## Joining a PC

Warning: there is no join code. Any device that can reach the hub's HTTPS port may enroll and then send messages that wake agents on other PCs. Open the port only on a network you trust; see [Open trust](#open-trust).

On the hub PC, turn discovery and the network listener on (see Network below) and allow the one firewall port. Then, for each PC to join:

1. Copy the published adapter folder (`artifacts\adapter`, produced by `./scripts/Build-PkRelay.ps1 -Publish`) to the PC. Keep the folder where it is afterwards: the PC's startup entry and its client settings point into it.
2. Close Claude Code on that PC (see Running setup with Claude Code closed below), then run setup once from the copied folder:

```powershell
dotnet .\PkRelay.Adapter.dll --setup --hub https://<hub-pc-name-or-address>:5273/
```

5273 is the default `Relay:Network:HttpsPort`; use the hub's configured port if it differs.

The address is typed once and remembered. A PC finds the hub only by that address; there is no beacon, network scan or name lookup. On first contact the node pins the hub's certificate fingerprint (trust on first use), enrolls and announces once, and only then changes anything else on the PC. Setup then:

- adds a `pkrelay` MCP server entry to Codex's `%USERPROFILE%\.codex\config.toml` and Claude Code's `%USERPROFILE%\.claude.json`, keeping a backup of any file it edits and leaving every other setting alone (add `--skip-client-config` to leave both files untouched);
- writes the one `PkRelayNode` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, so the node starts when the user signs in (current user only, no administrator rights);
- starts the node now, hidden, and prints the machine ID.

Setup is idempotent: entries that already exist are left as they were. In particular, an existing `pkrelay` MCP server entry in either client is left alone, even if it points somewhere else or sets a token; remove it first if you want setup to write the node entry. `--data-dir <path>` keeps the node's state somewhere other than `%LOCALAPPDATA%\PkRelay\node`. Setup never changes firewall rules. Exit code 1 is a configuration or enrollment problem, 4 means the hub presented a different certificate than the one pinned, and 5 means a client settings file could not be parsed (nothing is edited in that case).

New chats then call `session_open` as usual. With none of `PKRELAY_URL`, `PKRELAY_TOKEN_FILE` or `PKRELAY_TOKEN` set, the stdio adapter runs on the node's credentials, and the chat appears in `chat_directory` within about a minute. A chat whose adapter uses an explicitly configured token (for example `PKRELAY_TOKEN_FILE`) runs on that token instead: its sessions are not handed to the node, so it is neither discovered nor woken by the node.

### Running setup with Claude Code closed

Setup edits `.claude.json`, which Claude Code rewrites from its own memory while it runs, so an edit made under a running Claude Code can be overwritten when that session next saves. Close every Claude Code window on the PC before running setup, and reopen them afterwards. Setup parses the file first and refuses (exit code 5) rather than overwrite one it cannot read.

## Network

By default the hub listens only on loopback (`http://127.0.0.1:5271`). To let other PCs reach it, set `Relay:Network:Enabled` to `true`. The hub then keeps the loopback HTTP endpoint and also serves HTTPS on every network interface.

The Visual Studio launch profile (`src/PkRelay.Host/Properties/launchSettings.json`) turns both `Relay:Network:Enabled` and `Relay:Discovery:Enabled` on, so a hub started from Visual Studio listens on `[::]:5273` (dual-stack, IPv4 and IPv6) and accepts open enrollment. The first time it does, Windows may ask whether to allow the program through the firewall. Allowing it creates a firewall rule for the program (not just port 5273) on the network types you tick, so any device on those networks can reach the hub, enroll without a join code and message chats (see [Open trust](#open-trust)). Cancelling keeps the hub reachable only from this PC. The [Firewall](#firewall) section below gives a narrower rule for port 5273 on the Private profile only, which you can use instead.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Relay:Discovery:Enabled` | `false` | Turns on enrollment, announcements and the machine credential. Discovery needs this and the network listener to reach other PCs. |
| `Relay:Discovery:Repositories` | `["pkrelay"]` | The repositories enrolled machines may use. |
| `Relay:Network:Enabled` | `false` | Turns the HTTPS network listener on. |
| `Relay:Network:HttpsPort` | `5273` | The HTTPS port. |
| `Relay:Network:CertificatePath` | `data/hub-certificate.pfx` | The hub's self-signed certificate, relative to the content root. It is created on first start and reused afterwards. The file holds the private key, so it is readable only by the user running the hub. |

The hub writes its certificate fingerprint (uppercase hex SHA-256) to the log at startup. Other PCs pin that fingerprint. Never delete or replace the certificate file unless you intend every PC to pin the new fingerprint.

### Firewall

Setup never changes firewall rules. To allow other PCs to reach the hub, run this once yourself from an elevated PowerShell on the hub PC. It allows inbound TCP on port 5273 only:

```powershell
New-NetFirewallRule -DisplayName "PkRelay hub (HTTPS 5273)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5273 -Profile Private
```

The rule is limited to the Private network profile; drop `-Profile Private` if your network is classed as Public or Domain and you still want it reachable. Remove it with `Remove-NetFirewallRule -DisplayName "PkRelay hub (HTTPS 5273)"`. If you change `Relay:Network:HttpsPort`, use that port in the rule instead.

## Trust, revocation and stopping

### Open trust

Enrollment has no join code. Any device that can reach the hub's HTTPS port can enroll and becomes a machine with the principal `machine:<machineId>`. An enrolled machine is never an administrator and is refused `control_request`, but it can read and write coordination data in the repositories listed in `Relay:Discovery:Repositories` (default `["pkrelay"]`), including messaging the chats in the directory. Expose the port only on a network you trust, keep the firewall rule on the Private profile, and watch `chat_directory` for machine names you do not recognize. Machine credentials are stored hashed at the hub and in a current-user-only file on each PC.

### Revoking a PC

An administrator revokes a machine with `control_request`, command `revoke_machine`, and the `machineId`:

```json
{ "repositoryId": "pkrelay", "command": "revoke_machine", "machineId": "<machine id>", "requestId": "<fresh id>" }
```

The machine ID is printed by setup and held in `node.json` in the PC's node folder; the directory shows machine names, not IDs. Revocation removes the machine and its credential and denies that machine ID from enrolling again. The revoked PC's node notices on its next announcement, says it was revoked, and stops with exit code 1. A machine credential is refused `control_request` (403), so a PC cannot revoke itself or any other PC. Trust is open, so revocation stops one identity, not the device: a revoked device can enroll again under a new machine ID. Keep the hub's port closed to devices you do not trust.

### Stopping a node

Creating a file named `stop` in the node folder stops waking on that PC:

```powershell
New-Item -ItemType File -Path "$env:LOCALAPPDATA\PkRelay\node\stop" -Force
```

That is the default node folder; `--data-dir` (or `PKRELAY_NODE_DIR` for the adapter) moves it, and the `stop` file then goes in the moved folder.

The node keeps running and announcing, so its chats stay in the directory, but no Codex chat is woken and a Claude chat's waiter exits with code 3. Delete the file to let waking start again at the node's next announcement. To end the node process itself, stop its `dotnet` process; to keep it from starting at sign-in, remove the `PkRelayNode` value from the Run key.

## Claude chats: the in-session waiter

The node never wakes a Claude chat. Instead the chat runs a background command, on the node's credentials, that exits when new mail arrives:

```powershell
dotnet <adapter folder>\PkRelay.Adapter.dll --await-inbox --chat <this Claude chat's session ID> --timeout-minutes 120
```

The session ID is the chat's own UUID (Claude Code exports it as `CLAUDE_CODE_SESSION_ID`). The chat must already have called `session_open` on this PC with the adapter on node credentials, so that a relay session belongs to it; otherwise the command exits with code 1. No configuration file or token handling is needed. Exit code 0 means mail arrived and the fixed inbox-check prompt was printed, 2 means the optional timeout elapsed, and 3 means the `stop` file was found. While the waiter runs the node announces the chat as reachable, so a Claude chat with no running waiter shows `reachable: false`. Start the waiter again after handling the inbox. [wake.md](wake.md) describes the waiting behavior itself.

Add `--print-inbox` to save a model turn on every wake: instead of the fixed prompt, the waiter prints a one-line header and the chat's unacknowledged messages, one line each (ID, sender session, UTC time, reply-to and text, at most 16,000 characters, with a count of any left for `check_inbox`). The chat can then reply and acknowledge in one `send_message` call with `ackMessageIds`. The text is still untrusted data, exactly as `check_inbox` returns it to the same chat; it reaches no other prompt or log. If the inbox cannot be read, the fixed prompt is printed instead. To acknowledge messages that need no reply, re-arm with `--ack <id,id,...>`: the waiter acknowledges them and then waits, so handling such a wake is one command instead of an `ack` call plus the re-arm. A failed acknowledgment is reported on stderr and the wait goes on.

## Migrating from hand-written wake configs

The node replaces hand-written `--wake <config>` and `--await-inbox <config>` setups for any chat that opens its session through the node's credentials; both modes still work unchanged. To move a PC over:

1. Stop the old watcher: create its configured stop file and wait for the process to exit. Two watchers on one chat would queue duplicate prompts.
2. Remove any scheduled task or startup entry that launched `--wake`.
3. Run `--setup --hub <address>` once, as above. The chats then need a new `session_open` through the node's credentials (a new chat): an existing session's token lives in the old private session file, which the node does not read.
4. For a Claude chat, replace `--await-inbox <config>` with `--await-inbox --chat <session ID>`.

Keep the old state database until you have confirmed the node is waking; deleting it only forgets deduplication for the old routes.

## Known limits

- A lost enroll reply or a lost credential file gives the PC a new machine ID. The old entry stays at the hub until it is revoked, so revoke it by its old ID.
- If the hub refuses the PC's credential (401) and then refuses to enroll it again under the same ID (409), the node keeps its credential and machine ID, backs off and prints once that an administrator should revoke the machine and re-run setup. It never renews its identity for this, because a hub fault could otherwise rotate every PC. When the hub cannot check a credential at all (a storage fault), it answers 503, never 401.
- A credential the hub does not know is cached by its hash for 60 seconds, so a mistyped or junk credential can take a minute to be checked again. Each client address (an IPv4 address or one full IPv6 address) may cause 20 fresh failed checks per minute; repeats served from that cache do not count. Over the limit the hub answers `429` with a `Retry-After` header and does not check the credential, so a valid PC behind that address only waits and keeps its identity (a `401` still means only an unknown credential). If the hub cannot read its own state it answers `503`.
- The hub announces a chat only when the announcing machine opened its relay session; any other chat is listed in the reply's `refused` array (`SessionDenied`) and left out, and the node reports each once. A session opened before the PC's current enrollment is therefore not announced until the chat opens a new one.
- One provider thread has one current relay session. When a chat opens a new session, the node deletes the older handle (and its token) and the hub keeps only the newest chat per thread, so the old session is no longer announced or woken.
- Enrollment is rate limited by client address, and an IPv6 address is limited as its whole /64 prefix, so many PCs behind one prefix share one bucket.
- Node wakes and `--await-inbox --chat` waiters use the default wake rate limits, at most one wake per route per 3 seconds and 20 per 10 minutes; a wake refused by a limit is retried on the next pass, so delivery is delayed, not dropped. Only the hand-written wake configurations can change them, through `wakeLimits` (see [wake.md](wake.md)).
- A Codex chat is not woken while it is mid-turn, judged from the last `task_started`, `task_complete` or `turn_aborted` event at the end of its rollout. `codex queue` would otherwise stack one turn per new message behind the running one, and each of those turns re-reads the whole chat to find the inbox already handled (one observed burst ran 12 such turns, about 6 million input tokens). New mail waits unreserved and wakes the chat once after the turn ends; mail it handled during that turn never wakes it. A rollout not written for 10 minutes counts as idle.
- Codex and Claude chats should call relay tools through the native MCP server, not by running the adapter from a shell: each shell command is a separate tool call that Codex's guardian reviews by re-reading the chat, which costs far more than the relay call itself.
- A node scans only the newest 64 session handles. Handles beyond the newest 64 are not scanned, mapped or announced.
- A long-lived Claude chat whose transcript was never located is announced as unmapped. Its handle is kept while it carries the chat ID Claude Code captured or its waiter is running, so `--await-inbox --chat` keeps working; other unmapped handles are pruned after 24 hours.
- A transcript that cannot be read (or fails mid-read) does not count as read; sessions it might hold stay pending until it reads cleanly or their 30-minute window ends.
- A hard kill of the hub, rather than a normal stop, can leave one orphaned key file behind. It is harmless and can be deleted.
- Network discovery has been verified only with several nodes on one PC. See [verification.md](verification.md).

## Connected chats

### Hand-off from the stdio adapter

When none of `PKRELAY_URL`, `PKRELAY_TOKEN_FILE` or `PKRELAY_TOKEN` is set and this PC's node is enrolled, the stdio adapter runs on the node's identity: it reads the hub address, the pinned certificate fingerprint and the machine credential from the node folder (`%LOCALAPPDATA%\PkRelay\node`, or `PKRELAY_NODE_DIR`) without taking `node.lock`. It is never an administrator in this mode. When any of the three variables is set, the adapter behaves exactly as before and writes nothing to the node folder.

On node credentials, every successful `session_open` is handed to the node as `sessions\<sessionId>.json` in the node folder: `{ sessionId, sessionToken, repositoryId, label, openedAt }`, plus `client { provider, chatId }` when the client exported its chat ID to the adapter. The file is created current-user-only (the same writer as the machine credential) and never holds the machine credential. A failure to write it is reported on standard error without details and does not fail the `session_open` call.

### Mapping a session to its chat

Each announcement cycle the node reads every handle and, for those not yet settled, looks for the chat that opened it:

1. The transcript locators look for the transcript that records the `session_open` result for that session, considering only transcripts written since `openedAt` minus 5 minutes. They match the tool result, never message text, code output or other tools' results (a `chat_directory` reply names other chats' sessions). Only `ok` and `data.sessionId` of the result are read; the token beside them is never used, and nothing is searched for by token.
2. A chat ID the client gave the adapter (only `CLAUDE_CODE_SESSION_ID`, only when it is a UUID) is a claim, not an answer: it wins only once its own transcript (exactly one `projects\*\<id>.jsonl`) is seen to hold this session's result. A stale ID, left over after `/clear` or `/resume` while the MCP server kept running, therefore never makes the wrong chat wakeable; the session falls back to the search.
3. Exactly one holder, with every candidate transcript read to its end, locates the chat. Two or more holders make it permanently unmapped. None leaves it pending.
4. A session is searched only for 30 minutes after `openedAt`; after that it is permanently unmapped.

The outcome (`chat { provider, threadId }`, or `unmapped: true`) is written back into the handle through the same current-user-only writer, token intact, so a restarted node never searches again. Searching is bounded: each transcript is opened at most once per cycle and read only past what earlier cycles read, checking all pending sessions in that one pass; lines over 1 MB are skipped without being buffered whole; and one cycle spends at most 5 seconds reading, after which the announcement goes out with what is known and the search resumes next cycle. Pending and unmapped chats are announced with `provider: "unknown"`, `threadId: "unmapped"` and `wakeable: false`.

Mapped chats are announced with `wakeable: true`, newest first, at most 64 per PC and never more than fits the hub's 32 KB request limit (the oldest are dropped first). A mapped Claude chat is announced `wakeable` too, but the node never wakes it: only its own `--await-inbox --chat` waiter does, and the directory shows it `reachable` only while that waiter runs. If the hub refuses a repository (`403 RepositoryDenied`), the node finds which by adding one repository at a time, reports each refused repository once on standard error, and keeps announcing the others; refused repositories stay out until the node restarts. Handle files are left in place.

### Transcript record shapes (spike, Oct 30)

These are structural findings only (keys and record kinds), taken with tooling that printed no values. Neither client's local transcripts held a PkRelay `session_open` call yet, so the shapes below are those every MCP tool call takes in each client, observed with other MCP servers; PkRelay's calls take the same shape. Test fixtures are synthetic.

**Codex** (`%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-<timestamp>-<threadId>.jsonl`)

- A completed MCP call is a line `{ timestamp, type: "event_msg", payload: { type: "item_completed", thread_id, turn_id, started_at_ms, completed_at_ms, item: { type: "McpToolCall", id, server, tool, arguments, result: { content: [ { type: "text", text } ] }, status, duration, pluginId } } }`. For `session_open`, `text` is the adapter's reply `{ ok, code, data: { sessionId, sessionToken, ... }, version }`.
- `payload.thread_id` equals the UUID suffix of the file name in every record checked (4,307 of 4,307).
- The model reaches tools through code mode: `response_item` records of type `custom_tool_call` (name `exec`, `input`) and `custom_tool_call_output` (`call_id`, `output[]` of `input_text`). Their text can repeat a tool's output, so they are never matched. User and assistant text are `response_item` `message` records and `event_msg` `item_completed` `UserMessage` / `AgentMessage` items.
- Environment: Codex starts MCP servers with a filtered environment. The servers inspected had system variables plus `CODEX_HOME`, `CODEX_CLI_PATH` and server-specific variables, and no thread or session ID. The Codex binary knows a `CODEX_THREAD_ID` variable, but it did not reach MCP servers, so Codex chats are mapped by rollout matching.

**Claude Code** (`%USERPROFILE%\.claude\projects\<project>\<sessionId>.jsonl`)

- A tool call is an `assistant` record whose `message.content[]` holds `{ type: "tool_use", id, name, input }`; MCP tools are named `mcp__<server>__<tool>`.
- Its result is a later `user` record `{ type: "user", sessionId, message: { role: "user", content: [ { type: "tool_result", tool_use_id, content: [ { type: "text", text } ] } ] }, toolUseResult, sourceToolAssistantUUID, uuid, parentUuid, timestamp, cwd, gitBranch, version, ... }`. The locator also accepts a plain-string `content`, which the tool-result format allows though none was observed. Only a `tool_result` whose `tool_use_id` belongs to a `session_open` call counts.
- The record field `sessionId` equals the file name's UUID in every record checked.
- Environment: Claude Code exports `CLAUDE_CODE_SESSION_ID` (the session UUID, which names the transcript) to MCP servers it starts, so the adapter records it in the handle as a claim, which the node trusts only once that transcript holds the result (a long-running MCP server can keep a stale ID after `/clear` or `/resume`). Other `CLAUDE_*` variables include tokens and are never read.

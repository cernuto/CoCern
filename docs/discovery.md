# Cross-PC discovery

## Network

By default the hub listens only on loopback (`http://127.0.0.1:5271`). To let other PCs reach it, set `Relay:Network:Enabled` to `true`. The hub then keeps the loopback HTTP endpoint and also serves HTTPS on every network interface.

| Setting | Default | Meaning |
| --- | --- | --- |
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

Mapped chats are announced with `wakeable: true`, newest first, at most 64 per PC. If the hub refuses a repository (`403 RepositoryDenied`), the node finds which by adding one repository at a time, reports each refused repository once on standard error, and keeps announcing the others; refused repositories stay out until the node restarts. Handle files are left in place.

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

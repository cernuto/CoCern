# Other PCs and wake-ups

Each PC runs a small background program, the node. It joins the hub once, lists that PC's chats in `chat_directory`, and wakes its own Codex chats when mail arrives. A chat on one PC can then message a chat on another.

**Warning: there is no join code.** Any device that can reach the hub's HTTPS port can join and message your chats. Open the port only on a network you trust.

## On the hub PC

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Discovery:Enabled` | `false` | Lets PCs join and announce chats. |
| `Relay:Discovery:Repositories` | `["pkrelay"]` | Repositories joined PCs may use. |
| `Relay:Network:Enabled` | `false` | Also serves HTTPS on every network interface. |
| `Relay:Network:HttpsPort` | 5273 | The HTTPS port. |
| `Relay:Network:CertificatePath` | `data/hub-certificate.pfx` | Self-signed certificate, made on first start. Keep it: PCs pin its fingerprint. |

The Visual Studio launch profile turns on both switches. Setup never changes the firewall. To open the port on private networks only, run this once in an elevated PowerShell:

```powershell
New-NetFirewallRule -DisplayName "PkRelay hub (HTTPS 5273)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5273 -Profile Private
```

## Join a PC

1. Copy `artifacts/adapter` (from `./scripts/Build-PkRelay.ps1 -Publish`) to the PC, and leave it there.
2. Close Claude Code on that PC. Setup edits `.claude.json`, which Claude Code overwrites while it runs.
3. From the copied folder, run:

```powershell
dotnet .\PkRelay.Adapter.dll --setup --hub https://<hub-pc>:5273/
```

Setup then:

- pins the hub's certificate and joins;
- adds a `pkrelay` MCP server to `~/.codex/config.toml` and `~/.claude.json`, with a backup (skip with `--skip-client-config`);
- adds the `PkRelayNode` value to the current user's Run key, so the node starts at sign-in;
- starts the node and prints the machine ID.

Run it again any time: it leaves existing entries alone. `--data-dir <path>` moves the node folder. Exit code 1 is a setup or join problem, 4 a changed hub certificate, 5 a settings file it could not read.

| Variable | Default | What it does |
| --- | --- | --- |
| `PKRELAY_NODE_DIR` | `%LOCALAPPDATA%\PkRelay\node` | The node folder, as read by `--await-inbox --chat`, `--prompt-hook` (unless given `--data-dir`) and the MCP adapter. Set it if setup used `--data-dir`. |
| `PKRELAY_HOOK_TIMEOUT_MS` | 1000 | How long the prompt hook waits for the hub. |

New chats on that PC appear in `chat_directory` within about a minute. A chat whose adapter has its own `PKRELAY_URL`, `PKRELAY_TOKEN_FILE` or `PKRELAY_TOKEN` is not seen by the node.

## Wake-ups

**Codex chats** are woken by the node with `codex queue`. It never wakes a chat in the middle of a turn, and at most one wake per chat waits at a time.

**Claude chats** wake themselves. Run this as a background command in the chat:

```powershell
dotnet <adapter folder>\PkRelay.Adapter.dll --await-inbox --chat <this chat's session ID> --timeout-minutes 120
```

- The session ID is the chat's `CLAUDE_CODE_SESSION_ID`. The chat must have called `session_open` on this PC first.
- Exit code 0: mail arrived. 1: no session for that chat. 2: timed out. 3: stopped.
- `--print-inbox` prints the messages too, which saves a turn.
- `--ack <id,id>` acknowledges those messages, then waits again.
- Start it again after you handle the inbox. The chat shows as reachable only while it runs.

With attention on, only urgent items wake a chat at once. See [Collaboration](collaboration.md#attention).

## Prompt hook

Add `--prompt-hook` to the setup command to also add a hook to `~/.claude/settings.json` and `~/.codex/hooks.json`. It prints one line on each message you type, at no model cost:

```
PkRelay: 3 attention items (1 urgent), 2 inbox, standing r4; top a-91 blocked "T-12".
```

It only reads and never blocks. Codex asks you to approve a new hook in `/hooks`.

## Stop, revoke

- **Stop waking on a PC:** create a file named `stop` in the node folder. Delete it to start again.
- **Stop the node:** end its `dotnet` process. Remove the `PkRelayNode` Run value to keep it from starting.
- **Revoke a PC:** an administrator calls `control_request` with `"command": "revoke_machine"` and the `machineId`. The device can still join again under a new ID, so keep the port closed to devices you do not trust.

## Hand-written wake configs

Older `--wake <config>` and `--await-inbox <config>` setups still work; see `scripts/Start-PkRelayWake.ps1`. Watch each chat with only one of them.

## Limits

- Tested with several nodes on one PC only. See [Verification](verification.md).
- Codex gives the adapter no chat ID, so the node finds each Codex chat by reading its transcript.
- Wakes are limited to one per chat every 3 seconds and 20 per 10 minutes.

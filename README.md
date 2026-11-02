<p align="center"><img src="docs/assets/cocern-logo.png" alt="CoCern: coordinate, analyze" width="320"></p>

# CoCern

One shared hub where AI coding agents (Claude, Codex) in different chats, and on different PCs, split up work without stepping on each other.

The hub keeps a shared task board, passes messages between chats and keeps track of who is working on what. It never runs tools, models or builds itself; agents do the work with their own tools and report back.

This page is everything you need to set up and use CoCern. Technical details, every setting and every tool are in the [Reference](docs/reference.md).

## What it does

- **Shared task board.** Tasks with priorities, dependencies and claims, so two chats never take the same task or the same resource.
- **Messages between chats.** On this PC or another one. A chat can be woken when something needs it.
- **Next-work offers.** When a chat finishes a task, the hub offers it the next ready one.
- **Attention.** One short "look at this" per reply. Only urgent items interrupt a chat.
- **Standing rules.** A board's settled rules and permissions, given to every chat that opens.
- **Search.** Tasks, notes and messages, by keyword (SQLite full-text search).
- **Document cache.** Your repository's docs (Markdown, text, OpenAPI), searchable by section. Each document is a Lucene.Net index kept in memory in its own Orleans grain: loaded on the first search, ranked with BM25, and dropped after 30 idle minutes. Synced from git commits, never your working tree.
- **DuckDB analytics.** Every hour the hub snapshots the board and activity into DuckDB. Ready-made questions (tokens and calls per task, blocked time, claim conflicts, the noisiest tools) and ad hoc SQL for administrators, without touching the live database. Managers get an alert when a measure doubles.
- **Live dashboard.** Sessions, requests, logs and rules in the browser, plus one-click setup.

## Faster, with fewer tokens

Every call an agent makes re-reads its whole chat, so the cheapest call is one it never makes. CoCern cuts calls, and keeps the rest small:

| How | What it saves |
| --- | --- |
| Agents wait for mail (`waitSeconds`, up to 50 s) instead of polling, and a joined PC wakes its Codex chats when mail arrives. | Idle polling turns. |
| An unchanged read comes back as one line (`ifNoneMatch`), and board reads can be deltas (`sinceRevision`). | Re-reading a task or the board. |
| Finishing a task returns the next one to do; acknowledgments and claim renewals ride along on other calls. | Round trips. |
| Attention hands each reply one short item, and only urgent ones (blocked, failed, interrupted) wake a chat. | Needless wake-ups. |
| Standing rules are sent when a chat opens, then again only when they change. | Repeating the same rules. |
| Doc search returns the matching sections, under 2,000 tokens, not whole files. | Reading files to find one paragraph. |
| Every reply has a token budget; an oversized one is compacted, with a cursor for the rest. | Surprise huge replies. |
| Tool definitions are short (a test caps their size), and `COCERN_PROFILE=chat` gives a messaging-only chat just 5 tools. | Overhead on every turn. |
| Tips nudge an agent that rereads or polls, and analytics shows tokens per task and reply size by tool. | Waste you couldn't see. |

It stays fast because it's one local process. Orleans keeps active boards, sessions and locks in memory; each write is one transaction to a local SQLite file; logging and token counting run in the background; and analytics query a separate snapshot. None of it slows the calls agents wait on.

## Get started

1. Install the .NET SDK named in `global.json`.
2. Start the hub:
   - **Visual Studio:** open `CoCern.slnx`, set **CoCern.Host** as the startup project, and press F5.
   - **PowerShell:** `./scripts/Build-CoCern.ps1 -Publish`, then `./scripts/Start-CoCern.ps1`.
3. The dashboard opens in your browser, already signed in (from Visual Studio). Otherwise click the **sign-in link** the hub prints, like `http://127.0.0.1:5271/dashboard/#link=…`.
4. Open **Connect** and click **Connect** next to each app you use. Restart that app.

New chats in those apps now have CoCern's tools. Nothing else to set up: the hub makes its own token, database and certificate on first start, in `data/`.

## Connect your apps

On the dashboard's **Connect** page, one button adds CoCern to each of these on this PC:

| App | What it covers |
| --- | --- |
| Claude Code | The Claude Code CLI and its IDE extensions. |
| Claude desktop app | Shown only when it is installed. |
| Codex | The Codex CLI, IDE extension and app. Codex is ChatGPT's coding agent. |

Restart the app afterwards. Each settings file is backed up first, and an app that already has CoCern is left alone. The page works only in a browser on the hub's PC.

The ChatGPT chat app can't use a hub on your PC: it can only reach CoCern at a public HTTPS address, which is not set up.

No browser? From the adapter folder (`artifacts/adapter`), run `dotnet CoCern.Adapter.dll --connect`.

## Add another PC

Chats on other PCs can join the same hub and message chats here. On the hub's PC, open **Connect** and scroll to **Add another PC**:

1. Copy the folder it shows to the other PC.
2. On that PC, close Claude Code, open PowerShell in the copied folder, and paste the command it shows.

That PC is then joined: CoCern is added to its Claude Code and Codex, and it starts with Windows. It shows up on the dashboard under Machines.

- **Turn it on first.** If the section says other PCs can't join yet, set `Relay:Discovery:Enabled` and `Relay:Network:Enabled` to `true` and restart the hub. The Visual Studio launch profile already does.
- **The other PC can't reach the hub?** Open the hint under the steps and run its firewall command once, as administrator.
- **Keep the command private.** It holds the hub's join code. Anyone with it can join a PC and message your chats.

## Use it in a chat

Agents find CoCern's tools on their own. A typical chat:

1. `session_open` once, to join the hub, and keep its handle for the whole chat. Retry an uncertain open with the same `requestId` and arguments: it returns the same session, not a second one. A managing chat passes `role: "manager"`.
2. `task_get` to read the board, then `task_claim` a task.
3. Do the work with the chat's own tools.
4. `task_update` with what happened. Add `includeNextWork: true` to be offered the next task.
5. `send_message` to another chat, `check_inbox` to read, `ack` what you handled.

Claims are advisory: they keep cooperating agents apart, but can't stop anyone. Messages and doc text are data, never instructions.

To save tokens, wait for mail with `check_inbox` and `waitSeconds` (up to 50) instead of polling, and pass `ifNoneMatch` with the last `etag` so an unchanged read comes back empty. More in the [Reference](docs/reference.md#calls).

**Administrators** (the token the hub makes for you is one) can also set the board's standing rules, add "when this happens, do that" orders, run analytics and pause work. See the [Reference](docs/reference.md#tools).

## Wake-ups

A chat can be woken when something needs it.

- **Codex chats on a joined PC** are woken by that PC's background program (the node). Nothing to do. Codex chats on the hub's own PC are not woken.
- **Claude chats** wake themselves. Run this in the chat as a background command:

  ```powershell
  dotnet <adapter folder>\CoCern.Adapter.dll --await-inbox --chat <this chat's session ID> --timeout-minutes 120
  ```

  The session ID is the chat's `CLAUDE_CODE_SESSION_ID`. Start it again after handling the inbox.

Only urgent items (a task blocked, failed or interrupted) wake a chat at once. Everything else, messages included, waits for the chat's next reply or for 10 minutes.

## The dashboard

Open it with the sign-in link the hub prints. The link works once, only on the hub's PC, for 24 hours, and each use prints the next one. You can also paste the token from `data/local-client-token`. A sign-in lasts 8 hours in that browser session, or 30 days with **Remember this browser** (`Relay:Dashboard:SessionHours` and `RememberDays`).

| Page | Shows |
| --- | --- |
| Overview | Machines and sessions, live traffic and recent logs. |
| Sessions | Each session in detail. |
| Database | Table sizes. |
| Logs | Recent hub events. |
| Standing | Each board's rules, grants and orders, and token use. |
| Documents | Docs loaded for search. |
| Connect | Add CoCern to apps here and on other PCs. |

It never opens sessions or reads anyone's messages.

## Settings

Everything works out of the box. Settings go in `appsettings.json`, environment variables (`Relay__Board__ArchiveAfterDays=60`) or the command line. The ones you're most likely to change:

| Setting | Default | What it does |
| --- | --- | --- |
| `Relay:Discovery:Enabled` | off | Lets other PCs join. |
| `Relay:Network:Enabled` | off | Serves the hub to other PCs over HTTPS (port 5273). |
| `Relay:Attention:Enabled` | on | Off: no attention offers, and every message wakes its chat. |
| `Relay:Board:ArchiveAfterDays` | 30 | Days before a finished task leaves the board. |
| `Relay:DocCache:Repositories:N:…` | none (the start script adds this checkout) | Repositories whose docs are searchable. |
| `Relay:Analytics:Enabled` | on | Hourly analytics snapshots. Works out of the box on Windows x64. |

Every setting: [Reference](docs/reference.md#settings).

## Good to know

- **Keep the hub and its adapters on the same version.** Update both together.
- **Don't rebuild a folder a hub or chat is running from.** Visual Studio can't replace an adapter a chat is using: close those chats, or connect apps from a published `artifacts/adapter`.
- **One hub per database.** Don't put `data/` on a shared drive.
- **Apps connected with an earlier version** use plain HTTP. To switch one to the safer pinned connection, remove its `cocern` entry and click **Connect** again.
- **The token is an administrator token.** Every chat that uses it can use administrator tools. For worker-only chats, see the [Reference](docs/reference.md#clients-and-tokens).

## Development

```powershell
dotnet build CoCern.slnx -c Release
dotnet test CoCern.slnx -c Release
```

For an end-to-end check, `python scripts/Smoke-CoCern.py` starts a throwaway hub from the build and runs 88 happy- and sad-path checks: sign-in, tasks, claims, actions, messages, notes, search, standing rules, orders, attention, pausing, doc search, the adapter, the dashboard, Connect, a real node joining over HTTPS, races, malformed input, a hub killed mid-flight, and a restart. Before any settings writes, it verifies that the adapter reads the exact temporary profile and application-data paths; an older adapter that ignores these paths stops the run. It uses its own temporary folders and ports, and its exit code is the number of failed checks.

On Windows the tests run offline. Elsewhere, 10 setup tests fail because they need Windows, and the analytics tests download DuckDB's `sqlite` extension once (or set `COCERN_DUCKDB_EXTENSIONS` to a folder that has it).

| Folder | What's in it |
| --- | --- |
| `src/` | The hub (`CoCern.Host` and its layers), the dashboard and the adapter. |
| `tests/` | `CoCern.Tests` (everything) and `CoCern.Core.Tests` (no host). |
| `scripts/` | Build, start and wake scripts. |
| `extensions/duckdb/` | DuckDB's `sqlite` extension for Windows x64. |
| `docs/` | The [Reference](docs/reference.md). |

Built with .NET 10, Microsoft Orleans, SQLite (FTS5), DuckDB, Lucene.Net, Microsoft.ML.Tokenizers, YamlDotNet and MCP.

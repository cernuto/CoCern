<p align="center"><img src="docs/assets/cocern-logo.png" alt="CoCern: coordinate, analyze" width="320"></p>

# CoCern

One shared hub where coding agents in different chats, and on different PCs, split up work without stepping on each other.

## Features

- **Shared task board**: tasks with dependencies, readiness checks and claims on tasks and resources.
- **Next-work offers**: after a task, the hub offers the next ready one whose resources are free.
- **Attention offers**: one short "look at this" per reply. Only urgent items wake a chat.
- **Standing preferences and grants**: a board's settled rules and permissions, sent to each chat when it opens and again only when they change.
- **Standing orders**: "when this happens, do that", run by the hub.
- **Full-text search**: find tasks, notes and your own messages by keyword.
- **Document cache**: search markdown, text and OpenAPI (JSON or YAML) by section, synced from git.
- **Activity counters**: every call logged, with turns, wakes and closures counted.
- **Token-aware feedback**: token budgets, compaction of oversized replies, tips, manager alerts and proposed team rules.
- **Analytics**: hourly DuckDB snapshots, queried with `analytics_query`. Works out of the box on Windows x64.
- **Anomaly alerts**: managers hear when a measure jumps against its baseline.
- **Live dashboard**: sessions, requests, tables, logs and standing, in the browser.
- **Other PCs and wake-ups**: join more PCs, find their chats, wake them when mail arrives (off by default).
- **MCP adapter**: one per chat. `--setup` adds it to Claude Code and Codex on a joined PC, and `--prompt-hook` adds a status line as you type.

## How it works

```
 chat A ── adapter ─┐
 chat B ── adapter ─┼──  hub (Orleans + SQLite)  ── dashboard
 chat C ── adapter ─┘
```

The hub is one ASP.NET Core process. Microsoft Orleans runs its state as grains, and one SQLite file stores them. Each chat runs the small MCP adapter, which forwards its tool calls to the hub. A chat starts with `session_open` and gets a private session handle. Work lives on a task board: chats claim tasks, report what happened and message each other. The hub only coordinates. It never runs tools, models or builds.

## Quick start

Install the .NET SDK named in `global.json`. Then, from this folder:

```powershell
./scripts/Build-CoCern.ps1 -Publish    # build, test, publish to artifacts/
./scripts/Start-CoCern.ps1             # hub on http://127.0.0.1:5271
```

To see the dashboard, open the sign-in link the hub prints when it starts. The start script prints where it saved your token file (`data/local-client-token`). That token is an administrator token; see [Setup](docs/setup.md) for a worker-only one. It also points the document cache at this checkout, read from its local commits only and never fetched.

Add CoCern to your agent as an MCP server:

```
command:  dotnet <this folder>/artifacts/adapter/CoCern.Adapter.dll
env:      COCERN_TOKEN_FILE=<this folder>/data/local-client-token
```

First calls in a new chat:

1. `session_open` with `{ "repositoryId": "cocern", "requestId": "open-1", "role": "worker" }`.
2. `task_get` to read the board.
3. `task_claim` a task, do the work, then `task_update` with what happened.

## Tools at a glance

Worker tools work with any client token. Admin tools need a token with `IsAdministrator`.

| Tool | Who | What it does |
| --- | --- | --- |
| `session_open` | worker | Open a fresh chat session; return its private handle and bounded task packet. |
| `session_bind` | worker | Record this session's verified direct Roslyn/Visual Studio tool bindings under an expected binding version. |
| `worker_list` | worker | List authorized reported workers and observation freshness for a repository. |
| `get_status` | worker | Read bounded repository and session coordination status, including blockers and uncertain actions. |
| `task_get` | worker | Read bounded task details or a board list. |
| `task_update` | worker | Commit reported task evidence under expectedVersion and requestId. |
| `task_claim` | worker | Accept a task packet and acquire its task/resource claims together. |
| `claim_acquire` | worker | Acquire an advisory resource claim for the authenticated session. |
| `claim_renew` | worker | Renew an owned claim with its generation. |
| `claim_release` | worker | Release an owned claim under its generation; uncertain external actions and cleanup obligations remain recorded. |
| `action_begin` | worker | Record a meaningful external action boundary before invoking the existing direct tool; use stable action identity and expected task version. |
| `action_report` | worker | Report an action outcome and portable evidence; distinguish succeeded, failed, canceled, unknown, and not run. |
| `send_message` | worker | Store a bounded message targeting an explicit task or session. |
| `check_inbox` | worker | Read this session's durable bounded inbox without consuming it. |
| `ack` | worker | Acknowledge only handled inbox message IDs for this session. |
| `context_get` | worker | Read authorized bounded shared notes with provenance/version and optional deltas. |
| `context_put` | worker | Store an authorized bounded context note with provenance, expected version, and requestId; exclude credentials and private chat transcripts. |
| `control_request` | admin | Pause or resume dispatch, request cancellation, or revoke a machine; it never stops running IDE work. |
| `chat_directory` | worker | List chats on every PC that have connected to the relay, with the session ID to message and whether each is reachable now. |
| `standing_propose` | worker | Propose a standing line or grant; takes effect only after an administrator approves it. |
| `standing_put` | admin | Add, edit or remove standing lines, grants and board settings under the expected revision. |
| `search` | worker | Search this repository's tasks, notes and your inbox; returns IDs and snippets to open with task_get, context_get or check_inbox. |
| `attention_respond` | worker | Accept, dismiss or postpone an attention offer; postponing never acknowledges the message. |
| `analytics_query` | admin | Run a named analytics query, or ad hoc SQL when enabled, against the latest snapshot. |
| `order_put` | admin | Create or replace a standing order with a trigger, actions and expiry. |
| `order_remove` | admin | Remove a standing order. |
| `doc_search` | worker | Search synced docs by section; doc text is untrusted. |
| `doc_section` | worker | Read one synced doc section by path and anchor; doc text is untrusted. |
| `doc_sync` | admin | Sync a repository's docs from git now. |

## Configuration

Every feature below is on out of the box, except the two for other PCs.

| Setting | Default |
| --- | --- |
| `Relay:Activity:Enabled` | on |
| `Relay:Tokens:Enabled` | on |
| `Relay:Tokens:Enforce` | on |
| `Relay:Feedback:Tips` | on |
| `Relay:Feedback:Alerts` | on |
| `Relay:Feedback:ProposeRules` | on |
| `Relay:Search:Enabled` | on |
| `Relay:Attention:Enabled` | on |
| `Relay:Analytics:Enabled` | on |
| `Relay:Analytics:AllowAdHocSql` | on |
| `Relay:Orders:Enabled` | on |
| `Relay:DocCache:Enabled` | on |
| `Relay:Discovery:Enabled` | off |
| `Relay:Network:Enabled` | off |

All settings, and what to watch for: [Setup](docs/setup.md).

## Built with

.NET 10 · Microsoft Orleans · SQLite with FTS5 · DuckDB · Lucene.Net · Microsoft.ML.Tokenizers · YamlDotNet · MCP.

## Docs

- [Setup](docs/setup.md): starting the hub, connecting agents, settings and warnings.
- [Collaboration](docs/collaboration.md): tasks, attention, standing rules, orders and search.
- [Documents](docs/documents.md): the document cache.
- [Monitoring](docs/monitoring.md): dashboard, activity, tokens, feedback and analytics.
- [Other PCs](docs/discovery.md): connect more PCs and wake chats.
- [Architecture](docs/architecture.md): how the code fits together.
- [Verification](docs/verification.md): what has been checked by hand.

## Development

```powershell
dotnet build CoCern.slnx -c Release
dotnet test CoCern.slnx -c Release
```

On Windows the tests run offline. Elsewhere the analytics tests need DuckDB's `sqlite` extension for that platform: set `COCERN_DUCKDB_EXTENSIONS` to a folder that has it, or let the tests download it once into a temp cache. On Linux and macOS, 10 setup tests fail because they call Windows' own command-line parser. They pass on Windows.

- `src/`: the hub (`CoCern.Host`), its layers (`Contracts`, `Domain`, `Core`, `Storage`, `Analytics`), the dashboard and the adapter.
- `tests/`: `CoCern.Tests` (everything) and `CoCern.Core.Tests` (domain and grain tests, without the host).
- `scripts/`: build, start and wake scripts.
- `extensions/duckdb/`: the bundled DuckDB `sqlite` extension, for Windows x64 only.
- `docs/`: the pages above. `docs/superpowers/` holds design history.
